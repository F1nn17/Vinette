# Vinette

[![CI](https://github.com/F1nn17/vinette/actions/workflows/ci.yml/badge.svg)](https://github.com/F1nn17/vinette/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

Vinette — self-hosted веб-клиент почты (в одном ряду с Roundcube и SnappyMail) для собственного почтового сервера на связке Postfix + Dovecot + Rspamd.

Браузер не умеет работать с IMAP и SMTP, поэтому бэкенд Vinette — шлюз: он держит IMAP-соединения пользователей, отправляет почту через SMTP submission, разбирает MIME и отдаёт фронтенду REST API и real-time события. Поставляется одним артефактом: SPA собирается и раздаётся самим бэкендом (один процесс, один origin).

- **White-label:** название, логотип и акцентный цвет задаются в конфигурации; по умолчанию — бренд Vinette.
- **Просто для пользователей:** в интерфейсе нет технического жаргона.
- **Безопасно:** письма — недоверенный контент. HTML санитайзится на сервере и показывается только в изолированном iframe.

> Статус: каркас проекта. Почтовой функциональности пока нет — см. [ROADMAP.md](ROADMAP.md).

## Быстрый старт разработчика

Нужны: GNU Make, .NET SDK (версия в [backend/global.json](backend/global.json)), Node.js LTS с corepack, Docker (для тестового почтового сервера и образа).

```bash
make setup      # dotnet restore + pnpm install
make dev        # бэкенд (http://localhost:5080) и фронтенд (http://localhost:5173) параллельно
make check      # lint + test + build — ровно то, что гоняет CI
```

Тестовый почтовый сервер с готовыми ящиками (логин `alice` / пароль `alice-pass`, логин `bob` / пароль `bob-pass`; адреса `alice@vinette.test` и `bob@vinette.test`):

```bash
docker compose -f deploy/docker/compose.dev.yaml up -d
```

Все цели Makefile: `make help`. На Windows GNU Make удобнее запускать из Git Bash или WSL.

## Запуск

Docker (образ из GitHub Container Registry), пример — [deploy/docker/compose.yaml](deploy/docker/compose.yaml):

```bash
docker compose -f deploy/docker/compose.yaml up -d
```

Голое железо — юнит [deploy/systemd/vinette.service](deploy/systemd/vinette.service), конфигурация — по образцу [deploy/config/appsettings.example.json](deploy/config/appsettings.example.json).

Конфигурация читается по цепочке: `appsettings.json` → переменные окружения с префиксом `VINETTE_` (вложенность через `__`, например `VINETTE_Imap__Host`) → необязательный файл по пути из `VINETTE_CONFIG`.

## Документация

- [docs/architecture.md](docs/architecture.md) — архитектура
- [docs/adr/](docs/adr/) — архитектурные решения (стек, лицензия)
- [docs/design/](docs/design/README.md) — дизайн-спецификация (появится позже)
- [ROADMAP.md](ROADMAP.md), [CHANGELOG.md](CHANGELOG.md)
- [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md), [AGENTS.md](AGENTS.md)

## Лицензия

Apache License 2.0 — см. [LICENSE](LICENSE) и [NOTICE](NOTICE).
