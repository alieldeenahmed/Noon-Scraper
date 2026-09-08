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
    <form onSubmit={handleSubmit} className="rounded-lg border border-slate-200 bg-white p-4">
      <label htmlFor="product-url" className="block text-sm font-medium text-slate-700">
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
          className="flex-1 rounded-md border border-slate-300 px-3 py-2 text-sm focus:border-slate-500 focus:outline-none"
        />
        <button
          type="submit"
          disabled={status === 'submitting'}
          className="rounded-md bg-slate-900 px-4 py-2 text-sm font-medium text-white transition hover:bg-slate-700 disabled:opacity-50"
        >
          {status === 'submitting' ? 'Adding…' : 'Add'}
        </button>
      </div>
      {message && (
        <p className={`mt-2 text-sm ${message.tone === 'error' ? 'text-red-600' : 'text-emerald-600'}`}>
          {message.text}
        </p>
      )}
    </form>
  )
}
