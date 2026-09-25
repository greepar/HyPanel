import type { BackendDefinition, Backup, BackupValidation, Certificate, CertificateRequest, GlobalSettings, HealthSummary, Node, NodeIdentity, PublicEndpoint, ServerUpdate, Service, ServiceDiagnostic, Usage, User, UserGroup } from './domain'

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

export class ApiClient {
  constructor(private token: () => string, private unauthorized: () => void) {}
  async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    // Sign-in endpoints are anonymous: they never carry a token and a 401 there means bad credentials.
    const anonymous = path === '/api/auth/v1/login' || path.startsWith('/api/auth/v1/passkey/') || path === '/api/auth/v1/setup'
    const bearer = anonymous ? '' : this.token()
    const response = await fetch(path, { ...init, headers: { ...(init.body ? { 'Content-Type': 'application/json' } : {}), ...(bearer ? { Authorization: `Bearer ${bearer}` } : {}), ...init.headers } })
    if (!response.ok) {
      if (response.status === 401 && !anonymous) this.unauthorized()
      const labels: Record<number, string> = { 400: '提交内容无效，请检查表单。', 401: anonymous ? '用户名、密码或通行密钥不正确。' : '登录已失效，请重新登录。', 403: '当前账户没有此操作权限。', 404: '请求的资源不存在。', 409: '名称或状态发生冲突。', 429: '尝试次数过多，请几分钟后再试。' }
      // Prefer a specific reason when the Server provides one ({ "error": "..." }).
      let detail: string | undefined
      try { const body = await response.json() as { error?: unknown }; if (typeof body?.error === 'string') detail = body.error } catch { /* no JSON body */ }
      throw new ApiError(response.status, detail ?? labels[response.status] ?? `请求失败（${response.status}）`)
    }
    return (response.status === 204 ? undefined : await response.json()) as T
  }
  private async authenticatedFetch(path: string, init: RequestInit = {}) {
    const bearer = this.token()
    const response = await fetch(path, { ...init, headers: { ...(bearer ? { Authorization: `Bearer ${bearer}` } : {}), ...init.headers } })
    if (!response.ok) {
      if (response.status === 401) this.unauthorized()
      const labels: Record<number, string> = { 400: '提交内容无效，请检查文件。', 401: '登录已失效，请重新登录。', 403: '当前账户没有此操作权限。', 404: '请求的资源不存在。', 409: '名称或状态发生冲突。' }
      throw new ApiError(response.status, labels[response.status] ?? `请求失败（${response.status}）`)
    }
    return response
  }
  login = (username: string, password: string) => this.request<{ token: string; expiresAtUtc: string; user: User }>('/api/auth/v1/login', { method: 'POST', body: JSON.stringify({ username, password }) })
  async validateAdminToken(token: string): Promise<void> {
    const response = await fetch('/api/admin/v1/nodes', { headers: { Authorization: `Bearer ${token}` } })
    if (!response.ok) throw new ApiError(response.status, response.status === 401 ? '管理员令牌无效。' : `验证失败（${response.status}）`)
  }
  nodes = () => this.request<Node[]>('/api/admin/v1/nodes')
  health = () => this.request<HealthSummary>('/api/admin/v1/health-summary')
  deleteNode = (nodeId: string) => this.request<void>(`/api/admin/v1/nodes/${nodeId}`, { method: 'DELETE' })
  createNode = (displayName: string) => this.request<NodeIdentity>('/api/admin/v1/nodes', { method: 'POST', body: JSON.stringify({ displayName }) })
  installCommand = (nodeId: string, platform: 'unix' | 'powershell') => this.request<{ command: string }>(`/api/admin/v1/nodes/${nodeId}/install-command`, { method: 'POST', body: JSON.stringify({ platform }) })
  healthCheck = (agentId: string) => this.request<{ commandId: string }>(`/api/admin/v1/agents/${agentId}/commands/health-check`, { method: 'POST' })
  updateAgent = (nodeId: string) => this.request<{ updateId: string; version: string }>(`/api/admin/v1/nodes/${nodeId}/agent-update`, { method: 'POST' })
  updateAgents = (nodeIds: string[]) => this.request<{ version: string; updatedNodes: number }>('/api/admin/v1/nodes/agent-update', { method: 'POST', body: JSON.stringify({ nodeIds }) })
  setAgentUpdatePolicy = (nodeId: string, policy: 'Manual' | 'Auto') => this.request<void>(`/api/admin/v1/nodes/${nodeId}/agent-update-policy`, { method: 'PUT', body: JSON.stringify({ policy }) })
  services = (nodeId: string, signal?: AbortSignal) => this.request<Service[]>(`/api/admin/v1/nodes/${nodeId}/services`, { signal })
  endpoint = (nodeId: string, serviceId: string) => this.request<PublicEndpoint | undefined>(`/api/admin/v1/nodes/${nodeId}/services/${serviceId}/public-endpoint`)
  diagnostics = (nodeId: string, serviceId: string) => this.request<ServiceDiagnostic[]>(`/api/admin/v1/nodes/${nodeId}/services/${serviceId}/diagnostics`)
  collectLogs = (nodeId: string, serviceId: string) => this.request<{ commandId: string }>(`/api/admin/v1/nodes/${nodeId}/services/${serviceId}/diagnostics/logs`, { method: 'POST' })
  updateBackend = (nodeId: string, serviceId: string) => this.request<{ version: string; revision: number }>(`/api/admin/v1/nodes/${nodeId}/services/${serviceId}/backend-update`, { method: 'POST' })
  setBackendUpdatePolicy = (nodeId: string, serviceId: string, policy: 'Manual' | 'Auto') => this.request<void>(`/api/admin/v1/nodes/${nodeId}/services/${serviceId}/backend-update-policy`, { method: 'PUT', body: JSON.stringify({ policy }) })
  users = () => this.request<User[]>('/api/admin/v1/users')
  groups = () => this.request<UserGroup[]>('/api/admin/v1/groups')
  subscriptionTemplate = () => this.request<{ template: string; isCustom: boolean; defaultTemplate: string }>('/api/admin/v1/subscription-template')
  saveSubscriptionTemplate = (template: string) => this.request<{ template: string; isCustom: boolean; defaultTemplate: string }>('/api/admin/v1/subscription-template', { method: 'PUT', body: JSON.stringify({ template }) })
  resetSubscriptionTemplate = () => this.request<{ template: string; isCustom: boolean; defaultTemplate: string }>('/api/admin/v1/subscription-template', { method: 'DELETE' })
  saveGroup = (id: string | null, value: { name: string; autoIncludeNewServices: boolean; serviceIds: string[] }) => this.request<UserGroup>(id ? `/api/admin/v1/groups/${id}` : '/api/admin/v1/groups', { method: id ? 'PUT' : 'POST', body: JSON.stringify(value) })
  deleteGroup = (id: string) => this.request<void>(`/api/admin/v1/groups/${id}`, { method: 'DELETE' })
  usage = () => this.request<Usage[]>('/api/admin/v1/usage')
  serverUpdate = () => this.request<ServerUpdate>('/api/admin/v1/server-update')
  updateServer = () => this.request<{ version: string }>('/api/admin/v1/server-update', { method: 'POST' })
  settings = () => this.request<GlobalSettings>('/api/admin/v1/settings')
  backends = async (): Promise<BackendDefinition[]> => {
    const raw = await this.request<{
      backendType: string
      displayName: string
      core: string
      protocol: string
      description: string
      badge: string
      defaultVersion: string | null
      fields: BackendDefinition['fields']
    }[]>('/api/admin/v1/backends')
    return raw.map(item => ({
      backendType: item.backendType,
      name: item.displayName,
      core: item.core,
      protocol: item.protocol,
      description: item.description,
      badge: item.badge,
      defaultVersion: item.defaultVersion,
      fields: item.fields,
    }))
  }
  generateBackendDefaults = (backendType: string, nodeId?: string) => this.request<{ backendType: string; values: Record<string, string> }>(`/api/admin/v1/backends/${encodeURIComponent(backendType)}/defaults${nodeId ? `?nodeId=${encodeURIComponent(nodeId)}` : ''}`, { method: 'POST' })
  updateSettings = (value: Pick<GlobalSettings, 'agentUpdateDefaultPolicy' | 'backendUpdateDefaultPolicy' | 'githubMirrorBaseUrl' | 'panelUrl'>) => this.request('/api/admin/v1/settings', { method: 'PUT', body: JSON.stringify(value) })
  certificates = () => this.request<Certificate[]>('/api/admin/v1/certificates')
  createCertificate = (value: CertificateRequest) => this.request<Certificate>('/api/admin/v1/certificates', { method: 'POST', body: JSON.stringify(value) })
  replaceCertificate = (id: string, value: CertificateRequest) => this.request<Certificate>(`/api/admin/v1/certificates/${id}`, { method: 'PUT', body: JSON.stringify(value) })
  deleteCertificate = (id: string) => this.request<void>(`/api/admin/v1/certificates/${id}`, { method: 'DELETE' })
  backups = () => this.request<Backup[]>('/api/admin/v1/backups')
  createBackup = () => this.request<Backup>('/api/admin/v1/backups', { method: 'POST' })
  deleteBackup = (id: string) => this.request<void>(`/api/admin/v1/backups/${encodeURIComponent(id)}`, { method: 'DELETE' })
  async downloadBackup(id: string): Promise<{ blob: Blob; filename: string }> {
    const response = await this.authenticatedFetch(`/api/admin/v1/backups/${encodeURIComponent(id)}/download`)
    const disposition = response.headers.get('Content-Disposition') ?? ''
    const encoded = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1]
    const plain = disposition.match(/filename="?([^";]+)"?/i)?.[1]
    return { blob: await response.blob(), filename: encoded ? decodeURIComponent(encoded) : plain ?? `backup-${id}.gz` }
  }
  async validateBackup(file: File): Promise<BackupValidation> {
    const response = await this.authenticatedFetch('/api/admin/v1/backups/validate', { method: 'POST', headers: { 'Content-Type': 'application/gzip' }, body: file })
    return await response.json() as BackupValidation
  }
  restoreBackup = (validationId: string) => this.request<{ status: string }>('/api/admin/v1/backups/restore', { method: 'POST', body: JSON.stringify({ validationId, confirmation: 'RESTORE' }) })
  createUser = (payload: Record<string, unknown>) => this.request<{ user: User; subscriptionToken: string }>('/api/admin/v1/users', { method: 'POST', body: JSON.stringify(payload) })
}
