import { useEffect, useState } from 'preact/hooks'
import type { ComponentChildren } from 'preact'

type Theme = 'light' | 'dark' | 'system'
type AdminPage = 'overview' | 'services' | 'templates' | 'users'
type User = { id: string; username: string; role: 'Admin' | 'User'; enabled: boolean; trafficLimitBytes: number | null; expiresAtUtc: string | null }
type Node = { id: string; displayName: string; online: boolean; platform: string | null; reportedVersion: string | null }
type Runtime = { status: number; trafficUploadBytes: number | null; trafficDownloadBytes: number | null; errorMessage: string | null }
type Service = { id: string; name: string; backendType: string; backendVersion: string; enabled: boolean; configJson: string; runtime: Runtime | null }
type Usage = { userId: string; serviceId: string; uploadBytes: number; downloadBytes: number; updatedAtUtc: string }
type Issue = { kind: string; nodeId: string; nodeDisplayName: string; serviceId?: string; serviceName?: string; errorCode?: string; errorMessage?: string }
type HealthSummary = { observedAtUtc: string; counts: { nodesTotal: number; nodesOnline: number; nodesDrifted: number; servicesTotal: number; servicesRunning: number; servicesFailed: number; servicesStoppedOrUnknown: number }; issues: Issue[] }
type Template = { id: string; name: string; backendType: string; backendVersion: string; configSchemaVersion: number; configJson: string }
type EndpointForm = { host: string; port: string; tlsServerName: string }
type UserForm = { username: string; password: string; role: 'Admin' | 'User'; enabled: boolean; trafficLimitBytes: string; expiresAtUtc: string }
type HyForm = { backendType: 'hysteria2'; name: string; version: string; listenHost: string; port: string; certificatePath: string; privateKeyPath: string; authPassword: string; masqueradeUrl: string; obfsPassword: string; upMbps: string; downMbps: string }
type XrayForm = { backendType: 'xray'; name: string; version: string; listenHost: string; port: string; clientId: string; clientEmail: string; flow: 'xtls-rprx-vision'; realityPrivateKey: string; realityPublicKey: string; shortId: string; serverName: string; destination: string; fingerprint: 'chrome' | 'firefox' | 'safari' | 'edge' | 'randomized' }
type ShadowsocksForm = { backendType: 'mihomo' | 'sing-box'; name: string; version: string; listenHost: string; port: string; method: '2022-blake3-aes-256-gcm'; password: string; udp: true }
type ServiceFormState = HyForm | XrayForm | ShadowsocksForm
type ServicePayload = { name: string; backendType: string; backendVersion: string; configSchemaVersion: 1; configJson: string }
type Request = <T,>(path: string, init?: RequestInit) => Promise<T>

const themes: Theme[] = ['light', 'dark', 'system']
const themeLabels: Record<Theme, string> = { light: '浅色', dark: '深色', system: '跟随系统' }
const adminPageLabels: Record<AdminPage, { name: string; mark: string }> = {
  overview: { name: '概览', mark: '概' }, services: { name: '服务', mark: '服' },
  templates: { name: '模板', mark: '模' }, users: { name: '用户', mark: '用' },
}
const statusLabels = ['未知', '安装中', '已停止', '启动中', '运行中', '停止中', '失败', '更新中']
const issueKindLabels: Record<string, string> = { offline: '节点离线', revisionDrift: '配置漂移', failedService: '服务失败' }
const serviceFieldLabels: Record<string, string> = {
  name: '服务名称', version: '后端版本', listenHost: '监听地址', port: '监听端口',
  certificatePath: '证书路径', privateKeyPath: '私钥路径', authPassword: '认证密码',
  masqueradeUrl: '伪装地址', obfsPassword: '混淆密码', upMbps: '上行带宽（Mbps）',
  downMbps: '下行带宽（Mbps）', clientId: '客户端 ID', clientEmail: '客户端邮箱',
  realityPrivateKey: 'Reality 私钥', realityPublicKey: 'Reality 公钥', shortId: '短 ID',
  serverName: '服务器名称', destination: '目标地址', password: '密码',
}
const emptyUser: UserForm = { username: '', password: '', role: 'User', enabled: true, trafficLimitBytes: '', expiresAtUtc: '' }
const emptyService: HyForm = { backendType: 'hysteria2', name: '', version: '2.12.2', listenHost: '0.0.0.0', port: '443', certificatePath: '', privateKeyPath: '', authPassword: '', masqueradeUrl: 'https://example.com/', obfsPassword: '', upMbps: '100', downMbps: '100' }
const emptyXrayService: XrayForm = { backendType: 'xray', name: '', version: '26.3.27', listenHost: '0.0.0.0', port: '443', clientId: '', clientEmail: '', flow: 'xtls-rprx-vision', realityPrivateKey: '', realityPublicKey: '', shortId: '', serverName: '', destination: '', fingerprint: 'chrome' }
const emptyMihomoService: ShadowsocksForm = { backendType: 'mihomo', name: '', version: '1.19.30', listenHost: '0.0.0.0', port: '24446', method: '2022-blake3-aes-256-gcm', password: '', udp: true }
const emptySingBoxService: ShadowsocksForm = { backendType: 'sing-box', name: '', version: '1.14.0', listenHost: '0.0.0.0', port: '24447', method: '2022-blake3-aes-256-gcm', password: '', udp: true }

