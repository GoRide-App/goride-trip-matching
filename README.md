# GoRide.ServiceTemplate

This is the **base walking skeleton** for every GoRide backend microservice — it contains everything that should exist *before* any Sprint story work begins: a runnable ASP.NET Web API, a working Azure MySQL connection (ADO.NET, no ORM), CORS configured for the Vercel-hosted frontend, a Dockerfile, a basic CI pipeline, and a test project. No Kafka code yet — that comes later, per-service, once a story actually needs it.

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

## Why ADO.NET, not an ORM

`MySqlConnectionFactory` returns a raw `MySqlConnection` — every query you write uses parameterised `MySqlCommand` objects directly, not Entity Framework or any other ORM. This is a deliberate, explicit requirement in the assignment brief, not a stylistic choice — keep it consistent across all four services.

## Why the frontend origin is configuration, not hardcoded

`Cors:AllowedOrigins` in `appsettings.json` lists which frontend URLs may call this API. Locally that's `http://localhost:3000` (Next.js dev server); in production it's your Vercel deployment URL. Update the placeholder once your frontend actually has a Vercel URL — until then, local development works fine with just `localhost:3000`.
