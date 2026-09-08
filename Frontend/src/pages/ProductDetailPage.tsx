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
    return <p className="text-sm text-red-600">Product not found.</p>
  }

  if (!product) {
    return <p className="text-sm text-slate-500">Loading…</p>
  }

  return (
    <div className="space-y-6">
      <div>
        <a href={product.url} target="_blank" rel="noreferrer" className="text-xs text-slate-400 hover:underline">
          View on noon.com ↗
        </a>
        <h1 className="mt-1 text-xl font-semibold text-slate-900">{product.name ?? product.url}</h1>
        <div className="mt-2 flex flex-wrap items-center gap-1.5">
          <Badge>{formatCategory(product.category)}</Badge>
          {product.merchantName && <Badge tone="purple">{product.merchantName}</Badge>}
          {product.latestStock === false && <Badge tone="red">Out of stock</Badge>}
          {product.latestDiscountPercent != null && <Badge tone="green">-{product.latestDiscountPercent}%</Badge>}
          {discountFlags.length > 0 && <Badge tone="amber">Fake-discount flagged</Badge>}
        </div>
      </div>

      <div className="flex items-center justify-between rounded-lg border border-slate-200 bg-white p-4">
        <div>
          <p className="text-2xl font-semibold text-slate-900">{formatPrice(product.latestPrice)}</p>
          <p className="text-xs text-slate-400">Last crawled {formatDate(product.lastCrawledAt)}</p>
        </div>
        <NotifyMeButton productId={product.id} />
      </div>

      <div className="rounded-lg border border-slate-200 bg-white p-4">
        <h2 className="mb-3 font-medium text-slate-900">Price history</h2>
        <PriceHistoryChart history={history} />
      </div>

      <CheckNowPanel productId={product.id} />

      {discountFlags.length > 0 && (
        <div className="rounded-lg border border-amber-200 bg-amber-50 p-4">
          <h2 className="font-medium text-amber-900">Fake-discount flags</h2>
          <ul className="mt-2 space-y-1 text-sm text-amber-800">
            {discountFlags.map((flag, i) => (
              <li key={i}>
                Claims {flag.discountPercent}% off to {formatPrice(flag.discountedPrice)}, but the price was{' '}
                {formatPrice(flag.priorHighPrice)} as recently as {formatDate(flag.priorHighDetectedAt)} — flagged{' '}
                {formatDate(flag.detectedAt)}.
              </li>
            ))}
          </ul>
        </div>
      )}

      {restocks.length > 0 && (
        <div className="rounded-lg border border-slate-200 bg-white p-4">
          <h2 className="font-medium text-slate-900">Restock history</h2>
          <ul className="mt-2 space-y-1 text-sm text-slate-600">
            {restocks.map((restock, i) => (
              <li key={i}>Back in stock — {formatDate(restock.detectedAt)}</li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}
