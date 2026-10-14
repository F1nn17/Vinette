# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- API errors are returned as `application/problem+json` (RFC 9457) with a machine-readable `code` and a `correlationId`; every response carries an `X-Correlation-Id` header that also appears in the logs.
- Design specification (`docs/design/DESIGN.md`), design tokens wired into Tailwind v4, Golos Text font bundled locally, brand icons and web manifest.
- UI primitives (Button, IconButton, Chip, LabelTag, Avatar, Badge, TextField, TextArea, Select, Checkbox, ProgressBar, Logo) with tests and a dev-only `/dev/ui` catalogue.

- Project scaffold: backend (ASP.NET Core, .NET 10) and frontend (React + Vite) skeletons, build tooling, CI/CD, Docker and systemd deployment, documentation.
