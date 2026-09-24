# 安装面板

面板可以用 Docker 部署，也可以直接在 Linux 上以 systemd 服务运行。两种方式都需要先准备两个密钥：

```bash
export HYPANEL_ADMIN_TOKEN="$(openssl rand -hex 32)"
export HYPANEL_MASTER_KEY="$(openssl rand -base64 32)"
```

| 变量 | 作用 |
| --- | --- |
| `HYPANEL_ADMIN_TOKEN` | 安装令牌，首次创建管理员时使用 |
| `HYPANEL_MASTER_KEY` | 加密代理凭据和证书私钥，**安装后不能再改**，请和备份分开妥善保存 |

## 方式一：Docker Compose（推荐）

创建 `compose.yml`：

```yaml
services:
  hypanel:
    image: ghcr.io/greepar/hypanel:latest
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      HYPANEL_ADMIN_TOKEN: ${HYPANEL_ADMIN_TOKEN:?required}
      HYPANEL_MASTER_KEY: ${HYPANEL_MASTER_KEY:?required}
    volumes:
      - hypanel-data:/data

volumes:
  hypanel-data:
```

启动：

```bash
docker compose up -d
```

镜像支持 `linux/amd64` 和 `linux/arm64`，数据保存在 `/data`。更新时执行：

```bash
docker compose pull && docker compose up -d
```

## 方式二：直接部署（systemd）

1. 从 [Releases](https://github.com/greepar/HyPanel/releases) 下载 `hypanel-server-<版本>-linux-<架构>.tar.gz`，用
   `SHA256SUMS` 校验。
2. 创建用户和目录，放入程序：

   ```bash
   useradd --system --no-create-home --shell /usr/sbin/nologin hypanel
   mkdir -p /opt/hypanel/server /var/lib/hypanel /etc/hypanel
   tar -xzf hypanel-server-*.tar.gz -C /opt/hypanel/server
   chown -R hypanel:hypanel /opt/hypanel/server /var/lib/hypanel
   ```

3. 创建 `/etc/hypanel/server.env`（权限 `0600`）：

   ```bash
   HYPANEL_ADMIN_TOKEN=<上面生成的安装令牌>
   HYPANEL_MASTER_KEY=<上面生成的 MasterKey>
   HYPANEL_DATA_DIR=/var/lib/hypanel
   ```

4. 安装 [systemd 示例](https://github.com/greepar/HyPanel/blob/main/deploy/systemd/hypanel-server.service)
   到 `/etc/systemd/system/hypanel-server.service`，然后启动：

   ```bash
   systemctl daemon-reload
   systemctl enable --now hypanel-server
   ```

示例配置监听 `127.0.0.1:5291`，需要通过 Nginx、Caddy 等反向代理对外提供 HTTPS。之后可以在面板“设置 → Server 版本”
里一键检查并更新。

## 首次登录

打开面板地址，登录页会显示“创建管理员”：填入 `HYPANEL_ADMIN_TOKEN` 和新管理员的用户名、密码即可。创建完成后这个入口
自动关闭，之后用账户密码登录；也可以在“设置 → 通行密钥”里添加 Passkey，用指纹或面容登录。

::: tip
接入公网节点之前，请确保面板已经通过 HTTPS 访问。节点和面板之间的所有通信都走这个地址。
:::
