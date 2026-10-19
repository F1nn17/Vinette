# AGENTS.md

Рабочая инструкция для агентов и людей. Короткие императивные правила; детали — по ссылкам.

## 1. О проекте

Vinette — self-hosted веб-клиент почты для Postfix + Dovecot + Rspamd. Бэкенд (ASP.NET Core, .NET 10) — шлюз: держит IMAP-соединения, отправляет почту через SMTP submission, разбирает MIME. Фронтенд — React SPA, которую раздаёт тот же процесс. Письма — недоверенный контент. White-label: бренд задаётся конфигурацией.

- [README.md](README.md) — быстрый старт
- [ROADMAP.md](ROADMAP.md) — вехи и задачи (VIN-xxx)
- [docs/architecture.md](docs/architecture.md) — архитектура
- [docs/design/DESIGN.md](docs/design/DESIGN.md) — дизайн-спецификация
- [docs/adr/](docs/adr/) — архитектурные решения (стек: [ADR-0002](docs/adr/0002-technology-stack.md)); лицензия — Apache-2.0
- [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md), [CHANGELOG.md](CHANGELOG.md)

## 2. Порядок работы над задачей

Одна задача за раз.

1. Возьми первую незакрытую задачу текущей вехи (или ту, что назвал владелец) и отметь её `[~]` в ROADMAP.md. Проверь, что зависимости закрыты.
2. **Перед созданием ветки обнови `main`:** `git fetch origin`, затем создавай ветку от свежего `origin/main` — `git switch -c feat/VIN-012-short-name origin/main` (также `fix/…`, `docs/…`, `refactor/…`). Не ветвись от другой рабочей ветки и от устаревшего локального `main`. Если ветка нужна поверх ещё не влитой — спроси владельца.
3. До кода напиши план из 3–7 пунктов. Если в плане есть пункт из раздела 3 — остановись и спроси владельца.
4. Реализуй небольшими коммитами, сразу с тестами.
5. Прогони `make check`. Не сдавай работу с красным `make check`.
6. Обнови CHANGELOG, секция `[Unreleased]` (раздел 7).
7. Отметь задачу `[x]` с датой и коротким хэшем коммита: `[x] **VIN-012 · …** (2026-10-08, 1a2b3c4)`.
8. Выдай краткий отчёт: что сделано, что проверено, что не сделано, что требует решения владельца.

Если задача больше 2 дней — разбей её на подзадачи (новые ID), обнови ROADMAP.md и продолжай с первой.
Не чини побочное в той же ветке: заведи задачу в ROADMAP.md. Не переиспользуй ID.

## 3. Когда остановиться и спросить владельца

Не продолжай, пока нет ответа, если задача требует:

- **новой зависимости** NuGet или npm — предложи её с обоснованием, лицензией и альтернативами (раздел 13, «Лицензии»);
- **несовместимого изменения API** (`/api/v1`);
- **изменения стека или архитектуры** — сначала ADR;
- **хранения учётных данных** и всего, что касается безопасности;
- **изменения дизайна** (см. раздел 11);
- **удаления функциональности**.

Задачи в ROADMAP.md с пометкой «Требует решения владельца» или «Нужно согласие владельца» относятся сюда же. Для остального выбирай разумное решение сам и записывай его в отчёте.

## 4. Сборка и запуск

### Окружение

GNU Make, .NET SDK (версия в [backend/global.json](backend/global.json)), Node.js LTS с corepack (pnpm закреплён полем `packageManager`), Docker. На Windows запускай `make` из Git Bash или WSL.

### Цели Makefile

| Цель | Что делает |
| --- | --- |
| `make setup` | `dotnet restore` + `pnpm install --frozen-lockfile` |
| `make dev` | бэкенд (`http://localhost:5080`) и фронтенд (`http://localhost:5173`) параллельно |
| `make build` | сборка бэкенда (Release) и фронтенда |
| `make test` | модульные тесты бэкенда и фронтенда |
| `make lint` | `dotnet format --verify-no-changes`, ESLint, Prettier |
| `make format` | автоформатирование |
| `make check` | lint + test + build — ровно то, что гоняет CI |
| `make api-types` | OpenAPI бэкенда → TypeScript-типы (`frontend/src/shared/api/schema.d.ts`) |
| `make dev-mail` | dev-почта на Dovecot (docker-mailserver): `alice@vinette.test` / `alice-pass` |
| `make dev-mail-check` | проверить IMAP-расширения dev-почты (SPECIAL-USE, IDLE, MOVE, CONDSTORE, QRESYNC, QUOTA, ID, METADATA) |
| `make dev-mail-down` | остановить dev-почту и удалить её данные |
| `make docker` | образ `vinette:dev` |
| `make clean` | удалить артефакты сборки |