const storedTheme = (): Theme => {
  const value = localStorage.getItem('hypanel-theme') as Theme
  return themes.includes(value) ? value : 'system'
}

const bytes = (value: number) => {
  if (!value) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), 4)
  return `${(value / 1024 ** index).toFixed(index ? 1 : 0)} ${units[index]}`
}

function configFor(form: ServiceFormState): ServicePayload {
  if (form.backendType === 'hysteria2') {
    const configJson = JSON.stringify({
      listenHost: form.listenHost,
      listenPort: Number(form.port),
      certificatePath: form.certificatePath,
      privateKeyPath: form.privateKeyPath,
      authPassword: form.authPassword,
      masqueradeUrl: form.masqueradeUrl,
      ...(form.obfsPassword ? { obfsPassword: form.obfsPassword } : {}),
      upMbps: Number(form.upMbps),
      downMbps: Number(form.downMbps),
    })
    return { name: form.name, backendType: form.backendType, backendVersion: form.version, configSchemaVersion: 1, configJson }
  }

  if (form.backendType === 'xray') {
    if (!form.realityPrivateKey) throw new Error('必须填写后端密钥。')
    const configJson = JSON.stringify({
      listenHost: form.listenHost, listenPort: Number(form.port), clientId: form.clientId,
      clientEmail: form.clientEmail, flow: form.flow, realityPrivateKey: form.realityPrivateKey,
      realityPublicKey: form.realityPublicKey, shortId: form.shortId, serverName: form.serverName,
      destination: form.destination, fingerprint: form.fingerprint,
    })
    return { name: form.name, backendType: form.backendType, backendVersion: form.version, configSchemaVersion: 1, configJson }
  }

  if (!form.password) throw new Error('必须填写后端密钥。')
  const configJson = JSON.stringify({
    listenHost: form.listenHost, listenPort: Number(form.port), method: form.method,
    password: form.password, udp: true,
  })
  return { name: form.name, backendType: form.backendType, backendVersion: form.version, configSchemaVersion: 1, configJson }
}

export function App() {
  const [theme, setTheme] = useState<Theme>(storedTheme)
  const [token, setToken] = useState(() => sessionStorage.getItem('hypanel-session-token') ?? sessionStorage.getItem('hypanel-admin-token') ?? '')
  const [user, setUser] = useState<User | null>(() => {
    try { return JSON.parse(sessionStorage.getItem('hypanel-session-user') ?? 'null') }
    catch { return null }
  })
  const [bootstrapToken, setBootstrapToken] = useState('')
  const [login, setLogin] = useState({ username: '', password: '' })
  const [error, setError] = useState('')

  useEffect(() => {
    document.documentElement.dataset.theme = theme
    localStorage.setItem('hypanel-theme', theme)
  }, [theme])

  const endSession = () => {
    sessionStorage.removeItem('hypanel-session-token')
    sessionStorage.removeItem('hypanel-session-user')
    sessionStorage.removeItem('hypanel-admin-token')
    setToken('')
    setUser(null)
  }

  const request: Request = async (path, init = {}) => {
    const response = await fetch(path, {
      ...init,
      headers: {
        ...(init.body ? { 'Content-Type': 'application/json' } : {}),
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...init.headers,
      },
    })
    if (!response.ok) {
      if (response.status === 401 && path !== '/api/auth/v1/login') endSession()
      throw new Error(response.status === 403 ? '您没有管理员权限。' : `请求失败（状态 ${response.status}）`)
    }
    return (response.status === 204 ? undefined : await response.json()) as never
  }

  const signIn = async (event: Event) => {
    event.preventDefault()
    try {
      const result = await request<{ token: string; user: User }>('/api/auth/v1/login', { method: 'POST', body: JSON.stringify(login) })
      sessionStorage.setItem('hypanel-session-token', result.token)
      sessionStorage.setItem('hypanel-session-user', JSON.stringify(result.user))
      setToken(result.token)
      setUser(result.user)
      setLogin({ username: '', password: '' })
      setError('')
    } catch (reason) { setError(messageFor(reason, '登录失败')) }
  }

  const useBootstrap = () => {
    sessionStorage.setItem('hypanel-admin-token', bootstrapToken)
    setToken(bootstrapToken)
    setUser(null)
    setBootstrapToken('')
  }

  const logout = async () => {
    try { if (user) await request<void>('/api/auth/v1/logout', { method: 'POST' }) }
    catch (reason) { setError(messageFor(reason, '无法结束会话')) }
    finally { endSession() }
  }

  if (!token) {
    return <Shell theme={theme} setTheme={setTheme} status="需要登录">
      <div className="auth-grid">
        <form className="card form-card" onSubmit={signIn}>
          <p className="eyebrow">账户</p><h1>欢迎回来。</h1>
          <label>用户名<input value={login.username} onInput={event => setLogin({ ...login, username: event.currentTarget.value })} /></label>
          <label>密码<input type="password" value={login.password} onInput={event => setLogin({ ...login, password: event.currentTarget.value })} /></label>
          <button className="button button-primary">登录</button>
        </form>
        <section className="card form-card">
          <p className="eyebrow">恢复</p><h2>初始管理员令牌</h2>
          <p className="muted">仅保留在当前浏览器会话中。</p>
          <label>访问令牌（Bearer）<input type="password" value={bootstrapToken} onInput={event => setBootstrapToken(event.currentTarget.value)} /></label>
          <button className="button button-secondary" type="button" onClick={useBootstrap}>使用令牌</button>
        </section>
      </div>
      {error && <p className="alert">{error}</p>}
    </Shell>
  }

  const isAdmin = user === null || user.role === 'Admin'
  return <Shell theme={theme} setTheme={setTheme} status={user ? `${user.username} · ${user.role === 'Admin' ? '管理员' : '用户'}` : '初始管理员'} onEnd={logout} adminWorkspace={isAdmin}>
    {error && <p className="alert">{error}</p>}
    {isAdmin ? <AdminPanel request={request} setError={setError} /> : <UserPanel request={request} setError={setError} />}
  </Shell>
}

