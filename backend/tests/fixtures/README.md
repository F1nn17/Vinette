# Тестовые письма

Фикстуры `.eml` для тестов разбора MIME, кодировок, санитайзера и интерфейса (VIN-054…070). Файлы хранятся **без нормализации окончаний строк** (`-text` в `.gitattributes`): CRLF и 8-битные байты значимы — не редактируйте их текстовым редактором, который меняет кодировку или окончания строк.

Машиночитаемое описание ожиданий — [manifest.json](manifest.json) (тема, отправитель, фрагменты текста, вложения, inline-идентификаторы). Тест `FixtureTests` проверяет, что каждый файл есть в манифесте и разбирается парсером в соответствии с ним. Новая фикстура = файл + запись в манифесте + строка в таблице ниже.

Письма адресованы `alice@vinette.test`; в dev-ящики их загружает `make seed`.

Правило для поля `attachments` в манифесте: вложение — часть с `Content-Disposition: attachment` либо `inline` с именем файла и **без** `Content-ID`. Часть с `Content-ID` — встроенный ресурс (`inlineContentIds`), не вложение.

Особенности разбора, выявленные фикстурами: кодировки koi8-r и windows-1251 в .NET требуют `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` (в тестах это делает `TestSetup`, в приложении — нужно при старте, VIN-055); MimeKit раскрывает обратную косую черту в quoted-string как escape-символ, поэтому path traversal в имени файла проверяется формой `../../`.

| Файл | Что проверяет |
| --- | --- |
| `01-plain-ascii.eml` | Baseline: us-ascii text/plain, no encodings. |
| `02-utf8-qp-alternative.eml` | multipart/alternative, UTF-8 quoted-printable in both parts, RFC 2047 Q-encoded subject. |
| `03-koi8r-8bit.eml` | Raw 8-bit koi8-r body, B-encoded koi8-r subject. |
| `04-windows1251-qp.eml` | windows-1251 quoted-printable body, Q-encoded subject; '№' exists only in cp1251, not in koi8-r. |
| `05-utf8-base64.eml` | UTF-8 base64 body and B-encoded subject, non-Latin and non-BMP-adjacent characters. |
| `06-nested-multipart.eml` | mixed > related > alternative; inline PNG referenced by cid; two real attachments. Inline image is not an attachment. |
| `07-inline-image-cid.eml` | HTML-only related message; the image has a Content-ID and no filename. |
| `08-attachments-unicode-names.eml` | RFC 2231 (filename*=UTF-8'') and legacy RFC 2047 encoded-word file names. |
| `09-attached-rfc822.eml` | Nested message/rfc822 attachment with its own headers (Subject: Inner message). |
| `10-malicious-html.eml` | Sanitizer input: script, event handlers, javascript:/data: links, iframe/object/embed, form, SVG script, CSS expression/url, meta refresh, base, @import, conditional comment. After sanitizing, none of these may remain; 'safe-marker' and the https link must survive. |
| `11-dangerous-attachments.eml` | SVG and HTML attachments declared 'inline' and a path-traversal file name. They must be served as attachment with nosniff, never inline; the name must be sanitized to a plain file name. |
| `12-external-resources.eml` | Tracking pixel, remote images in img/background/srcset/table, remote stylesheet, protocol-relative URL. All must be blocked by default and rewritten to the proxy when allowed. |
| `13-html-only.eml` | Single text/html part; the text version must be derived (or absent) without errors. |
| `14-attachment-only.eml` | No text or HTML body at all; the viewer must show an empty body, not fail. |
| `15-broken-headers.eml` | No Date or Message-ID, bare LF endings, unterminated encoded word, duplicate Subject, unknown charset. The parser must not throw; show raw subject text when decoding fails. |
| `16-auth-results-pass.eml` | dkim=pass, spf=pass, dmarc=pass -> 'sender verified'. |
| `17-auth-results-fail.eml` | spf=fail, dkim=none, dmarc=fail -> 'looks like fraud' warning. |
| `18-reply-with-quote.eml` | Reply with In-Reply-To/References and a quoted block (for quote folding and reply composition tests). |
| `19-subject-split-encoded-words.eml` | One UTF-8 character is split between two RFC 2047 encoded words (non-conforming but common); decoders must join bytes before decoding. |
| `20-many-recipients.eml` | 30 folded To recipients plus Cc; for address list parsing and reply-all. |
