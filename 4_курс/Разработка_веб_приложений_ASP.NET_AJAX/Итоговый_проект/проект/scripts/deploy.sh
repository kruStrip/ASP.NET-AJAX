#!/usr/bin/env bash
# Builds the image locally and deploys МузЛото to the shared server.
#   bash scripts/deploy.sh            — build, upload, (re)start the app
# Requires an SSH host alias (default "boca", override with MUZLOTO_SSH_HOST).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOST="${MUZLOTO_SSH_HOST:-boca}"
REMOTE_DIR="${MUZLOTO_REMOTE_DIR:-/home/BocA/muzloto}"
TAG="$(git -C "$ROOT" rev-parse --short HEAD)$(git -C "$ROOT" diff --quiet || echo -dirty)"
SSH=(ssh -o BatchMode=yes -o ClearAllForwardings=yes "$HOST")

echo "==> Build muzloto-web:$TAG"
docker build -t "muzloto-web:$TAG" -t muzloto-web:latest "$ROOT"

echo "==> Upload image ($(docker image inspect muzloto-web:latest --format '{{.Size}}' | numfmt --to=iec))"
docker save "muzloto-web:$TAG" muzloto-web:latest | gzip -1 | "${SSH[@]}" 'gunzip | docker load'

echo "==> Upload compose file"
"${SSH[@]}" "mkdir -p '$REMOTE_DIR/backups'"
scp -q -o BatchMode=yes -o ClearAllForwardings=yes "$ROOT/deploy/docker-compose.prod.yml" "$HOST:$REMOTE_DIR/docker-compose.yml"

echo "==> Secrets (.env is created on the server once and never leaves it)"
"${SSH[@]}" "REMOTE_DIR='$REMOTE_DIR' ADMIN_EMAIL='${MUZLOTO_ADMIN_EMAIL:-}' bash -s" <<'REMOTE'
set -euo pipefail
cd "$REMOTE_DIR"
if [[ ! -f .env ]]; then
  [[ -n "$ADMIN_EMAIL" ]] || { echo "Set MUZLOTO_ADMIN_EMAIL for the first deployment" >&2; exit 1; }
  umask 077
  admin_password="$(openssl rand -base64 18 | tr -d '/+=')7"
  cat > .env <<ENV
POSTGRES_PASSWORD=$(openssl rand -hex 24)
MUZLOTO_JWT_KEY=$(openssl rand -hex 48)
MUZLOTO_INTEGRATION_SECRET=$(openssl rand -hex 24)
MUZLOTO_ADMIN_EMAIL=$ADMIN_EMAIL
MUZLOTO_ADMIN_PASSWORD=$admin_password
ENV
  echo "Created .env. Admin: $ADMIN_EMAIL / $admin_password  (shown once — save it in a password manager)"
fi
REMOTE

echo "==> Start"
"${SSH[@]}" "cd '$REMOTE_DIR' && MUZLOTO_TAG='$TAG' docker compose up -d --remove-orphans && docker image prune -f >/dev/null"

echo "==> Health (from the proxy network)"
for i in $(seq 1 30); do
  if "${SSH[@]}" "docker exec calendar-nginx wget -qO- http://muzloto-app:8080/health" 2>/dev/null | grep -q ok; then
    echo "OK: muzloto-app is healthy"; exit 0
  fi
  sleep 3
done
echo "App did not become healthy; logs:" >&2
"${SSH[@]}" "cd '$REMOTE_DIR' && docker compose logs --tail 60 app" >&2
exit 1
