import { z } from 'zod'
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
import type {
  CheckNowAccepted,
  CheckNowResult,
  Category,
  DiscountFlag,
  PagedResult,
  PriceSnapshot,
  ProductDetail,
  ProductListItem,
  ProductSortKey,
  ProductStats,
  RestockEvent,
  SortDirection,
} from './types'

const BASE_URL = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? ''

// status 0 means the request never got an answer (offline, DNS, CORS, a cold
// server that timed out); 502 from our own API means it couldn't start a workflow;
// anything else is the HTTP status the API returned.
export class ApiError extends Error {
  status: number

  // The API's own explanation (RFC 7807 "detail"), when it sent one. Safe to show.
  detail?: string

  // Set on a 409 "already tracked": the product that already exists.
  productId?: number

  constructor(status: number, message: string, extras: { detail?: string; productId?: number } = {}) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.detail = extras.detail
    this.productId = extras.productId
  }
}

// The response wasn't the shape the UI was built against.
export class ContractError extends Error {
  constructor(path: string, cause: unknown) {
    super(`Unexpected response from ${path}`, { cause })
    this.name = 'ContractError'
  }
}

async function send(path: string, init?: RequestInit): Promise<string> {
  let response: Response
  try {
    response = await fetch(`${BASE_URL}${path}`, {
      headers: { 'Content-Type': 'application/json' },
      ...init,
    })
  } catch {
    throw new ApiError(0, `${init?.method ?? 'GET'} ${path} could not reach the server`)
  }

  const text = await response.text()

  if (!response.ok) {
    throw new ApiError(response.status, `${init?.method ?? 'GET'} ${path} failed with ${response.status}`, readProblem(text))
  }

  return text
}

function readProblem(body: string): { detail?: string; productId?: number } {
  try {
    const parsed = problemDetailsSchema.safeParse(JSON.parse(body))
    return parsed.success ? { detail: parsed.data.detail, productId: parsed.data.productId } : {}
  } catch {
    return {}
  }
}

async function request<T extends z.ZodType>(schema: T, path: string, init?: RequestInit): Promise<z.infer<T>> {
  const text = await send(path, init)

  let json: unknown
  try {
    json = JSON.parse(text)
  } catch (err) {
    throw new ContractError(path, err)
  }

  const parsed = schema.safeParse(json)
  if (!parsed.success) {
    throw new ContractError(path, parsed.error)
  }
  return parsed.data
}

export interface ProductQuery {
  category?: Category
  search?: string
  sortBy: ProductSortKey
  sortDir: SortDirection
  page: number
  pageSize: number
}

export function getProducts(query: ProductQuery): Promise<PagedResult<ProductListItem>> {
  const params = new URLSearchParams({
    sortBy: query.sortBy,
    sortDir: query.sortDir,
    page: String(query.page),
    pageSize: String(query.pageSize),
  })
  if (query.category) params.set('category', query.category)
  if (query.search) params.set('search', query.search)
  return request(pagedResultSchema(productListItemSchema), `/api/products?${params}`)
}

export function getProductStats(category?: Category): Promise<ProductStats> {
  const query = category ? `?category=${category}` : ''
  return request(productStatsSchema, `/api/products/stats${query}`)
}

export function getProduct(id: number): Promise<ProductDetail> {
  return request(productDetailSchema, `/api/products/${id}`)
}

export function getProductHistory(id: number): Promise<PriceSnapshot[]> {
  return request(z.array(priceSnapshotSchema), `/api/products/${id}/history`)
}

export function getDiscountFlags(id: number): Promise<DiscountFlag[]> {
  return request(z.array(discountFlagSchema), `/api/products/${id}/discount-flags`)
}

export function getRestockEvents(id: number): Promise<RestockEvent[]> {
  return request(z.array(restockEventSchema), `/api/products/${id}/restocks`)
}

export function createProduct(url: string): Promise<ProductDetail> {
  return request(productDetailSchema, '/api/products', {
    method: 'POST',
    body: JSON.stringify({ url }),
  })
}

export function startCheckNow(productId: number): Promise<CheckNowAccepted> {
  return request(checkNowAcceptedSchema, `/api/products/${productId}/check-now`, { method: 'POST' })
}

export function getCheckNowResult(productId: number, requestId: number): Promise<CheckNowResult> {
  return request(checkNowResultSchema, `/api/products/${productId}/check-now/${requestId}`)
}