function AdminPanel({ request, setError }: { request: Request; setError: (value: string) => void }) {
  const [page, setPage] = useState<AdminPage>('overview')
  const [nodes, setNodes] = useState<Node[]>([])
  const [node, setNode] = useState<Node | null>(null)
  const [services, setServices] = useState<Service[]>([])
  const [users, setUsers] = useState<User[]>([])
  const [health, setHealth] = useState<HealthSummary | null>(null)
  const [templates, setTemplates] = useState<Template[]>([])
  const [editing, setEditing] = useState<Service | null>(null)
  const [serviceForm, setServiceForm] = useState<ServiceFormState>(emptyService)
  const [showService, setShowService] = useState(false)
  const [userForm, setUserForm] = useState<UserForm>(emptyUser)
  const [issuedToken, setIssuedToken] = useState('')
  const [bindings, setBindings] = useState<Record<string, string[]>>({})
  const [selected, setSelected] = useState<string[]>([])
  const [endpointForms, setEndpointForms] = useState<Record<string, EndpointForm>>({})
  const [templateNames, setTemplateNames] = useState<Record<string, string>>({})

  const loadServices = async (selectedNode: Node) => {
    const rows = await request<Service[]>(`/api/admin/v1/nodes/${selectedNode.id}/services`)
    setNode(selectedNode)
    setServices(rows)
    setSelected([])
  }

  const load = async () => {
    try {
      const [loadedNodes, summary, loadedTemplates, loadedUsers] = await Promise.all([
        request<Node[]>('/api/admin/v1/nodes'), request<HealthSummary>('/api/admin/v1/health-summary'),
        request<Template[]>('/api/admin/v1/service-templates'), request<User[]>('/api/admin/v1/users'),
      ])
      const pairs = await Promise.all(loadedUsers.map(async loadedUser => [
        loadedUser.id,
        await request<string[]>(`/api/admin/v1/users/${loadedUser.id}/services`),
      ] as const))
      setNodes(loadedNodes); setHealth(summary); setTemplates(loadedTemplates); setUsers(loadedUsers)
      setBindings(Object.fromEntries(pairs))
      if (loadedNodes[0]) await loadServices(loadedNodes[0])
    } catch (reason) { setError(messageFor(reason, '无法加载管理员数据')) }
  }

  const refresh = async () => {
    try {
      const [summary, loadedTemplates] = await Promise.all([
        request<HealthSummary>('/api/admin/v1/health-summary'), request<Template[]>('/api/admin/v1/service-templates'),
      ])
      setHealth(summary); setTemplates(loadedTemplates)
      if (node) await loadServices(node)
    } catch (reason) { setError(messageFor(reason, '无法刷新管理员数据')) }
  }

  useEffect(() => { void load() }, [])

  const saveService = async (event: Event) => {
    event.preventDefault()
    if (!node) return
    try {
      const payload = configFor(serviceForm)
      const body = editing
        ? { name: payload.name, backendVersion: payload.backendVersion, enabled: editing.enabled, configSchemaVersion: 1, configJson: payload.configJson }
        : payload
      await request(`/api/admin/v1/nodes/${node.id}/services${editing ? `/${editing.id}` : ''}`, {
        method: editing ? 'PUT' : 'POST', body: JSON.stringify(body),
      })
      setShowService(false); setEditing(null); setServiceForm(emptyService)
      await refresh()
    } catch (reason) { setError(messageFor(reason, '无法保存服务')) }
  }

  const saveTemplate = async (payload: ServicePayload) => {
    try {
      await request<Template>('/api/admin/v1/service-templates', { method: 'POST', body: JSON.stringify(payload) })
      await refresh()
    } catch (reason) { setError(messageFor(reason, '无法保存模板')) }
  }

  const deleteTemplate = async (template: Template) => {
    try {
      await request<void>(`/api/admin/v1/service-templates/${template.id}`, { method: 'DELETE' })
      await refresh()
    } catch (reason) { setError(messageFor(reason, '无法删除模板')) }
  }

  const instantiateTemplate = async (template: Template) => {
    if (!node) return
    const name = templateNames[template.id]?.trim()
    if (!name) { setError('实例化模板前请输入服务名称。'); return }
    try {
      await request(`/api/admin/v1/nodes/${node.id}/services/from-template/${template.id}`, { method: 'POST', body: JSON.stringify({ name }) })
      setTemplateNames({ ...templateNames, [template.id]: '' })
      await refresh()
    } catch (reason) { setError(messageFor(reason, '无法从模板创建服务')) }
  }

  const batchEnabled = async (enabled: boolean) => {
    if (!node || !selected.length) return
    try {
      await request('/api/admin/v1/services/batch-enabled', {
        method: 'POST', body: JSON.stringify({ items: selected.map(serviceId => ({ nodeId: node.id, serviceId, enabled })) }),
      })
      setSelected([])
      await refresh()
    } catch (reason) { setError(messageFor(reason, '无法更新服务')) }
  }

  const saveEndpoint = async (service: Service) => {
    if (!node) return
    const endpoint = endpointForms[service.id] ?? { host: '', port: '', tlsServerName: '' }
    try {
      await request(`/api/admin/v1/nodes/${node.id}/services/${service.id}/public-endpoint`, {
        method: 'PUT',
        body: JSON.stringify({ host: endpoint.host, port: Number(endpoint.port), tlsServerName: endpoint.tlsServerName }),
      })
    } catch (reason) { setError(messageFor(reason, '无法保存公网端点')) }
  }

  const createUser = async (event: Event) => {
    event.preventDefault()
    try {
      const result = await request<{ user: User; subscriptionToken: string }>('/api/admin/v1/users', {
        method: 'POST',
        body: JSON.stringify({
          ...userForm,
          trafficLimitBytes: userForm.trafficLimitBytes ? Number(userForm.trafficLimitBytes) : null,
          expiresAtUtc: userForm.expiresAtUtc || null,
        }),
      })
      setIssuedToken(result.subscriptionToken)
      setUserForm(emptyUser)
      await load()
    } catch (reason) { setError(messageFor(reason, '无法创建用户')) }
  }

  const rotateUserToken = async (selectedUser: User) => {
    try {
      const result = await request<{ subscriptionToken: string }>(`/api/admin/v1/users/${selectedUser.id}/subscription-token/rotate`, { method: 'POST' })
      setIssuedToken(result.subscriptionToken)
    } catch (reason) { setError(messageFor(reason, '无法轮换订阅令牌')) }
  }

  const toggleBinding = async (userId: string, serviceId: string) => {
    const bound = bindings[userId]?.includes(serviceId)
    try {
      await request<void>(`/api/admin/v1/users/${userId}/services/${serviceId}`, { method: bound ? 'DELETE' : 'PUT' })
      const updated = await request<string[]>(`/api/admin/v1/users/${userId}/services`)
      setBindings({ ...bindings, [userId]: updated })
    } catch (reason) { setError(messageFor(reason, '无法更新访问权限')) }
  }

  const pageContent: Record<AdminPage, { title: string; description: string }> = {
    overview: { title: '基础设施概览', description: '节点集群健康状况与当前运行状态。' },
    services: { title: '服务', description: '所选节点上的独立后端实例。' },
    templates: { title: '服务模板', description: node ? `${node.displayName} 可用的复用配置。` : '实例化模板前请选择节点。' },
    users: { title: '用户与访问', description: node ? `以下授权仅适用于当前从 ${node.displayName} 加载的服务。` : '请选择节点以管理服务授权。' },
  }
  const currentPage = pageContent[page]
  const beginService = () => { setEditing(null); setServiceForm(emptyService); setShowService(true) }

  return <div className="admin-workspace">
    <aside className="admin-sidebar">
      <div className="admin-sidebar-brand">
        <span className="admin-mark">HP</span>
        <div><strong>HyPanel</strong><span>控制平面</span></div>
      </div>
      <nav className="admin-navigation" aria-label="管理工作区">
        {(['overview', 'services', 'templates', 'users'] as AdminPage[]).map(item => <button
          key={item}
          type="button"
          className={page === item ? 'admin-nav-item active' : 'admin-nav-item'}
          aria-current={page === item ? 'page' : undefined}
          onClick={() => setPage(item)}
        >
          <span className="admin-nav-mark">{adminPageLabels[item].mark}</span>
          {adminPageLabels[item].name}
        </button>)}
      </nav>
      <div className="admin-node-context">
        <span>当前节点</span>
        <strong>{node?.displayName ?? '未选择节点'}</strong>
        <small><i className={node?.online ? 'online' : ''} />{node ? (node.online ? '在线' : '离线') : '不可用'}</small>
      </div>
    </aside>
    <div className="admin-main">
      <header className="workspace-header">
        <div>
          <p className="eyebrow">管理</p>
          <h1>{currentPage.title}</h1>
          <p className="muted">{currentPage.description}</p>
        </div>
        <div className="workspace-actions">
          {page === 'overview' && <button type="button" className="button button-secondary" onClick={() => void refresh()}>刷新</button>}
          {page === 'services' && <>
            <select className="node-select" aria-label="当前节点" value={node?.id ?? ''} onChange={event => { const selectedNode = nodes.find(item => item.id === event.currentTarget.value); if (selectedNode) void loadServices(selectedNode) }}>
              <option value="" disabled>请选择节点</option>
              {nodes.map(item => <option key={item.id} value={item.id}>{item.displayName}</option>)}
            </select>
            <button type="button" className="button button-primary" disabled={!node} onClick={beginService}>添加服务</button>
            {selected.length > 0 && <>
              <button type="button" className="button button-secondary" onClick={() => void batchEnabled(true)}>启用所选</button>
              <button type="button" className="button button-secondary" onClick={() => void batchEnabled(false)}>停用所选</button>
            </>}
          </>}
        </div>
      </header>
      <div className="workspace-content">
        {page === 'overview' && <><HealthSection health={health} /><NodeFleet nodes={nodes} selectedNode={node} selectNode={loadServices} /></>}
        {page === 'services' && <ServicesWorkspace
          nodes={nodes}
          node={node}
          services={services}
          selected={selected}
          endpointForms={endpointForms}
          loadServices={loadServices}
          setSelected={setSelected}
          setEndpointForms={setEndpointForms}
          edit={service => editService(service, setEditing, setServiceForm, setShowService)}
          saveEndpoint={saveEndpoint}
        />}
        {page === 'services' && showService && <ServiceForm form={serviceForm} setForm={setServiceForm} save={saveService} saveAsTemplate={saveTemplate} cancel={() => { setShowService(false); setEditing(null) }} editing={editing} setError={setError} />}
        {page === 'templates' && <TemplatesSection templates={templates} names={templateNames} setNames={setTemplateNames} canInstantiate={!!node} targetNodeName={node?.displayName ?? null} instantiate={instantiateTemplate} remove={deleteTemplate} />}
        {page === 'users' && <UsersSection users={users} form={userForm} setForm={setUserForm} create={createUser} issuedToken={issuedToken} clearToken={() => setIssuedToken('')} rotate={rotateUserToken} services={services} bindings={bindings} toggleBinding={toggleBinding} setError={setError} targetNodeName={node?.displayName ?? null} />}
      </div>
    </div>
  </div>
}

