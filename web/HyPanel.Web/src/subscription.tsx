import { useEffect, useRef, useState } from 'preact/hooks'
import QRCode from 'qrcode'
import type { ApiClient } from './api'
import type { Usage, User } from './domain'
import { Empty, ErrorState, formatBytes, Loading, messageFor, Modal, Notice, Page, Stat } from './ui'

export function SubscriptionPage({ api, setError }: { api: ApiClient; setError: (value: string) => void }) {
  const [me, setMe] = useState<User | null>(null)
  const [usage, setUsage] = useState<Usage[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState('')
  const [rotating, setRotating] = useState(false)
  const rotationInFlight = useRef(false)
  const [token, setToken] = useState<string | null>(null)

  const load = async () => {
    setLoading(true)
    setLoadError('')
    try {
      const [user, rows] = await Promise.all([
        api.request<User>('/api/user/v1/me'),
        api.request<Usage[]>('/api/user/v1/usage'),
      ])
      setMe(user)
      setUsage(rows)
    } catch (reason) {
      setLoadError(messageFor(reason, '无法加载订阅账户'))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { void load() }, [])

  const rotate = async () => {
    if (rotationInFlight.current || !confirm('签发新令牌会立即撤销所有旧订阅链接，是否继续？')) return
    rotationInFlight.current = true
    setRotating(true)
    try {
      const result = await api.request<{ subscriptionToken: string }>('/api/user/v1/subscription-token/rotate', { method: 'POST' })
      setToken(result.subscriptionToken)
    } catch (reason) {
      setError(messageFor(reason, '无法签发订阅令牌'))
    } finally {
      rotationInFlight.current = false
      setRotating(false)
    }
  }

  const total = usage.reduce((sum, row) => sum + row.uploadBytes + row.downloadBytes, 0)
  const remaining = me?.trafficLimitBytes == null ? null : Math.max(0, me.trafficLimitBytes - total)
  const percent = me?.trafficLimitBytes ? Math.min(100, total / me.trafficLimitBytes * 100) : 0

  return <Page
    eyebrow="订阅中心"
    title="我的订阅"
    description="查看账户额度、服务用量，并安全签发客户端订阅链接。"
    actions={<button className="button button-primary" type="button" disabled={loading || rotating} onClick={() => void rotate()}>{rotating ? '正在签发…' : '签发新链接'}</button>}
  >
    {loading ? <Loading /> : loadError ? <ErrorState message={loadError} retry={() => void load()} /> : me ? <>
      <div className="stats-grid">
        <Stat label="已用流量" value={formatBytes(total)} note="上传与下载合计" />
        <Stat label="剩余额度" value={remaining === null ? '不限' : formatBytes(remaining)} note={me.trafficLimitBytes === null ? '账户未设置流量上限' : `总额度 ${formatBytes(me.trafficLimitBytes)}`} />
        <Stat label="账户有效期" value={me.expiresAtUtc ? new Date(me.expiresAtUtc).toLocaleDateString('zh-CN') : '永久'} note={me.enabled ? '账户状态正常' : '账户已停用'} />
      </div>
      <section className="card panel usage-card">
        <div className="section-heading"><div><h2>流量额度</h2><p>所有已授权服务的累计用量。</p></div><strong>{me.trafficLimitBytes === null ? '不限额度' : `${percent.toFixed(1)}%`}</strong></div>
        {me.trafficLimitBytes !== null && <div className="progress" role="progressbar" aria-label="流量额度使用率" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Number(percent.toFixed(1))}><span style={{ width: `${percent}%` }} /></div>}
      </section>
      <section className="card panel">
        <div className="section-heading"><div><h2>服务用量</h2><p>数据由 Agent 按服务幂等上报。</p></div></div>
        {usage.length ? <div className="usage-list">{usage.map(row => <div key={row.serviceId}><code>{row.serviceId}</code><span>上传 {formatBytes(row.uploadBytes)}</span><span>下载 {formatBytes(row.downloadBytes)}</span><strong>{formatBytes(row.uploadBytes + row.downloadBytes)}</strong></div>)}</div> : <Empty title="暂无用量数据" description="服务开始产生并上报流量后，这里会显示明细。" />}
      </section>
    </> : null}
    {token && <OwnToken token={token} close={() => setToken(null)} setError={setError} />}
  </Page>
}

function OwnToken({ token, close, setError }: { token: string; close: () => void; setError: (value: string) => void }) {
  const formats = [['通用原始格式', 'raw'], ['Base64', 'base64'], ['Mihomo', 'mihomo'], ['sing-box', 'singbox']] as const
  const [selected, setSelected] = useState('raw')
  const [qr, setQr] = useState('')
  const selectedLink = `${location.origin}/s/${encodeURIComponent(token)}?format=${selected}`
  useEffect(() => {
    void QRCode.toDataURL(selectedLink, { width: 260, margin: 2, errorCorrectionLevel: 'M', color: { dark: '#171717', light: '#ffffff' } })
      .then(setQr)
      .catch(reason => setError(messageFor(reason, '无法生成二维码')))
  }, [selectedLink])
  const copy = async (value: string) => {
    try { await navigator.clipboard.writeText(value); setError('订阅链接已复制。') }
    catch (reason) { setError(messageFor(reason, '无法复制')) }
  }

  return <Modal title="新订阅链接" description="这些链接仅显示一次。签发新令牌后旧链接已失效。" close={close}>
    <Notice kind="success">选择客户端支持的格式并立即保存。</Notice>
    <div className="subscription-delivery">
      <fieldset className="token-list">
        <legend className="sr-only">订阅格式</legend>
        {formats.map(([label, format]) => {
          const link = `${location.origin}/s/${encodeURIComponent(token)}?format=${format}`
          return <label className={selected === format ? 'selected' : ''} key={format}>
            <input className="sr-only" type="radio" name="subscription-format" value={format} checked={selected === format} onChange={() => setSelected(format)} />
            <span>{label}</span><code>{link}</code>
            <button className="button button-secondary" type="button" onClick={event => { event.stopPropagation(); void copy(link) }}>复制</button>
          </label>
        })}
      </fieldset>
      <aside className="qr-panel">
        <span>{formats.find(([, format]) => format === selected)?.[0]}</span>
        {qr ? <img src={qr} alt={`${formats.find(([, format]) => format === selected)?.[0]} 订阅二维码`} /> : <Loading label="生成二维码…" />}
        <small>使用客户端扫描，或复制左侧链接。</small>
      </aside>
    </div>
    <footer className="modal-actions"><button className="button button-primary" type="button" onClick={close}>完成</button></footer>
  </Modal>
}
