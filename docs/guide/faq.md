# 常见问题

## 节点显示离线

在节点上查看 Agent 日志：

```bash
systemctl status hypanel-agent --no-pager
journalctl -u hypanel-agent -n 100 --no-pager
```

常见原因是节点无法访问面板地址（DNS、防火墙、HTTPS 证书）或系统时间不准。面板暂时不可用时，节点上已运行的代理服务
不受影响。

## 安装命令提示注册码过期

注册码 15 分钟内有效且只能用一次，在节点设置里重新生成安装命令即可。

## 服务一直“重启退避”

到节点的“日志”页查看报错。最常见的是端口被占用：换一个监听端口，或停掉占用端口的程序。

## 客户端连不上

- 确认云服务商安全组放行了服务端口（Hysteria2 是 UDP，REALITY / Shadowsocks 是 TCP）；
- Hysteria2 使用域名证书时，确认 SNI 对应的域名正确；
- 在面板更新订阅后，客户端需要重新拉取订阅。

## 订阅是空的

确认用户已启用、未到期、未超出流量，并且所在用户组勾选了已启用的服务。

## 面板相关

```bash
systemctl status hypanel-server --no-pager
journalctl -u hypanel-server -n 100 --no-pager
```

- **MasterKey 丢失或不匹配**：必须恢复原来的 `HYPANEL_MASTER_KEY`，不要为已有数据库重新生成。
- **忘记管理员密码**：用另一个管理员账户修改；如果只有一个管理员，可以用安装令牌通过 API 新建一个管理员，再登录修改：

  ```bash
  curl -X POST https://panel.example.com/api/admin/v1/users \
    -H "Authorization: Bearer $HYPANEL_ADMIN_TOKEN" -H "Content-Type: application/json" \
    -d '{"username":"rescue","password":"<新密码>","role":"Admin","enabled":true}'
  ```