function NodeFleet({ nodes, selectedNode, selectNode }: { nodes: Node[]; selectedNode: Node | null; selectNode: (node: Node) => Promise<void> }) {
  return <section className="card panel fleet-panel"><div className="section-heading"><div><h2>节点集群</h2><p className="muted">代理程序可用性、平台和上报版本。</p></div></div>{nodes.length ? <div className="fleet-list">{nodes.map(item => <button key={item.id} type="button" className={selectedNode?.id === item.id ? 'fleet-row selected' : 'fleet-row'} onClick={() => void selectNode(item)}><strong><i className={item.online ? 'online' : ''} />{item.displayName}</strong><span>{item.online ? '在线' : '离线'}</span><span>{item.platform ?? '未知平台'}</span><span>{item.reportedVersion ?? '未上报版本'}</span></button>)}</div> : <p className="muted">尚未有节点完成注册。</p>}</section>
}

function ServicesWorkspace({ nodes, node, services, selected, endpointForms, loadServices, setSelected, setEndpointForms, edit, saveEndpoint }: { nodes: Node[]; node: Node | null; services: Service[]; selected: string[]; endpointForms: Record<string, EndpointForm>; loadServices: (node: Node) => Promise<void>; setSelected: (value: string[]) => void; setEndpointForms: (value: Record<string, EndpointForm>) => void; edit: (service: Service) => void; saveEndpoint: (service: Service) => Promise<void> }) {
  return <section className="services-workspace">
    <aside className="card panel node-list">
      <div className="section-heading"><h2>节点</h2></div>
      {nodes.length ? nodes.map(item => <button key={item.id} type="button" className={`node-item ${node?.id === item.id ? 'selected' : ''}`} onClick={() => void loadServices(item)}>
        <strong><i className={item.online ? 'online' : ''} />{item.displayName}</strong>
        <span>{item.platform ?? '未知平台'} · {item.reportedVersion ?? '未上报版本'}</span>
      </button>) : <p className="muted">暂无可选节点。</p>}
    </aside>
    <section className="card panel services-panel">
      <div className="section-heading"><div><h2>{node?.displayName ?? '未选择节点'}</h2><p className="muted">选择服务可批量变更状态，也可编辑配置或设置公网端点。</p></div></div>
      {services.length ? <div className="service-list">{services.map(service => <ServiceRow
        key={service.id}
        service={service}
        selected={selected.includes(service.id)}
        endpoint={endpointForms[service.id] ?? { host: '', port: '', tlsServerName: '' }}
        onSelect={() => setSelected(selected.includes(service.id) ? selected.filter(id => id !== service.id) : [...selected, service.id])}
        onEdit={() => edit(service)}
        onEndpointChange={value => setEndpointForms({ ...endpointForms, [service.id]: value })}
        onSaveEndpoint={() => void saveEndpoint(service)}
      />)}</div> : <p className="muted">请选择包含服务的节点，或在上方添加第一个服务。</p>}
    </section>
  </section>
}

