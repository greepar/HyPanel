import { useEffect, useState } from 'preact/hooks'
import type { ComponentChildren } from 'preact'

type Theme = 'light' | 'dark' | 'system'
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
const statusLabels = ['Unknown', 'Installing', 'Stopped', 'Starting', 'Running', 'Stopping', 'Failed', 'Updating']
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
    if (!form.realityPrivateKey) throw new Error('The backend secret is required.')
    const configJson = JSON.stringify({
      listenHost: form.listenHost, listenPort: Number(form.port), clientId: form.clientId,
      clientEmail: form.clientEmail, flow: form.flow, realityPrivateKey: form.realityPrivateKey,
      realityPublicKey: form.realityPublicKey, shortId: form.shortId, serverName: form.serverName,
      destination: form.destination, fingerprint: form.fingerprint,
    })
    return { name: form.name, backendType: form.backendType, backendVersion: form.version, configSchemaVersion: 1, configJson }
  }

  if (!form.password) throw new Error('The backend secret is required.')
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
      throw new Error(response.status === 403 ? 'You do not have administrator access.' : `${response.status} ${response.statusText}`)
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
    } catch (reason) { setError(messageFor(reason, 'Login failed')) }
  }

  const useBootstrap = () => {
    sessionStorage.setItem('hypanel-admin-token', bootstrapToken)
    setToken(bootstrapToken)
    setUser(null)
    setBootstrapToken('')
  }

  const logout = async () => {
    try { if (user) await request<void>('/api/auth/v1/logout', { method: 'POST' }) }
    catch (reason) { setError(messageFor(reason, 'Unable to end session')) }
    finally { endSession() }
  }

  if (!token) {
    return <Shell theme={theme} setTheme={setTheme} status="Sign in required">
      <div className="auth-grid">
        <form className="card form-card" onSubmit={signIn}>
          <p className="eyebrow">Account</p><h1>Welcome back.</h1>
          <label>Username<input value={login.username} onInput={event => setLogin({ ...login, username: event.currentTarget.value })} /></label>
          <label>Password<input type="password" value={login.password} onInput={event => setLogin({ ...login, password: event.currentTarget.value })} /></label>
          <button className="button button-primary">Sign in</button>
        </form>
        <section className="card form-card">
          <p className="eyebrow">Recovery</p><h2>Bootstrap admin token</h2>
          <p className="muted">Kept only for this browser session.</p>
          <label>Bearer token<input type="password" value={bootstrapToken} onInput={event => setBootstrapToken(event.currentTarget.value)} /></label>
          <button className="button button-secondary" type="button" onClick={useBootstrap}>Use token</button>
        </section>
      </div>
      {error && <p className="alert">{error}</p>}
    </Shell>
  }

  const isAdmin = user === null || user.role === 'Admin'
  return <Shell theme={theme} setTheme={setTheme} status={user ? `${user.username} · ${user.role}` : 'Bootstrap Admin'} onEnd={logout}>
    {error && <p className="alert">{error}</p>}
    {isAdmin ? <AdminPanel request={request} setError={setError} /> : <UserPanel request={request} setError={setError} />}
  </Shell>
}

