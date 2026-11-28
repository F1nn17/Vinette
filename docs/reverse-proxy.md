# Vinette за reverse proxy

Обычно Vinette стоит за reverse proxy: он принимает HTTPS, получает сертификат и передаёт запросы приложению по обычному HTTP внутри сети. Это руководство для nginx, Caddy и Traefik. Готовые файлы лежат в [deploy/proxy/](../deploy/proxy/); каждый проверен на настоящей связке (Vinette в контейнере, прокси перед ним, настоящий браузер, настоящая почта): вход, канал обновлений, загрузка файла в 12 МБ, заголовки.

## Что приложению нужно от прокси

Пять вещей. Все примеры ниже их делают.

1. **HTTPS.** Cookie сессии помечена `Secure`: по обычному HTTP (кроме `localhost`) браузер её не сохранит и войти нельзя. Приложение само TLS не терминирует, это делает прокси.
2. **Имя, как его ввёл пользователь: заголовок `Host` с портом, если порт нестандартный.** Приложение сравнивает его с `Origin` каждого изменяющего запроса (защита от CSRF). Если `Host` не совпал, будет `403 origin_not_allowed`.
3. **Настоящий адрес клиента и схема: `X-Forwarded-For`, `X-Forwarded-Proto`, `X-Forwarded-Host`**, и приложение должно знать, каким прокси верить (см. следующий раздел). Без этого:
   - ограничение числа неверных входов считает **всех пользователей одним адресом (адресом прокси)**: несколько ошибок у одного человека блокируют вход всем;
   - в журнале везде адрес прокси;
   - приложение считает, что работает по HTTP, и **не отправляет `Strict-Transport-Security`**.
4. **WebSocket для канала обновлений** (`/hubs/mail`): заголовки `Upgrade` и `Connection`, длинные таймауты и без буферизации. Без него письма приходят только после обновления страницы, а на экране появляется плашка «нет связи».
5. **Размер запроса не меньше, чем принимает приложение.** Файл письма загружается запросом целиком: приложение разрешает до `Attachments:MaxTotalBytes` (25 МБ по умолчанию). У nginx по умолчанию лимит **1 МБ**: без правки любое вложение больше мегабайта получает `413`.

Прокси **не нужно**: кэшировать (приложение само запрещает кэш для API), сжимать вручную, добавлять заголовки безопасности (CSP, HSTS и другие приложение ставит само), проверять сессию.

## Каким прокси верить

Приложение верит `X-Forwarded-*` **только от прокси из списка** `Security:TrustedProxies` (адреса или сети в формате CIDR). Без списка заголовки игнорируются, чтобы клиент не мог сам назвать свой адрес. Список надо задать:

```yaml
environment:
  VINETTE_Security__TrustedProxies__0: 172.20.0.0/16   # сеть, в которой стоит прокси
```

Сеть `0.0.0.0/0` и `::/0` не принимается: она позволила бы любому подделать адрес. Указывайте адрес или сеть прокси, не больше.

В Docker Compose задайте сети **постоянный адрес**, чтобы он не менялся между запусками:

```yaml
services:
  vinette:
    image: ghcr.io/f1nn17/vinette:latest
    networks: [web]
    environment:
      VINETTE_Security__TrustedProxies__0: 172.20.0.0/24
      # ... адреса почтового сервера, см. compose.yaml
    volumes:
      - vinette-data:/data
  proxy:
    image: nginx:alpine          # или caddy:2, traefik:v3
    networks: [web]
    ports: ["80:80", "443:443"]
    # ... конфигурация из этого руководства
networks:
  web:
    ipam:
      config:
        - subnet: 172.20.0.0/24
volumes:
  vinette-data:
```

Порт приложения (8080) наружу **не публикуйте**: пусть он виден только прокси. На голом железе приложение слушает только `127.0.0.1:8080` (см. `ASPNETCORE_URLS` в [vinette.service](../deploy/systemd/vinette.service)), и доверенный прокси — `127.0.0.1`.

Если несколько прокси подряд (например, внешний балансировщик и nginx), укажите их все: приложение идёт по цепочке `X-Forwarded-For` справа налево, пока адреса доверенные, и первый недоверенный считает клиентом.

