# Участие в разработке

Спасибо, что хотите помочь Vinette. Подробные правила для людей и агентов — в [AGENTS.md](AGENTS.md); ниже — коротко.

## Сборка и проверки

```bash
make setup   # зависимости
make check   # lint + test + build; то же самое запускает CI
```

PR не принимается, пока `make check` не проходит.

## Коммиты

Формат [Conventional Commits](https://www.conventionalcommits.org/): `тип: описание` в повелительном наклонении, до 72 символов.
Типы: `feat`, `fix`, `docs`, `refactor`, `test`, `build`, `ci`, `chore`. Пример: `feat: add folder list endpoint`.

## Pull request

- Одна задача — один PR; укажите задачу из [ROADMAP.md](ROADMAP.md) (`VIN-xxx`).
- Добавьте или обновите тесты.
- Отразите изменение в [CHANGELOG.md](CHANGELOG.md), секция `[Unreleased]`.
- Заполните чек-лист из шаблона PR.

## Зависимости

Проект распространяется под Apache-2.0. Новая зависимость должна иметь совместимую лицензию (MIT, BSD, Apache-2.0, ISC). GPL/AGPL — только с согласия владельца проекта.

## Уязвимости

Не открывайте публичные issues — см. [SECURITY.md](SECURITY.md).
