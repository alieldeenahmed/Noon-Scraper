import type {
  CheckNowAccepted,
  CheckNowResult,
  Category,
  DiscountFlag,
  PriceSnapshot,
  ProductDetail,
  ProductListItem,
  RestockEvent,
} from './types'

const BASE_URL = import.meta.env.VITE_API_BASE_URL as string

export class ApiError extends Error {
  status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...init,
  })

  if (!response.ok) {
    throw new ApiError(response.status, `${init?.method ?? 'GET'} ${path} failed with ${response.status}`)
  }

  // 202/204 responses (e.g. check-now's Accepted) may have no body.
  const text = await response.text()
  return text ? (JSON.parse(text) as T) : (undefined as T)
}

export function getProducts(category?: Category): Promise<ProductListItem[]> {
  const query = category ? `?category=${category}` : ''
  return request(`/api/products${query}`)
}

export function getProduct(id: number): Promise<ProductDetail> {
  return request(`/api/products/${id}`)
}

export function getProductHistory(id: number): Promise<PriceSnapshot[]> {
  return request(`/api/products/${id}/history`)
}

export function getDiscountFlags(id: number): Promise<DiscountFlag[]> {
  return request(`/api/products/${id}/discount-flags`)
}

export function getRestockEvents(id: number): Promise<RestockEvent[]> {
  return request(`/api/products/${id}/restocks`)
}

export function createProduct(url: string): Promise<ProductDetail> {
  return request('/api/products', {
    method: 'POST',
    body: JSON.stringify({ url }),
  })
}

export function startCheckNow(productId: number): Promise<CheckNowAccepted> {
  return request(`/api/products/${productId}/check-now`, { method: 'POST' })
}

export function getCheckNowResult(productId: number, requestId: number): Promise<CheckNowResult> {
  return request(`/api/products/${productId}/check-now/${requestId}`)
}
