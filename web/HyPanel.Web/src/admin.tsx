import { useEffect, useRef, useState } from "preact/hooks";
import { ApiError, type ApiClient } from "./api";
import {
  backendFor,
  emptyService,
  formFor,
  parseConfig,
  payloadFor,
  serviceFacts,
  setBackendDefinitions,
} from "./backend";
import type {
  AppRoute,
  BackendDefinition,
  BackendField,
  Backup,
  BackupValidation,
  Certificate,
  HealthSummary,
  GlobalSettings,
  Node,
  NodeIdentity,
  NodeTab,
  PublicEndpoint,
  ServerUpdate,
  Service,
  ServiceDiagnostic,
  ServiceForm,
  ServiceRef,
  Template,
  Usage,
  User,
  UserForm,
  UserServiceAccess,
} from "./domain";
import {
  Empty,
  ErrorState,
  formatBytes,
  formatUptime,
  Loading,
  messageFor,
  Modal,
  Notice,
  Page,
  Stat,
  usedBytes,
} from "./ui";

type PageProps = { api: ApiClient; setError: (value: string) => void };
const emptyUser: UserForm = {
  username: "",
  password: "",
  role: "User",
  enabled: true,
  trafficLimitBytes: "",
  expiresAtUtc: "",
};

export function AdminApp({
  route,
  api,
  setError,
}: PageProps & { route: AppRoute }) {
  const nodeRoute = route.match(
    /^nodes\/([^/]+)\/(overview|services|network|logs|settings)$/,
  );
  if (nodeRoute)
    return (
      <NodeDetailPage
        api={api}
        setError={setError}
        nodeId={decodeURIComponent(nodeRoute[1])}
        tab={nodeRoute[2] as NodeTab}
      />
    );
  if (route === "nodes") return <NodesPage api={api} setError={setError} />;
  if (route === "templates")
    return <TemplatesPage api={api} setError={setError} />;
  if (route === "users") return <UsersPage api={api} setError={setError} />;
  if (route === "settings") return <SettingsPage api={api} setError={setError} />;
  return <OverviewPage api={api} setError={setError} />;
}

function SettingsPage({ api, setError }: PageProps) {
  const [settings, setSettings] = useState<GlobalSettings | null>(null);
  const [certificates, setCertificates] = useState<Certificate[]>([]);
  const [loading, setLoading] = useState(true);
  const load = async () => {
    setLoading(true);
    try { const [nextSettings, nextCertificates] = await Promise.all([api.settings(), api.certificates()]); setSettings(nextSettings); setCertificates(nextCertificates); }
    catch (reason) { setError(messageFor(reason, "无法加载设置")); }
    finally { setLoading(false); }
  };
  useEffect(() => { void load(); }, []);
  const server = settings?.server;
  const update = async () => {
    if (!server?.latestVersion || !confirm(`更新 Server 到 ${server.latestVersion}？服务会短暂重启。`)) return;
    try { const result = await api.updateServer(); setError(`Server ${result.version} 更新已开始。`); }
    catch (reason) { setError(messageFor(reason, "无法更新 Server")); }
  };
  const save = async () => { if (!settings) return; try { await api.updateSettings(settings); setError("全局设置已保存。"); await load(); } catch (reason) { setError(messageFor(reason, "无法保存设置")); } };
  if (loading || !server || !settings)
    return <Page title="设置" description="控制面更新、Release 来源和数据目录。"><Loading /></Page>;
  return (
    <Page title="设置" description="控制面更新、Release 来源和数据目录。">
      <div className="detail-grid">
        <section className="card panel">
          <SectionTitle title="Server 版本" description={server.deploymentMode === "Docker" ? "容器由外部编排更新，HyPanel 不访问 Docker daemon。" : "Bare-metal Server 使用 size、SHA256 和 self-test 校验。"} />
          <dl className="facts-list"><div><dt>当前版本</dt><dd>{server.currentVersion}</dd></div><div><dt>最新版本</dt><dd>{server.latestVersion ?? "暂不可用"}</dd></div><div><dt>部署方式</dt><dd>{server.deploymentMode}</dd></div><div><dt>状态</dt><dd>{server.status}</dd></div></dl>
          {server.error && <Notice kind="error">{server.error}</Notice>}
          {server.deploymentMode === "Docker" ? <Notice>发现新镜像时运行 <code>docker compose pull &amp;&amp; docker compose up -d</code>。</Notice> : <div className="section-toolbar"><button className="button button-primary" type="button" disabled={!server.updateAvailable || server.status === "Downloading" || server.status === "Applying"} onClick={() => void update()}>更新 Server</button></div>}
        </section>
        <section className="card panel"><SectionTitle title="更新默认值" description="仅应用于之后创建的 Node 和 Service" /><div className="modal-form"><label>Agent 默认策略<select value={settings.agentUpdateDefaultPolicy} onChange={event => setSettings({ ...settings, agentUpdateDefaultPolicy: event.currentTarget.value as "Manual" | "Auto" })}><option value="Manual">Manual</option><option value="Auto">Auto</option></select></label><label>Backend 默认策略<select value={settings.backendUpdateDefaultPolicy} onChange={event => setSettings({ ...settings, backendUpdateDefaultPolicy: event.currentTarget.value as "Manual" | "Auto" })}><option value="Manual">Manual</option><option value="Auto">Auto</option></select></label><label>GitHub Mirror Base URL<input placeholder="留空使用 GitHub 官方源" value={settings.githubMirrorBaseUrl ?? ""} onInput={event => setSettings({ ...settings, githubMirrorBaseUrl: event.currentTarget.value || null })} /></label><button className="button button-primary" type="button" onClick={() => void save()}>保存设置</button></div></section>
         <section className="card panel"><SectionTitle title="Release 与数据" description="受控的官方发布来源" /><dl className="facts-list"><div><dt>Agent Release</dt><dd>{settings.agentReleaseVersion ?? "Unavailable"}</dd></div>{Object.entries(settings.backendReleases).map(([name, version]) => <div key={name}><dt>{name}</dt><dd>{version}</dd></div>)}<div><dt>数据目录</dt><dd>{settings.dataDirectory}</dd></div><div><dt>数据库</dt><dd>{formatBytes(settings.databaseSizeBytes)}</dd></div></dl></section>
          <CertificatePanel api={api} certificates={certificates} setCertificates={setCertificates} setError={setError} />
          <BackupPanel api={api} setError={setError} />
       </div>
    </Page>
  );
}

function BackupPanel({ api, setError }: PageProps) {
  const [backups, setBackups] = useState<Backup[]>([]);
  const [validation, setValidation] = useState<BackupValidation | null>(null);
  const [confirmation, setConfirmation] = useState("");
  const [busy, setBusy] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const load = async () => { try { setBackups(await api.backups()); } catch (reason) { setError(messageFor(reason, "无法加载备份列表")); } };
  useEffect(() => { void load(); }, []);
  const create = async () => {
    setBusy(true);
    try { const backup = await api.createBackup(); setBackups(current => [backup, ...current]); setError("备份已创建。"); }
    catch (reason) { setError(messageFor(reason, "无法创建备份")); }
    finally { setBusy(false); }
  };
  const download = async (backup: Backup) => {
    try { const { blob, filename } = await api.downloadBackup(backup.id); const url = URL.createObjectURL(blob); const link = document.createElement("a"); link.href = url; link.download = filename; link.click(); URL.revokeObjectURL(url); }
    catch (reason) { setError(messageFor(reason, "无法下载备份")); }
  };
  const remove = async (backup: Backup) => {
    if (!confirm("删除此备份？此操作无法撤销。")) return;
    try { await api.deleteBackup(backup.id); setBackups(current => current.filter(item => item.id !== backup.id)); setError("备份已删除。"); }
    catch (reason) { setError(messageFor(reason, "无法删除备份")); }
  };
  const validate = async (event: Event) => {
    const file = (event.currentTarget as HTMLInputElement).files?.[0];
    setValidation(null); setConfirmation("");
    if (!file) return;
    setBusy(true);
    try { setValidation(await api.validateBackup(file)); }
    catch (reason) { setError(messageFor(reason, "无法验证备份文件")); }
    finally { setBusy(false); if (fileInput.current) fileInput.current.value = ""; }
  };
  const restore = async () => {
    if (!validation?.valid || confirmation !== "RESTORE") return;
    setBusy(true);
    try { const result = await api.restoreBackup(validation.validationId); setError(`恢复已开始：${result.status}。Server 将重启，当前会话可能断开。`); }
    catch (reason) { setError(messageFor(reason, "无法恢复备份")); }
    finally { setBusy(false); }
  };
  return <section className="card panel backup-panel">
    <SectionTitle title="数据备份与恢复" description="创建、下载或验证 Server 数据库备份。" />
    <div className="section-toolbar"><span>最近备份</span><button className="button button-primary" type="button" disabled={busy} onClick={() => void create()}>{busy ? "处理中…" : "立即创建备份"}</button></div>
    {backups.length ? <div className="backup-list">{backups.map(backup => <article className="backup-row" key={backup.id}><div><strong>{formatDate(backup.createdAtUtc)}</strong><small>{formatBytes(backup.sizeBytes)} · Schema {backup.schemaVersion} · Server {backup.serverVersion}</small></div><div className="row-actions"><button className="button button-secondary" type="button" onClick={() => void download(backup)}>下载</button><button className="button button-secondary" type="button" onClick={() => void remove(backup)}>删除</button></div></article>)}</div> : <p className="muted">还没有备份。</p>}
    <div className="backup-restore"><h3>从文件恢复</h3><p className="muted">先选择 .gz 备份文件进行验证。验证通过后才会显示恢复确认。</p><input ref={fileInput} type="file" accept=".gz,application/gzip" disabled={busy} onChange={event => void validate(event)} />
      {validation && <><dl className="facts-list"><div><dt>备份时间</dt><dd>{validation.createdAtUtc ? formatDate(validation.createdAtUtc) : "不可用"}</dd></div><div><dt>Server 版本</dt><dd>{validation.serverVersion ?? "不可用"}</dd></div><div><dt>Schema 版本</dt><dd>{validation.schemaVersion ?? "不可用"}（格式 {validation.formatVersion ?? "不可用"}）</dd></div><div><dt>数据库大小</dt><dd>{validation.databaseSizeBytes == null ? "不可用" : formatBytes(validation.databaseSizeBytes)}</dd></div><div><dt>完整性检查</dt><dd>{validation.databaseIntegrity ? "通过" : "未通过"}</dd></div><div><dt>MasterKey 兼容</dt><dd>{validation.masterKeyCompatible ? "兼容" : "不兼容"}</dd></div></dl>
        {validation.error && <Notice kind="error">{validation.error}</Notice>}
        {validation.valid && <div className="restore-controls"><Notice>恢复将覆盖当前数据，Server 会重启，当前会话可能断开。请在下方输入 RESTORE 确认。</Notice><label>输入 RESTORE<input value={confirmation} onInput={event => setConfirmation(event.currentTarget.value)} autoComplete="off" /></label><button className="button button-primary" type="button" disabled={busy || confirmation !== "RESTORE"} onClick={() => void restore()}>恢复备份</button></div>}
      </>}
    </div>
  </section>;
}

