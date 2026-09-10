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
    <form onSubmit={handleSubmit} className="bg-ink-900 px-6 py-5">
      <label htmlFor="product-url" className="block text-xs text-ink-300 lowercase">
        add an item
      </label>
      <div className="mt-2 flex items-end gap-4">
        <input
          id="product-url"
          type="url"
          required
          placeholder="https://www.noon.com/egypt-en/..."
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          className="flex-1 border-b border-ink-300/40 bg-transparent py-1 text-sm text-paper placeholder:text-ink-300/50 focus:border-flag-gold focus:outline-none"
        />
        <button
          type="submit"
          disabled={status === 'submitting'}
          className="bg-flag-gold px-5 py-2.5 text-sm font-bold text-ink-900 transition hover:brightness-110 disabled:opacity-40"
        >
          {status === 'submitting' ? 'adding…' : 'track it'}
        </button>
      </div>
      {message && (
        <p className={`mt-2 text-sm ${message.tone === 'error' ? 'text-flag-red' : 'text-flag-green'}`}>
          {message.text}
        </p>
      )}
    </form>
  )
}
