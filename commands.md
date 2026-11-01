# Game Store commands

Run commands from the repository root (`Game-Store`) unless stated otherwise.
Requires .NET 9 SDK, Node.js 22.12+ and Docker with Compose v2 and Linux containers.
Start Docker Desktop first when using it. Install these tools for your OS before continuing.

PowerShell examples support Windows PowerShell 5.1 and PowerShell 7.
The Bash examples also work in macOS's default zsh. In WSL, install the SDK and
Node inside WSL and enable Docker Desktop's WSL integration.

## Check installed tools (all shells)

```sh
dotnet --version
node --version
npm --version
docker version
docker compose version
```

## First-time setup

### PowerShell

```powershell
cd F:\GitRepos\Game-Store
dotnet restore ./Game-Store-API/Game-Store-API.csproj
npm --prefix ./Game-Store-Web ci
docker compose -f ./Game-Store-API/compose.yaml up -d --wait
if ($LASTEXITCODE -ne 0) { throw 'Database startup failed' }

# Apply numbered migrations in order; stop on the first failure.
Get-ChildItem ./Game-Store-API/Data -Filter '*.sql' |
    Where-Object { $_.Name -match '^\d+_' } |
    Sort-Object Name |
    ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw |
            docker compose -f ./Game-Store-API/compose.yaml exec -T db psql -v ON_ERROR_STOP=1 -U game_store -d game_store
        if ($LASTEXITCODE -ne 0) { throw "Migration failed: $($_.Name)" }
    }

# Optional: add the 12 sample products. Existing seed products are preserved.
Get-Content ./Game-Store-API/Data/seed_catalog.sql -Raw |
    docker compose -f ./Game-Store-API/compose.yaml exec -T db psql -v ON_ERROR_STOP=1 -U game_store -d game_store
```

### Bash / macOS zsh

Replace the example path with your checkout location. Windows drives in WSL
typically use paths such as `/mnt/f/GitRepos/Game-Store`.

```sh
cd /path/to/Game-Store
dotnet restore ./Game-Store-API/Game-Store-API.csproj
npm --prefix ./Game-Store-Web ci
docker compose -f ./Game-Store-API/compose.yaml up -d --wait

# Run in a subshell so failure stops migrations without closing your terminal.
(
    set -e
    for migration in ./Game-Store-API/Data/[0-9]*_*.sql; do
        docker compose -f ./Game-Store-API/compose.yaml exec -T db \
            psql -v ON_ERROR_STOP=1 -U game_store -d game_store < "$migration"
    done
)

# Optional sample catalog.
docker compose -f ./Game-Store-API/compose.yaml exec -T db \
    psql -v ON_ERROR_STOP=1 -U game_store -d game_store \
    < ./Game-Store-API/Data/seed_catalog.sql
```

Check each command succeeds before continuing. Apply all migrations before starting
the API. The Compose database credentials are for local development only.

## Configure development secrets (once per machine)

The project already has a UserSecretsId; do not run `user-secrets init`.
Skip signing-key generation if a key is already configured: replacing it invalidates
existing JWTs. These commands store secrets outside the repository. User secrets
are development storage and are not encrypted.

### PowerShell

```powershell
# Generate a signing key without printing it.
$keyBytes = New-Object byte[] 32
$keyGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $keyGenerator.GetBytes($keyBytes) } finally { $keyGenerator.Dispose() }
dotnet user-secrets set 'Jwt:SigningKey' ([Convert]::ToBase64String($keyBytes)) --project ./Game-Store-API
Remove-Variable keyBytes, keyGenerator

# Prompt so the SMTP password is not written literally into shell history.
$smtpCredential = Get-Credential -UserName 'noreply@bundberg.dk' -Message 'Enter the one.com mailbox password'
dotnet user-secrets set 'Email:Password' ($smtpCredential.GetNetworkCredential().Password) --project ./Game-Store-API
Remove-Variable smtpCredential
```

### Bash / macOS zsh

Requires `openssl`. Bash and zsh use different hidden-input prompt syntax.

