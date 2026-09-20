import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { listItem, page } from '../test/factories'
import { stubApi } from '../test/http'
import ProductListPage from './ProductListPage'

const LIST = 'GET /api/products'
const STATS = 'GET /api/products/stats'

afterEach(() => vi.unstubAllGlobals())

const stats = { body: { total: 30, inStock: 25, onDiscount: 4 } }

function renderPage() {
  render(
    <MemoryRouter>
      <ProductListPage />
    </MemoryRouter>,
  )
  return userEvent.setup()
}

// The query the page most recently sent for the list.
function lastQuery(api: ReturnType<typeof stubApi>) {
  return Object.fromEntries(api.urls(LIST).at(-1)!.searchParams)
}

describe('the list', () => {
  it('shows each product with its price and status, and the totals', async () => {
    stubApi({
      [LIST]: {
        body: page([
          listItem({ id: 1, name: 'Alpha Phone', latestPrice: 5000 }),
          listItem({ id: 2, name: 'Beta Phone', latestPrice: 3000, latestDiscountPercent: 20 }),
          listItem({ id: 3, name: 'Gamma Phone', latestStock: false }),
        ]),
      },
      [STATS]: stats,
    })

    renderPage()

    expect(await screen.findByText('Alpha Phone')).toBeInTheDocument()
    const rows = screen.getAllByRole('link')
    expect(rows).toHaveLength(3)
    expect(within(rows[0]).getByText(/5,000/)).toBeInTheDocument()
    expect(within(rows[0]).getByText('steady')).toBeInTheDocument()
    expect(within(rows[1]).getByText('-20%')).toBeInTheDocument()
    expect(within(rows[2]).getByText('out of stock')).toBeInTheDocument()
    expect(rows[0]).toHaveAttribute('href', '/products/1')
    expect(await screen.findByText('30')).toBeInTheDocument()
  })

  it('falls back to the URL for a product with no name yet, and a dash for no crawl time', async () => {
    stubApi({
      [LIST]: {
        body: page([listItem({ name: null, url: 'https://www.noon.com/egypt-en/x/N1/p/', lastCrawledAt: null, latestPrice: null, category: null })]),
      },
      [STATS]: stats,
    })

    renderPage()

    expect(await screen.findByText('https://www.noon.com/egypt-en/x/N1/p/')).toBeInTheDocument()
    expect(screen.getByText('Uncategorized')).toBeInTheDocument()
  })

  it('says when nothing matches', async () => {
    stubApi({ [LIST]: { body: page([]) }, [STATS]: stats })

    renderPage()

    expect(await screen.findByText('No products match.')).toBeInTheDocument()
  })

  it('says when the API cannot be reached', async () => {
    stubApi({ [LIST]: new TypeError('offline'), [STATS]: new TypeError('offline') })

    renderPage()

    expect(await screen.findByText(/couldn’t reach the API/i)).toBeInTheDocument()
  })

  it('treats a response that is not the expected shape as an outage, not as data', async () => {
    stubApi({ [LIST]: { body: { items: 'nope' } }, [STATS]: stats })

    renderPage()

    expect(await screen.findByText(/couldn’t reach the API/i)).toBeInTheDocument()
  })
})

describe('sorting', () => {
  it('starts newest-crawled first', async () => {
    const api = stubApi({ [LIST]: { body: page([listItem()]) }, [STATS]: stats })

    renderPage()
    await screen.findByText('Item')

    expect(lastQuery(api)).toMatchObject({ sortBy: 'crawled', sortDir: 'desc', page: '1' })
  })

  it('sorts a new column descending first, then flips direction when it is clicked again', async () => {
    const api = stubApi({ [LIST]: { body: page([listItem()]) }, [STATS]: stats })
    const user = renderPage()
    await screen.findByText('Item')

    await user.click(screen.getByRole('button', { name: 'price' }))
    await waitFor(() => expect(lastQuery(api)).toMatchObject({ sortBy: 'price', sortDir: 'desc' }))

    await user.click(screen.getByRole('button', { name: /^price/ }))
    await waitFor(() => expect(lastQuery(api)).toMatchObject({ sortBy: 'price', sortDir: 'asc' }))
    expect(screen.getByRole('button', { name: /^price/ })).toHaveTextContent('↑')

    await user.click(screen.getByRole('button', { name: 'discount' }))
    await waitFor(() => expect(lastQuery(api)).toMatchObject({ sortBy: 'discount', sortDir: 'desc' }))
  })

  it('goes back to page 1 when the sort changes', async () => {
    const api = stubApi({
      [LIST]: (call) => ({ body: page([listItem()], { page: call === 1 ? 1 : 2, totalPages: 3, total: 60 }) }),
      [STATS]: stats,
    })
    const user = renderPage()
    await screen.findByText('Item')
    await user.click(screen.getByRole('button', { name: /next/ }))
    await waitFor(() => expect(lastQuery(api).page).toBe('2'))

    await user.click(screen.getByRole('button', { name: 'price' }))

    await waitFor(() => expect(lastQuery(api)).toMatchObject({ sortBy: 'price', page: '1' }))
  })
})

