# HyPanel Project Instructions

## Mission

HyPanel is a lightweight centralized proxy-service management platform built with .NET 10 NativeAOT.

A single `HyPanel.Server` manages many cross-platform `HyPanel.Agent` instances. Each Agent may manage multiple independent proxy backend instances such as Hysteria2, Xray, Mihomo, and sing-box.

The five core concepts are:

- `Node`
- `ServiceInstance`
- `BackendProvider`
- `DesiredState`
- `Agent`

Keep the architecture simple. HyPanel is not tied to one proxy core.

## Mandatory technical constraints

- C# / .NET 10.
- `HyPanel.Server`: ASP.NET Core Minimal API + NativeAOT.
- `HyPanel.Agent`: NativeAOT, self-contained, no .NET runtime dependency.
- JSON must be NativeAOT-friendly; prefer `System.Text.Json` source generation.
- SQLite for the first production-capable version.
- Server-Agent control plane uses outbound Agent HTTPS sync/polling.
- Nodes do not need to expose an Agent management port.
- No persistent SSH control channel.
- No runtime DLL plugin system.
- No Redis, MQ, Kubernetes, microservices, CQRS framework, event sourcing, GraphQL, or mandatory Docker.
- Frontend should be lightweight TypeScript + Vite; prefer Preact unless the repository already establishes another lightweight choice.
- UI: clean black/white/gray palette, rounded corners, Light/Dark/System themes.

## Architecture rules

- One `Node` can own many `ServiceInstance` objects.
- A Node is never modeled as one proxy service.
- Multiple services/backends may coexist on one Agent when ports do not conflict.
- Backend integrations are compile-time `IBackendProvider` implementations.
- Server owns desired state; Agent reconciles actual state toward it.
- Use one-shot `AgentCommand` only for transient actions such as restart, update, health check, or log collection.
- Do not make arbitrary remote shell execution a normal feature.
- Public shared contracts are owned by the primary architect.
- Prefer a small stable contract over deep abstraction.
- Config writes should be validate -> temporary file -> atomic replace -> reload/restart.
- Backend failure must not crash the whole Agent.
- Usage accounting must be idempotent under retries.

## Roles and model routing

The primary agent is `architect` on GPT-5.6 Sol.

Two subagents exist:

- `terra` on GPT-5.6 Terra: bounded engineering that still requires meaningful reasoning.
- `luna` on GPT-5.6 Luna: deterministic, repetitive, pattern-following work.

Optimize for engineering quality per token, not the lowest possible cost.

### Use Sol directly for

- Architecture and cross-module design.
- Shared/public contracts.
- Database schema decisions.
- Authentication/security model.
- Server-Agent protocol.
- Update protocol.
- BackendProvider public interface.
- Usage idempotency design.
- Difficult multi-module debugging.
- Architectural review, integration, and conflict resolution.

### Delegate to Terra for

- A bounded feature whose architecture is already decided.
- Reconciliation logic.
- Enrollment implementation.
- Agent updater implementation.
- Backend provider implementation.
- Traffic accounting implementation.
- Non-trivial NativeAOT/platform fixes.
- Moderate debugging across several related files.
- Server or Agent feature slices with clear acceptance criteria.

### Delegate to Luna for

- Mechanical CRUD following an existing pattern.
- DTO mapping and serialization registrations.
- Repetitive tests following existing tests.
- Straightforward UI components/CSS.
- Documentation synchronization.
- CI matrix repetition.
- Namespace/rename/formatting work.
- Simple installer variants after one platform pattern is established.
- Small validation rules and boilerplate.

### Delegation discipline

- Do not delegate just to delegate. A trivial local edit is cheaper to do directly.
- Never send the full product specification to a subagent.
- Give a subagent only: goal, relevant paths, existing pattern, acceptance criteria, forbidden files.
- Prefer one coherent task over many tiny calls.
- Parallel editing is allowed only for disjoint files/modules.
- Terra/Luna must not recursively delegate.
- If Luna finds ambiguity, it stops with a concise blocker.
- If Terra finds an architectural issue, it reports the blocker rather than redesigning the system.
- Review delegated work with targeted `git diff`, focused reads, build/tests; do not reread the whole repository.
- Do not ask multiple models to solve the same task unless an earlier attempt failed.

## Context loading

At the beginning of a new project session, read `docs/CONTEXT.md`.

Read these lazily when relevant:

- `docs/ARCHITECTURE.md` for structural decisions.
- `docs/CONTRACTS.md` before changing Server-Agent/shared DTOs.
- `docs/ROADMAP.md` for phase scope.
- `TASKS.md` for active work and ownership.

Do not preload every document when the current task does not need it.

## Project shape

Target structure:

```text
HyPanel/
├── src/
│   ├── HyPanel.Server/
│   ├── HyPanel.Agent/
│   └── HyPanel.Shared/
├── web/
│   └── HyPanel.Web/
├── scripts/
│   ├── install.sh
│   └── install.ps1
├── deploy/
│   └── docker/
├── docs/
├── .opencode/
│   └── agents/
└── .github/
    └── workflows/
```

Do not create many extra projects without a concrete reason.

## Verification

A task is not complete merely because code exists.

Use the smallest relevant verification during development. Before marking a phase complete:

- `dotnet build` succeeds.
- Relevant tests succeed.
- Required NativeAOT `dotnet publish` succeeds for at least the representative RIDs of that phase.
- Web build succeeds when web code changed.
- No known serious trimming/AOT warning is ignored.
- No fake implementation or hidden TODO is presented as complete.

If something is intentionally deferred, mark it explicitly.
