# Active proxy maintenance protection — 2026-09-26

## Delivery status

Implemented, validated, and deployed on 2026-09-26 at 15:01 UTC from the
merge commit `71f56fc`. The
independent Sama VPN repair was tested in a guarded transaction and rolled back
after PPTP/LCP failed; it remains a provider/path blocker. No proxy-router
rules, WireGuard identities, or database/session mounts were changed by this
rollout.

Maintenance now checks persisted Starting/Running local sessions for the same
account and case-insensitive Codespace name immediately before each eligible
idle stop. A matching session prevents maintenance auto-stop. The skip event
includes account/session IDs, name, LastUsedAt, and the effective threshold.
Events are deduplicated by session ID in memory, pruned when sessions are no
longer active, and may be emitted again after a backend restart. Local traffic
accounting, profile idle timeout, and reconnect logic are unchanged. The Arvan
20-minute policy remains a deployment/profile setting, not a new global default.

Validation: 111 backend tests passed, including eight new theory cases covering
Starting/Running protection, repeated passes, ending/re-entering active state,
Stopped/Error/Stopping/no-session behavior, account/name isolation, and name case.
Frontend lint/build and `docker compose config --quiet` passed. Roslyn MCP could
not resolve its package references; native `dotnet test GhProxy.sln
-p:NuGetAudit=false` rebuilt successfully without warnings. `git diff --check`
passed.

## Deployment identity

- Local/deployed base: `71f56fce5600e22347265987d9d4308efe3391b2`.
- Live `origin/main` now points to the same merge commit.
- The deployed DLL matches the VPS offline publish DLL byte-for-byte and carries
  informational version `1.0.0+71f56fce5600e22347265987d9d4308efe3391b2`.
- DLL SHA-256: `5e773a91e2a44dc0adca1d903f8ae11e8105ba7e1b787e993489de0cdb47a472`.
- Backend image: `sha256:320ba92d8f90ec877f28d16190745609634ca1d1977854ea72d1e0ef8dfbea6c`.
- Frontend image: `sha256:f254fd4a4ec1689ed3b8eb274289ed48163a83b655c277d84cc9739b381209cb`.
- Both containers were force-recreated at 15:01 UTC, are healthy, and have
  restart count zero and OOM false.
- Rollback compose and image metadata are retained under
  `/opt/arvan-vps-gateway/backups/gh-proxy-deploy-20260926T145837Z/`.

The image has no revision label. The separate `/opt/arvan-vps-gateway/src/gh-proxy`
source tree differs from local HEAD; it is not authoritative for the running
image. The deployment identity is the commit, DLL hash, and image digest above.

## Independent VPN blocker

Read-only checks on 2026-09-26 established:

- Sama source is `37.32.24.52` on `eth1`; DHCP agrees on address, /22 netmask,
  and gateway `37.32.24.1`. Source rule/table 1077 pins it to eth1.
- eth3 is absent. The current source still exists; its absence is not this
  incident's cause. `ppp-tadvin` is a separate tunnel.
- Service journal repeatedly reports LCP timeout and pppd exit 16.
- A 55-second capture on all interfaces observed only outbound TCP/1723 packets
  to `81.91.159.130`, with no inbound response or GRE. A second eth1 capture
  confirmed SYN retransmission; ss showed SYN-SENT.
- Source-bound, five-second TCP probes: current eth1 source timed out; canonical
  eth0 source timed out; existing `37.32.26.173` on eth2 connected successfully.
  DHCP confirms the eth2 source and /22 subnet; its route uses policy table 1079.
- This narrows the failure to source/path-specific reachability. Remote filtering,
  allowlisting, and upstream routing are not distinguished by these observations.
  A successful TCP connection on eth2 does not yet prove GRE, authentication,
  PPP, company data plane, or Codespaces egress.
- The installed egress guard was run directly (without upload/refresh) and failed:
  GitHub targets route through eth0. validate-vps also reported company data-plane
  and database-route failures plus existing proxy-router policy drift.