```sh
dotnet user-secrets set 'Jwt:SigningKey' "$(openssl rand -base64 32)" --project ./Game-Store-API
```

Bash password prompt:

```bash
read -r -s -p 'one.com mailbox password: ' smtp_password
printf '\n'
dotnet user-secrets set 'Email:Password' "$smtp_password" --project ./Game-Store-API
unset smtp_password
```

macOS zsh password prompt:

```zsh
read -r -s 'smtp_password?one.com mailbox password: '
printf '\n'
dotnet user-secrets set 'Email:Password' "$smtp_password" --project ./Game-Store-API
unset smtp_password
```

## Run locally (all shells)

Start the database:

```sh
docker compose -f ./Game-Store-API/compose.yaml up -d --wait
```

In a separate terminal at the repository root, start the API:

```sh
dotnet run --project ./Game-Store-API --launch-profile http
```

In another terminal at the repository root, start Vue:

```sh
npm --prefix ./Game-Store-Web run dev
```

- Frontend: http://127.0.0.1:5173
- Swagger: http://localhost:5225/swagger
- OpenAPI: http://localhost:5225/openapi/v1.json
- Database/logging health: http://localhost:5225/health/db

Stop the API and frontend with Ctrl+C in their terminals.

### Optional frontend proxy configuration

PowerShell:

```powershell
Copy-Item ./Game-Store-Web/.env.example ./Game-Store-Web/.env.local
```

Bash / macOS zsh:

```sh
cp ./Game-Store-Web/.env.example ./Game-Store-Web/.env.local
```

Edit `API_PROXY_TARGET` in `.env.local` if the API address differs, then restart
Vite. The default is `http://localhost:5225`. Never put secrets in `VITE_*` values.

## Build and test (all shells)

```sh
dotnet build ./Game-Store-API/Game-Store-API.csproj
npm --prefix ./Game-Store-Web run build
```

Backend integration checks require the database to be running. They create and
remove an isolated test database and capture email without sending real mail.
This is a console test runner, so use `dotnet run`, not `dotnet test`.
Stop the API first if Windows reports locked build files.
Alternatively, use `-c Release` to build and run checks separately from a running
Debug API. Backend checks are in `Game-Store-API.IntegrationTests`; browser checks
are in `Game-Store-Web/tests/browser` and mock the API rather than testing a live backend.

```sh
dotnet run --project ./Game-Store-API.IntegrationTests -- ./Game-Store-API
```

For detailed build output while running backend checks in Release:

```sh
dotnet run --project ./Game-Store-API.IntegrationTests -c Release --verbosity detailed -- ./Game-Store-API
```

`--verbosity detailed` controls build output; the console test runner still prints
its existing PASS/failure messages.

Browser tests use mocked API responses and a separate Vite server on port 5174.
Run from the frontend directory so Playwright finds its configuration:

```sh
cd Game-Store-Web
npx playwright install chromium
npm test
# List individual test results and record traces for every test:
npx playwright test --reporter=list --trace=on
# Optional interactive test UI / report:
npx playwright test --ui
npx playwright show-report
cd ..
```

On Linux/WSL, if browser system libraries are missing, use
`npx playwright install --with-deps chromium` from the frontend directory.

## Database operations (all shells)

```sh
# Container state and database logs.
docker compose -f ./Game-Store-API/compose.yaml ps
docker compose -f ./Game-Store-API/compose.yaml logs --tail 100 -f db

# Interactive SQL console; use \q to exit.
docker compose -f ./Game-Store-API/compose.yaml exec db psql -U game_store -d game_store

# Stop containers while preserving database data.
docker compose -f ./Game-Store-API/compose.yaml down
```

**Delete all local database data** only when intentionally resetting development:

```sh
docker compose -f ./Game-Store-API/compose.yaml down --volumes
```

After a reset, start the database and rerun migrations and optional seed commands.

## Health and application logs

PowerShell:

