import { vi } from 'vitest'

// Stand in for the network: the real API client runs, so the tests cover how a
// component reacts to what actually comes back over HTTP (statuses, problem
// details, unparseable bodies) rather than to a mocked function.

export type Reply = { status?: number; body?: unknown } | Error

function toResponse(reply: Reply): Promise<Response> {
  if (reply instanceof Error) return Promise.reject(reply)
  const { status = 200, body } = reply
  const text = body === undefined ? '' : typeof body === 'string' ? body : JSON.stringify(body)
  return Promise.resolve(new Response(text, { status }))
}

// Route requests by "METHOD /path" (query string ignored) to a reply, or to a
// function of the call number for endpoints that change over time. Unrouted
// requests fail the test loudly.
export function stubApi(routes: Record<string, Reply | ((call: number) => Reply | Promise<Response>)>) {
  const calls: Record<string, number> = {}
  const fetchMock = vi.fn((input: string | URL | Request, init?: RequestInit) => {
    const url = new URL(String(input))
    const key = `${init?.method ?? 'GET'} ${url.pathname}`
    const route = routes[key]
    if (route === undefined) {
      return Promise.reject(new Error(`Unrouted request: ${key}`))
    }
    calls[key] = (calls[key] ?? 0) + 1
    const reply = typeof route === 'function' ? route(calls[key]) : route
    return reply instanceof Promise ? reply : toResponse(reply)
  })
  vi.stubGlobal('fetch', fetchMock)
  return {
    fetchMock,
    calls: (key: string) => calls[key] ?? 0,
    urls: (key: string) =>
      fetchMock.mock.calls
        .filter(([input, init]) => `${init?.method ?? 'GET'} ${new URL(String(input)).pathname}` === key)
        .map(([input]) => new URL(String(input))),
  }
}

export const never = () => new Promise<Response>(() => {})
