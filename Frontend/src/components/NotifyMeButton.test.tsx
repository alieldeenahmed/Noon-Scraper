import { render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

afterEach(() => {
  vi.unstubAllEnvs()
  vi.resetModules()
})

// The bot name is read once when the module loads, so each case loads it fresh.
async function renderButton(botUsername: string, productId = 7) {
  vi.stubEnv('VITE_TELEGRAM_BOT_USERNAME', botUsername)
  vi.resetModules()
  const { default: NotifyMeButton } = await import('./NotifyMeButton')
  return render(<NotifyMeButton productId={productId} />)
}

describe('NotifyMeButton', () => {
  it('links to the bot with the product id as the /start payload', async () => {
    await renderButton('noon_test_bot', 42)

    const link = screen.getByRole('link', { name: /notify me on telegram/i })
    expect(link).toHaveAttribute('href', 'https://t.me/noon_test_bot?start=42')
    expect(link).toHaveAttribute('target', '_blank')
    expect(link).toHaveAttribute('rel', expect.stringContaining('noreferrer'))
  })

  it('renders nothing when no bot is configured, rather than a link to nowhere', async () => {
    const { container } = await renderButton('')

    expect(container).toBeEmptyDOMElement()
  })
})
