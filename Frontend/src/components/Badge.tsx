import type { ReactNode } from 'react'

type Dot = 'red' | 'brand'

const dotClasses: Record<Dot, string> = {
  red: 'bg-red-400',
  brand: 'bg-brand-400',
}

// Sits on dark card surfaces (see ProductCard/ProductDetailPage) - translucent
// white fill/border reads correctly there regardless of the page's own
// (yellow) background. A dot is the only way a badge stands out - reserved
// for states worth noticing.
export default function Badge({ dot, children }: { dot?: Dot; children: ReactNode }) {
  return (
    <span className="inline-flex items-center gap-1.5 rounded-md border border-white/10 bg-white/5 px-2.5 py-1 text-xs text-white/70">
      {dot && <span className={`h-1.5 w-1.5 rounded-full ${dotClasses[dot]}`} />}
      {children}
    </span>
  )
}
