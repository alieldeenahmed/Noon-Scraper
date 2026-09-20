import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import type { z } from 'zod'
import {
  checkNowAcceptedSchema,
  checkNowResultSchema,
  discountFlagSchema,
  pagedResultSchema,
  priceSnapshotSchema,
  problemDetailsSchema,
  productDetailSchema,
  productListItemSchema,
  productStatsSchema,
  restockEventSchema,
} from './schemas'

// The other half of the API contract. Backend/NoonScraper.Tests/ContractTests.cs
// pins the exact JSON the real API produces into these files (and fails if the API
// stops producing it); here, the same files must satisfy the schemas the UI
// validates every response with. A DTO change that the frontend hasn't caught up
// with fails one of the two, in CI.
const CONTRACTS = resolve(__dirname, '../../../Backend/NoonScraper.Tests/Contracts')

function fixture(name: string): unknown {
  return JSON.parse(readFileSync(resolve(CONTRACTS, `${name}.json`), 'utf8'))
}

function parses(schema: z.ZodType, name: string) {
  const result = schema.safeParse(fixture(name))
  expect(result.success, result.success ? '' : JSON.stringify(result.error.issues, null, 2)).toBe(true)
  return result.data
}

describe('the API responses the backend produces', () => {
  it('product list', () => {
    const list = parses(pagedResultSchema(productListItemSchema), 'product-list') as { items: unknown[]; total: number }
    expect(list.items.length).toBe(list.total)
  })

  it('product stats', () => {
    parses(productStatsSchema, 'product-stats')
  })

  it.each(['product-detail-crawled', 'product-detail-crawl-pending', 'product-detail-crawl-failed'])('%s', (name) => {
    parses(productDetailSchema, name)
  })

  it('price history, discount flags, and restocks', () => {
    expect(parses(priceSnapshotSchema.array(), 'price-history')).toHaveLength(2)
    expect(parses(discountFlagSchema.array(), 'discount-flags')).toHaveLength(1)
    expect(parses(restockEventSchema.array(), 'restocks')).toHaveLength(1)
  })

  it('check-now accepted', () => {
    parses(checkNowAcceptedSchema, 'check-now-accepted')
  })

  it.each(['check-now-result-completed', 'check-now-result-failed', 'check-now-result-running'])('%s', (name) => {
    parses(checkNowResultSchema, name)
  })

  it('problem details, including the id on "already tracked"', () => {
    parses(problemDetailsSchema, 'problem-invalid-url')
    const conflict = parses(problemDetailsSchema, 'problem-already-tracked') as { productId?: number }
    expect(conflict.productId).toEqual(expect.any(Number))
  })
})

describe('what the fixtures say about behaviour the UI depends on', () => {
  it('a pending crawl has no failure fields and no start time', () => {
    const detail = productDetailSchema.parse(fixture('product-detail-crawl-pending'))
    expect(detail.crawl).toMatchObject({ status: 'Pending', failureStage: null, errorMessage: null, startedAt: null })
    expect(detail.lastCrawledAt).toBeNull()
  })

  it('a failed crawl carries a stage, a public message, and the workflow run link', () => {
    const detail = productDetailSchema.parse(fixture('product-detail-crawl-failed'))
    expect(detail.crawl?.status).toBe('Failed')
    expect(detail.crawl?.failureStage).toBe('scrape')
    expect(detail.crawl?.errorMessage).toBeTruthy()
    expect(detail.crawl?.runUrl).toMatch(/^https:\/\/github\.com\/.+\/actions\/runs\/\d+$/)
  })

  it('a completed check lists offers cheapest first, and the lowest matches the first', () => {
    const result = checkNowResultSchema.parse(fixture('check-now-result-completed'))
    expect(result.offers?.map((o) => o.price)).toEqual([...(result.offers ?? [])].map((o) => o.price).sort((a, b) => a - b))
    expect(result.lowestPrice).toBe(result.offers?.[0].price)
  })
})
