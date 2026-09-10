import { useEffect, useMemo, useState } from 'react'
import { getProducts } from '../api/client'
import type { Category, ProductListItem } from '../api/types'
import { formatCategory } from '../lib/format'
import AddProductForm from '../components/AddProductForm'
import ProductRow, { ROW_GRID } from '../components/ProductRow'

const categories: Category[] = ['Mobiles', 'Laptops', 'SkinCare', 'HairCare', 'PersonalCare']

type SortKey = 'crawled' | 'price' | 'discount'
type SortDir = 'asc' | 'desc'

export default function ProductListPage() {
  const [products, setProducts] = useState<ProductListItem[] | null>(null)
  const [category, setCategory] = useState<Category | ''>('')
  const [search, setSearch] = useState('')
  const [sortKey, setSortKey] = useState<SortKey>('crawled')
  const [sortDir, setSortDir] = useState<SortDir>('desc')
  const [error, setError] = useState(false)

  const load = () => {
    getProducts(category || undefined)
      .then((data) => {
        setProducts(data)
        setError(false)
      })
      .catch(() => setError(true))
  }

  useEffect(load, [category])

  function toggleSort(key: SortKey) {
    if (key === sortKey) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'))
    } else {
      setSortKey(key)
      setSortDir('desc')
    }
  }

  const visible = useMemo(() => {
    if (!products) return null

    const q = search.trim().toLowerCase()
    const filtered = q ? products.filter((p) => (p.name ?? p.url).toLowerCase().includes(q)) : products

    const sortValue = (p: ProductListItem): number => {
      if (sortKey === 'price') return p.latestPrice ?? -Infinity
      if (sortKey === 'discount') return p.latestDiscountPercent ?? -Infinity
      return p.lastCrawledAt ? new Date(p.lastCrawledAt).getTime() : -Infinity
    }

    const sign = sortDir === 'asc' ? 1 : -1
    return [...filtered].sort((a, b) => (sortValue(a) - sortValue(b)) * sign)
  }, [products, search, sortKey, sortDir])

  const inStock = products?.filter((p) => p.latestStock !== false).length ?? 0
  const onDiscount = products?.filter((p) => p.latestDiscountPercent != null).length ?? 0

  return (
    <div className="space-y-10">
      <section className="grid gap-10 border-b border-ink-300 pb-10 lg:grid-cols-[1.4fr_1fr]">
        <div>
          <p className="text-xs text-ink-600 lowercase">noon.com/egypt-en · itemized watch list</p>
          <h1 className="mt-3 max-w-lg text-4xl leading-[1.15] font-bold tracking-tight sm:text-5xl">
            Every price change, logged the moment it happens.
          </h1>
          <p className="mt-4 max-w-md text-sm text-ink-600">
            Paste a product link. We check it against its own history and flag discounts that don’t add up.
          </p>
        </div>
        <dl className="space-y-3 self-center">
          <StatRow label="items tracked" value={products?.length ?? '—'} />
          <StatRow label="in stock" value={products ? inStock : '—'} className="text-flag-green" />
          <StatRow label="on discount" value={products ? onDiscount : '—'} className="text-flag-red" />
        </dl>
      </section>

      <AddProductForm onAdded={load} />

      <section>
        <div className="space-y-4 border-b border-ink-900 pb-3">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-baseline sm:justify-between">
            <div className="flex flex-wrap items-baseline gap-5 text-sm">
              <FilterLink active={category === ''} onClick={() => setCategory('')}>
                All
              </FilterLink>
              {categories.map((c) => (
                <FilterLink key={c} active={category === c} onClick={() => setCategory(c)}>
                  {formatCategory(c)}
                </FilterLink>
              ))}
            </div>
            <input
              type="search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="search items…"
              className="w-full border-b border-ink-300 bg-transparent py-1 text-sm text-ink-900 placeholder:text-ink-600 focus:border-ink-900 focus:outline-none sm:w-48"
            />
          </div>

          <div className="flex flex-wrap items-baseline justify-between gap-x-5 gap-y-2 text-xs text-ink-600 lowercase">
            <span>sort by</span>
            <span className="mr-auto flex flex-wrap gap-4">
              <SortLink label="last crawled" sortKey="crawled" active={sortKey} dir={sortDir} onClick={toggleSort} />
              <SortLink label="price" sortKey="price" active={sortKey} dir={sortDir} onClick={toggleSort} />
              <SortLink label="discount" sortKey="discount" active={sortKey} dir={sortDir} onClick={toggleSort} />
            </span>
            <span>{visible ? `${visible.length} items` : '—'}</span>
          </div>
        </div>

        {error && <p className="mt-4 text-sm font-bold text-flag-red">Couldn’t reach the API. Is it running?</p>}
        {!error && products === null && <p className="mt-4 text-sm text-ink-600">loading…</p>}
        {visible?.length === 0 && <p className="mt-4 text-sm text-ink-600">No products match.</p>}

        {visible && visible.length > 0 && (
          <div>
            <div className={`${ROW_GRID} hidden border-b border-ink-900 pb-2 text-xs text-ink-600 lowercase sm:grid`}>
              <span>item</span>
              <span className="text-right">price</span>
              <span className="text-right">status</span>
              <span className="text-right">last crawled</span>
            </div>
            {visible.map((product) => (
              <ProductRow key={product.id} product={product} />
            ))}
          </div>
        )}
      </section>
    </div>
  )
}

function StatRow({
  label,
  value,
  className = '',
}: {
  label: string
  value: number | string
  className?: string
}) {
  return (
    <div className="flex items-baseline justify-between gap-4 border-b border-dashed border-ink-300 pb-2">
      <span className="text-sm text-ink-600 lowercase">{label}</span>
      <span className={`text-xl font-bold ${className}`}>{value}</span>
    </div>
  )
}

function FilterLink({
  active,
  onClick,
  children,
}: {
  active: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <button
      onClick={onClick}
      className={active ? 'border-b-2 border-ink-900 pb-0.5 font-bold' : 'text-ink-600 hover:text-ink-900'}
    >
      {children}
    </button>
  )
}

function SortLink({
  label,
  sortKey,
  active,
  dir,
  onClick,
}: {
  label: string
  sortKey: SortKey
  active: SortKey
  dir: SortDir
  onClick: (key: SortKey) => void
}) {
  const isActive = active === sortKey
  return (
    <button onClick={() => onClick(sortKey)} className={isActive ? 'font-bold text-ink-900' : 'hover:text-ink-900'}>
      {label}
      {isActive && <span className="ml-1">{dir === 'asc' ? '↑' : '↓'}</span>}
    </button>
  )
}
