import { act, render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { crawl, flag, product, snapshot, uncrawled } from '../test/factories'
import { stubApi } from '../test/http'
import ProductDetailPage, { POLL_INTERVAL_MS, POLL_TIMEOUT_MS } from './ProductDetailPage'

const DETAIL = 'GET /api/products/7'
const HISTORY = 'GET /api/products/7/history'
const FLAGS = 'GET /api/products/7/discount-flags'
const RESTOCKS = 'GET /api/products/7/restocks'

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
})

// The three detail endpoints with sensible empty defaults.
function api(routes: Parameters<typeof stubApi>[0]) {
  return stubApi({
    [HISTORY]: { body: [] },
    [FLAGS]: { body: [] },
    [RESTOCKS]: { body: [] },
    ...routes,
  })
}

function renderPage() {
  render(
    <MemoryRouter initialEntries={['/products/7']}>
      <Routes>
        <Route path="/products/:id" element={<ProductDetailPage />} />
      </Routes>
    </MemoryRouter>,
  )
}

const tick = (ms = POLL_INTERVAL_MS) => act(() => vi.advanceTimersByTimeAsync(ms))

describe('a crawled product', () => {
  it('shows the name, price, last crawl, and its price history', async () => {
    api({
      [DETAIL]: { body: product({ name: 'Galaxy Phone', latestPrice: 1999 }) },
      [HISTORY]: { body: [snapshot(2100, '2026-09-19T10:00:00Z'), snapshot(1999, '2026-09-20T10:00:00Z')] },
    })

    renderPage()

    expect(await screen.findByRole('heading', { name: 'Galaxy Phone' })).toBeInTheDocument()
    expect(screen.getByText('1,999')).toBeInTheDocument()
    expect(screen.getByText(/last crawled/)).toBeInTheDocument()
    expect(await screen.findByText(/↓ down from .*2,100\.00/)).toBeInTheDocument()
  })

  it('does not poll a product that already has data', async () => {
    const stub = api({ [DETAIL]: { body: product() } })
    renderPage()
    await screen.findByRole('heading')

    await tick(POLL_INTERVAL_MS * 3)

    expect(stub.calls(DETAIL)).toBe(1)
  })

  it('lists discount flags with the price it was recently, and marks the product flagged', async () => {
    api({
      [DETAIL]: { body: product({ latestDiscountPercent: 40 }) },
      [FLAGS]: { body: [flag({ priorHighPrice: 2500, discountedPrice: 2000, discountPercent: 40, historicalLowPrice: 1800 })] },
    })

    renderPage()

    expect(await screen.findByText(/flagged as fake discount/)).toBeInTheDocument()
    expect(await screen.findByText(/advertised as -\s*40%/)).toBeInTheDocument()
    expect(screen.getByText(/lowest before that: .*1,800\.00/)).toBeInTheDocument()
  })

  it('shows a flag that has no historical low without the low-price clause', async () => {
    api({
      [DETAIL]: { body: product() },
      [FLAGS]: { body: [flag({ historicalLowPrice: null })] },
    })

    renderPage()

    expect(await screen.findByText(/advertised as/)).not.toHaveTextContent(/lowest before that/)
  })

  it('lists restock events', async () => {
    api({
      [DETAIL]: { body: product() },
      [RESTOCKS]: { body: [{ detectedAt: '2026-09-20T10:00:00Z' }] },
    })

    renderPage()

    expect(await screen.findByText(/back in stock/)).toBeInTheDocument()
  })

  it('says a product is out of stock', async () => {
    api({ [DETAIL]: { body: product({ latestStock: false }) } })

    renderPage()

    expect(await screen.findByText('out of stock')).toBeInTheDocument()
  })
})

describe('loading failures', () => {
  it('says the product was not found on a 404', async () => {
    api({ [DETAIL]: { status: 404 } })

    renderPage()

    expect(await screen.findByText('product not found.')).toBeInTheDocument()
  })

  it('does not call an outage "not found"', async () => {
    api({ [DETAIL]: { status: 500 } })

    renderPage()

    expect(await screen.findByText(/couldn’t load this product right now/)).toBeInTheDocument()
    expect(screen.queryByText('product not found.')).not.toBeInTheDocument()
  })

  it('does not call an unreadable response "not found" either', async () => {
    api({ [DETAIL]: { body: { id: 7 } } })

    renderPage()

    expect(await screen.findByText(/couldn’t load this product right now/)).toBeInTheDocument()
  })
})

