#!/usr/bin/env python3
"""Loads the test emails from backend/tests/fixtures into dev mailboxes via IMAP APPEND.

Idempotent: every fixture is tagged with an IMAP keyword (vfx-NN) and is skipped when the
mailbox already has it. Messages are uploaded byte-for-byte (CRLF and 8-bit charsets intact).
Standard library only. Usage: see --help or `make seed`.
"""
import argparse
import imaplib
import json
import re
import ssl
import sys
import time
from email.utils import parsedate_to_datetime
from pathlib import Path

DEFAULT_FIXTURES = Path(__file__).resolve().parents[3] / "backend" / "tests" / "fixtures"


def keyword_for(file_name: str) -> str:
    """'06-nested-multipart.eml' -> 'vfx-06' (IMAP keywords are atoms; short and stable)."""
    match = re.match(r"(\d+)-", file_name)
    if not match:
        raise ValueError(f"Fixture name must start with a number: {file_name}")
    return f"vfx-{match.group(1)}"


def internal_date(raw: bytes) -> float:
    """INTERNALDATE from the Date header so the list order is deterministic; now() when absent/broken."""
    header = re.search(rb"^Date:[ \t]*(.+)$", raw, re.MULTILINE | re.IGNORECASE)
    if header:
        try:
            return parsedate_to_datetime(header.group(1).decode("ascii", "replace").strip()).timestamp()
        except (TypeError, ValueError):
            pass
    return time.time()


def connect(host: str, port: int, security: str) -> imaplib.IMAP4:
    context = ssl.create_default_context()
    context.check_hostname = False  # dev server uses a self-signed certificate
    context.verify_mode = ssl.CERT_NONE
    if security == "tls":
        return imaplib.IMAP4_SSL(host, port, ssl_context=context)
    client = imaplib.IMAP4(host, port)
    if security == "starttls":
        client.starttls(ssl_context=context)
    return client


def check(status: str, data, what: str):
    if status != "OK":
        raise RuntimeError(f"{what} failed: {status} {data}")
    return data


def seed_mailbox(client: imaplib.IMAP4, mailbox: str, fixtures: Path, names: list[str], reset: bool) -> tuple[int, int]:
    status, data = client.select(mailbox)
    check(status, data, f"SELECT {mailbox}")

    if reset:
        for name in names:
            status, found = client.uid("SEARCH", None, "KEYWORD", keyword_for(name))
            check(status, found, "SEARCH")
            uids = found[0].split()
            if uids:
                client.uid("STORE", b",".join(uids).decode(), "+FLAGS.SILENT", r"(\Deleted)")
        client.expunge()

    appended = skipped = 0
    for name in names:
        keyword = keyword_for(name)
        status, found = client.uid("SEARCH", None, "KEYWORD", keyword)
        check(status, found, "SEARCH")
        if found[0].split():
            skipped += 1
            continue
        raw = (fixtures / name).read_bytes()
        status, data = client.append(mailbox, f"({keyword})", imaplib.Time2Internaldate(internal_date(raw)), raw)
        check(status, data, f"APPEND {name}")
        appended += 1
    return appended, skipped


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="localhost")
    parser.add_argument("--port", type=int, default=3143)
    parser.add_argument("--security", choices=["none", "starttls", "tls"], default="none")
    parser.add_argument(
        "--user",
        action="append",
        metavar="LOGIN:PASSWORD",
        help="mailbox to fill; repeatable (default: alice@vinette.test and bob@vinette.test)",
    )
    parser.add_argument("--mailbox", default="INBOX")
    parser.add_argument("--fixtures", type=Path, default=DEFAULT_FIXTURES)
    parser.add_argument("--reset", action="store_true", help="delete previously seeded messages first")
    args = parser.parse_args()

    users = args.user or ["alice@vinette.test:alice-pass", "bob@vinette.test:bob-pass"]
    manifest = json.loads((args.fixtures / "manifest.json").read_text(encoding="utf-8"))
    names = [entry["file"] for entry in manifest]

    for credentials in users:
        login, _, password = credentials.partition(":")
        client = connect(args.host, args.port, args.security)
        try:
            check(*client.login(login, password), f"LOGIN {login}")
            appended, skipped = seed_mailbox(client, args.mailbox, args.fixtures, names, args.reset)
            print(f"{login}: {appended} appended, {skipped} already present ({args.mailbox})")
        finally:
            try:
                client.logout()
            except imaplib.IMAP4.error:
                pass
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, RuntimeError, imaplib.IMAP4.error) as error:
        print(f"seed failed: {error}", file=sys.stderr)
        sys.exit(1)
