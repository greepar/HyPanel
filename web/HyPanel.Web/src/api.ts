import type { HealthSummary, Node, NodeIdentity, PublicEndpoint, ServerUpdate, Service, ServiceDiagnostic, Template, Usage, User, UserForm } from './domain'

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

export class ApiClient {
  constructor(private token: () => string, private unauthorized: () => void) {}
  async request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const bearer = path === '/api/auth/v1/login' ? '' : this.token()
    const response = await fetch(path, { ...init, headers: { ...(init.body ? { 'Content-Type': 'application/json' } : {}), ...(bearer ? { Authorization: `Bearer ${bearer}` } : {}), ...init.headers } })
    if (!response.ok) {
      if (response.status === 401 && path !== '/api/auth/v1/login') this.unauthorized()
      const labels: Record<number, string> = { 400: '提交内容无效，请检查表单。', 401: '登录已失效，请重新登录。', 403: '当前账户没有此操作权限。', 404: '请求的资源不存在。', 409: '名称或状态发生冲突。' }
      throw new ApiError(response.status, labels[response.status] ?? `请求失败（${response.status}）`)
    }
    return (response.status === 204 ? undefined : await response.json()) as T
  }
  login = (username: string, password: string) => this.request<{ token: string; expiresAtUtc: string; user: User }>('/api/auth/v1/login', { method: 'POST', body: JSON.stringify({ username, password }) })
  async validateAdminToken(token: string): Promise<void> {
    const response = await fetch('/api/admin/v1/nodes', { headers: { Authorization: `Bearer ${token}` } })
    if (!response.ok) throw new ApiError(response.status, response.status === 401 ? '管理员令牌无效。' : `验证失败（${response.status}）`)
  }
  nodes = () => this.request<Node[]>('/api/admin/v1/nodes')
  health = () => this.request<HealthSummary>('/api/admin/v1/health-summary')
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
  usage = () => this.request<Usage[]>('/api/admin/v1/usage')
  templates = () => this.request<Template[]>('/api/admin/v1/service-templates')
  serverUpdate = () => this.request<ServerUpdate>('/api/admin/v1/server-update')
  updateServer = () => this.request<{ version: string }>('/api/admin/v1/server-update', { method: 'POST' })
  createUser = (form: UserForm) => this.request<{ user: User; subscriptionToken: string }>('/api/admin/v1/users', { method: 'POST', body: JSON.stringify({ ...form, trafficLimitBytes: form.trafficLimitBytes ? Number(form.trafficLimitBytes) : null, expiresAtUtc: form.expiresAtUtc || null }) })
}