The read-only command `scripts/sama-underlay-ip 37.32.26.173 --dry-run` stopped
with `canonical public SSH control path is not reachable`; it changed nothing.
The guarded apply workflow also installs the staged firewall helper and applies
nft policy. Firewall changes are explicitly outside the approved scope without
separate confirmation. The gateway checkout contains unrelated dirty helper
changes; do not upload them as an incidental part of this repair.

## Remaining rollout and rollback

1. Restore and verify an independent canonical public SSH/control path. Confirm
   eth2 is the intended Sama source. Prepare a reviewed helper bundle that does
   not include unrelated dirty gateway changes. Obtain explicit authorization
   for the required firewall regeneration before using the underlay workflow.
2. Repeat dry-run, then use the existing guarded underlay transaction. Retain the
   old address, backups, transaction ID, and timed rollback. A rollback can restore
   configuration but cannot make the already-broken old source healthy. Keep
   WireGuard identities/endpoints, Tadvin, and proxy-router rules unchanged.
3. Require bidirectional TCP and GRE, established PPP, all company data-plane
   probes, expected GitHub routes, and the egress guard. Run validate-vps; classify
   unchanged proxy-router drift separately, never silently rewrite those rules.
4. Completed: the exact image IDs above and the active compose were retained
   before recreating only the two gh-proxy services. Persistent mounts remain
   `/opt/arvan-vps-gateway/data/gh-proxy` and its SSH directory.
5. Completed: both containers are healthy, restart count is zero, OOM is false,
   and `/api/health` returns `200`.
6. Observe at least 24 hours from the recorded deployment timestamp. Require zero
   maintenance auto-stops for active sessions, zero traffic-time stop/interrupted/
   reconnect cycles, zero unexpected Xray exits, no OOM/restarts, healthy company
   egress, and real idle shutdown after 20 minutes without user traffic. A quiet
   window alone is not proof of traffic-time stability: correlate session state
   and traffic counters. Verify idle behavior in a controlled idle interval.
7. Application rollback: restore the saved compose, retag the retained exact
   images to its expected image names, then recreate only gh-proxy services with
   `--no-build --pull never`. Keep database/session/key mounts intact. Do not use
   `down -v`, image pruning, or an unbounded build during rollback. VPN rollback
   remains the independent underlay transaction, not application rollback.

No Codex automation has been scheduled. The 24-hour window is intentionally a
manual report using the existing operational logs and gateway monitor.

## Authorized continuation

The user confirmed on 2026-09-26 that this is the Sama company VPN and explicitly
authorized continuing the source change, necessary firewall reapplication, and
application rollout. Separate firewall approval is no longer pending.

The fresh dry-run still failed before mutation because independent canonical
public SSH is unavailable. WireGuard SSH works. An alternative connection to
`arvan2` failed host-key verification; its recorded key was not replaced and
host-key checking was not disabled. These are access blockers, not a request to
repeat the operational approval.

The deployed artifact was built from a local self-contained linux-x64 Release
publish at
`personal-vps-gateway/.cache/gh-proxy-offline-maintenance-20260926/backend-publish`,
with informational version `1.0.0+5d053c0.active-session-fix`.
The live compose remains `/opt/arvan-vps-gateway/gh-proxy.compose.yaml` with
SHA-256 `452ff0d191be696dd8ef0edbca77e3f9ff2611d04f5d1f9f8e444e444ee16143`;
The active compose hash is unchanged from the pre-deploy backup. Existing
persistent bind mounts remain `/opt/arvan-vps-gateway/data/gh-proxy` and its SSH
directory.

The maintenance-only deployment was authorized and completed over the working
WireGuard SSH path. `ssh arvan` is healthy through `10.77.0.1`; direct public
SSH to `94.101.185.175:22` still times out, so it is not an independent control
path for the Sama transaction.

Post-deploy validation on 2026-09-26 confirmed the new backend image digest,
healthy gh-proxy containers, zero restarts/OOM, API `200`, and frontend `200`.
The operational log contained 34 historical `github.maintenance.autostop`
entries before this deploy; no active-session skip entry has been observed yet.
The 24-hour acceptance window starts at the force-recreate time and remains
open.
