import type { z } from 'zod'
import type {
  categorySchema,
  checkNowAcceptedSchema,
  checkNowResultSchema,
  crawlStatusSchema,
  discountFlagSchema,
  jobStatusSchema,
  offerSchema,
  priceSnapshotSchema,
  productDetailSchema,
  productListItemSchema,
  productSourceSchema,
  productStatsSchema,
  restockEventSchema,
} from './schemas'

// Inferred from the runtime schemas in schemas.ts - the one place the API
// contract is written down. See the note there.

export type Category = z.infer<typeof categorySchema>

export type ProductSource = z.infer<typeof productSourceSchema>

export type JobStatus = z.infer<typeof jobStatusSchema>

export type ProductListItem = z.infer<typeof productListItemSchema>

export interface PagedResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export type ProductStats = z.infer<typeof productStatsSchema>

export type ProductSortKey = 'crawled' | 'price' | 'discount'

export type SortDirection = 'asc' | 'desc'

export type CrawlStatus = z.infer<typeof crawlStatusSchema>

export type ProductDetail = z.infer<typeof productDetailSchema>

export type PriceSnapshot = z.infer<typeof priceSnapshotSchema>

export type DiscountFlag = z.infer<typeof discountFlagSchema>

export type RestockEvent = z.infer<typeof restockEventSchema>

export type Offer = z.infer<typeof offerSchema>

export type CheckNowAccepted = z.infer<typeof checkNowAcceptedSchema>

export type CheckNowResult = z.infer<typeof checkNowResultSchema>

// A job that has stopped changing: nothing more to poll for.
export function isFinished(status: JobStatus): boolean {
  return status === 'Completed' || status === 'Failed'
}
