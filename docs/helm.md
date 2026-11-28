# Установка в Kubernetes (Helm)

Helm-чарт [deploy/helm/vinette](../deploy/helm/vinette) ставит Vinette в режиме «только UI» (VIN-125): само приложение, а почтовый сервер (Dovecot и Postfix) у вас уже есть и работает отдельно ([mail-server.md](mail-server.md)). Чарт создаёт Deployment, Service, при желании Ingress, ConfigMap с настройками, Secret и том для ключей.

Нужны Kubernetes 1.27 и новее и Helm 3 или 4. Образ — `ghcr.io/f1nn17/vinette`, по умолчанию тег совпадает с версией чарта.

## Быстрый старт

```bash
helm install vinette deploy/helm/vinette --namespace mail --create-namespace \
  --set mail.imap.host=mail.example.com \
  --set ingress.enabled=true --set ingress.className=nginx \
  --set 'ingress.hosts[0].host=mail.example.com' --set 'ingress.hosts[0].paths[0].path=/' \
  --set 'ingress.tls[0].secretName=vinette-tls' --set 'ingress.tls[0].hosts[0]=mail.example.com' \
  --set 'security.trustedProxies[0]=10.244.0.0/16'
helm test vinette --namespace mail     # спрашивает адрес проверки у сервиса
```

Удобнее собственный файл значений:

```yaml
# my-values.yaml
mail:
  imap: { host: mail.example.com, port: 993, security: Tls }
  smtp: { port: 587, security: StartTls }       # host пуст: тот же, что у IMAP
branding:
  name: Acme Mail
  accent: "#1F6FEB"
security:
  trustedProxies: ["10.244.0.0/16"]             # где работает ваш ingress-контроллер
ingress:
  enabled: true
  className: nginx
  hosts:
    - host: mail.example.com
      paths: [{ path: /, pathType: Prefix }]
  tls:
    - secretName: vinette-tls
      hosts: [mail.example.com]
persistence:
  size: 1Gi
```

```bash
helm install vinette deploy/helm/vinette -n mail --create-namespace -f my-values.yaml
```

Кроме `mail.imap.host` обязательного нет. Значения проверяет схема ([values.schema.json](../deploy/helm/vinette/values.schema.json)): пустой адрес, неверный режим защиты соединения или порт вне 1–65535 отклоняются при установке, а не при первом входе.

## Что обязательно сделать

- **HTTPS перед приложением.** Cookie сессии `Secure`: без HTTPS (кроме `localhost`) войти нельзя. Обычно TLS заканчивается на ingress ([reverse-proxy.md](reverse-proxy.md), там же про WebSocket для обновлений в реальном времени: для ingress-nginx дополнительных настроек не нужно).
- **`security.trustedProxies`.** Адреса, откуда ingress-контроллер ходит к поду, в виде CIDR (диапазон подов контроллера). Без этого приложение видит адрес контроллера вместо адреса человека: ограничение входов считает всех одним, а в журнале нет настоящих адресов. Чарт напоминает об этом после установки.
- **Постоянный том.** Ключи Data Protection лежат на томе `/data`: без них все выходят из системы при каждом перезапуске и пропадают отложенные письма. Том включён по умолчанию (`persistence.enabled: true`, 1 ГиБ). `helm uninstall` **не удаляет** созданный чартом том: ключи нужны, чтобы вернуться. Удалите его сами, когда уверены (`kubectl delete pvc`).

## Настройки

