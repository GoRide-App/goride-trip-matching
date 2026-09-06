# GoRide.ServiceTemplate

This is the **base walking skeleton** for every GoRide-trip microservice — it contains everything that should exist *before* any Sprint story work begins: a runnable ASP.NET Web API, a working Azure MySQL connection (ADO.NET, no ORM), CORS configured for the Vercel-hosted frontend, a Dockerfile, a basic CI pipeline, and a test project. No Kafka code yet — that comes later, per-service, once a story actually needs it.

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


