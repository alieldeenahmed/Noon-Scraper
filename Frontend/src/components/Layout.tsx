import type { ReactNode } from 'react'
import { Link } from 'react-router'

export default function Layout({ children }: { children: ReactNode }) {
  return (
    <div className="min-h-screen bg-brand-500">
      <header className="sticky top-0 z-10 border-b border-ink-900/15 bg-brand-500/90 backdrop-blur">
        <div className="mx-auto flex max-w-6xl items-center justify-between px-6 py-5">
          <Link to="/" className="text-lg font-semibold tracking-tight text-ink-900">
            noon<span className="font-light">scraper</span>
          </Link>
          <nav className="flex items-center gap-6">
            <Link to="/" className="hidden text-sm font-medium text-ink-900/70 hover:text-ink-900 sm:block">
              Products
            </Link>
            <span className="hidden text-xs tracking-wide text-ink-900/50 uppercase md:block">
              Price tracking &amp; deal-quality checking
            </span>
          </nav>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-6 py-12">{children}</main>
    </div>
  )
}
