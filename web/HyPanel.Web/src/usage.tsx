import { useState } from 'preact/hooks'
import type { PreviousUsage } from './domain'
import { countryFlag, formatBytes } from './ui'

/** One service's usage, already labelled with its node. */
export type UsageEntry = { serviceId: string; nodeKey: string; nodeName: string; serviceName: string; upload: number; download: number }

type UsageNode = { key: string; name: string; upload: number; download: number; lines: UsageEntry[] }

const size = (item: { upload: number; download: number }) => item.upload + item.download

/** Groups per-service usage by node, largest first. */
function byNode(entries: UsageEntry[]): UsageNode[] {
  const nodes = new Map<string, UsageNode>()
  for (const entry of entries) {
    if (size(entry) <= 0) continue
    const node = nodes.get(entry.nodeKey) ?? { key: entry.nodeKey, name: entry.nodeName, upload: 0, download: 0, lines: [] }
    node.upload += entry.upload
    node.download += entry.download
    node.lines.push(entry)
    nodes.set(entry.nodeKey, node)
  }
  return [...nodes.values()]
    .map(node => ({ ...node, lines: node.lines.sort((a, b) => size(b) - size(a)) }))
    .sort((a, b) => size(b) - size(a))
}

/** A usage row the Panel labelled itself (the signed-in user's view, or a closed period with names as they were). */
export const namedEntry = (entry: PreviousUsage['entries'][number]): UsageEntry => ({
  serviceId: entry.serviceId,
  nodeKey: entry.nodeName || 'deleted',
  nodeName: entry.nodeName ? `${countryFlag(entry.countryCode)} ${entry.nodeName}`.trim() : '已删除的节点',
  serviceName: entry.serviceName || '已删除',
  upload: entry.uploadBytes,
  download: entry.downloadBytes,
})

const dateOf = (value: string) => new Date(value).toLocaleDateString('zh-CN')

/** Where traffic went, per node and service, for this period and the one the last reset closed. */
export function UsageBreakdownView({ current, previous }: { current: UsageEntry[]; previous: PreviousUsage | null }) {
  const [period, setPeriod] = useState<'current' | 'previous'>('current')
  const now = byNode(current)
  const last = byNode((previous?.entries ?? []).map(namedEntry))
  const total = (list: UsageNode[]) => list.reduce((sum, node) => sum + size(node), 0)
  const nodes = period === 'current' ? now : last
  const shown = total(nodes)
  return <div className="usage-breakdown">
    <div className="segmented" role="tablist" aria-label="统计周期">
      <button type="button" role="tab" aria-selected={period === 'current'} className={period === 'current' ? 'active' : ''}
        onClick={() => setPeriod('current')}>本期 · {formatBytes(total(now))}</button>
      <button type="button" role="tab" aria-selected={period === 'previous'} className={period === 'previous' ? 'active' : ''}
        disabled={!previous} onClick={() => setPeriod('previous')}>上期 · {previous ? formatBytes(total(last)) : '无记录'}</button>
    </div>
    {period === 'previous' && previous && <p className="field-help">{dateOf(previous.startUtc)} 至 {dateOf(previous.endUtc)}，在流量重置时记录。</p>}
    {nodes.length === 0
      ? <p className="muted">{period === 'current' ? '本期还没有用量。' : '上期没有用量。'}</p>
      : <ol className="usage-nodes">
        {nodes.map(node => <li key={node.key}>
          <div className="usage-row usage-node"><strong>{node.name}</strong><span className="mono">{formatBytes(size(node))}</span></div>
          <div className="usage-bar" aria-hidden="true"><i style={{ width: `${shown ? size(node) / shown * 100 : 0}%` }} /></div>
          {node.lines.map(line => <div className="usage-row usage-service" key={line.serviceId}>
            <span>{line.serviceName}</span>
            <small className="mono">↑ {formatBytes(line.upload)} · ↓ {formatBytes(line.download)}</small>
            <span className="mono">{formatBytes(size(line))}</span>
          </div>)}
        </li>)}
      </ol>}
  </div>
}