```powershell
Invoke-RestMethod http://localhost:5225/health/db

# Optional: set a known log directory before starting the API in this terminal.
$env:ApplicationLogging__Directory = Join-Path (Get-Location) 'Game-Store-API/logs'
dotnet run --project ./Game-Store-API --launch-profile http
```

Bash / macOS zsh:

```sh
curl --fail --show-error http://localhost:5225/health/db

# Optional: set a known log directory for this API process.
ApplicationLogging__Directory="$PWD/Game-Store-API/logs" \
    dotnet run --project ./Game-Store-API --launch-profile http
```

The API also writes structured logs to its console. The optional directory above
is ignored by Git. Admins can inspect the security audit dashboard at
http://127.0.0.1:5173/staff/monitoring.

## HTTPS and production build artifacts (all shells)

```sh
# Optional local HTTPS certificate; trust support depends on your OS/browser.
dotnet dev-certs https --trust
dotnet run --project ./Game-Store-API --launch-profile https

# Publish API and compile frontend.
dotnet publish ./Game-Store-API/Game-Store-API.csproj -c Release -o ./Game-Store-API/bin/publish
npm --prefix ./Game-Store-Web run build
```

HTTPS API: https://localhost:7008/swagger. Frontend output: `Game-Store-Web/dist`.
These commands build artifacts; deployment still needs production secrets,
database migrations, persistent Data Protection keys, HTTPS, SPA route fallback
and a same-origin `/api` reverse proxy. The Vite development proxy is not included
in the production build.

Preview compiled frontend assets locally:

```sh
npm --prefix ./Game-Store-Web run preview
```

Preview does not proxy `/api`; use the development server for local API-backed use.

## Dummy checkout and account features

Apply `Data/008_orders.sql` using the migration procedure above before using
checkout or order history on an existing database. Restart the API after rebuilding.

- `/checkout`: authenticated demo checkout, reached from the cart. Nothing is
  charged or shipped. Only these test card numbers are accepted:
  `4242 4242 4242 4242` succeeds; `4000 0000 0000 0002` simulates a decline.
  Use a fictional cardholder name, an unexpired month/year (at most 20 years ahead),
  and a made-up 3–4 digit security code. Never enter real card details.
- `/orders`: private, paginated order history with saved product names, quantities,
  prices and DKK totals. Deleting or editing products does not change past orders.
- `/account`: change password with current-password verification. A successful
  change revokes all JWT sessions and pending password reset codes.

The API validates card syntax, Luhn checksum, test-card allowlist, expiry and
security-code format. Card details are used transiently for simulation and are
excluded from database storage and application logs. This is not a real payment
provider integration or proof that a card/account exists or has funds.

Checkout locks cart writes and product rows, validates the reviewed cart against
current server prices/stock, then saves the order, deducts stock and clears the
cart in one transaction. The per-account checkout key makes retries return the
same order without another stock deduction. Price/cart conflicts require a new
review; declines preserve the cart. Carts support up to 100 distinct products.
Checkout and password changes have separate per-account rate limits of 20 and 10
requests per minute respectively. Existing current-password lockout rules also
apply to password changes.

See `user-stories.md` for the implementation and validation map.

### Order confirmation emails

For an existing database, apply `Data/009_order_confirmation_email.sql` using the
migration procedure above and restart the API with the new build. This migration
also updates the account-email uniqueness index; the matching API code is required
for registration and recovery email requests after migration.

Each new checkout queues an encrypted confirmation for the signed-in account's
email address in the same transaction as the order. It includes the order ID,
saved items, quantities, DKK prices/total, and the simulated-payment/no-shipping
notice. No card details or reset codes are included. Checkout retries do not queue
another message, and separate orders can each have a pending confirmation.

The existing outbox worker sends confirmations through the configured SMTP account.
Delivery failures retain the message and retry after one minute, up to five attempts;
confirmation messages expire after seven days. Check application logs for delivery
failures. The order remains saved if SMTP delivery fails. SMTP delivery is at least
once: an SMTP acceptance followed by a lost acknowledgement or database failure
can cause a duplicate email on retry.

