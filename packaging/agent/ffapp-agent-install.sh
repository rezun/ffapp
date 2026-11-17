#!/usr/bin/env bash
# Installs or updates the FFApp server agent as a systemd service.
#
#   curl -fsSL https://github.com/rezun/ffapp/releases/latest/download/ffapp-agent-install.sh | sudo bash
#   curl -fsSL https://github.com/rezun/ffapp/releases/download/v1.15.0/ffapp-agent-install.sh | sudo bash -s -- --version 1.15.0
#
# Running it again is safe: it keeps the configuration and clients, installs the requested
# version and regenerates the systemd unit (for example after changing OutputRoots).
set -euo pipefail

REPO="rezun/ffapp"
SERVICE="ffapp-agent"
SERVICE_USER="ffapp-agent"
ROOT="/var/lib/ffapp-agent"
CONFIG_DIR="/etc/ffapp-agent"
CONFIG="$CONFIG_DIR/appsettings.json"
CLIENTS="$CONFIG_DIR/clients.json"
UNIT="/etc/systemd/system/$SERVICE.service"

VERSION=""
CLIENT="default"
WITH_FFMPEG=false
FFMPEG_URL=""
BASE_URL="${FFAPP_AGENT_RELEASES:-https://github.com/$REPO/releases}"

usage() {
    cat <<USAGE
Usage: ffapp-agent-install.sh [options]

Options:
  --version <version>   Install this release (default: the latest release)
  --client <name>       Name of the first client created on a new installation (default: default)
  --with-ffmpeg         Download a static FFmpeg 9 build into $ROOT/tools
  --ffmpeg-url <url>    Use this .tar.xz FFmpeg build with --with-ffmpeg
  -h, --help            Show this help
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="${2:?--version needs a value}"; VERSION="${VERSION#v}"; shift 2 ;;
        --client) CLIENT="${2:?--client needs a value}"; shift 2 ;;
        --with-ffmpeg) WITH_FFMPEG=true; shift ;;
        --ffmpeg-url) FFMPEG_URL="${2:?--ffmpeg-url needs a value}"; WITH_FFMPEG=true; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

fail() { echo "error: $*" >&2; exit 1; }
step() { echo "==> $*"; }

[[ $EUID -eq 0 ]] || fail "run this script as root, for example with sudo"
command -v systemctl >/dev/null || fail "systemd is required"
command -v curl >/dev/null || fail "curl is required"
command -v sha256sum >/dev/null || fail "sha256sum is required"

case "$(uname -m)" in
    x86_64|amd64) RID="linux-x64"; FFMPEG_ARCH="linux64" ;;
    aarch64|arm64) RID="linux-arm64"; FFMPEG_ARCH="linuxarm64" ;;
    *) fail "unsupported architecture $(uname -m); the agent runs on x86_64 and arm64" ;;
esac

if [[ -z "$VERSION" ]]; then
    latest="$(curl -fsSLI -o /dev/null -w '%{url_effective}' "$BASE_URL/latest")"
    VERSION="${latest##*/v}"
    [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+ ]] || fail "could not determine the latest release from $latest"
fi
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.+][0-9A-Za-z.-]+)?$ ]] || fail "invalid version: $VERSION"

ASSET="ffapp-agent-$VERSION-$RID.tar.gz"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

step "Downloading FFApp agent $VERSION ($RID)"
curl -fsSL -o "$WORK/$ASSET" "$BASE_URL/download/v$VERSION/$ASSET"
curl -fsSL -o "$WORK/SHA256SUMS" "$BASE_URL/download/v$VERSION/SHA256SUMS"
(cd "$WORK" && grep -E "[[:space:]]\*?$ASSET\$" SHA256SUMS | sha256sum -c --quiet -) \
    || fail "checksum of $ASSET does not match SHA256SUMS"

step "Creating the service user $SERVICE_USER"
if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --user-group --home-dir "$ROOT" --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

step "Installing to $ROOT/versions/$VERSION"
install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 0750 "$ROOT" "$ROOT/versions"
rm -rf "$ROOT/versions/$VERSION.staging"
install -d -m 0755 "$ROOT/versions/$VERSION.staging"
tar -xzf "$WORK/$ASSET" -C "$ROOT/versions/$VERSION.staging"
chmod 0755 "$ROOT/versions/$VERSION.staging/ffapp-agent"
rm -rf "$ROOT/versions/$VERSION"
mv "$ROOT/versions/$VERSION.staging" "$ROOT/versions/$VERSION"
if [[ -L "$ROOT/current" ]] && [[ "$(readlink "$ROOT/current")" != *"/$VERSION" ]]; then
    ln -sfn "$(readlink "$ROOT/current")" "$ROOT/previous"
