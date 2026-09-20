import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, ContractError, createProduct, getProduct, getProducts, startCheckNow } from './client'
import type { CrawlStatus } from './types'
import { crawl, listItem, page, product } from '../test/factories'

function respond(status: number, body?: unknown) {
  const text = body === undefined ? '' : typeof body === 'string' ? body : JSON.stringify(body)
  const fetchMock = vi.fn().mockResolvedValue(new Response(text, { status }))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('requests', () => {
  it('sends the query the list page asked for, and skips unset filters', async () => {
    const fetchMock = respond(200, page([listItem()]))

    await getProducts({ sortBy: 'price', sortDir: 'asc', page: 3, pageSize: 25 })

    const url = new URL(fetchMock.mock.calls[0][0] as string)
    expect(url.origin + url.pathname).toBe('http://api.test/api/products')
    expect(Object.fromEntries(url.searchParams)).toEqual({ sortBy: 'price', sortDir: 'asc', page: '3', pageSize: '25' })
  })

  it('encodes a search term rather than splicing it into the URL', async () => {
    const fetchMock = respond(200, page([]))

    await getProducts({ search: 'a&b=c #1', category: 'Laptops', sortBy: 'crawled', sortDir: 'desc', page: 1, pageSize: 25 })

    const url = new URL(fetchMock.mock.calls[0][0] as string)
    expect(url.searchParams.get('search')).toBe('a&b=c #1')
    expect(url.searchParams.get('category')).toBe('Laptops')
  })

  it('posts the submitted URL as JSON', async () => {
    const fetchMock = respond(201, product())

    await createProduct('https://www.noon.com/egypt-en/x/N1/p/')

    const init = fetchMock.mock.calls[0][1] as RequestInit
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ url: 'https://www.noon.com/egypt-en/x/N1/p/' })
  })
})

describe('errors', () => {
  it('carries the API explanation on a 400', async () => {
    respond(400, { title: 'Invalid product URL', detail: 'Only https:// links are accepted.', status: 400 })

    const error = await createProduct('http://x').catch((e) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error.status).toBe(400)
    expect(error.detail).toBe('Only https:// links are accepted.')
  })

  it('carries the existing product id on a 409', async () => {
    respond(409, { title: 'Already tracked', detail: 'This product is already tracked.', status: 409, productId: 42 })

    const error = await createProduct('https://www.noon.com/egypt-en/x/N1/p/').catch((e) => e)

    expect(error.status).toBe(409)
    expect(error.productId).toBe(42)
  })

  it('still reports the status when an error body is not JSON', async () => {
    respond(502, '<html>Bad gateway</html>')

    const error = await startCheckNow(1).catch((e) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error.status).toBe(502)
    expect(error.detail).toBeUndefined()
  })

  it('reports status 0 when the server cannot be reached', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    const error = await getProduct(1).catch((e) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error.status).toBe(0)
  })
})

describe('the response contract', () => {
  it('rejects a body that is not the shape the UI was built against', async () => {
    const { lastCrawledAt: _dropped, ...missingField } = product()
    respond(200, missingField)

    await expect(getProduct(7)).rejects.toBeInstanceOf(ContractError)
  })

  it('rejects an unknown job status instead of rendering it as something else', async () => {
    respond(200, product({ crawl: crawl({ status: 'Exploded' as CrawlStatus['status'] }) }))

    await expect(getProduct(7)).rejects.toBeInstanceOf(ContractError)
  })

  it('rejects a 200 with an empty or non-JSON body', async () => {
    respond(200, '')
    await expect(getProduct(7)).rejects.toBeInstanceOf(ContractError)

    respond(200, 'not json')
    await expect(getProduct(7)).rejects.toBeInstanceOf(ContractError)
  })

  it('accepts a crawl in every status the backend can report', async () => {
    for (const status of ['Pending', 'Running', 'Completed', 'Failed'] as const) {
      respond(200, product({ crawl: crawl({ status }) }))
      await expect(getProduct(7)).resolves.toMatchObject({ crawl: { status } })
    }
  })
})
