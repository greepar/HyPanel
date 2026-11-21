import type { JSX } from 'preact'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'preact/hooks'
import { ArrowDownToLine, Copy, Eraser, Search } from 'lucide-preact'
import type { ApiClient } from './api'
import type { Service, ServiceLogLine } from './domain'
import { copyText, Notice } from './ui'

/** Lines kept in the browser; the Server keeps its own (longer) history. */
const MAX_LINES = 3000

type Connection = 'connecting' | 'live' | 'retrying'

/**
 * Live tail of one service's output, kept on the Server and streamed with a long-poll: new lines appear within a
 * couple of seconds of the backend writing them. Scrolling up pauses following; new lines are counted instead.
 */
export function LiveLogPanel({ api, nodeId, service, online }: { api: ApiClient; nodeId: string; service: Service; online: boolean }) {
  const [lines, setLines] = useState<(ServiceLogLine & { fresh?: boolean })[]>([])
  const [loaded, setLoaded] = useState(false)
  const [connection, setConnection] = useState<Connection>('connecting')
  const [follow, setFollow] = useState(true)
  const [unseen, setUnseen] = useState(0)
  const [filter, setFilter] = useState('')
  const [clearedAfter, setClearedAfter] = useState(0)
  const [copied, setCopied] = useState(false)
  const viewport = useRef<HTMLDivElement>(null)
  const followRef = useRef(follow)
  followRef.current = follow

  useEffect(() => {
    const controller = new AbortController()
    const { signal } = controller
    void (async () => {
      let cursor = 0
      let first = true
      let failures = 0
      while (!signal.aborted) {
        try {
          const page = await api.serviceLogs(nodeId, service.id, cursor, !first, signal)
          if (page.reset) {
            // The Server restarted and lost the lines we point into: start over from what it has now.
            cursor = 0; first = true
            setLines([]); setClearedAfter(0)
            continue
          }
          if (page.lines.length) {
            const arrived = page.lines.map(line => ({ ...line, fresh: !first }))
            setLines(current => {
              const next = first ? arrived : [...current.map(line => line.fresh ? { ...line, fresh: false } : line), ...arrived]
              return next.length > MAX_LINES ? next.slice(next.length - MAX_LINES) : next
            })
            if (!first && !followRef.current) setUnseen(count => count + arrived.length)
            cursor = page.lines[page.lines.length - 1].number
          }
          first = false
          failures = 0
          setLoaded(true)
          setConnection('live')
        } catch {
          if (signal.aborted) return
          failures++
          setConnection('retrying')
          await new Promise(resolve => setTimeout(resolve, Math.min(15_000, 1000 * 2 ** failures)))
        }
      }
    })()
    return () => controller.abort()
  }, [nodeId, service.id])

  const visible = useMemo(() => {
    const needle = filter.trim().toLowerCase()
    return lines.filter(line => line.number > clearedAfter && (!needle || plain(line.text).toLowerCase().includes(needle)))
  }, [lines, filter, clearedAfter])

  // Stick to the bottom while following; runs before paint so new lines never flash above the fold.
  useLayoutEffect(() => {
    const element = viewport.current
    if (element && follow) element.scrollTop = element.scrollHeight
  }, [visible, follow])

  const onScroll = () => {
    const element = viewport.current
    if (!element) return
    const atBottom = element.scrollHeight - element.scrollTop - element.clientHeight < 32
    if (atBottom !== followRef.current) setFollow(atBottom)
    if (atBottom) setUnseen(0)
  }
  const jumpToLatest = () => {
    setFollow(true); setUnseen(0)
    viewport.current?.scrollTo({ top: viewport.current.scrollHeight, behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' })
  }
  const copy = async () => {
    await copyText(visible.map(line => plain(stripStream(line.text))).join('\n'))
    setCopied(true)
    setTimeout(() => setCopied(false), 1500)
  }

  const status = !online
    ? { className: 'offline', label: '节点离线' }
    : connection === 'live'
      ? { className: 'live', label: '实时' }
      : connection === 'retrying'
        ? { className: 'retrying', label: '重新连接…' }
        : { className: 'connecting', label: '连接中…' }

  return <section className="log-console">
    <header className="log-toolbar">
      <div className="log-title">
        <h2>{service.name}</h2>
        <span className={`live-chip ${status.className}`} role="status"><i aria-hidden="true" />{status.label}</span>
        <span className="log-count">{filter ? `${visible.length} / ${lines.length} 行` : `${lines.length} 行`}</span>
      </div>
      <div className="log-actions">
        <label className="log-search">
          <Search size={14} aria-hidden="true" />
          <input type="search" placeholder="筛选日志" aria-label="筛选日志" value={filter} onInput={event => setFilter(event.currentTarget.value)} />
        </label>
        <button type="button" className="icon-button" aria-pressed={follow} title={follow ? '正在跟随最新日志' : '跟随最新日志'} aria-label="跟随最新日志" onClick={() => (follow ? setFollow(false) : jumpToLatest())}><ArrowDownToLine size={15} /></button>
        <button type="button" className="icon-button" title={copied ? '已复制' : '复制显示的日志'} aria-label="复制显示的日志" disabled={!visible.length} onClick={() => void copy()}><Copy size={15} /></button>
        <button type="button" className="icon-button" title="清屏（只影响此页面）" aria-label="清屏" disabled={!visible.length} onClick={() => setClearedAfter(lines[lines.length - 1]?.number ?? 0)}><Eraser size={15} /></button>
      </div>
    </header>
    {service.runtime?.errorMessage && <Notice kind="error">当前错误：{service.runtime.errorMessage}</Notice>}
    <div className="log-frame">
      <div ref={viewport} className="log-viewport" onScroll={onScroll} tabIndex={0} aria-label={`${service.name} 日志`} aria-live="off">
        {visible.length ? <ol className="log-lines">
          {visible.map(line => <li key={line.number} className={`log-line ${levelOf(line.text)}${line.fresh ? ' fresh' : ''}`} title={new Date(line.receivedAtUtc).toLocaleString('zh-CN')}>{renderAnsi(stripStream(line.text))}</li>)}
        </ol> : <div className="log-empty">
          {!loaded ? '正在读取日志…'
            : filter ? '没有匹配的日志。'
              : clearedAfter ? '已清屏，新的日志会继续出现在这里。'
                : online ? '还没有日志。服务一有输出就会出现在这里；若一直为空，请先把节点的 Agent 更新到最新版本。'
                  : '节点离线。恢复连接后，日志会接着推送过来。'}
        </div>}
      </div>
      {!follow && unseen > 0 && <button type="button" className="log-jump" onClick={jumpToLatest}><ArrowDownToLine size={14} />{unseen} 条新日志</button>}
    </div>
  </section>
}

// Escape sequences may arrive with the ESC byte stripped by an older collector, so it is optional here.
const ansiPattern = /\x1b?\[(\d{1,3}(?:;\d{1,3})*)?m/g
const plain = (text: string) => text.replace(ansiPattern, '')
const stripStream = (text: string) => text.replace(/^(stdout|stderr): /, '')

function levelOf(text: string) {
  const value = plain(text)
  if (/\b(ERROR|FATAL|PANIC)\b|\[Error\]|level=error/i.test(value)) return 'level-error'
  if (/\bWARN(ING)?\b|\[Warning\]|level=warn/i.test(value)) return 'level-warn'
  return ''
}

const ansiColors = ['black', 'red', 'green', 'yellow', 'blue', 'magenta', 'cyan', 'white']

/** Turns SGR colour codes into spans; anything else in the line stays plain text. */
function renderAnsi(text: string): JSX.Element[] | string {
  if (!ansiPattern.test(text)) return text
  ansiPattern.lastIndex = 0
  const parts: JSX.Element[] = []
  let color = '', bold = false, dim = false, last = 0, key = 0
  const push = (chunk: string) => {
    if (!chunk) return
    const classes = [color && `ansi-${color}`, bold && 'ansi-bold', dim && 'ansi-dim'].filter(Boolean).join(' ')
    parts.push(classes ? <span key={key++} className={classes}>{chunk}</span> : <span key={key++}>{chunk}</span>)
  }
  for (const match of text.matchAll(ansiPattern)) {
    push(text.slice(last, match.index))
    last = (match.index ?? 0) + match[0].length
    for (const code of (match[1] ?? '0').split(';').map(Number)) {
      if (code === 0) { color = ''; bold = false; dim = false }
      else if (code === 1) bold = true
      else if (code === 2) dim = true
      else if (code === 22) { bold = false; dim = false }
      else if (code === 39) color = ''
      else if (code >= 30 && code <= 37) color = ansiColors[code - 30]
      else if (code >= 90 && code <= 97) color = `bright-${ansiColors[code - 90]}`
    }
  }
  push(text.slice(last))
  return parts
}