function HealthSection({ health }: { health: HealthSummary | null }) {
  if (!health) return null
  return <>
    <div className="stats-grid health-grid">
      <Stat label="在线节点" value={`${health.counts.nodesOnline} / ${health.counts.nodesTotal}`} note="已连接的代理程序" />
      <Stat label="运行中的服务" value={`${health.counts.servicesRunning} / ${health.counts.servicesTotal}`} note="已观测到的运行状态" />
      <Stat label="配置漂移" value={String(health.counts.nodesDrifted)} note="需要关注的节点" />
      <Stat label="失败服务" value={String(health.counts.servicesFailed)} note="服务运行失败" />
    </div>
    <section className="card panel"><div className="section-heading"><div><h2>健康问题</h2><p className="muted">观测时间：{new Date(health.observedAtUtc).toLocaleString('zh-CN')}</p></div></div>
      {health.issues.length ? health.issues.map((issue, index) => <p className="issue-row" key={`${issue.kind}-${issue.nodeId}-${issue.serviceId ?? index}`}><strong>{issueKindLabels[issue.kind] ?? issue.kind}</strong> · {issue.nodeDisplayName}{issue.serviceName ? ` · ${issue.serviceName}` : ''}{issue.errorCode ? ` · ${issue.errorCode}` : ''}{issue.errorMessage ? ` — ${issue.errorMessage}` : ''}</p>) : <p className="muted">当前没有健康问题。</p>}
    </section>
  </>
}