function AdminPanel({ request, setError }: { request: Request; setError: (value: string) => void }) {
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
    } catch (reason) { setError(messageFor(reason, 'Unable to load administrator data')) }
  }

  const refresh = async () => {
    try {
      const [summary, loadedTemplates] = await Promise.all([
        request<HealthSummary>('/api/admin/v1/health-summary'), request<Template[]>('/api/admin/v1/service-templates'),
      ])
      setHealth(summary); setTemplates(loadedTemplates)
      if (node) await loadServices(node)
    } catch (reason) { setError(messageFor(reason, 'Unable to refresh administrator data')) }
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
    } catch (reason) { setError(messageFor(reason, 'Unable to save service')) }
  }

  const saveTemplate = async (payload: ServicePayload) => {
    try {
      await request<Template>('/api/admin/v1/service-templates', { method: 'POST', body: JSON.stringify(payload) })
      await refresh()
    } catch (reason) { setError(messageFor(reason, 'Unable to save template')) }
  }

  const deleteTemplate = async (template: Template) => {
    try {
      await request<void>(`/api/admin/v1/service-templates/${template.id}`, { method: 'DELETE' })
      await refresh()
    } catch (reason) { setError(messageFor(reason, 'Unable to delete template')) }
  }

  const instantiateTemplate = async (template: Template) => {
    if (!node) return
    const name = templateNames[template.id]?.trim()
    if (!name) { setError('Enter a service name before instantiating a template.'); return }
    try {
      await request(`/api/admin/v1/nodes/${node.id}/services/from-template/${template.id}`, { method: 'POST', body: JSON.stringify({ name }) })
      setTemplateNames({ ...templateNames, [template.id]: '' })
      await refresh()
    } catch (reason) { setError(messageFor(reason, 'Unable to create service from template')) }
  }

  const batchEnabled = async (enabled: boolean) => {
    if (!node || !selected.length) return
    try {
      await request('/api/admin/v1/services/batch-enabled', {
        method: 'POST', body: JSON.stringify({ items: selected.map(serviceId => ({ nodeId: node.id, serviceId, enabled })) }),
      })
      setSelected([])
      await refresh()
    } catch (reason) { setError(messageFor(reason, 'Unable to update services')) }
  }

  const saveEndpoint = async (service: Service) => {
    if (!node) return
    const endpoint = endpointForms[service.id] ?? { host: '', port: '', tlsServerName: '' }
    try {
      await request(`/api/admin/v1/nodes/${node.id}/services/${service.id}/public-endpoint`, {
        method: 'PUT',
        body: JSON.stringify({ host: endpoint.host, port: Number(endpoint.port), tlsServerName: endpoint.tlsServerName }),
      })
    } catch (reason) { setError(messageFor(reason, 'Unable to save public endpoint')) }
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
    } catch (reason) { setError(messageFor(reason, 'Unable to create user')) }
  }

  const rotateUserToken = async (selectedUser: User) => {
    try {
      const result = await request<{ subscriptionToken: string }>(`/api/admin/v1/users/${selectedUser.id}/subscription-token/rotate`, { method: 'POST' })
      setIssuedToken(result.subscriptionToken)
    } catch (reason) { setError(messageFor(reason, 'Unable to rotate subscription token')) }
  }

  const toggleBinding = async (userId: string, serviceId: string) => {
    const bound = bindings[userId]?.includes(serviceId)
    try {
      await request<void>(`/api/admin/v1/users/${userId}/services/${serviceId}`, { method: bound ? 'DELETE' : 'PUT' })
      const updated = await request<string[]>(`/api/admin/v1/users/${userId}/services`)
      setBindings({ ...bindings, [userId]: updated })
    } catch (reason) { setError(messageFor(reason, 'Unable to update access')) }
  }

  return <>
    <section className="intro">
      <div><p className="eyebrow">Administration</p><h1>Infrastructure overview.</h1><p className="muted">Nodes, services, user access, and operational health.</p></div>
      <button className="button button-secondary" onClick={() => void refresh()}>Refresh</button>
    </section>
    <HealthSection health={health} />
    <section className="node-layout">
      <aside className="card panel node-list">
        <div className="section-heading"><h2>Nodes</h2></div>
        {nodes.map(item => <button key={item.id} className={`node-item ${node?.id === item.id ? 'selected' : ''}`} onClick={() => void loadServices(item)}>
          <strong><i className={item.online ? 'online' : ''} />{item.displayName}</strong>
          <span>{item.platform ?? 'Unknown platform'} · {item.reportedVersion ?? 'No version'}</span>
        </button>)}
      </aside>
      <section className="card panel services-panel">
        <div className="section-heading">
          <div><h2>Services · {node?.displayName ?? 'No node selected'}</h2><p className="muted">Independent backend instances on the selected node.</p></div>
          <button className="button button-primary" disabled={!node} onClick={() => { setEditing(null); setServiceForm(emptyService); setShowService(true) }}>Add service</button>
        </div>
        {services.length > 0 && <div className="row-actions batch-actions">
          <button className="button button-secondary" disabled={!selected.length} onClick={() => void batchEnabled(true)}>Enable selected</button>
          <button className="button button-secondary" disabled={!selected.length} onClick={() => void batchEnabled(false)}>Disable selected</button>
        </div>}
        {services.length ? <div className="service-list">{services.map(service => <ServiceRow key={service.id} service={service} selected={selected.includes(service.id)} endpoint={endpointForms[service.id] ?? { host: '', port: '', tlsServerName: '' }} onSelect={() => setSelected(selected.includes(service.id) ? selected.filter(id => id !== service.id) : [...selected, service.id])} onEdit={() => editService(service, setEditing, setServiceForm, setShowService)} onEndpointChange={value => setEndpointForms({ ...endpointForms, [service.id]: value })} onSaveEndpoint={() => void saveEndpoint(service)} />)}</div> : <p className="muted">Select a node with services, or add the first service.</p>}
      </section>
    </section>
    {showService && <ServiceForm form={serviceForm} setForm={setServiceForm} save={saveService} saveAsTemplate={saveTemplate} cancel={() => { setShowService(false); setEditing(null) }} editing={editing} setError={setError} />}
    <TemplatesSection templates={templates} names={templateNames} setNames={setTemplateNames} canInstantiate={!!node} instantiate={instantiateTemplate} remove={deleteTemplate} />
    <UsersSection users={users} form={userForm} setForm={setUserForm} create={createUser} issuedToken={issuedToken} clearToken={() => setIssuedToken('')} rotate={rotateUserToken} services={services} bindings={bindings} toggleBinding={toggleBinding} setError={setError} />
  </>
}

