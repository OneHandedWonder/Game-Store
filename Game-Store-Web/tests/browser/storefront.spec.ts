import { expect, test, type Page } from '@playwright/test'

const product = { id: '11111111-1111-4111-8111-111111111111', name: 'Test Game', description: 'A browser test product.', price: 249.95, stock: 5, imageUrl: null, isActive: true, version: '33333333-3333-4333-8333-333333333333' }
const user = { id: '22222222-2222-4222-8222-222222222222', role: 'admin' }

async function catalog(page: Page) {
  await page.route('**/api/products?*', route => route.fulfill({ json: product.name.toLowerCase().includes((new URL(route.request().url()).searchParams.get('search') || '').toLowerCase()) ? [product] : [] }))
  await page.route(`**/api/products/${product.id}`, route => route.fulfill({ json: product }))
}
async function fillAddress(page: Page) {
  await page.getByLabel('Recipient name', { exact: true }).fill('Demo Recipient')
  await page.getByLabel('Street address', { exact: true }).fill('Example Street 42')
  await page.getByLabel('Postal code', { exact: true }).fill('1234')
  await page.getByLabel('City', { exact: true }).fill('Demo City')
}

async function login(page: Page, role = 'admin') {
  await page.route('**/api/staff/orders/analytics?*', route => route.fulfill({ json: { daily: [], topProducts: [] } }))
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

test('dummy checkout rejects real cards, preserves retry key and displays the saved order on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await login(page, 'default'); await expect(page).toHaveURL(/\/$/)
  const cart = { items: [{ ...product, productId: product.id, quantity: 2, lineTotal: 499.90 }], total: 499.90, totalQuantity: 2 }
  await page.route('**/api/cart', route => route.fulfill({ json: cart }))
  const order = { id: '55555555-5555-4555-8555-555555555555', createdAt: new Date().toISOString(), total: 499.90, currency: 'DKK', paymentStatus: 'simulated', items: [{ productId: product.id, name: product.name, price: product.price, quantity: 2 }] }
  const bodies: any[] = []
  await page.route('**/api/orders/checkout', route => {
    bodies.push(route.request().postDataJSON())
    return bodies.length === 1 ? route.fulfill({ status: 400, json: { message: 'The dummy payment was declined.' } }) : route.fulfill({ status: 201, json: { id: order.id } })
  })
  await page.route(`**/api/orders/${order.id}`, route => route.fulfill({ json: order }))
  await page.locator('nav a[href="/cart"]').click()
  await page.getByRole('link', { name: 'Continue to demo checkout' }).click()
  await expect(page.getByText('Demo only. No money will be charged', { exact: false })).toBeVisible()
  await fillAddress(page)
  await page.getByLabel('Test cardholder name').fill('Demo Person')
  await page.getByLabel('Test card number').fill('4012888888881881')
  await page.getByLabel('Expiry date').fill(`${new Date().getFullYear() + 1}-12`)
  await page.getByLabel('Test security code').fill('123')
  await page.getByRole('button', { name: /Place demo order/ }).click()
  await expect(page.getByRole('alert')).toContainText('Use one of the displayed test cards')
  expect(bodies).toHaveLength(0)
  await page.getByLabel('Test card number').fill('4000000000000002')
  await page.getByLabel('Test security code').fill('123')
  await page.getByRole('button', { name: /Place demo order/ }).click()
  await expect(page.getByRole('alert')).toContainText('declined')
  await expect(page.getByLabel('Test security code')).toHaveValue('')
  await page.getByLabel('Test card number').fill('4242 4242 4242 4242')
  await page.getByLabel('Test security code').fill('123')
  await page.getByRole('button', { name: /Place demo order/ }).click()
  await expect(page).toHaveURL(new RegExp(`/orders/${order.id}$`))
  await expect(page.getByRole('heading', { name: 'Demo order', exact: true })).toBeVisible()
  expect(bodies[0].checkoutKey).toBe(bodies[1].checkoutKey)
  expect(bodies[1]).toMatchObject({ cardNumber: '4242424242424242', expectedTotal: 499.90, deliveryAddress: { recipient: 'Demo Recipient', line1: 'Example Street 42', city: 'Demo City', postalCode: '1234', countryCode: 'DK' }, items: [{ productId: product.id, quantity: 2, price: product.price }] })
  const storage = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))
  expect(storage).not.toContain('4242'); expect(storage).not.toContain('Demo Person')
  expect(storage).not.toContain('Example Street 42')
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})

