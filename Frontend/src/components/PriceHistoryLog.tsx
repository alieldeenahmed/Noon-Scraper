import type { PriceSnapshot } from '../api/types'
import { formatPriceExact, formatShortDate, formatTime } from '../lib/format'

export default function PriceHistoryLog({ history }: { history: PriceSnapshot[] }) {
  if (history.length === 0) {
    return <p className="text-sm text-ink-600">No crawl data yet.</p>
  }

  return (
    <div>
      <div className="divide-y divide-dotted divide-ink-300">
        {history.map((snapshot, i) => {
          const prev = i > 0 ? history[i - 1] : null
          const note =
            prev === null
              ? '← first reading, nothing to compare yet'
              : snapshot.price < prev.price
                ? `↓ down from ${formatPriceExact(prev.price)}`
                : snapshot.price > prev.price
                  ? `↑ up from ${formatPriceExact(prev.price)}`
                  : '→ steady'

          const sameDayAsPrev = prev && new Date(snapshot.crawledAt).toDateString() === new Date(prev.crawledAt).toDateString()

          return (
            <div key={i} className="flex flex-wrap items-baseline gap-x-3 gap-y-0.5 py-2 text-sm">
              <span className="w-24 shrink-0 text-ink-600">
                {sameDayAsPrev
                  ? formatTime(snapshot.crawledAt)
                  : `${formatShortDate(snapshot.crawledAt)}, ${formatTime(snapshot.crawledAt)}`}
              </span>
              <span className="w-28 shrink-0 tabular-nums">{formatPriceExact(snapshot.price)}</span>
              <span
                className={
                  note.startsWith('↓')
                    ? 'text-flag-green'
                    : note.startsWith('↑')
                      ? 'text-flag-red'
                      : 'text-ink-600'
                }
              >
                {note}
              </span>
            </div>
          )
        })}
      </div>
      {history.length === 1 && (
        <p className="mt-3 text-sm text-ink-600 italic">we'll add a line here the next time the price changes.</p>
      )}
    </div>
  )
}