function ServiceRow({ service, selected, endpoint, onSelect, onEdit, onEndpointChange, onSaveEndpoint }: { service: Service; selected: boolean; endpoint: EndpointForm; onSelect: () => void; onEdit: () => void; onEndpointChange: (value: EndpointForm) => void; onSaveEndpoint: () => void }) {
  return <article className="service-row">
    <label className="service-select"><input type="checkbox" checked={selected} onChange={onSelect} /><span className="sr-only">选择 {service.name}</span></label>
    <div><strong>{service.name}</strong><small className="service-meta">{service.backendType} {service.backendVersion} · {statusLabels[service.runtime?.status ?? 0] ?? '未知'}</small>{service.runtime?.errorMessage && <small className="service-error">{service.runtime.errorMessage}</small>}</div>
    <div className="row-actions"><button type="button" className="button button-secondary" onClick={onEdit}>编辑</button></div>
    <div className="endpoint-form">
      <input aria-label={`${service.name} 公网主机`} placeholder="公网主机" value={endpoint.host} onInput={event => onEndpointChange({ ...endpoint, host: event.currentTarget.value })} />
      <input aria-label={`${service.name} 公网端口`} placeholder="端口" inputMode="numeric" value={endpoint.port} onInput={event => onEndpointChange({ ...endpoint, port: event.currentTarget.value })} />
      <input aria-label={`${service.name} TLS 服务器名称`} placeholder="TLS 服务器名称" value={endpoint.tlsServerName} onInput={event => onEndpointChange({ ...endpoint, tlsServerName: event.currentTarget.value })} />
      <button type="button" className="button button-secondary" onClick={onSaveEndpoint}>保存端点</button>
    </div>
  </article>
}

function TemplatesSection({ templates, names, setNames, canInstantiate, targetNodeName, instantiate, remove }: { templates: Template[]; names: Record<string, string>; setNames: (value: Record<string, string>) => void; canInstantiate: boolean; targetNodeName: string | null; instantiate: (template: Template) => Promise<void>; remove: (template: Template) => Promise<void> }) {
  return <section className="card panel"><div className="section-heading"><div><h2>{targetNodeName ?? '未选择节点'}的模板</h2><p className="muted">将新建服务配置保存为可复用模板。实例化操作将目标设为{targetNodeName ?? '当前所选节点'}。</p></div></div>
    {templates.length ? templates.map(template => <div className="template-row" key={template.id}><div><strong>{template.name}</strong><p className="muted">{template.backendType} {template.backendVersion}</p></div><div className="row-actions"><input placeholder="新服务名称" value={names[template.id] ?? ''} onInput={event => setNames({ ...names, [template.id]: event.currentTarget.value })} /><button type="button" className="button button-secondary" disabled={!canInstantiate} onClick={() => void instantiate(template)}>实例化</button><button type="button" className="button button-danger" onClick={() => void remove(template)}>删除</button></div></div>) : <p className="muted">尚未保存模板。</p>}
  </section>
}

