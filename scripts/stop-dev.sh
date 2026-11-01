#!/usr/bin/env bash
# Stops the processes started by run-dev.sh (by PID file, then by this project's assembly names).
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [[ -f "$ROOT/App_Data/dev.pids" ]]; then
  xargs kill <"$ROOT/App_Data/dev.pids" 2>/dev/null || true
  rm -f "$ROOT/App_Data/dev.pids"
fi
pkill -f 'Wings\.MockBackend\.dll' 2>/dev/null || true
pkill -f 'Wings\.Gateway\.dll' 2>/dev/null || true
pkill -f 'Wings\.Admin\.Api\.dll' 2>/dev/null || true
echo "Stopped."