test('checkout conflicts require a new review and do not resubmit stale totals', async ({ page }) => {
  await login(page, 'default'); await expect(page).toHaveURL(/\/$/)
  await page.route('**/api/cart', route => route.fulfill({ json: { items: [{ ...product, productId: product.id, quantity: 1, lineTotal: product.price }], total: product.price, totalQuantity: 1 } }))
  await page.route('**/api/orders/checkout', route => route.fulfill({ status: 409, json: { message: 'Your cart or prices changed.' } }))
  await page.locator('nav a[href="/cart"]').click()
  await page.getByRole('link', { name: 'Continue to demo checkout' }).click()
  await fillAddress(page)
  await page.getByLabel('Test cardholder name').fill('Demo Person')
  await page.getByLabel('Test card number').fill('4242424242424242')
  await page.getByLabel('Expiry date').fill(`${new Date().getFullYear() + 1}-12`)
  await page.getByLabel('Test security code').fill('123')
  await page.getByRole('button', { name: /Place demo order/ }).click()
  await expect(page.getByRole('alert')).toContainText('prices changed')
  await expect(page.getByRole('button', { name: /Place demo order/ })).toHaveCount(0)
  await page.getByRole('button', { name: 'Reload checkout' }).click()
  await expect(page.getByRole('button', { name: /Place demo order/ })).toBeVisible()
})

