// Mirrors the backend's DTOs (Backend/NoonScraper.Api/Dtos) field-for-field.
// ASP.NET Core serializes to camelCase and enums as strings by default -
// see Program.cs's JsonStringEnumConverter - so these line up directly with
// what the API actually returns, no mapping layer needed.

export type Category = 'Mobiles' | 'Laptops' | 'SkinCare' | 'HairCare' | 'PersonalCare'

export type ProductSource = 'Seed' | 'UserAdded'

export type CheckNowStatus = 'Pending' | 'Completed' | 'Failed'

export interface ProductListItem {
  id: number
  url: string
  name: string | null
  category: Category | null
  merchantName: string | null
  rating: number | null
  isActive: boolean
  latestPrice: number | null
  latestDiscountPercent: number | null
  latestStock: boolean | null
  lastCrawledAt: string | null
}

export interface ProductDetail {
  id: number
  url: string
  noonProductId: string | null
  name: string | null
  category: Category | null
  merchantName: string | null
  rating: number | null
  source: ProductSource
  isActive: boolean
  addedAt: string
  latestPrice: number | null
  latestDiscountPercent: number | null
  latestStock: boolean | null
  lastCrawledAt: string | null
}

export interface PriceSnapshot {
  price: number
  stock: boolean
  discountPercent: number | null
  crawledAt: string
}

export interface DiscountFlag {
  priorHighPrice: number
  priorHighDetectedAt: string
  discountedPrice: number
  discountPercent: number
  detectedAt: string
}

export interface RestockEvent {
  detectedAt: string
}

export interface Offer {
  merchantName: string
  price: number
  rating: number | null
}

export interface CheckNowAccepted {
  requestId: number
  status: CheckNowStatus
}

export interface CheckNowResult {
  requestId: number
  status: CheckNowStatus
  lowestPrice: number | null
  lowestPriceMerchant: string | null
  offers: Offer[] | null
  errorMessage: string | null
}
