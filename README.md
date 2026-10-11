# Hestia backend

Hestia is a personal "everything app" for the home lab. This repository holds its backend:
one ASP.NET Core host, `Hestia.Api`, with all Hestia modules inside it (a modular monolith).

- The host will sign users in (T03) and proxy the finance service, [PersonalFinanceInsightsBackend](https://github.com/guibilar/PersonalFinanceInsightsBackend) (T02).
- Each new module (Documents, Tasks, People, Journal, Home, and others) lives in the host as a module.
- Clients reach the host only through Tailscale HTTPS and the frontend's nginx. Nothing is open to the internet.

.NET 10, EF Core, PostgreSQL.

## Layout

```
src/
  Hestia.Api/                       host: composition root, health checks
  Hestia.Platform/                  shared kernel: base entity, paging, exceptions, error handler,
                                    health check, reverse proxy setup, IModule, module database setup
  Hestia.Contracts/                 contracts shared with other processes (finance events, Today cards)
  Modules/
    Sample/
      Hestia.Modules.Sample/            internal: Domain, Application, Infrastructure, Endpoints
      Hestia.Modules.Sample.Contracts/  public: DTOs, events, query interfaces
tests/
  Hestia.Testing/                   shared test setup: test database, host factory (not a test project)
  Hestia.Architecture.Tests/        the module rules
  Hestia.Platform.Tests/
  Hestia.Api.Tests/
  Hestia.Modules.Sample.Tests/
```

Package versions are in `Directory.Packages.props`. Shared build settings (net10.0, nullable,
warnings as errors) are in `Directory.Build.props`.

## Modules

A module is two projects:

| Project | Contents | Can reference |
| --- | --- | --- |
| `Hestia.Modules.<Name>` | Internal: domain, application, infrastructure (DbContext, migrations), endpoints, the `IModule` class | `Hestia.Platform`, `Hestia.Contracts`, any `.Contracts` project |
| `Hestia.Modules.<Name>.Contracts` | Public: DTOs, events, query interfaces | `Hestia.Platform`, `Hestia.Contracts` |

The rules:

- A module never references the internal project of another module. It uses the other module's `.Contracts`.
- Each module has its own `DbContext` (a `ModuleDbContext`) and its own schema: the lower-case module name.
  The migration history of a module is in its schema too. No module reads another module's tables.
- Each module registers itself with one call (`services.AddSampleModule(config)`, `app.MapSampleModule()`),
  and has one `IModule` class that makes these calls.
- Each module maps its endpoints under its own prefix, for example `/api/sample`.

The host finds the modules when it starts: each `Hestia.Modules.<Name>.dll` next to it (see `ModuleLoader`).
The host project references each module project, so the build puts the module there.

`tests/Hestia.Architecture.Tests` enforces the rules in CI. It checks the project references in the `.csproj`
files, and the references in the built assemblies. It also checks the schema of each `DbContext`.

To add a module, follow [docs/new-module.md](docs/new-module.md).

## Run locally

You need the .NET 10 SDK and a PostgreSQL server on `localhost:5432` (user and password `postgres`,
as in `appsettings.Development.json`). In Development, the host applies the migrations when it starts.

```bash
dotnet run --project src/Hestia.Api        # http://localhost:5097
curl http://localhost:5097/health/ready
```

Endpoints now:

| Endpoint | Description |
| --- | --- |
| `GET /health/live` | The process answers. |
| `GET /health/ready` | The host can serve requests (each module can connect to its database). |
| `GET /api/sample/items?page=1&pageSize=20` | Sample module: a page of items. |
| `POST /api/sample/items` | Sample module: make an item, `{ "name": "Kettle" }`. |

There is no sign-in yet (T03). Keep the host on the internal Docker network only.

## Test

```bash
dotnet test                                     # in-memory database (fast)
HESTIA_TEST_POSTGRES=container dotnet test      # PostgreSQL in Docker (Testcontainers), migrated from scratch
```

`HESTIA_TEST_POSTGRES` can also be the connection string of a PostgreSQL server that you run yourself.
Each test host gets its own database, and the tests delete it at the end.

CI also runs these checks:

```bash
sh scripts/check-vulnerable-packages.sh                     # after dotnet restore
dotnet format style --verify-no-changes --severity warn
dotnet tool restore && sh scripts/check-migrations.sh       # after dotnet build
```

## Migrations

Each module has its own migrations, in `Infrastructure/Migrations` of its internal project:

```bash
dotnet tool restore
dotnet ef migrations add <Name> \
  --project src/Modules/Sample/Hestia.Modules.Sample \
  --startup-project src/Hestia.Api \
  --context SampleDbContext \
  --output-dir Infrastructure/Migrations
```

## Docker

`docker-compose.yml` starts the host (`hestia-api`) and its PostgreSQL (`hestia-db`, database `Hestia`).
The host joins the external network `finance`, which the finance backend's compose project makes.
Start the finance project first.

```bash
cp .env.example .env          # set POSTGRES_PASSWORD
docker compose up -d --build
curl http://localhost:5097/health/ready
```

The frontend's nginx will reach the host as `http://hestia-api:8080` (T02).