fi
ln -sfn "$ROOT/versions/$VERSION" "$ROOT/current"
rm -f "$ROOT/update.json"
AGENT="$ROOT/current/ffapp-agent"
ln -sfn "$AGENT" /usr/local/bin/ffapp-agent

if $WITH_FFMPEG; then
    step "Downloading a static FFmpeg 9 build"
    url="${FFMPEG_URL:-https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-$FFMPEG_ARCH-gpl-9.0.tar.xz}"
    curl -fsSL -o "$WORK/ffmpeg.tar.xz" "$url"
    install -d -m 0755 "$ROOT/tools"
    tar -xJf "$WORK/ffmpeg.tar.xz" -C "$WORK"
    for tool in ffmpeg ffprobe; do
        found="$(find "$WORK" -type f -name "$tool" -perm -u+x | head -n 1)"
        [[ -n "$found" ]] || fail "the FFmpeg archive does not contain $tool"
        install -m 0755 "$found" "$ROOT/tools/$tool"
    done
fi
chown -R "$SERVICE_USER:$SERVICE_USER" "$ROOT"

step "Configuring $CONFIG_DIR"
install -d -o root -g "$SERVICE_USER" -m 0750 "$CONFIG_DIR"
if [[ ! -f "$CONFIG" ]]; then
    cat > "$CONFIG" <<JSON
{
  "Agent": {
    "Urls": "http://0.0.0.0:8737",
    "WorkDirectory": "$ROOT",
    "ClientsFile": "$CLIENTS",
    "QuotaGiB": 500,
    "MaxConcurrentJobs": 1,
    "RetentionDays": 14,
    "OutputRoots": {},
    "Tools": { "Ffmpeg": "", "Ffprobe": "", "Mkvpropedit": "" }
  }
}
JSON
    echo "    Wrote $CONFIG. Add folders for results kept on the server under OutputRoots, then run this script again."
fi
chown root:"$SERVICE_USER" "$CONFIG"
chmod 0640 "$CONFIG"
export FFAPP_AGENT_CONFIG="$CONFIG"

TOKEN_OUTPUT=""
if [[ ! -f "$CLIENTS" ]]; then
    step "Creating the first client \"$CLIENT\""
    TOKEN_OUTPUT="$("$AGENT" client add "$CLIENT")"
fi
if [[ -f "$CLIENTS" ]]; then
    chown root:"$SERVICE_USER" "$CLIENTS"
    chmod 0640 "$CLIENTS"
fi

step "Installing the systemd unit"
read_write_paths="$ROOT"
while IFS= read -r root_path; do
    [[ -z "$root_path" ]] && continue
    if [[ ! -d "$root_path" ]]; then
        echo "    warning: output folder $root_path does not exist; create it and give $SERVICE_USER write access" >&2
        continue
    fi
    read_write_paths+=" $root_path"
done < <("$AGENT" output-roots)

cat > "$UNIT" <<UNIT
[Unit]
Description=FFApp server agent
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=$SERVICE_USER
Group=$SERVICE_USER
Environment=FFAPP_AGENT_CONFIG=$CONFIG
ExecStartPre=$ROOT/current/ffapp-agent startup-check
ExecStart=$ROOT/current/ffapp-agent serve
Restart=always
RestartSec=5
Nice=10
ProtectSystem=strict
ProtectHome=read-only
PrivateTmp=yes
ReadWritePaths=$read_write_paths
NoNewPrivileges=yes

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
systemctl restart "$SERVICE"

step "Checking FFmpeg and the configuration"
if ! command -v mkvpropedit >/dev/null && [[ ! -x "$ROOT/tools/mkvpropedit" ]]; then
    echo "    mkvpropedit is not installed. For Matroska statistics install it, for example: apt install mkvtoolnix"
fi
"$AGENT" check || echo "    The agent needs FFmpeg 9 or newer. Run this script again with --with-ffmpeg to install a static build."

echo
echo "FFApp agent $VERSION is running on port 8737."
if [[ -n "$TOKEN_OUTPUT" ]]; then
    echo "$TOKEN_OUTPUT"
else
    echo "Add another client with: sudo ffapp-agent client add <name>"
fi
