#!/usr/bin/env bash
# One-shot setup for the Harmony Resolver production stack on an Oracle Cloud
# Ampere A1 (arm64) Ubuntu VPS. Run once via: sudo ./bootstrap-oracle-vps.sh
set -euo pipefail

REPO_URL="https://github.com/bozmund/Harmony-Resolver.git"
INSTALL_DIR="/opt/harmony-resolver"
ENV_DIR="/etc/harmony-resolver"
ENV_FILE="$ENV_DIR/harmony-resolver.env"

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this script as root: sudo $0" >&2
  exit 1
fi

echo "==> Installing Docker Engine + Compose plugin"
if ! command -v docker >/dev/null 2>&1; then
  apt-get update
  apt-get install -y ca-certificates curl gnupg
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  # shellcheck disable=SC1091
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  systemctl enable --now docker
else
  echo "Docker already installed, skipping."
fi

echo "==> Opening 80/443 on the host firewall"
# Stock OCI Ubuntu images ship iptables rules that DROP everything but SSH by
# default, in addition to the cloud-level Security List/NSG. Both layers need
# 80/443 open, or Let's Encrypt validation and HTTPS traffic will silently fail.
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q "Status: active"; then
  ufw allow 80/tcp
  ufw allow 443/tcp
else
  iptables -C INPUT -p tcp --dport 80 -j ACCEPT 2>/dev/null || iptables -I INPUT -p tcp --dport 80 -j ACCEPT
  iptables -C INPUT -p tcp --dport 443 -j ACCEPT 2>/dev/null || iptables -I INPUT -p tcp --dport 443 -j ACCEPT
  if command -v netfilter-persistent >/dev/null 2>&1; then
    netfilter-persistent save
  elif [ -d /etc/iptables ]; then
    iptables-save > /etc/iptables/rules.v4
  fi
fi
echo "    Reminder: also open TCP 80 and 443 in the OCI Security List / Network Security Group for this VPS's subnet — that cannot be done from inside the instance."

echo "==> Fetching Harmony Resolver into $INSTALL_DIR"
if [ -d "$INSTALL_DIR/.git" ]; then
  git -C "$INSTALL_DIR" pull --ff-only
else
  git clone "$REPO_URL" "$INSTALL_DIR"
fi

echo "==> Preparing $ENV_FILE"
mkdir -p "$ENV_DIR"
if [ ! -f "$ENV_FILE" ]; then
  {
    echo "POSTGRES_PASSWORD=$(openssl rand -hex 32)"
    echo "MINIO_ROOT_USER=harmony"
    echo "MINIO_ROOT_PASSWORD=$(openssl rand -hex 32)"
    echo "IDENTITY_HMAC_KEY=$(openssl rand -hex 32)"
    echo "AUDIT_HMAC_KEY=$(openssl rand -hex 32)"
  } > "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  chown root:root "$ENV_FILE"
  echo "    Generated new random secrets."
else
  echo "    Already exists, leaving untouched."
fi

echo "==> Installing systemd unit"
cp "$INSTALL_DIR/deploy/systemd/harmony-resolver.service" /etc/systemd/system/harmony-resolver.service
systemctl daemon-reload
systemctl enable harmony-resolver.service

echo "==> Starting the stack"
if ! systemctl start harmony-resolver.service; then
  echo
  echo "Start failed — most likely 'docker compose pull' couldn't reach ghcr.io/bozmund images."
  echo "If the GitHub Container Registry packages are private, run first:"
  echo "  docker login ghcr.io -u <github-username>"
  echo "(use a PAT with 'read:packages' scope as the password), then re-run:"
  echo "  systemctl start harmony-resolver.service"
  exit 1
fi

cat <<EOF

==> Done. Next steps:
  1. Confirm harmony-resolver.duckdns.org resolves to this VPS's public IP:
       dig +short harmony-resolver.duckdns.org
  2. Watch the stack come up (migrate must finish before api-1/api-2 start):
       docker compose -f $INSTALL_DIR/compose.prod.yaml ps
       docker compose -f $INSTALL_DIR/compose.prod.yaml logs -f migrate
  3. Once DNS resolves, Caddy requests a Let's Encrypt cert automatically on
     first request. Check:
       curl -sf https://harmony-resolver.duckdns.org/health/live
  4. Grafana is private by design — reach it via:
       ssh -L 3000:localhost:3000 <user>@<this-vps-ip>
     then open http://localhost:3000
EOF