type CertificateDraft = { name: string };

function CertificatePanel({ api, certificates, setCertificates, setError }: { api: ApiClient; certificates: Certificate[]; setCertificates: (value: Certificate[]) => void; setError: (value: string) => void }) {
  const empty: CertificateDraft = { name: "" };
  const [draft, setDraft] = useState<CertificateDraft>(empty);
  const certificatePem = useRef<HTMLTextAreaElement>(null);
  const privateKeyPem = useRef<HTMLTextAreaElement>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const change = (key: keyof CertificateDraft, value: string) => setDraft(current => ({ ...current, [key]: value }));
  const clear = () => { setDraft(empty); setEditing(null); if (certificatePem.current) certificatePem.current.value = ""; if (privateKeyPem.current) privateKeyPem.current.value = ""; };
  const submit = async (event: Event) => {
    event.preventDefault();
    setBusy(true);
    try {
      const body = { name: draft.name, certificatePem: certificatePem.current?.value ?? "", privateKeyPem: privateKeyPem.current?.value ?? "" };
      const certificate = editing ? await api.replaceCertificate(editing, body) : await api.createCertificate(body);
      setCertificates(editing ? certificates.map(item => item.id === certificate.id ? certificate : item) : [...certificates, certificate]);
      clear();
      setError(editing ? "证书已替换。" : "证书已上传。");
    } catch (reason) { setError(messageFor(reason, editing ? "无法替换证书" : "无法上传证书")); }
    finally { setBusy(false); }
  };
  const edit = (certificate: Certificate) => { setEditing(certificate.id); setDraft({ name: certificate.name }); };
  return <section className="card panel certificate-panel">
    <SectionTitle title="TLS 证书" description="集中管理证书，并在 Hysteria 2 服务中按 ID 选择。" />
    {certificates.length ? <div className="certificate-list">{certificates.map(certificate => <article className="certificate-row" key={certificate.id}><div><strong>{certificate.name}</strong><small>{certificate.subject} · {certificate.san.join(", ") || "无 SAN"}</small><small>有效期至 {formatDate(certificate.expiresAtUtc)} · 使用中 {certificate.usedBy} 个服务</small></div><button className="button button-secondary" type="button" onClick={() => edit(certificate)}>替换</button></article>)}</div> : <p className="muted">还没有上传证书。</p>}
    <form className="certificate-form" onSubmit={event => void submit(event)}>
      <h3>{editing ? "替换证书" : "上传证书"}</h3>
      <div className="form-grid"><label>名称<input required value={draft.name} onInput={event => change("name", event.currentTarget.value)} /></label><label>证书 PEM<textarea required ref={certificatePem} placeholder={editing ? "粘贴新的证书 PEM" : "-----BEGIN CERTIFICATE-----"} /></label><label>私钥 PEM<textarea required ref={privateKeyPem} placeholder={editing ? "粘贴新的私钥 PEM" : "-----BEGIN PRIVATE KEY-----"} /></label></div>
      <div className="row-actions"><button className="button button-primary" type="submit" disabled={busy}>{busy ? "提交中…" : editing ? "替换证书" : "上传证书"}</button>{editing && <button className="button button-secondary" type="button" onClick={clear}>取消</button>}</div>
    </form>
  </section>;
}

