#!/usr/bin/env bash
# Starts a complete local environment: 8 mock application servers, the admin API (seeds the SQLite database and runs
# the health monitor) and one gateway. Logs go to App_Data/logs. Stop with scripts/stop-dev.sh.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT PATH="$DOTNET_ROOT:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$ROOT/App_Data/logs"

# Development credentials are generated once and kept in App_Data/dev-credentials.env (git-ignored).
CREDS="$ROOT/App_Data/dev-credentials.env"
if [[ ! -f "$CREDS" ]]; then
  gen() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 16; }
  {
    echo "Seed__AdminPassword=Adm-$(gen)"
    echo "Seed__SampleUserPassword=Usr-$(gen)"
  } > "$CREDS"
  chmod 600 "$CREDS"
  echo "Created development credentials in $CREDS"
fi
set -a; source "$CREDS"; set +a

dotnet build "$ROOT/Wings.LoadBalancer.slnx" -v q -nologo >/dev/null

MASTER_KEY="ZGV2ZWxvcG1lbnQtb25seS1tYXN0ZXIta2V5LWNoYW5nZS1tZQ=="
declare -a SERVERS=(
  "5101 APP-SERVER-01 0192f000-0000-7000-8000-000000000301 2.1.0"
  "5102 APP-SERVER-02 0192f000-0000-7000-8000-000000000302 2.1.0"
  "5103 APP-SERVER-03 0192f000-0000-7000-8000-000000000303 2.1.0"
  "5104 APP-SERVER-04 0192f000-0000-7000-8000-000000000304 2.1.0"
  "5105 APP-SERVER-05 0192f000-0000-7000-8000-000000000305 2.1.0"
  "5106 APP-SERVER-06 0192f000-0000-7000-8000-000000000306 2.1.0"
  "5107 APP-SERVER-07 0192f000-0000-7000-8000-000000000307 2.1.0"
  "5108 STAGING-01 0192f000-0000-7000-8000-000000000308 2.2.0"
)
MOCK="$ROOT/samples/Wings.MockBackend/bin/Debug/net10.0/Wings.MockBackend.dll"
for entry in "${SERVERS[@]}"; do
  read -r port name id version <<<"$entry"
  (cd "$ROOT/samples/Wings.MockBackend" && nohup dotnet "$MOCK" --urls "http://localhost:$port" \
     --MockBackend:ServerName "$name" --MockBackend:ServerId "$id" --MockBackend:Version "$version" \
     --MockBackend:MasterKey "$MASTER_KEY" >"$ROOT/App_Data/logs/$name.log" 2>&1 & echo $! >>"$ROOT/App_Data/dev.pids")
done

(cd "$ROOT/src/Wings.Admin.Api" && ASPNETCORE_ENVIRONMENT=Development nohup dotnet bin/Debug/net10.0/Wings.Admin.Api.dll \
   --urls http://localhost:5290 >"$ROOT/App_Data/logs/admin.log" 2>&1 & echo $! >>"$ROOT/App_Data/dev.pids")
for _ in $(seq 1 60); do curl -sf http://localhost:5290/health/live >/dev/null && break; sleep 1; done

(cd "$ROOT/src/Wings.Gateway" && ASPNETCORE_ENVIRONMENT=Development nohup dotnet bin/Debug/net10.0/Wings.Gateway.dll \
   --urls http://localhost:5280 >"$ROOT/App_Data/logs/gateway.log" 2>&1 & echo $! >>"$ROOT/App_Data/dev.pids")
for _ in $(seq 1 60); do curl -sf http://localhost:5280/_wings/health/ready >/dev/null && break; sleep 1; done

echo "Gateway:   http://localhost:5280   (sign in as ABC / user1 with the sample user password)"
echo "Admin UI:  http://localhost:5290   (admin / see $CREDS)"
echo "API docs:  http://localhost:5290/api/docs"