test('order history opens private snapshots and password changes require confirmation then sign out', async ({ page }) => {
  await login(page, 'default'); await expect(page).toHaveURL(/\/$/)
  const order = { id: '55555555-5555-4555-8555-555555555555', createdAt: new Date().toISOString(), total: product.price, currency: 'DKK', paymentStatus: 'simulated', items: [{ productId: product.id, name: 'Purchased game snapshot', price: product.price, quantity: 1 }] }
  await page.route('**/api/orders?*', route => route.fulfill({ json: [order] }))
  await page.route(`**/api/orders/${order.id}`, route => route.fulfill({ json: order }))
  await page.getByRole('navigation', { name: 'Main navigation', exact: true }).getByRole('link', { name: 'Orders', exact: true }).click()
  await page.getByRole('link', { name: new RegExp(order.id) }).click()
  await expect(page.getByText(/Purchased game snapshot/)).toBeVisible()
  let body: any
  await page.route('**/api/auth/change-password', route => { body = route.request().postDataJSON(); return route.fulfill({ json: { message: 'Password changed.' } }) })
  await page.getByRole('link', { name: 'Account', exact: true }).click()
  await page.getByLabel('Current password', { exact: true }).fill('Current password!')
  await page.getByLabel('New password', { exact: true }).fill('Replacement password!')
  await page.getByLabel('Confirm new password', { exact: true }).fill('Different password!')
  await page.getByRole('button', { name: 'Change password', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('do not match'); expect(body).toBeUndefined()
  await page.getByLabel('Current password', { exact: true }).fill('Current password!')
  await page.getByLabel('Confirm new password', { exact: true }).fill('Replacement password!')
  await page.getByRole('button', { name: 'Change password', exact: true }).click()
  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByRole('status')).toContainText('All sessions have been revoked')
  expect(body).toEqual({ currentPassword: 'Current password!', newPassword: 'Replacement password!' })
  await expect(page.getByRole('link', { name: 'Account', exact: true })).toHaveCount(0)
})

test('checkout, orders and account routes require authentication', async ({ page }) => {
  for (const path of ['/checkout', '/orders', '/orders/55555555-5555-4555-8555-555555555555', '/account']) {
    await page.goto(path)
    await expect(page).toHaveURL(/\/login\?redirect=/)
  }
})

for (const role of ['employee', 'admin']) {
  test(`${role} can browse staff orders and open customer details`, async ({ page }) => {
    await login(page, role); await expect(page).toHaveURL(/\/staff\/products$/)
    const order = { id: '55555555-5555-4555-8555-555555555555', createdAt: new Date().toISOString(), total: 249.95, currency: 'DKK', paymentStatus: 'simulated', customerEmail: 'customer@example.com', items: [{ productId: product.id, name: 'Saved customer game', price: 249.95, quantity: 1 }] }
    const pages: string[] = []
    await page.route('**/api/staff/orders?*', route => {
      const pageNumber = new URL(route.request().url()).searchParams.get('page')!
      pages.push(pageNumber)
      return route.fulfill({ json: pageNumber === '1' ? Array.from({ length: 20 }, (_, i) => ({ ...order, id: i === 0 ? order.id : `order-${i}` })) : [] })
    })
    await page.route(`**/api/staff/orders/${order.id}`, route => route.fulfill({ json: order }))
    await page.route('**/api/orders?*', route => route.fulfill({ json: [] }))
    await page.locator('a[href="/staff/orders"]').click()
    await expect(page.getByRole('heading', { name: 'Customer orders.' })).toBeVisible()
    await page.getByRole('button', { name: 'Next', exact: true }).click()
    await expect(page.getByText('No orders on this page.')).toBeVisible()
    await page.getByRole('button', { name: 'Previous', exact: true }).click()
    await page.getByRole('link', { name: new RegExp(order.id) }).click()
    await expect(page.getByText('Customer: customer@example.com', { exact: true })).toBeVisible()
    await expect(page.getByText(/Saved customer game/)).toBeVisible()
    await page.getByRole('link', { name: 'Back to customer orders' }).click()
    await expect(page.getByRole('heading', { name: 'Customer orders.' })).toBeVisible()
    expect(pages).toContain('2')
    await page.getByRole('navigation', { name: 'Main navigation', exact: true }).getByRole('link', { name: 'Orders', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Order history.' })).toBeVisible()
    await expect(page.getByText('Customer: customer@example.com', { exact: true })).toHaveCount(0)
  })
}

test('staff orders deny guests and customers and show explicit API failures', async ({ page }) => {
  await page.goto('/staff/orders'); await expect(page).toHaveURL(/\/login\?redirect=/)
  await login(page, 'default'); await expect(page).toHaveURL(/\/$/)
  await expect(page.locator('a[href="/staff/orders"]')).toHaveCount(0)
  await page.evaluate(async () => { await (document.querySelector('#app') as any).__vue_app__.config.globalProperties.$router.push('/staff/orders') })
  await expect(page).toHaveURL(/\/$/)
  await login(page, 'employee'); await expect(page).toHaveURL(/\/staff\/products$/)
  await page.route('**/api/staff/orders?*', route => route.fulfill({ status: 503 }))
  await page.locator('a[href="/staff/orders"]').click()
  await expect(page.getByRole('alert')).toContainText('could not complete')
  await page.route('**/api/staff/orders?*', route => route.fulfill({ json: [] }))
  await page.getByRole('button', { name: 'Retry', exact: true }).click()
  await expect(page.getByText('No orders on this page.')).toBeVisible()
})

for (const role of ['employee', 'admin']) {
  test(`${role} can open management metrics and update fulfillment`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    await login(page, role); await expect(page).toHaveURL(/\/staff\/products$/)
    const metrics = { totalOrders: 4, fulfilledOrders: 1, unfulfilledOrders: 3, totalSales: 1000, averageOrderValue: 250, ordersToday: 2, orderingCustomers: 3, unitsOrdered: 8, activeProducts: 12, lowStockProducts: 2 }
    let fail = false
    await page.route('**/api/staff/orders/metrics', route => fail ? route.fulfill({ status: 503 }) : route.fulfill({ json: metrics }))
    await page.getByRole('link', { name: 'Management', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Store overview.' })).toBeVisible()
    await expect(page.getByText('1 of 4 orders completed')).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Simulated sales', exact: true })).toBeVisible()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    fail = true
    await page.getByRole('button', { name: 'Refresh metrics' }).click()
    await expect(page.getByRole('alert')).toContainText('could not complete')
    await expect(page.getByText('1 of 4 orders completed')).toHaveCount(0)
    fail = false
    await page.getByRole('button', { name: 'Retry', exact: true }).click()
    const order = { id: '55555555-5555-4555-8555-555555555555', createdAt: new Date().toISOString(), total: 250, currency: 'DKK', paymentStatus: 'simulated', customerEmail: 'customer@example.com', fulfillmentStatus: 'unfulfilled', fulfillmentVersion: '66666666-6666-4666-8666-666666666666', items: [] }
    await page.route('**/api/staff/orders?*', route => route.fulfill({ json: [order] }))
    await page.route(`**/api/staff/orders/${order.id}`, route => route.fulfill({ json: order }))
    let body: any
    await page.route(`**/api/staff/orders/${order.id}/fulfillment`, route => { body = route.request().postDataJSON(); order.fulfillmentStatus = 'fulfilled'; metrics.fulfilledOrders++; metrics.unfulfilledOrders--; return route.fulfill({ json: {} }) })
    await page.getByRole('link', { name: 'Manage orders', exact: true }).click()
    await page.getByRole('link', { name: new RegExp(order.id) }).click()
    await page.getByRole('button', { name: 'Mark fulfilled', exact: true }).click()
    await expect(page.getByRole('button', { name: 'Mark unfulfilled', exact: true })).toBeVisible()
    expect(body).toEqual({ status: 'fulfilled', version: order.fulfillmentVersion })
    await page.getByRole('link', { name: 'Management', exact: true }).click()
    await expect(page.getByText('2 of 4 orders completed')).toBeVisible()
  })
}

test('management is hidden and protected for customers and guests', async ({ page }) => {
  await page.goto('/staff'); await expect(page).toHaveURL(/\/login\?redirect=/)
  await login(page, 'default'); await expect(page).toHaveURL(/\/$/)
  await expect(page.getByRole('link', { name: 'Management', exact: true })).toHaveCount(0)
  await page.evaluate(async () => { await (document.querySelector('#app') as any).__vue_app__.config.globalProperties.$router.push('/staff') })
  await expect(page).toHaveURL(/\/$/)
})

test('management charts expose real series, keyboard details and selectable periods on mobile', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await login(page, 'employee'); await expect(page).toHaveURL(/\/staff\/products$/)
  await page.route('**/api/staff/orders/metrics', route => route.fulfill({ json: { totalOrders: 2, fulfilledOrders: 1, unfulfilledOrders: 1, totalSales: 250, averageOrderValue: 125, ordersToday: 1, orderingCustomers: 2, unitsOrdered: 2, activeProducts: 4, lowStockProducts: 1 } }))
  const periods: number[] = []
  await page.route('**/api/staff/orders/analytics?*', route => {
    const days = Number(new URL(route.request().url()).searchParams.get('days')); periods.push(days)
    return route.fulfill({ json: { daily: Array.from({ length: days }, (_, i) => ({ date: new Date(Date.UTC(2026, 0, i + 1)).toISOString().slice(0, 10), orders: i === days - 1 ? 2 : 0, sales: i === days - 1 ? 250 : 0 })), topProducts: [{ productId: product.id, name: 'Popular saved game', units: 2, sales: 250 }] } })
  })
  await page.getByRole('link', { name: 'Management', exact: true }).click()
  await expect(page.getByRole('img', { name: 'Simulated sales trend', exact: true })).toBeVisible()
  await expect(page.getByRole('img', { name: 'Daily orders', exact: true })).toBeVisible()
  await expect(page.getByText('Popular saved game', { exact: true })).toBeVisible()
  await page.getByLabel('Chart period').selectOption('7')
  await expect(page.locator('.dashboard-chart').first().locator('circle')).toHaveCount(7)
  await page.locator('.dashboard-chart').first().locator('circle').last().focus()
  await expect(page.locator('.chart-readout').first()).toContainText('250,00')
  await page.locator('.dashboard-chart').first().getByText('View daily data', { exact: true }).click()
  await expect(page.locator('.dashboard-chart').first().getByRole('table')).toBeVisible()
  expect(periods).toContain(30); expect(periods).toContain(7)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})

test('order details display a saved delivery address and explicit legacy absence', async ({ page }) => {
  await login(page, 'default'); await expect(page).toHaveURL(/\/$/)
  const order = { id: '55555555-5555-4555-8555-555555555555', createdAt: new Date().toISOString(), total: 250, currency: 'DKK', paymentStatus: 'simulated', items: [], deliveryAddress: { recipient: 'Demo Recipient', line1: 'Example Street 42', line2: 'Suite 2', postalCode: '1234', city: 'Demo City', countryCode: 'DK' } as any }
  await page.route('**/api/orders?*', route => route.fulfill({ json: [order] }))
  await page.route(`**/api/orders/${order.id}`, route => route.fulfill({ json: order }))
  await page.getByRole('navigation', { name: 'Main navigation', exact: true }).getByRole('link', { name: 'Orders', exact: true }).click()
  await page.getByRole('link', { name: new RegExp(order.id) }).click()
  await expect(page.locator('address')).toContainText('Example Street 42')
  await expect(page.locator('address')).toContainText('Suite 2')
  await page.getByRole('link', { name: 'Back to order history' }).click()
  order.deliveryAddress = null
  await page.getByRole('link', { name: new RegExp(order.id) }).click()
  await expect(page.getByText('No delivery address was collected for this older order.')).toBeVisible()
})
