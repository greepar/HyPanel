import { useEffect, useRef } from 'preact/hooks'
import type { ApiClient } from './api'

/**
 * One shared long-poll against the Server's change counter. Whenever an Agent reports a different status or anyone
 * saves something, every subscribed page reloads right away instead of waiting for its timer.
 */
const liveSubscribers = new Set<() => void>()
let liveClient: ApiClient | null = null
let liveLoop: AbortController | null = null

export function setLiveClient(api: ApiClient | null) {
  if (liveClient === api) return
  liveClient = api
  liveLoop?.abort()
  liveLoop = null
  ensureLiveLoop()
}

function ensureLiveLoop() {
  if (liveLoop || !liveClient || liveSubscribers.size === 0) return
  const controller = new AbortController()
  liveLoop = controller
  const api = liveClient
  void (async () => {
    let version: number | null = null
    let failures = 0
    while (!controller.signal.aborted && liveSubscribers.size > 0) {
      try {
        // While the tab is hidden, check rarely; visibilitychange triggers a refresh on return anyway.
        if (document.visibilityState !== 'visible') { await sleep(5000, controller.signal); continue }
        const result = await api.changes(version, controller.signal)
        if (version !== null && result.version !== version) for (const notify of [...liveSubscribers]) notify()
        version = result.version
        failures = 0
      } catch {
        if (controller.signal.aborted) return
        failures++
        await sleep(Math.min(30_000, 1000 * 2 ** failures), controller.signal)
      }
    }
    if (liveLoop === controller) liveLoop = null
  })()
}

const sleep = (ms: number, signal: AbortSignal) => new Promise<void>(resolve => {
  const timer = setTimeout(resolve, ms)
  signal.addEventListener('abort', () => { clearTimeout(timer); resolve() }, { once: true })
})

/**
 * Silently re-runs `refresh` when the Server reports a change, every few seconds while the tab is visible (metrics),
 * and once when the tab becomes visible again, so runtime status shows up without reloading the page.
 */
export function useAutoRefresh(refresh: () => unknown, deps: unknown[] = [], intervalMs = 5000) {
  const latest = useRef(refresh)
  latest.current = refresh
  useEffect(() => {
    let pending: ReturnType<typeof setTimeout> | null = null
    const tick = () => {
      if (document.visibilityState === 'visible') void latest.current()
    }
    // A burst of changes (several services restarting) becomes one reload.
    const onChange = () => {
      if (pending) return
      pending = setTimeout(() => { pending = null; tick() }, 150)
    }
    const timer = setInterval(tick, intervalMs)
    document.addEventListener('visibilitychange', tick)
    liveSubscribers.add(onChange)
    ensureLiveLoop()
    return () => {
      clearInterval(timer)
      if (pending) clearTimeout(pending)
      document.removeEventListener('visibilitychange', tick)
      liveSubscribers.delete(onChange)
    }
  }, deps)
}

/**
 * Module-level snapshot cache for page data. Revisiting a page renders the previous data instantly
 * while a silent refresh runs in the background, instead of flashing a loading spinner.
 */
const cacheStore = new Map<string, unknown>()
export const readCache = <T>(key: string): T | undefined => cacheStore.get(key) as T | undefined
export const writeCache = <T>(key: string, value: T) => { cacheStore.set(key, value) }
export const dropCache = (prefix: string) => { for (const key of [...cacheStore.keys()]) if (key.startsWith(prefix)) cacheStore.delete(key) }
