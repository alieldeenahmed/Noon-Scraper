import { useEffect, useState } from 'react'
import { useParams } from 'react-router'
import { getDiscountFlags, getProduct, getProductHistory, getRestockEvents } from '../api/client'
import type { DiscountFlag, PriceSnapshot, ProductDetail, RestockEvent } from '../api/types'
import { formatCategory, formatDate, formatPrice } from '../lib/format'
import CheckNowPanel from '../components/CheckNowPanel'
import NotifyMeButton from '../components/NotifyMeButton'
import PriceHistoryLog from '../components/PriceHistoryLog'

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
    return <p className="text-sm font-bold text-flag-red">product not found.</p>
  }

  if (!product) {
    return <p className="text-sm text-ink-600">loading…</p>
  }

  const metaParts = [formatCategory(product.category).toLowerCase()]
  if (product.latestDiscountPercent != null) metaParts.push(`-${product.latestDiscountPercent}% vs. list price`)
  if (product.latestStock === false) metaParts.push('out of stock')
  if (discountFlags.length > 0) metaParts.push('flagged as fake discount')

  return (
    <div className="grid gap-10 lg:grid-cols-[1fr_320px] lg:items-start">
      <div className="space-y-6">
        <div>
          <a href={product.url} target="_blank" rel="noreferrer" className="text-xs text-ink-600 hover:text-ink-900">
            ← view on noon.com
          </a>
          <h1 className="mt-2 text-3xl leading-tight font-bold tracking-tight sm:text-4xl">
            {product.name ?? product.url}
          </h1>
          <p className="mt-2 text-sm text-ink-600 lowercase">
            {metaParts.map((part, i) => (
              <span key={i}>
                {i > 0 && <span className="mx-2 text-ink-300">|</span>}
                <span className={i > 0 ? 'text-flag-red' : ''}>{part}</span>
              </span>
            ))}
          </p>
        </div>

        <div className="border-t border-ink-900 pt-6">
          <p className="mb-3 text-xs text-ink-600 lowercase">price history</p>
          <PriceHistoryLog history={history} />
        </div>

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
          <p className="text-3xl font-bold">{formatPrice(product.latestPrice).replace('EGP', '').trim()}</p>
          <p className="mt-1 text-xs text-ink-600">last crawled {formatDate(product.lastCrawledAt)}</p>
        </div>

        <NotifyMeButton productId={product.id} />

        <CheckNowPanel productId={product.id} />
      </div>
    </div>
  )
}
