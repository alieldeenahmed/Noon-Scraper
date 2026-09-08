import type { ReactNode } from 'react'

type Tone = 'neutral' | 'green' | 'red' | 'amber' | 'purple'

const toneClasses: Record<Tone, string> = {
  neutral: 'bg-slate-100 text-slate-700',
  green: 'bg-emerald-100 text-emerald-700',
  red: 'bg-red-100 text-red-700',
  amber: 'bg-amber-100 text-amber-800',
  purple: 'bg-purple-100 text-purple-700',
}

export default function Badge({ tone = 'neutral', children }: { tone?: Tone; children: ReactNode }) {
  return (
    <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ${toneClasses[tone]}`}>
      {children}
    </span>
  )
}
