import type { ComponentChildren } from 'preact'
import { useEffect, useRef, useState } from 'preact/hooks'
import { Check, ChevronDown, ChevronsUpDown, LayoutDashboard, LogOut, Monitor, Moon, Server, Settings, Sun, Users } from 'lucide-preact'
import type { AppRoute, Role, Theme } from './domain'
import { BrandLockup, BrandMark } from './brand'

export const themes: Theme[] = ['light', 'dark', 'system']
const themeLabels: Record<Theme, string> = { light: '浅色', dark: '深色', system: '系统' }
const navigation: { route: AppRoute; label: string; admin: boolean; icon: typeof Server }[] = [
  { route: 'overview', label: '概览', admin: true, icon: LayoutDashboard },
  { route: 'nodes', label: '节点', admin: true, icon: Server },
  { route: 'users', label: '用户', admin: true, icon: Users },
  { route: 'settings', label: '设置', admin: true, icon: Settings },
  { route: 'subscription', label: '我的订阅', admin: false, icon: LayoutDashboard },
]

export function AppShell({ children, theme, setTheme, route, navigate, identity, role, logout }: { children: ComponentChildren; theme: Theme; setTheme: (value: Theme) => void; route?: AppRoute; navigate?: (value: AppRoute) => void; identity?: string; role?: Role | 'Bootstrap'; logout?: () => void }) {
  const items = navigation.filter(item => role === 'User' ? !item.admin : item.admin)
  const activeRoute = route?.startsWith('nodes/') ? 'nodes' : route
  // Dropdown menus are <details>; close them after choosing an item, clicking elsewhere or pressing Escape.
  useEffect(() => {
    const closeMenus = (keep?: Element | null) => document.querySelectorAll<HTMLDetailsElement>('details.more-menu[open], details.user-menu[open]')
      .forEach(menu => { if (menu !== keep) menu.open = false })
    const onClick = (event: MouseEvent) => {
      const target = event.target as Element | null
      const menu = target?.closest('details.more-menu, details.user-menu') ?? null
      closeMenus(menu && !target?.closest('button') ? menu : null)
    }
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') closeMenus() }
    document.addEventListener('click', onClick, true)
    document.addEventListener('keydown', onKey)
    return () => { document.removeEventListener('click', onClick, true); document.removeEventListener('keydown', onKey) }
  }, [])
  return <div className="app-shell">
    <header className="topbar">
      <a className="brand" href={role === 'User' ? '#/subscription' : '#/overview'} aria-label="HyPanel 首页"><BrandLockup size={28} /></a>
      <div className="topbar-actions">
        <details className="user-menu"><summary><span className="avatar-sm" aria-hidden="true">{(identity ?? 'H').slice(0, 1).toUpperCase()}</span><span className="user-menu-name">{identity ?? '账户'}</span><ChevronDown size={14} /></summary><div className="user-menu-popover"><p>主题</p>{themes.map(value => { const Icon = value === 'light' ? Sun : value === 'dark' ? Moon : Monitor; return <button key={value} type="button" onClick={() => setTheme(value)}><Icon size={16} />{themeLabels[value]}{theme === value && <Check size={15} />}</button> })}{logout && <button type="button" onClick={logout}><LogOut size={16} />退出</button>}</div></details>
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

export function Page({ title, description, actions, children, eyebrow }: { title: ComponentChildren; description: ComponentChildren; actions?: ComponentChildren; children: ComponentChildren; eyebrow?: ComponentChildren }) {
  return <><header className="page-header"><div>{eyebrow && <p className="eyebrow">{eyebrow}</p>}<h1>{title}</h1><p>{description}</p></div>{actions && <div className="page-actions">{actions}</div>}</header><div className="page-content">{children}</div></>
}

export function Loading({ label = '正在加载…' }: { label?: string }) { return <div className="state" role="status"><span className="spinner" />{label}</div> }
/** Full-screen splash while the session is being validated. */
export function Splash({ label }: { label: string }) { return <div className="splash" role="status"><BrandMark size={56} detail="full" /><span><i className="spinner" />{label}</span></div> }
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
      // An open dropdown consumes Escape first (it calls preventDefault); only then close the dialog.
      if (event.key === 'Escape') { if (event.defaultPrevented) return; event.preventDefault(); closeRef.current(); return }
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
export function Stat({ label, value, note, percent, tone }: { label: string; value: string; note: string; percent?: number | null; tone?: 'good' | 'warn' | 'bad' }) {
  return <article className={`card stat-card${tone ? ` tone-${tone}` : ''}`}><span>{label}</span><strong>{value}</strong>{percent != null && <Meter value={percent} />}<small>{note}</small></article>
}
/** Horizontal usage bar; turns amber above 75 % and red above 90 %. */
export function Meter({ value, label }: { value: number; label?: string }) {
  const clamped = Math.max(0, Math.min(100, Number.isFinite(value) ? value : 0))
  const level = clamped >= 90 ? 'bad' : clamped >= 75 ? 'warn' : 'good'
  return <span className={`meter meter-${level}`} role="meter" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(clamped)} aria-label={label}><span style={{ width: `${clamped}%` }} /></span>
}
export const percentOf = (used: number, total: number) => total > 0 ? (used / total) * 100 : 0
export const formatRelative = (value: string | null) => {
  if (!value) return '从未'
  const seconds = Math.round((Date.now() - new Date(value).getTime()) / 1000)
  if (seconds < 45) return '刚刚'
  if (seconds < 3600) return `${Math.max(1, Math.round(seconds / 60))} 分钟前`
  if (seconds < 86400) return `${Math.round(seconds / 3600)} 小时前`
  return `${Math.round(seconds / 86400)} 天前`
}
export async function copyText(value: string) {
  try { await navigator.clipboard.writeText(value); return }
  catch { /* clipboard API unavailable (HTTP or denied); fall back below */ }
  const area = document.createElement('textarea')
  area.value = value; area.setAttribute('readonly', ''); area.style.position = 'fixed'; area.style.opacity = '0'
  document.body.append(area); area.select()
  try { if (!document.execCommand('copy')) throw new Error('无法复制，请手动选择文本。') } finally { area.remove() }
}

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

