import { expect, test, type Page } from '@playwright/test'

const product = { id: '11111111-1111-4111-8111-111111111111', name: 'Test Game', description: 'A browser test product.', price: 249.95, stock: 5, imageUrl: null, isActive: true, version: '33333333-3333-4333-8333-333333333333' }
const user = { id: '22222222-2222-4222-8222-222222222222', role: 'admin' }

async function catalog(page: Page) {
  await page.route('**/api/products?*', route => route.fulfill({ json: product.name.toLowerCase().includes((new URL(route.request().url()).searchParams.get('search') || '').toLowerCase()) ? [product] : [] }))
  await page.route(`**/api/products/${product.id}`, route => route.fulfill({ json: product }))
}
async function login(page: Page, role = 'admin') {
  await page.route('**/api/cart', route => route.fulfill({ json: { items: [], total: 0, totalQuantity: 0 } }))
  await page.route('**/api/auth/login', route => route.fulfill({ json: { accessToken: 'browser-test-token', tokenType: 'Bearer', expiresAtUtc: new Date(Date.now() + 900000).toISOString() } }))
  await page.route('**/api/auth/me', route => route.fulfill({ json: { ...user, role } }))
  await catalog(page)
  await page.goto('/login')
  await page.getByLabel('Email address', { exact: true }).fill('admin@example.com')
  await page.getByLabel('Password', { exact: true }).fill('Browser test password!')
  await page.getByRole('button', { name: 'Sign in →' }).click()
}

test('catalog loads, searches, and opens detail on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await catalog(page); await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Test Game' })).toBeVisible()
  await page.getByRole('searchbox').fill('nothing')
  await expect(page.getByText('Nothing here just yet.')).toBeVisible()
  await page.getByRole('searchbox').fill('Test')
  await page.getByRole('link', { name: /Test Game/ }).click()
  await expect(page.getByRole('heading', { name: 'Test Game' })).toBeVisible()
  await expect(page.getByText('A browser test product.')).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})
