import { expect, test } from '@playwright/test'

test('frontend documents reject cross-origin framing', async ({ page, browser }) => {
  const response = await page.goto('/login')
  expect(response!.headers()['content-security-policy']).toContain("frame-ancestors 'none'")
  expect(response!.headers()['x-frame-options']).toBe('DENY')
  const attacker = await browser.newPage()
  await attacker.route('http://localhost:5199/**', route => route.fulfill({ contentType: 'text/html', body: '<iframe src="http://127.0.0.1:5174/login"></iframe>' }))
  await attacker.goto('http://localhost:5199/')
  await expect.poll(async () => {
    const frame = attacker.frames().find(candidate => candidate !== attacker.mainFrame())
    return frame ? await frame.locator('input[type="password"]').count().catch(() => 0) : -1
  }).toBe(0)
  await expect.poll(() => attacker.frames().some(frame => frame.url().startsWith('chrome-error://'))).toBe(true)
  await attacker.close()
})
