// Regenerates the demo-*.png files in this folder from a running local stack that has been
// loaded with the synthetic demo data (see the root README, "Getting started"). Not part of
// the app or its tests; playwright-core is deliberately not a project dependency:
//
//   npm i --no-save playwright-core        (run once, from the repository root)
//   node docs/screenshots/capture.mjs      (needs Google Chrome installed)
//
// Product ids are looked up by name from the API, so re-seeding (which changes ids) is fine.
import { chromium } from 'playwright-core'

const OUT = import.meta.dirname
const BASE = 'http://localhost:5173'
const API = process.env.API_URL ?? 'http://localhost:5176'

async function idOf(search) {
  const res = await fetch(`${API}/api/products?search=${encodeURIComponent(search)}&pageSize=1`)
  const body = await res.json()
  if (!body.items.length) throw new Error(`No demo product matching "${search}" - is the demo data loaded?`)
  return body.items[0].id
}


const browser = await chromium.launch({ channel: 'chrome', headless: true })

async function shot(name, url, { width = 1280, height = 900, mobile = false, scrollTo, fullPage = false, waitFor, before } = {}) {
  const context = await browser.newContext({
    viewport: { width, height },
    deviceScaleFactor: mobile ? 2 : 1,
    colorScheme: 'light',
  })
  const page = await context.newPage()
  await page.goto(url, { waitUntil: 'networkidle' })
  if (waitFor) await page.waitForSelector(waitFor, { timeout: 15000 })
  await page.waitForTimeout(600)
  if (before) { await before(page); await page.waitForTimeout(700) }
  if (scrollTo) {
    await page.getByText(scrollTo, { exact: false }).first().scrollIntoViewIfNeeded()
    await page.waitForTimeout(300)
  }
  await page.screenshot({ path: `${OUT}/${name}`, fullPage })
  console.log('saved', name)
  await context.close()
}

// The list, sorted by discount so the page shows discounted items first.
await shot('demo-product-list.png', `${BASE}/`, {
  waitFor: 'a[href^="/products/"]', height: 960,
  before: async (page) => { await page.getByRole('button', { name: 'discount' }).click() },
})

// A product whose "25% off" sale is flagged: the price quietly spiked ~40% for a week, then
// the "sale" landed at a price still above its old low. Scrolled so the last rows of the
// history (spike, sale) and the flag entry are in view together.
await shot('demo-fake-discount.png', `${BASE}/products/${await idOf('Aurora')}`, {
  waitFor: 'text=discount flags', height: 900,
  before: async (page) => {
    await page.evaluate(() => {
      const heading = [...document.querySelectorAll('p')].find((p) => p.textContent.trim() === 'discount flags')
      window.scrollTo(0, heading.getBoundingClientRect().top + window.scrollY - 640)
    })
  },
})

// A restock.
await shot('demo-restock.png', `${BASE}/products/${await idOf('Drift Mini')}`, {
  waitFor: 'text=restock history', height: 700,
  before: async (page) => {
    await page.evaluate(() => {
      const heading = [...document.querySelectorAll('p')].find((p) => p.textContent.trim() === 'restock history')
      window.scrollTo(0, heading.getBoundingClientRect().top + window.scrollY - 420)
    })
  },
})

// Phone width.
await shot('demo-product-list-mobile.png', `${BASE}/`, {
  width: 390, height: 844, mobile: true, waitFor: 'a[href^="/products/"]',
  before: async (page) => { await page.getByRole('button', { name: 'discount' }).click() },
})

await browser.close()
