import { useEffect, useState } from 'preact/hooks'
import type { AppRoute, Role } from './domain'

const adminRoutes: AppRoute[] = ['overview', 'nodes', 'users', 'settings']
const nodeRoute = /^nodes\/[^/]+\/(overview|services|network|logs|settings)$/
const routeFromHash = (): AppRoute => {
  const value = location.hash.replace(/^#\/?/, '') as AppRoute
  return [...adminRoutes, 'subscription'].includes(value) || nodeRoute.test(value) ? value : 'overview'
}

export function useRoute(role: Role | 'Bootstrap' | null) {
  const [route, setRouteState] = useState<AppRoute>(routeFromHash)
  useEffect(() => {
    const changed = () => setRouteState(routeFromHash())
    addEventListener('hashchange', changed)
    return () => removeEventListener('hashchange', changed)
  }, [])
  const allowed: AppRoute = role === 'User' ? 'subscription' : route === 'subscription' ? 'overview' : route
  useEffect(() => {
    if (role && allowed !== route) location.replace(`#/${allowed}`)
  }, [role, route, allowed])
  return { route: allowed, navigate: (next: AppRoute) => { location.hash = `/${next}` } }
}
