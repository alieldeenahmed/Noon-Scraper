import { z } from 'zod'

// The API's response contract, as runtime schemas. The TypeScript types in
// types.ts are inferred from these, so there's one definition, and every response
// is checked against it at the boundary: if the backend's DTOs drift from what the
// UI expects, the failure is a clear "unexpected response" error right where the
// data arrives, not an undefined three components deeper.
//
// Backend/NoonScraper.Api/Dtos is the other side. ContractTests (backend) pins the
// exact JSON the API produces into fixtures, and contract.test.ts (here) parses
// those same fixtures with these schemas - so a change on either side fails a test.
//
// ASP.NET serializes camelCase, enums as strings, and null (not absent) for
// missing values; timestamps are ISO-8601 strings.

export const categorySchema = z.enum(['Mobiles', 'Laptops', 'SkinCare', 'HairCare', 'PersonalCare'])

export const productSourceSchema = z.enum(['Seed', 'UserAdded'])

// A crawl or check request: Pending -> Running -> Completed | Failed.
export const jobStatusSchema = z.enum(['Pending', 'Running', 'Completed', 'Failed'])

export const productListItemSchema = z.object({
  id: z.number().int(),
  url: z.string(),
  name: z.string().nullable(),
  category: categorySchema.nullable(),
  merchantName: z.string().nullable(),
  rating: z.number().nullable(),
  isActive: z.boolean(),
  latestPrice: z.number().nullable(),
  latestDiscountPercent: z.number().nullable(),
  latestStock: z.boolean().nullable(),
  lastCrawledAt: z.string().nullable(),
})

export const pagedResultSchema = <T extends z.ZodType>(item: T) =>
  z.object({
    items: z.array(item),
    total: z.number().int(),
    page: z.number().int(),
    pageSize: z.number().int(),
    totalPages: z.number().int(),
  })

export const productStatsSchema = z.object({
  total: z.number().int(),
  inStock: z.number().int(),
  onDiscount: z.number().int(),
})

export const crawlStatusSchema = z.object({
  requestId: z.number().int(),
  status: jobStatusSchema,
  failureStage: z.string().nullable(),
  errorMessage: z.string().nullable(),
  requestedAt: z.string(),
  startedAt: z.string().nullable(),
  completedAt: z.string().nullable(),
  runUrl: z.string().nullable(),
})

export const productDetailSchema = z.object({
  id: z.number().int(),
  url: z.string(),
  noonProductId: z.string().nullable(),
  name: z.string().nullable(),
  category: categorySchema.nullable(),
  merchantName: z.string().nullable(),
  rating: z.number().nullable(),
  source: productSourceSchema,
  isActive: z.boolean(),
  addedAt: z.string(),
  latestPrice: z.number().nullable(),
  latestDiscountPercent: z.number().nullable(),
  latestStock: z.boolean().nullable(),
  lastCrawledAt: z.string().nullable(),
  // The most recent "crawl this now" request, if any.
  crawl: crawlStatusSchema.nullable(),
})

export const priceSnapshotSchema = z.object({
  price: z.number(),
  stock: z.boolean(),
  discountPercent: z.number().nullable(),
  crawledAt: z.string(),
})

export const discountFlagSchema = z.object({
  priorHighPrice: z.number(),
  priorHighDetectedAt: z.string(),
  historicalLowPrice: z.number().nullable(),
  discountedPrice: z.number(),
  discountPercent: z.number(),
  detectedAt: z.string(),
})

export const restockEventSchema = z.object({
  detectedAt: z.string(),
})

export const offerSchema = z.object({
  merchantName: z.string(),
  price: z.number(),
  rating: z.number().nullable(),
})

export const checkNowAcceptedSchema = z.object({
  requestId: z.number().int(),
  status: jobStatusSchema,
})

export const checkNowResultSchema = z.object({
  requestId: z.number().int(),
  status: jobStatusSchema,
  lowestPrice: z.number().nullable(),
  lowestPriceMerchant: z.string().nullable(),
  offers: z.array(offerSchema).nullable(),
  errorMessage: z.string().nullable(),
  failureStage: z.string().nullable(),
  requestedAt: z.string(),
  startedAt: z.string().nullable(),
  completedAt: z.string().nullable(),
  runUrl: z.string().nullable(),
})

// RFC 7807 problem details, which is what every error response from the API is.
// productId is only present on the 409 "already tracked" response.
export const problemDetailsSchema = z.object({
  title: z.string().optional(),
  detail: z.string().optional(),
  status: z.number().optional(),
  traceId: z.string().optional(),
  productId: z.number().int().optional(),
})
