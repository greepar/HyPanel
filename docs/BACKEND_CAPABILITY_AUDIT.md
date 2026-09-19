# Backend Capability Audit

Research date: 2026-09-19. This matrix covers only current official server/configuration and statistics interfaces.
It does not treat log parsing, traffic estimation, shared passwords or third-party panels as reliable capability.

| Backend / shipped profile | Official version checked | MultiUser | PerUserTraffic | TrafficStats | Decision |
| --- | --- | --- | --- | --- | --- |
| Hysteria2 | 2.12.3 | Yes | Yes | Yes | Plan implementation |
| Mihomo SS2022 listener | 1.19.31 | No stable identity in current profile | No | Aggregate/controller only | Do not implement |
| sing-box SS2022 | 1.14.1 | Yes | Conditional | Conditional | Prototype/gate before implementation |

## Hysteria2

Official Hysteria2 supports `auth.type: userpass`, where deterministic usernames map cleanly to HyPanel User IDs.
Its loopback-capable Traffic Stats API returns a JSON map from authenticated client ID to cumulative `tx`/`rx`, and
also exposes online client IDs. The API supports a secret in the `Authorization` header. This satisfies deterministic
configuration, stable identity mapping and Agent-side collection without parsing logs.

Implementation should use stable non-secret usernames such as `hypanel-{UserId:N}`, independent random passwords, a
loopback-only Traffic Stats API with an Agent-generated/stored secret, and the existing cumulative-counter reset and
idempotent usage-batch mechanism. `GET /traffic` should be read without `clear=1`; HyPanel must not depend on destructive
reads for retry correctness.

Official sources:

- <https://v2.hysteria.network/docs/advanced/Full-Server-Config>
- <https://v2.hysteria.network/docs/advanced/Traffic-Stats-API>
- <https://v2.hysteria.network/docs/developers/URI-Scheme>

## Mihomo

The official Shadowsocks listener documentation for the currently shipped SS2022 profile specifies one `password`, not
a first-class user list. Mihomo has user lists for other listeners/global HTTP-SOCKS authentication, but those are not
evidence that the managed SS2022 inbound provides stable per-user identities. The controller exposes connections and
aggregate traffic, but no documented, durable SS2022 user-counter contract was found.

HyPanel must keep this profile single-user and must not claim MultiUser or PerUserTraffic. Shared-password grants,
connection-log attribution and estimated splits remain forbidden.

Official sources:

- <https://wiki.metacubex.one/en/config/inbound/listeners/ss>
- <https://wiki.metacubex.one/en/config/inbound>
- <https://wiki.metacubex.one/en/config/general>

## sing-box

Official sing-box Shadowsocks configuration supports a deterministic `users[]` structure for AEAD 2022. The official
V2Ray API has `stats.users`, which is a plausible stable per-user counter path, but that API is explicitly not included
in every build. The SSM API can dynamically manage Shadowsocks users and persist user/traffic state, but adopting it
would add a second mutation authority and is unnecessary for HyPanel's desired-state model.

Before enabling capability flags, test the exact official release artifact used by HyPanel 1.14.1 (and future resolved
releases) for V2Ray API inclusion, user-name counter semantics, TCP/UDP accounting, restart/reset behavior
and bounded loopback collection. Until that executable-level gate passes, only MultiUser is a candidate; HyPanel must
continue reporting PerUserTraffic/TrafficStats as unsupported.

Official sources:

- <https://sing-box.sagernet.org/configuration/inbound/shadowsocks>
- <https://sing-box.sagernet.org/configuration/experimental/v2ray-api>
- <https://sing-box.sagernet.org/configuration/service/ssm-api>

## Decision

1. Implement Hysteria2 MultiUser + PerUserTraffic using official userpass and Traffic Stats API.
2. Do not expand Mihomo's current SS2022 profile.
3. Run a narrow sing-box artifact prototype before deciding between MultiUser-only and MultiUser + PerUserTraffic.
4. Do not add another protocol profile as part of this audit.
