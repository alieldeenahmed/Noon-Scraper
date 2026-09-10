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

const exactCurrencyFormatter = new Intl.NumberFormat('en-EG', {
  style: 'currency',
  currency: 'EGP',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

export function formatPriceExact(price: number): string {
  return exactCurrencyFormatter.format(price)
}

export function formatDate(iso: string | null): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleString('en-GB', {
    dateStyle: 'medium',
    timeStyle: 'short',
  })
}

export function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })
}

export function formatShortDate(iso: string): string {
  return new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' })
}
