# Cross-platform acceptance

This matrix records real lifecycle evidence separately from static/unit coverage. A platform is not marked verified
until the listed service manager and executable-replacement path run on that platform.

| Platform | Evidence | Status |
|---|---|---|
| Linux x64 / systemd | Ubuntu 26.04 and AlmaLinux 10.2 glibc lifecycle; details below | Phase 18 production verified |
| Linux Server / systemd | Bare-metal Server `0.4.0 -> 0.4.1`, same-PID exec, health and persistent DB on US Ubuntu | Production verified |
| Docker amd64 | GHCR pull, non-root startup, health, `/data` persistence | Verified |
| Docker arm64 | Native GitHub ARM build, GHCR pull and internal health endpoint under arm64 execution | Verified |
| macOS x64 | Non-root install plus local acceptance build enrollment/sync, metrics and bootout/bootstrap reload on Intel macOS | User LaunchAgent lifecycle verified locally; fixed release artifact and system LaunchDaemon pending |
| Alpine musl x64 | Official musl archive selection, size/SHA validation, safe extraction and `--self-test` in Alpine 3.22 | Staging verified; OpenRC pending |
| Windows x64 | Build, archive and helper unit coverage only | Real Windows Service update pending |
| OpenWrt musl | Installer/procd implementation and static review only | Real procd device pending |

## Linux glibc systemd checklist

- Ubuntu 26.04 x64: fresh one-line enrollment, dedicated user, hardened systemd unit, 0600 credentials/bootstrap,
  0700 DataDir/service directories, and 0750 install directory verified.
- AlmaLinux 10.2 x64: same-node reinstall preserved credential hash and DataDir; final hardening unit loaded; Agent
  SIGKILL caused systemd restart after five seconds and sync recovered.
- Ubuntu: same-node reinstall and uninstall/reinstall preserved credential/state hashes, Backend cache and service
  directories. Default uninstall stopped all managed Backends and preserved DataDir.
- Ubuntu: four simultaneous managed services (two Mihomo and two sing-box) reached unique TCP/UDP listeners. A duplicate
  shared Backend artifact defect in Server desired-state generation was found and fixed; revision 14 then converged
  applied=desired with all Desired/Runtime versions equal.
- Killing one Backend restarted only that Backend. Agent SIGKILL and graceful `systemctl restart` recreated all managed
  Backends with one process per service and no stale listener.
- Invalid config and a read-only service-directory write failure both preserved the old PID, metadata hash, config and
  listener. A corrupt command cache was quarantined and rebuilt; four Backends restored. Corrupt identity was not
  replaced or re-enrolled.
- A 61-minute Agent-only Panel outage preserved the same Agent PID, all four Backend PIDs and all eight listeners.
  Retry was bounded to one warning and Panel recovery reconnected automatically with revision 20/20.
- No-op sync did not change config mtimes. Backend updates to Mihomo 1.19.31 and sing-box 1.14.1 converged for four
  instances. Agent journal search found no tested passwords or secret field names.
- Resource snapshots: idle Agent on AlmaLinux used about 28 MiB cgroup memory and 10 tasks. Ubuntu with two services
  showed Agent 17-33 MiB RSS, 11-13 threads and 15 FDs; with four services Agent was about 17 MiB RSS, 11 threads and
  23 FDs. A 20-sample, 9.5-minute four-service run measured 19.2-30.3 MiB RSS (first 27.6 MiB, last 30.3 MiB),
  11-13 threads and 13-20 FDs without linear growth. Backend memory is accounted separately inside the systemd cgroup.
- AlmaLinux four-service soak collected 181 one-minute samples over three hours: Agent RSS was 19-33 MiB, threads
  9-14 (first/last 12), FDs 18-20, and CPU time was about 2.5% of one core. RSS rose during warm-up, plateaued around
  32 MiB, and the final 30-minute average fell to about 30 MiB; no continuously growing collection/handle count was
  observed.
