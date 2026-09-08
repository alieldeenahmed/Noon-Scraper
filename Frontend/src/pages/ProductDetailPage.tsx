import { useEffect, useState } from 'react'
import { useParams } from 'react-router'
import { getDiscountFlags, getProduct, getProductHistory, getRestockEvents } from '../api/client'
import type { DiscountFlag, PriceSnapshot, ProductDetail, RestockEvent } from '../api/types'
import { formatCategory, formatDate, formatPrice } from '../lib/format'
import Badge from '../components/Badge'
import CheckNowPanel from '../components/CheckNowPanel'
import NotifyMeButton from '../components/NotifyMeButton'
import PriceHistoryChart from '../components/PriceHistoryChart'

export default function ProductDetailPage() {
  const { id } = useParams<{ id: string }>()
  const productId = Number(id)

  const [product, setProduct] = useState<ProductDetail | null>(null)
  const [history, setHistory] = useState<PriceSnapshot[]>([])
  const [discountFlags, setDiscountFlags] = useState<DiscountFlag[]>([])
  const [restocks, setRestocks] = useState<RestockEvent[]>([])
  const [notFound, setNotFound] = useState(false)

  useEffect(() => {
    getProduct(productId)
      .then(setProduct)
      .catch(() => setNotFound(true))
    getProductHistory(productId).then(setHistory).catch(() => {})
    getDiscountFlags(productId).then(setDiscountFlags).catch(() => {})
    getRestockEvents(productId).then(setRestocks).catch(() => {})
  }, [productId])

  if (notFound) {
    return <p className="text-sm font-medium text-red-700">Product not found.</p>
  }

  if (!product) {
    return <p className="text-sm text-ink-900/50">Loading…</p>
  }

  return (
    <div className="grid gap-8 lg:grid-cols-[1fr_320px] lg:items-start">
      <div className="space-y-6">
        <div>
          <a
            href={product.url}
            target="_blank"
            rel="noreferrer"
            className="text-xs text-ink-900/50 hover:text-ink-900"
          >
            View on noon.com ↗
          </a>
          <h1 className="mt-2 text-4xl leading-tight font-medium tracking-tight text-ink-900">
            {product.name ?? product.url}
          </h1>
          <div className="mt-3 flex flex-wrap items-center gap-1.5">
            <Badge>{formatCategory(product.category)}</Badge>
            {product.merchantName && <Badge>{product.merchantName}</Badge>}
            {product.latestStock === false && <Badge dot="red">Out of stock</Badge>}
            {product.latestDiscountPercent != null && <Badge dot="brand">-{product.latestDiscountPercent}%</Badge>}
            {discountFlags.length > 0 && <Badge dot="red">Fake-discount flagged</Badge>}
          </div>
        </div>

        <div className="rounded-xl border border-ink-900/10 bg-ink-800 p-5">
          <h2 className="mb-3 text-xs tracking-wide text-white/50 uppercase">Price history</h2>
          <PriceHistoryChart history={history} />
        </div>

        {discountFlags.length > 0 && (
          <div className="rounded-xl border border-red-500/30 bg-ink-800 p-5">
            <h2 className="text-xs tracking-wide text-red-400 uppercase">Fake-discount flags</h2>
            <ul className="mt-2 space-y-1 text-sm text-white/60">
              {discountFlags.map((flag, i) => (
                <li key={i}>
                  Claims {flag.discountPercent}% off to {formatPrice(flag.discountedPrice)}, but the price was{' '}
                  {formatPrice(flag.priorHighPrice)} as recently as {formatDate(flag.priorHighDetectedAt)} —
                  flagged {formatDate(flag.detectedAt)}.
                </li>
              ))}
            </ul>
          </div>
        )}

        {restocks.length > 0 && (
          <div className="rounded-xl border border-ink-900/10 bg-ink-800 p-5">
            <h2 className="text-xs tracking-wide text-white/50 uppercase">Restock history</h2>
            <ul className="mt-2 space-y-1 text-sm text-white/60">
              {restocks.map((restock, i) => (
                <li key={i}>Back in stock — {formatDate(restock.detectedAt)}</li>
              ))}
            </ul>
          </div>
        )}
      </div>

      <div className="space-y-4 lg:sticky lg:top-24">
        <div className="rounded-xl border border-ink-900/10 bg-ink-800 p-5">
          <p className="text-3xl font-light tracking-tight text-white">{formatPrice(product.latestPrice)}</p>
          <p className="mt-1 text-xs text-white/30">Last crawled {formatDate(product.lastCrawledAt)}</p>
          <div className="mt-4">
            <NotifyMeButton productId={product.id} />
          </div>
        </div>

        <CheckNowPanel productId={product.id} />
      </div>
    </div>
  )
}