Integration checks capture confirmation emails without sending real mail and cover
encryption, recipient selection, contents, duplicate submissions, multiple pending
orders, transaction rollback and delivery retry behavior.

### Staff orders

Admins and employees can open **Management** (`/staff`) and then **Staff orders** (`/staff/orders`) to browse all
customer orders, 20 per page, and view saved items, quantities, DKK totals, dates,
customer email addresses and simulated-payment status. The API at
`/api/staff/orders` and `/api/staff/orders/{id}` enforces staff roles and disables
response caching. Customer `/orders` routes remain restricted to the order owner.

### Management dashboard and fulfillment

Apply `Data/010_order_fulfillment.sql` and restart the rebuilt API. Existing and new
orders start as `unfulfilled`. The staff-only Management button opens `/staff`,
with database metrics for total/fulfilled/unfulfilled orders, simulated sales,
average order value, orders today (Europe/Copenhagen), distinct ordering customers,
units ordered, active products, and active products with five or fewer units left.
Figures are all-time unless labeled otherwise; use Refresh metrics for current data.

Staff can mark orders fulfilled or unfulfilled on the order-details page. Updates
require the loaded fulfillment version; stale updates return a conflict and require
reloading. Fulfillment is a demo workflow and does not ship goods, charge money,
change stock, or send additional confirmation emails. Inventory and order links
are in the management navigation; team and monitoring remain admin-only.

### Dashboard charts and order addresses

Apply `Data/011_order_addresses.sql` before restarting the rebuilt API. Checkout
now requires a recipient, street address, postal code, city, and two-letter country
code, with an optional second address line. Use fictional addresses in this demo.
Fields have server-side length/format validation; this does not verify deliverability.
The address is an immutable order snapshot encrypted with ASP.NET Data Protection,
and is returned only with owner/staff order details. It is excluded from lists,
analytics, confirmation emails and application logs, and is not saved in browser
storage. Older orders have no address and display that explicitly. Preserve the
API's Data Protection keys across deployments to keep addresses decryptable.

Management includes daily simulated-sales and order charts, a fulfillment ring,
and the five most-ordered products. Choose a 7/30/90-day period for charts and top
products. Daily totals use Europe/Copenhagen dates, include zero-order days, and
are calculated from saved orders. The fulfillment ring and metric cards remain
all-time. Graph points support keyboard focus and daily tables provide the data
as text. Empty periods display zero totals without fabricated activity.

## Frontend security headers

Vite development and preview responses send CSP `frame-ancestors 'none'` and
`X-Frame-Options: DENY`, preventing the SPA from being embedded. They also send
`nosniff`, a no-referrer policy and restrictive browser feature permissions.
Restart the development server after changing its configuration.

The production CSP allows same-origin scripts, API requests and workers, the
existing Google Fonts stylesheet/font hosts, and HTTP(S) product images. Inline
scripts, eval, embedded frames, plugins and foreign form destinations are blocked.
Inline style attributes remain allowed for Vue's chart and password-meter styles.
Development additionally permits injected styles and localhost HMR WebSockets;
these exceptions are absent from the compiled preview/production policy.

Vite headers are not embedded in `dist/`. For production Nginx hosting, include
`Game-Store-Web/deploy/nginx-security-headers.conf` in the HTTPS frontend server
block. Repeat the include in any location that defines its own `add_header`, since
those directives affect inheritance. Keep existing TLS, SPA fallback, API reverse
proxy and static asset configuration. Validate with `nginx -t` before reloading.
For other hosts, configure the equivalent response headers from `security-headers.ts`.
Do not use a CSP meta tag for frame protection: `frame-ancestors` requires a header.

Run `npm test` in the frontend for development browser checks and
`npm run test:production` for compiled-app CSP checks. The latter verifies framing
and inline-script blocking, password-worker rendering, and Nginx policy parity.
Verify real deployed HTML responses separately, including deep links and errors;
local preview tests do not prove an external host has applied these headers.
