# wslp-ddns

A small C# client that keeps `<hostname>.internal.wslp.cc` pointing at an LXC container's IPv4 address in
[Technitium DNS Server](https://technitium.com/dns/). It runs from a systemd timer once at boot (after the
network is online) and then hourly.

Every record is written with a Technitium **expiry TTL of 6 hours**. Each successful run resets that clock, so
a record lives as long as its container keeps checking in. A container that is deleted, stopped, or cut off
has its record removed by Technitium about 6 hours after its last successful run. Nothing needs cleaning up by hand.

## How a run works

1. Pick the IPv4 address to publish: the source address the kernel uses to reach the Technitium server
   (falls back to the default route; `DDNS_INTERFACE` overrides).
2. Read the current A records for `<hostname>.<zone>`.
3. Write the A record with `overwrite=true`, `ttl=300`, `expiryTtl=21600`. This one call creates, updates, or
   refreshes the record. Technitium stamps a new last-modified time on every write, which restarts the expiry countdown.

The log says which of the three happened:

```
created app01.internal.wslp.cc A 10.0.20.15 (ttl 5m, auto-delete 6h after last refresh)
refreshed app01.internal.wslp.cc A 10.0.20.15 (ttl 5m, auto-delete 6h after last refresh)
updated app01.internal.wslp.cc A 10.0.20.15 -> 10.0.20.31 (ttl 5m, auto-delete 6h after last refresh)
```

Records it writes carry the comment `managed by wslp-ddns`. If the name already has an A record **without** that
comment (for example one you created by hand), the client leaves it alone and fails with an error. Set
`DDNS_TAKEOVER=true` to let it replace such records. Once replaced, they carry the comment and the flag is no longer needed.

## Technitium setup (once)

Requires Technitium DNS Server v15 or later (it uses bearer-token auth). Tested against 15.6.2.

1. **Zone**: create a Primary zone `internal.wslp.cc` if it doesn't exist.
2. **User**: *Administration → Users → Add User*, e.g. `ddns`. It needs no groups.
3. **Permissions**: open the zone's *Permissions* and give user `ddns` **View** and **Modify** on `internal.wslp.cc`.
   That's all the client needs. Requests for any other zone get "Access was denied".
4. **Token**: as admin, *Administration → Sessions → Create Token* for user `ddns`, or:

   ```bash
   curl -s http://<technitium>:5380/api/user/createToken -d user=ddns -d 'pass=<password>' -d tokenName=wslp-ddns
   ```

## Build

Needs the .NET 10 SDK on the build machine. Containers need no .NET runtime: the output is one self-contained file.

```bash
./build.sh                 # dist/wslp-ddns-linux-x64.tar.gz
./build.sh linux-arm64     # for arm64 hosts
VERSION=1.2.0 ./build.sh   # stamp a version (otherwise `wslp-ddns --version` says 0.0.0-dev)
```

For a mass rollout, put your settings in `deploy/wslp-ddns.conf` (copy the `.example`; the file is gitignored
because it holds the token) before building. The bundle then ships with it and every install is zero-touch.

## Releases