function UsersSection({ users, form, setForm, create, issuedToken, clearToken, rotate, services, bindings, toggleBinding, setError, targetNodeName }: { users: User[]; form: UserForm; setForm: (value: UserForm) => void; create: (event: Event) => Promise<void>; issuedToken: string; clearToken: () => void; rotate: (user: User) => Promise<void>; services: Service[]; bindings: Record<string, string[]>; toggleBinding: (userId: string, serviceId: string) => Promise<void>; setError: (value: string) => void; targetNodeName: string | null }) {
  return <section className="card panel"><div className="section-heading"><div><h2>用户与访问</h2><p className="muted">授权仅对应当前从{targetNodeName ?? '所选节点'}加载的服务。</p></div></div>
    <form className="form-grid" onSubmit={create}>
      <label>用户名<input required value={form.username} onInput={event => setForm({ ...form, username: event.currentTarget.value })} /></label>
      <label>密码<input required type="password" value={form.password} onInput={event => setForm({ ...form, password: event.currentTarget.value })} /></label>
      <label>角色<select value={form.role} onChange={event => setForm({ ...form, role: event.currentTarget.value as UserForm['role'] })}><option value="User">用户</option><option value="Admin">管理员</option></select></label>
      <label>流量上限（字节）<input inputMode="numeric" value={form.trafficLimitBytes} onInput={event => setForm({ ...form, trafficLimitBytes: event.currentTarget.value })} /></label>
      <label>到期时间<input type="datetime-local" value={form.expiresAtUtc} onInput={event => setForm({ ...form, expiresAtUtc: event.currentTarget.value })} /></label>
      <label className="toggle-label">已启用<input type="checkbox" checked={form.enabled} onChange={event => setForm({ ...form, enabled: event.currentTarget.checked })} /></label>
      <div className="row-actions"><button className="button button-primary">创建用户</button></div>
    </form>
    {issuedToken && <SensitiveToken token={issuedToken} clear={clearToken} setError={setError} />}
    <div className="user-list">{users.map(item => <article className="user-row" key={item.id}><div><strong>{item.username}</strong><span className="service-meta">{item.role === 'Admin' ? '管理员' : '用户'} · {item.enabled ? '已启用' : '已停用'} · {item.trafficLimitBytes === null ? '不限' : bytes(item.trafficLimitBytes)} · {item.expiresAtUtc ? new Date(item.expiresAtUtc).toLocaleDateString('zh-CN') : '永不过期'}</span><div className="grant-list">{services.map(service => <label key={service.id} className="toggle-label"><input type="checkbox" checked={bindings[item.id]?.includes(service.id) ?? false} onChange={() => void toggleBinding(item.id, service.id)} />{service.name}</label>)}</div></div><button type="button" className="button button-secondary" onClick={() => void rotate(item)}>轮换令牌</button></article>)}</div>
  </section>
}

function editService(service: Service, setEditing: (value: Service) => void, setForm: (value: ServiceFormState) => void, setShow: (value: boolean) => void) {
  let config: Record<string, unknown> = {}
  try { config = JSON.parse(service.configJson) as Record<string, unknown> } catch { /* retain defaults */ }
  setEditing(service)
  if (service.backendType === 'xray') setForm({ ...emptyXrayService, name: service.name, version: service.backendVersion, listenHost: String(config.listenHost ?? '0.0.0.0'), port: String(config.listenPort ?? 443), clientId: String(config.clientId ?? ''), clientEmail: String(config.clientEmail ?? ''), realityPublicKey: String(config.realityPublicKey ?? ''), shortId: String(config.shortId ?? ''), serverName: String(config.serverName ?? ''), destination: String(config.destination ?? '') })
  else if (service.backendType === 'mihomo' || service.backendType === 'sing-box') setForm({ ...(service.backendType === 'mihomo' ? emptyMihomoService : emptySingBoxService), name: service.name, version: service.backendVersion, listenHost: String(config.listenHost ?? '0.0.0.0'), port: String(config.listenPort ?? 443), password: '' })
  else setForm({ ...emptyService, name: service.name, version: service.backendVersion, listenHost: String(config.listenHost ?? '0.0.0.0'), port: String(config.listenPort ?? 443), certificatePath: String(config.certificatePath ?? ''), privateKeyPath: String(config.privateKeyPath ?? ''), masqueradeUrl: String(config.masqueradeUrl ?? ''), upMbps: String(config.upMbps ?? 100), downMbps: String(config.downMbps ?? 100) })
  setShow(true)
}

function ServiceForm({ form, setForm, save, saveAsTemplate, cancel, editing, setError }: { form: ServiceFormState; setForm: (value: ServiceFormState) => void; save: (event: Event) => Promise<void>; saveAsTemplate: (payload: ServicePayload) => Promise<void>; cancel: () => void; editing: Service | null; setError: (value: string) => void }) {
  const keys = form.backendType === 'xray' ? ['name', 'version', 'listenHost', 'port', 'clientId', 'clientEmail', 'realityPrivateKey', 'realityPublicKey', 'shortId', 'serverName', 'destination'] : form.backendType === 'mihomo' || form.backendType === 'sing-box' ? ['name', 'version', 'listenHost', 'port', 'password'] : ['name', 'version', 'listenHost', 'port', 'certificatePath', 'privateKeyPath', 'authPassword', 'masqueradeUrl', 'obfsPassword', 'upMbps', 'downMbps']
  const change = (key: string, value: string) => setForm({ ...form, [key]: value } as ServiceFormState)
  const secret = (key: string) => /password|privatekey/i.test(key)
  const changeBackend = (value: string) => setForm(value === 'xray' ? emptyXrayService : value === 'mihomo' ? emptyMihomoService : value === 'sing-box' ? emptySingBoxService : emptyService)
  const saveTemplate = async () => { try { await saveAsTemplate(configFor(form)) } catch (reason) { setError(messageFor(reason, '无法保存模板')) } }
  return <form className="card panel form-grid" onSubmit={event => void save(event)}><h2>{editing ? '编辑' : '创建'} {form.backendType} 服务</h2>
    <label>后端类型<select value={form.backendType} disabled={!!editing} onChange={event => changeBackend(event.currentTarget.value)}><BackendOptions /></select></label>
    {keys.map(key => <label key={key}>{serviceFieldLabels[key] ?? key}<input required={key === 'password' || key === 'realityPrivateKey'} type={secret(key) ? 'password' : 'text'} value={String(form[key as keyof ServiceFormState] ?? '')} onInput={event => change(key, event.currentTarget.value)} /></label>)}
    <div className="row-actions"><button className="button button-primary">保存</button>{!editing && <button className="button button-secondary" type="button" onClick={() => void saveTemplate()}>保存为模板</button>}<button className="button button-secondary" type="button" onClick={cancel}>取消</button></div>
  </form>
}

