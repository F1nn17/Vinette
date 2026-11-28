# Builds the mails of DovecotFixture.FillInboxAsync: one maildir file per mail, numbered 1..n.
# Run as: awk -v n=COUNT -v user=ADDRESS -v curdir=DIR/cur -v newdir=DIR/new -f fill-inbox.awk
BEGIN {
  split("report invoice meeting travel notes", topic, " ")
  split("Jan Feb Mar Apr May Jun Jul Aug Sep Oct Nov Dec", month, " ")
  for (i = 1; i <= n; i++) {
    unseen = (i % 10 == 0)
    flags = (i % 97 == 0) ? "FS" : "S"
    f = (unseen ? newdir : curdir) "/" (1700000000 + i) ".M" i "P1.perf" (unseen ? "" : ":2," flags)
    subject = "Message " i " about " topic[i % 5 + 1]
    if (i % 1999 == 0) subject = subject " needle"
    body = "Hello " i ".\nThis is message " i " of a big mailbox.\n"
    if (i % 997 == 0) body = body "There is a needle in the text.\n"
    if (i % 500 == 0) body = body "Привет, мир: годовой отчёт.\n"
    date = sprintf("Mon, %02d %s 2026 %02d:%02d:%02d +0000", 1 + int(i / 1440) % 28, month[int(i / 40320) % 12 + 1], int(i / 60) % 24, i % 60, i % 60)
    printf "From: Sender %d <sender%d@example.org>\nTo: %s\nSubject: %s\nDate: %s\nMessage-ID: <%d@perf.test>\nMIME-Version: 1.0\nContent-Type: text/plain; charset=utf-8\nContent-Transfer-Encoding: 8bit\n\n%s", i % 200, i % 200, user, subject, date, i, body > f
    close(f)
  }
}
