# GoRide.ServiceTemplate

This is the **Codebase Structure** for GoRide-trip_matching backend microservice.

SCRUM-83 ride-start rules, API errors, and test instructions are documented in
[docs/SCRUM-83.md](docs/SCRUM-83.md).

## Folder structure

```
GoRide.ServiceTemplate/
├── .github/workflows/ci.yml        ← build + test + docker build on every push
├── src/GoRide.ServiceTemplate/
│   ├── Controllers/                 ← HTTP request handling only, no business logic
│   │   └── HealthController.cs      ← GET /health — proves DB connectivity
│   ├── Services/                    ← business logic goes here (empty — fills up story by story)
│   ├── Models/                      ← C# classes representing your data (empty — same)
│   ├── Data/                        ← ADO.NET data access
│   │   ├── IDbConnectionFactory.cs
│   │   └── MySqlConnectionFactory.cs
│   ├── Events/                      ← Kafka code goes here later (empty for now)
│   ├── Program.cs                   ← app startup: DI, CORS, Swagger, routing
│   ├── appsettings.json             ← non-secret config
│   ├── appsettings.Development.json ← local secret (gitignored — see below)
│   └── GoRide.ServiceTemplate.csproj
├── tests/GoRide.ServiceTemplate.Tests/
├── Dockerfile
├── docker-compose.yml
├── .env.example
├── .gitignore / .dockerignore
└── GoRide.ServiceTemplate.sln
```

## How to turn this into a real service (do this once, per microservice)

Say you're creating `goride-location` from this template:

1. **Rename the solution and project.** Every occurrence of `GoRide.ServiceTemplate` (folder names, file names, and text *inside* files — `.csproj`, `.sln`, `Program.cs`'s namespace, `Dockerfile`, `docker-compose.yml`'s container name) needs to become `GoRide.Location`. On Mac/Linux, from the repo root:
   ```bash
   # rename files/folders containing ServiceTemplate
   find . -depth -name "*ServiceTemplate*" -not -path "./.git/*" | while read f; do
     new=$(echo "$f" | sed 's/ServiceTemplate/Location/g')
     mv "$f" "$new"
   done
   # rename the text inside every file
   grep -rl "ServiceTemplate" --include="*.cs" --include="*.csproj" --include="*.sln" \
        --include="*.yml" --include="Dockerfile" . | xargs sed -i '' 's/ServiceTemplate/Location/g'
   ```
   (Drop the `''` after `-i` on Linux; keep it on macOS — that's a BSD-vs-GNU `sed` quirk, not something specific to this project.)

2. **Fill in the real database name and user** in `appsettings.json` (`Db:Database`, `Db:User`) — these come from your Azure MySQL Setup Guide, Section 5 (e.g. `location_db` / `location_svc`).

3. **Copy `.env.example` to `.env`** and fill in the real password + Vercel URL. Do the same for `appsettings.Development.json`'s password if you're running via `dotnet run` instead of Docker.

4. **Pick a distinct local port** if you'll ever run more than one service locally at once — edit the `ports:` mapping in `docker-compose.yml` (e.g. `8081:8080` for Location, `8082:8080` for Trip, `8083:8080` for Payment) so they don't collide on your machine.

5. **Verify the walking skeleton before writing any story code:**
   ```bash
   dotnet restore
   dotnet build
   dotnet test              # should show 1 passing sanity test
   dotnet run --project src/GoRide.Location/GoRide.Location.csproj
   ```
   Visit `https://localhost:5001/health` (or whatever port `dotnet run` prints) — you should see `{"status":"healthy","database":"connected",...}`. Then repeat via Docker:
   ```bash
   docker compose up --build
   ```
   and confirm the same `/health` response, now containerized.

6. **Push to the service's own GitHub repo** and confirm the CI workflow goes green on an empty skeleton — this is the moment to catch pipeline issues, before any real feature code makes debugging harder.

Only once all of that is green do you start on the service's first real Sprint story.

## CI/CD

Everything is in `.github/workflows/`:

| File | Runs when | Does |
|---|---|---|
| `ci.yml` | every PR into `dev`/`main`, and pushes to them | just calls `ci-reusable.yml` |
| `ci-reusable.yml` | called by the other two | build, tests, `dotnet format` check (warning only for now), vulnerable NuGet scan (**blocks**), docker build. Ends in a **CI Gate** job — that's the required check on PRs |
| `cd.yml` | push to `dev` | runs CI, builds the image, pushes it to ACR, updates the Azure Container App, checks `/health` |

Branch flow: `SCRUM-xx-...` → PR into `dev` (CI Gate + CodeRabbit) → merge auto-deploys `dev` → `dev` → `main` PR when we want a release.

CodeRabbit config is in `.coderabbit.yaml`. Note it reviews PRs into `dev` because of `base_branches` there — don't remove that.

### Turning CD on

`cd.yml` skips the deploy until the repo variable `CD_ENABLED` is `true`. Before flipping it:

1. Add a federated credential on the Azure app registration (`AZURE_CLIENT_ID`) with subject `repo:GoRide-App/goride-trip-matching:ref:refs/heads/dev`. Without it the Azure login step fails.
2. Create the container app once by hand in `goride-rg` / `goride-env`, named whatever `AZURE_CONTAINERAPP_NAME` is (`goride-trip-matching`), target port 8080, **external** HTTP ingress (the deploy job calls `/health` from a GitHub runner, so internal-only ingress would fail it), pulling from the ACR. **Don't reuse `goride-api` — that's identity-auth.**
3. On the container app set `Db__Password` (as a secret), `Cors__AllowedOrigins__1` (the real Vercel URL) and `Kafka__BootstrapServers`.
4. Settings → Secrets and variables → Actions → Variables → set `CD_ENABLED` = `true`. The next push to `dev` deploys.
| `cd.yml` | push to `dev` | runs CI, builds the image, pushes it to `ghcr.io/goride-app/goride-trip-matching`, updates the Azure Container App, checks `/health` |

Branch flow: `SCRUM-xx-...` → PR into `dev` (CI Gate + CodeRabbit review) → merge deploys `dev` → `dev` → `main` PR for a release.

**Don't change the workflow files inside a story branch.** Pipeline changes go in their own `ci/...` PR so they get reviewed on their own.

CodeRabbit reads `.coderabbit.yaml`. Its `base_branches: ["dev"]` line is what makes it review PRs into `dev`, so keep it.

### Why images go to GHCR, not the Azure registry

Our Azure for Students subscription only allows Container Apps **Express** environments. Express won't keep a managed-identity login for our Azure registry, so pulling from it would mean storing the registry's shared admin password on every app. Instead, CD publishes the image as a **public** GitHub package and Azure pulls it without any credentials. The image has nothing secret in it. It's built from a clean checkout, and passwords are set on the container app as secrets. Express doesn't support Key Vault references either.

### Turning CD on

`cd.yml` does nothing until the repo variable `CD_ENABLED` is `true`. Before that:

1. **Azure (done):** container app `goride-trip-matching` in `goride-rg` / `goride-env` (port 8080, external ingress, DB host/user/CORS env vars), and a federated credential on `goride-github-actions-identity` for this repo's `dev` branch.
2. **Azure (resource group owner):** give `goride-github-actions-identity` the **Contributor** role on the `goride-trip-matching` container app.
3. **Azure (whoever has the password):** add the DB password as an app secret and point the env var at it:
   ```bash
   az containerapp secret set -n goride-trip-matching -g goride-rg --secrets db-password=<password>
   az containerapp update -n goride-trip-matching -g goride-rg --set-env-vars Db__Password=secretref:db-password
   ```
4. **GitHub (org owner):** allow public packages by default (org Settings → Packages), or make the `goride-trip-matching` package public after the first push.
5. **GitHub (this repo):** Settings → Secrets and variables → Actions → **Variables**. Run the `az` commands with the student subscription selected:

   | Name | Value |
   |---|---|
   | `AZURE_CLIENT_ID` | `az identity show -n goride-github-actions-identity -g goride-rg --query clientId -o tsv` |
   | `AZURE_TENANT_ID` | `az account show --query tenantId -o tsv` |
   | `AZURE_SUBSCRIPTION_ID` | `az account show --query id -o tsv` |
   | `AZURE_RESOURCE_GROUP` | `goride-rg` |
   | `AZURE_CONTAINERAPP_NAME` | `goride-trip-matching` |
   | `CD_ENABLED` | `true` (set this last) |

Then push to `dev`, or run **CD** from the Actions tab with the `dev` branch selected (Azure only trusts logins from `dev`). If `/health` returns 500 "unhealthy", the app is running but can't reach MySQL, so check the `db-password` secret.

### Formatting

CI warns about `dotnet format` violations but doesn't fail on them yet. Run this before pushing:

```bash
dotnet format GoRide.Trip.sln
```

Once the existing violations are cleaned up we'll remove `continue-on-error` from the formatting step and make it blocking.

## Why ADO.NET, not an ORM

`MySqlConnectionFactory` returns a raw `MySqlConnection` — every query you write uses parameterised `MySqlCommand` objects directly, not Entity Framework or any other ORM. This is a deliberate, explicit requirement in the assignment brief, not a stylistic choice — keep it consistent across all four services.

## Why the frontend origin is configuration, not hardcoded

`Cors:AllowedOrigins` in `appsettings.json` lists which frontend URLs may call this API. Locally that's `http://localhost:3000` (Next.js dev server); in production it's your Vercel deployment URL. Update the placeholder once your frontend actually has a Vercel URL — until then, local development works fine with just `localhost:3000`.


---------------------------------------------------
QA Testings
---------------------------------------------------


docker compose up --build -d


# SCRUM-53/54 
## Happy path — valid input, expected 200 response---
* Black box — hit only through the public HTTP endpoint, no code involved.

Invoke-RestMethod -Uri "http://localhost:8080/fare/estimate" -Method Post -ContentType "application/json" -Body '{"startLat":6.9344,"startLng":79.8428,"endLat":6.8905,"endLng":79.8565}'

## Negative / edge cases — not happy path anymore
* Black box — same, just through the HTTP interface 
* 0 Distance Trip---

Invoke-RestMethod -Uri "http://localhost:8080/fare/estimate" -Method Post -ContentType "application/json" -Body '{"startLat":6.9344,"startLng":79.8428,"endLat":6.9344,"endLng":79.8428}'


* A missing field return Bad Request ---

Invoke-RestMethod -Uri "http://localhost:8080/fare/estimate" -Method Post -ContentType "application/json" -Body '{"startLat":6.9344,"startLng":79.8428,"endLat":6.8905}'

---------------------------------------------------







Frontend ──► FindNearbyDriversRequest ──► DriverMatchingService ──► FindNearbyDriversResponse ──► Frontend
                                            │        │                     └─ list of MatchedDriver
            NearbyDriverLocation ◄──────────┘        └──────► ActiveDriver (already existed)
            (from goride-location)                            (from identity-auth)
