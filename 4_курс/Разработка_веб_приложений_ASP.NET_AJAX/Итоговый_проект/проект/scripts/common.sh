#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
if [[ ! -f .env ]]; then
    echo 'No .env found: cp .env.example .env and edit credentials.' >&2
    exit 1
fi
set -a
# shellcheck disable=SC1091
source .env
set +a
for var in POSTGRES_PASSWORD MUZLOTO_INTEGRATION_SECRET MUZLOTO_JWT_KEY; do
  if [[ -z "${!var:-}" || "${!var}" == REPLACE_* ]]; then
    echo "Set $var in .env" >&2
    exit 1
  fi
done
export ConnectionStrings__Default="Host=localhost;Port=5432;Database=muzloto_dev;Username=muzloto;Password=${POSTGRES_PASSWORD}"
# Optional: without these, Development uses the local admin from appsettings.Development.json.
if [[ -n "${MUZLOTO_ADMIN_EMAIL:-}" && "${MUZLOTO_ADMIN_EMAIL}" != REPLACE_* &&
      -n "${MUZLOTO_ADMIN_PASSWORD:-}" && "${MUZLOTO_ADMIN_PASSWORD}" != REPLACE_* ]]; then
  export BootstrapAdmin__Email="$MUZLOTO_ADMIN_EMAIL"
  export BootstrapAdmin__Password="$MUZLOTO_ADMIN_PASSWORD"
fi
export Integration__Secret="$MUZLOTO_INTEGRATION_SECRET"

export Jwt__Key="$MUZLOTO_JWT_KEY"
