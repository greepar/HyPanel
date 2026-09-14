# HyPanel

HyPanel is a lightweight centralized proxy backend orchestration panel built around .NET 10 NativeAOT.

One Server manages many cross-platform Agents. Each Agent can run and manage multiple independent service instances backed by Hysteria2, Xray, Mihomo, sing-box, and future adapters.

## Core idea

```text
Server owns Desired State
          |
          | HTTPS sync
          v
Agent reconciles Actual State
          |
          +-- ServiceInstance: Hysteria2
          +-- ServiceInstance: Xray / VLESS REALITY Vision
          +-- ServiceInstance: Mihomo
          +-- ServiceInstance: sing-box
```

The project intentionally avoids a heavy distributed-control stack. Early versions use one Server process, SQLite, HTTPS Agent polling, and backend child processes.

See:

- `AGENTS.md` — coding-agent rules and model routing.
- `docs/CONTEXT.md` — compact product context.
- `docs/ARCHITECTURE.md` — architecture.
- `docs/CONTRACTS.md` — public contract invariants.
- `docs/ROADMAP.md` — phased delivery plan.
- `TASKS.md` — current work queue.