function HealthSection({ health }: { health: HealthSummary | null }) {
  if (!health) return null
  return <>
    <div className="stats-grid health-grid">
      <Stat label="Nodes online" value={`${health.counts.nodesOnline} / ${health.counts.nodesTotal}`} note="Connected agents" />
      <Stat label="Services running" value={`${health.counts.servicesRunning} / ${health.counts.servicesTotal}`} note="Observed runtime" />
      <Stat label="Drifted" value={String(health.counts.nodesDrifted)} note="Nodes needing attention" />
      <Stat label="Failed" value={String(health.counts.servicesFailed)} note="Service failures" />
    </div>
    <section className="card panel"><div className="section-heading"><div><h2>Health issues</h2><p className="muted">Observed {new Date(health.observedAtUtc).toLocaleString()}</p></div></div>
      {health.issues.length ? health.issues.map((issue, index) => <p className="issue-row" key={`${issue.kind}-${issue.nodeId}-${issue.serviceId ?? index}`}><strong>{issue.kind}</strong> · {issue.nodeDisplayName}{issue.serviceName ? ` · ${issue.serviceName}` : ''}{issue.errorCode ? ` · ${issue.errorCode}` : ''}{issue.errorMessage ? ` — ${issue.errorMessage}` : ''}</p>) : <p className="muted">No current issues.</p>}
    </section>
  </>
}

function ServiceRow({ service, selected, endpoint, onSelect, onEdit, onEndpointChange, onSaveEndpoint }: { service: Service; selected: boolean; endpoint: EndpointForm; onSelect: () => void; onEdit: () => void; onEndpointChange: (value: EndpointForm) => void; onSaveEndpoint: () => void }) {
  return <article className="service-row">
    <label className="service-select"><input type="checkbox" checked={selected} onChange={onSelect} /><span className="sr-only">Select {service.name}</span></label>
    <div><strong>{service.name}</strong><small className="service-meta">{service.backendType} {service.backendVersion} · {statusLabels[service.runtime?.status ?? 0] ?? 'Unknown'}</small>{service.runtime?.errorMessage && <small className="service-error">{service.runtime.errorMessage}</small>}</div>
    <div className="row-actions"><button className="button button-secondary" onClick={onEdit}>Edit</button></div>
    <div className="endpoint-form">
      <input aria-label={`${service.name} public host`} placeholder="Public host" value={endpoint.host} onInput={event => onEndpointChange({ ...endpoint, host: event.currentTarget.value })} />
      <input aria-label={`${service.name} public port`} placeholder="Port" inputMode="numeric" value={endpoint.port} onInput={event => onEndpointChange({ ...endpoint, port: event.currentTarget.value })} />
      <input aria-label={`${service.name} TLS server name`} placeholder="TLS server name" value={endpoint.tlsServerName} onInput={event => onEndpointChange({ ...endpoint, tlsServerName: event.currentTarget.value })} />
      <button className="button button-secondary" onClick={onSaveEndpoint}>Save endpoint</button>
    </div>
  </article>
}

