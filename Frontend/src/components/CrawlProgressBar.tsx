import { useEffect, useState } from 'react'

// Real step-by-step progress isn't tracked anywhere (the crawl runs as one
// opaque GitHub Actions job), so this is an elapsed-time estimate against a
// typical run - caps short of 100% until the crawl actually finishes, rather
// than claiming a precision we don't have.
const ESTIMATED_MS = 75_000

export default function CrawlProgressBar({ startedAt }: { startedAt: string }) {
  const [now, setNow] = useState(Date.now())

  useEffect(() => {
    const tick = setInterval(() => setNow(Date.now()), 500)
    return () => clearInterval(tick)
  }, [])

  const elapsed = now - new Date(startedAt).getTime()
  const percent = Math.min(95, Math.round((elapsed / ESTIMATED_MS) * 95))
  const secondsLeft = Math.max(0, Math.ceil((ESTIMATED_MS - elapsed) / 1000))

  return (
    <div className="mt-3">
      <div className="h-1.5 w-full overflow-hidden bg-ink-300/30">
        <div
          className="h-full bg-flag-gold transition-[width] duration-500 ease-linear"
          style={{ width: `${percent}%` }}
        />
      </div>
      <p className="mt-1.5 text-xs text-ink-600 lowercase">
        {secondsLeft > 0 ? `~${secondsLeft}s left (estimate)` : 'almost there…'}
      </p>
    </div>
  )
}
