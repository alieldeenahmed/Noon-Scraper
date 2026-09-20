import type {
  CheckNowResult,
  CrawlStatus,
  DiscountFlag,
  PriceSnapshot,
  ProductDetail,
  ProductListItem,
} from '../api/types'

// Complete, valid objects with the fields a test doesn't care about filled in, so
// each test states only what it's about.

export function product(overrides: Partial<ProductDetail> = {}): ProductDetail {
  return {
    id: 7,
    url: 'https://www.noon.com/egypt-en/some-phone/N123/p/',
    noonProductId: 'N123',
    name: 'Some Phone 128GB',
    category: 'Mobiles',
    merchantName: 'noon',
    rating: 4.5,
    source: 'UserAdded',
    isActive: true,
    addedAt: '2026-09-20T10:00:00Z',
    latestPrice: 1999,
    latestDiscountPercent: null,
    latestStock: true,
    lastCrawledAt: '2026-09-20T10:02:00Z',
    crawl: null,
    ...overrides,
  }
}

export function uncrawled(overrides: Partial<ProductDetail> = {}): ProductDetail {
  return product({
    name: null,
    category: null,
    merchantName: null,
    rating: null,
    latestPrice: null,
    latestStock: null,
    lastCrawledAt: null,
    ...overrides,
  })
}

export function crawl(overrides: Partial<CrawlStatus> = {}): CrawlStatus {
  return {
    requestId: 1,
    status: 'Pending',
    failureStage: null,
    errorMessage: null,
    requestedAt: '2026-09-20T10:00:00Z',
    startedAt: null,
    completedAt: null,
    runUrl: null,
    ...overrides,
  }
}

export function listItem(overrides: Partial<ProductListItem> = {}): ProductListItem {
  return {
    id: 1,
    url: 'https://www.noon.com/egypt-en/item/N1/p/',
    name: 'Item',
    category: 'Mobiles',
    merchantName: 'noon',
    rating: null,
    isActive: true,
    latestPrice: 100,
    latestDiscountPercent: null,
    latestStock: true,
    lastCrawledAt: '2026-09-20T10:00:00Z',
    ...overrides,
  }
}

export function snapshot(price: number, crawledAt: string, overrides: Partial<PriceSnapshot> = {}): PriceSnapshot {
  return { price, stock: true, discountPercent: null, crawledAt, ...overrides }
}

export function flag(overrides: Partial<DiscountFlag> = {}): DiscountFlag {
  return {
    priorHighPrice: 2500,
    priorHighDetectedAt: '2026-09-10T10:00:00Z',
    historicalLowPrice: 1800,
    discountedPrice: 2000,
    discountPercent: 40,
    detectedAt: '2026-09-20T10:00:00Z',
    ...overrides,
  }
}

export function checkResult(overrides: Partial<CheckNowResult> = {}): CheckNowResult {
  return {
    requestId: 5,
    status: 'Pending',
    lowestPrice: null,
    lowestPriceMerchant: null,
    offers: null,
    errorMessage: null,
    failureStage: null,
    requestedAt: '2026-09-20T10:00:00Z',
    startedAt: null,
    completedAt: null,
    runUrl: null,
    ...overrides,
  }
}

export function page<T>(items: T[], overrides: Partial<{ total: number; page: number; pageSize: number; totalPages: number }> = {}) {
  return { items, total: items.length, page: 1, pageSize: 25, totalPages: 1, ...overrides }
}