function TemplatesSection({ templates, names, setNames, canInstantiate, instantiate, remove }: { templates: Template[]; names: Record<string, string>; setNames: (value: Record<string, string>) => void; canInstantiate: boolean; instantiate: (template: Template) => Promise<void>; remove: (template: Template) => Promise<void> }) {
  return <section className="card panel"><div className="section-heading"><div><h2>Service templates</h2><p className="muted">Save a create-only service configuration as a reusable template.</p></div></div>
    {templates.length ? templates.map(template => <div className="template-row" key={template.id}><div><strong>{template.name}</strong><p className="muted">{template.backendType} {template.backendVersion}</p></div><div className="row-actions"><input placeholder="New service name" value={names[template.id] ?? ''} onInput={event => setNames({ ...names, [template.id]: event.currentTarget.value })} /><button className="button button-secondary" disabled={!canInstantiate} onClick={() => void instantiate(template)}>Instantiate</button><button className="button button-danger" onClick={() => void remove(template)}>Delete</button></div></div>) : <p className="muted">No templates saved yet.</p>}
  </section>
}

function UsersSection({ users, form, setForm, create, issuedToken, clearToken, rotate, services, bindings, toggleBinding, setError }: { users: User[]; form: UserForm; setForm: (value: UserForm) => void; create: (event: Event) => Promise<void>; issuedToken: string; clearToken: () => void; rotate: (user: User) => Promise<void>; services: Service[]; bindings: Record<string, string[]>; toggleBinding: (userId: string, serviceId: string) => Promise<void>; setError: (value: string) => void }) {
  return <section className="card panel"><div className="section-heading"><div><h2>Users and access</h2><p className="muted">Create accounts and grant services from the selected node.</p></div></div>
    <form className="form-grid" onSubmit={create}>
      <label>Username<input required value={form.username} onInput={event => setForm({ ...form, username: event.currentTarget.value })} /></label>
      <label>Password<input required type="password" value={form.password} onInput={event => setForm({ ...form, password: event.currentTarget.value })} /></label>
      <label>Role<select value={form.role} onChange={event => setForm({ ...form, role: event.currentTarget.value as UserForm['role'] })}><option value="User">User</option><option value="Admin">Admin</option></select></label>
      <label>Traffic limit (bytes)<input inputMode="numeric" value={form.trafficLimitBytes} onInput={event => setForm({ ...form, trafficLimitBytes: event.currentTarget.value })} /></label>
      <label>Expires at<input type="datetime-local" value={form.expiresAtUtc} onInput={event => setForm({ ...form, expiresAtUtc: event.currentTarget.value })} /></label>
      <label className="toggle-label">Enabled<input type="checkbox" checked={form.enabled} onChange={event => setForm({ ...form, enabled: event.currentTarget.checked })} /></label>
      <div className="row-actions"><button className="button button-primary">Create user</button></div>
    </form>
    {issuedToken && <SensitiveToken token={issuedToken} clear={clearToken} setError={setError} />}
    <div className="user-list">{users.map(item => <article className="user-row" key={item.id}><div><strong>{item.username}</strong><span className="service-meta">{item.role} · {item.enabled ? 'Enabled' : 'Disabled'} · {item.trafficLimitBytes === null ? 'Unlimited' : bytes(item.trafficLimitBytes)} · {item.expiresAtUtc ? new Date(item.expiresAtUtc).toLocaleDateString() : 'No expiration'}</span><div className="grant-list">{services.map(service => <label key={service.id} className="toggle-label"><input type="checkbox" checked={bindings[item.id]?.includes(service.id) ?? false} onChange={() => void toggleBinding(item.id, service.id)} />{service.name}</label>)}</div></div><button className="button button-secondary" onClick={() => void rotate(item)}>Rotate token</button></article>)}</div>
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
  const saveTemplate = async () => { try { await saveAsTemplate(configFor(form)) } catch (reason) { setError(messageFor(reason, 'Unable to save template')) } }
  return <form className="card panel form-grid" onSubmit={event => void save(event)}><h2>{editing ? 'Edit' : 'Create'} {form.backendType} service</h2>
    <label>Backend type<select value={form.backendType} disabled={!!editing} onChange={event => changeBackend(event.currentTarget.value)}><BackendOptions /></select></label>
    {keys.map(key => <label key={key}>{key}<input required={key === 'password' || key === 'realityPrivateKey'} type={secret(key) ? 'password' : 'text'} value={String(form[key as keyof ServiceFormState] ?? '')} onInput={event => change(key, event.currentTarget.value)} /></label>)}
    <div className="row-actions"><button className="button button-primary">Save</button>{!editing && <button className="button button-secondary" type="button" onClick={() => void saveTemplate()}>Save as template</button>}<button className="button button-secondary" type="button" onClick={cancel}>Cancel</button></div>
  </form>
}

