import { Link } from 'react-router'
import type { ProductListItem } from '../api/types'
import { formatCategory, formatDate, formatPrice } from '../lib/format'
import Badge from './Badge'

export default function ProductCard({ product }: { product: ProductListItem }) {
  return (
    <Link
      to={`/products/${product.id}`}
      className="block min-w-0 rounded-lg border border-slate-200 bg-white p-4 transition hover:border-slate-300 hover:shadow-sm"
    >
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0 flex-1">
          <p className="truncate font-medium text-slate-900">{product.name ?? product.url}</p>
          <div className="mt-1 flex flex-wrap items-center gap-1.5">
            <Badge>{formatCategory(product.category)}</Badge>
            {product.merchantName && <Badge tone="purple">{product.merchantName}</Badge>}
            {product.latestStock === false && <Badge tone="red">Out of stock</Badge>}
            {product.latestDiscountPercent != null && (
              <Badge tone="green">-{product.latestDiscountPercent}%</Badge>
            )}
          </div>
        </div>
        <div className="shrink-0 text-right">
          <p className="text-lg font-semibold text-slate-900">{formatPrice(product.latestPrice)}</p>
          {product.rating != null && <p className="text-sm text-slate-500">★ {product.rating}</p>}
        </div>
      </div>
      <p className="mt-3 text-xs text-slate-400">Last crawled {formatDate(product.lastCrawledAt)}</p>
    </Link>
  )
}
