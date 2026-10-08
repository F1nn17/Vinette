# 2. Технологический стек

Статус: принято. Изменение стека — только по согласованию с владельцем проекта.

## Контекст

Vinette — шлюз между браузером и IMAP/SMTP с real-time событиями, поставляемый одним артефактом, который можно запустить в Docker или под systemd.

## Решение

**Backend: C# / ASP.NET Core на .NET 10 (LTS), minimal API.**
- MailKit + MimeKit — зрелая, активно поддерживаемая реализация IMAP/SMTP/MIME (MIT) с поддержкой IDLE, CONDSTORE и т. д.
- SignalR — готовый транспорт для real-time (WebSocket с запасными вариантами) и интеграция с ASP.NET Core.
- ASP.NET Core Data Protection — шифрование учётных данных сессии без собственной криптографии.
- Microsoft.AspNetCore.OpenApi — встроенная генерация OpenAPI; из документа генерируются TypeScript-типы.
- HtmlSanitizer — санитайз HTML писем на сервере.
- xUnit + Testcontainers — модульные и интеграционные тесты против реального почтового сервера в контейнере.
- Центральное управление версиями NuGet (`Directory.Packages.props`), SDK закреплён в `global.json`.

**Frontend: React + TypeScript + Vite.**
- pnpm через corepack (поле `packageManager`) — воспроизводимые установки и быстрый store.
- React Router, TanStack Query — маршрутизация и серверное состояние.
- Tailwind CSS v4 поверх CSS-переменных дизайн-токенов — акцентный цвет и тема подставляются в рантайме (white-label).
- lucide-react — иконки; react-i18next — локализация (ru по умолчанию, en).
- Vitest + Testing Library, Playwright — модульные и end-to-end тесты; ESLint + Prettier.

**CI/CD: GitHub Actions**, образы публикуются в GitHub Container Registry (ghcr.io).

## Последствия

- Нужны .NET SDK и Node.js LTS; единая точка входа — `make`.
- Версии закрепляются в `global.json`, `Directory.Packages.props`, `pnpm-lock.yaml`, а actions — по мажорному тегу.
- Известное ограничение: `typescript-eslint` пока не поддерживает TypeScript 7, поэтому проект закреплён на TypeScript 6.0.x.
