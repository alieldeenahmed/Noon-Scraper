import { useEffect, useState } from 'react'
import { getProducts } from '../api/client'
import type { Category, ProductListItem } from '../api/types'
import { formatCategory } from '../lib/format'
import AddProductForm from '../components/AddProductForm'
import ProductRow, { ROW_GRID } from '../components/ProductRow'

const categories: Category[] = ['Mobiles', 'Laptops', 'SkinCare', 'HairCare', 'PersonalCare']

export default function ProductListPage() {
  const [products, setProducts] = useState<ProductListItem[] | null>(null)
  const [category, setCategory] = useState<Category | ''>('')
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
        <div className="flex flex-wrap items-baseline justify-between gap-4 border-b border-ink-900 pb-3">
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
          <p className="text-xs text-ink-600 lowercase">
            {products ? `${products.length} items` : '—'} · sorted by last crawled
          </p>
        </div>

        {error && <p className="mt-4 text-sm font-bold text-flag-red">Couldn’t reach the API. Is it running?</p>}
        {!error && products === null && <p className="mt-4 text-sm text-ink-600">loading…</p>}
        {products?.length === 0 && <p className="mt-4 text-sm text-ink-600">No products tracked yet.</p>}

        {products && products.length > 0 && (
          <div>
            <div className={`${ROW_GRID} border-b border-ink-900 pb-2 text-xs text-ink-600 lowercase`}>
              <span>item</span>
              <span className="text-right">price</span>
              <span className="text-right">status</span>
              <span className="text-right">last crawled</span>
            </div>
            {products.map((product) => (
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
