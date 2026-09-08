import type { Category } from '../api/types'

const categoryLabels: Record<Category, string> = {
  Mobiles: 'Mobiles',
  Laptops: 'Laptops',
  SkinCare: 'Skin Care',
  HairCare: 'Hair Care',
  PersonalCare: 'Personal Care',
}

export function formatCategory(category: Category | null): string {
  return category ? categoryLabels[category] : 'Uncategorized'
}

const currencyFormatter = new Intl.NumberFormat('en-EG', {
  style: 'currency',
  currency: 'EGP',
  maximumFractionDigits: 0,
})

export function formatPrice(price: number | null): string {
  return price === null ? '—' : currencyFormatter.format(price)
}

export function formatDate(iso: string | null): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleString('en-GB', {
    dateStyle: 'medium',
    timeStyle: 'short',
  })
}