## nginx

Файл [deploy/proxy/nginx.conf](../deploy/proxy/nginx.conf). Положите его в `/etc/nginx/conf.d/vinette.conf`, замените `vinette.example.com`, пути к сертификату и адрес приложения (`server vinette:8080`).

```nginx
# Vinette behind nginx: TLS, the live channel (WebSocket), forwarded headers.
# Put this file in /etc/nginx/conf.d/vinette.conf (or include it in a server context).
# Replace vinette.example.com and the certificate paths; "vinette" is where the application listens (host:port).

map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

upstream vinette {
    server vinette:8080;
    keepalive 16;
}

server {
    listen 80;
    server_name vinette.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    http2 on;
    server_name vinette.example.com;

    ssl_certificate     /etc/nginx/certs/fullchain.pem;
    ssl_certificate_key /etc/nginx/certs/privkey.pem;
    ssl_protocols       TLSv1.2 TLSv1.3;

    # Attachments: the application accepts up to Attachments:MaxTotalBytes (25 MB by default) in one message.
    client_max_body_size 30m;

    location / {
        proxy_pass http://vinette;
        proxy_http_version 1.1;

        # The application needs the name the browser used, with its port if it has one (it compares it with Origin),
        # the scheme and the client address. $http_host keeps the port; $host would drop it.
        proxy_set_header Host              $http_host;
        proxy_set_header X-Forwarded-For   $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host  $http_host;

        # The live channel: WebSocket, kept open for long.
        proxy_set_header Upgrade    $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_read_timeout  1h;
        proxy_send_timeout  1h;
        proxy_buffering     off;
    }
}
```

Что здесь важно:

- `proxy_set_header Host $http_host;` — не `$host`. `$host` теряет порт; если сайт открыт не на 443 (например, `:8443`), `Origin` и `Host` перестанут совпадать и каждое действие получит `403`.
- `client_max_body_size 30m;` — иначе вложения больше 1 МБ получат `413`.
- Три строки про `Upgrade`/`Connection` и `proxy_read_timeout 1h` — канал обновлений. Приложение посылает пинг примерно раз в 15 секунд, поэтому и стандартных 60 секунд хватило бы, но час надёжнее.
- `proxy_buffering off;` — ответы не задерживаются, это нужно запасным способам доставки канала.

## Caddy

Файл [deploy/proxy/Caddyfile](../deploy/proxy/Caddyfile). Caddy сам получает и продлевает сертификат Let's Encrypt (имя должно указывать на этот сервер, порты 80 и 443 открыты).

```caddy
# Vinette behind Caddy. Caddy gets and renews the certificate by itself (the name must point to this server, ports 80 and 443 open).
# Replace vinette.example.com; "vinette:8080" is where the application listens.

vinette.example.com {
	# Attachments: up to Attachments:MaxTotalBytes (25 MB by default) in one message.
	request_body {
		max_size 30MB
	}

	# Caddy passes Host and sets X-Forwarded-For, -Proto and -Host itself; WebSocket needs nothing extra.
	reverse_proxy vinette:8080 {
		flush_interval -1
		transport http {
			read_timeout 1h
			write_timeout 1h
		}
	}
}
```

Caddy сам передаёт `Host` (с портом) и ставит `X-Forwarded-For`, `-Proto` и `-Host`, а WebSocket проходит без настройки. Остаётся лимит размера и таймауты.

## Traefik v3

Два файла: [traefik-static.yml](../deploy/proxy/traefik-static.yml) (точки входа и сертификаты; кладётся как `/etc/traefik/traefik.yml`) и [traefik-dynamic.yml](../deploy/proxy/traefik-dynamic.yml) (маршрут и сервис; `/etc/traefik/dynamic.yml`). Замените `vinette.example.com`, почту для Let's Encrypt и адрес приложения.