describe('filtering', () => {
  it('filters by category, resets to page 1, and asks for that category’s stats', async () => {
    const api = stubApi({ [LIST]: { body: page([listItem()]) }, [STATS]: stats })
    const user = renderPage()
    await screen.findByText('Item')

    await user.click(screen.getByRole('button', { name: 'Laptops' }))

    await waitFor(() => expect(lastQuery(api)).toMatchObject({ category: 'Laptops', page: '1' }))
    await waitFor(() => expect(api.urls(STATS).at(-1)!.searchParams.get('category')).toBe('Laptops'))

    await user.click(screen.getByRole('button', { name: 'All' }))
    await waitFor(() => expect(lastQuery(api)).not.toHaveProperty('category'))
  })

  it('waits for a pause in typing before searching, and trims the term', async () => {
    const api = stubApi({ [LIST]: { body: page([listItem()]) }, [STATS]: stats })
    const user = renderPage()
    await screen.findByText('Item')
    const before = api.calls(LIST)

    await user.type(screen.getByPlaceholderText('search items…'), '  iph')
    // Typing is fast relative to the debounce: still no new request.
    expect(api.calls(LIST)).toBe(before)

    await waitFor(() => expect(lastQuery(api)).toMatchObject({ search: 'iph' }))
    // One request for the finished word, not one per keystroke.
    expect(api.calls(LIST) - before).toBe(1)
  })

  it('does not send a search parameter for an empty or whitespace-only term', async () => {
    const api = stubApi({ [LIST]: { body: page([listItem()]) }, [STATS]: stats })
    const user = renderPage()
    await screen.findByText('Item')

    await user.type(screen.getByPlaceholderText('search items…'), '   ')
    await new Promise((resolve) => setTimeout(resolve, 400))

    expect(lastQuery(api)).not.toHaveProperty('search')
  })
})

describe('paging', () => {
  it('shows the range and moves between pages, disabling the ends', async () => {
    const api = stubApi({
      [LIST]: () => ({}),
      [STATS]: stats,
    })
    let current = 1
    api.fetchMock.mockImplementation((input: string | URL | Request) => {
      const url = new URL(String(input))
      if (url.pathname === '/api/products/stats') return Promise.resolve(new Response(JSON.stringify(stats.body)))
      current = Number(url.searchParams.get('page'))
      const items = current === 3 ? [listItem({ id: 51, name: 'Last one' })] : [listItem({ id: current, name: `Row ${current}` })]
      return Promise.resolve(new Response(JSON.stringify(page(items, { page: current, totalPages: 3, total: 51 }))))
    })
    const user = renderPage()

    await screen.findByText('Row 1')
    expect(screen.getByText(/1–1 of 51 · page 1 of 3/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /prev/ })).toBeDisabled()

    await user.click(screen.getByRole('button', { name: /next/ }))
    await screen.findByText('Row 2')
    expect(screen.getByRole('button', { name: /prev/ })).toBeEnabled()

    await user.click(screen.getByRole('button', { name: /next/ }))
    await screen.findByText('Last one')
    expect(screen.getByRole('button', { name: /next/ })).toBeDisabled()
  })

  it('hides the pager when everything fits on one page', async () => {
    stubApi({ [LIST]: { body: page([listItem()]) }, [STATS]: stats })

    renderPage()
    await screen.findByText('Item')

    expect(screen.queryByRole('navigation')).not.toBeInTheDocument()
  })
})

describe('overlapping requests', () => {
  // The user changes the filter while the previous request is still in flight. If
  // the slow first response arrived last and were applied, the list would show
  // results for a filter that is no longer selected.
  it('ignores a slow response that a newer query has superseded', async () => {
    let releaseSlow!: () => void
    const api = stubApi({ [LIST]: () => ({}), [STATS]: stats })
    api.fetchMock.mockImplementation((input: string | URL | Request) => {
      const url = new URL(String(input))
      if (url.pathname === '/api/products/stats') return Promise.resolve(new Response(JSON.stringify(stats.body)))
      if (url.searchParams.get('category') === 'Laptops') {
        return Promise.resolve(new Response(JSON.stringify(page([listItem({ name: 'A laptop' })]))))
      }
      return new Promise<Response>((resolve) => {
        releaseSlow = () => resolve(new Response(JSON.stringify(page([listItem({ name: 'Stale unfiltered row' })]))))
      })
    })
    const user = renderPage()
    await waitFor(() => expect(releaseSlow).toBeDefined())

    await user.click(screen.getByRole('button', { name: 'Laptops' }))
    expect(await screen.findByText('A laptop')).toBeInTheDocument()

    releaseSlow()
    await new Promise((resolve) => setTimeout(resolve, 50))

    expect(screen.getByText('A laptop')).toBeInTheDocument()
    expect(screen.queryByText('Stale unfiltered row')).not.toBeInTheDocument()
  })

  it('keeps showing the last good list while the next one loads', async () => {
    let release!: () => void
    const api = stubApi({ [LIST]: () => ({}), [STATS]: stats })
    api.fetchMock.mockImplementation((input: string | URL | Request) => {
      const url = new URL(String(input))
      if (url.pathname === '/api/products/stats') return Promise.resolve(new Response(JSON.stringify(stats.body)))
      if (url.searchParams.get('sortBy') === 'price') {
        return new Promise<Response>((resolve) => {
          release = () => resolve(new Response(JSON.stringify(page([listItem({ name: 'By price' })]))))
        })
      }
      return Promise.resolve(new Response(JSON.stringify(page([listItem({ name: 'By date' })]))))
    })
    const user = renderPage()
    await screen.findByText('By date')

    await user.click(screen.getByRole('button', { name: 'price' }))

    expect(screen.getByText('By date')).toBeInTheDocument()
    release()
    expect(await screen.findByText('By price')).toBeInTheDocument()
  })
})
