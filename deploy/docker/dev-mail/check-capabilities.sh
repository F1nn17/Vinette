#!/usr/bin/env bash
# Verifies that the dev mail server advertises the IMAP capabilities Vinette needs.
# Usage: check-capabilities.sh [host] [port] [login] [password]
set -euo pipefail

HOST="${1:-localhost}"
PORT="${2:-3143}"
LOGIN="${3:-alice@vinette.test}"
PASSWORD="${4:-alice-pass}"

REQUIRED=(SPECIAL-USE IDLE MOVE CONDSTORE QRESYNC QUOTA ID METADATA)
OPTIONAL=(PREVIEW NOTIFY LIST-STATUS ESEARCH SORT THREAD=REFERENCES BINARY COMPRESS=DEFLATE)

exec 3<>"/dev/tcp/${HOST}/${PORT}"
IFS= read -r -t 10 _greeting <&3 || { echo "No IMAP greeting from ${HOST}:${PORT}" >&2; exit 2; }
printf 'a1 LOGIN "%s" "%s"\r\n' "$LOGIN" "$PASSWORD" >&3

response=""
while IFS= read -r -t 10 line <&3; do
  line="${line%$'\r'}"
  case "$line" in
    "a1 OK"*) response="$line"; break ;;
    "a1 "*) echo "Login failed: $line" >&2; exit 2 ;;
  esac
done
printf 'a2 LOGOUT\r\n' >&3 || true
exec 3>&-

# Dovecot puts the post-login capability list into the OK response: "a1 OK [CAPABILITY ...] Logged in"
capabilities=" $(sed -n 's/.*\[CAPABILITY \([^]]*\)\].*/\1/p' <<<"$response") "
if [ "$capabilities" = "  " ]; then
  echo "No CAPABILITY in login response: $response" >&2
  exit 2
fi

missing=0
echo "Required:"
for cap in "${REQUIRED[@]}"; do
  if [[ "$capabilities" == *" $cap "* ]]; then echo "  ok       $cap"; else echo "  MISSING  $cap"; missing=1; fi
done
echo "Optional:"
for cap in "${OPTIONAL[@]}"; do
  if [[ "$capabilities" == *" $cap "* ]]; then echo "  yes      $cap"; else echo "  no       $cap"; fi
done

exit "$missing"
