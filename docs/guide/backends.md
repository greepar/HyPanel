# 添加新协议（开发者）

HyPanel 把服务拆成两层：

- **协议**：客户端说的语言，决定订阅里怎么写，例如 Hysteria2、VLESS + REALITY。
- **内核（engine）**：在节点上实际运行的程序，例如官方 Hysteria 2、Xray-core、Mihomo。

一个“后端类型”（服务里存的 `backend_type`，如 `hysteria2`、`xray-hysteria2`、`xray`、`mihomo-reality`）就是“某个协议跑在某个内核上”。同一协议可以有多个内核，已有服务可以在它们之间切换，订阅不变。

## 需要改的地方

| 位置 | 做什么 |
| --- | --- |
| `src/HyPanel.Server/Backends/BackendDefinitionCatalog.cs` | 加一条定义：编辑器字段、内核（决定下载哪个发布包）、协议、功能标记、默认值生成、需要的最低 Agent 版本 |
| `src/HyPanel.Server/Backends/Protocols.cs` | 客户端协议是新的时，加一个 `ServiceProtocol` 子类：把服务和用户凭据变成订阅条目，并写出 Mihomo 格式 |
| `src/HyPanel.Server/Releases/BackendReleaseSources.cs` | 内核是新的时，告诉面板从哪个 GitHub 仓库、哪个文件下载 |
| `src/HyPanel.Agent/Backends/` | 加一个 `IBackendProvider`：校验配置、生成内核配置文件、启动参数、流量统计 |

前端不用改：编辑器的字段、哪些区块显示（线路、入口、证书、端口跳跃）都由面板返回的定义和功能标记决定。

## 功能标记

`BackendFeatures` 里的标记让面板、Agent 同步和编辑器按能力判断，而不是按名字：

| 标记 | 含义 |
| --- | --- |
| `MultiUser` | 每个用户独立凭据和流量统计，用户组授权会作用到它 |
| `ControlPort` | 需要本机统计接口端口 |
| `Routes` | 支持一个端口多条线路 |
| `Relay` | 可以由其他节点转发 TCP 端口进来 |
| `Certificate` | 需要 TLS 证书 |
| `AcmeCertificate` | 内核能自己申请 ACME 证书 |
| `PortHopping` | 支持 UDP 端口跳跃（Agent 用防火墙规则转发，Provider 实现 `IPortHoppingBackend`） |

## 跑在 Xray 上的协议

Xray 内核的协议都继承 `XrayEngineProvider<TConfig>`。引擎部分是共用的：每个用户每条线路一个客户端、默认出口和每条线路的出口、按用户分流、统计接口、进程和流量统计。新协议只需要提供：

1. 配置类（实现 `IXrayServiceConfig`，并加到 `XrayJsonSerializerContext`）；
2. 协议自己的校验 `IsValidProtocolConfig`；
3. 入站 `BuildInbound`：把传入的 `XrayClientIdentity` 列表写成这个协议的用户。

然后在 Agent 的 `Program.cs` 注册 Provider，在面板的目录里加一条 `engine: "xray"` 的定义即可。参考 `XrayProvider`（REALITY）和 `XrayHysteria2Provider`。

## 跑在 Mihomo 上的协议

Mihomo 内核的协议都继承 `MihomoEngineProvider<TConfig>`，用 Mihomo 的 `listeners` 作服务端。目前有：

| 后端类型 | 协议 | 说明 |
| --- | --- | --- |
| `mihomo-reality` | VLESS + Vision + REALITY | 字段与 `xray` 相同，服务可以在 Xray 和 Mihomo 之间切换，订阅不变；握手目标总是直连（`reality-config.proxy: DIRECT`），与 Xray 一致 |
| `mihomo-anytls` | AnyTLS | 需要上传或节点本地路径的证书（Mihomo 不能自己申请 ACME）；每个用户用自己的凭据作密码 |
| `mihomo-ss2022` | Shadowsocks 2022 + sing-mux | `2022-blake3-aes-128-gcm`，TCP 与 UDP 同端口；客户端密码是「服务端密钥:用户密钥」，用户密钥由凭据派生（`Shadowsocks2022.UserKey`）；订阅开启 h2mux，服务端自动接受 |

引擎部分共用：

- 配置以 JSON 写入（Mihomo 当作 YAML 读取），启动参数 `-d <实例目录> -f <配置>`。Mihomo 只读实例目录里的文件，协议需要的外部目录（如节点本地证书）通过 `SafePaths` 放进 `SAFE_PATHS` 环境变量。
- 默认出口：`MATCH,DIRECT`，或 `MATCH,upstream` 走上游代理；禁用 IPv6 出口时写 `ipv6: false`。
- 多条线路：三种协议都支持。每个用户在每条线路上用派生凭据登录（用户名 `hypanel-<用户>-<线路>`，与 Xray 相同），`IN-USER` 规则把这些用户名送到线路自己的出口，其余走默认出口。
- 流量统计：Agent 对每个服务常驻一个到 `127.0.0.1:<控制端口>/connections` 的 WebSocket（每 500 毫秒推送一次打开的连接，`MihomoConnectionWatcher`），按连接增量归到 `inboundUser`；连接关闭前最后不到半秒的流量，用进程总流量对账后分给刚关闭连接的用户（`MihomoTrafficTracker`）。推送断开时退回到每次同步读一次。节点总量准确，用户之间的分配接近准确。
- 二进制使用 `MetaCubeX/mihomo` 发布的 `amd64-v1` 构建，兼容所有 x86-64 CPU。

新协议需要：配置类（实现 `IMihomoServiceConfig` 并加到 `MihomoJsonSerializerContext`）、`IsValidProtocolConfig`、`BuildListener`（返回不含 `name`/`listen`/`port` 的监听器），同时占用 UDP 时覆盖 `AlsoListensOnUdp`。
