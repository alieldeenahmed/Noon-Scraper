import { useState } from 'react'
import { ApiError, createProduct } from '../api/client'

export default function AddProductForm({ onAdded }: { onAdded: () => void }) {
  const [url, setUrl] = useState('')
  const [status, setStatus] = useState<'idle' | 'submitting'>('idle')
  const [message, setMessage] = useState<{ tone: 'error' | 'success'; text: string } | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    setStatus('submitting')
    setMessage(null)

    try {
      await createProduct(url)
      setMessage({ tone: 'success', text: 'Added — the next crawl will fill in its details.' })
      setUrl('')
      onAdded()
    } catch (err) {
      const text =
        err instanceof ApiError && err.status === 409
          ? 'This product is already tracked.'
          : err instanceof ApiError && err.status === 400
            ? 'That doesn’t look like a noon.com product URL.'
            : 'Something went wrong submitting that URL.'
      setMessage({ tone: 'error', text })
    } finally {
      setStatus('idle')
    }
  }

  return (
    <form onSubmit={handleSubmit} className="rounded-lg border border-brand-500/20 bg-ink-800 p-4">
      <label htmlFor="product-url" className="block text-xs tracking-wide text-white/50 uppercase">
        Track a new product
      </label>
      <div className="mt-2 flex gap-2">
        <input
          id="product-url"
          type="url"
          required
          placeholder="https://www.noon.com/egypt-en/..."
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          className="flex-1 rounded-md border border-white/10 bg-white/5 px-3 py-2 text-sm text-white placeholder:text-white/30 focus:border-brand-400/60 focus:outline-none"
        />
        <button
          type="submit"
          disabled={status === 'submitting'}
          className="rounded-md bg-brand-400 px-4 py-2 text-sm font-medium text-ink-900 transition hover:bg-brand-300 disabled:opacity-40"
        >
          {status === 'submitting' ? 'Adding…' : 'Add'}
        </button>
      </div>
      {message && (
        <p className={`mt-2 text-sm ${message.tone === 'error' ? 'text-red-400' : 'text-brand-300'}`}>
          {message.text}
        </p>
      )}
    </form>
  )
}
