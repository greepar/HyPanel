export type Theme = 'light' | 'dark' | 'system'
export type NodeTab = 'overview' | 'services' | 'network' | 'logs' | 'settings'
export type AdminRoute = 'overview' | 'nodes' | 'users' | 'settings' | `nodes/${string}/${NodeTab}`
export type AppRoute = AdminRoute | 'subscription'
export type Role = 'Admin' | 'User'
export type ServerUpdate = { currentVersion: string; latestVersion: string | null; deploymentMode: 'BareMetal' | 'Docker'; updateAvailable: boolean; status: string; error: string | null }
export type GlobalSettings = { agentUpdateDefaultPolicy: 'Manual' | 'Auto'; backendUpdateDefaultPolicy: 'Manual' | 'Auto'; githubMirrorBaseUrl: string | null; agentReleaseVersion: string | null; backendReleases: Record<string, string>; server: ServerUpdate; dataDirectory: string; databaseSizeBytes: number }
export type CertificateKind = 'Upload' | 'Path' | 'Acme'
export type AcmeChallenge = 'http' | 'tls' | 'cloudflare'
export type Certificate = { id: string; name: string; kind: CertificateKind; createdAtUtc: string; notBeforeUtc: string | null; expiresAtUtc: string | null; fingerprint: string | null; subject: string | null; san: string[]; usedBy: number; certificatePath: string | null; privateKeyPath: string | null; acmeEmail: string | null; acmeChallenge: AcmeChallenge | null; hasAcmeDnsToken: boolean }
export type CertificateRequest = { name: string; kind: CertificateKind; certificatePem?: string; privateKeyPem?: string; certificatePath?: string; privateKeyPath?: string; domain?: string; acmeEmail?: string; acmeChallenge?: AcmeChallenge; acmeDnsToken?: string }
export type Backup = { id: string; createdAtUtc: string; sizeBytes: number; serverVersion: string; schemaVersion: number }
export type BackupValidation = { validationId: string; valid: boolean; error: string | null; formatVersion: number | null; createdAtUtc: string | null; serverVersion: string | null; schemaVersion: number | null; databaseSizeBytes: number | null; databaseIntegrity: boolean; masterKeyCompatible: boolean }

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
  publicIpv4?: string | null
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


export type PublicEndpoint = { host: string; port: number; tlsServerName: string | null }
export type ServiceDiagnostic = { commandId: string; status: 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Expired'; createdAtUtc: string; startedAtUtc: string | null; completedAtUtc: string | null; expiresAtUtc: string | null; errorCode: string | null; errorMessage: string | null; output: string | null }
export type NodeIdentity = Pick<Node, 'id' | 'displayName'>
export type ServiceRef = Service & { nodeId: string; nodeName: string }
export type UserForm = { username: string; password: string; role: Role; enabled: boolean; trafficLimitBytes: string; expiresAtUtc: string }

export type BackendFieldKind = 'text' | 'password' | 'number' | 'certificate' | 'select' | 'fixed'
export type BackendField = {
  key: string
  configKey: string | null
  label: string
  kind: BackendFieldKind
  required: boolean
  secret: boolean
  generate: boolean
  defaultValue: string | null
  placeholder: string | null
  options: string[]
  fixed: string | null
  fixedKind: 'string' | 'number' | 'boolean' | null
  min: number | null
  max: number | null
  section: string
}
export type BackendDefinition = {
  backendType: string
  name: string
  core: string
  protocol: string
  description: string
  badge: string
  defaultVersion: string | null
  fields: BackendField[]
}

export type ServiceForm = { backendType: string; name: string; version: string; values: Record<string, string> }
export type ServicePayload = { name: string; backendType: string; backendVersion: string; configSchemaVersion: 1; configJson: string }
