#!/usr/bin/env bash
# Forced command of the CI deploy key (authorized_keys: restrict,command="…/ci-deploy.sh").
# The only thing that key can do: deploy the image built by GitHub Actions for one commit, with health check
# and automatic rollback. No shell, no port forwarding.
set -euo pipefail
cmd="${SSH_ORIGINAL_COMMAND:-}"
[[ "$cmd" =~ ^deploy\ ([0-9a-f]{40})$ ]] || { echo "usage: deploy <40-hex commit sha>" >&2; exit 2; }
sha="${BASH_REMATCH[1]}"
image=ghcr.io/krustrip/muzloto-web
dir=/home/BocA/muzloto
cd "$dir"
exec 9>.deploy.lock
flock -n 9 || { echo "another deployment is running" >&2; exit 3; }

# Records the running release in .env so a manual 'docker compose up' keeps the same version.
set_release() {
  local tmp; tmp="$(mktemp "$dir/.env.XXXX")"
  grep -vE '^MUZLOTO_(IMAGE|TAG)=' .env > "$tmp" || true
  printf 'MUZLOTO_IMAGE=%s\nMUZLOTO_TAG=%s\n' "$1" "$2" >> "$tmp"
  chmod 600 "$tmp"; mv "$tmp" .env
}
healthy() {
  for _ in $(seq 1 30); do
    docker exec calendar-nginx wget -qO- http://muzloto-app:8080/health 2>/dev/null | grep -q ok && return 0
    sleep 3
  done
  return 1
}

prev_image="$(grep -E '^MUZLOTO_IMAGE=' .env | cut -d= -f2- || true)"
prev_tag="$(grep -E '^MUZLOTO_TAG=' .env | cut -d= -f2- || true)"

echo "==> Pull $image:$sha"
docker pull -q "$image:$sha" || {
  echo "Cannot pull $image:$sha. A new GHCR package may be private: make it public (no secrets in the image) at" >&2
  echo "https://github.com/users/kruStrip/packages/container/muzloto-web/settings → Change visibility → Public" >&2
  exit 4; }
echo "==> Compose file of this commit"
curl -fsSL --retry 3 "https://raw.githubusercontent.com/kruStrip/ASP.NET-AJAX/$sha/4_%D0%BA%D1%83%D1%80%D1%81/%D0%A0%D0%B0%D0%B7%D1%80%D0%B0%D0%B1%D0%BE%D1%82%D0%BA%D0%B0_%D0%B2%D0%B5%D0%B1_%D0%BF%D1%80%D0%B8%D0%BB%D0%BE%D0%B6%D0%B5%D0%BD%D0%B8%D0%B9_ASP.NET_AJAX/%D0%98%D1%82%D0%BE%D0%B3%D0%BE%D0%B2%D1%8B%D0%B9_%D0%BF%D1%80%D0%BE%D0%B5%D0%BA%D1%82/%D0%BF%D1%80%D0%BE%D0%B5%D0%BA%D1%82/deploy/docker-compose.prod.yml" -o docker-compose.yml.new
cp -p docker-compose.yml docker-compose.yml.prev
mv docker-compose.yml.new docker-compose.yml

set_release "$image" "$sha"
docker compose up -d --remove-orphans
if healthy; then
  echo "OK: $sha is live (previous: ${prev_image:-?}:${prev_tag:-?})"
  docker image prune -f >/dev/null
  exit 0
fi

echo "FAILED: $sha is not healthy, rolling back" >&2
docker compose logs --tail 40 app >&2 || true
mv docker-compose.yml.prev docker-compose.yml
set_release "${prev_image:-muzloto-web}" "${prev_tag:-latest}"
docker compose up -d --remove-orphans
healthy && echo "Rolled back to ${prev_tag}" >&2 || echo "Rollback is not healthy either — check manually" >&2
exit 1