function UserPanel({ request, setError }: { request: Request; setError: (value: string) => void }) {
  const [me, setMe] = useState<User | null>(null)
  const [usage, setUsage] = useState<Usage[]>([])
  const [issuedToken, setIssuedToken] = useState('')
  useEffect(() => { void (async () => { try { const [account, rows] = await Promise.all([request<User>('/api/user/v1/me'), request<Usage[]>('/api/user/v1/usage')]); setMe(account); setUsage(rows) } catch (reason) { setError(messageFor(reason, 'Unable to load account')) } })() }, [])
  const rotate = async () => { try { const result = await request<{ subscriptionToken: string }>('/api/user/v1/subscription-token/rotate', { method: 'POST' }); setIssuedToken(result.subscriptionToken) } catch (reason) { setError(messageFor(reason, 'Unable to rotate subscription token')) } }
  const total = usage.reduce((sum, row) => sum + row.uploadBytes + row.downloadBytes, 0)
  return <><section className="intro"><div><p className="eyebrow">Account</p><h1>Your subscription.</h1><p className="muted">Usage, account limits, and revocable subscription URLs.</p></div></section><div className="stats-grid"><Stat label="Usage" value={bytes(total)} note="Upload + download" /><Stat label="Limit" value={me?.trafficLimitBytes === null ? 'Unlimited' : bytes(me?.trafficLimitBytes ?? 0)} note="Traffic allowance" /><Stat label="Expiration" value={me?.expiresAtUtc ? new Date(me.expiresAtUtc).toLocaleDateString() : 'None'} note="Account expiration" /></div><section className="card panel"><div className="section-heading"><h2>Subscription links</h2><button className="button button-primary" onClick={() => void rotate()}>Issue new token</button></div>{issuedToken && <SensitiveToken token={issuedToken} clear={() => setIssuedToken('')} setError={setError} />}</section></>
}

function BackendOptions() { return <><option value="hysteria2">hysteria2</option><option value="xray">xray</option><option value="mihomo">mihomo</option><option value="sing-box">sing-box</option></> }
function SensitiveToken({ token, clear, setError }: { token: string; clear: () => void; setError: (value: string) => void }) { const links = ['raw', 'base64', 'mihomo', 'singbox'].map(format => `${location.origin}/s/${encodeURIComponent(token)}?format=${format}`); const copy = async (link: string) => { try { await navigator.clipboard.writeText(link) } catch (reason) { setError(messageFor(reason, 'Unable to copy subscription URL')) } }; return <aside className="token-result"><strong>Copy now — this token cannot be recovered.</strong>{links.map(link => <p key={link}><code>{link}</code><button className="button button-secondary" onClick={() => void copy(link)}>Copy</button></p>)}<button className="button button-secondary" onClick={clear}>Hide</button></aside> }
function Stat({ label, value, note }: { label: string; value: string; note: string }) { return <article className="card stat-card"><span className="stat-label">{label}</span><strong>{value}</strong><span className="muted">{note}</span></article> }
function Shell({ children, theme, setTheme, status, onEnd }: { children: ComponentChildren; theme: Theme; setTheme: (theme: Theme) => void; status: string; onEnd?: () => void }) { return <div className="app-shell"><header className="topbar"><a className="brand" href="/"><span className="brand-mark">H</span>HyPanel</a><div className="topbar-actions"><span className="status"><span className="status-dot" />{status}</span><div className="theme-picker">{themes.map(value => <button key={value} className={theme === value ? 'theme-button active' : 'theme-button'} onClick={() => setTheme(value)}>{value[0].toUpperCase() + value.slice(1)}</button>)}</div>{onEnd && <button className="button button-secondary" onClick={() => void onEnd()}>End session</button>}</div></header><main className="content">{children}</main></div> }
function messageFor(reason: unknown, fallback: string) { return reason instanceof Error ? reason.message : fallback }
