import { useEffect, useRef } from 'preact/hooks'

/**
 * Silently re-runs `refresh` every few seconds while the tab is visible (and once when it becomes visible again),
 * so runtime status such as 重启退避 → 运行中 shows up without reloading the page.
 */
export function useAutoRefresh(refresh: () => unknown, deps: unknown[] = [], intervalMs = 5000) {
  const latest = useRef(refresh)
  latest.current = refresh
  useEffect(() => {
    const tick = () => {
      if (document.visibilityState === 'visible') void latest.current()
    }
    const timer = setInterval(tick, intervalMs)
    document.addEventListener('visibilitychange', tick)
    return () => {
      clearInterval(timer)
      document.removeEventListener('visibilitychange', tick)
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
