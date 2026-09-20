import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { ApiError, createProduct } from '../api/client'

// What to tell the user for each way a submission can fail. The API's own 400
// explanation (which says *what* was wrong with the link) is shown when it sent one.
function describeFailure(err: unknown): { text: string; existingProductId?: number } {
  if (err instanceof ApiError) {
    if (err.status === 409) {
      return { text: 'This product is already tracked.', existingProductId: err.productId }
    }
    if (err.status === 400) {
      return { text: err.detail ?? 'That doesn’t look like a noon.com product URL.' }
    }
    if (err.status === 429) {
      return { text: err.detail ?? 'Too many submissions — wait a minute and try again.' }
    }
    if (err.status === 0) {
      return { text: 'Couldn’t reach the server. Check your connection and try again.' }
    }
  }
  return { text: 'Something went wrong submitting that URL.' }
}

export default function AddProductForm({ onAdded }: { onAdded: () => void }) {
  const navigate = useNavigate()
  const [url, setUrl] = useState('')
  const [status, setStatus] = useState<'idle' | 'submitting'>('idle')
  const [message, setMessage] = useState<{ text: string; existingProductId?: number } | null>(null)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    // Enter in the field submits without going through the disabled button.
    if (status === 'submitting') return
    setStatus('submitting')
    setMessage(null)

    try {
      const product = await createProduct(url)
      setUrl('')
      onAdded()
      // Jump straight to the product's own page, which polls and fills in
      // live as soon as the crawl-product workflow finishes - no need to
      // wait on the list page for a bare, uncrawled row.
      navigate(`/products/${product.id}`)
    } catch (err) {
      setMessage(describeFailure(err))
    } finally {
      setStatus('idle')
    }
  }

  return (
    <form onSubmit={handleSubmit} className="bg-ink-900 px-6 py-5">
      <label htmlFor="product-url" className="block text-xs text-ink-300 lowercase">
        add an item
      </label>
      <div className="mt-2 flex flex-col gap-3 sm:flex-row sm:items-end sm:gap-4">
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
        <p role="alert" className="mt-2 text-sm text-flag-red">
          {message.text}
          {message.existingProductId !== undefined && (
            <>
              {' '}
              <Link to={`/products/${message.existingProductId}`} className="underline">
                View it
              </Link>
            </>
          )}
        </p>
      )}
    </form>
  )
}
