#!/bin/sh
# Installs wslp-ddns into this container. Run as root from the unpacked bundle:
#   ./install.sh               install or upgrade, enable the hourly timer, run once now
#   ./install.sh --uninstall   remove everything (the DNS record expires on its own)
#
# If a filled-in wslp-ddns.conf sits next to this script, it is installed as the config
# (replacing any existing one). Otherwise the example is installed only when no config exists.
set -eu

BIN=/usr/local/bin/wslp-ddns
CONF_DIR=/etc/wslp-ddns
CONF=$CONF_DIR/wslp-ddns.conf
UNIT_DIR=/etc/systemd/system
SVC_USER=wslp-ddns

cd "$(dirname "$0")"

if [ "$(id -u)" -ne 0 ]; then
    echo "install.sh: must be run as root" >&2
    exit 1
fi
if [ ! -d /run/systemd/system ]; then
    echo "install.sh: systemd is not running in this container" >&2
    exit 1
fi

if [ "${1:-}" = "--uninstall" ]; then
    systemctl disable --now wslp-ddns.timer 2>/dev/null || true
    rm -f "$UNIT_DIR/wslp-ddns.service" "$UNIT_DIR/wslp-ddns.timer" "$BIN"
    rm -rf "$CONF_DIR"
    systemctl daemon-reload
    if id "$SVC_USER" >/dev/null 2>&1; then
        userdel "$SVC_USER"
    fi
    echo "wslp-ddns removed. Its DNS record will be auto-deleted once its expiry TTL passes."
    exit 0
fi

install -m 0755 wslp-ddns "$BIN"

if ! id "$SVC_USER" >/dev/null 2>&1; then
    useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "$SVC_USER"
fi

install -d -m 0750 -o root -g "$SVC_USER" "$CONF_DIR"
if [ -f wslp-ddns.conf ]; then
    install -m 0640 -o root -g "$SVC_USER" wslp-ddns.conf "$CONF"
elif [ ! -f "$CONF" ]; then
    install -m 0640 -o root -g "$SVC_USER" wslp-ddns.conf.example "$CONF"
fi
# A config copied in beforehand may be root-only; the service user must be able to read it.
chown root:"$SVC_USER" "$CONF"
chmod 0640 "$CONF"

install -m 0644 wslp-ddns.service wslp-ddns.timer "$UNIT_DIR/"
systemctl daemon-reload
systemctl enable wslp-ddns.timer >/dev/null

if ! grep -Eq '^TECHNITIUM_URL=.+' "$CONF" || ! grep -Eq '^TECHNITIUM_TOKEN=.+' "$CONF"; then
    echo "Installed. Set TECHNITIUM_URL and TECHNITIUM_TOKEN in $CONF, then run:"
    echo "  systemctl start wslp-ddns.service wslp-ddns.timer"
    exit 0
fi

# Arming the timer after boot fires its OnBootSec trigger right away; starting the
# service as well waits for that run and reports whether it succeeded.
systemctl start wslp-ddns.timer
status=0
systemctl start wslp-ddns.service || status=$?
invocation=$(systemctl show -p InvocationID --value wslp-ddns.service)
journalctl "_SYSTEMD_INVOCATION_ID=$invocation" -o cat --no-pager
if [ "$status" -ne 0 ]; then
    echo "First run failed; fix $CONF and run: systemctl start wslp-ddns.service" >&2
    exit 1
fi