Versions follow [SemVer](https://semver.org) and come from git tags. To cut a release:

```bash
git tag -a v1.2.0 -m v1.2.0 && git push origin v1.2.0
```

The [build workflow](.github/workflows/build.yml) runs the tests, builds the `linux-x64` and `linux-arm64` bundles
with the tag's version stamped in, and publishes them to a GitHub release along with a `SHA256SUMS` file.
A tag with a pre-release suffix (`v1.2.0-rc.1`) becomes a pre-release. A tag that isn't
`vMAJOR.MINOR.PATCH[-prerelease]` fails the build. Pushes to `main` and pull requests run the same tests and build
without releasing.

Asset names carry no version, so `releases/latest/download/wslp-ddns-linux-x64.tar.gz` always points at the newest release.

## Install in a container

From the latest release, as root:

```bash
curl -fsSL https://github.com/ComputerComa/wslp-ddns/releases/latest/download/wslp-ddns-linux-x64.tar.gz | tar xz
wslp-ddns-linux-x64/install.sh
```

Or from your own build (which can carry your config): copy the tarball in, then as root:

```bash
tar xzf wslp-ddns-linux-x64.tar.gz && wslp-ddns-linux-x64/install.sh
```

The installer puts the binary in `/usr/local/bin`, creates a `wslp-ddns` system user, and enables `wslp-ddns.timer`.
The config at `/etc/wslp-ddns/wslp-ddns.conf` comes from the bundle if it carries one. Otherwise an existing file
is kept (so you can copy yours in first), or the example is written for you to fill in. Either way the installer sets
the file to mode 0640, group `wslp-ddns`. If the config is filled in, it runs once and prints the result. Re-running
it upgrades in place, and `install.sh --uninstall` removes everything (the record then expires on its own).

On Proxmox you can roll it out to several containers from the host, with your filled-in config in `wslp-ddns.conf`:

```bash
for ct in 101 102 103; do
  pct exec "$ct" -- mkdir -p /etc/wslp-ddns
  pct push "$ct" wslp-ddns.conf /etc/wslp-ddns/wslp-ddns.conf
  pct exec "$ct" -- sh -c 'cd /root && curl -fsSL https://github.com/ComputerComa/wslp-ddns/releases/latest/download/wslp-ddns-linux-x64.tar.gz | tar xz && wslp-ddns-linux-x64/install.sh'
done
```

Container requirements: systemd, glibc 2.27+ (Debian 10+, Ubuntu 18.04+). Alpine (musl/OpenRC) is not supported.

## Configuration

`/etc/wslp-ddns/wslp-ddns.conf` holds `KEY=VALUE` lines, with comments on their own lines. Environment variables
of the same name override the file, which is handy for one-off runs.

| Key | Default | Meaning |
| --- | --- | --- |
| `TECHNITIUM_URL` | (required) | Web console URL, e.g. `http://10.0.0.53:5380`. A path prefix behind a reverse proxy is kept. |
| `TECHNITIUM_TOKEN` | (required) | API token from the setup above. |
| `DDNS_ZONE` | `internal.wslp.cc` | Zone to write into. |
| `DDNS_HOSTNAME` | machine hostname | Record name. The default takes the first label, lowercased, with invalid characters replaced by `-`. |
| `DDNS_TTL` | `300` | DNS TTL: how long resolvers may cache the answer. Kept short so IP changes show up quickly. |
| `DDNS_EXPIRY_TTL` | `21600` | Seconds after the last refresh before Technitium deletes the record. `0` disables auto-delete. |
| `DDNS_INTERFACE` | (none) | Publish this interface's IPv4 address instead of the route-based one (for multi-homed containers). |
| `DDNS_TAKEOVER` | `false` | Replace existing A records that wslp-ddns did not create. |

For HTTPS with a private CA, add the CA to the container's trust store (`update-ca-certificates`).

## Operating it

```bash
systemctl list-timers wslp-ddns.timer      # next/last run
systemctl start wslp-ddns.service          # run now
journalctl -u wslp-ddns                    # history
journalctl -u wslp-ddns -p err             # failures only
wslp-ddns --dry-run                        # (as root) show what a run would do, write nothing
```

The client retries network failures 3 times over about 50 seconds, which covers boot-time races. Log lines carry
journald priorities: retries are `warning`, final failures are `err`. A failed run exits non-zero, so the unit
shows up in `systemctl --failed` until the next successful run. Those two signals are what an alerting bridge can watch.

Exit codes: `0` success, `1` failed run (unreachable server, rejected token, foreign record), `2` configuration error.

## Notes

- **Duplicate hostnames**: two containers with the same hostname overwrite each other every hour (last writer
  wins). Give them unique hostnames or set `DDNS_HOSTNAME`.
- **IPv4 only**: the client manages A records, not AAAA.
- **Sandboxing**: the unit runs as an unprivileged user with `NoNewPrivileges`. Stronger sandboxing
  (`ProtectSystem=` etc.) needs mount namespaces, which unprivileged LXC only allows with nesting enabled.
  Those lines are in `wslp-ddns.service`, commented out.

## Development

```bash
dotnet test
```

Layout: `src/WslpDdns` (client), `tests/WslpDdns.Tests` (xUnit), `deploy/` (systemd units, installer, example config).