| Ключ значений | Что делает | По умолчанию |
| --- | --- | --- |
| `image.repository`, `image.tag`, `image.digest`, `image.pullPolicy` | Образ. Пустой тег — версия чарта. `digest` (`sha256:…`) главнее тега ([image-security.md](image-security.md)) | `ghcr.io/f1nn17/vinette` |
| `mail.imap.*`, `mail.smtp.*` | Почтовый сервер: `host`, `port`, `security` (`Tls`, `StartTls`, `None`), `allowInvalidCertificate` (только для проверок) | IMAP 993 `Tls`, SMTP 587 `StartTls` |
| `branding.*` | Название, акцент, подпись, «Powered by» ([white-label.md](white-label.md)); логотип и значок на этом чарте пока не подключаются (нужен файл в поде) | |
| `security.trustedProxies`, `security.trustedOrigins` | Доверенные прокси (CIDR) и адреса сайта, если прокси не передаёт `Host` | пусто |
| `config` | Любая другая настройка по имени с `__` вместо двоеточия: `Session__IdleTimeout: "04:00:00"` ([admin-guide.md](admin-guide.md)) | пусто |
| `secret.existingSecret`, `secret.values` | Секретные настройки (строка соединения с Redis, токен метрик): свой Secret, ключи которого называются как переменные (`VINETTE_Redis__ConnectionString`), или значения в чарте. Лучше свой Secret: значения из `values` хранятся в истории релиза | пусто |
| `dataProtection.certificate.existingSecret` | Secret с `keys.pfx` и `password`: ключи на томе шифруются сертификатом ([data-protection-keys.md](data-protection-keys.md)) | пусто |
| `persistence.*` | `enabled`, `existingClaim`, `storageClass`, `size`, `accessModes` | включён, 1 ГиБ |
| `service.*`, `ingress.*` | Service (80 → 8080) и Ingress | ClusterIP, Ingress выключен |
| `resources`, `nodeSelector`, `tolerations`, `affinity` и т. п. | Обычные настройки пода | запрос 50m, 128 МиБ; предел 512 МиБ |
| `podSecurityContext`, `securityContext` | Не root (1654), файловая система только для чтения, без привилегий Linux, профиль seccomp `RuntimeDefault` ([image-security.md](image-security.md)) | включено |

Secret с сертификатом ключей:

```bash
kubectl -n mail create secret generic vinette-keys \
  --from-file=keys.pfx=./keys.pfx --from-file=password=./password.txt
helm upgrade vinette deploy/helm/vinette -n mail --reuse-values --set dataProtection.certificate.existingSecret=vinette-keys
```

## Устройство

- **Один экземпляр.** `replicaCount: 1`, стратегия обновления `Recreate`: данные пишет один под, сессии хранятся в его памяти. Старый под останавливается до запуска нового, поэтому при обновлении есть пауза в несколько секунд и пользователи входят заново, если не включён общий том и Redis. Несколько экземпляров нужны только при общем `ReadWriteMany`-томе и `Session__Store=Redis` ([admin-guide.md](admin-guide.md), [ADR-0007](adr/0007-multiple-instances.md)); при `replicaCount` выше 1 чарт предупреждает.
- **Корневая файловая система только для чтения.** Приложение пишет только в `/data` (ключи, настройки, вложения, отложенные письма, временные файлы). Каталоги внутри тома оно создаёт само при запуске, а если не может — не стартует и называет настройку.
- **Проверки состояния** (запуск, готовность, жизнь) — адрес `/api/v1/health`.
- **Изменение настроек перезапускает под** (контрольная сумма ConfigMap и Secret в аннотациях): приложение читает настройки при запуске.
- Токен сервисной учётной записи не монтируется: Vinette не обращается к API Kubernetes.

## Обновление

```bash
helm upgrade vinette deploy/helm/vinette -n mail --reset-then-reuse-values --set image.tag=0.1.1
```

Перед обновлением снимите копию тома и прочитайте [backup-and-update.md](backup-and-update.md) (совместимость версий, откат: `helm rollback`). Обновляйте чарт и образ вместе: версия чарта совпадает с версией продукта (это проверяет `make check-version`).

## Как проверено

- `helm lint --strict` и схема значений: пустой адрес сервера и неверный режим отклоняются.
- Установка в kind (CI, задание «Helm chart», и вручную на Kubernetes 1.37): под доходит до готовности, `helm test` проходит; сквозные тесты Playwright (вход, чтение, отправка, черновики, метки, настройки, политика безопасности) проходят против пода с настоящей почтой на Dovecot; в журнале нет ошибок; проверены шифрование ключей сертификатом из Secret и перезапуск пода (`rollout restart`).
- При проверке нашлась и исправлена ошибка: на пустом томе (каждый новый PVC) не было каталога временных файлов, и первый вход падал. Теперь приложение создаёт его при запуске.

## Чего здесь нет

- Чарт не публикуется в реестре (нет `helm repo` и OCI): ставьте из каталога репозитория соответствующего тега. Публикация чарта вместе с релизом — отдельная задача, если она понадобится.
- Нет NetworkPolicy, PodDisruptionBudget и автомасштабирования: при одном экземпляре они мало что дают; сетевые правила зависят от вашего кластера.
- Нет собственного почтового сервера и Redis: чарт ставит только Vinette.
