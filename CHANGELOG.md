# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Sessions: signed-in users are kept in server memory with the mailbox password encrypted (ASP.NET Core Data Protection); a session ends after `Session:IdleTimeout` of inactivity or `Session:MaxLifetime` after login. New key `Session:RememberMeLifetime` (default 30 days) for "stay signed in" sessions. Without `DataProtection:KeysPath` the application warns at startup that all users are signed out on every restart.
- Configuration keys `Imap:Timeout` (limit for connecting and for each reply of the mail server, default 15 s) and `Imap:AllowInvalidCertificate` (accept any TLS certificate of the mail server; off by default, for development servers with self-signed certificates only).
- API errors are returned as `application/problem+json` (RFC 9457) with a machine-readable `code` and a `correlationId`; every response carries an `X-Correlation-Id` header that also appears in the logs.
- Design specification (`docs/design/DESIGN.md`), design tokens wired into Tailwind v4, Golos Text font bundled locally, brand icons and web manifest.
- UI primitives (Button, IconButton, Chip, LabelTag, Avatar, Badge, TextField, TextArea, Select, Checkbox, ProgressBar, Logo) with tests and a dev-only `/dev/ui` catalogue.

- Project scaffold: backend (ASP.NET Core, .NET 10) and frontend (React + Vite) skeletons, build tooling, CI/CD, Docker and systemd deployment, documentation.
