import { useEffect, useState } from 'react'
import { getProducts } from '../api/client'
import type { Category, ProductListItem } from '../api/types'
import { formatCategory } from '../lib/format'
import AddProductForm from '../components/AddProductForm'
import ProductCard from '../components/ProductCard'

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
    <div className="space-y-14">
      <section className="grid gap-10 lg:grid-cols-[1.3fr_1fr] lg:items-end">
        <div>
          <p className="text-xs tracking-wide text-ink-900/60 uppercase">Noon Scraper</p>
          <h1 className="mt-3 max-w-xl text-5xl leading-[1.05] font-medium tracking-tight text-ink-900">
            Track prices. Catch fake discounts. Never miss a restock.
          </h1>
        </div>
        <div className="flex gap-8 lg:justify-end">
          <Stat label="Tracked" value={products?.length ?? '—'} />
          <Stat label="In stock" value={products ? inStock : '—'} />
          <Stat label="On discount" value={products ? onDiscount : '—'} />
        </div>
      </section>

      <AddProductForm onAdded={load} />

      <section className="space-y-5">
        <div className="flex flex-wrap items-center gap-2">
          <FilterPill active={category === ''} onClick={() => setCategory('')}>
            All
          </FilterPill>
          {categories.map((c) => (
            <FilterPill key={c} active={category === c} onClick={() => setCategory(c)}>
              {formatCategory(c)}
            </FilterPill>
          ))}
        </div>

        {error && <p className="text-sm font-medium text-red-700">Couldn’t reach the API. Is it running?</p>}
        {!error && products === null && <p className="text-sm text-ink-900/50">Loading…</p>}
        {products?.length === 0 && <p className="text-sm text-ink-900/50">No products tracked yet.</p>}

        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {products?.map((product) => <ProductCard key={product.id} product={product} />)}
        </div>
      </section>
    </div>
  )
}

function Stat({ label, value }: { label: string; value: number | string }) {
  return (
    <div>
      <p className="text-3xl font-medium tracking-tight text-ink-900">{value}</p>
      <p className="text-xs tracking-wide text-ink-900/60 uppercase">{label}</p>
    </div>
  )
}

function FilterPill({
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
      className={`rounded-full border px-3.5 py-1.5 text-sm transition ${
        active
          ? 'border-ink-900 bg-ink-900 text-brand-400'
          : 'border-ink-900/20 bg-transparent text-ink-900/70 hover:border-ink-900/40'
      }`}
    >
      {children}
    </button>
  )
}
