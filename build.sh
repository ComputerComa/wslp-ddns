#!/usr/bin/env bash
# Builds the deployable bundle dist/wslp-ddns-<rid>.tar.gz.
#   ./build.sh               linux-x64
#   ./build.sh linux-arm64   e.g. for Raspberry Pi hosts
#   VERSION=1.2.0 ./build.sh stamp a version into the binary (CI sets this from the release tag)
# Put a filled-in deploy/wslp-ddns.conf (gitignored) next to the example to ship it in the bundle.
set -euo pipefail
cd "$(dirname "$0")"

rid="${1:-linux-x64}"
name="wslp-ddns-$rid"
out="dist/$name"

rm -rf "$out" "dist/$name.tar.gz"
dotnet publish src/WslpDdns -c Release -r "$rid" -o "$out" --nologo ${VERSION:+"-p:Version=$VERSION"}
install -m 0755 deploy/install.sh "$out/"
cp deploy/wslp-ddns.service deploy/wslp-ddns.timer deploy/wslp-ddns.conf.example "$out/"
if [[ -f deploy/wslp-ddns.conf ]]; then
    install -m 0600 deploy/wslp-ddns.conf "$out/"
    echo "note: bundle includes deploy/wslp-ddns.conf (contains the API token)"
fi

tar -C dist -czf "dist/$name.tar.gz" "$name"
echo "built dist/$name.tar.gz"