describe('a freshly submitted product', () => {
  it('shows it as queued, then crawling, then fills in once the crawl finishes', async () => {
    const replies = [
      uncrawled({ crawl: crawl({ status: 'Pending' }) }),
      uncrawled({ crawl: crawl({ status: 'Running' }) }),
      product({ name: 'Fresh Phone', crawl: crawl({ status: 'Completed' }) }),
    ]
    const stub = api({
      [DETAIL]: (call) => ({ body: replies[Math.min(call, replies.length) - 1] }),
      [HISTORY]: (call) => ({ body: call === 1 ? [] : [snapshot(1999, '2026-09-20T10:00:00Z')] }),
    })

    renderPage()
    expect(await screen.findByText(/queued/)).toBeInTheDocument()
    expect(screen.getByText('not crawled yet')).toBeInTheDocument()

    await tick()
    expect(screen.getByText(/crawling this product now/)).toBeInTheDocument()

    await tick()
    expect(await screen.findByRole('heading', { name: 'Fresh Phone' })).toBeInTheDocument()
    expect(await screen.findByText(/first reading/)).toBeInTheDocument()

    // Done: polling stops.
    const detailCalls = stub.calls(DETAIL)
    await tick(POLL_INTERVAL_MS * 3)
    expect(stub.calls(DETAIL)).toBe(detailCalls)
  })

  it('shows why a crawl failed, stops polling, and says the daily crawl will retry', async () => {
    const stub = api({
      [DETAIL]: {
        body: uncrawled({
          crawl: crawl({
            status: 'Failed',
            failureStage: 'scrape',
            errorMessage: 'The page had no product data (it may have been blocked).',
            runUrl: 'https://github.com/o/r/actions/runs/1',
          }),
        }),
      },
    })

    renderPage()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('couldn’t crawl this product')
    expect(alert).toHaveTextContent('The page had no product data')
    expect(alert).toHaveTextContent('stopped while reading the page')
    expect(screen.getByRole('link', { name: /view the workflow run/ })).toHaveAttribute(
      'href',
      'https://github.com/o/r/actions/runs/1',
    )
    expect(screen.getByText(/daily crawl will try it again/)).toBeInTheDocument()
    expect(screen.queryByText(/crawling this product now/)).not.toBeInTheDocument()

    await tick(POLL_INTERVAL_MS * 3)
    expect(stub.calls(DETAIL)).toBe(1)
  })

  it('shows a failure that appears while polling', async () => {
    api({
      [DETAIL]: (call) => ({
        body: uncrawled({
          crawl:
            call === 1
              ? crawl({ status: 'Running' })
              : crawl({ status: 'Failed', failureStage: 'timeout', errorMessage: 'Timed out: no worker picked this request up.' }),
        }),
      }),
    })

    renderPage()
    await screen.findByText(/crawling this product now/)

    await tick()

    expect(await screen.findByRole('alert')).toHaveTextContent(/timed out/i)
  })

  it('handles a product with no crawl request at all as still waiting', async () => {
    api({ [DETAIL]: { body: uncrawled({ crawl: null }) } })

    renderPage()

    expect(await screen.findByText(/crawling this product now/)).toBeInTheDocument()
  })

  it('stops after five minutes and tells the user to come back', async () => {
    const stub = api({ [DETAIL]: { body: uncrawled({ crawl: crawl({ status: 'Running' }) }) } })
    renderPage()
    await screen.findByText(/crawling this product now/)

    await tick(POLL_TIMEOUT_MS + POLL_INTERVAL_MS * 2)

    expect(screen.getByText(/still not back yet/)).toBeInTheDocument()
    const calls = stub.calls(DETAIL)
    await tick(POLL_INTERVAL_MS * 3)
    expect(stub.calls(DETAIL)).toBe(calls)
  })

  it('says so when the connection drops while waiting, instead of spinning forever', async () => {
    api({ [DETAIL]: (call) => (call === 1 ? { body: uncrawled({ crawl: crawl({ status: 'Running' }) }) } : new TypeError('offline')) })
    renderPage()
    await screen.findByText(/crawling this product now/)

    await tick()

    expect(await screen.findByText(/lost connection/)).toBeInTheDocument()
  })
})
