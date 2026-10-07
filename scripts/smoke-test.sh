#!/usr/bin/env bash
# End-to-end smoke test for Notify.
#
#   bash scripts/smoke-test.sh [base_url=http://localhost:5600] [sink_port=2525]
#
# Requires a running app (cd src/Notify.Web && dotnet run), bash, curl and python3.
# Starts a local SMTP sink, registers throwaway tenants, imports a CSV, runs a campaign with
# concurrency 4 and asserts the delivery report, the observed parallelism and tenant isolation.
#
# When the app runs in Docker it cannot reach 127.0.0.1 on the host; set SINK_HOST to the address
# the container should use (host.docker.internal on Docker Desktop). The sink then binds 0.0.0.0.
#   SINK_HOST=host.docker.internal bash scripts/smoke-test.sh
set -u
BASE="${1:-http://localhost:5600}"
SINK_PORT="${2:-2525}"
SINK_HOST="${SINK_HOST:-127.0.0.1}"
SINK_BIND="127.0.0.1"; [ "$SINK_HOST" != "127.0.0.1" ] && SINK_BIND="0.0.0.0"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Keep temp files inside the repo: on Windows (Git Bash) /tmp paths are not visible to the native curl.exe.
WORK="$ROOT/scripts/.tmp-smoke"
rm -rf "$WORK"; mkdir -p "$WORK"
# Pick a Python that actually runs (on Windows "python3" may be a Microsoft Store stub).
PY=""
for candidate in python3 python py; do
  if "$candidate" -c "pass" >/dev/null 2>&1; then PY="$candidate"; break; fi
done
FAILED=0

pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1"; FAILED=1; }
assert_eq() { if [ "$2" = "$3" ]; then pass "$1 ($2)"; else fail "$1: expected '$3', got '$2'"; fi; }
token() { grep -o 'name="__RequestVerificationToken" type="hidden" value="[^"]*"' | head -1 | sed 's/.*value="//;s/"$//'; }
wait_short() { "$PY" -c "import time; time.sleep(${1:-1})"; }

cleanup() { [ -n "${SINK_PID:-}" ] && kill "$SINK_PID" 2>/dev/null; rm -rf "$WORK"; }
trap cleanup EXIT

echo "== Notify smoke test against $BASE (sink on $SINK_PORT)"
[ -n "$PY" ] || { echo "python3 not found"; exit 2; }
curl -s -o /dev/null --max-time 5 "$BASE/Account/Login" || { echo "App is not reachable at $BASE. Start it with: cd src/Notify.Web && dotnet run"; exit 2; }

# ---- SMTP sink ----
"$PY" "$ROOT/scripts/smtp-sink.py" "$SINK_PORT" 120 "$SINK_BIND" > "$WORK/sink.log" 2>&1 &
SINK_PID=$!
wait_short 1

# ---- Tenant A: register ----
JAR="$WORK/a.txt"
EMAIL_A="smoke$RANDOM$RANDOM@example.com"
T=$(curl -s -c "$JAR" -b "$JAR" "$BASE/Account/Register" | token)
R=$(curl -s -c "$JAR" -b "$JAR" -o /dev/null -w "%{http_code} %{redirect_url}" \
  --data-urlencode "__RequestVerificationToken=$T" --data-urlencode "CompanyName=Smoke Co" \
  --data-urlencode "Email=$EMAIL_A" --data-urlencode "Password=secret123" --data-urlencode "ConfirmPassword=secret123" \
  "$BASE/Account/Register")
assert_eq "register redirects to SMTP settings" "$R" "302 $BASE/Settings/Smtp"

for p in /Dashboard /Campaigns /Campaigns/Create /Settings/Smtp /Account/Profile /Account/ChangePassword; do
  assert_eq "GET $p" "$(curl -s -b "$JAR" -o /dev/null -w '%{http_code}' "$BASE$p")" "200"
done

# ---- SMTP settings -> sink, concurrency 4 ----
T=$(curl -s -c "$JAR" -b "$JAR" "$BASE/Settings/Smtp" | token)
R=$(curl -s -c "$JAR" -b "$JAR" -o /dev/null -w "%{http_code}" \
  --data-urlencode "__RequestVerificationToken=$T" \
  --data-urlencode "Host=$SINK_HOST" --data-urlencode "Port=$SINK_PORT" --data-urlencode "Security=1" \
  --data-urlencode "Username=" --data-urlencode "Password=" \
  --data-urlencode "FromEmail=noreply@smoke.local" --data-urlencode "FromName=Smoke Co" \
  --data-urlencode "ConcurrencyLevel=4" --data-urlencode "DelayBetweenEmailsMs=0" \
  "$BASE/Settings/Smtp")
assert_eq "save SMTP settings" "$R" "302"

