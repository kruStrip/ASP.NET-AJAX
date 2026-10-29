#!/usr/bin/env bash
# One-time CD setup, run by the repository owner:  bash scripts/setup-cd.sh
#  1. creates a dedicated deploy key (the private part goes only to GitHub Secrets and is shredded locally);
#  2. installs deploy/ci-deploy.sh on the server and authorizes the key for that single command
#     (authorized_keys: restrict,command=…) — no shell, no forwarding;
#  3. creates the GitHub environment "production" (deployments from main only) with the secrets.
# Safe to re-run: it replaces the previous deploy key.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO="${MUZLOTO_REPO:-kruStrip/ASP.NET-AJAX}"
HOST="${MUZLOTO_SSH_HOST:-boca}"          # your admin SSH alias for the server
SERVER_IP="${MUZLOTO_SERVER_IP:-185.244.51.134}"
DIR=/home/BocA/muzloto
SSH_OPTS=(-o BatchMode=yes -o ClearAllForwardings=yes -o ConnectTimeout=40 -o ServerAliveInterval=15)

tmp="$(mktemp -d)"
trap '[[ -f "$tmp/ci_deploy" ]] && shred -u "$tmp/ci_deploy"; rm -rf "$tmp"' EXIT

remote_file() {   # retries dropped connections; re-opens the input file on every attempt
  local file=$1 attempt rc; shift
  for attempt in 1 2 3 4 5 6; do
    ssh "${SSH_OPTS[@]}" "$HOST" "$@" < "$file" && return 0 || rc=$?
    [[ $rc -eq 255 ]] || return $rc
    echo "   (ssh dropped, retry $attempt)" >&2; sleep 5
  done
  return $rc
}

echo "==> Deploy key"
ssh-keygen -q -t ed25519 -N "" -C muzloto-ci-deploy -f "$tmp/ci_deploy"
ssh-keygen -F "$SERVER_IP" -f ~/.ssh/known_hosts | grep -v '^#' > "$tmp/known_hosts" || true
[[ -s "$tmp/known_hosts" ]] || { echo "Host key of $SERVER_IP is not in ~/.ssh/known_hosts — connect once with 'ssh $HOST' first." >&2; exit 1; }
printf 'restrict,command="%s/ci-deploy.sh" %s\n' "$DIR" "$(cat "$tmp/ci_deploy.pub")" > "$tmp/entry"

echo "==> Server: deploy script"
remote_file "$ROOT/deploy/ci-deploy.sh" "cat > $DIR/ci-deploy.sh && chmod 700 $DIR/ci-deploy.sh"

echo "==> Server: authorize the key for that command only (backup: ~/.ssh/authorized_keys.bak-muzloto)"
remote_file "$tmp/entry" 'set -e; umask 077; touch ~/.ssh/authorized_keys
cp -p ~/.ssh/authorized_keys ~/.ssh/authorized_keys.bak-muzloto
{ grep -v "muzloto-ci-deploy" ~/.ssh/authorized_keys || true; cat; } > ~/.ssh/authorized_keys.new
mv ~/.ssh/authorized_keys.new ~/.ssh/authorized_keys'
remote_file /dev/null 'echo "   admin access still works"'

echo "==> Check: the deploy key can run nothing but the deploy command"
out="$(ssh -i "$tmp/ci_deploy" -o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=40 "root@$SERVER_IP" 'id' 2>&1 || true)"
grep -q "usage: deploy" <<<"$out" && echo "   OK: '$(echo "$out" | head -1)'" || { echo "Unexpected answer: $out" >&2; exit 1; }

echo "==> GitHub: environment 'production' (main only) and secrets"
gh api -X PUT "repos/$REPO/environments/production" --input - >/dev/null \
  <<<'{"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true}}'
gh api "repos/$REPO/environments/production/deployment-branch-policies" --jq '.branch_policies[].name' | grep -qx main \
  || gh api -X POST "repos/$REPO/environments/production/deployment-branch-policies" -f name=main -f type=branch >/dev/null
gh secret set DEPLOY_SSH_KEY --env production -R "$REPO" < "$tmp/ci_deploy"
gh secret set DEPLOY_KNOWN_HOSTS --env production -R "$REPO" < "$tmp/known_hosts"
gh secret list --env production -R "$REPO"
echo "Done. Push to main: tests run, then the image is built and deployed automatically."
