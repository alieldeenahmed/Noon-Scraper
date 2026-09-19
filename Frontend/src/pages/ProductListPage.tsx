import { useEffect, useState } from 'react'
import { getProducts, getProductStats } from '../api/client'
import type { Category, PagedResult, ProductListItem, ProductSortKey, ProductStats, SortDirection } from '../api/types'
import { formatCategory } from '../lib/format'
import AddProductForm from '../components/AddProductForm'
import ProductRow, { ROW_GRID } from '../components/ProductRow'

const categories: Category[] = ['Mobiles', 'Laptops', 'SkinCare', 'HairCare', 'PersonalCare']

const PAGE_SIZE = 25
const SEARCH_DEBOUNCE_MS = 300

export default function ProductListPage() {
  const [result, setResult] = useState<PagedResult<ProductListItem> | null>(null)
  const [stats, setStats] = useState<ProductStats | null>(null)
  const [category, setCategory] = useState<Category | ''>('')
  const [searchInput, setSearchInput] = useState('')
  const [search, setSearch] = useState('')
  const [sortKey, setSortKey] = useState<ProductSortKey>('crawled')
  const [sortDir, setSortDir] = useState<SortDirection>('desc')
  const [page, setPage] = useState(1)
  const [reloadCount, setReloadCount] = useState(0)
  const [error, setError] = useState(false)

  // Search runs in the database now, so wait for a pause in typing instead
  // of firing a request per keystroke.
  useEffect(() => {
    const timer = setTimeout(() => {
      setSearch(searchInput.trim())
      setPage(1)
    }, SEARCH_DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [searchInput])

  // The previous page stays on screen while the next one loads; `cancelled`
  // drops a slow response that a newer query has already superseded.
  useEffect(() => {
    let cancelled = false
    getProducts({ category: category || undefined, search, sortBy: sortKey, sortDir, page, pageSize: PAGE_SIZE })
      .then((data) => {
        if (cancelled) return
        setResult(data)
        setError(false)
      })
      .catch(() => {
        if (!cancelled) setError(true)
      })
    return () => {
      cancelled = true
    }
  }, [category, search, sortKey, sortDir, page, reloadCount])

  useEffect(() => {
    let cancelled = false
    getProductStats(category || undefined)
      .then((data) => {
        if (!cancelled) setStats(data)
      })
      .catch(() => {})
    return () => {
      cancelled = true
    }
  }, [category, reloadCount])

  function selectCategory(next: Category | '') {
    setCategory(next)
    setPage(1)
  }

  function toggleSort(key: ProductSortKey) {
    if (key === sortKey) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'))
    } else {
      setSortKey(key)
      setSortDir('desc')
    }
    setPage(1)
  }

  const visible = result?.items ?? null
  const firstShown = result && result.items.length > 0 ? (result.page - 1) * result.pageSize + 1 : 0
  const lastShown = result && result.items.length > 0 ? firstShown + result.items.length - 1 : 0

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
          <StatRow label="items tracked" value={stats?.total ?? '—'} />
          <StatRow label="in stock" value={stats?.inStock ?? '—'} className="text-flag-green" />
          <StatRow label="on discount" value={stats?.onDiscount ?? '—'} className="text-flag-red" />
        </dl>
      </section>

      <AddProductForm onAdded={() => setReloadCount((n) => n + 1)} />

      <section>
        <div className="space-y-4 border-b border-ink-900 pb-3">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-baseline sm:justify-between">
            <div className="flex flex-wrap items-baseline gap-5 text-sm">
              <FilterLink active={category === ''} onClick={() => selectCategory('')}>
                All
              </FilterLink>
              {categories.map((c) => (
                <FilterLink key={c} active={category === c} onClick={() => selectCategory(c)}>
                  {formatCategory(c)}
                </FilterLink>
              ))}
            </div>
            <input
              type="search"
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
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
            <span>{result ? `${result.total} items` : '—'}</span>
          </div>
        </div>

        {error && <p className="mt-4 text-sm font-bold text-flag-red">Couldn’t reach the API. Is it running?</p>}
        {!error && result === null && <p className="mt-4 text-sm text-ink-600">loading…</p>}
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

        {result && result.totalPages > 1 && (
          <nav className="mt-6 flex items-baseline justify-between gap-4 border-t border-ink-900 pt-3 text-xs lowercase">
            <button
              onClick={() => setPage((p) => p - 1)}
              disabled={result.page <= 1}
              className="hover:text-ink-900 disabled:cursor-default disabled:opacity-30"
            >
              ← prev
            </button>
            <span className="text-ink-600">
              {firstShown}–{lastShown} of {result.total} · page {result.page} of {result.totalPages}
            </span>
            <button
              onClick={() => setPage((p) => p + 1)}
              disabled={result.page >= result.totalPages}
              className="hover:text-ink-900 disabled:cursor-default disabled:opacity-30"
            >
              next →
            </button>
          </nav>
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
  sortKey: ProductSortKey
  active: ProductSortKey
  dir: SortDirection
  onClick: (key: ProductSortKey) => void
}) {
  const isActive = active === sortKey
  return (
    <button onClick={() => onClick(sortKey)} className={isActive ? 'font-bold text-ink-900' : 'hover:text-ink-900'}>
      {label}
      {isActive && <span className="ml-1">{dir === 'asc' ? '↑' : '↓'}</span>}
    </button>
  )
}
