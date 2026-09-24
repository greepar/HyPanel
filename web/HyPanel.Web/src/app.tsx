import { useEffect, useMemo, useState } from 'preact/hooks'
import { ApiClient } from './api'
import type { Theme, User } from './domain'
import { useRoute } from './router'
import { AdminApp } from './admin'
import { SubscriptionPage } from './subscription'
import { AppShell, messageFor, Notice, Splash, Toast } from './ui'
import { BrandMark } from './brand'
import { passkeySupported, signInWithPasskey } from './passkey'

const storedTheme = (): Theme => {
  const value = localStorage.getItem('hypanel-theme') as Theme
  return ['light', 'dark', 'system'].includes(value) ? value : 'system'
}

const storedToken = () => sessionStorage.getItem('hypanel-token')
  ?? sessionStorage.getItem('hypanel-session-token')
  ?? sessionStorage.getItem('hypanel-admin-token')
  ?? ''

const storedUser = (): User | null => {
  try { return JSON.parse(sessionStorage.getItem('hypanel-user') ?? sessionStorage.getItem('hypanel-session-user') ?? 'null') as User | null }
  catch { return null }
}

const storeAccountSession = (token: string, user: User) => {
  sessionStorage.setItem('hypanel-token', token)
  sessionStorage.setItem('hypanel-user', JSON.stringify(user))
  for (const key of ['hypanel-bootstrap', 'hypanel-session-token', 'hypanel-session-user', 'hypanel-admin-token']) sessionStorage.removeItem(key)
}

export function App() {
  const [theme, setTheme] = useState<Theme>(storedTheme)
  const [token, setToken] = useState(storedToken)
  const [user, setUser] = useState<User | null>(storedUser)
  const [bootstrap, setBootstrap] = useState(() => sessionStorage.getItem('hypanel-bootstrap') === 'true'
    || (!sessionStorage.getItem('hypanel-user') && !sessionStorage.getItem('hypanel-session-user') && !!sessionStorage.getItem('hypanel-admin-token')))
  const [checkingSession, setCheckingSession] = useState(() => !!storedToken())
  const [error, setError] = useState('')
  const role = bootstrap ? 'Bootstrap' : user?.role ?? null
  const { route, navigate } = useRoute(role)

  useEffect(() => {
    document.documentElement.dataset.theme = theme
    document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')?.setAttribute('content', theme === 'dark' ? '#131316' : '#ffffff')
    localStorage.setItem('hypanel-theme', theme)
  }, [theme])
  const endSession = () => { for (const key of ['hypanel-token', 'hypanel-user', 'hypanel-bootstrap', 'hypanel-session-token', 'hypanel-session-user', 'hypanel-admin-token']) sessionStorage.removeItem(key); setToken(''); setUser(null); setBootstrap(false) }
  const api = useMemo(() => new ApiClient(() => token, endSession), [token])

  useEffect(() => {
    if (!checkingSession || !token) return
    let active = true
    const validate = async () => {
      try {
        if (bootstrap) await api.validateAdminToken(token)
        else {
          const current = await api.request<User>('/api/user/v1/me')
          if (active) {
            storeAccountSession(token, current)
            setUser(current)
          }
        }
      } catch { if (active) endSession() }
      finally { if (active) setCheckingSession(false) }
    }
    void validate()
    return () => { active = false }
  }, [checkingSession, token, bootstrap])

  if (checkingSession) return <div className="splash-screen"><Splash label="正在验证会话…" /></div>

  if (!token || !role) return <Login theme={theme} setTheme={setTheme} api={api} onLogin={(nextToken, nextUser) => { storeAccountSession(nextToken, nextUser); setToken(nextToken); setUser(nextUser); setBootstrap(false) }} />

  const logout = async () => { try { if (!bootstrap) await api.request<void>('/api/auth/v1/logout', { method: 'POST' }) } catch { /* local logout remains available */ } finally { endSession() } }
  return <AppShell theme={theme} setTheme={setTheme} route={route} navigate={navigate} identity={bootstrap ? '初始管理员' : user?.username} role={role ?? 'User'} logout={() => void logout()}>
    {/* Success toasts go quickly; errors stay longer so they can be read. */}
    {error && (() => { const ok = /已|排队|成功/.test(error); return <Toast key={error} message={error} kind={ok ? 'success' : 'error'} duration={ok ? 3500 : 8000} dismiss={() => setError('')} /> })()}
    {role === 'User' ? <SubscriptionPage api={api} setError={setError} /> : <AdminApp route={route} api={api} setError={setError} account={!bootstrap} />}
  </AppShell>
}

function Login({ theme, setTheme, api, onLogin }: { theme: Theme; setTheme: (value: Theme) => void; api: ApiClient; onLogin: (token: string, user: User) => void }) {
  const [form, setForm] = useState({ username: '', password: '', token: '' })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  // A fresh install has no accounts: the first administrator is created with the install token instead.
  const [setup, setSetup] = useState(false)
  useEffect(() => { void api.request<{ needsSetup: boolean }>('/api/auth/v1/setup').then(result => setSetup(result.needsSetup), () => undefined) }, [])
  const submit = async (event: Event) => {
    event.preventDefault(); setBusy(true); setError('')
    try {
      const result = setup
        ? await api.request<{ token: string; user: User }>('/api/auth/v1/setup', { method: 'POST', body: JSON.stringify({ token: form.token.trim(), username: form.username, password: form.password }) })
        : await api.login(form.username, form.password)
      onLogin(result.token, result.user)
    } catch (reason) { setError(messageFor(reason, setup ? '无法创建管理员，请检查安装令牌' : '登录失败')) } finally { setBusy(false) }
  }
  const passkey = async () => { setBusy(true); setError(''); try { const result = await signInWithPasskey(api); if (result) onLogin(result.token, result.user) } catch (reason) { setError(messageFor(reason, '通行密钥登录失败')) } finally { setBusy(false) } }
  return <AppShell theme={theme} setTheme={setTheme}><div className="login-layout"><form className="card login-card" onSubmit={event => void submit(event)}>
    <div className="login-head"><BrandMark size={48} detail="full" /><h2>{setup ? '创建管理员' : '登录 HyPanel'}</h2><p className="muted">{setup ? '首次使用：用安装时生成的管理令牌创建第一个管理员账户。' : '使用账户密码或通行密钥继续。'}</p></div>
    {setup && <label>安装令牌<input required type="password" autoComplete="off" value={form.token} onInput={event => setForm({ ...form, token: event.currentTarget.value })} /></label>}
    <label>用户名<input required autoComplete="username webauthn" value={form.username} onInput={event => setForm({ ...form, username: event.currentTarget.value })} /></label>
    <label>密码<input required type="password" minLength={setup ? 6 : undefined} autoComplete={setup ? 'new-password' : 'current-password'} value={form.password} onInput={event => setForm({ ...form, password: event.currentTarget.value })} /></label>
    {error && <Notice kind="error">{error}</Notice>}
    <button className="button button-primary button-wide" disabled={busy}>{busy ? '正在验证…' : setup ? '创建并登录' : '登录'}</button>
    {!setup && passkeySupported() && <button className="button button-secondary button-wide" type="button" disabled={busy} onClick={() => void passkey()}>使用通行密钥登录</button>}
  </form></div></AppShell>
}
