# Cross-platform acceptance

This matrix records real lifecycle evidence separately from static/unit coverage. A platform is not marked verified
until the listed service manager and executable-replacement path run on that platform.

| Platform | Evidence | Status |
|---|---|---|
| Linux x64 / systemd | Fresh enrollment, service reconciliation, Agent self-update, backend update/rollback and reconnect on UK AlmaLinux | Production verified |
| Linux Server / systemd | Bare-metal Server `0.4.0 -> 0.4.1`, same-PID exec, health and persistent DB on US Ubuntu | Production verified |
| Docker amd64 | GHCR pull, non-root startup, health, `/data` persistence | Verified |
| Docker arm64 | Native GitHub ARM build, GHCR pull and internal health endpoint under arm64 execution | Verified |
| macOS x64 | Non-root install plus local acceptance build enrollment/sync, metrics and bootout/bootstrap reload on Intel macOS | User LaunchAgent lifecycle verified locally; fixed release artifact and system LaunchDaemon pending |
| Alpine musl x64 | Official musl archive selection, size/SHA validation, safe extraction and `--self-test` in Alpine 3.22 | Staging verified; OpenRC pending |
| Windows x64 | Build, archive and helper unit coverage only | Real Windows Service update pending |
| OpenWrt musl | Installer/procd implementation and static review only | Real procd device pending |

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