function OverviewPage({ api, setError }: PageProps) {
  const [health, setHealth] = useState<HealthSummary | null>(null);
  const [nodes, setNodes] = useState<Node[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const load = async () => {
    setLoading(true);
    setLoadError("");
    try {
      const [nextHealth, nextNodes] = await Promise.all([
        api.health(),
        api.nodes(),
      ]);
      setHealth(nextHealth);
      setNodes(nextNodes);
    } catch (reason) {
      setLoadError(messageFor(reason, "无法加载概览"));
    } finally {
      setLoading(false);
    }
  };
  useEffect(() => {
    void load();
  }, []);
  if (!loading && loadError)
    return (
      <Page
        title="基础设施概览"
        description="节点连通性、配置收敛和服务运行状态。"
      >
        <ErrorState message={loadError} retry={() => void load()} />
      </Page>
    );
  return (
    <Page
      title="基础设施概览"
      description="节点连通性、配置收敛和服务运行状态。"
      actions={
        <button
          className="button button-secondary"
          type="button"
          onClick={() => void load()}
        >
          刷新
        </button>
      }
    >
      {loading ? (
        <Loading />
      ) : health ? (
        <>
          <div className="stats-grid four">
            <Stat
              label="在线节点"
              value={`${health.counts.nodesOnline} / ${health.counts.nodesTotal}`}
              note="30 秒内有 Agent 上报"
            />
            <Stat
              label="运行服务"
              value={`${health.counts.servicesRunning} / ${health.counts.servicesTotal}`}
              note="最近观测为运行中"
            />
            <Stat
              label="配置漂移"
              value={String(health.counts.nodesDrifted)}
              note="期望与应用版本不同"
            />
            <Stat
              label="失败服务"
              value={String(health.counts.servicesFailed)}
              note="需要立即处理"
            />
          </div>
          <div className="overview-grid">
            <section className="card panel">
              <SectionTitle
                title="健康问题"
                description={`观测于 ${formatDate(health.observedAtUtc)}`}
              />
              {health.issues.length ? (
                <div className="issue-list">
                  {health.issues.map((issue) => (
                    <article
                      className="issue"
                      key={`${issue.kind}-${issue.nodeId}-${issue.serviceId}`}
                    >
                      <span className="danger-dot" />
                      <div>
                        <strong>
                          {issue.kind === "offline"
                            ? "节点离线"
                            : issue.kind === "revisionDrift"
                              ? "配置尚未收敛"
                              : "服务运行失败"}
                        </strong>
                        <p>
                          {issue.nodeDisplayName}
                          {issue.serviceName ? ` · ${issue.serviceName}` : ""}
                          {issue.errorMessage ? ` — ${issue.errorMessage}` : ""}
                        </p>
                      </div>
                    </article>
                  ))}
                </div>
              ) : (
                <Empty
                  title="运行状态良好"
                  description="当前没有离线、漂移或失败问题。"
                />
              )}
            </section>
            <section className="card panel">
              <SectionTitle title="节点快照" description="最近 Agent 状态" />
              {nodes.length ? (
                <div className="compact-list">
                  {nodes.map((node) => (
                    <NodeSummary key={node.id} node={node} />
                  ))}
                </div>
              ) : (
                <Empty
                  title="尚无节点"
                  description="前往节点页面创建并安装第一个 Agent。"
                  action={
                    <a className="button button-primary" href="#/nodes">
                      添加节点
                    </a>
                  }
                />
              )}
            </section>
          </div>
        </>
      ) : null}
    </Page>
  );
}

function NodesPage({ api, setError }: PageProps) {
  const [nodes, setNodes] = useState<Node[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [showCreate, setShowCreate] = useState(false);
  const [name, setName] = useState("");
  const [install, setInstall] = useState<{
    node: NodeIdentity;
    platform: "unix" | "powershell";
    command: string;
  } | null>(null);
  const [busy, setBusy] = useState(false);
  const load = async () => {
    setLoading(true);
    setLoadError("");
    try {
      setNodes(await api.nodes());
    } catch (reason) {
      setLoadError(messageFor(reason, "无法加载节点"));
    } finally {
      setLoading(false);
    }
  };
  useEffect(() => {
    void load();
  }, []);
  const create = async (event: Event) => {
    event.preventDefault();
    setBusy(true);
    try {
      const node = await api.createNode(name);
      setShowCreate(false);
      setName("");
      await load();
      await generate(node, "unix");
    } catch (reason) {
      setError(messageFor(reason, "无法创建节点"));
    } finally {
      setBusy(false);
    }
  };
  const generate = async (
    node: NodeIdentity,
    platform: "unix" | "powershell",
  ) => {
    setBusy(true);
    try {
      const result = await api.installCommand(node.id, platform);
      setInstall({ node, platform, command: result.command });
    } catch (reason) {
      setError(reason instanceof ApiError && reason.status === 400
        ? "无法生成安装命令。请使用 HTTPS 访问 Panel，并确认反向代理传递了 X-Forwarded-Proto。"
        : messageFor(reason, "无法生成安装命令"));
    } finally {
      setBusy(false);
    }
  };
  const check = async (node: Node) => {
    if (!node.agentId) return;
    setBusy(true);
    try {
      await api.healthCheck(node.agentId);
      setError("健康检查命令已排队，Agent 将在下次同步时执行。");
    } catch (reason) {
      setError(messageFor(reason, "无法排队健康检查"));
    } finally {
      setBusy(false);
    }
  };
  const updateable = nodes.filter(
    (node) =>
      node.agentUpdateStatus === "Available" ||
      node.agentUpdateStatus === "Failed",
  );
  const updateAll = async () => {
    if (
      !updateable.length ||
      !confirm(
        `${updateable.length} 个节点可更新到 ${updateable[0].latestAgentVersion}，继续吗？`,
      )
    )
      return;
    setBusy(true);
    try {
      const result = await api.updateAgents(updateable.map((node) => node.id));
      setError(
        `已请求更新 ${result.updatedNodes} 个节点到 ${result.version}。`,
      );
      await load();
    } catch (reason) {
      setError(messageFor(reason, "无法请求批量更新"));
    } finally {
      setBusy(false);
    }
  };
  if (!loading && loadError)
    return (
      <Page
        title="节点"
        description="创建机器记录、安装 Agent，并检查控制面连通性。"
      >
        <ErrorState message={loadError} retry={() => void load()} />
      </Page>
    );
  return (
    <Page
      title="节点"
      description="创建机器记录、安装 Agent，并检查控制面连通性。"
      actions={
        <>
          {updateable.length > 0 && (
            <button
              className="button button-secondary"
              type="button"
              disabled={busy}
              onClick={() => void updateAll()}
            >
              更新所有可更新 Agent
            </button>
          )}
          <button
            className="button button-primary"
            type="button"
            onClick={() => setShowCreate(true)}
          >
            添加节点
          </button>
        </>
      }
    >
      {loading ? (
        <Loading />
      ) : nodes.length ? (
        <section className="node-list">
          <div className="table-header">
            <span>节点</span>
            <span>Agent</span>
            <span>版本收敛</span>
            <span>最后上报</span>
            <span />
          </div>
          {nodes.map((node) => (
            <div
              className={
                node.agentId
                  ? "table-row node-table-row clickable"
                  : "table-row node-table-row"
              }
              key={node.id}
              role={node.agentId ? "link" : undefined}
              tabIndex={node.agentId ? 0 : undefined}
              onClick={() => {
                if (node.agentId) location.hash = `/nodes/${node.id}/overview`;
              }}
              onKeyDown={(event) => {
                if (
                  node.agentId &&
                  (event.key === "Enter" || event.key === " ")
                )
                  location.hash = `/nodes/${node.id}/overview`;
              }}
            >
              <NodeSummary node={node} />
              <span>
                {node.platform ?? "等待安装"}
                <small>
                  Agent {node.reportedVersion ?? "—"}
                  {node.agentUpdateStatus === "Available"
                    ? ` · 可更新至 ${node.latestAgentVersion}`
                    : node.desiredAgentVersion &&
                        node.reportedVersion !== node.desiredAgentVersion
                      ? ` · ${agentUpdateLabel(node)}`
                      : ""}
                </small>
              </span>
              <span>
                {node.appliedRevision === node.desiredRevision
                  ? "已收敛"
                  : `${node.appliedRevision ?? "—"} → ${node.desiredRevision}`}
              </span>
              <span>
                {node.lastSeenAt ? formatDate(node.lastSeenAt) : "从未"}
              </span>
              <div
                className="row-actions"
                onClick={(event) => event.stopPropagation()}
              >
                {!node.agentId && (
                  <button
                    className="button button-primary"
                    type="button"
                    onClick={() => void generate(node, "unix")}
                  >
                    安装 Agent
                  </button>
                )}
                {node.agentId && (
                  <details className="more-menu">
                    <summary aria-label={`${node.displayName} 更多操作`}>
                      •••
                    </summary>
                    <div>
                      <button
                        type="button"
                        disabled={busy}
                        onClick={() => void check(node)}
                      >
                        健康检查
                      </button>
                      <button
                        type="button"
                        onClick={() => void generate(node, "unix")}
                      >
                        重新安装
                      </button>
                    </div>
                  </details>
                )}
              </div>
            </div>
          ))}
        </section>
      ) : (
        <section className="card">
          <Empty
            title="创建第一个节点"
            description="节点代表一台运行 Agent 的机器；每个节点可以承载多个独立服务。"
            action={
              <button
                className="button button-primary"
                type="button"
                onClick={() => setShowCreate(true)}
              >
                添加节点
              </button>
            }
          />
        </section>
      )}
      {showCreate && (
        <Modal
          title="添加节点"
          description="创建记录后会立即引导安装 Agent。"
          close={() => setShowCreate(false)}
        >
          <form className="modal-form" onSubmit={(event) => void create(event)}>
            <label>
              节点名称
              <input
                autoFocus
                required
                maxLength={128}
                placeholder="例如：伦敦边缘节点"
                value={name}
                onInput={(event) => setName(event.currentTarget.value)}
              />
            </label>
            <footer>
              <button
                className="button button-secondary"
                type="button"
                onClick={() => setShowCreate(false)}
              >
                取消
              </button>
              <button className="button button-primary" disabled={busy}>
                创建并安装
              </button>
            </footer>
          </form>
        </Modal>
      )}
      {install && (
        <InstallModal
          value={install}
          busy={busy}
          close={() => setInstall(null)}
          change={(platform) => void generate(install.node, platform)}
          setError={setError}
        />
      )}
    </Page>
  );
}

function InstallModal({
  value,
  busy,
  close,
  change,
  setError,
}: {
  value: {
    node: NodeIdentity;
    platform: "unix" | "powershell";
    command: string;
  };
  busy: boolean;
  close: () => void;
  change: (platform: "unix" | "powershell") => void;
  setError: (value: string) => void;
}) {
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value.command);
      setError("安装命令已复制。它包含 15 分钟有效的一次性令牌，请勿分享。");
    } catch (reason) {
      setError(messageFor(reason, "无法复制命令"));
    }
  };
  return (
    <Modal
      title={`安装 ${value.node.displayName}`}
      description="在目标机器以管理员权限运行。命令含一次性注册凭据。"
      close={close}
    >
      <div className="segmented compact">
        <button
          type="button"
          className={value.platform === "unix" ? "active" : ""}
          onClick={() => change("unix")}
        >
          Linux / macOS
        </button>
        <button
          type="button"
          className={value.platform === "powershell" ? "active" : ""}
          onClick={() => change("powershell")}
        >
          Windows
        </button>
      </div>
      <pre className="command-box">{busy ? "正在生成…" : value.command}</pre>
      <Notice>命令只显示在这里；重新生成会签发新的 15 分钟一次性令牌。</Notice>
      <footer className="modal-actions">
        <button
          className="button button-secondary"
          type="button"
          onClick={close}
        >
          完成
        </button>
        <button
          className="button button-primary"
          type="button"
          disabled={busy}
          onClick={() => void copy()}
        >
          复制命令
        </button>
      </footer>
    </Modal>
  );
}

