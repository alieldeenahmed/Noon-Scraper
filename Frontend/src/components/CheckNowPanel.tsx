import { useRef, useState } from 'react'
import { getCheckNowResult, startCheckNow } from '../api/client'
import type { CheckNowResult } from '../api/types'
import { formatPrice } from '../lib/format'

const POLL_INTERVAL_MS = 3000

export default function CheckNowPanel({ productId }: { productId: number }) {
  const [result, setResult] = useState<CheckNowResult | null>(null)
  const [running, setRunning] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  function stopPolling() {
    if (pollRef.current) {
      clearInterval(pollRef.current)
      pollRef.current = null
    }
  }

  async function handleCheckNow() {
    setRunning(true)
    setResult(null)
    setError(null)
    stopPolling()

    try {
      const { requestId } = await startCheckNow(productId)

      pollRef.current = setInterval(async () => {
        try {
          const current = await getCheckNowResult(productId, requestId)
          setResult(current)
          if (current.status !== 'Pending') {
            stopPolling()
            setRunning(false)
          }
        } catch {
          stopPolling()
          setRunning(false)
          setError('lost connection while checking on this request')
        }
      }, POLL_INTERVAL_MS)
    } catch {
      setRunning(false)
      setError('couldn’t start the check')
    }
  }

  return (
    <div className="border border-ink-900 p-4">
      <div className="flex items-center justify-between gap-3">
        <div>
          <p className="text-xs text-ink-600 lowercase">cross-merchant check</p>
          <p className="font-bold">compare vs. other sellers</p>
        </div>
        <button
          onClick={handleCheckNow}
          disabled={running}
          className="shrink-0 border border-ink-900 px-3 py-1.5 text-xs lowercase transition hover:bg-ink-900 hover:text-paper disabled:opacity-40"
        >
          {running ? 'checking…' : 'check'}
        </button>
      </div>

      {error && <p className="mt-3 text-sm text-flag-red">{error}</p>}

      {running && !result && !error && (
        <p className="mt-3 text-sm text-ink-600">kicking off a live scrape — this can take a couple minutes.</p>
      )}

      {result?.status === 'Pending' && <p className="mt-3 text-sm text-ink-600">still running…</p>}

      {result?.status === 'Failed' && <p className="mt-3 text-sm text-flag-red">check failed: {result.errorMessage}</p>}

      {result?.status === 'Completed' && (
        <div className="mt-3 divide-y divide-dotted divide-ink-300 border-t border-dotted border-ink-300">
          {result.offers?.length === 0 ? (
            <p className="py-2 text-sm text-ink-600">no other sellers found.</p>
          ) : (
            result.offers?.map((offer, i) => (
              <div key={i} className="flex items-baseline justify-between py-2 text-sm">
                <span>
                  {offer.merchantName}
                  {offer.rating != null && <span className="ml-1 text-ink-600">★ {offer.rating}</span>}
                </span>
                <span className="font-bold">{formatPrice(offer.price)}</span>
              </div>
            ))
          )}
        </div>
      )}
    </div>
  )
}
