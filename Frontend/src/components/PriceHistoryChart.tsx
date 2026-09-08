import type { PriceSnapshot } from '../api/types'
import { formatDate, formatPrice } from '../lib/format'

const WIDTH = 600
const HEIGHT = 160
const PADDING = 8

export default function PriceHistoryChart({ history }: { history: PriceSnapshot[] }) {
  if (history.length < 2) {
    return <p className="text-sm text-slate-500">Not enough history yet for a chart.</p>
  }

  const prices = history.map((h) => h.price)
  const min = Math.min(...prices)
  const max = Math.max(...prices)
  const range = max - min || 1

  const points = history.map((snapshot, i) => {
    const x = PADDING + (i / (history.length - 1)) * (WIDTH - PADDING * 2)
    const y = HEIGHT - PADDING - ((snapshot.price - min) / range) * (HEIGHT - PADDING * 2)
    return { x, y, snapshot }
  })

  const path = points.map((p, i) => `${i === 0 ? 'M' : 'L'} ${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(' ')

  return (
    <div>
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} className="w-full" role="img" aria-label="Price history chart">
        <path d={path} fill="none" stroke="#0f172a" strokeWidth={2} />
        {points.map((p, i) => (
          <circle key={i} cx={p.x} cy={p.y} r={2.5} fill="#0f172a">
            <title>
              {formatPrice(p.snapshot.price)} — {formatDate(p.snapshot.crawledAt)}
            </title>
          </circle>
        ))}
      </svg>
      <div className="mt-1 flex justify-between text-xs text-slate-400">
        <span>{formatPrice(min)}</span>
        <span>{formatPrice(max)}</span>
      </div>
    </div>
  )
}