Планируются: `make e2e` (VIN-014), `make seed` (VIN-012). Не ссылайся на них, пока задачи не закрыты.

### Dev-среда

```bash
docker compose -f deploy/docker/compose.dev.yaml up -d   # тестовая почта GreenMail
make dev
```

| Ящик | Логин | Пароль |
| --- | --- | --- |
| `alice@vinette.test` | `alice` | `alice-pass` |
| `bob@vinette.test` | `bob` | `bob-pass` |

GreenMail принимает логин **без** `@домен`; Dovecot в продакшене — полный адрес. Порты: IMAP 3143 (IMAPS 3993), SMTP 3025 (SMTPS 3465), API GreenMail 8081. Dev-конфигурация бэкенда — `appsettings.Development.json` (без шифрования, только локально). Фронтенд проксирует `/api`, `/hubs`, `/branding` на бэкенд.

GreenMail не умеет часть расширений IMAP (SPECIAL-USE, QRESYNC и др.). Для них есть dev-почта на настоящем Dovecot 2.4 + Postfix (`make dev-mail`, файл [compose.dovecot.yaml](deploy/docker/compose.dovecot.yaml)):

| Ящик | Логин (полный адрес) | Пароль |
| --- | --- | --- |
| `alice@vinette.test` | `alice@vinette.test` | `alice-pass` |
| `bob@vinette.test` | `bob@vinette.test` | `bob-pass` |

Порты те же, что у GreenMail (IMAP 3143, IMAPS 3993, SMTP 3025, submission 3587, SMTPS 3465), поэтому два сервера одновременно не запускай. Для IMAPS и submission используется самоподписанный сертификат (`mail.vinette.test`, `localhost`) — клиент в dev должен его принимать. Незашифрованный IMAP на 3143 разрешён только для dev. Первый запуск скачивает образ около 1 ГБ и стартует около минуты.


### e2e

После VIN-014: `make e2e` (Playwright, сценарии «вход», «чтение», «отправка»). Браузеры: `pnpm exec playwright install chromium` в `frontend/`.

### Docker и голое железо

- Образ: `make docker`; запуск — [deploy/docker/compose.yaml](deploy/docker/compose.yaml); порт 8080, non-root, ключи Data Protection в томе `/data`.
- Голое железо: [deploy/systemd/vinette.service](deploy/systemd/vinette.service); пользователь `vinette`, конфиг `/etc/vinette`, данные `/var/lib/vinette`.

### Конфигурация

Слои по возрастанию приоритета: `appsettings.json` → переменные окружения `VINETTE_*` (вложенность через `__`, например `VINETTE_Imap__Host`) → файл по пути из `VINETTE_CONFIG`. Образец — [deploy/config/appsettings.example.json](deploy/config/appsettings.example.json). Новый ключ: класс Options с валидацией, запись в примере и в документации.

### Подводные камни

- Команды `dotnet` запускай из `backend/` (там `global.json`); тесты — `dotnet test --solution Vinette.sln`, без `-nologo` (на .NET 10 используется Microsoft.Testing.Platform).
- TypeScript закреплён на 6.0.x (typescript-eslint не поддерживает 7).

## 5. Git

