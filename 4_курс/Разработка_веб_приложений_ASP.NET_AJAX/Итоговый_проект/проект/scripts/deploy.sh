#!/usr/bin/env bash
# Builds the image locally and deploys МузЛото to the shared server.
#   bash scripts/deploy.sh            — build, upload, (re)start the app
# Requires an SSH host alias (default "boca", override with MUZLOTO_SSH_HOST).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HOST="${MUZLOTO_SSH_HOST:-boca}"
REMOTE_DIR="${MUZLOTO_REMOTE_DIR:-/home/BocA/muzloto}"
TAG="$(git -C "$ROOT" rev-parse --short HEAD)$(git -C "$ROOT" diff --quiet || echo -dirty)"
# The small server sometimes answers slowly: generous timeouts and keepalives.
SSH_OPTS=(-o BatchMode=yes -o ClearAllForwardings=yes -o ConnectTimeout=40 -o ConnectionAttempts=3 -o ServerAliveInterval=15)
# sshd on the server randomly drops some new connections under brute-force bot load (MaxStartups):
# retry anything that fails with ssh's own exit code 255.
remote() {
  local attempt rc
  for attempt in 1 2 3 4 5; do
    ssh "${SSH_OPTS[@]}" "$HOST" "$@" && return 0 || rc=$?
    [[ $rc -eq 255 ]] || return $rc
    echo "   (ssh dropped, retry $attempt)" >&2; sleep 4
  done
  return $rc
}
# Same, but re-opens the input file on every attempt so a retry never starts mid-file.
remote_file() {
  local file=$1 attempt rc; shift
  for attempt in 1 2 3 4 5; do
    ssh "${SSH_OPTS[@]}" "$HOST" "$@" < "$file" && return 0 || rc=$?
    [[ $rc -eq 255 ]] || return $rc
    echo "   (ssh dropped, retry $attempt)" >&2; sleep 4
  done
  return $rc
}

echo "==> Build muzloto-web:$TAG"
docker build -t "muzloto-web:$TAG" -t muzloto-web:latest "$ROOT"

echo "==> Upload image ($(docker image inspect muzloto-web:latest --format '{{.Size}}' | numfmt --to=iec))"
IMAGE_FILE="$(mktemp --suffix=.tar.gz)"; trap 'rm -f "$IMAGE_FILE"' EXIT
docker save "muzloto-web:$TAG" muzloto-web:latest | gzip -1 > "$IMAGE_FILE"
remote_file "$IMAGE_FILE" 'gunzip | docker load'

echo "==> Upload compose file"
remote "mkdir -p '$REMOTE_DIR/backups'"
remote_file "$ROOT/deploy/docker-compose.prod.yml" "cat > '$REMOTE_DIR/docker-compose.yml'"

echo "==> Secrets (.env is created on the server once and never leaves it)"
remote "REMOTE_DIR='$REMOTE_DIR' ADMIN_EMAIL='${MUZLOTO_ADMIN_EMAIL:-}' bash -s" <<'REMOTE'
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
remote "cd '$REMOTE_DIR' && MUZLOTO_TAG='$TAG' docker compose up -d --remove-orphans && docker image prune -f >/dev/null"

echo "==> Health (from the proxy network)"
for i in $(seq 1 30); do
  if remote "docker exec calendar-nginx wget -qO- http://muzloto-app:8080/health" 2>/dev/null | grep -q ok; then
    echo "OK: muzloto-app is healthy"; exit 0
  fi
  sleep 3
done
echo "App did not become healthy; logs:" >&2
remote "cd '$REMOTE_DIR' && docker compose logs --tail 60 app" >&2
exit 1