```yaml
# traefik.yml
# Vinette behind Traefik v3: the static configuration (entry points and where the dynamic file is).
# Replace the email; the certificate comes from Let's Encrypt (ports 80 and 443 open, the name points here).
entryPoints:
  web:
    address: ":80"
    http:
      redirections:
        entryPoint:
          to: websecure
          scheme: https
  websecure:
    address: ":443"
    transport:
      respondingTimeouts:
        readTimeout: 1h
        writeTimeout: 1h
        idleTimeout: 1h

providers:
  file:
    filename: /etc/traefik/dynamic.yml

certificatesResolvers:
  letsencrypt:
    acme:
      email: admin@example.com
      storage: /letsencrypt/acme.json
      httpChallenge:
        entryPoint: web
```

```yaml
# dynamic.yml
# Vinette behind Traefik v3: the router and the service. Replace vinette.example.com.
# Traefik passes Host and sets X-Forwarded-For, -Proto and -Host itself; WebSocket needs nothing extra, and it limits
# the size of a request only if you add a middleware (do not add "buffering": it holds the answers back).
http:
  routers:
    vinette:
      rule: Host(`vinette.example.com`)
      entryPoints: [websecure]
      service: vinette
      tls:
        certResolver: letsencrypt

  services:
    vinette:
      loadBalancer:
        servers:
          - url: http://vinette:8080
```

Traefik сам передаёт `Host` и ставит `X-Forwarded-*`, WebSocket проходит без настройки. **Не добавляйте middleware `buffering`**: он задерживает ответы. Лимита размера запроса у Traefik по умолчанию нет.

## Как проверить

```bash
# 1. приложение отвечает через прокси, и HSTS есть (то есть оно знает, что оно за HTTPS)
curl -sI https://vinette.example.com/api/v1/health | grep -i "HTTP/\|strict-transport"

# 2. вход работает, изменяющие запросы не получают 403 (откройте сайт в браузере и войдите)

# 3. канал обновлений: в инструментах разработчика браузера, вкладка Network, фильтр WS:
#    соединение /hubs/mail со статусом 101 держится открытым, плашки «нет связи» нет

# 4. адрес клиента в журнале приложения настоящий, а не адрес прокси
docker logs vinette 2>&1 | grep "Login succeeded" | tail -1
```

## Если что-то не так

| Симптом | Причина | Что сделать |
| --- | --- | --- |
| Вход не удаётся: после ввода пароля снова экран входа | сайт открыт по HTTP, cookie `Secure` браузер не сохраняет | открывать только по HTTPS |
| Любое действие после входа даёт «запрос не из этого приложения» (`403 origin_not_allowed`) | прокси передаёт приложению другое имя или без порта | в nginx `Host $http_host`; в других проверьте, что `Host` не переписывается. Если имя внутри намеренно другое, добавьте адрес сайта в `Security:TrustedOrigins` |
| Вложения больше 1 МБ не прикрепляются (`413`) | лимит размера запроса прокси | `client_max_body_size 30m` (nginx) |
| Письма приходят только после обновления страницы, плашка «нет связи» | не проходит WebSocket (нет `Upgrade`, короткий таймаут, балансировщик перед прокси режет соединения) | заголовки `Upgrade`/`Connection`, длинные таймауты; в журнале прокси искать `400`/`502` на `/hubs/mail` |
| Несколько неверных паролей у одного человека блокируют вход всем | приложение не знает, каким прокси верить, и видит один адрес | задать `Security:TrustedProxies` (раздел выше) |
| В ответах нет `Strict-Transport-Security` | то же: приложение считает, что за HTTP | `Security:TrustedProxies` и передача `X-Forwarded-Proto` |

## Что проверено

На Docker (Vinette `vinette:dev`, nginx 1.31, Caddy 2, Traefik 3.1, настоящий Dovecot и Postfix, браузер Chromium), через самоподписанный сертификат и нестандартный порт 8443. У всех трёх: страница входа и вход, канал `/hubs/mail` открыт и держится, загрузка файла в 12 МБ даёт `201`, изменение настроек из страницы даёт `200`, есть `Strict-Transport-Security`, в журнале адрес настоящего клиента. Без `Security:TrustedProxies` (проверено на nginx): всё работает, но адрес клиента в журнале — адрес прокси, а HSTS нет.
