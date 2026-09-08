import type { ReactNode } from 'react'
import { Link } from 'react-router'

export default function Layout({ children }: { children: ReactNode }) {
  return (
    <div className="min-h-screen bg-slate-50 text-slate-900">
      <header className="border-b border-slate-200 bg-white">
        <div className="mx-auto flex max-w-5xl items-center justify-between px-4 py-4">
          <Link to="/" className="text-lg font-semibold tracking-tight">
            Noon Scraper
          </Link>
          <p className="text-sm text-slate-500">Price tracking &amp; deal-quality checking for noon.com</p>
        </div>
      </header>
      <main className="mx-auto max-w-5xl px-4 py-8">{children}</main>
    </div>
  )
}
