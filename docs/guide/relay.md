# 转发入口（TCP 端口转发）

服务运行在节点 B，由入口节点 A 的 Agent 把一个 TCP 端口转发到 B。客户端连接 A，网站看到的是 B 的出口 IP。
典型用法：A 有更好的线路（例如 CN2），B 有更合适的出口 IP。

```
客户端 ──▶ 入口节点 A（转发端口） ──▶ 节点 B 上的服务 ──▶ 互联网
                                      └ 网站看到 B 的 IP
```

REALITY 是端到端加密的，A 只搬运字节，不解密也不重新加密，所以 A 的 CPU 开销很小，延迟只多一跳。

## 配置

1. 在 B 上创建或编辑 Xray REALITY 服务。
2. 在“转发入口节点”选择 A，并填写“转发端口”（在 A 上监听）。
3. 保存。A 的 Agent 在下一次同步时开始监听该端口，并自动放行防火墙。
4. 订阅自动使用 A 的公网地址和转发端口，其余参数（UUID、公钥、SNI）仍是 B 的。

## 注意事项

- **B 需要允许 A 访问服务的监听端口。** 这是 B 的防火墙或云安全组，Agent 不替你改云厂商的安全组。
- **云厂商安全组需要自行放行 A 上的转发端口。**
- 转发端口是 A 上的 TCP 监听端口，不能和 A 上其他已启用服务的 TCP 监听端口重复；保存或启用服务时面板会检查，并说明与哪个服务冲突。已停用的服务不占用端口。
- 用户认证和流量统计在 B，B 看到的来源 IP 是 A。
- REALITY 的“伪装目标”由 B 去连接，请选一个对 B 来说稳定、低延迟的站点。目标不稳定时，客户端会表现为连接超时。
- 当前只支持 Xray REALITY（TCP）。Hysteria2 和 Shadowsocks 暂不支持转发。
- 转发由 Agent 内置的 C# 代码完成，不依赖 `ip`、`nft` 或 `iptables`。入口节点 A 可以是 Linux、macOS 或 Windows。
  Windows 和 macOS 的系统防火墙需要自行放行 Agent 监听的转发端口；Docker Desktop / OrbStack 里的 Agent 会监听该端口，由 Docker 转发到主机。
- A 绑定端口失败（例如被占用）时，Agent 日志会记录原因；面板暂不显示转发状态。

## 防火墙

Agent 会为 A 上的转发端口自动放行入站 TCP，不需要手工添加规则。规则只在需要时存在：服务停用或删除后，Agent 会撤销自己添加的规则；Agent 重启后也会清理上次遗留的规则。

- **firewalld**：通过 `firewall-cmd` 同时写入运行时和永久配置，不会 `reload` 清掉你自己的运行时规则。
  firewalld 只允许 root 修改规则，所以安装脚本会写入 `/etc/polkit-1/rules.d/50-hypanel-agent-firewalld.rules`，仅授权 Agent 用户管理 firewalld。
  **旧节点需要重新运行一次新版安装命令**才会写入这条规则；否则 Agent 会在日志里提示“无权管理 firewalld”，此时请手动放行转发端口。
- **ufw、Docker 和其他 iptables 防火墙**：Agent 使用自己的 `HYPANEL-INPUT` 链并插到最前面，只需要 `CAP_NET_ADMIN`。
- 没有 firewalld 和 iptables 的主机：无法自动放行，请自行放行转发端口。

失败时 Agent 每分钟重试一次，同一条错误只记录一次日志。

## 与跨节点出口的关系

早期版本还提供“跨节点出口”（GRE、GRE over UDP、WireGuard 隧道）：客户端连接 A，A 解密后经隧道交给 B 出网。
它要在 A 上解密再重新加密，CPU 开销大，而且依赖内核对隧道协议的支持，现已移除。升级时面板会自动清理旧的出口配置，
并从服务配置里去掉 `exitNodeId`。需要“入口在 A、出口 IP 在 B”的场景请改用转发入口。
