let getToken = () => ''
let expireSession = () => {}
let getSessionVersion = () => 0
export function configureApi(token: () => string, expire: () => void, version: () => number = () => 0) {
  getToken = token; expireSession = expire; getSessionVersion = version
}
export class ApiError extends Error {
  readonly status: number
  constructor(message: string, status: number) { super(message); this.status = status }
}

export async function api<T>(path: string, options: RequestInit = {}, authenticated = true): Promise<T> {
  const headers = new Headers(options.headers)
  if (options.body) headers.set('Content-Type', 'application/json')
  const token = authenticated ? getToken() : ''
  const sessionVersion = getSessionVersion()
  if (token) headers.set('Authorization', `Bearer ${token}`)
  let response: Response
  try { response = await fetch(`/api${path}`, { ...options, headers, cache: 'no-store' }) }
  catch { throw new ApiError('Cannot reach the store. Check your connection and try again.', 0) }
  if (!response.ok) {
    if (response.status === 401 && token && token === getToken() && sessionVersion === getSessionVersion()) expireSession()
    const fallback: Record<number, string> = { 400: 'Check the form and try again.', 401: 'Please sign in with a verified account.',
      403: 'Your account does not have permission for this action.', 404: 'This item could not be found.',
      409: 'An account with this email already exists.', 412: 'This product changed since you opened it. Reload it before saving or deleting.',
      428: 'Reload this product before saving or deleting.', 429: 'Too many requests. Wait a minute and try again.' }
    let message = fallback[response.status] || 'The store could not complete this request. Please try again.'
    if (response.status < 500 && response.headers.get('content-type')?.includes('json')) {
      const body = await response.json()
      if (body.errors) message = Object.values(body.errors).flat().join(' ')
      else if (body.message) message = body.message
    }
    throw new ApiError(message, response.status)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}
export const json = (method: string, body: unknown): RequestInit => ({ method, body: JSON.stringify(body) })
