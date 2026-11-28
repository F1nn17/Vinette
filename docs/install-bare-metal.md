# Установка на голое железо (systemd)

Для тех, кто не использует Docker: Vinette ставится как обычная служба Linux. Архив с приложением и фронтендом лежит в каждом выпуске на GitHub (`vinette-X.Y.Z.tar.gz`). Эта инструкция проверена на чистой Ubuntu 22.04 с настоящим systemd и настоящей почтой (см. «Что проверено»).

## Что нужно

- Linux x64 или arm64 с **systemd**.
- **ASP.NET Core Runtime 11** (`aspnetcore-runtime-11.0`; пакеты Microsoft или скрипт `dotnet-install`), команда `dotnet` в `/usr/bin` (в unit-файле путь `/usr/bin/dotnet`). SDK не нужен. Проверка: `dotnet --list-runtimes` показывает `Microsoft.AspNetCore.App 11.…`.
- Почтовый сервер ([mail-server.md](mail-server.md)) и reverse proxy с HTTPS ([reverse-proxy.md](reverse-proxy.md)): приложение слушает только `127.0.0.1:8080`.
- Библиотеки, которые .NET берёт из системы: `libicu` и `libssl` (на свежих дистрибутивах уже есть).

## Установка

Команды от root (`sudo -i`). Замените `0.1.0` на нужную версию.

```bash
# 1. Скачайте архив и контрольную сумму из выпуска и проверьте её
cd /tmp
sha256sum -c vinette-0.1.0.tar.gz.sha256        # должно быть: vinette-0.1.0.tar.gz: OK
tar xzf vinette-0.1.0.tar.gz

# 2. Пользователь, от которого работает служба (без входа в систему)
useradd --system --home-dir /var/lib/vinette --shell /usr/sbin/nologin vinette

# 3. Приложение (принадлежит root: служба не может его менять)
mkdir -p /opt/vinette
cp -r vinette-0.1.0/app/. /opt/vinette/
chown -R root:root /opt/vinette

# 4. Конфигурация (читать может служба, писать только root)
install -d -m 0755 -o root -g root /etc/vinette
cp vinette-0.1.0/appsettings.example.json /etc/vinette/appsettings.json
chown root:vinette /etc/vinette/appsettings.json && chmod 640 /etc/vinette/appsettings.json
editor /etc/vinette/appsettings.json            # см. «Настройка» ниже

# 5. Служба
cp vinette-0.1.0/vinette.service /etc/systemd/system/vinette.service
systemctl daemon-reload
systemctl enable --now vinette
```

Каталог данных `/var/lib/vinette` (владелец `vinette`, права `0700`) создаёт systemd (`StateDirectory`), подкаталоги `keys`, `settings`, `attachments` и `scheduled` создаёт само приложение при запуске. Если каталог недоступен для записи, служба не запустится, а в журнале будет сказано, какую настройку исправить.

## Настройка

Файл `/etc/vinette/appsettings.json` — самый сильный слой настроек (сильнее переменных окружения; [admin-guide.md](admin-guide.md)). **Оставьте в нём только то, что меняете.** Образец перечисляет все ключи; минимум для работы:

```json
{
  "Imap": { "Host": "mail.example.com" },
  "Smtp": { "Host": "mail.example.com" },
  "Security": { "TrustedProxies": ["127.0.0.1"] }
}
```

- `Imap` и `Smtp`: адрес почтового сервера; остальное по умолчанию (IMAPS 993 и submission 587 с шифрованием).
- `Security:TrustedProxies`: прокси, которому верят `X-Forwarded-*` (здесь он на этой же машине).
- Пути к данным (`DataProtection:KeysPath`, `Settings:Path`, `Attachments:Path`, `Scheduling:Path`) уже заданы в unit-файле переменными окружения на `/var/lib/vinette/...`: в файле их повторять не надо (если укажете, файл победит).
- Шифрование ключей сертификатом: [data-protection-keys.md](data-protection-keys.md). Название, логотип, цвет: [white-label.md](white-label.md).

