import { useEffect, useState } from 'react'
import { useParams } from 'react-router'
import { ApiError, getDiscountFlags, getProduct, getProductHistory, getRestockEvents } from '../api/client'
import type { DiscountFlag, PriceSnapshot, ProductDetail, RestockEvent } from '../api/types'
import { formatCategory, formatDate, formatPrice, formatPriceExact } from '../lib/format'
import CheckNowPanel from '../components/CheckNowPanel'
import CrawlProgressBar from '../components/CrawlProgressBar'
import JobFailure from '../components/JobFailure'
import NotifyMeButton from '../components/NotifyMeButton'
import PriceHistoryLog from '../components/PriceHistoryLog'

export const POLL_INTERVAL_MS = 4000
export const POLL_TIMEOUT_MS = 5 * 60 * 1000

// Whether the first crawl of this product is still worth waiting for: it has no
// reading yet, and its crawl request hasn't been closed out as failed.
function awaitingFirstCrawl(product: ProductDetail): boolean {
  return !product.lastCrawledAt && product.crawl?.status !== 'Failed'
}

export default function ProductDetailPage() {
  const { id } = useParams<{ id: string }>()
  const productId = Number(id)

  const [product, setProduct] = useState<ProductDetail | null>(null)
  const [history, setHistory] = useState<PriceSnapshot[]>([])
  const [discountFlags, setDiscountFlags] = useState<DiscountFlag[]>([])
  const [restocks, setRestocks] = useState<RestockEvent[]>([])
  const [loadError, setLoadError] = useState<'not-found' | 'unavailable' | null>(null)
  const [pollTimedOut, setPollTimedOut] = useState(false)
  const [pollLost, setPollLost] = useState(false)

  function loadDetails() {
    getProductHistory(productId).then(setHistory).catch(() => {})
    getDiscountFlags(productId).then(setDiscountFlags).catch(() => {})
    getRestockEvents(productId).then(setRestocks).catch(() => {})
  }

  useEffect(() => {
    setPollTimedOut(false)
    setPollLost(false)
    setLoadError(null)
    getProduct(productId)
      .then(setProduct)
      .catch((err) => setLoadError(err instanceof ApiError && err.status === 404 ? 'not-found' : 'unavailable'))
    loadDetails()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [productId])

  // A freshly-submitted product starts with no crawl data yet - the
  // crawl-product workflow (dispatched right when it was added) usually
  // fills it in within a couple minutes, so poll until it does instead of
  // leaving the page stuck showing an empty shell. The poll also ends when the
  // crawl request is reported Failed: there's nothing left to wait for.
  const waiting = product ? awaitingFirstCrawl(product) : false
  useEffect(() => {
    if (!waiting) return

    const startedAt = Date.now()
    const interval = setInterval(async () => {
      if (Date.now() - startedAt > POLL_TIMEOUT_MS) {
        clearInterval(interval)
        setPollTimedOut(true)
        return
      }

      try {
        const fresh = await getProduct(productId)
        setProduct(fresh)
        if (!awaitingFirstCrawl(fresh)) {
          clearInterval(interval)
          loadDetails()
        }
      } catch {
        clearInterval(interval)
        setPollLost(true)
      }
    }, POLL_INTERVAL_MS)

    return () => clearInterval(interval)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [product?.id, waiting])

  if (loadError === 'not-found') {
    return <p className="text-sm font-bold text-flag-red">product not found.</p>
  }

  if (loadError === 'unavailable') {
    return <p className="text-sm font-bold text-flag-red">couldn’t load this product right now. Try again in a moment.</p>
  }

  if (!product) {
    return <p className="text-sm text-ink-600">loading…</p>
  }

  const isPending = !product.lastCrawledAt
  const crawlFailed = isPending && product.crawl?.status === 'Failed'
  const metaParts = [formatCategory(product.category).toLowerCase()]
  if (product.latestDiscountPercent != null) metaParts.push(`-${product.latestDiscountPercent}% vs. list price`)
  if (product.latestStock === false) metaParts.push('out of stock')
  if (discountFlags.length > 0) metaParts.push('flagged as fake discount')

  const crawlLabel =
    product.crawl?.status === 'Pending' ? 'queued — waiting for a worker to pick it up…' : 'crawling this product now…'

  return (
    <div className="grid gap-10 lg:grid-cols-[1fr_320px] lg:items-start">
      <div className="space-y-6">
        <div>
          <div className="flex items-baseline justify-between gap-3">
            <a href={product.url} target="_blank" rel="noreferrer" className="text-xs text-ink-600 hover:text-ink-900">
              ← view on noon.com
            </a>
            <span className="text-xs text-ink-600">#{product.id}</span>
          </div>
          <h1 className="mt-2 text-3xl leading-tight font-bold tracking-tight sm:text-4xl">
            {product.name ?? product.url}
          </h1>
          {crawlFailed ? (
            <div className="mt-3 space-y-1">
              <JobFailure
                prefix="couldn’t crawl this product"
                message={product.crawl!.errorMessage}
                stage={product.crawl!.failureStage}
                runUrl={product.crawl!.runUrl}
              />
              <p className="text-xs text-ink-600">the daily crawl will try it again.</p>
            </div>
          ) : isPending ? (
            <div>
              <p className="mt-2 text-sm text-flag-gold lowercase">
                {pollTimedOut
                  ? 'still not back yet — refresh this page in a bit.'
                  : pollLost
                    ? 'lost connection — refresh this page to check again.'
                    : crawlLabel}
              </p>
              {!pollTimedOut && !pollLost && <CrawlProgressBar startedAt={product.addedAt} />}
            </div>
          ) : (
            <p className="mt-2 text-sm text-ink-600 lowercase">
              {metaParts.map((part, i) => (
                <span key={i}>
                  {i > 0 && <span className="mx-2 text-ink-300">|</span>}
                  <span className={i > 0 ? 'text-flag-red' : ''}>{part}</span>
                </span>
              ))}
            </p>
          )}
        </div>

        <div className="border-t border-ink-900 pt-6">
          <p className="mb-3 text-xs text-ink-600 lowercase">price history</p>
          <PriceHistoryLog history={history} />
        </div>

        {discountFlags.length > 0 && (
          <div className="border-t border-ink-900 pt-6">
            <p className="mb-3 text-xs text-ink-600 lowercase">discount flags</p>
            <div className="divide-y divide-dotted divide-ink-300">
              {discountFlags.map((flag, i) => (
                <p key={i} className="py-2 text-sm text-flag-red">
                  {formatDate(flag.detectedAt)} — {formatPriceExact(flag.discountedPrice)} advertised as -
                  {flag.discountPercent}%, but it was {formatPriceExact(flag.priorHighPrice)} only recently
                  {flag.historicalLowPrice != null &&
                    ` (lowest before that: ${formatPriceExact(flag.historicalLowPrice)})`}
                </p>
              ))}
            </div>
          </div>
        )}

        {restocks.length > 0 && (
          <div className="border-t border-ink-900 pt-6">
            <p className="mb-3 text-xs text-ink-600 lowercase">restock history</p>
            <div className="divide-y divide-dotted divide-ink-300">
              {restocks.map((restock, i) => (
                <p key={i} className="py-2 text-sm text-flag-green">
                  back in stock — {formatDate(restock.detectedAt)}
                </p>
              ))}
            </div>
          </div>
        )}
      </div>

      <div className="space-y-4 lg:sticky lg:top-6">
        <div className="border border-ink-900 p-4">
          <p className="text-xs text-ink-600">EGP</p>
          <p className="text-3xl font-bold">
            {isPending ? '…' : formatPrice(product.latestPrice).replace('EGP', '').trim()}
          </p>
          <p className="mt-1 text-xs text-ink-600">
            {isPending ? 'not crawled yet' : `last crawled ${formatDate(product.lastCrawledAt)}`}
          </p>
        </div>

        <NotifyMeButton productId={product.id} />

        <CheckNowPanel productId={product.id} />
      </div>
    </div>
  )
}
