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
          setError('Lost connection while checking on this request.')
        }
      }, POLL_INTERVAL_MS)
    } catch {
      setRunning(false)
      setError('Couldn’t start the check — the API might be unreachable or misconfigured.')
    }
  }

  return (
    <div className="rounded-lg border border-slate-200 bg-white p-4">
      <div className="flex items-center justify-between">
        <h2 className="font-medium text-slate-900">Cross-merchant check</h2>
        <button
          onClick={handleCheckNow}
          disabled={running}
          className="rounded-md bg-slate-900 px-3 py-1.5 text-sm font-medium text-white transition hover:bg-slate-700 disabled:opacity-50"
        >
          {running ? 'Checking…' : 'Check now'}
        </button>
      </div>

      {error && <p className="mt-3 text-sm text-red-600">{error}</p>}

      {running && !result && !error && (
        <p className="mt-3 text-sm text-slate-500">
          Kicking off a live scrape via GitHub Actions — this can take a couple minutes.
        </p>
      )}

      {result?.status === 'Pending' && (
        <p className="mt-3 text-sm text-slate-500">Still running…</p>
      )}

      {result?.status === 'Failed' && (
        <p className="mt-3 text-sm text-red-600">Check failed: {result.errorMessage}</p>
      )}

      {result?.status === 'Completed' && (
        <div className="mt-3 space-y-2">
          {result.offers?.length === 0 ? (
            <p className="text-sm text-slate-500">No other sellers found for this product.</p>
          ) : (
            <table className="w-full text-sm">
              <tbody>
                {result.offers?.map((offer, i) => (
                  <tr key={i} className="border-t border-slate-100 first:border-0">
                    <td className="py-1.5">
                      {offer.merchantName}
                      {offer.rating != null && <span className="ml-1 text-slate-400">★ {offer.rating}</span>}
                    </td>
                    <td className="py-1.5 text-right font-medium">{formatPrice(offer.price)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  )
}
