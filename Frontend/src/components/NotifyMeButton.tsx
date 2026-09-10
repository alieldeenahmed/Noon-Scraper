const BOT_USERNAME = import.meta.env.VITE_TELEGRAM_BOT_USERNAME as string

// Subscribing has to happen through Telegram itself, not a form here - a bot
// can't message someone until they've messaged it first, and this deep link
// is what turns a click into a "/start <productId>" message with the user's
// chat id already attached. See Backend/docs/telegram-notifications.md.
export default function NotifyMeButton({ productId }: { productId: number }) {
  if (!BOT_USERNAME) {
    return null
  }

  return (
    <a
      href={`https://t.me/${BOT_USERNAME}?start=${productId}`}
      target="_blank"
      rel="noreferrer"
      className="flex w-full items-center justify-center bg-ink-900 px-3 py-3 text-sm font-bold text-paper transition hover:bg-ink-900/85"
    >
      notify me on Telegram
    </a>
  )
}
