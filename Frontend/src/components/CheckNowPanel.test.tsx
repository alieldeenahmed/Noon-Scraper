import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { checkResult } from '../test/factories'
import { stubApi } from '../test/http'
import CheckNowPanel, { POLL_GIVE_UP_MS, POLL_INTERVAL_MS } from './CheckNowPanel'

const START = 'POST /api/products/7/check-now'
const POLL = 'GET /api/products/7/check-now/5'

const accepted = { status: 202, body: { requestId: 5, status: 'Pending' } }

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
})

async function startCheck() {
  const user = userEvent.setup({ delay: null })
  render(<CheckNowPanel productId={7} />)
  await user.click(screen.getByRole('button', { name: 'check' }))
  return user
}

// Let one polling interval elapse, and the request it makes settle.
const tick = () => act(() => vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS))

describe('a check that completes', () => {
  it('walks Pending -> Running -> Completed and lists the other sellers cheapest first', async () => {
    const replies = [
      checkResult({ status: 'Pending' }),
      checkResult({ status: 'Running' }),
      checkResult({
        status: 'Completed',
        lowestPrice: 1800,
        lowestPriceMerchant: 'Seller A',
        offers: [
          { merchantName: 'Seller A', price: 1800, rating: 4.7 },
          { merchantName: 'Seller B', price: 1950, rating: null },
        ],
      }),
    ]
    const api = stubApi({ [START]: accepted, [POLL]: (call) => ({ body: replies[call - 1] }) })
    await startCheck()

    expect(screen.getByRole('button', { name: 'checking…' })).toBeDisabled()

    await tick()
    expect(screen.getByText(/queued/)).toBeInTheDocument()

    await tick()
    expect(screen.getByText(/running — reading the page/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'checking…' })).toBeDisabled()

    await tick()
    expect(screen.getByText('Seller A')).toBeInTheDocument()
    expect(screen.getByText(/★ 4\.7/)).toBeInTheDocument()
    expect(screen.getByText('Seller B')).toBeInTheDocument()
    // Only Seller B has no rating, so exactly one star shows.
    expect(screen.getAllByText(/★/)).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'check' })).toBeEnabled()

    // Finished: no further polling.
    await tick()
    await tick()
    expect(api.calls(POLL)).toBe(3)
  })

  it('says so when no other sellers were found', async () => {
    stubApi({ [START]: accepted, [POLL]: { body: checkResult({ status: 'Completed', offers: [] }) } })
    await startCheck()

    await tick()

    expect(screen.getByText(/no other sellers found/)).toBeInTheDocument()
  })

  it('treats a completed check with no result body as no sellers, not a crash', async () => {
    stubApi({ [START]: accepted, [POLL]: { body: checkResult({ status: 'Completed', offers: null }) } })
    await startCheck()

    await tick()

    expect(screen.getByText(/no other sellers found/)).toBeInTheDocument()
  })
})

describe('a check that fails', () => {
  it('shows the reason, the stage, and a link to the workflow run', async () => {
    stubApi({
      [START]: accepted,
      [POLL]: {
        body: checkResult({
          status: 'Failed',
          errorMessage: 'The page had no product data.',
          failureStage: 'scrape',
          runUrl: 'https://github.com/o/r/actions/runs/99',
        }),
      },
    })
    await startCheck()

    await tick()

    const alert = screen.getByRole('alert')
    expect(alert).toHaveTextContent('check failed: The page had no product data.')
    expect(alert).toHaveTextContent('stopped while reading the page')
    expect(screen.getByRole('link', { name: /view the workflow run/ })).toHaveAttribute(
      'href',
      'https://github.com/o/r/actions/runs/99',
    )
    expect(screen.getByRole('button', { name: 'check' })).toBeEnabled()
  })

  it('shows a timed-out check the same way (the server closes requests nothing picked up)', async () => {
    stubApi({
      [START]: accepted,
      [POLL]: {
        body: checkResult({
          status: 'Failed',
          errorMessage: 'Timed out: no worker picked this request up.',
          failureStage: 'timeout',
        }),
      },
    })
    await startCheck()

    await tick()

    expect(screen.getByRole('alert')).toHaveTextContent(/timed out/i)
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })

  it('still shows a failure that came with no explanation', async () => {
    stubApi({ [START]: accepted, [POLL]: { body: checkResult({ status: 'Failed' }) } })
    await startCheck()

    await tick()

    expect(screen.getByRole('alert')).toHaveTextContent(/no reason was recorded/)
  })
})

describe('when something goes wrong talking to the API', () => {
  it('says to wait when starting is rate limited', async () => {
    stubApi({ [START]: { status: 429 } })
    await startCheck()

    expect(await screen.findByRole('alert')).toHaveTextContent(/too many checks/)
    expect(screen.getByRole('button', { name: 'check' })).toBeEnabled()
  })

  it('says so when the workflow could not be started', async () => {
    stubApi({ [START]: { status: 502 } })
    await startCheck()

    expect(await screen.findByRole('alert')).toHaveTextContent(/couldn’t start the live check/)
  })

  it('stops polling and says so when the connection is lost mid-check', async () => {
    const api = stubApi({ [START]: accepted, [POLL]: (call) => (call === 1 ? { body: checkResult() } : new TypeError('offline')) })
    await startCheck()

    await tick()
    await tick()

    expect(screen.getByRole('alert')).toHaveTextContent(/lost connection/)
    await tick()
    expect(api.calls(POLL)).toBe(2)
    expect(screen.getByRole('button', { name: 'check' })).toBeEnabled()
  })

  it('gives up after a long time instead of polling forever', async () => {
    const api = stubApi({ [START]: accepted, [POLL]: { body: checkResult({ status: 'Running' }) } })
    await startCheck()

    await act(() => vi.advanceTimersByTimeAsync(POLL_GIVE_UP_MS + 2 * POLL_INTERVAL_MS))

    expect(screen.getByRole('alert')).toHaveTextContent(/taking much longer than expected/)
    const callsAtGiveUp = api.calls(POLL)
    await tick()
    expect(api.calls(POLL)).toBe(callsAtGiveUp)
  })
})

describe('leaving the page', () => {
  it('stops polling when the panel goes away', async () => {
    const api = stubApi({ [START]: accepted, [POLL]: { body: checkResult() } })
    const user = userEvent.setup({ delay: null })
    const { unmount } = render(<CheckNowPanel productId={7} />)
    await user.click(screen.getByRole('button', { name: 'check' }))
    await tick()
    expect(api.calls(POLL)).toBe(1)

    unmount()
    await tick()
    await tick()

    expect(api.calls(POLL)).toBe(1)
  })
})

describe('starting again', () => {
  it('clears the previous result and polls the new request', async () => {
    let started = 0
    stubApi({
      [START]: () => ({ status: 202, body: { requestId: 5, status: 'Pending' }, ...{ n: ++started } }),
      [POLL]: { body: checkResult({ status: 'Failed', errorMessage: 'boom', failureStage: 'scrape' }) },
    })
    const user = await startCheck()
    await tick()
    expect(screen.getByRole('alert')).toHaveTextContent('boom')

    await user.click(screen.getByRole('button', { name: 'check' }))

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(started).toBe(2)
  })
})