T=$(curl -s -c "$JAR" -b "$JAR" "$BASE/Settings/Smtp" | token)
curl -s -c "$JAR" -b "$JAR" -o /dev/null --data-urlencode "__RequestVerificationToken=$T" --data-urlencode "ToEmail=me@example.com" "$BASE/Settings/TestSmtp"
MSG=$(curl -s -b "$JAR" "$BASE/Settings/Smtp" | grep -o 'Test email sent[^<]*\|Test email failed[^<]*' | head -1)
assert_eq "test email" "${MSG%% to*}" "Test email sent"

# ---- Campaign with CSV (20 valid + 1 invalid + 1 duplicate) ----
{
  echo "Email,Name,Plan"
  for i in $(seq 1 20); do echo "client$i@example.com,Client $i,$([ $((i % 2)) -eq 1 ] && echo Gold || echo Silver)"; done
  echo "not-an-email,Nobody,None"
  echo "client1@example.com,Duplicate,Gold"
} > "$WORK/recipients.csv"

T=$(curl -s -c "$JAR" -b "$JAR" "$BASE/Campaigns/Create" | token)
# The upload uses a relative file name from inside $WORK: Git Bash does not translate POSIX paths inside "@path".
LOC=$(cd "$WORK" && curl -s -c "$JAR" -b "$JAR" -o /dev/null -w "%{redirect_url}" \
  -F "__RequestVerificationToken=$T" -F "Name=Smoke campaign" --form-string "Subject=Hello {{Name}}" \
  --form-string "BodyHtml=<p>Dear {{Name}}, your {{Plan}} plan is ready. Sent to {{Email}}.</p>" \
  -F "RecipientsFile=@recipients.csv;type=text/csv" \
  "$BASE/Campaigns/Create")
ID="${LOC##*/}"
case "$ID" in ''|*[!0-9]*) fail "create campaign (redirect: '$LOC')"; echo "Aborting."; exit 1;; esac
pass "create campaign #$ID"

curl -s -b "$JAR" -o "$WORK/details.html" "$LOC"
assert_eq "import summary" "$(grep -o 'Imported [^<]*' "$WORK/details.html" | head -1)" "Imported 20 recipient(s). Skipped 1 row(s) with an invalid email. Skipped 1 duplicate(s)."
assert_eq "preview renders placeholders" "$(curl -s -b "$JAR" "$BASE/Campaigns/Preview/$ID")" "<p>Dear Client 1, your Gold plan is ready. Sent to client1@example.com.</p>"
assert_eq "export csv" "$(curl -s -b "$JAR" -o /dev/null -w '%{http_code}' "$BASE/Campaigns/Export/$ID")" "200"

# ---- Run and wait ----
T=$(grep -o 'name="__RequestVerificationToken" type="hidden" value="[^"]*"' "$WORK/details.html" | head -1 | sed 's/.*value="//;s/"$//')
assert_eq "run campaign" "$(curl -s -c "$JAR" -b "$JAR" -o /dev/null -w '%{http_code}' --data-urlencode "__RequestVerificationToken=$T" "$BASE/Campaigns/Run/$ID")" "302"

STATUS=""
for _ in $(seq 1 60); do
  STATUS=$(curl -s -b "$JAR" "$BASE/Campaigns/Status/$ID")
  echo "$STATUS" | grep -q '"isActive":false' && break
  wait_short 1
done
get() { echo "$STATUS" | "$PY" -c "import json,sys; print(json.load(sys.stdin)['$1'])"; }
assert_eq "final status" "$(get status)" "Completed"
assert_eq "sent" "$(get sent)" "20"
assert_eq "failed" "$(get failed)" "0"
assert_eq "pending" "$(get pending)" "0"

wait_short 2
PEAK=$(grep -o 'peak_concurrent_connections=[0-9]*' "$WORK/sink.log" | tail -1 | cut -d= -f2)
assert_eq "sink peak concurrent connections" "${PEAK:-0}" "4"

# ---- Tenant B must not see tenant A's campaign ----
JAR_B="$WORK/b.txt"
T=$(curl -s -c "$JAR_B" -b "$JAR_B" "$BASE/Account/Register" | token)
curl -s -c "$JAR_B" -b "$JAR_B" -o /dev/null \
  --data-urlencode "__RequestVerificationToken=$T" --data-urlencode "CompanyName=Other Co" \
  --data-urlencode "Email=smoke$RANDOM$RANDOM@example.com" --data-urlencode "Password=secret123" --data-urlencode "ConfirmPassword=secret123" \
  "$BASE/Account/Register"
assert_eq "tenant isolation: details" "$(curl -s -b "$JAR_B" -o /dev/null -w '%{http_code}' "$BASE/Campaigns/Details/$ID")" "404"
assert_eq "tenant isolation: status" "$(curl -s -b "$JAR_B" -o /dev/null -w '%{http_code}' "$BASE/Campaigns/Status/$ID")" "404"

echo
if [ "$FAILED" -eq 0 ]; then echo "ALL PASSED"; else echo "SOME CHECKS FAILED"; fi
exit "$FAILED"
