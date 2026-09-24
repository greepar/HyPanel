# 更新与备份

## 更新

| 对象 | 方式 |
| --- | --- |
| 面板（直接部署） | “设置 → Server 版本”点“检查更新”，有新版本时点“立即更新” |
| 面板（Docker） | `docker compose pull && docker compose up -d` |
| Agent | 节点设置里开启“自动更新”，或在节点页手动更新 |
| 代理内核 | 服务卡片上开启“自动更新后端”，或手动点“更新到 x.x.x” |

所有更新都会校验文件大小和 SHA256，新版本确认正常后才删除旧版本，失败会自动回滚。新节点、新服务的默认更新策略可以在
“设置 → 自动更新”里设置。

## 备份与恢复

在“设置 → 数据备份与恢复”里可以：

- **创建备份**：面板运行时即可在线备份，也可以下载到本地；
- **恢复备份**：上传备份文件，面板会先完整校验，通过后自动创建一份紧急备份，再替换数据并重启。

::: danger 务必同时保存 MasterKey
备份文件里不包含 `HYPANEL_MASTER_KEY`。只有备份、没有对应的 MasterKey，代理凭据和证书私钥都无法恢复。请把两者分开
妥善保存。
:::

网页无法打开时，也可以在面板主机上用命令行操作（直接部署方式）：

```bash
# 在线备份
sudo -u hypanel /opt/hypanel/server/hypanel-server backup

# 离线恢复
sudo systemctl stop hypanel-server
sudo -u hypanel /opt/hypanel/server/hypanel-server restore /path/to/hypanel-backup.tar.gz
sudo systemctl start hypanel-server
```
