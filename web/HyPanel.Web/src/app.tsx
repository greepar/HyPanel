import { useEffect, useMemo, useRef, useState } from 'preact/hooks'
import { ApiClient } from './api'
import type { Theme, User } from './domain'
import { useRoute } from './router'
import { AdminApp } from './admin'
import { SubscriptionPage } from './subscription'
import { AppShell, messageFor, Notice, PasswordInput, Splash, Toast } from './ui'
import { BrandMark } from './brand'
import { passkeySupported, signInWithPasskey } from './passkey'

const storedTheme = (): Theme => {
  const value = localStorage.getItem('hypanel-theme') as Theme
  return ['light', 'dark', 'system'].includes(value) ? value : 'system'
}

// "Stay signed in" keeps the session in localStorage (survives closing the browser, 30-day server session);
// otherwise it lives in sessionStorage and ends with the tab. Passwords are never stored by the page: the
// browser's own password manager offers to save and fill them.
const safeGet = (storage: Storage, key: string) => { try { return storage.getItem(key) } catch { return null } }
const storedToken = () => safeGet(localStorage, 'hypanel-token')
  ?? sessionStorage.getItem('hypanel-token')
  ?? sessionStorage.getItem('hypanel-session-token')
  ?? sessionStorage.getItem('hypanel-admin-token')
  ?? ''

const storedUser = (): User | null => {
  try { return JSON.parse(safeGet(localStorage, 'hypanel-user') ?? sessionStorage.getItem('hypanel-user') ?? sessionStorage.getItem('hypanel-session-user') ?? 'null') as User | null }
  catch { return null }
}

const sessionKeys = ['hypanel-token', 'hypanel-user', 'hypanel-bootstrap', 'hypanel-session-token', 'hypanel-session-user', 'hypanel-admin-token']
const clearSessions = () => {
  for (const key of sessionKeys) sessionStorage.removeItem(key)
  try { localStorage.removeItem('hypanel-token'); localStorage.removeItem('hypanel-user') } catch { /* storage blocked */ }
}

const storeAccountSession = (token: string, user: User, remember: boolean) => {
  clearSessions()
  let storage: Storage = sessionStorage
  if (remember) { try { localStorage.setItem('hypanel-probe', '1'); localStorage.removeItem('hypanel-probe'); storage = localStorage } catch { /* falls back to the tab */ } }
  storage.setItem('hypanel-token', token)
  storage.setItem('hypanel-user', JSON.stringify(user))
}
const rememberedChoice = () => safeGet(localStorage, 'hypanel-remember') !== '0'

// "记住密码" keeps the sign-in fields in this browser so the form is filled (and, with 自动登录, submitted) next time.
type SavedLogin = { username: string; password: string }
const savedLogin = (): SavedLogin | null => {
  try {
    const value = JSON.parse(safeGet(localStorage, 'hypanel-login') ?? 'null') as SavedLogin | null
    return value && typeof value.username === 'string' && typeof value.password === 'string' ? value : null
  } catch { return null }
}
const saveLogin = (value: SavedLogin | null) => {
  try { if (value) localStorage.setItem('hypanel-login', JSON.stringify(value)); else localStorage.removeItem('hypanel-login') } catch { /* not remembered */ }
}
// Set by 退出登录 for this tab, so signing out is not undone by an automatic sign-in a moment later.
const signedOutKey = 'hypanel-signed-out'
const remembered = () => safeGet(localStorage, 'hypanel-token') !== null

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
    document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')?.setAttribute('content', theme === 'dark' ? '#292a2d' : '#ffffff')
    localStorage.setItem('hypanel-theme', theme)
  }, [theme])
  const endSession = () => { clearSessions(); setToken(''); setUser(null); setBootstrap(false) }
  const signOut = () => { try { sessionStorage.setItem(signedOutKey, '1') } catch { /* storage blocked */ } endSession() }
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
            storeAccountSession(token, current, remembered())
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

  if (!token || !role) return <Login theme={theme} setTheme={setTheme} api={api} onLogin={(nextToken, nextUser, remember) => { storeAccountSession(nextToken, nextUser, remember); setToken(nextToken); setUser(nextUser); setBootstrap(false) }} />

  const logout = async () => { try { if (!bootstrap) await api.request<void>('/api/auth/v1/logout', { method: 'POST' }) } catch { /* local logout remains available */ } finally { signOut() } }
  return <AppShell theme={theme} setTheme={setTheme} route={route} navigate={navigate} identity={bootstrap ? '初始管理员' : user?.username} role={role ?? 'User'} logout={() => void logout()}>
    {/* Success toasts go quickly; errors stay longer so they can be read. */}
    {error && (() => { const ok = /已|排队|成功/.test(error); return <Toast key={error} message={error} kind={ok ? 'success' : 'error'} duration={ok ? 3500 : 8000} dismiss={() => setError('')} /> })()}
    {role === 'User' ? <SubscriptionPage api={api} setError={setError} /> : <AdminApp route={route} api={api} setError={setError} account={!bootstrap} />}
  </AppShell>
}