function NodeDetailPage({
  api,
  setError,
  nodeId,
  tab,
}: PageProps & { nodeId: string; tab: NodeTab }) {
  const [node, setNode] = useState<Node | null>(null);
  const [services, setServices] = useState<Service[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const load = async () => {
    setLoading(true);
    setLoadError("");
    try {
      const [nodes, nextServices] = await Promise.all([
        api.nodes(),
        api.services(nodeId),
      ]);
      setNode(nodes.find((item) => item.id === nodeId) ?? null);
      setServices(nextServices);
    } catch (reason) {
      setLoadError(messageFor(reason, "无法加载节点详情"));
    } finally {
      setLoading(false);
    }
  };
  useEffect(() => {
    void load();
  }, [nodeId]);
  if (!loading && (loadError || !node))
    return (
      <Page title="节点详情" description="节点可能不存在或暂时无法读取。">
        <ErrorState
          message={loadError || "找不到节点。"}
          retry={() => void load()}
        />
      </Page>
    );
  if (loading || !node)
    return (
      <Page title="节点详情" description="正在读取节点。">
        <Loading />
      </Page>
    );
  const tabs: { value: NodeTab; label: string }[] = [
    { value: "overview", label: "概览" },
    { value: "services", label: "服务" },
    { value: "network", label: "网络" },
    { value: "logs", label: "日志" },
    { value: "settings", label: "设置" },
  ];
  const content =
    tab === "services" ? (
      <ServicesPage api={api} setError={setError} node={node} />
    ) : tab === "network" ? (
      <NodeNetwork node={node} services={services} />
    ) : tab === "logs" ? (
      <NodeLogs services={services} />
    ) : tab === "settings" ? (
      <NodeSettings api={api} node={node} setError={setError} />
    ) : (
      <NodeOverview node={node} services={services} />
    );
  return (
    <Page
      eyebrow="节点详情"
      title={node.displayName}
      description={`${node.platform ?? "未知平台"} · Agent ${node.reportedVersion ?? "未上报"} · ${node.online ? "在线" : "离线"}`}
      actions={
        <>
          <a className="button button-secondary" href="#/nodes">
            返回节点
          </a>
          <button
            className="button button-secondary"
            type="button"
            onClick={() => void load()}
          >
            刷新
          </button>
        </>
      }
    >
      <nav className="detail-tabs" aria-label="节点详情">
        {tabs.map((item) => (
          <a
            key={item.value}
            className={tab === item.value ? "active" : ""}
            href={`#/nodes/${node.id}/${item.value}`}
          >
            {item.label}
          </a>
        ))}
      </nav>
      {content}
    </Page>
  );
}

function NodeOverview({ node, services }: { node: Node; services: Service[] }) {
  const metrics = node.metrics;
  return (
    <>
      <div className="stats-grid node-stats">
        <Stat
          label="CPU"
          value={metrics ? `${metrics.cpuUsagePercent.toFixed(1)}%` : "无数据"}
          note="Agent 主机采样"
        />
        <Stat
          label="RAM"
          value={
            metrics
              ? `${formatBytes(usedBytes(metrics.memoryTotalBytes, metrics.memoryAvailableBytes))} / ${formatBytes(metrics.memoryTotalBytes)}`
              : "无数据"
          }
          note="已用 / 总量"
        />
        <Stat
          label="磁盘"
          value={
            metrics
              ? `${formatBytes(usedBytes(metrics.diskTotalBytes, metrics.diskAvailableBytes))} / ${formatBytes(metrics.diskTotalBytes)}`
              : "无数据"
          }
          note="已用 / 总量"
        />
        <Stat
          label="网络"
          value={
            metrics
              ? formatBytes(
                  metrics.networkUploadBytes + metrics.networkDownloadBytes,
                )
              : "无数据"
          }
          note={
            metrics
              ? `↑ ${formatBytes(metrics.networkUploadBytes)} · ↓ ${formatBytes(metrics.networkDownloadBytes)}`
              : "等待上报"
          }
        />
        <Stat
          label="运行时间"
          value={metrics ? formatUptime(metrics.uptimeSeconds) : "无数据"}
          note={metrics ? formatDate(metrics.observedAt) : "等待上报"}
        />
        <Stat
          label="服务"
          value={String(services.length)}
          note={`${services.filter((service) => service.runtime?.status === 4).length} 个运行中`}
        />
      </div>
      <section className="card panel">
        <SectionTitle title="节点状态" description="Agent 与配置同步信息" />
        <dl className="facts-list">
          <div>
            <dt>Agent 状态</dt>
            <dd>{node.online ? "在线" : "离线"}</dd>
          </div>
          <div>
            <dt>最后上报</dt>
            <dd>{node.lastSeenAt ? formatDate(node.lastSeenAt) : "从未"}</dd>
          </div>
          <div>
            <dt>配置版本</dt>
            <dd>
              {node.appliedRevision ?? "—"} / {node.desiredRevision}
            </dd>
          </div>
        </dl>
      </section>
    </>
  );
}

function NodeNetwork({ node, services }: { node: Node; services: Service[] }) {
  return (
    <div className="detail-grid">
      <section className="card panel">
        <SectionTitle title="节点网络" description="Agent 累计主机流量" />
        <dl className="facts-list">
          <div>
            <dt>上传</dt>
            <dd>{formatBytes(node.metrics?.networkUploadBytes ?? 0)}</dd>
          </div>
          <div>
            <dt>下载</dt>
            <dd>{formatBytes(node.metrics?.networkDownloadBytes ?? 0)}</dd>
          </div>
        </dl>
      </section>
      <section className="card panel">
        <SectionTitle title="服务流量" description="最近运行时上报" />
        <dl className="facts-list">
          {services.map((service) => (
            <div key={service.id}>
              <dt>{service.name}</dt>
              <dd>
                ↑ {formatBytes(service.runtime?.trafficUploadBytes ?? 0)} · ↓{" "}
                {formatBytes(service.runtime?.trafficDownloadBytes ?? 0)}
              </dd>
            </div>
          ))}
        </dl>
        {!services.length && <p className="muted">此节点没有服务。</p>}
      </section>
    </div>
  );
}
function NodeLogs({ services }: { services: Service[] }) {
  const errors = services.filter((service) => service.runtime?.errorMessage);
  return (
    <section className="card panel">
      <SectionTitle
        title="节点日志"
        description="完整诊断日志可在服务页按实例安全收集"
      />
      {errors.length ? (
        <div className="issue-list">
          {errors.map((service) => (
            <article className="issue" key={service.id}>
              <span className="danger-dot" />
              <div>
                <strong>{service.name}</strong>
                <p>{service.runtime?.errorMessage}</p>
              </div>
            </article>
          ))}
        </div>
      ) : (
        <Empty
          title="没有运行错误"
          description="前往服务标签可对具体实例发起受限日志收集。"
          action={
            <a
              className="button button-primary"
              href={`${location.hash.replace(/\/logs$/, "/services")}`}
            >
              查看服务
            </a>
          }
        />
      )}
    </section>
  );
}
function NodeSettings({
  api,
  node,
  setError,
}: {
  api: ApiClient;
  node: Node;
  setError: (value: string) => void;
}) {
  const check = async () => {
    if (!node.agentId) return;
    try {
      await api.healthCheck(node.agentId);
      setError("健康检查命令已排队。");
    } catch (reason) {
      setError(messageFor(reason, "无法排队健康检查"));
    }
  };
  const reinstall = async () => {
    try {
      const result = await api.installCommand(node.id, "unix");
      await navigator.clipboard.writeText(result.command);
      setError("重新安装命令已复制。");
    } catch (reason) {
      setError(reason instanceof ApiError && reason.status === 400
        ? "无法生成重新安装命令。请使用 HTTPS 访问 Panel，并确认反向代理传递了 X-Forwarded-Proto。"
        : messageFor(reason, "无法生成重新安装命令"));
    }
  };
  const update = async () => {
    try {
      const result = await api.updateAgent(node.id);
      setError(`已请求更新到 ${result.version}。`);
    } catch (reason) {
      setError(messageFor(reason, "无法请求 Agent 更新"));
    }
  };
  const policy = async (enabled: boolean) => {
    try {
      await api.setAgentUpdatePolicy(node.id, enabled ? "Auto" : "Manual");
      setError(`Agent 自动更新已${enabled ? "开启" : "关闭"}。`);
    } catch (reason) {
      setError(messageFor(reason, "无法修改自动更新策略"));
    }
  };
  const canUpdate =
    node.agentUpdateStatus === "Available" ||
    node.agentUpdateStatus === "Failed";
  return (
    <div className="detail-grid">
      <section className="card panel">
        <SectionTitle title="Agent 更新" description={agentUpdateLabel(node)} />
        <dl className="facts-list">
          <div>
            <dt>当前版本</dt>
            <dd>{node.reportedVersion ?? "未上报"}</dd>
          </div>
          <div>
            <dt>最新版本</dt>
            <dd>{node.latestAgentVersion ?? "暂不可用"}</dd>
          </div>
          <div>
            <dt>目标版本</dt>
            <dd>{node.desiredAgentVersion ?? "无"}</dd>
          </div>
        </dl>
        {node.agentUpdateError && (
          <Notice kind="error">更新失败：{node.agentUpdateError}</Notice>
        )}
        <div className="section-toolbar update-controls">
          <label className="switch">
            <input
              type="checkbox"
              checked={node.agentUpdatePolicy === "Auto"}
              onChange={(event) => void policy(event.currentTarget.checked)}
            />
            <span />
            自动更新 Agent
          </label>
          <button
            className="button button-primary"
            type="button"
            disabled={!canUpdate}
            onClick={() => void update()}
          >
            {node.agentUpdateStatus === "Failed"
              ? "重试更新"
              : `更新到 ${node.latestAgentVersion ?? "最新版"}`}
          </button>
        </div>
      </section>
      <section className="card panel">
        <SectionTitle title="Agent 设置" description="低频维护操作" />
        <div className="row-actions">
          <button
            className="button button-secondary"
            type="button"
            disabled={!node.agentId}
            onClick={() => void check()}
          >
            运行健康检查
          </button>
          <button
            className="button button-secondary"
            type="button"
            onClick={() => void reinstall()}
          >
            复制重新安装命令
          </button>
        </div>
      </section>
    </div>
  );
}

function ServicesPage({ api, setError, node }: PageProps & { node: Node }) {
  const nodeId = node.id;
  const [services, setServices] = useState<Service[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [editor, setEditor] = useState<{ service: Service | null } | null>(
    null,
  );
  const [selected, setSelected] = useState<string[]>([]);
  const loadServices = async (id: string, signal?: AbortSignal) => {
    setLoading(true);
    setLoadError("");
    try {
      const next = await api.services(id, signal);
      setServices(
        node.online
          ? next
          : next.map((service) => ({ ...service, runtime: null })),
      );
      setSelected([]);
    } catch (reason) {
      if (!(reason instanceof DOMException && reason.name === "AbortError"))
        setLoadError(messageFor(reason, "无法加载服务"));
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  };
  useEffect(() => {
    const controller = new AbortController();
    setServices([]);
    void loadServices(nodeId, controller.signal);
    return () => controller.abort();
  }, [nodeId]);
  const save = async (form: ServiceForm, service: Service | null) => {
    const payload = payloadFor(form, backendFor(form.backendType));
    const path = `/api/admin/v1/nodes/${nodeId}/services${service ? `/${service.id}` : ""}`;
    const body = service
      ? {
          name: payload.name,
          backendVersion: payload.backendVersion,
          enabled: service.enabled,
          configSchemaVersion: 1,
          configJson: payload.configJson,
        }
      : payload;
    await api.request(path, {
      method: service ? "PUT" : "POST",
      body: JSON.stringify(body),
    });
    setEditor(null);
    await loadServices(nodeId);
  };
  const remove = async (service: Service) => {
    if (!confirm(`确定删除“${service.name}”？Agent 将停止并清理此实例。`))
      return;
    try {
      await api.request(
        `/api/admin/v1/nodes/${nodeId}/services/${service.id}`,
        { method: "DELETE" },
      );
      await loadServices(nodeId);
    } catch (reason) {
      setError(messageFor(reason, "无法删除服务"));
    }
  };
  const enabled = async (service: Service, value: boolean) => {
    try {
      await api.request(
        `/api/admin/v1/nodes/${nodeId}/services/${service.id}/enabled`,
        { method: "PUT", body: JSON.stringify({ enabled: value }) },
      );
      await loadServices(nodeId);
    } catch (reason) {
      setError(messageFor(reason, "无法更新服务状态"));
    }
  };
  const batch = async (value: boolean) => {
    try {
      await api.request("/api/admin/v1/services/batch-enabled", {
        method: "POST",
        body: JSON.stringify({
          items: selected.map((serviceId) => ({
            nodeId,
            serviceId,
            enabled: value,
          })),
        }),
      });
      await loadServices(nodeId);
    } catch (reason) {
      setError(messageFor(reason, "无法批量更新服务"));
    }
  };
  if (!loading && loadError)
    return (
      <ErrorState message={loadError} retry={() => void loadServices(nodeId)} />
    );
  return (
    <section className="node-services">
      <div className="section-toolbar">
        <div>
          <strong>{services.length} 个服务实例</strong>
          <span>服务只在当前节点范围内管理。</span>
        </div>
        <button
          className="button button-primary"
          type="button"
          onClick={() => setEditor({ service: null })}
        >
          添加服务
        </button>
      </div>
      {selected.length > 0 && (
        <div className="selection-bar">
          <strong>已选择 {selected.length} 项</strong>
          <button
            className="button button-secondary"
            type="button"
            onClick={() => void batch(true)}
          >
            启用
          </button>
          <button
            className="button button-secondary"
            type="button"
            onClick={() => void batch(false)}
          >
            停用
          </button>
          <button
            className="link-button"
            type="button"
            onClick={() => setSelected([])}
          >
            取消选择
          </button>
        </div>
      )}
      {loading ? (
        <Loading />
      ) : services.length ? (
        <section className="card panel">
          <SectionTitle
            title={node.displayName}
            description={`${services.length} 个独立服务实例`}
          />
          <div className="service-list">
            {services.map((service) => (
              <ServiceCard
                key={service.id}
                api={api}
                nodeId={nodeId}
                service={service}
                selected={selected.includes(service.id)}
                select={() =>
                  setSelected((current) =>
                    current.includes(service.id)
                      ? current.filter((id) => id !== service.id)
                      : [...current, service.id],
                  )
                }
                edit={() => setEditor({ service })}
                remove={() => void remove(service)}
              enabled={(value) => void enabled(service, value)}
              refresh={() => loadServices(nodeId)}
              setError={setError}
              />
            ))}
          </div>
        </section>
      ) : (
        <section className="card">
          <Empty
            title="这个节点还没有服务"
            description="添加 Hysteria 2、Xray、Mihomo 或 sing-box 实例。"
            action={
              <button
                className="button button-primary"
                type="button"
                onClick={() =>
                  setEditor({ service: null })
                }
              >
                添加服务
              </button>
            }
          />
        </section>
      )}
      {editor && (
        <ServiceEditor
          service={editor.service}
          api={api}
          save={save}
          close={() => setEditor(null)}
          setError={setError}
        />
      )}
    </section>
  );
}

function ServiceCard({
  api,
  nodeId,
  service,
  selected,
  select,
  edit,
  remove,
  enabled,
  refresh,
  setError,
}: {
  api: ApiClient;
  nodeId: string;
  service: Service;
  selected: boolean;
  select: () => void;
  edit: () => void;
  remove: () => void;
  enabled: (value: boolean) => void;
  refresh: () => Promise<void>;
  setError: (value: string) => void;
}) {
  const backend = backendFor(service.backendType);
  const facts = serviceFacts(service);
  const status = service.runtime?.status ?? 0;
  const [endpoint, setEndpoint] = useState<PublicEndpoint | null>(null);
  const [endpointLoading, setEndpointLoading] = useState(false);
  const [endpointOpen, setEndpointOpen] = useState(false);
  const [diagnostics, setDiagnostics] = useState<ServiceDiagnostic[] | null>(
    null,
  );
  const [diagnosticsOpen, setDiagnosticsOpen] = useState(false);
  const [diagnosticsLoading, setDiagnosticsLoading] = useState(false);
  const [updatingBackend, setUpdatingBackend] = useState(false);
  const openEndpoint = async () => {
    const next = !endpointOpen;
    setEndpointOpen(next);
    if (!next || endpoint) return;
    setEndpointLoading(true);
    try {
      setEndpoint((await api.endpoint(nodeId, service.id)) ?? null);
    } catch (reason) {
      setError(messageFor(reason, "无法加载公网端点"));
    } finally {
      setEndpointLoading(false);
    }
  };
  const saveEndpoint = async (event: Event) => {
    event.preventDefault();
    if (!endpoint) return;
    try {
      const result = await api.request<PublicEndpoint>(
        `/api/admin/v1/nodes/${nodeId}/services/${service.id}/public-endpoint`,
        { method: "PUT", body: JSON.stringify(endpoint) },
      );
      setEndpoint(result);
      setError("公网端点已保存。");
    } catch (reason) {
      setError(messageFor(reason, "无法保存公网端点"));
    }
  };
  const loadDiagnostics = async () => {
    setDiagnosticsLoading(true);
    try {
      setDiagnostics(await api.diagnostics(nodeId, service.id));
    } catch (reason) {
      setError(messageFor(reason, "无法加载诊断记录"));
    } finally {
      setDiagnosticsLoading(false);
    }
  };
  const openDiagnostics = () => {
    setDiagnosticsOpen(true);
    void loadDiagnostics();
  };
  const collectLogs = async () => {
    setDiagnosticsLoading(true);
    try {
      await api.collectLogs(nodeId, service.id);
      setError("日志收集已排队，Agent 将在下次同步时返回结果。");
      await loadDiagnostics();
    } catch (reason) {
      setError(messageFor(reason, "无法排队日志收集"));
    } finally {
      setDiagnosticsLoading(false);
    }
  };
  const updateBackend = async () => {
    if (
      !service.latestBackendVersion ||
      !confirm(
        `将“${service.name}”更新到 ${service.latestBackendVersion}？仅此服务会重启，失败时自动回滚。`,
      )
    )
      return;
    setUpdatingBackend(true);
    try {
      const result = await api.updateBackend(nodeId, service.id);
      setError(`已请求更新 ${service.name} 到 ${result.version}。`);
      await refresh();
    } catch (reason) {
      setError(messageFor(reason, "无法请求后端更新"));
    } finally {
      setUpdatingBackend(false);
    }
  };
  const backendPolicy = async (enabled: boolean) => {
    try {
      await api.setBackendUpdatePolicy(
        nodeId,
        service.id,
        enabled ? "Auto" : "Manual",
      );
      setError(`后端自动更新已${enabled ? "开启" : "关闭"}。`);
      await refresh();
    } catch (reason) {
      setError(messageFor(reason, "无法修改后端更新策略"));
    }
  };
  return (
    <article className={`service-card ${selected ? "selected" : ""}`}>
      <div className="service-card-main">
        <label className="check">
          <input type="checkbox" checked={selected} onChange={select} />
          <span className="sr-only">选择 {service.name}</span>
        </label>
        <span className="backend-mark">{backend.badge}</span>
        <div className="service-identity">
          <div>
            <h3>{service.name}</h3>
            <StatusBadge status={status} enabled={service.enabled} />
          </div>
          <p>{backend.name}</p>
          <small>
            {backend.core} · 当前 {service.runtime?.backendVersion ?? "未知"} · 目标 {service.backendVersion} · 最新 {service.latestBackendVersion ?? "未知"}
          </small>
        </div>
        <div className="row-actions">
          <button
            className="button button-secondary"
            type="button"
            onClick={edit}
          >
            编辑
          </button>
          <button
            className="more-button"
            type="button"
            title="删除服务"
            onClick={remove}
          >
            删除
          </button>
        </div>
      </div>
      <div className="service-facts">
        {facts.map((fact, index) => (
          <span key={`${index}-${fact}`}>
            <b>{["监听地址", "协议参数", "传输能力"][index]}</b>
            {fact}
          </span>
        ))}
      </div>
      {service.runtime?.errorMessage && (
        <div className="service-error">
          <strong>运行错误</strong>
          <span>{service.runtime.errorMessage}</span>
        </div>
      )}
      <div className="service-footer">
        <div className="row-actions">
          <button
            className="button button-secondary"
            type="button"
            disabled={updatingBackend || !service.latestBackendVersion || service.backendVersion === service.latestBackendVersion}
            onClick={() => void updateBackend()}
          >
            {updatingBackend ? "准备更新…" : `更新到 ${service.latestBackendVersion ?? "最新版"}`}
          </button>
        </div>
        <label className="switch">
          <input
            type="checkbox"
            checked={service.backendUpdatePolicy === "Auto"}
            onChange={(event) => void backendPolicy(event.currentTarget.checked)}
          />
          <span />自动更新后端
        </label>
      </div>
      <div className="service-footer">
        <div className="row-actions">
          <button
            className="link-button"
            type="button"
            onClick={() => void openEndpoint()}
          >
            {endpointOpen ? "收起公网端点" : "配置公网端点"}
          </button>
          <button
            className="link-button"
            type="button"
            onClick={openDiagnostics}
          >
            诊断日志
          </button>
        </div>
        <label className="switch">
          <input
            type="checkbox"
            checked={service.enabled}
            onChange={(event) => enabled(event.currentTarget.checked)}
          />
          <span />
          {service.enabled ? "已启用" : "已停用"}
        </label>
      </div>
      {endpointOpen && (
        <form
          className="endpoint-form"
          onSubmit={(event) => void saveEndpoint(event)}
        >
          {endpointLoading ? (
            <Loading label="加载端点…" />
          ) : (
            <>
              {!endpoint && (
                <button
                  className="button button-secondary"
                  type="button"
                  onClick={() =>
                    setEndpoint({ host: "", port: 443, tlsServerName: null })
                  }
                >
                  添加公网端点
                </button>
              )}
              {endpoint && (
                <>
                  <label>
                    公网主机
                    <input
                      required
                      value={endpoint.host}
                      onInput={(event) =>
                        setEndpoint({
                          ...endpoint,
                          host: event.currentTarget.value,
                        })
                      }
                    />
                  </label>
                  <label>
                    端口
                    <input
                      required
                      type="number"
                      min="1"
                      max="65535"
                      value={endpoint.port}
                      onInput={(event) =>
                        setEndpoint({
                          ...endpoint,
                          port: Number(event.currentTarget.value),
                        })
                      }
                    />
                  </label>
                  <label>
                    TLS 服务器名
                    <input
                      value={endpoint.tlsServerName ?? ""}
                      onInput={(event) =>
                        setEndpoint({
                          ...endpoint,
                          tlsServerName: event.currentTarget.value || null,
                        })
                      }
                    />
                  </label>
                  <button className="button button-primary">保存端点</button>
                </>
              )}
            </>
          )}
        </form>
      )}
      {diagnosticsOpen && (
        <DiagnosticsModal
          service={service}
          records={diagnostics}
          loading={diagnosticsLoading}
          close={() => setDiagnosticsOpen(false)}
          refresh={loadDiagnostics}
          collect={collectLogs}
        />
      )}
    </article>
  );
}

function DiagnosticsModal({
  service,
  records,
  loading,
  close,
  refresh,
  collect,
}: {
  service: Service;
  records: ServiceDiagnostic[] | null;
  loading: boolean;
  close: () => void;
  refresh: () => Promise<void>;
  collect: () => Promise<void>;
}) {
  const label = (status: ServiceDiagnostic["status"]) =>
    status === "Succeeded"
      ? "收集成功"
      : status === "Failed"
        ? "收集失败"
        : status === "Expired"
          ? "已过期"
          : "等待 Agent";
  return (
    <Modal
      title={`${service.name} · 诊断日志`}
      description="仅收集 Agent 内存中最近的受管进程 stdout/stderr；不读取任意文件。"
      close={close}
    >
      <div className="diagnostic-actions">
        <button
          className="button button-secondary"
          type="button"
          disabled={loading}
          onClick={() => void refresh()}
        >
          刷新
        </button>
        <button
          className="button button-primary"
          type="button"
          disabled={
            loading ||
            records?.some(
              (record) =>
                record.status === "Pending" || record.status === "Running",
            )
          }
          onClick={() => void collect()}
        >
          收集最新日志
        </button>
      </div>
      {loading && records === null ? (
        <Loading />
      ) : records?.length ? (
        <div className="diagnostic-list">
          {records.map((record) => (
            <article key={record.commandId}>
              <header>
                <strong>{label(record.status)}</strong>
                <time>
                  {new Date(record.createdAtUtc).toLocaleString("zh-CN")}
                </time>
              </header>
              {record.output !== null && (
                <pre>{record.output || "日志缓冲区为空。"}</pre>
              )}
              {record.status === "Expired" && (
                <Notice>Agent 未在有效期内领取或完成命令，可重新收集。</Notice>
              )}
              {record.errorMessage && (
                <Notice kind="error">{record.errorMessage}</Notice>
              )}
            </article>
          ))}
        </div>
      ) : (
        <Empty
          title="还没有诊断记录"
          description="发起收集后，Agent 会通过下一次出站同步返回有界日志。"
        />
      )}
      <footer className="modal-actions">
        <button
          className="button button-secondary"
          type="button"
          onClick={close}
        >
          关闭
        </button>
      </footer>
    </Modal>
  );
}

function ServiceEditor({
  service,
  api,
  save,
  close,
  setError,
}: {
  service: Service | null;
  api: ApiClient;
  save: (form: ServiceForm, service: Service | null) => Promise<void>;
  close: () => void;
  setError: (value: string) => void;
}) {
  const [definitions, setDefinitions] = useState<BackendDefinition[]>([]);
  const [form, setForm] = useState<ServiceForm | null>(null);
  const [certificates, setCertificates] = useState<Certificate[]>([]);
  const [busy, setBusy] = useState(false);
  const [generating, setGenerating] = useState(false);
  const generate = async (definition: BackendDefinition) => {
    setGenerating(true);
    try {
      const result = await api.generateBackendDefaults(definition.backendType);
      setForm((current) =>
        current && current.backendType === definition.backendType
          ? { ...current, values: { ...current.values, ...result.values } }
          : current,
      );
    } catch (reason) {
      setError(messageFor(reason, "无法生成默认密钥"));
    } finally {
      setGenerating(false);
    }
  };
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const all = await api.backends();
        if (cancelled) return;
        setBackendDefinitions(all);
        setDefinitions(all);
        const definition = service
          ? all.find((item) => item.backendType === service.backendType)
          : all[0];
        if (!definition) {
          setError("当前没有可用的后端定义。");
          return;
        }
        if (service) {
          setForm(formFor(service, definition));
        } else {
          setForm(emptyService(definition));
          void generate(definition);
        }
      } catch (reason) {
        setError(messageFor(reason, "无法加载后端定义"));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);
  useEffect(() => {
    if (form?.backendType !== "hysteria2") return;
    void api.certificates().then(setCertificates).catch(reason => setError(messageFor(reason, "无法加载证书")));
  }, [form?.backendType]);
  const change = (key: string, next: string) =>
    setForm((current) =>
      current
        ? { ...current, values: { ...current.values, [key]: next } }
        : current,
    );
  const switchBackend = (definition: BackendDefinition) => {
    setForm(emptyService(definition));
    void generate(definition);
  };
  const submit = async (event: Event) => {
    event.preventDefault();
    if (!form) return;
    setBusy(true);
    try {
      await save(form, service);
    } catch (reason) {
      setError(messageFor(reason, "无法保存服务"));
    } finally {
      setBusy(false);
    }
  };
  const saveTemplate = async () => {
    if (!form) return;
    setBusy(true);
    try {
      await api.request("/api/admin/v1/service-templates", {
        method: "POST",
        body: JSON.stringify(payloadFor(form, backendFor(form.backendType))),
      });
      setError("服务模板已保存。");
    } catch (reason) {
      setError(messageFor(reason, "无法保存模板"));
    } finally {
      setBusy(false);
    }
  };
  const renderField = (field: BackendField) => {
    if (!form) return null;
    if (field.kind === "fixed") {
      const display =
        field.fixedKind === "boolean"
          ? field.fixed === "true"
            ? "启用"
            : "禁用"
          : field.fixed;
      return (
        <div className="fixed-setting" key={field.key}>
          <span>{field.label}</span>
          <strong>{display}</strong>
        </div>
      );
    }
    if (field.kind === "certificate") {
      return (
        <label key={field.key}>
          证书
          <select
            required={field.required}
            value={form.values[field.key] ?? ""}
            onChange={(event) => change(field.key, event.currentTarget.value)}
          >
            <option value="">选择证书</option>
            {certificates.map((certificate) => (
              <option key={certificate.id} value={certificate.id}>
                {certificate.name} · 到期 {formatDate(certificate.expiresAtUtc)}
              </option>
            ))}
          </select>
        </label>
      );
    }
    if (field.kind === "select") {
      return (
        <label key={field.key}>
          {field.label}
          <select
            required={field.required}
            value={form.values[field.key] ?? ""}
            onChange={(event) => change(field.key, event.currentTarget.value)}
          >
            {field.options.map((option) => (
              <option key={option} value={option}>
                {option}
              </option>
            ))}
          </select>
        </label>
      );
    }
    const type =
      field.kind === "number" ? "number" : field.kind === "password" ? "password" : "text";
    const label =
      service && field.secret ? `${field.label}（保留当前占位值即可沿用）` : field.label;
    return (
      <label key={field.key}>
        {label}
        <input
          required={field.required}
          type={type}
          value={form.values[field.key] ?? ""}
          placeholder={field.placeholder ?? undefined}
          onInput={(event) => change(field.key, event.currentTarget.value)}
        />
      </label>
    );
  };
  if (!form || !definitions.length)
    return (
      <Modal
        title={service ? `编辑 ${service.name}` : "添加服务"}
        close={close}
      >
        <Loading />
      </Modal>
    );
  const definition = backendFor(form.backendType);
  const listenFields = definition.fields.filter(
    (field) => field.section === "listen",
  );
  const protocolFields = definition.fields.filter(
    (field) => field.section !== "listen",
  );
  const hasGenerated = protocolFields.some((field) => field.generate);
  return (
    <Modal
      title={service ? `编辑 ${service.name}` : "添加服务"}
      description={`${definition.core} · ${definition.protocol}`}
      close={close}
    >
      <form className="service-editor" onSubmit={(event) => void submit(event)}>
        {!service && (
          <fieldset>
            <legend>后端类型</legend>
            <div className="backend-picker">
              {definitions.map((item) => (
                <label
                  className={
                    form.backendType === item.backendType
                      ? "backend-option selected"
                      : "backend-option"
                  }
                  key={item.backendType}
                >
                  <input
                    type="radio"
                    checked={form.backendType === item.backendType}
                    onChange={() => switchBackend(item)}
                  />
                  <span className="backend-mark">{item.badge}</span>
                  <span>
                    <strong>{item.core}</strong>
                    <small>{item.protocol}</small>
                  </span>
                </label>
              ))}
            </div>
          </fieldset>
        )}
        <fieldset>
          <legend>基本与监听</legend>
          <div className="form-grid">
            <label>
              服务名称
              <input
                required
                value={form.name}
                onInput={(event) =>
                  setForm({ ...form, name: event.currentTarget.value })
                }
              />
            </label>
            <label>
              后端版本
              <input
                required
                value={form.version}
                onInput={(event) =>
                  setForm({ ...form, version: event.currentTarget.value })
                }
              />
            </label>
            {listenFields.map(renderField)}
          </div>
        </fieldset>
        <fieldset>
          <legend>协议配置</legend>
          {hasGenerated && (
            <div className="service-editor-actions">
              <button
                className="button button-secondary"
                type="button"
                disabled={busy || generating}
                onClick={() => void generate(definition)}
              >
                {generating ? "生成中…" : "重新生成默认密钥"}
              </button>
            </div>
          )}
          <div className="form-grid">{protocolFields.map(renderField)}</div>
        </fieldset>
        <footer>
          <div>
            {!service && (
              <button
                className="button button-secondary"
                type="button"
                disabled={busy}
                onClick={() => void saveTemplate()}
              >
                另存为模板
              </button>
            )}
          </div>
          <div className="row-actions">
            <button
              className="button button-secondary"
              type="button"
              onClick={close}
            >
              取消
            </button>
            <button
              className="button button-primary"
              disabled={busy || generating}
            >
              {busy ? "正在保存…" : "保存服务"}
            </button>
          </div>
        </footer>
      </form>
    </Modal>
  );
}

function TemplatesPage({ api, setError }: PageProps) {
  const [templates, setTemplates] = useState<Template[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const load = async () => {
    setLoading(true);
    setLoadError("");
    try {
      setTemplates(await api.templates());
    } catch (reason) {
      setLoadError(messageFor(reason, "无法加载模板"));
    } finally {
      setLoading(false);
    }
  };
  useEffect(() => {
    void load();
  }, []);
  const remove = async (template: Template) => {
    if (!confirm(`确定删除模板“${template.name}”？`)) return;
    try {
      await api.request(`/api/admin/v1/service-templates/${template.id}`, {
        method: "DELETE",
      });
      await load();
    } catch (reason) {
      setError(messageFor(reason, "无法删除模板"));
    }
  };
  if (!loading && loadError)
    return (
      <Page title="服务模板" description="维护可复用后端配置。">
        <ErrorState message={loadError} retry={() => void load()} />
      </Page>
    );
  return (
    <Page
      title="服务模板"
      description="维护可复用配置；服务实例请从具体节点的服务标签创建。"
    >
      {loading ? (
        <Loading />
      ) : templates.length ? (
        <div className="template-grid">
          {templates.map((template) => {
            const backend = backendFor(template.backendType);
            return (
              <article className="card template-card" key={template.id}>
                <div className="template-heading">
                  <span className="backend-mark">{backend.badge}</span>
                  <div>
                    <h2>{template.name}</h2>
                    <p>{backend.name}</p>
                  </div>
                </div>
                <div className="service-tags">
                  <span>
                    {backend.core} {template.backendVersion}
                  </span>
                  <span>{backend.protocol}</span>
                </div>
                <footer>
                  <button
                    className="more-button"
                    type="button"
                    onClick={() => void remove(template)}
                  >
                    删除
                  </button>
                </footer>
              </article>
            );
          })}
        </div>
      ) : (
        <section className="card">
          <Empty
            title="尚无模板"
            description="在节点详情的服务编辑器中选择“另存为模板”即可建立第一份复用配置。"
            action={
              <a className="button button-primary" href="#/nodes">
                前往节点
              </a>
            }
          />
        </section>
      )}
    </Page>
  );
}

function UsersPage({ api, setError }: PageProps) {
  const [users, setUsers] = useState<User[]>([]);
  const [services, setServices] = useState<ServiceRef[]>([]);
  const [usage, setUsage] = useState<Usage[]>([]);
  const [bindings, setBindings] = useState<Record<string, string[]>>({});
  const [access, setAccess] = useState<Record<string, UserServiceAccess[]>>({});
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState("");
  const [rotatingUserId, setRotatingUserId] = useState<string | null>(null);
  const [credentialAction, setCredentialAction] = useState("");
  const [editor, setEditor] = useState<{
    user: User | null;
    form: UserForm;
  } | null>(null);
  const [token, setToken] = useState<{
    username: string;
    value: string;
  } | null>(null);
  const rotationInFlight = useRef(false);
  const load = async () => {
    setLoading(true);
    setLoadError("");
    try {
      const [nextUsers, nodes, nextUsage] = await Promise.all([
        api.users(),
        api.nodes(),
        api.usage(),
      ]);
      const nested = await Promise.all(
        nodes.map(async (node) =>
          (await api.services(node.id)).map((service) => ({
            ...service,
            nodeId: node.id,
            nodeName: node.displayName,
          })),
        ),
      );
      const pairs = await Promise.all(
        nextUsers.map(
          async (user) =>
            [
              user.id,
              await api.request<string[]>(
                `/api/admin/v1/users/${user.id}/services`,
              ),
            ] as const,
        ),
      );
      const accessRows = await Promise.all(
        nextUsers.map(
          async (user) =>
            [
              user.id,
              await api.request<UserServiceAccess[]>(
                `/api/admin/v1/users/${user.id}/service-access`,
              ),
            ] as const,
        ),
      );
      setUsers(nextUsers);
      setServices(nested.flat());
      setUsage(nextUsage);
      setBindings(Object.fromEntries(pairs));
      setAccess(Object.fromEntries(accessRows));
    } catch (reason) {
      setLoadError(messageFor(reason, "无法加载用户"));
    } finally {
      setLoading(false);
    }
  };
  useEffect(() => {
    void load();
  }, []);
  const save = async (event: Event) => {
    event.preventDefault();
    if (!editor) return;
    try {
      const expiresAtUtc = editor.form.expiresAtUtc
        ? new Date(editor.form.expiresAtUtc).toISOString()
        : null;
      if (editor.user) {
        await api.request(`/api/admin/v1/users/${editor.user.id}`, {
          method: "PUT",
          body: JSON.stringify({
            ...editor.form,
            password: editor.form.password || null,
            trafficLimitBytes: editor.form.trafficLimitBytes
              ? Number(editor.form.trafficLimitBytes)
              : null,
            expiresAtUtc,
          }),
        });
      } else {
        const result = await api.createUser({
          ...editor.form,
          expiresAtUtc: expiresAtUtc ?? "",
        });
        setToken({
          username: result.user.username,
          value: result.subscriptionToken,
        });
      }
      setEditor(null);
      await load();
    } catch (reason) {
      setError(messageFor(reason, "无法保存用户"));
    }
  };
  const remove = async (user: User) => {
    if (!confirm(`确定删除用户“${user.username}”？其会话与订阅将失效。`))
      return;
    try {
      await api.request(`/api/admin/v1/users/${user.id}`, { method: "DELETE" });
      await load();
    } catch (reason) {
      setError(messageFor(reason, "无法删除用户"));
    }
  };
  const rotate = async (user: User) => {
    if (
      rotationInFlight.current ||
      !confirm(`轮换“${user.username}”的订阅令牌？旧链接会立即失效。`)
    )
      return;
    rotationInFlight.current = true;
    setRotatingUserId(user.id);
    try {
      const result = await api.request<{ subscriptionToken: string }>(
        `/api/admin/v1/users/${user.id}/subscription-token/rotate`,
        { method: "POST" },
      );
      setToken({ username: user.username, value: result.subscriptionToken });
    } catch (reason) {
      setError(messageFor(reason, "无法轮换令牌"));
    } finally {
      rotationInFlight.current = false;
      setRotatingUserId(null);
    }
  };
  const toggle = async (userId: string, service: ServiceRef) => {
    const bound = bindings[userId]?.includes(service.id);
    if (!bound && service.backendType !== "xray") {
      setError("该后端当前不支持独立用户身份，不能授予共享凭据。");
      return;
    }
    try {
      await api.request(
        `/api/admin/v1/users/${userId}/services/${service.id}`,
        { method: bound ? "DELETE" : "PUT" },
      );
      await load();
    } catch (reason) {
      setError(messageFor(reason, "无法更新授权"));
    }
  };
  const rotateCredential = async (user: User, service: ServiceRef) => {
    const key = `${user.id}:${service.id}`;
    if (
      !confirm(
        `轮换“${user.username}”在“${service.name}”的代理凭据？旧 UUID 会在下一次 Agent 同步后失效。`,
      )
    )
      return;
    setCredentialAction(key);
    try {
      await api.request(
        `/api/admin/v1/users/${user.id}/services/${service.id}/credential/rotate`,
        { method: "POST" },
      );
      await load();
      setError("代理凭据已轮换。");
    } catch (reason) {
      setError(messageFor(reason, "无法轮换代理凭据"));
    } finally {
      setCredentialAction("");
    }
  };
  if (!loading && loadError)
    return (
      <Page
        title="用户与访问"
        description="管理账户、跨节点服务授权、流量限制和订阅令牌。"
      >
        <ErrorState message={loadError} retry={() => void load()} />
      </Page>
    );
  return (
    <Page
      title="用户与访问"
      description="管理账户、跨节点服务授权、流量限制和订阅令牌。"
      actions={
        <button
          className="button button-primary"
          type="button"
          onClick={() => setEditor({ user: null, form: emptyUser })}
        >
          添加用户
        </button>
      }
    >
      {loading ? (
        <Loading />
      ) : users.length ? (
        <div className="user-grid">
          {users.map((user) => {
            const total = usage
              .filter((row) => row.userId === user.id)
              .reduce(
                (sum, row) => sum + row.uploadBytes + row.downloadBytes,
                0,
              );
            return (
              <article className="card user-card" key={user.id}>
                <header>
                  <div className="avatar">
                    {user.username.slice(0, 1).toUpperCase()}
                  </div>
                  <div>
                    <h2>{user.username}</h2>
                    <p>
                      {user.role === "Admin" ? "管理员" : "普通用户"} ·{" "}
                      {user.enabled ? "已启用" : "已停用"}
                    </p>
                  </div>
                  <div className="row-actions">
                    <button
                      className="button button-secondary"
                      type="button"
                      onClick={() =>
                        setEditor({
                          user,
                          form: {
                            username: user.username,
                            password: "",
                            role: user.role,
                            enabled: user.enabled,
                            trafficLimitBytes:
                              user.trafficLimitBytes?.toString() ?? "",
                            expiresAtUtc: toLocalDate(user.expiresAtUtc),
                          },
                        })
                      }
                    >
                      编辑
                    </button>
                    <button
                      className="more-button"
                      type="button"
                      onClick={() => void remove(user)}
                    >
                      删除
                    </button>
                  </div>
                </header>
                <div className="user-facts">
                  <span>
                    <b>已用流量</b>
                    {formatBytes(total)}
                  </span>
                  <span>
                    <b>流量上限</b>
                    {user.trafficLimitBytes === null
                      ? "不限"
                      : formatBytes(user.trafficLimitBytes)}
                  </span>
                  <span>
                    <b>有效期</b>
                    {user.expiresAtUtc ? formatDate(user.expiresAtUtc) : "永久"}
                  </span>
                </div>
                <details>
                  <summary>
                    服务权限 <small>{bindings[user.id]?.length ?? 0} 项</small>
                  </summary>
                  <div className="grant-list">
                    {services.length ? (
                      services.map((service) => {
                        const row = access[user.id]?.find(
                          (item) => item.serviceId === service.id,
                        );
                        const bytes = usage
                          .filter(
                            (item) =>
                              item.userId === user.id &&
                              item.serviceId === service.id,
                          )
                          .reduce(
                            (sum, item) =>
                              sum + item.uploadBytes + item.downloadBytes,
                            0,
                          );
                        return (
                          <div className="grant-option" key={service.id}>
                            <input
                              type="checkbox"
                              aria-label={`${service.name} 服务权限`}
                              checked={
                                bindings[user.id]?.includes(service.id) ?? false
                              }
                              disabled={
                                service.backendType !== "xray" &&
                                !bindings[user.id]?.includes(service.id)
                              }
                              onChange={() => void toggle(user.id, service)}
                            />
                            <span>
                              <strong>{service.name}</strong>
                              <small>
                                {service.nodeName} ·{" "}
                                {backendFor(service.backendType).core} ·{" "}
                                {row?.credentialStatus === "Active"
                                  ? "凭据有效"
                                  : row?.credentialStatus === "Revoked"
                                    ? "凭据已吊销"
                                    : service.backendType === "xray"
                                      ? "未授权"
                                      : "不支持独立身份"}{" "}
                                ·{" "}
                                {row?.perUserTraffic
                                  ? formatBytes(bytes)
                                  : "不支持用户流量统计"}
                              </small>
                            </span>
                            {row?.credentialStatus === "Active" && (
                              <button
                                className="link-button"
                                type="button"
                                disabled={credentialAction !== ""}
                                onClick={() =>
                                  void rotateCredential(user, service)
                                }
                              >
                                {credentialAction === `${user.id}:${service.id}`
                                  ? "轮换中…"
                                  : "轮换凭据"}
                              </button>
                            )}
                          </div>
                        );
                      })
                    ) : (
                      <p className="muted">尚无可授权服务。</p>
                    )}
                  </div>
                </details>
                <footer>
                  <button
                    className="link-button"
                    type="button"
                    disabled={rotatingUserId !== null}
                    onClick={() => void rotate(user)}
                  >
                    {rotatingUserId === user.id ? "正在轮换…" : "轮换订阅令牌"}
                  </button>
                </footer>
              </article>
            );
          })}
        </div>
      ) : (
        <section className="card">
          <Empty
            title="尚无用户"
            description="创建账户后可授予一个或多个节点上的服务。"
            action={
              <button
                className="button button-primary"
                type="button"
                onClick={() => setEditor({ user: null, form: emptyUser })}
              >
                添加用户
              </button>
            }
          />
        </section>
      )}
      {editor && (
        <UserEditor
          value={editor}
          setValue={setEditor}
          save={save}
          close={() => setEditor(null)}
        />
      )}
      {token && (
        <TokenModal
          username={token.username}
          token={token.value}
          close={() => setToken(null)}
          setError={setError}
        />
      )}
    </Page>
  );
}

function UserEditor({
  value,
  setValue,
  save,
  close,
}: {
  value: { user: User | null; form: UserForm };
  setValue: (value: { user: User | null; form: UserForm }) => void;
  save: (event: Event) => Promise<void>;
  close: () => void;
}) {
  const form = value.form;
  const update = (next: Partial<UserForm>) =>
    setValue({ ...value, form: { ...form, ...next } });
  return (
    <Modal
      title={value.user ? `编辑 ${value.user.username}` : "添加用户"}
      description="密码至少 12 个字符。订阅令牌只会显示一次。"
      close={close}
    >
      <form className="modal-form" onSubmit={(event) => void save(event)}>
        <div className="form-grid">
          <label>
            用户名
            <input
              required
              minLength={3}
              maxLength={64}
              value={form.username}
              onInput={(event) =>
                update({ username: event.currentTarget.value })
              }
            />
          </label>
          <label>
            {value.user ? "新密码（留空则不修改）" : "密码"}
            <input
              required={!value.user}
              minLength={12}
              type="password"
              value={form.password}
              onInput={(event) =>
                update({ password: event.currentTarget.value })
              }
            />
          </label>
          <label>
            角色
            <select
              value={form.role}
              onChange={(event) =>
                update({ role: event.currentTarget.value as UserForm["role"] })
              }
            >
              <option value="User">普通用户</option>
              <option value="Admin">管理员</option>
            </select>
          </label>
          <label>
            流量上限（字节）
            <input
              type="number"
              min="0"
              step="1"
              max="9007199254740991"
              placeholder="留空表示不限"
              value={form.trafficLimitBytes}
              onInput={(event) =>
                update({ trafficLimitBytes: event.currentTarget.value })
              }
            />
          </label>
          <label>
            到期时间
            <input
              type="datetime-local"
              value={form.expiresAtUtc}
              onInput={(event) =>
                update({ expiresAtUtc: event.currentTarget.value })
              }
            />
          </label>
          <label className="switch form-switch">
            <input
              type="checkbox"
              checked={form.enabled}
              onChange={(event) =>
                update({ enabled: event.currentTarget.checked })
              }
            />
            <span />
            账户已启用
          </label>
        </div>
        <footer>
          <button
            className="button button-secondary"
            type="button"
            onClick={close}
          >
            取消
          </button>
          <button className="button button-primary">保存用户</button>
        </footer>
      </form>
    </Modal>
  );
}

function TokenModal({
  username,
  token,
  close,
  setError,
}: {
  username: string;
  token: string;
  close: () => void;
  setError: (value: string) => void;
}) {
  const links = [
    ["原始链接", "raw"],
    ["Base64", "base64"],
    ["Mihomo", "mihomo"],
    ["sing-box", "singbox"],
  ];
  const copy = async (value: string) => {
    try {
      await navigator.clipboard.writeText(value);
      setError("订阅链接已复制。");
    } catch (reason) {
      setError(messageFor(reason, "无法复制链接"));
    }
  };
  return (
    <Modal
      title={`${username} 的新订阅令牌`}
      description="关闭后无法再次查看；旧令牌已经失效。"
      close={close}
    >
      <Notice kind="success">请立即保存以下链接。</Notice>
      <div className="token-list">
        {links.map(([label, format]) => {
          const link = `${location.origin}/s/${encodeURIComponent(token)}?format=${format}`;
          return (
            <div key={format}>
              <span>{label}</span>
              <code>{link}</code>
              <button
                className="button button-secondary"
                type="button"
                onClick={() => void copy(link)}
              >
                复制
              </button>
            </div>
          );
        })}
      </div>
      <footer className="modal-actions">
        <button className="button button-primary" type="button" onClick={close}>
          我已保存
        </button>
      </footer>
    </Modal>
  );
}

function SectionTitle({
  title,
  description,
}: {
  title: string;
  description: string;
}) {
  return (
    <div className="section-heading">
      <div>
        <h2>{title}</h2>
        <p>{description}</p>
      </div>
    </div>
  );
}
function NodeSummary({ node }: { node: Node }) {
  return (
    <div className="node-summary">
      <i className={node.online ? "online-dot" : "offline-dot"} />
      <span>
        <strong>{node.displayName}</strong>
        <small>
          {node.online ? "在线" : node.agentId ? "离线" : "等待安装"}
        </small>
      </span>
    </div>
  );
}
function StatusBadge({
  status,
  enabled,
}: {
  status: number;
  enabled: boolean;
}) {
  const labels = [
    "未知",
    "安装中",
    "已停止",
    "启动中",
    "运行中",
    "停止中",
    "失败",
    "更新中",
    "正在重启",
    "重启退避",
  ];
  return (
    <>
      <span className={`badge status-${status}`}>
        {labels[status] ?? "未知"}
      </span>
      {!enabled && <span className="badge disabled">配置停用</span>}
    </>
  );
}
function agentUpdateLabel(node: Node) {
  const labels: Record<Node["agentUpdateStatus"], string> = {
    UpToDate: "已是最新版本",
    Available: `可更新至 ${node.latestAgentVersion ?? "最新版"}`,
    Requested: `正在更新到 ${node.desiredAgentVersion}`,
    Downloading: `正在下载 ${node.desiredAgentVersion}`,
    Staged: "更新已准备，等待应用",
    Applying: "正在应用更新",
    WaitingForReconnect: "等待 Agent 重新上线",
    Succeeded: "已更新",
    Failed: "更新失败",
  };
  return labels[node.agentUpdateStatus];
}
const formatDate = (value: string) =>
  new Date(value).toLocaleString("zh-CN", {
    dateStyle: "medium",
    timeStyle: "short",
  });
const toLocalDate = (value: string | null) =>
  value
    ? new Date(
        new Date(value).getTime() - new Date(value).getTimezoneOffset() * 60000,
      )
        .toISOString()
        .slice(0, 16)
    : "";