После правки: `systemctl restart vinette`.

## Проверка

```bash
systemctl status vinette                 # active (running)
curl -s http://127.0.0.1:8080/api/v1/health      # {"status":"ok","version":"0.1.0"}
journalctl -u vinette -f                 # журнал (строки JSON)
```

В журнале при запуске будут **предупреждения**: ключи не зашифрованы сертификатом (пока не настроите [data-protection-keys.md](data-protection-keys.md)), и если выключена проверка сертификата почтового сервера. Это нормально для первого запуска. Затем настройте прокси ([reverse-proxy.md](reverse-proxy.md)) и войдите в браузере своим адресом и паролем ящика.

## Что делает unit-файл

[vinette.service](../deploy/systemd/vinette.service) запускает `dotnet /opt/vinette/Vinette.Api.dll` от `vinette` и закрывает службе всё лишнее: `NoPrivileges`, `ProtectSystem=strict` (файловая система только для чтения, кроме `/var/lib/vinette`), `ProtectHome`, `PrivateTmp`, `PrivateDevices`, ограничение семейств сетевых адресов. Перезапуск при сбое (`Restart=on-failure`, пауза 5 секунд): если служба упала на запуске из-за неверной настройки, она будет перезапускаться, пока вы её не исправите (`systemctl status vinette` покажет `activating`, причина в `journalctl -u vinette`).

## Обновление

1. Сделайте копию `/var/lib/vinette` ([backup-and-update.md](backup-and-update.md)) и прочитайте `CHANGELOG.md`.
2. Скачайте новый архив, проверьте сумму, остановите службу, замените содержимое `/opt/vinette` и запустите:

```bash
systemctl stop vinette
rm -rf /opt/vinette/* && cp -r vinette-0.2.0/app/. /opt/vinette/ && chown -R root:root /opt/vinette
cp vinette-0.2.0/vinette.service /etc/systemd/system/ && systemctl daemon-reload   # если unit-файл изменился
systemctl start vinette
```

Откат — те же шаги со старым архивом (и копией данных, если новая версия их изменила).

## Удаление

```bash
systemctl disable --now vinette
rm -f /etc/systemd/system/vinette.service && systemctl daemon-reload
rm -rf /opt/vinette /etc/vinette
# данные (ключи, настройки, очередь) и пользователь, если они больше не нужны:
rm -rf /var/lib/vinette && userdel vinette
```

## Если что-то не работает

| Симптом | Причина |
| --- | --- |
| `systemctl status` показывает `activating (auto-restart)` | служба падает при запуске: `journalctl -u vinette -n 30`; чаще всего неверная настройка или каталог, недоступный для записи |
| `Unable to find a compatible framework` / `You must install .NET` | не установлена среда выполнения ASP.NET Core 11 |
| `curl 127.0.0.1:8080` не отвечает | служба не запущена; или `ASPNETCORE_URLS` в unit-файле изменён |
| Вход не удаётся | не тот адрес или порт почтового сервера, или сервер недоступен с этой машины: [admin-guide.md](admin-guide.md), таблица симптомов |
| `Permission denied` в журнале на `/var/lib/vinette/…` | каталог создан не от `vinette`: `chown -R vinette:vinette /var/lib/vinette && chmod 700 /var/lib/vinette` |

## Что проверено

На чистой Ubuntu 22.04 с настоящим systemd (в контейнере, с .NET 11 Runtime): по этой инструкции из архива, собранного `make package`, установлено приложение; служба стартует (`active (running)`), `/api/v1/health` отвечает, каталоги данных принадлежат `vinette` (`/var/lib/vinette` с правами `0700`), вход настоящим ящиком Dovecot работает, настройки сохраняются, файл загружается (то есть запись в `/var/lib/vinette` проходит при включённой защите `ProtectSystem=strict`). Недоступный для записи каталог (`Settings:Path` в `/usr/local`) останавливает запуск с сообщением, какую настройку исправить; systemd при этом перезапускает службу (`activating`), как описано выше. Проверка на отдельной настоящей виртуальной машине не проводилась.
