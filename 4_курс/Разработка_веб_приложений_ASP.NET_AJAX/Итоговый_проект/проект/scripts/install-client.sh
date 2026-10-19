#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
if ! command -v npm >/dev/null; then
    echo 'Node.js/npm is required to install the SignalR browser client.' >&2
    exit 1
fi
npm install --ignore-scripts --no-audit --no-fund
mkdir -p src/MuzLoto.Web/wwwroot/lib/signalr
cp node_modules/@microsoft/signalr/dist/browser/signalr.min.js \
   src/MuzLoto.Web/wwwroot/lib/signalr/signalr.min.js
echo 'Self-hosted SignalR client installed.'
