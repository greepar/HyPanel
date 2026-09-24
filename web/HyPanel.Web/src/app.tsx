import { useEffect, useMemo, useState } from 'preact/hooks'
import { ApiClient } from './api'
import type { Theme, User } from './domain'
import { useRoute } from './router'
import { AdminApp } from './admin'
import { SubscriptionPage } from './subscription'
import { AppShell, messageFor, Notice, Splash } from './ui'
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

const storeBootstrapSession = (token: string) => {
  sessionStorage.setItem('hypanel-token', token)
  sessionStorage.setItem('hypanel-bootstrap', 'true')
  for (const key of ['hypanel-user', 'hypanel-session-token', 'hypanel-session-user', 'hypanel-admin-token']) sessionStorage.removeItem(key)
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
  // Success toasts fade on their own; errors stay a little longer so they can be read.
  useEffect(() => {
    if (!error) return
    const timer = setTimeout(() => setError(''), /已|排队|成功/.test(error) ? 3500 : 8000)
    return () => clearTimeout(timer)
  }, [error])

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

  if (checkingSession) return <AppShell theme={theme} setTheme={setTheme}><Splash label="正在验证会话…" /></AppShell>

  if (!token || !role) return <Login theme={theme} setTheme={setTheme} api={api} onLogin={(nextToken, nextUser) => { storeAccountSession(nextToken, nextUser); setToken(nextToken); setUser(nextUser); setBootstrap(false) }} onBootstrap={nextToken => { storeBootstrapSession(nextToken); setToken(nextToken); setUser(null); setBootstrap(true) }} />

  const logout = async () => { try { if (!bootstrap) await api.request<void>('/api/auth/v1/logout', { method: 'POST' }) } catch { /* local logout remains available */ } finally { endSession() } }
  return <AppShell theme={theme} setTheme={setTheme} route={route} navigate={navigate} identity={bootstrap ? '初始管理员' : user?.username} role={role ?? 'User'} logout={() => void logout()}>
    {error && <div className="global-notice"><Notice kind={/已|排队|成功/.test(error) ? 'success' : 'error'} dismiss={() => setError('')}>{error}</Notice></div>}
    {role === 'User' ? <SubscriptionPage api={api} setError={setError} /> : <AdminApp route={route} api={api} setError={setError} account={!bootstrap} />}
  </AppShell>
}

function Login({ theme, setTheme, api, onLogin, onBootstrap }: { theme: Theme; setTheme: (value: Theme) => void; api: ApiClient; onLogin: (token: string, user: User) => void; onBootstrap: (token: string) => void }) {
  const [mode, setMode] = useState<'account' | 'bootstrap'>('account')
  const [form, setForm] = useState({ username: '', password: '', token: '' })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const submit = async (event: Event) => { event.preventDefault(); setBusy(true); setError(''); try { if (mode === 'bootstrap') { if (!form.token.trim()) throw new Error('请输入初始管理员令牌。'); await api.validateAdminToken(form.token.trim()); onBootstrap(form.token.trim()) } else { const result = await api.login(form.username, form.password); onLogin(result.token, result.user) } } catch (reason) { setError(messageFor(reason, '登录失败')) } finally { setBusy(false) } }
  const passkey = async () => { setBusy(true); setError(''); try { const result = await signInWithPasskey(api); if (result) onLogin(result.token, result.user) } catch (reason) { setError(messageFor(reason, '通行密钥登录失败')) } finally { setBusy(false) } }
  return <AppShell theme={theme} setTheme={setTheme}><div className="login-layout"><form className="card login-card" onSubmit={event => void submit(event)}><div className="login-head"><BrandMark size={48} detail="full" /><h2>{mode === 'account' ? '登录 HyPanel' : '初始管理模式'}</h2><p className="muted">{mode === 'account' ? '使用账户密码或通行密钥继续。' : '令牌仅保存在当前浏览器会话。'}</p></div><div className="segmented"><button type="button" className={mode === 'account' ? 'active' : ''} onClick={() => setMode('account')}>账户登录</button><button type="button" className={mode === 'bootstrap' ? 'active' : ''} onClick={() => setMode('bootstrap')}>初始令牌</button></div>{mode === 'account' ? <><label>用户名<input required autoComplete="username" value={form.username} onInput={event => setForm({ ...form, username: event.currentTarget.value })} /></label><label>密码<input required type="password" autoComplete="current-password" value={form.password} onInput={event => setForm({ ...form, password: event.currentTarget.value })} /></label></> : <label>Bearer 令牌<input required type="password" autoComplete="off" value={form.token} onInput={event => setForm({ ...form, token: event.currentTarget.value })} /></label>}{error && <Notice kind="error">{error}</Notice>}<button className="button button-primary button-wide" disabled={busy}>{busy ? '正在验证…' : '继续'}</button>{mode === 'account' && passkeySupported() && <button className="button button-secondary button-wide" type="button" disabled={busy} onClick={() => void passkey()}>使用通行密钥登录</button>}</form></div></AppShell>
}
