import type { BackendDefinition, BackendField, Service, ServiceForm, ServicePayload } from './domain'

// Display-only placeholders so existing views paint correctly before the server definitions arrive. Forms and payloads
// always come from the server-provided definitions, so a new backend never requires a frontend change.
const displayFallback: Record<string, { name: string; core: string; protocol: string; description: string; badge: string }> = {
  hysteria2: { name: 'Hysteria 2 官方服务端', core: 'Hysteria 2', protocol: 'Hysteria 2 / QUIC', description: '适合高延迟或不稳定网络。', badge: 'HY2' },
  xray: { name: 'Xray REALITY', core: 'Xray-core', protocol: 'VLESS + TCP + REALITY', description: 'VLESS Vision 与 REALITY 握手。', badge: 'XR' },
  'xray-ss': { name: 'Xray Shadowsocks', core: 'Xray-core', protocol: 'Shadowsocks 2022 多用户', description: '每个用户独立密钥与流量统计，支持 UDP。', badge: 'SS' },
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
  }
}

export function parseConfig(json: string): Record<string, unknown> {
  try { return JSON.parse(json) as Record<string, unknown> } catch { return {} }
}

export function emptyService(definition: BackendDefinition): ServiceForm {
  const values: Record<string, string> = {}
  for (const field of definition.fields) if (field.defaultValue != null) values[field.key] = field.defaultValue
  return { backendType: definition.backendType, name: '', version: definition.defaultVersion ?? '', values }
}

export function formFor(service: Service, definition: BackendDefinition): ServiceForm {
  const config = parseConfig(service.configJson)
  const values: Record<string, string> = {}
  for (const field of definition.fields) {
    if (field.configKey == null) continue
    const raw = config[field.configKey]
    values[field.key] = raw == null ? '' : String(raw)
  }
  return { backendType: service.backendType, name: service.name, version: service.backendVersion, values }
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
  if (service.backendType === 'xray-ss') return [listen, '2022-blake3-aes-128-gcm', 'TCP + UDP · 多用户']
  return [listen, '自定义后端', '']
}
