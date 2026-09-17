export type Theme = 'light' | 'dark' | 'system'
export type NodeTab = 'overview' | 'services' | 'network' | 'logs' | 'settings'
export type AdminRoute = 'overview' | 'nodes' | 'templates' | 'users' | 'settings' | `nodes/${string}/${NodeTab}`
export type AppRoute = AdminRoute | 'subscription'
export type Role = 'Admin' | 'User'
export type ServerUpdate = { currentVersion: string; latestVersion: string | null; deploymentMode: 'BareMetal' | 'Docker'; updateAvailable: boolean; status: string; error: string | null }

export type User = {
  id: string
  username: string
  role: Role
  enabled: boolean
  trafficLimitBytes: number | null
  expiresAtUtc: string | null
}

export type NodeMetrics = {
  observedAt: string
  uptimeSeconds: number
  cpuUsagePercent: number
  memoryTotalBytes: number
  memoryAvailableBytes: number
  diskTotalBytes: number
  diskAvailableBytes: number
  networkUploadBytes: number
  networkDownloadBytes: number
}

export type Node = {
  id: string
  displayName: string
  agentId: string | null
  online: boolean
  lastSeenAt: string | null
  reportedVersion: string | null
  platform: string | null
  desiredRevision: number
  appliedRevision: number | null
  metrics: NodeMetrics | null
  agentUpdatePolicy: 'Manual' | 'Auto'
  latestAgentVersion: string | null
  desiredAgentVersion: string | null
  agentUpdateStatus: 'UpToDate' | 'Available' | 'Requested' | 'Downloading' | 'Staged' | 'Applying' | 'WaitingForReconnect' | 'Succeeded' | 'Failed'
  agentUpdateError: string | null
}

export type Runtime = {
  status: number
  backendVersion: string | null
  trafficUploadBytes: number | null
  trafficDownloadBytes: number | null
  observedAtUtc: string
  errorCode: string | null
  errorMessage: string | null
}

export type Service = {
  id: string
  name: string
  backendType: string
  backendVersion: string
  enabled: boolean
  configSchemaVersion: number
  configJson: string
  runtime: Runtime | null
  backendUpdatePolicy: 'Manual' | 'Auto'
  latestBackendVersion: string | null
}

export type Usage = {
  userId: string
  serviceId: string
  uploadBytes: number
  downloadBytes: number
  updatedAtUtc: string
}
export type UserServiceAccess = { serviceId: string; credentialStatus: 'Active' | 'Revoked' | 'Unsupported'; multiUser: boolean; perUserTraffic: boolean; trafficStats: boolean }

export type HealthIssue = {
  kind: string
  nodeId: string
  nodeDisplayName: string
  serviceId: string | null
  serviceName: string | null
  errorCode: string | null
  errorMessage: string | null
}

export type HealthSummary = {
  observedAtUtc: string
  counts: {
    nodesTotal: number
    nodesOnline: number
    nodesDrifted: number
    servicesTotal: number
    servicesRunning: number
    servicesFailed: number
    servicesStoppedOrUnknown: number
  }
  issues: HealthIssue[]
}

export type Template = {
  id: string
  name: string
  backendType: string
  backendVersion: string
  configSchemaVersion: number
  configJson: string
}

export type PublicEndpoint = { host: string; port: number; tlsServerName: string | null }
export type ServiceDiagnostic = { commandId: string; status: 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Expired'; createdAtUtc: string; startedAtUtc: string | null; completedAtUtc: string | null; expiresAtUtc: string | null; errorCode: string | null; errorMessage: string | null; output: string | null }
export type NodeIdentity = Pick<Node, 'id' | 'displayName'>
export type ServiceRef = Service & { nodeId: string; nodeName: string }
export type UserForm = { username: string; password: string; role: Role; enabled: boolean; trafficLimitBytes: string; expiresAtUtc: string }

export type HyForm = { backendType: 'hysteria2'; name: string; version: string; listenHost: string; port: string; certificatePath: string; privateKeyPath: string; authPassword: string; masqueradeUrl: string; obfsPassword: string; upMbps: string; downMbps: string }
export type XrayForm = { backendType: 'xray'; name: string; version: string; listenHost: string; port: string; realityPrivateKey: string; realityPublicKey: string; shortId: string; serverName: string; destination: string; fingerprint: string }
export type ShadowsocksForm = { backendType: 'mihomo' | 'sing-box'; name: string; version: string; listenHost: string; port: string; password: string }
export type ServiceForm = HyForm | XrayForm | ShadowsocksForm
export type ServicePayload = { name: string; backendType: string; backendVersion: string; configSchemaVersion: 1; configJson: string }
