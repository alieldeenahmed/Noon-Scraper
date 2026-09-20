import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { snapshot } from '../test/factories'
import PriceHistoryLog from './PriceHistoryLog'

// Prices are formatted as EGP with two decimals; assert on the digits so the test
// doesn't depend on the locale's currency symbol spacing.
const egp = (amount: string) => new RegExp(amount.replace('.', '\\.'))

describe('with no history', () => {
  it('says there is no crawl data yet', () => {
    render(<PriceHistoryLog history={[]} />)

    expect(screen.getByText('No crawl data yet.')).toBeInTheDocument()
  })
})

describe('with one reading', () => {
  it('shows it as the first reading and promises a line when the price changes', () => {
    render(<PriceHistoryLog history={[snapshot(1999, '2026-09-20T10:00:00Z')]} />)

    expect(screen.getByText(egp('1,999.00'))).toBeInTheDocument()
    expect(screen.getByText(/first reading/)).toBeInTheDocument()
    expect(screen.getByText(/next time the price changes/)).toBeInTheDocument()
  })
})

describe('with several readings', () => {
  const history = [
    snapshot(2000, '2026-09-18T10:00:00Z'),
    snapshot(1800, '2026-09-19T10:00:00Z'),
    snapshot(1800, '2026-09-20T10:00:00Z'),
    snapshot(2100, '2026-09-21T10:00:00Z'),
  ]

  it('annotates each reading against the one before it', () => {
    render(<PriceHistoryLog history={history} />)

    expect(screen.getByText(/first reading/)).toBeInTheDocument()
    expect(screen.getByText(/↓ down from .*2,000\.00/)).toBeInTheDocument()
    expect(screen.getByText('→ steady')).toBeInTheDocument()
    expect(screen.getByText(/↑ up from .*1,800\.00/)).toBeInTheDocument()
  })

  it('keeps the readings in the order given (the API sends them oldest first)', () => {
    render(<PriceHistoryLog history={history} />)

    const rows = screen.getAllByText(/first reading|down from|steady|up from/)
    expect(rows.map((row) => row.textContent?.split(' ')[0])).toEqual(['←', '↓', '→', '↑'])
  })

  it('colours a fall green and a rise red, and a steady price neutral', () => {
    render(<PriceHistoryLog history={history} />)

    expect(screen.getByText(/↓ down/)).toHaveClass('text-flag-green')
    expect(screen.getByText(/↑ up/)).toHaveClass('text-flag-red')
    expect(screen.getByText('→ steady')).toHaveClass('text-ink-600')
  })

  it('does not show the "next change" hint once there is more than one reading', () => {
    render(<PriceHistoryLog history={history} />)

    expect(screen.queryByText(/next time the price changes/)).not.toBeInTheDocument()
  })
})

describe('edge cases', () => {
  it('treats a one-piastre change as a change, not as steady', () => {
    render(<PriceHistoryLog history={[snapshot(100, '2026-09-20T10:00:00Z'), snapshot(100.01, '2026-09-20T11:00:00Z')]} />)

    expect(screen.getByText(/↑ up from .*100\.00/)).toBeInTheDocument()
  })

  it('shows only the time for a second reading on the same day, and the date when the day changes', () => {
    render(
      <PriceHistoryLog
        history={[
          snapshot(100, '2026-09-20T09:00:00Z'),
          snapshot(110, '2026-09-20T09:30:00Z'),
          snapshot(120, '2026-09-21T09:00:00Z'),
        ]}
      />,
    )

    // The first and third rows carry a date ("20 Sept, 09:00"); the second doesn't.
    const labels = screen.getAllByText(/\d\d:\d\d/).map((el) => el.textContent ?? '')
    expect(labels).toHaveLength(3)
    expect(labels[0]).toMatch(/,/)
    expect(labels[1]).not.toMatch(/,/)
    expect(labels[2]).toMatch(/,/)
  })
})
