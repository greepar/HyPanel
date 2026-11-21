import type { BackendDefinition, BackendFeature, BackendField, ExitForm, RouteForm, Service, ServiceForm, ServicePayload } from './domain'

// Display-only placeholders so existing views paint correctly before the server definitions arrive. Forms and payloads
// always come from the server-provided definitions, so a new backend never requires a frontend change.
const displayFallback: Record<string, { name: string; core: string; protocol: string; description: string; badge: string; features: BackendFeature[] }> = {
  hysteria2: { name: 'Hysteria 2 官方服务端', core: 'Hysteria 2', protocol: 'Hysteria 2 / QUIC', description: '适合高延迟或不稳定网络。', badge: 'HY2', features: ['multiUser', 'certificate', 'portHopping'] },
  xray: { name: 'Xray REALITY', core: 'Xray-core', protocol: 'VLESS + TCP + REALITY', description: 'VLESS Vision 与 REALITY 握手。', badge: 'XR', features: ['multiUser', 'routes', 'relay'] },
}

let definitions: BackendDefinition[] = []

export const setBackendDefinitions = (value: BackendDefinition[]) => { definitions = value }
export const backendDefinitions = () => definitions

export function backendFor(value: string): BackendDefinition {
  const found = definitions.find(item => item.backendType === value)
  if (found) return found
  const fallback = displayFallback[value]
  return {
    backendType: value,
    name: fallback?.name ?? value,
    core: fallback?.core ?? value,
    protocol: fallback?.protocol ?? '未知协议',
    description: fallback?.description ?? '未知后端',
    badge: fallback?.badge ?? '?',
    defaultVersion: null,
    fields: [],
    engine: value,
    protocolId: value,
    features: fallback?.features ?? [],
  }
}

/** True when the backend supports the feature (from the server's definitions once loaded). */
export const supports = (backendType: string, feature: BackendFeature) => backendFor(backendType).features.includes(feature)

export function parseConfig(json: string): Record<string, unknown> {
  try { return JSON.parse(json) as Record<string, unknown> } catch { return {} }
}

export function emptyService(definition: BackendDefinition): ServiceForm {
  const values: Record<string, string> = {}
  for (const field of definition.fields) if (field.defaultValue != null) values[field.key] = field.defaultValue
  return { backendType: definition.backendType, name: '', version: definition.defaultVersion ?? '', values, routes: [] }
}

export const directExit = (): ExitForm => ({ type: '', host: '', port: '', username: '', password: '' })

/** The exit stored as flat upstreamProxy* keys (the service itself and each route use the same names). */
export function exitFrom(source: Record<string, unknown>): ExitForm {
  const text = (key: string) => source[key] == null ? '' : String(source[key])
  const type = text('upstreamProxyType')
  return {
    type: type === 'http' || type === 'https' || type === 'socks5' ? type : '',
    host: text('upstreamProxyHost'), port: text('upstreamProxyPort'),
    username: text('upstreamProxyUsername'), password: text('upstreamProxyPassword'),
  }
}

/** Writes the exit back as flat keys; a direct exit writes nothing. `label` names the line in error messages. */
export function writeExit(exit: ExitForm, target: Record<string, unknown>, label: string) {
  if (!exit.type) return
  const host = exit.host.trim()
  const port = Number(exit.port.trim())
  if (!host) throw new Error(`请填写${label}的代理地址。`)
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error(`${label}的代理端口必须在 1–65535 之间。`)
  const username = exit.username.trim()
  if (exit.password && !username) throw new Error(`${label}填写了代理密码时，还需要填写用户名。`)
  target.upstreamProxyType = exit.type
  target.upstreamProxyHost = host
  target.upstreamProxyPort = port
  if (username) target.upstreamProxyUsername = username
  if (exit.password) target.upstreamProxyPassword = exit.password
}

export const newRoute = (index: number): RouteForm => ({ id: crypto.randomUUID(), name: `线路 ${index}`, exit: directExit() })

/** Flat config keys of the optional upstream proxy every backend supports. */
const upstreamProxyKeys = ['upstreamProxyType', 'upstreamProxyHost', 'upstreamProxyPort', 'upstreamProxyUsername', 'upstreamProxyPassword']

