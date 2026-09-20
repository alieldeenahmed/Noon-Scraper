import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { product } from '../test/factories'
import { never, stubApi } from '../test/http'
import AddProductForm from './AddProductForm'

const POST = 'POST /api/products'
const URL_TEXT = 'https://www.noon.com/egypt-en/x/N1/p/'

function renderForm(onAdded = vi.fn()) {
  render(
    <MemoryRouter>
      <Routes>
        <Route path="/" element={<AddProductForm onAdded={onAdded} />} />
        <Route path="/products/:id" element={<p>detail page</p>} />
      </Routes>
    </MemoryRouter>,
  )
  return { onAdded, user: userEvent.setup() }
}

async function submit(user: ReturnType<typeof userEvent.setup>, text = URL_TEXT) {
  await user.type(screen.getByLabelText(/add an item/i), text)
  await user.click(screen.getByRole('button', { name: /track it/i }))
}

afterEach(() => vi.unstubAllGlobals())

describe('a successful submission', () => {
  it('sends the URL, tells the list to reload, and opens the new product page', async () => {
    const api = stubApi({ [POST]: { status: 201, body: product({ id: 12 }) } })
    const { onAdded, user } = renderForm()

    await submit(user)

    expect(await screen.findByText('detail page')).toBeInTheDocument()
    expect(onAdded).toHaveBeenCalledOnce()
    const init = api.fetchMock.mock.calls[0][1] as RequestInit
    expect(JSON.parse(init.body as string)).toEqual({ url: URL_TEXT })
  })
})

describe('while submitting', () => {
  it('disables the button and ignores a second submit (Enter in the field)', async () => {
    const api = stubApi({ [POST]: never })
    const { user } = renderForm()

    await submit(user)
    expect(screen.getByRole('button', { name: /adding/i })).toBeDisabled()

    await user.type(screen.getByLabelText(/add an item/i), '{Enter}')
    expect(api.calls(POST)).toBe(1)
  })
})

describe('failures', () => {
  it('shows the API reason for a rejected link', async () => {
    stubApi({ [POST]: { status: 400, body: { detail: 'Only https:// links are accepted.' } } })
    const { user } = renderForm()

    await submit(user)

    expect(await screen.findByRole('alert')).toHaveTextContent('Only https:// links are accepted.')
  })

  it('falls back to a generic message when a 400 has no detail', async () => {
    stubApi({ [POST]: { status: 400 } })
    const { user } = renderForm()

    await submit(user)

    expect(await screen.findByRole('alert')).toHaveTextContent(/look like a noon\.com product URL/i)
  })

  it('links to the product that is already tracked', async () => {
    stubApi({ [POST]: { status: 409, body: { title: 'Already tracked', productId: 42 } } })
    const { user } = renderForm()

    await submit(user)

    expect(await screen.findByRole('alert')).toHaveTextContent(/already tracked/i)
    expect(screen.getByRole('link', { name: /view it/i })).toHaveAttribute('href', '/products/42')
  })

  it('says to wait when rate limited', async () => {
    stubApi({ [POST]: { status: 429 } })
    const { user } = renderForm()

    await submit(user)

    expect(await screen.findByRole('alert')).toHaveTextContent(/wait a minute/i)
  })

  it('says so when the server cannot be reached', async () => {
    stubApi({ [POST]: new TypeError('Failed to fetch') })
    const { user } = renderForm()

    await submit(user)

    expect(await screen.findByRole('alert')).toHaveTextContent(/reach the server/i)
  })

  it('does not treat an unreadable success body as success', async () => {
    stubApi({ [POST]: { status: 201, body: { id: 'not-a-number' } } })
    const { user } = renderForm()

    await submit(user)

    expect(await screen.findByRole('alert')).toHaveTextContent(/something went wrong/i)
    expect(screen.queryByText('detail page')).not.toBeInTheDocument()
  })

  it('re-enables the form after a failure so the user can correct the link', async () => {
    stubApi({ [POST]: (call) => (call === 1 ? { status: 400 } : { status: 201, body: product({ id: 3 }) }) })
    const { user } = renderForm()

    await submit(user, 'https://www.noon.com/bad')
    await screen.findByRole('alert')
    expect(screen.getByRole('button', { name: /track it/i })).toBeEnabled()

    await user.clear(screen.getByLabelText(/add an item/i))
    await user.type(screen.getByLabelText(/add an item/i), URL_TEXT)
    await user.click(screen.getByRole('button', { name: /track it/i }))

    await waitFor(() => expect(screen.getByText('detail page')).toBeInTheDocument())
  })

  it('clears the previous error when submitting again', async () => {
    stubApi({ [POST]: (call) => (call === 1 ? { status: 429 } : never()) })
    const { user } = renderForm()

    await submit(user)
    await screen.findByRole('alert')
    await user.click(screen.getByRole('button', { name: /track it/i }))

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})
