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
      className="inline-flex items-center gap-1.5 rounded-md border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
    >
      Notify me on Telegram
    </a>
  )
}