- Published Agent updates `0.4.3 -> 0.4.4 -> 0.4.5` passed with identity/revision retention, four-service restoration,
  and cleanup of `.previous`, `.staged`, archive and rollback marker files. An intentionally non-starting replacement
  exposed and validated the fixed-path systemd update guard: it restored `0.4.4` on the second failed start before
  start-limit, reported `restart_failed`, and restored all four Backends.
- AlmaLinux was rebooted after the final `0.4.5` update. systemd started the Agent at boot, all four Backends and eight
  listeners returned, Panel reconnected online, and revision remained applied=desired. The host's degraded systemd
  status was caused by pre-existing `mcelog` and `uk-edge-connlimit` failures, not HyPanel.
- `linux-x64` and `linux-arm64` NativeAOT publishes pass. No ARM64 glibc VPS is currently available for runtime evidence.
- All temporary acceptance Services, artifacts, firewall rules and samplers were removed. Production Server and Agent
  both run official `0.4.5`. ARM64 has NativeAOT publish evidence but still lacks a real ARM64 glibc runtime host.

## Windows x64 checklist

- Fresh `install.ps1` enrollment under Windows Service Control Manager.
- Confirm service account ACLs protect credentials and permit only the managed install/data directories.
- Create and reconcile one backend service.
- Trigger Agent update while `HyPanel.Agent.exe` is locked.
- Verify helper parent PID, fixed sibling paths, UpdateId and state file checks.
- Verify successful service restart, reconnect and `.previous` cleanup.
- Inject bad replacement/start failure and verify old executable rollback.
- Reboot, confirm service and backend recovery, then uninstall/reinstall where supported.

## macOS service checklist

- Verified without `sudo` on Intel macOS: the installer used the current user's `~/Library/LaunchAgents/com.hypanel.agent.plist` and `launchctl gui/$UID` domain.
- Verified enrollment, repeated sync, uptime/memory/disk metrics and explicit bootout/bootstrap reconnect under the user LaunchAgent with a local NativeAOT acceptance build containing the macOS metrics fix. CPU and network totals are reported as unavailable (`0`) because their macOS 26 NativeAOT enumeration paths abort under launchd.
- Verified a real Hysteria2 artifact download, configuration, process start, UDP listener and backend restoration after LaunchAgent reload.
- Verified the published `0.4.2` update download, digest validation, extraction, self-test, replacement failure, automatic rollback to the local fixed build and backend restoration. The rollback also cleans staging artifacts.
- The user LaunchAgent starts only after that user logs in; it does not replace unattended boot coverage from a system LaunchDaemon.
- Log out and back in, then confirm the user LaunchAgent starts and reconciles a backend.
- Publish a release containing the macOS metrics fix, then verify a successful Agent update and `.previous` cleanup under the user LaunchAgent.
- Run root installer on Intel or Apple Silicon macOS.
- Validate `/Library/LaunchDaemons/com.hypanel.agent.plist` ownership/mode and bootstrap environment permissions.
- Reboot and confirm `launchd` starts the Agent and reconciles a backend.
- Trigger Agent update, confirm exec/reconnect and backend survival.
- Inject verification failure and confirm rollback.
- Bootout/remove/reinstall and verify no stale credential exposure.

## Alpine OpenRC checklist

- Fresh install on real Alpine using OpenRC, not a container without init.
- Confirm musl RID, dedicated service user, file ownership and `rc-update` registration.
- Reboot, sync, create service, update Agent and verify backend recovery.
- Inject failed update and confirm rollback, then remove/reinstall.

## OpenWrt procd checklist

- Test x64 or arm64 OpenWrt with BusyBox `sh`, `tar`, `jsonfilter` and available SHA256 utility.
- Confirm exact `linux-musl-*` asset selection without Python.
- Validate `/etc/init.d/hypanel-agent`, `USE_PROCD`, respawn and boot enablement.
- Reboot, enroll, sync and run a supported backend within device resource limits.
- Trigger Agent update and verify procd process replacement/reconnect.
- Inject failure and verify rollback without corrupting overlay storage.
- Remove/reinstall and inspect residual files and permissions.
