#!/usr/bin/env bash
# One-time setup enabling GitHub Actions to trigger redeploys after a new
# image publishes. Creates a dedicated, minimally-privileged 'deploy' user
# that can only restart the harmony-resolver service, nothing else.
# Run once, after deploy/bootstrap-oracle-vps.sh: sudo ./setup-ci-deploy-user.sh
set -euo pipefail

INSTALL_DIR="/opt/harmony-resolver"
DEPLOY_USER="deploy"
SUDOERS_FILE="/etc/sudoers.d/harmony-resolver-deploy"
SSH_DIR="/home/$DEPLOY_USER/.ssh"
KEY_PATH="$SSH_DIR/ci_deploy_key"

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this script as root: sudo $0" >&2
  exit 1
fi

if [ ! -d "$INSTALL_DIR/.git" ]; then
  echo "$INSTALL_DIR is not a git checkout — run deploy/bootstrap-oracle-vps.sh first." >&2
  exit 1
fi

echo "==> Creating '$DEPLOY_USER' user"
if ! id "$DEPLOY_USER" >/dev/null 2>&1; then
  useradd --create-home --shell /bin/bash "$DEPLOY_USER"
else
  echo "    Already exists, skipping."
fi

echo "==> Handing ownership of $INSTALL_DIR to $DEPLOY_USER (so 'git pull' needs no sudo)"
chown -R "$DEPLOY_USER:$DEPLOY_USER" "$INSTALL_DIR"

echo "==> Restricting sudo to exactly one command: restarting the service"
echo "$DEPLOY_USER ALL=(root) NOPASSWD: /usr/bin/systemctl restart harmony-resolver.service" > "$SUDOERS_FILE"
chmod 440 "$SUDOERS_FILE"
visudo -c -f "$SUDOERS_FILE"

echo "==> Generating a dedicated SSH keypair for CI"
mkdir -p "$SSH_DIR"
if [ ! -f "$KEY_PATH" ]; then
  ssh-keygen -t ed25519 -N "" -f "$KEY_PATH" -C "harmony-resolver-ci-deploy"
  touch "$SSH_DIR/authorized_keys"
  cat "$KEY_PATH.pub" >> "$SSH_DIR/authorized_keys"
  chmod 600 "$SSH_DIR/authorized_keys"
else
  echo "    Key already exists, leaving untouched."
fi
chown -R "$DEPLOY_USER:$DEPLOY_USER" "$SSH_DIR"
chmod 700 "$SSH_DIR"

PUBLIC_IP="$(curl -fs ifconfig.me || echo '<this VPS public IP>')"

cat <<EOF

==> Done. Add two repository secrets on GitHub
    (repo -> Settings -> Secrets and variables -> Actions -> New repository secret):

    VPS_HOST     = $PUBLIC_IP
    VPS_SSH_KEY  = the full contents of $KEY_PATH, run:
                     cat $KEY_PATH

The VPS only ever needs the *public* half (already installed in
$SSH_DIR/authorized_keys). Once both secrets are saved on GitHub,
delete the private key from this box:

    rm $KEY_PATH

From then on, every successful run of the "publish images" workflow on
main will trigger .github/workflows/deploy.yml, which SSHes in as
'$DEPLOY_USER' and runs only 'git pull' + 'sudo systemctl restart
harmony-resolver.service' — nothing else that user/key can do.
EOF
