export function securityHeaders(development = false): Record<string, string> {
  const policy = [
    "default-src 'self'",
    "base-uri 'none'",
    "object-src 'none'",
    "frame-ancestors 'none'",
    "frame-src 'none'",
    "form-action 'self'",
    "script-src 'self'",
    development
      ? "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com"
      : "style-src 'self' https://fonts.googleapis.com",
    "style-src-attr 'unsafe-inline'",
    "font-src 'self' https://fonts.gstatic.com",
    "img-src 'self' http: https:",
    development ? "connect-src 'self' ws://localhost:* ws://127.0.0.1:* wss://localhost:* wss://127.0.0.1:*" : "connect-src 'self'",
    "worker-src 'self'",
    "manifest-src 'self'",
  ].join('; ')
  return {
    'Content-Security-Policy': policy,
    'X-Frame-Options': 'DENY',
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'no-referrer',
    'Permissions-Policy': 'camera=(), microphone=(), geolocation=(), payment=()',
  }
}
