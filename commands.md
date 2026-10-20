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
