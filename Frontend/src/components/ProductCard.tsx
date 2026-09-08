import { Link } from 'react-router'
import type { ProductListItem } from '../api/types'
import { formatCategory, formatDate, formatPrice } from '../lib/format'
import Badge from './Badge'

export default function ProductCard({ product }: { product: ProductListItem }) {
  return (
    <Link
      to={`/products/${product.id}`}
      className="flex min-w-0 flex-col rounded-xl border border-ink-900/10 bg-ink-800 p-5 transition hover:border-brand-400/60 hover:shadow-[0_0_0_1px_rgba(255,239,77,0.15)]"
    >
      <div className="flex flex-wrap items-center gap-1.5">
        <Badge>{formatCategory(product.category)}</Badge>
        {product.latestStock === false && <Badge dot="red">Out of stock</Badge>}
      </div>

      <p className="mt-3 line-clamp-2 flex-1 text-white">{product.name ?? product.url}</p>

      <div className="mt-4 flex items-end justify-between">
        <div>
          <p className="text-xl font-light tracking-tight text-white">{formatPrice(product.latestPrice)}</p>
          {product.latestDiscountPercent != null && (
            <p className="mt-0.5 text-sm text-brand-400">-{product.latestDiscountPercent}% off</p>
          )}
        </div>
        {product.rating != null && <p className="text-sm text-white/40">★ {product.rating}</p>}
      </div>

      <p className="mt-3 text-xs text-white/30">
        {product.merchantName ? `${product.merchantName} · ` : ''}
        Last crawled {formatDate(product.lastCrawledAt)}
      </p>
    </Link>
  )
}