function UserPanel({ request, setError }: { request: Request; setError: (value: string) => void }) {
  const [me, setMe] = useState<User | null>(null)
  const [usage, setUsage] = useState<Usage[]>([])
  const [issuedToken, setIssuedToken] = useState('')
  useEffect(() => { void (async () => { try { const [account, rows] = await Promise.all([request<User>('/api/user/v1/me'), request<Usage[]>('/api/user/v1/usage')]); setMe(account); setUsage(rows) } catch (reason) { setError(messageFor(reason, '无法加载账户信息')) } })() }, [])
  const rotate = async () => { try { const result = await request<{ subscriptionToken: string }>('/api/user/v1/subscription-token/rotate', { method: 'POST' }); setIssuedToken(result.subscriptionToken) } catch (reason) { setError(messageFor(reason, '无法轮换订阅令牌')) } }
  const total = usage.reduce((sum, row) => sum + row.uploadBytes + row.downloadBytes, 0)
  return <><section className="intro"><div><p className="eyebrow">账户</p><h1>您的订阅。</h1><p className="muted">查看用量、账户限制和可撤销的订阅链接。</p></div></section><div className="stats-grid"><Stat label="已用流量" value={bytes(total)} note="上传 + 下载" /><Stat label="流量上限" value={me?.trafficLimitBytes === null ? '不限' : bytes(me?.trafficLimitBytes ?? 0)} note="流量额度" /><Stat label="到期时间" value={me?.expiresAtUtc ? new Date(me.expiresAtUtc).toLocaleDateString('zh-CN') : '永不过期'} note="账户有效期" /></div><section className="card panel"><div className="section-heading"><h2>订阅链接</h2><button type="button" className="button button-primary" onClick={() => void rotate()}>签发新令牌</button></div>{issuedToken && <SensitiveToken token={issuedToken} clear={() => setIssuedToken('')} setError={setError} />}</section></>
}

function BackendOptions() { return <><option value="hysteria2">hysteria2</option><option value="xray">xray</option><option value="mihomo">mihomo</option><option value="sing-box">sing-box</option></> }
function SensitiveToken({ token, clear, setError }: { token: string; clear: () => void; setError: (value: string) => void }) { const links = ['raw', 'base64', 'mihomo', 'singbox'].map(format => `${location.origin}/s/${encodeURIComponent(token)}?format=${format}`); const copy = async (link: string) => { try { await navigator.clipboard.writeText(link) } catch (reason) { setError(messageFor(reason, '无法复制订阅链接')) } }; return <aside className="token-result"><strong>请立即复制：此令牌无法恢复。</strong>{links.map(link => <p key={link}><code>{link}</code><button type="button" className="button button-secondary" onClick={() => void copy(link)}>复制</button></p>)}<button type="button" className="button button-secondary" onClick={clear}>隐藏</button></aside> }
function Stat({ label, value, note }: { label: string; value: string; note: string }) { return <article className="card stat-card"><span className="stat-label">{label}</span><strong>{value}</strong><span className="muted">{note}</span></article> }
function Shell({ children, theme, setTheme, status, onEnd, adminWorkspace = false }: { children: ComponentChildren; theme: Theme; setTheme: (theme: Theme) => void; status: string; onEnd?: () => void; adminWorkspace?: boolean }) { return <div className="app-shell"><header className="topbar"><a className="brand" href="/"><span className="brand-mark">H</span>HyPanel</a><div className="topbar-actions"><span className="status"><span className="status-dot" />{status}</span><div className="theme-picker">{themes.map(value => <button key={value} type="button" className={theme === value ? 'theme-button active' : 'theme-button'} onClick={() => setTheme(value)}>{themeLabels[value]}</button>)}</div>{onEnd && <button type="button" className="button button-secondary" onClick={() => void onEnd()}>结束会话</button>}</div></header><main className={adminWorkspace ? 'content admin-content' : 'content'}>{children}</main></div> }
function messageFor(reason: unknown, fallback: string) { return reason instanceof Error ? reason.message : fallback }
