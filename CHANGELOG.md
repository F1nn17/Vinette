# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Brute-force protection for sign-in: after 5 wrong passwords a mailbox is locked for 1 minute, and every further failure doubles the lock up to 15 minutes; a client address is locked after 20 failures; at most 30 sign-in requests per minute per client address. Locked attempts answer `429` with `Retry-After` without contacting the mail server. Tunable with `Security:LoginRateLimit:*`. Counters are per instance and kept in memory. Behind a reverse proxy the per-address limits need the real client address (trusted proxy settings, coming next), otherwise all users share one address.
- CSRF protection: every state-changing API request (POST, PUT, PATCH, DELETE) must carry an anti-forgery token in the `X-CSRF-Token` header (issued by `GET /api/v1/csrf`) and come from a page of the application (`Origin`/`Referer` check; the real-time hub is checked by `Origin`). New optional key `Security:TrustedOrigins` lists extra allowed origins (for example a front-end dev server or an unusual proxy setup). Scripts that call the API without a browser must send both headers.
- `GET /api/v1/me` returns the signed-in mailbox. All API endpoints and the real-time hub now require a signed-in session by default (`401` as `problem+json` otherwise); only health, sign-in/out, branding files, the OpenAPI document (development) and the web app itself are public.
- Sign-in API: `POST /api/v1/auth/login` (address, password, "stay signed in") and `POST /api/v1/auth/logout`. The session id is kept in the `__Host-vinette_session` cookie (`HttpOnly`, `Secure`, `SameSite=Strict`); sign-in therefore needs HTTPS (or `localhost`). Failed and successful sign-ins are logged with the address and client IP, never the password.
- Sessions: signed-in users are kept in server memory with the mailbox password encrypted (ASP.NET Core Data Protection); a session ends after `Session:IdleTimeout` of inactivity or `Session:MaxLifetime` after login. New key `Session:RememberMeLifetime` (default 30 days) for "stay signed in" sessions. Without `DataProtection:KeysPath` the application warns at startup that all users are signed out on every restart.
- Configuration keys `Imap:Timeout` (limit for connecting and for each reply of the mail server, default 15 s) and `Imap:AllowInvalidCertificate` (accept any TLS certificate of the mail server; off by default, for development servers with self-signed certificates only).
- API errors are returned as `application/problem+json` (RFC 9457) with a machine-readable `code` and a `correlationId`; every response carries an `X-Correlation-Id` header that also appears in the logs.
- Design specification (`docs/design/DESIGN.md`), design tokens wired into Tailwind v4, Golos Text font bundled locally, brand icons and web manifest.
- UI primitives (Button, IconButton, Chip, LabelTag, Avatar, Badge, TextField, TextArea, Select, Checkbox, ProgressBar, Logo) with tests and a dev-only `/dev/ui` catalogue.

- Project scaffold: backend (ASP.NET Core, .NET 10) and frontend (React + Vite) skeletons, build tooling, CI/CD, Docker and systemd deployment, documentation.
