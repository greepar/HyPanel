# 添加新协议（开发者）

HyPanel 把服务拆成两层：

- **协议**：客户端说的语言，决定订阅里怎么写，例如 Hysteria2、VLESS + REALITY。
- **内核（engine）**：在节点上实际运行的程序，例如官方 Hysteria 2、Xray-core，以后也可以是 Mihomo。

一个“后端类型”（服务里存的 `backend_type`，如 `hysteria2`、`xray-hysteria2`、`xray`）就是“某个协议跑在某个内核上”。同一协议可以有多个内核，已有服务可以在它们之间切换，订阅不变。

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
