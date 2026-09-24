import { useEffect, useRef, useState } from 'preact/hooks'
import QRCode from 'qrcode'
import type { ApiClient } from './api'
import type { Usage, User } from './domain'
import { PasskeyPanel } from './passkey'
import { copyText, Empty, ErrorState, formatBytes, Loading, messageFor, Page, Stat } from './ui'

export function SubscriptionPage({ api, setError }: { api: ApiClient; setError: (value: string) => void }) {
  const [me, setMe] = useState<User | null>(null)
  const [usage, setUsage] = useState<Usage[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState('')

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

  const total = usage.reduce((sum, row) => sum + row.uploadBytes + row.downloadBytes, 0)
  const remaining = me?.trafficLimitBytes == null ? null : Math.max(0, me.trafficLimitBytes - total)
  const percent = me?.trafficLimitBytes ? Math.min(100, total / me.trafficLimitBytes * 100) : 0

  return <Page
    eyebrow="订阅中心"
    title="我的订阅"
  >
    {loading ? <Loading /> : loadError ? <ErrorState message={loadError} retry={() => void load()} /> : me ? <>
      <div className="stats-grid">
        <Stat label="已用流量" value={formatBytes(total)} note="上传与下载合计" />
        <Stat label="剩余额度" value={remaining === null ? '不限' : formatBytes(remaining)} note={me.trafficLimitBytes === null ? '账户未设置流量上限' : `总额度 ${formatBytes(me.trafficLimitBytes)}`} />
        <Stat label="账户有效期" value={me.expiresAtUtc ? new Date(me.expiresAtUtc).toLocaleDateString('zh-CN') : '永久'} note={me.enabled ? '账户状态正常' : '账户已停用'} />
      </div>
      <OwnSubscription api={api} setError={setError} />
      <section className="card panel usage-card">
        <div className="section-heading"><div><h2>流量额度</h2><p>所有已授权服务的累计用量。</p></div><strong>{me.trafficLimitBytes === null ? '不限额度' : `${percent.toFixed(1)}%`}</strong></div>
        {me.trafficLimitBytes !== null && <div className="progress" role="progressbar" aria-label="流量额度使用率" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Number(percent.toFixed(1))}><span style={{ width: `${percent}%` }} /></div>}
      </section>
      <section className="card panel">
        <div className="section-heading"><div><h2>服务用量</h2><p>数据由 Agent 按服务幂等上报。</p></div></div>
        {usage.length ? <div className="usage-list">{usage.map(row => <div key={row.serviceId}><code>{row.serviceId}</code><span>上传 {formatBytes(row.uploadBytes)}</span><span>下载 {formatBytes(row.downloadBytes)}</span><strong>{formatBytes(row.uploadBytes + row.downloadBytes)}</strong></div>)}</div> : <Empty title="暂无用量数据" description="服务开始产生并上报流量后，这里会显示明细。" />}
      </section>
      <PasskeyPanel api={api} setError={setError} />
    </> : null}
  </Page>
}

/**
 * The signed-in account's own subscription: link, QR code and reset. Each account manages only its own link; admins
 * see theirs on the overview.
 */
export function OwnSubscription({ api, setError }: { api: ApiClient; setError: (value: string) => void }) {
  const [token, setToken] = useState<string | null>(null)
  const [state, setState] = useState<'loading' | 'ready' | 'missing'>('loading')
  const [rotating, setRotating] = useState(false)
  const rotationInFlight = useRef(false)
  useEffect(() => {
    void api.request<{ subscriptionToken: string }>('/api/user/v1/subscription-token')
      .then(result => { setToken(result.subscriptionToken); setState('ready') }, () => setState('missing'))
  }, [])
  const rotate = async () => {
    if (rotationInFlight.current || (state === 'ready' && !confirm('重置后旧链接和已导入客户端的配置都会失效，需要用新链接重新导入。继续吗？'))) return
    rotationInFlight.current = true
    setRotating(true)
    try {
      const result = await api.request<{ subscriptionToken: string }>('/api/user/v1/subscription-token/rotate', { method: 'POST' })
      setToken(result.subscriptionToken)
      setState('ready')
      setError('已签发新订阅链接。')
    } catch (reason) {
      setError(messageFor(reason, '无法签发订阅链接'))
    } finally {
      rotationInFlight.current = false
      setRotating(false)
    }
  }
  return <section className="card panel usage-card">
    <div className="section-heading">
      <div><h2>我的订阅</h2><p>导入 Clash / Mihomo 客户端即可使用所在用户组的全部节点。</p></div>
      <button className="button button-secondary" type="button" disabled={rotating || state === 'loading'} onClick={() => void rotate()}>{rotating ? '正在重置…' : state === 'missing' ? '签发订阅链接' : '重置链接'}</button>
    </div>
    {state === 'loading' ? <Loading /> : token ? <SubscriptionLinks token={token} setError={setError} /> : <Empty title="还没有可显示的订阅链接" description="旧版本签发的链接无法再次显示，点击“签发订阅链接”获取新链接。" />}
  </section>
}

/** The user's Clash/Mihomo subscription link with a QR code. */
export function SubscriptionLinks({ token, setError }: { token: string; setError: (value: string) => void }) {
  const [qr, setQr] = useState('')
  const link = `${location.origin}/s/${encodeURIComponent(token)}`
  useEffect(() => {
    void QRCode.toDataURL(link, { width: 260, margin: 2, errorCorrectionLevel: 'M', color: { dark: '#171717', light: '#ffffff' } })
      .then(setQr)
      .catch(reason => setError(messageFor(reason, '无法生成二维码')))
  }, [link])
  const copy = async () => {
    try { await copyText(link); setError('订阅链接已复制。') }
    catch (reason) { setError(messageFor(reason, '无法复制')) }
  }
  return <div className="subscription-delivery">
    <div className="subscription-link">
      <span>Clash / Mihomo 订阅（YAML）</span>
      <code>{link}</code>
      <div className="row-actions">
        <button className="button button-primary" type="button" onClick={() => void copy()}>复制链接</button>
        <a className="button button-secondary" href={`clash://install-config?url=${encodeURIComponent(link)}`}>一键导入 Clash</a>
      </div>
      <small className="muted">适用于 Clash Verge、Mihomo Party、ClashX Meta、Stash 等客户端；已包含分流规则，客户端会显示剩余流量和到期时间。</small>
    </div>
    <aside className="qr-panel">
      <span>扫码导入</span>
      {qr ? <img src={qr} alt="订阅二维码" /> : <Loading label="生成二维码…" />}
    </aside>
  </div>
}