export type SelectOption = { value: string; label: string; hint?: string }

/**
 * Styled replacement for the native <select>, whose popup (blue system highlight on macOS) cannot be themed.
 * Keyboard: Enter/Space/ArrowDown opens, arrows move, Enter selects, Escape/Tab close.
 */
export function Select({ value, options, onChange, placeholder = '请选择', required = false, disabled = false, ariaLabel }: {
  value: string; options: SelectOption[]; onChange: (value: string) => void; placeholder?: string
  required?: boolean; disabled?: boolean; ariaLabel?: string
}) {
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const root = useRef<HTMLDivElement>(null)
  const selected = options.find(option => option.value === value)
  useEffect(() => {
    if (!open) return
    const close = (event: MouseEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [open])
  const show = () => { if (disabled) return; setActive(Math.max(0, options.findIndex(option => option.value === value))); setOpen(true) }
  const choose = (option: SelectOption) => { onChange(option.value); setOpen(false) }
  const onKey = (event: KeyboardEvent) => {
    if (!open) {
      if (['Enter', ' ', 'ArrowDown', 'ArrowUp'].includes(event.key)) { event.preventDefault(); show() }
      return
    }
    if (event.key === 'Escape' || event.key === 'Tab') { if (event.key === 'Escape') event.preventDefault(); setOpen(false) }
    else if (event.key === 'ArrowDown') { event.preventDefault(); setActive(index => Math.min(options.length - 1, index + 1)) }
    else if (event.key === 'ArrowUp') { event.preventDefault(); setActive(index => Math.max(0, index - 1)) }
    else if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); if (options[active]) choose(options[active]) }
  }
  return <div className={`select${open ? ' open' : ''}`} ref={root}>
    <button type="button" className="select-trigger" aria-haspopup="listbox" aria-expanded={open} aria-label={ariaLabel}
      disabled={disabled} onClick={event => { event.preventDefault(); open ? setOpen(false) : show() }} onKeyDown={onKey}>
      <span className={selected ? '' : 'select-placeholder'}>{selected?.label ?? placeholder}</span>
      <ChevronsUpDown size={14} aria-hidden="true" />
    </button>
    {required && <input className="select-validation" tabIndex={-1} aria-hidden="true" required value={value} onInput={() => undefined}
      onInvalid={event => { event.preventDefault(); show() }} />}
    {open && <ul className="select-menu" role="listbox">
      {options.map((option, index) => <li key={option.value} role="option" aria-selected={option.value === value}
        className={`${index === active ? 'active' : ''}${option.value === value ? ' selected' : ''}`}
        onMouseEnter={() => setActive(index)} onMouseDown={event => { event.preventDefault(); choose(option) }}>
        <span>{option.label}{option.hint && <small>{option.hint}</small>}</span>
        {option.value === value && <Check size={14} aria-hidden="true" />}
      </li>)}
    </ul>}
  </div>
}