function Login({ theme, setTheme, api, onLogin }: { theme: Theme; setTheme: (value: Theme) => void; api: ApiClient; onLogin: (token: string, user: User, remember: boolean) => void }) {
  const [form, setForm] = useState(() => ({ username: savedLogin()?.username ?? '', password: savedLogin()?.password ?? '', token: '' }))
  // Unchecking 记住密码 also turns 自动登录 off, so a fresh browser starts with both following 自动登录.
  const [keepPassword, setKeepPassword] = useState(() => savedLogin() ? !!savedLogin()?.password : rememberedChoice())
  // 自动登录: a 30-day session that survives closing the browser, and a fresh sign-in with the saved password when it ends.
  const [remember, setRemember] = useState(rememberedChoice)
  const chooseRemember = (value: boolean) => {
    setRemember(value)
    if (value) setKeepPassword(true)
    try { localStorage.setItem('hypanel-remember', value ? '1' : '0') } catch { /* not remembered */ }
  }
  const chooseKeepPassword = (value: boolean) => {
    setKeepPassword(value)
    if (!value) { saveLogin(null); chooseRemember(false) }
  }
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  // A fresh install has no accounts: the first administrator is created with the install token instead.
  const [setup, setSetup] = useState(false)
  useEffect(() => { void api.request<{ needsSetup: boolean }>('/api/auth/v1/setup').then(result => setSetup(result.needsSetup), () => undefined) }, [])
  const signIn = async (credentials: SavedLogin) => {
    setBusy(true); setError('')
    try {
      const result = setup
        ? await api.request<{ token: string; user: User }>('/api/auth/v1/setup', { method: 'POST', body: JSON.stringify({ token: form.token.trim(), username: credentials.username, password: credentials.password }) })
        : await api.login(credentials.username, credentials.password, remember)
      if (!setup) saveLogin(keepPassword ? credentials : null)
      try { sessionStorage.removeItem(signedOutKey) } catch { /* storage blocked */ }
      onLogin(result.token, result.user, remember && !setup)
    } catch (reason) {
      setError(messageFor(reason, setup ? '无法创建管理员，请检查安装令牌' : '登录失败'))
      // A saved password that stopped working is forgotten, so the next visit does not try it again.
      if (!setup && savedLogin()?.password === credentials.password) saveLogin({ username: credentials.username, password: '' })
    } finally { setBusy(false) }
  }
  const submit = (event: Event) => { event.preventDefault(); void signIn({ username: form.username, password: form.password }) }
  // Once per page load: the session ended (expired or revoked) while 自动登录 is on and a password is saved.
  const autoTried = useRef(false)
  useEffect(() => {
    if (autoTried.current || setup || !remember) return
    autoTried.current = true
    const saved = savedLogin()
    let signedOut = false
    try { signedOut = sessionStorage.getItem(signedOutKey) === '1' } catch { /* storage blocked */ }
    if (saved?.username && saved.password && !signedOut) void signIn(saved)
  }, [setup])
  const passkey = async () => { setBusy(true); setError(''); try { const result = await signInWithPasskey(api, remember); if (result) onLogin(result.token, result.user, remember) } catch (reason) { setError(messageFor(reason, '通行密钥登录失败')) } finally { setBusy(false) } }
  return <AppShell theme={theme} setTheme={setTheme}><div className="login-layout"><form className="card login-card" onSubmit={event => void submit(event)}>
    <div className="login-head"><BrandMark size={48} detail="full" /><h2>{setup ? '创建管理员' : '登录 HyPanel'}</h2><p className="muted">{setup ? '首次使用：用安装时生成的管理令牌创建第一个管理员账户。' : '使用账户密码或通行密钥继续。'}</p></div>
    {setup && <label>安装令牌<PasswordInput required value={form.token} onValue={token => setForm({ ...form, token })} /></label>}
    <label>用户名<input required name="username" autoComplete="username webauthn" value={form.username} onInput={event => setForm({ ...form, username: event.currentTarget.value })} /></label>
    <label>密码<PasswordInput required name="password" minLength={setup ? 6 : undefined} autoComplete={setup ? 'new-password' : 'current-password'} value={form.password} onValue={password => setForm({ ...form, password })} /></label>
    {!setup && <div className="remember-row">
      <label className="checkbox"><input type="checkbox" checked={keepPassword} onChange={event => chooseKeepPassword(event.currentTarget.checked)} />记住密码</label>
      <label className="checkbox" title="30 天内打开面板无需再次登录；会话过期后用记住的密码自动登录"><input type="checkbox" checked={remember} onChange={event => chooseRemember(event.currentTarget.checked)} />自动登录</label>
    </div>}
    {error && <Notice kind="error">{error}</Notice>}
    <button className="button button-primary button-wide" disabled={busy}>{busy ? '正在验证…' : setup ? '创建并登录' : '登录'}</button>
    {!setup && passkeySupported() && <button className="button button-secondary button-wide" type="button" disabled={busy} onClick={() => void passkey()}>使用通行密钥登录</button>}
  </form></div></AppShell>
}