test('API failure is explicit and retry recovers', async ({ page }) => {
  await page.route('**/api/products?*', route => route.fulfill({ status: 503, body: 'unavailable' }))
  await page.goto('/'); await expect(page.getByRole('alert')).toContainText('could not complete')
  await page.route('**/api/products?*', route => route.fulfill({ json: [product] }))
  await page.getByRole('button', { name: 'Try again' }).click()
  await expect(page.getByRole('heading', { name: 'Test Game' })).toBeVisible()
})
test('staff access requires a session and tokens never enter browser storage', async ({ page }) => {
  await page.goto('/staff/products'); await expect(page).toHaveURL(/\/login(?:\?|$)/)
  await login(page)
  await expect(page.getByRole('heading', { name: 'Inventory.' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Team', exact: true })).toBeVisible()
  const stored = await page.evaluate(() => ({ local: { ...localStorage }, session: { ...sessionStorage } }))
  expect(JSON.stringify(stored)).not.toContain('browser-test-token')
  await page.reload(); await expect(page).toHaveURL(/\/login(?:\?|$)/)
})
test('employee can manage products but cannot open team tools', async ({ page }) => {
  await login(page, 'employee'); await expect(page).toHaveURL(/\/staff\/products$/)
  await expect(page.getByRole('link', { name: 'Team', exact: true })).toHaveCount(0)
  await page.goto('/staff/team'); await expect(page).toHaveURL(/\/login(?:\?|$)/)
})
test('inventory editor sends product values and asks before delete', async ({ page }) => {
  let created: any
  await page.route('**/api/products', async route => {
    created = route.request().postDataJSON(); await route.fulfill({ status: 201, json: { ...product, ...created } })
  })
  await login(page); await page.getByRole('button', { name: '+ Add product' }).click()
  await page.getByLabel('Name', { exact: true }).fill('New Game')
  await page.getByLabel('Price (DKK)').fill('199.95')
  await page.getByLabel('Stock', { exact: true }).fill('4')
  await page.getByRole('button', { name: 'Save product' }).click()
  await expect(page.getByRole('status')).toContainText('Product saved')
  expect(created).toMatchObject({ name: 'New Game', price: 199.95, stock: 4, imageUrl: null, isActive: true })
  await page.getByRole('button', { name: 'Delete Test Game', exact: true }).click()
  await expect(page.getByRole('alertdialog')).toContainText('Delete Test Game?')
  await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  await expect(page.getByRole('alertdialog')).toHaveCount(0)
})
test('signup, verification and password reset submit correct bodies', async ({ page }) => {
  const bodies: Record<string, any> = {}
  await page.route('**/api/auth/*', async route => {
    bodies[new URL(route.request().url()).pathname] = route.request().postDataJSON()
    await route.fulfill({ json: { message: 'Request processed.' } })
  })
  await page.goto('/signup')
  await page.getByLabel('Email address', { exact: true }).fill('test@example.com')
  await page.getByLabel('Password', { exact: true }).fill('Browser test password!')
  await page.getByLabel('Confirm password', { exact: true }).fill('Browser test password!')
  await page.getByRole('button', { name: 'Create account →' }).click()
  await expect(page.getByRole('status')).toContainText('Request processed')
  expect(bodies['/api/auth/register'].password).toBe('Browser test password!')
  await page.goto('/verify')
  await page.getByLabel('Email address', { exact: true }).fill('test@example.com')
  await page.getByLabel('Email code', { exact: true }).fill('a'.repeat(64))
  await page.getByRole('button', { name: 'Verify email →' }).click()
  await expect(page.getByRole('status')).toContainText('Request processed')
  expect(bodies['/api/auth/verify-email'].code).toBe('A'.repeat(64))
  await page.goto('/reset-password')
  await page.getByLabel('Email address', { exact: true }).fill('test@example.com')
  await page.getByLabel('Email code', { exact: true }).fill('B'.repeat(64))
  await page.getByLabel('New password', { exact: true }).fill('Replacement password!')
  await page.getByLabel('Confirm password', { exact: true }).fill('Replacement password!')
  await page.getByRole('button', { name: 'Reset password →' }).click()
  await expect(page.getByRole('status')).toContainText('Request processed')
  expect(bodies['/api/auth/reset-password'].newPassword).toBe('Replacement password!')
})
test('unknown pages have a working recovery link', async ({ page }) => {
  await page.goto('/does-not-exist'); await expect(page.getByRole('heading', { name: 'Page not found.' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Back to the store' })).toHaveAttribute('href', '/')
})

test('admin can create staff and change a role', async ({ page }) => {
  let staff: any, assigned: any
  await page.route('**/api/auth/users/staff', async route => { staff = route.request().postDataJSON(); await route.fulfill({ status: 201, json: { id: product.id, message: 'Staff account created.' } }) })
  await page.route('**/api/auth/users/*/role', async route => { assigned = route.request().postDataJSON(); await route.fulfill({ status: 204 }) })
  await login(page)
  await page.getByRole('link', { name: 'Team', exact: true }).click()
  await page.getByLabel('Email address', { exact: true }).fill('employee@example.com')
  await page.locator('input[type=password]').fill('Initial staff password!')
  await page.getByRole('button', { name: 'Create employee', exact: true }).click()
  await expect(page.getByRole('status')).toContainText(product.id)
  expect(staff.email).toBe('employee@example.com')
  await page.getByLabel('Role', { exact: true }).selectOption('admin')
  await page.getByRole('button', { name: 'Update role' }).click()
  await expect(page.getByRole('status')).toContainText('Role updated')
  expect(assigned).toEqual({ role: 'admin' })
})

test('revoked session returns protected screens to login', async ({ page }) => {
  await login(page)
  await expect(page.getByRole('heading', { name: 'Inventory.' })).toBeVisible()
  await page.route('**/api/products?*', route => route.fulfill({ status: 401, json: { message: 'Invalid session.' } }))
  await page.getByRole('button', { name: '+ Add product' }).click()
  await page.getByLabel('Name', { exact: true }).fill('Sample')
  await page.route('**/api/products', route => route.fulfill({ status: 401 }))
  await page.getByRole('button', { name: 'Save product' }).click()
  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByRole('status')).toContainText('session is no longer valid')
})

test('a delayed profile response cannot restore a logged-out user', async ({ page }) => {
  await login(page); await expect(page).toHaveURL(/\/staff\/products$/)
  let release!: () => void
  const gate = new Promise<void>(resolve => release = resolve)
  await page.route('**/api/auth/me', async route => { await gate; await route.fulfill({ json: user }) })
  const requested = page.waitForRequest('**/api/auth/me')
  await page.evaluate(async () => {
    const { useAuth } = await import('/src/stores/auth.ts')
    const auth = useAuth()
    ;(window as any).pendingProfile = auth.refresh().catch(() => {})
    auth.clear()
  })
  await requested; release()
  await page.evaluate(() => (window as any).pendingProfile)
  expect(await page.evaluate(async () => {
    const { useAuth } = await import('/src/stores/auth.ts')
    return { token: useAuth().token, user: useAuth().user }
  })).toEqual({ token: '', user: null })
})

test('an old request cannot expire a replacement session', async ({ page }) => {
  await login(page); await expect(page).toHaveURL(/\/staff\/products$/)
  let release!: () => void
  const gate = new Promise<void>(resolve => release = resolve)
  await page.route('**/api/auth/me', async route => { await gate; await route.fulfill({ status: 401 }) })
  const requested = page.waitForRequest('**/api/auth/me')
  await page.evaluate(async () => {
    const { useAuth } = await import('/src/stores/auth.ts')
    const auth = useAuth()
    ;(window as any).pendingOld = auth.refresh().catch(() => {})
    auth.clear(); auth.token = 'replacement-token'; auth.user = { id: 'replacement-user', role: 'admin' }
  })
  await requested; release(); await page.evaluate(() => (window as any).pendingOld)
  expect(await page.evaluate(async () => { const { useAuth } = await import('/src/stores/auth.ts'); return useAuth().token })).toBe('replacement-token')
})

test('a delayed login cannot sign in after it was cancelled', async ({ page }) => {
  await catalog(page); await page.goto('/')
  let release!: () => void
  const gate = new Promise<void>(resolve => release = resolve)
  await page.route('**/api/auth/login', async route => { await gate; await route.fulfill({ json: { accessToken: 'cancelled-token', expiresAtUtc: new Date(Date.now() + 900000).toISOString() } }) })
  const requested = page.waitForRequest('**/api/auth/login')
  await page.evaluate(async () => {
    const { useAuth } = await import('/src/stores/auth.ts')
    ;(window as any).pendingLogin = useAuth().login('test@example.com', 'Test password!').catch(() => {})
    useAuth().clear()
  })
  await requested; release(); await page.evaluate(() => (window as any).pendingLogin)
  expect(await page.evaluate(async () => { const { useAuth } = await import('/src/stores/auth.ts'); return useAuth().token })).toBe('')
})

test('late product responses cannot replace the current product', async ({ page }) => {
  let release!: () => void
  const gate = new Promise<void>(resolve => release = resolve)
  const otherId = '44444444-4444-4444-8444-444444444444'
  await page.route(`**/api/products/${product.id}`, async route => { await gate; await route.fulfill({ json: product }) })
  await page.route(`**/api/products/${otherId}`, route => route.fulfill({ json: { ...product, id: otherId, name: 'Current Product' } }))
  const requested = page.waitForRequest(`**/api/products/${product.id}`)
  await page.goto(`/products/${product.id}`); await requested
  await page.evaluate(async id => {
    const router = (document.querySelector('#app') as any).__vue_app__.config.globalProperties.$router
    await router.push(`/products/${id}`)
  }, otherId)
  await expect(page.getByRole('heading', { name: 'Current Product', exact: true })).toBeVisible()
  const oldResponse = page.waitForResponse(`**/api/products/${product.id}`)
  release(); await (await oldResponse).finished()
  await expect(page.getByRole('heading', { name: 'Current Product', exact: true })).toBeVisible()
  await expect(page).toHaveURL(new RegExp(otherId + '$'))
})

test('temporary profile failures preserve the session and offer navigation retry', async ({ page }) => {
  await login(page); await expect(page).toHaveURL(/\/staff\/products$/)
  await page.route('**/api/auth/me', route => route.fulfill({ status: 503 }))
  await page.getByRole('link', { name: 'Team', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('could not complete')
  await expect(page).toHaveURL(/\/staff\/products$/)
  await expect(page.getByRole('button', { name: 'Sign out', exact: true })).toBeVisible()
  await page.route('**/api/auth/me', route => route.fulfill({ json: user }))
  await page.getByRole('button', { name: 'Retry navigation' }).click()
  await expect(page).toHaveURL(/\/staff\/team$/)
  await expect(page.getByRole('heading', { name: 'The team.' })).toBeVisible()
})

test('inventory conflicts preserve edits and reload uses the latest version', async ({ page }) => {
  await login(page); await expect(page).toHaveURL(/\/staff\/products$/)
  const latest = { ...product, stock: 20, version: '55555555-5555-4555-8555-555555555555' }
  await page.route(`**/api/products/${product.id}`, async route => {
    if (route.request().method() === 'GET') return route.fulfill({ json: latest })
    const version = route.request().headers()['if-match']
    if (version !== `"${latest.version}"`) return route.fulfill({ status: 412 })
    await route.fulfill({ json: latest })
  })
  await page.getByRole('button', { name: 'Edit Test Game', exact: true }).click()
  await page.getByLabel('Stock', { exact: true }).fill('3')
  await page.getByRole('button', { name: 'Save product' }).click()
  await expect(page.getByRole('alert')).toContainText('product changed')
  await expect(page.getByLabel('Stock', { exact: true })).toHaveValue('3')
  await page.getByRole('button', { name: 'Reload product and discard my edits' }).click()
  await expect(page.getByLabel('Stock', { exact: true })).toHaveValue('20')
  await page.getByRole('button', { name: 'Save product' }).click()
  await expect(page.getByRole('status')).toContainText('Product saved')
})

test('catalog sends filtering and sorting to the API and retains them on page changes', async ({ page }) => {
  await page.route('**/api/products?*', route => route.fulfill({ json: Array.from({length:12}, (_, i) => ({ ...product, id: `${i}`, name: `Filtered Product ${i}` })) }))
  await page.goto('/')
  await page.getByRole('searchbox').fill('console')
  await page.waitForResponse(response => new URL(response.url()).searchParams.get('search') === 'console')
  await page.getByRole('combobox').selectOption('low')
  await page.waitForResponse(response => new URL(response.url()).searchParams.get('sort') === 'low')
  const next = page.waitForRequest(request => new URL(request.url()).searchParams.get('page') === '2')
  await page.getByRole('button', { name: 'Next →', exact: true }).click()
  const query = new URL((await next).url()).searchParams
  expect(query.get('search')).toBe('console'); expect(query.get('sort')).toBe('low')
})

test('customers can add, update and remove cart items with server totals', async ({ page }) => {
  await login(page, 'default'); await expect(page).toHaveURL(/\/$/)
  let quantity = 0
  await page.route('**/api/cart', async route => {
    if (route.request().method() === 'DELETE') { quantity = 0; return route.fulfill({ status: 204 }) }
    await route.fulfill({ json: { items: quantity ? [{ productId: product.id, name: product.name, price: product.price, stock: product.stock, imageUrl: null, isActive: true, quantity, lineTotal: quantity * product.price }] : [], total: quantity * product.price, totalQuantity: quantity } })
  })
  await page.route('**/api/cart/items/*', async route => {
    if (route.request().method() === 'DELETE') quantity = 0
    else quantity = route.request().postDataJSON().quantity
    await route.fulfill({ status: 204 })
  })
  await page.getByRole('link', { name: /Test Game/ }).click()
  await page.getByLabel('Quantity', { exact: true }).fill('2')
  await page.getByRole('button', { name: 'Add to cart', exact: true }).click()
  await expect(page.getByRole('status')).toContainText('Added to your cart')
  expect(quantity).toBe(2)
  await page.getByRole('link', { name: 'View cart →' }).click()
  await expect(page.getByRole('heading', { name: 'Your cart.' })).toBeVisible()
  await expect(page.getByLabel(/Quantity.*Test Game/)).toHaveValue('2')
  await page.getByLabel(/Quantity.*Test Game/).fill('3')
  await page.getByRole('button', { name: 'Update Test Game', exact: true }).click()
  await expect(page.getByRole('link', { name: 'Cart (3)', exact: true })).toBeVisible()
  expect(quantity).toBe(3)
  await page.getByRole('button', { name: 'Remove Test Game', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Your cart is waiting.' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Cart (0)', exact: true })).toBeVisible()
})

test('guests must sign in to use the cart', async ({ page }) => {
  await catalog(page); await page.goto(`/products/${product.id}`)
  await page.getByRole('button', { name: 'Sign in to add to cart', exact: true }).click()
  await expect(page).toHaveURL(/\/login\?redirect=/)
  await page.goto('/cart'); await expect(page).toHaveURL(/\/login\?redirect=\/cart/)
})

test('unavailable cart items stay removable and emptying the cart requires confirmation', async ({ page }) => {
  await login(page); await expect(page).toHaveURL(/\/staff\/products$/)
  await page.route('**/api/cart', route => route.fulfill({ json: { items: [{ productId: product.id, name: product.name, price: 10, stock: 0, imageUrl: null, isActive: false, quantity: 2, lineTotal: 20 }], total: 20, totalQuantity: 2 } }))
  await page.getByRole('link', { name: /^Cart/ }).click()
  await page.setViewportSize({ width: 390, height: 844 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await expect(page.getByText('No longer available. Please remove this item.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Update Test Game', exact: true })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Remove Test Game', exact: true })).toBeEnabled()
  await page.getByRole('button', { name: 'Empty cart', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Yes, empty cart', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Yes, empty cart', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: 'Sign out', exact: true }).click()
  await expect(page.getByRole('link', { name: 'Cart', exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Cart (2)', exact: true })).toHaveCount(0)
})

test('admins can inspect monitoring alerts and filter protected audit events', async ({ page }) => {
  await login(page); await expect(page).toHaveURL(/\/staff\/products$/)
  await page.route('**/api/monitoring/summary', route => route.fulfill({ json: { windowMinutes: 15,
    counts: { LoginFailed: 6 }, alerts: [{ eventName: 'LoginFailed', remoteIp: '127.0.0.1', count: 6, threshold: 5 }], failedEmails: 1, loggingHealthy: true } }))
  let filter = ''
  await page.route('**/api/monitoring/events?*', route => {
    filter = new URL(route.request().url()).searchParams.get('eventName') || ''
    return route.fulfill({ json: [{ id: 1, occurredAt: new Date().toISOString(), event: 'LoginFailed', severity: 'Warning', actorId: null, targetId: null,
      remoteIp: '127.0.0.1', traceId: 'audit-test-trace', method: 'POST', route: 'api/auth/login', statusCode: 401, elapsedMs: 10 }] })
  })
  await page.getByRole('link', { name: 'Monitoring', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Security & logs.' })).toBeVisible()
  await expect(page.getByText('Activity requiring review')).toBeVisible()
  await expect(page.getByRole('alert')).toContainText('1 email deliveries')
  await expect(page.getByText('audit-test-trace', { exact: true })).toBeVisible()
  await page.getByLabel('Event name (exact)').fill('LoginFailed')
  await page.getByRole('button', { name: 'Apply filters', exact: true }).click()
  await expect.poll(() => filter).toBe('LoginFailed')
  await page.setViewportSize({ width: 390, height: 844 })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
})

test('employees cannot navigate to the monitoring workspace', async ({ page }) => {
  await login(page, 'employee'); await expect(page).toHaveURL(/\/staff\/products$/)
  await expect(page.getByRole('link', { name: 'Monitoring', exact: true })).toHaveCount(0)
  await page.evaluate(async () => {
    const router = (document.querySelector('#app') as any).__vue_app__.config.globalProperties.$router
    await router.push('/staff/monitoring')
  })
  await expect(page).toHaveURL(/\/$/)
})

test('password meter recognizes weak patterns despite meeting password rules', async ({ page }) => {
  const requests: string[] = []
  page.on('request', request => requests.push(request.url() + (request.postData() || '')))
  await page.goto('/signup')
  const password = page.getByLabel('Password', { exact: true })
  const meter = page.getByRole('meter', { name: 'Password strength', exact: true })
  await password.fill('Password123!')
  await expect(meter).toHaveAttribute('aria-valuenow', '1')
  await expect(meter).toHaveAttribute('aria-valuetext', 'Weak')
  await expect(page.getByText('This is similar to a commonly used password.')).toBeVisible()
  await password.fill('aaaaaaaaaaaa!')
  await expect(page.getByText('Repeated characters like "aaa" are easy to guess.')).toBeVisible()
  await password.fill('tulip-cobalt-orbit-marmalade!')
  await expect(meter).toHaveAttribute('aria-valuenow', '4')
  await expect(meter).toHaveAttribute('aria-valuetext', 'Very strong')
  expect(requests.some(request => request.includes('Password123!') || request.includes('tulip-cobalt-orbit-marmalade!'))).toBe(false)
  const stored = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))
  expect(stored).not.toContain('tulip-cobalt-orbit-marmalade!')
  await password.fill('')
  await expect(meter).toHaveCount(0)
  await expect(page.getByText('Use several unrelated words or a password manager to create a unique password.')).toBeVisible()
})

test('password meter reacts to email context and keeps rules separate from the score', async ({ page }) => {
  await page.goto('/reset-password')
  await page.getByLabel('New password', { exact: true }).fill('lovelyuniqueusername987!')
  const meter = page.getByRole('meter', { name: 'Password strength', exact: true })
  await expect(meter).toHaveAttribute('aria-valuenow', '4')
  await page.getByLabel('Email address', { exact: true }).fill('lovelyuniqueusername987@example.com')
  await expect(meter).toHaveAttribute('aria-valuenow', '1')
  await expect(page.getByText('There should not be any personal or page related data.')).toBeVisible()
  await page.getByLabel('New password', { exact: true }).fill('Long unrelated password without symbols')
  expect(await page.getByLabel('New password', { exact: true }).evaluate((input: HTMLInputElement) => input.validity.patternMismatch)).toBe(true)
  await page.goto('/login')
  await page.getByLabel('Password', { exact: true }).fill('Password123!')
  await expect(page.getByRole('meter')).toHaveCount(0)
})

test('staff password creation includes the strength meter', async ({ page }) => {
  await login(page); await expect(page).toHaveURL(/\/staff\/products$/)
  await page.getByRole('link', { name: 'Team', exact: true }).click()
  await page.getByLabel('Initial password', { exact: true }).fill('Password123!')
  await expect(page.getByRole('meter', { name: 'Password strength', exact: true })).toHaveAttribute('aria-valuetext', 'Weak')
})
