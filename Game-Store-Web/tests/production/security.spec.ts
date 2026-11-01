import { readFileSync } from 'node:fs'
import { expect, test } from '@playwright/test'
import { securityHeaders } from '../../security-headers'

test('compiled app enforces production CSP without breaking rendering or workers', async ({ page }) => {
  await page.addInitScript(() => {
    (window as any).cspViolations = []
    document.addEventListener('securitypolicyviolation', event => (window as any).cspViolations.push(event.violatedDirective))
  })
  await page.route('https://fonts.googleapis.com/**', route => route.fulfill({ contentType: 'text/css', body: '' }))
  await page.route('**/api/products?*', route => route.fulfill({ json: [] }))
  const response = await page.goto('/signup')
  expect(response!.headers()['content-security-policy']).toBe(securityHeaders()['Content-Security-Policy'])
  expect(response!.headers()['content-security-policy']).not.toContain('unsafe-eval')
  expect(response!.headers()['content-security-policy']).not.toContain('ws:')
  await expect(page.getByRole('heading', { name: 'Join the good times.' })).toBeVisible()
  await page.getByLabel('Password', { exact: true }).fill('tulip-cobalt-orbit-marmalade!')
  await expect(page.getByRole('meter')).toHaveAttribute('aria-valuetext', 'Very strong')
  await expect(page.getByRole('meter').locator('span')).toHaveCSS('width', /[1-9]/)
  await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))))
  expect(await page.evaluate(() => (window as any).cspViolations)).toEqual([])
  const nginx = readFileSync(new URL('../../deploy/nginx-security-headers.conf', import.meta.url), 'utf8')
  for (const [name, value] of Object.entries(securityHeaders())) expect(nginx).toContain(`add_header ${name} "${value}" always;`)
})

test('compiled frontend blocks framing and inline script injection', async ({ page, browser }) => {
  await page.goto('/login')
  const blocked = page.waitForEvent('console', message => message.text().includes('Content Security Policy'))
  await page.evaluate(() => { const script = document.createElement('script'); script.textContent = 'window.inlineScriptExecuted = true'; document.head.append(script) })
  await blocked
  expect(await page.evaluate(() => (window as any).inlineScriptExecuted)).toBeUndefined()
  const attacker = await browser.newPage()
  await attacker.route('http://localhost:5199/**', route => route.fulfill({ contentType: 'text/html', body: '<iframe src="http://127.0.0.1:5175/login"></iframe>' }))
  await attacker.goto('http://localhost:5199/')
  await expect.poll(() => attacker.frames().some(frame => frame.url().startsWith('chrome-error://'))).toBe(true)
  await attacker.close()
})
