import type { ReactNode } from 'react'
import { Link } from 'react-router'

export default function Layout({ children }: { children: ReactNode }) {
  return (
    <div className="min-h-screen bg-paper font-mono text-ink-900">
      <header className="border-b border-ink-900 px-6 py-5">
        <div className="mx-auto flex max-w-6xl items-center justify-between">
          <Link to="/" className="flex items-center gap-1.5 text-xl font-bold tracking-tight">
            noon <span className="text-flag-red">•</span> scraper
          </Link>
          <nav className="flex items-center gap-6">
            <Link to="/" className="border-b-2 border-ink-900 pb-0.5 text-sm font-bold">
              Products
            </Link>
            <span className="hidden text-xs text-ink-600 lowercase sm:block">
              price tracking &amp; deal-quality checking
            </span>
          </nav>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-6 py-10">{children}</main>
      <footer className="mx-auto max-w-6xl border-t border-dashed border-ink-300 px-6 py-8 text-xs text-ink-600">
        <Barcode />
        <p className="mt-2 lowercase">noonscraper · itemized price surveillance · updated continuously</p>
      </footer>
    </div>
  )
}

function Barcode() {
  // Purely decorative - a fixed pseudo-random sequence of bar widths, not
  // encoding anything.
  const widths = [2, 1, 3, 1, 1, 2, 4, 1, 2, 1, 1, 3, 2, 1, 4, 1, 1, 2, 3, 1, 2, 1, 1, 4, 2, 1, 3, 1, 2, 1]
  return (
    <svg width="200" height="32" viewBox="0 0 200 32" role="presentation" aria-hidden="true">
      {widths.reduce<{ x: number; els: ReactNode[] }>(
        (acc, w, i) => {
          acc.els.push(<rect key={i} x={acc.x} y={0} width={w} height={32} fill="#17160f" />)
          acc.x += w + 1.5
          return acc
        },
        { x: 0, els: [] },
      ).els}
    </svg>
  )
}
