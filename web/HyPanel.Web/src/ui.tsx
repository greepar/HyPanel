import type { ComponentChildren } from 'preact'
import { useEffect, useRef } from 'preact/hooks'
import { Check, ChevronDown, LayoutDashboard, LayoutTemplate, LogOut, Monitor, Moon, Server, Sun, Users } from 'lucide-preact'
import type { AppRoute, Role, Theme } from './domain'

export const themes: Theme[] = ['light', 'dark', 'system']
const themeLabels: Record<Theme, string> = { light: '浅色', dark: '深色', system: '系统' }
const navigation: { route: AppRoute; label: string; admin: boolean; icon: typeof Server }[] = [
  { route: 'overview', label: '概览', admin: true, icon: LayoutDashboard },
  { route: 'nodes', label: '节点', admin: true, icon: Server },
  { route: 'templates', label: '模板', admin: true, icon: LayoutTemplate },
  { route: 'users', label: '用户', admin: true, icon: Users },
  { route: 'subscription', label: '我的订阅', admin: false, icon: LayoutDashboard },
]

export function AppShell({ children, theme, setTheme, route, navigate, identity, role, logout }: { children: ComponentChildren; theme: Theme; setTheme: (value: Theme) => void; route?: AppRoute; navigate?: (value: AppRoute) => void; identity?: string; role?: Role | 'Bootstrap'; logout?: () => void }) {
  const items = navigation.filter(item => role === 'User' ? !item.admin : item.admin)
  const activeRoute = route?.startsWith('nodes/') ? 'nodes' : route
  return <div className="app-shell">
    <header className="topbar">
      <a className="brand" href={role === 'User' ? '#/subscription' : '#/overview'}>HyPanel</a>
      <div className="topbar-actions">
        <span className="status"><i className="status-dot" />{identity ?? '安全控制平面'}</span>
        <details className="user-menu"><summary>账户 <ChevronDown size={14} /></summary><div className="user-menu-popover"><p>主题</p>{themes.map(value => { const Icon = value === 'light' ? Sun : value === 'dark' ? Moon : Monitor; return <button key={value} type="button" onClick={() => setTheme(value)}><Icon size={16} />{themeLabels[value]}{theme === value && <Check size={15} />}</button> })}{logout && <button type="button" onClick={logout}><LogOut size={16} />退出</button>}</div></details>
      </div>
    </header>
    {route && navigate && role ? <div className="workspace">
      <aside className="sidebar">
        <nav aria-label="主导航">{items.map(item => { const Icon = item.icon; return <a key={item.route} href={`#/${item.route}`} className={activeRoute === item.route ? 'nav-item active' : 'nav-item'} aria-current={activeRoute === item.route ? 'page' : undefined} onClick={event => { event.preventDefault(); navigate(item.route) }}><Icon size={18} />{item.label}</a> })}</nav>
        <div className="sidebar-footer"><span>当前身份</span><strong>{identity}</strong><small>{role === 'Bootstrap' ? '初始管理员令牌' : role === 'Admin' ? '管理员' : '普通用户'}</small></div>
      </aside>
      <main className="main">{children}</main>
    </div> : <main className="auth-main">{children}</main>}
  </div>
}

export function Page({ title, description, actions, children, eyebrow = '管理工作区' }: { title: string; description: string; actions?: ComponentChildren; children: ComponentChildren; eyebrow?: string }) {
  return <><header className="page-header"><div><p className="eyebrow">{eyebrow}</p><h1>{title}</h1><p>{description}</p></div>{actions && <div className="page-actions">{actions}</div>}</header><div className="page-content">{children}</div></>
}

export function Loading({ label = '正在加载…' }: { label?: string }) { return <div className="state" role="status"><span className="spinner" />{label}</div> }
export function Empty({ title, description, action }: { title: string; description: string; action?: ComponentChildren }) { return <div className="empty-state"><span className="empty-icon">＋</span><h2>{title}</h2><p>{description}</p>{action}</div> }
export function ErrorState({ message, retry }: { message: string; retry: () => void }) { return <div className="empty-state error-state" role="alert"><span className="empty-icon">!</span><h2>加载失败</h2><p>{message}</p><button className="button button-primary" type="button" onClick={retry}>重试</button></div> }
export function Notice({ kind = 'info', children, dismiss }: { kind?: 'info' | 'success' | 'error'; children: ComponentChildren; dismiss?: () => void }) { return <div className={`notice notice-${kind}`} role={kind === 'error' ? 'alert' : 'status'}><span>{children}</span>{dismiss && <button type="button" aria-label="关闭消息" onClick={dismiss}>×</button>}</div> }
export function Modal({ title, description, close, children }: { title: string; description?: string; close: () => void; children: ComponentChildren }) {
  const dialog = useRef<HTMLElement>(null)
  const closeRef = useRef(close)
  closeRef.current = close
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null
    const root = dialog.current
    const preferred = root?.querySelector<HTMLElement>('[autofocus]')
      ?? root?.querySelector<HTMLElement>('input:not([type="hidden"]), select')
      ?? root?.querySelector<HTMLElement>('button, a[href]')
    preferred?.focus()
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { event.preventDefault(); closeRef.current(); return }
      if (event.key !== 'Tab' || !root) return
      const focusable = [...root.querySelectorAll<HTMLElement>('input:not([type="hidden"]):not([disabled]), select:not([disabled]), button:not([disabled]), a[href]')]
      if (!focusable.length) return
      const first = focusable[0]; const last = focusable[focusable.length - 1]
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
    }
    addEventListener('keydown', onKey)
    return () => { removeEventListener('keydown', onKey); previous?.focus() }
  }, [])
  return <div className="modal-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) close() }}><section ref={dialog} className="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title"><header><div><h2 id="modal-title">{title}</h2>{description && <p>{description}</p>}</div><button className="icon-button" type="button" aria-label="关闭" onClick={close}>×</button></header>{children}</section></div>
}
export function Stat({ label, value, note }: { label: string; value: string; note: string }) { return <article className="card stat-card"><span>{label}</span><strong>{value}</strong><small>{note}</small></article> }

export const formatBytes = (value: number) => {
  if (!value) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1)
  return `${(value / 1024 ** index).toFixed(index ? 1 : 0)} ${units[index]}`
}

export const formatUptime = (seconds: number) => {
  if (!Number.isFinite(seconds) || seconds <= 0) return '—'
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  if (days > 0) return `${days} 天 ${hours} 小时`
  if (hours > 0) return `${hours} 小时 ${minutes} 分`
  return `${minutes} 分`
}

export const usedBytes = (total: number, available: number) => Math.max(0, total - available)
export const messageFor = (reason: unknown, fallback: string) => reason instanceof Error ? reason.message : fallback
