# 介绍

HyPanel 由两部分组成：

- **面板（hypanel-server）**：网页管理界面和 API，保存节点、服务、用户等全部配置。通常只部署一台。
- **节点（hypanel-agent）**：安装在每台代理服务器上，定期主动连接面板，领取配置并在本机运行代理服务。

<ArchitectureDiagram />

节点只需要能访问面板，不需要开放任何管理端口；面板只下发“应该是什么样”，节点自己把实际运行状态调整过去。
某个服务出错不会影响同一节点上的其他服务。

## 支持的代理

| 后端 | 协议 | 说明 |
| --- | --- | --- |
| Hysteria2 | Hysteria2 / QUIC | 多用户、流量统计、Salamander 混淆、端口跳跃 |
| Xray | VLESS REALITY Vision | 每个用户独立 UUID 与流量统计 |
| Xray | Shadowsocks 2022 | 多用户，每个用户独立密钥与流量统计 |

代理内核（Hysteria2、Xray）由面板从官方发布下载并分发给节点，不需要在节点上手动安装。

## 支持的平台

| 组件 | 平台 |
| --- | --- |
| 面板 | Linux x64 / arm64（glibc、musl），或 Docker |
| 节点 | Linux x64 / arm64，另提供 macOS、Windows 版本 |

::: warning
生产环境主要在 Linux x64（systemd）上验证。macOS 和 Windows 节点尚未在真实机器上完整验收；端口跳跃只支持 Linux。
:::

## 使用流程

1. [安装面板](./install)，创建管理员账户。
2. [添加节点](./nodes)：在节点服务器上运行一条安装命令。
3. [创建服务](./services)：在节点上添加 Hysteria2 / Xray 服务。
4. [创建用户](./users)：把服务分给用户组，用户登录后复制订阅链接导入客户端。