export function formFor(service: Service, definition: BackendDefinition): ServiceForm {
  const config = parseConfig(service.configJson)
  const values: Record<string, string> = {}
  for (const field of definition.fields) {
    if (field.configKey == null) continue
    const raw = config[field.configKey]
    values[field.key] = raw == null ? '' : String(raw)
  }
  for (const key of upstreamProxyKeys) values[key] = config[key] == null ? '' : String(config[key])
  if (supports(service.backendType, 'relay')) {
    values.relayNodeId = typeof config.relayNodeId === 'string' ? config.relayNodeId : ''
    values.relayPort = config.relayPort == null ? '' : String(config.relayPort)
  }
  const routes = Array.isArray(config.routes)
    ? (config.routes as Record<string, unknown>[]).filter(item => item && typeof item.id === 'string')
        .map(item => ({ id: String(item.id), name: String(item.name ?? ''), exit: exitFrom(item) }))
    : []
  return { backendType: service.backendType, name: service.name, version: service.backendVersion, values, routes }
}

export function payloadFor(form: ServiceForm, definition: BackendDefinition): ServicePayload {
  if (!form.name.trim()) throw new Error('请填写服务名称。')
  if (!form.version.trim()) throw new Error('请填写后端版本。')
  const config: Record<string, unknown> = {}
  for (const field of definition.fields) {
    if (field.configKey == null) continue
    if (field.kind === 'fixed') { config[field.configKey] = fixedValue(field); continue }
    const raw = (form.values[field.key] ?? '').trim()
    if (!raw) {
      if (field.required) throw new Error(`请填写${field.label}。`)
      continue
    }
    if (field.kind === 'number') {
      const value = Number(raw)
      if (!Number.isFinite(value)) throw new Error(`请填写有效的${field.label}。`)
      if (field.min != null && value < field.min) throw new Error(`${field.label}不能小于 ${field.min}。`)
      if (field.max != null && value > field.max) throw new Error(`${field.label}不能大于 ${field.max}。`)
      if (field.min != null && field.max != null && !Number.isInteger(value)) throw new Error(`${field.label}必须是整数。`)
      config[field.configKey] = value
      continue
    }
    config[field.configKey] = raw
  }
  if (supports(form.backendType, 'relay') && form.values.relayNodeId) {
    const port = Number((form.values.relayPort ?? '').trim())
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('请填写 1–65535 之间的转发端口。')
    config.relayNodeId = form.values.relayNodeId
    config.relayPort = port
  }
  writeExit(exitFrom(form.values), config, '默认线路')
  if (supports(form.backendType, 'routes') && form.routes.length) {
    const names = new Set<string>([form.name.trim().toLowerCase()])
    config.routes = form.routes.map(route => {
      const name = route.name.trim()
      if (!name) throw new Error('请填写每条线路的名称。')
      if (name.length > 32) throw new Error(`线路名称“${name}”太长了，最多 32 个字符。`)
      if (names.has(name.toLowerCase())) throw new Error(`线路名称“${name}”重复了。`)
      names.add(name.toLowerCase())
      const item: Record<string, unknown> = { id: route.id, name }
      writeExit(route.exit, item, `线路“${name}”`)
      return item
    })
  }
  return { name: form.name.trim(), backendType: form.backendType, backendVersion: form.version.trim(), configSchemaVersion: 1, configJson: JSON.stringify(config) }
}

function fixedValue(field: BackendField): unknown {
  if (field.fixedKind === 'boolean') return field.fixed === 'true'
  if (field.fixedKind === 'number') return Number(field.fixed)
  return field.fixed
}

export const serviceFacts = (service: Service) => {
  const c = parseConfig(service.configJson)
  const listen = `${String(c.listenHost ?? '0.0.0.0')}:${String(c.listenPort ?? '—')}`
  if (service.backendType === 'hysteria2') return [listen, `${c.upMbps ?? '?'} / ${c.downMbps ?? '?'} Mbps`, c.obfsPassword ? 'Salamander 混淆' : '无混淆']
  if (service.backendType === 'xray') return [listen, String(c.serverName ?? '未设置 SNI'), 'Vision + REALITY']
  return [listen, '自定义后端', '']
}
