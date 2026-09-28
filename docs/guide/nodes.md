# 添加节点

1. 在面板“节点”页点击“新建节点”，填一个名字。
2. 复制弹出的安装命令，在节点服务器上用 root（Windows 用管理员 PowerShell）运行。
3. 几秒后节点变为“在线”，就可以在节点详情里添加服务了。

Linux / macOS 的安装命令形如：

```bash
curl -fsSL https://panel.example.com/i/123456 | sh
```

- 六位数字码 15 分钟内有效，只能用一次；过期了在节点设置里重新生成即可。
- 节点只需要能访问面板，不需要开放任何管理端口。只需放行代理服务本身用到的端口。
- 程序安装在 `/opt/hypanel/agent/hypanel-agent`，数据在 `/var/lib/hypanel-agent`，以专用的非特权用户运行。

## 用 Docker 运行

在安装弹窗里选择“Docker”，复制生成的 `docker run` 命令到 Linux 主机上运行即可。容器使用主机网络（面板下发的服务可以监听任意端口），
带 `NET_ADMIN` 权限（Hysteria2 端口跳跃），镜像基于 Alpine 并自带 nftables。

- Agent 程序和数据都保存在 `hypanel-agent` 数据卷里：首次启动时从面板下载，之后照常自动更新，重启或重建容器都不会丢。
- 用新的安装命令重新运行，就是重新安装：会替换掉旧容器，并以新的令牌重新注册。
- 网络参数优化需要改主机内核参数，容器里做不了，请在主机上单独处理。

卸载：

```bash
docker rm -f hypanel-agent; docker volume rm hypanel-agent; nft delete table inet hypanel_hop
```

## 重新安装与卸载

在节点上重新运行面板生成的安装命令，即可修复安装或迁移到新版本的安装方式，原有服务会保留。

卸载并清除全部数据：

```bash
curl -fsSL https://panel.example.com/install.sh | sh -s -- --uninstall
```

加上 `--keep-data` 可以保留数据目录。卸载后记得在面板里删除这个节点。

## 节点页面

| 标签 | 内容 |
| --- | --- |
| 概览 | 在线状态、CPU / 内存 / 磁盘、Agent 版本 |
| 服务 | 该节点上的全部服务，状态每 5 秒自动刷新 |
| 网络 | 每个服务的代理流量 |
| 日志 | 服务报错和诊断日志，可手动收集最新日志 |
| 设置 | Agent 更新与自动更新、删除节点 |

节点国旗根据节点的出口 IP 自动识别，会显示在面板和订阅的节点名前。
