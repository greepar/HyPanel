# 跨节点出口

Hysteria2、Xray REALITY 和 Xray Shadowsocks 服务可以运行在入口节点 A，同时使用出口节点 B 的 IPv4 访问网站。
例如 A 有 CN2 线路，B 有更合适的出口 IP：客户端连接 A，A 解密代理流量后通过不加密的内核隧道送到 B，
B 做 NAT 后访问目标网站。网站看到 B 的 IP，用户认证和流量统计仍由 A 的服务处理。

## 配置

1. 更新面板和两端 Agent。旧 Agent 不支持出口配置。
2. 两端必须是支持所选隧道后端的 Linux 节点，有固定、可互通的公网 IPv4。
3. 使用新版节点安装命令，勾选安装网络工具，安装 `iproute2`、`nftables`、`ping` 并配置 Agent 权限。
4. 在 B 的“设置 → 出口转发”选择 GRE 或 GRE over UDP（FOU），点击“启用出口转发”。Agent 检查工具、权限和内核支持，成功后显示“已就绪”。
5. 在 A 创建或编辑服务，在“出口节点”中选择 B，然后保存。
6. 两端 Agent 自动配置隧道、源地址策略路由和 B 的 NAT。A 会通过隧道检查 B 的连通性。

从正在使用的 GRE 切换到 GRE over UDP 时，先在 A 的服务中暂选“本机直连”并保存，待 B 的“使用此出口的服务”归零后停用 B 的出口，选择 GRE over UDP 并重新启用；B 显示“已就绪”后再把 A 的服务选回 B。

没有额外的用户态转发程序需要常驻；“启用出口转发”配置的是 Linux 内核网络。
每个服务有独立的隧道地址和路由表，可以分别选择不同出口。A 的整机默认路由不会修改。
订阅地址仍是 A，证书和端口跳跃仍配置在 A。

## 网络与权限

- 选择原生 GRE 时，两端安全组和已有防火墙需要允许对端发来的 **IP 协议 47（GRE）**，它不是 TCP/UDP 的端口 47。
- 选择 GRE over UDP（FOU）时，两端 Agent 使用内核 FOU 接收端口 `UDP 47541`。通常只需在 B 的安全组放行来自 A 公网 IP 的入站 UDP 47541；A 主动向 B 发包，有状态防火墙会允许匹配的回包。若 A 的防火墙是无状态规则或限制回包，也要在 A 放行来自 B 的 UDP 47541。FOU 仍不加密，也不提供身份认证。
- 隧道需要允许 ICMP，以便 Agent 检查连通性和获得网络错误。
- B 的已有防火墙需允许从 `hpe*` 隧道接口到公网的转发，以及已建立连接的回程。
  HyPanel 只管理自己的 `inet hypanel_egress` 表，不清空其他防火墙规则。
  其他表中的拒绝规则仍然生效。
- 原生安装的 Agent 使用 `CAP_NET_ADMIN` 管理隧道、路由和网络 sysctl，使用 `CAP_NET_RAW` 检查连通性。
  旧安装需要重新运行新版安装命令，单纯更新 Agent 二进制不会修改 systemd 权限或补齐工具。
- Docker 需要 Linux 主机、host 网络、`NET_ADMIN` 和 `NET_RAW` 权限，可写的网络 sysctl，以及主机的 GRE 支持。
  Docker Desktop / OrbStack 的 Linux 内核如果不支持 GRE，会显示配置失败。
- 如系统没有自动加载 GRE 模块，可以由管理员运行 `modprobe ip_gre`；GRE over UDP 还需要内核 FOU 支持，必要时加载 `fou` 模块。两端 Agent 都会尝试配置 FOU 接收端口，任一端不支持时会报告配置失败并停止该服务出网。

启用出口时 B 会开启 IPv4 forwarding，并将涉及的严格反向路径检查调整为宽松模式。
这些主机全局设置不会在停用时自动恢复，避免影响其他网络服务。
隧道 MTU 为 1400，B 对经过隧道的 TCP SYN 做 MSS 限制。

## 故障与停用

出口不可达或配置失败时，选择该出口的服务会停止出网并报告错误，默认不切回 A 的 IP。
Agent 后续同步会重新配置和检查，连通后恢复服务。隧道接口消失时，策略路由表中的不可达默认路由防止流量落回整机默认路由。

要恢复本机出口，在服务中选择“本机直连”并保存。所有服务解除引用之后，才可以停用或删除 B。
删除服务、取消出口或停用出口后，Agent 清理对应的隧道、策略路由和自有防火墙规则；卸载脚本也清理这些资源。

## 当前范围

目前支持 Hysteria2、Xray REALITY 和 Xray Shadowsocks 的 IPv4 出站连接；IPv6 目标不会通过 A 的本机 IPv6 回退。
域名解析继续使用 A 的系统 DNS。GRE 与 GRE over UDP 都不提供加密或身份认证，适用于已经接受两台服务器间明文传输的场景。
链路性能仍取决于 A 与 B 的互联、两端 VPS 的带宽和 CPU，需要在实际服务器上测量。

## 后端扩展

出口配置使用独立的后端标识，当前实现 `gre` 和 `gre-udp`，预留 `wireguard` 和 `gretap-udp`。
预留标识不会出现在可用后端列表里，也不会被自动替换成 GRE。
面板提供 `/api/admin/v1/egress-backends` 返回已经实现的后端，Agent 上报 `supportedTransports`，
只有目标后端被入口和出口两端支持时，才能保存新的服务绑定。新增服务后端可通过后端定义的 `SupportsEgress` 能力声明接入。

Agent 的 `Networking/IEgressTransportBackend.cs` 定义后端接口：

- `PrepareExit`：检查并准备出口使用的内核能力。
- `ConfigureTunnel`：配置该后端的隧道接口，接收下发的后端参数。
- `InputFirewallRule`：生成外层协议的入站匹配规则。
- `Definition`：声明标识、显示名称、是否加密、外层传输及 MTU。

`GreEgressTransportBackend` 是当前实现，注册到 `EgressTransportRegistry`。
`GreUdpEgressTransportBackend` 使用 Linux FOU 把 keyed GRE 封进 UDP，固定使用 47541 端口，预留给 HyPanel 管理。
`EgressNetworkManager` 统一配置内层 IPv4 地址、源地址策略路由、NAT、连通性检查、出口故障保护和资源清理。
TCP MSS 根据后端的 MTU 计算。未来的 UDP 封装后端可以复用这套流程。

增加真正可用的后端时，还需要实现其两端参数的生成和保存，在共享后端目录和 Agent 注册表中注册，
并验证实际 Linux 转发。WireGuard 的密钥管理及 GRETAP over UDP 的 UDP 封装参数仍属于各自未来实现的工作。
已有节点被服务引用时不能更换出口后端，避免两端封装配置短暂不一致。
