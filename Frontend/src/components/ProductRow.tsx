import { Link } from 'react-router'
import type { ProductListItem } from '../api/types'
import { formatCategory, formatPrice, formatTime } from '../lib/format'

export const ROW_GRID = 'sm:grid sm:grid-cols-[1fr_110px_150px_80px] sm:items-baseline sm:gap-4'

export default function ProductRow({ product }: { product: ProductListItem }) {
  const status =
    product.latestStock === false
      ? { text: 'out of stock', className: 'text-flag-red' }
      : product.latestDiscountPercent != null
        ? { text: `-${product.latestDiscountPercent}%`, className: 'text-flag-red' }
        : { text: 'steady', className: 'text-ink-600' }

  return (
    <Link
      to={`/products/${product.id}`}
      className={`${ROW_GRID} block border-b border-dotted border-ink-300 py-3 transition hover:bg-ink-900/[0.03]`}
    >
      <div className="min-w-0">
        <p className="text-xs text-ink-600 lowercase">{formatCategory(product.category)}</p>
        <p className="truncate text-ink-900">{product.name ?? product.url}</p>
      </div>
      <div className="mt-1.5 flex items-baseline gap-4 text-sm sm:mt-0 sm:contents">
        <p className="sm:text-right">{formatPrice(product.latestPrice)}</p>
        <p className={`sm:text-right ${status.className}`}>{status.text}</p>
        <p className="text-xs text-ink-600 sm:text-right">
          {product.lastCrawledAt ? formatTime(product.lastCrawledAt) : '—'}
        </p>
      </div>
    </Link>
  )
}
