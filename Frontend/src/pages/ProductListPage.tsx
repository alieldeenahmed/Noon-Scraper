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

  return (
    <div className="space-y-6">
      <AddProductForm onAdded={load} />

      <div className="flex items-center gap-2">
        <label htmlFor="category-filter" className="text-sm font-medium text-slate-700">
          Category
        </label>
        <select
          id="category-filter"
          value={category}
          onChange={(e) => setCategory(e.target.value as Category | '')}
          className="rounded-md border border-slate-300 px-2 py-1 text-sm focus:border-slate-500 focus:outline-none"
        >
          <option value="">All</option>
          {categories.map((c) => (
            <option key={c} value={c}>
              {formatCategory(c)}
            </option>
          ))}
        </select>
      </div>

      {error && <p className="text-sm text-red-600">Couldn’t reach the API. Is it running?</p>}
      {!error && products === null && <p className="text-sm text-slate-500">Loading…</p>}
      {products?.length === 0 && <p className="text-sm text-slate-500">No products tracked yet.</p>}

      <div className="grid gap-3">
        {products?.map((product) => <ProductCard key={product.id} product={product} />)}
      </div>
    </div>
  )
}