- Коммиты — [Conventional Commits](https://www.conventionalcommits.org/) на английском, до 72 символов, повелительное наклонение: `feat`, `fix`, `refactor`, `perf`, `test`, `docs`, `build`, `ci`, `chore`, `security`.
- Scope: `backend` | `frontend` | `deploy` | `docs`. Пример: `feat(backend): add folder list endpoint`.
- `main` всегда зелёный. Изменения попадают в `main` только через PR со squash-слиянием; заголовок PR — Conventional Commit. Не пушь в `main` напрямую, не переписывай опубликованную историю.
- PR: заполни шаблон; укажи VIN-xxx.
- Релизные теги: `vX.Y.Z`.
- Коммить, пушь и открывай PR только когда это попросил владелец или это явно входит в задачу.

## 6. Версии и релизы

- SemVer. Единый источник версии — `VersionPrefix` в [backend/Directory.Build.props](backend/Directory.Build.props); версия в `frontend/package.json` совпадает с ней (проверка в CI — VIN-015).
- Релиз: перенеси `[Unreleased]` в `[X.Y.Z] - ГГГГ-ММ-ДД`, подними версию, закоммить, поставь тег `vX.Y.Z`. `release.yml` проверит, что тег совпадает с версией, соберёт образ с тегами `X.Y.Z`, `X.Y`, `latest` и создаст GitHub Release из секции CHANGELOG.
- Тег ставит и пушит только владелец или по его прямому указанию.

## 7. CHANGELOG

- Формат [Keep a Changelog](https://keepachangelog.com/en/1.1.0/): `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed`, `Security`.
- Пиши для пользователей и администраторов: что изменилось в поведении, конфигурации, API. Не пересказывай коммиты и не упоминай внутренние рефакторинги.
- Каждая задача, меняющая поведение, добавляет строку в `[Unreleased]`. Язык — английский, как в существующих записях.

## 8. Backend

- Async до конца, `CancellationToken` через все слои; без `.Result`, `.Wait()`, `GetAwaiter().GetResult()`.
- DI; настройки — классы Options с валидацией при старте.
- Ошибки — ProblemDetails (RFC 9457) с понятным кодом; без утечки внутренних деталей.
- Структурированные логи (JSON, поля, а не склейка строк); без секретов (раздел 10).
- MailKit используется **только** в `Vinette.Mail`; остальной код работает через интерфейсы `Vinette.Core`. `Vinette.Core` не зависит от MailKit.
- Один `ImapClient` никогда не используется параллельно: доступ сериализован.
- Идентификатор письма в API — `(folder, uidValidity, uid)`, никогда не порядковый номер.
- Публичный API — только `/api/v1`; OpenAPI — контракт. После изменения API запусти `make api-types` и закоммить результат (`backend/openapi/`, `schema.d.ts`).
- `TreatWarningsAsErrors` включён; не подавляй предупреждения без причины в комментарии.

## 9. Frontend

- Структура по фичам: `src/features/<feature>`; общее — `src/shared`; маршруты и провайдеры — `src/app`.
- Серверное состояние — только TanStack Query; не дублируй его в локальном состоянии.
- Типы API — только сгенерированные (`make api-types`). Ручные DTO запрещены.
- Цвета, отступы, радиусы, шрифты — только из токенов ([tokens.css](frontend/src/styles/tokens.css), утилиты Tailwind из [DESIGN.md](docs/design/DESIGN.md)). Никаких HEX и произвольных цветов в компонентах.
- Все пользовательские строки — через i18n (`ru.json`, `en.json`, ключи в обоих файлах). Русский — язык по умолчанию.
- Строй интерфейс из `src/shared/ui`; недостающий примитив добавляй туда с тестом и записью в `/dev/ui`.
- Доступность — по [DESIGN.md §11](docs/design/DESIGN.md): семантические области, фокус-кольцо, `aria-label` у иконочных кнопок, `label` у полей, цели нажатия ≥ 40/44 px, AA-контраст, `prefers-reduced-motion`.
- Иконки — lucide-react, 18 px, `strokeWidth` 1.8.

## 10. Безопасность (жёсткие правила)

Нарушение любого пункта — блокер, не «долг на потом».

- HTML письма — только серверный санитайз + `iframe` с `sandbox` **без** `allow-scripts` и `allow-same-origin`.
- Содержимое писем никогда не вставляется в основной документ (`dangerouslySetInnerHTML` с данными письма запрещён).
- Внешние ресурсы писем (картинки, шрифты, стили) заблокированы по умолчанию; разрешённые загружаются через прокси бэкенда с защитой от SSRF.
- CSP без `unsafe-inline` для скриптов.
- Cookie сессии: `HttpOnly`, `Secure`, `SameSite=Strict`; идентификатор сессии непрозрачный.
- CSRF-защита на всех изменяющих запросах.
- Пароли, токены, cookie и тела писем никогда не попадают в логи, метрики и сообщения об ошибках.
- Rate limiting на вход.
- Пароль пользователя хранится только на сервере и только зашифрованным (Data Protection); в браузер не отправляется.
- Ключи Data Protection — на постоянном хранилище; без них все сессии теряются.
- Вложения отдаются с `Content-Disposition: attachment` и `X-Content-Type-Options: nosniff`; HTML и SVG никогда не отдаются инлайном.
- Не доверяй заголовкам `X-Forwarded-*` от недоверенных источников.
- Уязвимости — через GitHub Security Advisories ([SECURITY.md](SECURITY.md)), не в публичных issues.

## 11. UI

- Никакого технического жаргона для пользователя: IMAP, SMTP, порт, сервер, DKIM, SPF, TLS, UID, MIME и подобное — только в будущей админ-панели.
- Ошибки — на человеческом языке: что случилось и что делать дальше.
- Дизайн меняется только через обновление [DESIGN.md](docs/design/DESIGN.md) **с согласия владельца**. Вёрстка идёт строго по спецификации; при расхождении картинки в `docs/design/screens/` и текста прав текст. Нашёл противоречие или пробел — задай вопрос, не додумывай.

## 12. Тесты

- Бэкенд: xUnit. Фикстуры писем `.eml` — `backend/tests/fixtures` (koi8-r, windows-1251, base64, quoted-printable, вложенный multipart, inline-картинки, вредоносный HTML); у каждой фикстуры описано ожидание.
- Санитайзер покрыт отдельным набором XSS-векторов; новый вектор добавляется данными, а не кодом теста.
- Интеграционные тесты — против реального почтового сервера в Testcontainers, помечены категорией Integration; `make test` не требует Docker.
- Фронтенд: Vitest + Testing Library для логики и компонентов; каждый примитив `shared/ui` имеет тест.
- e2e: Playwright — сценарии «вход», «чтение», «отправка».
- Баг-фикс начинается с теста, который падает. Тесты не зависят от порядка и от внешней сети.

## 13. Definition of Done

Задача закрыта, только если выполнено всё:

- код + тесты;
- `make check` зелёный;
- все пользовательские строки в i18n (ru и en);
- доступность проверена (клавиатура, `aria-*`, контраст);
- CHANGELOG и ROADMAP обновлены;
- документация обновлена, если менялось поведение, конфигурация или API;
- нет секретов и персональных данных в коде, логах и фикстурах.

**Лицензии зависимостей.** Проект — Apache-2.0. Перед добавлением зависимости проверь лицензию: MIT, BSD, Apache-2.0, ISC — можно; GPL/AGPL — только с согласия владельца (MPL и прочие — спроси). Укажи лицензию в запросе.

## 14. Документация

- [docs/architecture.md](docs/architecture.md) — обновляй при изменении границ проектов, потоков данных, модели соединений, сессий, безопасности.
- [docs/adr/](docs/adr/) — новое ADR при любом решении, меняющем стек, лицензию или архитектурные границы. Формат: **контекст → решение → последствия**; файл `NNNN-кратко.md`, статус в заголовке. Принятые ADR не переписывай — заменяй новыми.
- [DESIGN.md](docs/design/DESIGN.md) — только по согласованию с владельцем (раздел 11).
- [README.md](README.md) — при изменении быстрого старта, целей Makefile, требований к окружению.
- [deploy/config/appsettings.example.json](deploy/config/appsettings.example.json) — при каждом новом ключе конфигурации.

## Шпаргалка

```bash
make setup                      # зависимости
make dev                        # бэкенд :5080 + фронтенд :5173
docker compose -f deploy/docker/compose.dev.yaml up -d   # тестовая почта (alice / alice-pass)
make check                      # lint + test + build — перед каждым коммитом
make api-types                  # после изменения API
make format                     # автоформатирование
cd backend && dotnet test --solution Vinette.sln         # только тесты бэкенда
cd frontend && corepack pnpm test                        # только тесты фронтенда
git fetch origin && git switch -c feat/VIN-012-short-name origin/main   # ветка под задачу — от свежего main
git commit -m "feat(backend): add folder list endpoint"  # Conventional Commits
make docker                     # образ vinette:dev
```
