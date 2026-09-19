import type { Service, ServiceForm, ServicePayload } from './domain'

export type BackendType = ServiceForm['backendType']
export type BackendDefinition = { name: string; core: string; protocol: string; description: string; badge: string }

export const backends: Record<BackendType, BackendDefinition> = {
  hysteria2: { name: 'Hysteria 2 官方服务端', core: 'Hysteria 2', protocol: 'Hysteria 2 / QUIC', description: '适合高延迟或不稳定网络。', badge: 'HY2' },
  xray: { name: 'Xray REALITY', core: 'Xray-core', protocol: 'VLESS + TCP + REALITY', description: 'VLESS Vision 与 REALITY 握手。', badge: 'XR' },
  mihomo: { name: 'Mihomo Shadowsocks', core: 'Mihomo', protocol: 'Shadowsocks 2022', description: 'Shadowsocks 2022 入站，支持 UDP。', badge: 'MI' },
  'sing-box': { name: 'sing-box Shadowsocks', core: 'sing-box', protocol: 'Shadowsocks 2022', description: 'Shadowsocks 2022 入站，支持 UDP。', badge: 'SB' },
}

export const backendFor = (value: string) => backends[value as BackendType] ?? { name: value, core: value, protocol: '未知协议', description: '未知后端', badge: '?' }
export const emptyService = (type: BackendType = 'hysteria2'): ServiceForm => type === 'hysteria2'
  ? { backendType: type, name: '', version: '2.12.2', listenHost: '0.0.0.0', port: '443', certificateId: '', authPassword: '', masqueradeUrl: 'https://example.com/', obfsPassword: '', upMbps: '100', downMbps: '100' }
  : type === 'xray'
    ? { backendType: type, name: '', version: '26.3.27', listenHost: '0.0.0.0', port: '443', realityPrivateKey: '', realityPublicKey: '', shortId: '', serverName: '', destination: '', fingerprint: 'chrome' }
    : { backendType: type, name: '', version: type === 'mihomo' ? '1.19.30' : '1.14.0', listenHost: '0.0.0.0', port: type === 'mihomo' ? '24446' : '24447', password: '' }

export function parseConfig(json: string): Record<string, unknown> {
  try { return JSON.parse(json) as Record<string, unknown> } catch { return {} }
}

export function formFor(service: Service): ServiceForm {
  const c = parseConfig(service.configJson)
  const base = { name: service.name, version: service.backendVersion, listenHost: String(c.listenHost ?? '0.0.0.0'), port: String(c.listenPort ?? 443) }
  if (service.backendType === 'xray') return { ...emptyService('xray'), ...base, realityPrivateKey: String(c.realityPrivateKey ?? ''), realityPublicKey: String(c.realityPublicKey ?? ''), shortId: String(c.shortId ?? ''), serverName: String(c.serverName ?? ''), destination: String(c.destination ?? ''), fingerprint: String(c.fingerprint ?? 'chrome') } as ServiceForm
  if (service.backendType === 'mihomo' || service.backendType === 'sing-box') return { ...emptyService(service.backendType), ...base, password: String(c.password ?? '') } as ServiceForm
  return { ...emptyService(), ...base, certificateId: String(c.certificateId ?? ''), authPassword: String(c.authPassword ?? ''), masqueradeUrl: String(c.masqueradeUrl ?? ''), obfsPassword: String(c.obfsPassword ?? ''), upMbps: String(c.upMbps ?? 100), downMbps: String(c.downMbps ?? 100) } as ServiceForm
}

export function payloadFor(form: ServiceForm): ServicePayload {
  const common = { listenHost: form.listenHost.trim(), listenPort: Number(form.port) }
  if (!form.name.trim() || !form.version.trim() || !common.listenHost || !Number.isInteger(common.listenPort) || common.listenPort < 1 || common.listenPort > 65535) throw new Error('请完整填写名称、版本和有效端口。')
  let config: Record<string, unknown>
  if (form.backendType === 'hysteria2') {
    if (!form.certificateId || !form.authPassword || !form.masqueradeUrl.trim() || Number(form.upMbps) <= 0 || Number(form.downMbps) <= 0) throw new Error('请完整填写 Hysteria 2 证书、认证和带宽配置。')
    config = { ...common, certificateId: form.certificateId, authPassword: form.authPassword, masqueradeUrl: form.masqueradeUrl, ...(form.obfsPassword ? { obfsPassword: form.obfsPassword } : {}), upMbps: Number(form.upMbps), downMbps: Number(form.downMbps) }
  } else if (form.backendType === 'xray') {
    if (!form.realityPrivateKey || !form.realityPublicKey.trim() || !form.shortId.trim() || !form.serverName.trim() || !form.destination.trim()) throw new Error('请完整填写 Xray REALITY 配置。')
    config = { ...common, flow: 'xtls-rprx-vision', realityPrivateKey: form.realityPrivateKey, realityPublicKey: form.realityPublicKey, shortId: form.shortId, serverName: form.serverName, destination: form.destination, fingerprint: form.fingerprint }
  } else {
    if (!form.password) throw new Error('请填写 Shadowsocks 2022 密钥。')
    config = { ...common, method: '2022-blake3-aes-256-gcm', password: form.password, udp: true }
  }
  return { name: form.name.trim(), backendType: form.backendType, backendVersion: form.version.trim(), configSchemaVersion: 1, configJson: JSON.stringify(config) }
}

export const serviceFacts = (service: Service) => {
  const c = parseConfig(service.configJson)
  const listen = `${String(c.listenHost ?? '0.0.0.0')}:${String(c.listenPort ?? '—')}`
  if (service.backendType === 'hysteria2') return [listen, `${c.upMbps ?? '?'} / ${c.downMbps ?? '?'} Mbps`, c.obfsPassword ? 'Salamander 混淆' : '无混淆']
  if (service.backendType === 'xray') return [listen, String(c.serverName ?? '未设置 SNI'), 'Vision + REALITY']
  return [listen, String(c.method ?? 'Shadowsocks 2022'), c.udp === false ? '仅 TCP' : 'TCP + UDP']
}
