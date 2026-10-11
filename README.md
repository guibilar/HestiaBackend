# Hestia backend

Hestia is a personal "everything app" for the home lab. This repository holds its backend:
one ASP.NET Core host, `Hestia.Api`, with all Hestia modules inside it (a modular monolith).

- The host is the gateway in front of the finance service, [PersonalFinanceInsightsBackend](https://github.com/guibilar/PersonalFinanceInsightsBackend).
  It signs users in (see [Sign-in](#sign-in)), and proxies the finance paths to it (see [Finance proxy](#finance-proxy)).
- Each new module (Documents, Tasks, People, Journal, Home, and others) lives in the host as a module.
- Modules send events to each other in process, and the host gets the finance events from RabbitMQ (see [Events](#events)).
- Clients reach the host only through Tailscale HTTPS and the frontend's nginx. Nothing is open to the internet.

.NET 10, EF Core, PostgreSQL.

## Layout

```
src/
  Hestia.Api/                       host: composition root, health checks, finance proxy (Gateway/)
  Hestia.Platform/                  shared kernel: base entity, paging, exceptions, error handler,
                                    health check, reverse proxy setup, IModule, module database setup
  Hestia.Contracts/                 contracts shared with other processes (finance events, Today cards), a NuGet package
  Modules/
    Identity/                       sign-in: users, sessions, the read-only token, /api/Auth (T03)
    Sample/
      Hestia.Modules.Sample/            internal: Domain, Application, Infrastructure, Endpoints
      Hestia.Modules.Sample.Contracts/  public: DTOs, events, query interfaces
tests/
  Hestia.Testing/                   shared test setup: test database, host factory (not a test project)
  Hestia.Architecture.Tests/        the module rules
  Hestia.Platform.Tests/
  Hestia.Api.Tests/
  Hestia.Modules.Identity.Tests/
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
- Each module maps its endpoints under its own prefix, for example `/api/sample`. (One exception: the Identity module
  keeps the finance sign-in path `/api/Auth`, so the clients do not change.)
- Every endpoint needs a signed-in user by default (see [Sign-in](#sign-in)). A module does nothing for this.
- A module calls finance only through `IFinanceClient` (D13, see [The shared finance client](#the-shared-finance-client)).

The host finds the modules when it starts: each `Hestia.Modules.<Name>.dll` next to it (see `ModuleLoader`).
The host project references each module project, so the build puts the module there.

`tests/Hestia.Architecture.Tests` enforces the rules in CI. It checks the project references in the `.csproj`
files, and the references in the built assemblies. It also checks the schema of each `DbContext`.

To add a module, follow [docs/new-module.md](docs/new-module.md).

## Run locally

You need the .NET 10 SDK and a PostgreSQL server on `localhost:5432` (user and password `postgres`,
as in `appsettings.Development.json`). In Development, the host applies the migrations when it starts.
It also makes the account `dev` with the password `development-only`, and uses a gateway signing key that is in
`appsettings.Development.json` (for development only).

```bash
dotnet run --project src/Hestia.Api        # http://localhost:5097
curl http://localhost:5097/health/ready
curl -c cookies -H 'Content-Type: application/json' -d '{"username":"dev","password":"development-only"}' \
  http://localhost:5097/api/Auth/login
curl -b cookies http://localhost:5097/api/sample/items
```

Endpoints now:

| Endpoint | Description |
| --- | --- |
| `GET /health/live` | The process answers. No sign-in. |
| `GET /health/ready` | The host can serve requests: each module can connect to its database, and finance answers its `/health/ready`. No sign-in. |
| `POST /api/Auth/login`, `logout`, `change-password`, `sign-out-others`, `GET /api/Auth/me` | Identity module: sign-in (see [Sign-in](#sign-in)). |
| `GET /api/sample/items?page=1&pageSize=20` | Sample module: a page of items. |
| `POST /api/sample/items` | Sample module: make an item, `{ "name": "Kettle" }`. |
| `GET /api/sample/activity` | Sample module: what its event consumer saw since the host started (`itemsCreated`, `lastItemName`). |
| `/api/Accounts`, `/api/Transaction`, and the other finance paths | Proxied to the finance backend, with a gateway token. |

All endpoints except the sign-in and the health checks need a signed-in user.

In Development, the host sends the finance paths to `http://localhost:5096` (the finance backend's `dotnet run` address).
Without a finance backend, `/health/ready` answers 503 and the finance paths answer 502. The modules still work.
A local finance backend must run in the auth mode `Gateway`, with the development public key (see its README).

## Sign-in

Decision D5, built in T03. The **Identity** module (`src/Modules/Identity/`, schema `identity`) signs users in for the
whole host. It took over the sign-in code of the finance backend. The clients do not change: the same
`/api/Auth/*` endpoints, the same answers, and the same `finance_session` cookie.

- **Session cookie** `finance_session`: HttpOnly, SameSite=Strict, `Secure` behind HTTPS. The cookie carries only a
  session key. The sessions are in the table `identity.UserSessions`, so sign-out, a password change, and "sign out
  other devices" end them on the server. A session lasts 14 days of inactivity ("remember me"), or until the browser
  closes.
- **Lockout**: 5 wrong passwords lock the account for 15 minutes. A user name that does not exist gets the same answers.
- **Rate limit**: 10 password tries per minute per client address (the address from nginx, see `ReverseProxy`).
- **Read-only token** (`Identity:ReadOnlyToken`, at least 32 characters): `Authorization: Bearer <token>` reads
  everything and changes nothing. It can call every GET, and the finance queries in `FinanceProxy:ReadOnlyQueries`.
  Other requests get 403.
- **Every endpoint needs a signed-in user**, unless it allows anonymous users (only the sign-in and the health checks).
  A module does nothing for this. In a module endpoint, the user is the normal `HttpContext.User`. A module endpoint
  that only reads, but uses POST, can be open to the read-only token: `.ReadOnlyQuery()` (`Hestia.Platform.Auth`).
- The data protection keys (they encrypt the cookie) are in the table `identity.DataProtectionKeys`, so sessions survive
  a new container.

Settings (section `Identity`):

| Setting | Docker (`.env`) | Description |
| --- | --- | --- |
| `InitialUser:Username`, `InitialUser:Password` | `HESTIA_USERNAME`, `HESTIA_PASSWORD` | Optional. The host makes this account when it starts, if it does not exist. The password needs 12 characters or more. |
| `InitialUser:ResetPassword` | `HESTIA_RESET_PASSWORD` | `true` sets the password again (and ends a lockout) at the next start. Remove it after use. |
| `ReadOnlyToken` | `HESTIA_READONLY_TOKEN` | Optional. Empty turns the read-only token off. |

### Move the account from finance (one time)

The account and its password hash move from the finance database with one command. You sign in with the same password.
Current sessions end, so each device signs in again once.

```bash
# From the HestiaBackend folder, with both projects running:
docker exec postgres-db psql -U postgres -d FinanceDb --no-align --tuples-only -c \
  'SELECT row_to_json(u) FROM (SELECT "Id", "UserName", "PasswordHash" FROM "AspNetUsers") u' \
  | docker compose run --rm -T hestia-api import-finance-user
```

The command prints one line for each account (`Imported the account ...`). It is safe to run it again: an account that
exists gets the password from finance. The command `dotnet Hestia.Api.dll import-finance-user` does the same outside Docker.

### The gateway token for finance

The host signs each request to finance with a short-lived token (a JWT, ES256, 60 seconds). Finance, in its auth mode
`Gateway`, accepts only this token, and only from the internal Docker network. The token has the user id (`sub`), the
user name (`name`), and the scope (`full`, or `read-only` for the read-only token). The code is in
`src/Hestia.Api/Gateway/GatewayTokens.cs`.

Only the host has the private key (`FinanceProxy:SigningKey`, `GATEWAY_SIGNING_KEY` in `.env`). Finance has the public
key (`Auth__Gateway__PublicKey` in the finance `.env`). Make the key pair once:

```bash
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out gateway.key
# Hestia .env, one line:
echo "GATEWAY_SIGNING_KEY=$(openssl pkcs8 -topk8 -nocrypt -in gateway.key -outform DER | base64 -w0)"
# Finance .env, one line:
echo "GATEWAY_PUBLIC_KEY=$(openssl pkey -in gateway.key -pubout -outform DER | base64 -w0)"
rm gateway.key
```

The host does not start without a valid signing key. To change the key, make a new pair, and set both values. Then
restart both services.

### The shared finance client

A module that needs finance data uses `IFinanceClient` (`Hestia.Platform.Finance`, D13). The host implements it: it
calls finance directly at `FinanceProxy:Address` (not through its own proxy), with the gateway token of the signed-in
user of the current request, and with the timeout `FinanceProxy:ClientTimeout` (10 seconds).

```csharp
public sealed class MoneyTiles(IFinanceClient finance)
{
    public Task<List<AccountDto>> AccountsAsync(CancellationToken cancellationToken) =>
        finance.GetAsync<List<AccountDto>>("/api/Accounts", cancellationToken);
}
```

A failed call throws `FinanceRequestException` (with the finance status, or none when finance does not answer).
If an endpoint does not catch it, the client gets 502. Use the client in a request only: outside a request, there is
no user, and the client refuses.

## Finance proxy

`Hestia.Api` sits between the frontend's nginx and the finance backend (D6). The code is in `src/Hestia.Api/Gateway/`
and uses [YARP](https://microsoft.github.io/reverse-proxy/).

```
client ──Tailscale HTTPS──> nginx (frontend) ──/api──> hestia-api:8080 ──finance paths──> personal-finance-api:8080
                                                            └── module paths (/api/sample, ...) stay in the host
```

The route table is in configuration, in the section `FinanceProxy` of `appsettings.json`:

| Setting | Default | Description |
| --- | --- | --- |
| `Address` | `http://personal-finance-api:8080` | The finance backend, without a path. Docker: `FINANCE_API_URL` in `.env`. |
| `Paths` | The finance controllers: `/api/Accounts`, `/api/Budget`, `/api/Categories`, `/api/CategoryRules`, `/api/FlowRules`, `/api/Reports`, `/api/Tags`, `/api/Transaction` | Each prefix matches itself and every path below it, in any letter case. `/api/Auth` is not here: the host signs users in (T03). |
| `ReadOnlyQueries` | The five finance POST endpoints that only read, for example `/api/Transaction/Filter` | The read-only token can call these (POST, exact path). Finance marks them `[ReadOnlyQuery]` and checks them again. |
| `SigningKey` | (none) | The private key of the gateway tokens. Required. See [The gateway token for finance](#the-gateway-token-for-finance). |
| `TokenLifetime` | `00:01:00` | How long a gateway token is valid (10 seconds to 10 minutes). |
| `ClientTimeout` | `00:00:10` | How long the shared finance client waits for an answer. |
| `HealthPath` | `/health/ready` | The finance endpoint that `/health/ready` of the host calls. |
| `HealthTimeout` | `00:00:05` | How long the readiness check waits for finance. |
| `ActivityTimeout` | `00:01:40` | How long a proxied request can stay idle before the host stops it. |

When the finance backend gets a new controller, add its path to `Paths`. When it gets a new read-only POST query, add
its path to `ReadOnlyQueries`. The host does not start with an invalid `FinanceProxy` section. When the host starts, it
logs a warning for each module endpoint on a finance path (the module gets these requests, not finance).

What the proxy keeps and changes:

- Only a signed-in user reaches finance. Without a session, the host answers 401 and sends nothing to finance.
- It keeps the method, the path, the query string, the body, the other cookies, and the `Host` header from nginx.
  It replaces the `Authorization` header with the gateway token, and it removes the session cookie (T03).
  The response (status, headers, `Set-Cookie`, body) comes back unchanged. The host does not add an error body to a
  finance response.
- It sets `X-Forwarded-For` and `X-Forwarded-Proto` to the client address and the scheme that the host found itself.
  The host believes these headers only from the proxies in `ReverseProxy:TrustedNetworks`, so a client cannot fake them.
  Finance sees the same client and scheme as before, so its rate limit per address and its `Secure` session cookie still work.
- A module endpoint takes priority over the proxy on the same path (the finance routes have endpoint order 1000).
  Module paths never go to finance. Each module uses its own prefix, for example `/api/documents`.
- If finance does not answer, a proxied request gets 502, and `/health/ready` answers 503.
  `/health/live` does not check finance, so a finance outage does not restart the host.

The tests use `FakeFinance` (in `tests/Hestia.Testing`): a small finance stand-in on a real local port, which records
each request as finance would see it.

## Events

Decision D7, built in T04 with [Wolverine](https://wolverinefx.net) (MIT licence). There are two paths. A consumer gets
an event in the same way on both, and does not know which path it used.

```
module A ──RaiseEvent──> DbContext ──SaveChanges / commit──> post-commit dispatcher ──in process──> consumers
finance ──outbox (same transaction)──> RabbitMQ exchange "hestia.finance-events" ──queue "hestia.api.finance-events"──> consumers
```

**Module events (in process).** A module raises an event on its own DbContext, and saves:

```csharp
db.Items.Add(item);
db.RaiseEvent(new SampleItemCreated(item.Id, item.Name, item.CreatedAt));
await db.SaveChangesAsync(cancellationToken);
```

The platform (`Hestia.Platform/Events/ModuleEventsInterceptor.cs`) publishes the event only after the change is committed:
after `SaveChanges`, or, in an explicit transaction, when the transaction commits. A failed save or a rollback publishes
nothing. This path is not durable: if the process stops between the commit and the publish, the event is lost.

**Finance events (RabbitMQ).** The finance service writes each event to its outbox in the same transaction as the change,
and sends it to the fanout exchange `hestia.finance-events` after the commit. The host binds its queue
`hestia.api.finance-events` to that exchange and acknowledges a message only after its consumers finish. A consumer that
fails gets the Wolverine retries, then the message goes to the dead-letter queue. A consumer can get an event more than
once, so make each consumer idempotent. The events are in `Hestia.Contracts/Finance/FinanceEvents.cs`:
`TransactionsImported`, `TransactionChanged`, `BudgetLimitReached`, `BalanceRecorded`, and `SubscriptionPriceChanged`.

**A consumer** is a Wolverine handler in a module project: a public class whose name ends in `Consumer`, with a public
`Consume` method that takes the event first. Other parameters come from the service container. The host finds the
consumers in each module assembly. The template is `SampleItemCreatedConsumer`.

```csharp
public sealed class TransactionsImportedConsumer(ILogger<TransactionsImportedConsumer> logger)
{
    public void Consume(TransactionsImported message) => logger.LogInformation("{Added} new transactions", message.Added);
}
```

Settings (section `Events`):

| Setting | Default | Description |
| --- | --- | --- |
| `RabbitMq` | empty | The broker, for example `amqp://hestia:secret@rabbitmq:5672`. Docker: `EVENTS_RABBITMQ_URL` in `.env`. Empty: no finance events; the module events still work. |
| `LogAll` | `false` (`true` in Development) | A debug consumer logs every event (finance and module events) with all its values. The host reads it for each event. Docker: `EVENTS_LOG_ALL`. |

The broker runs in the finance project (its `events` profile), on the network `finance`. If the broker is down when the
host starts, the host starts without the finance events and logs an error: restart the host when the broker is back.
Until a module consumes a finance event, the host takes it from the queue and drops it (or logs it, with `LogAll`).

### The contracts package

The finance service uses the events from the NuGet package `Hestia.Contracts`, on GitHub Packages
(`https://nuget.pkg.github.com/guibilar/index.json`). The CI job `contracts-package` publishes it:

- On `main`: the version in `src/Hestia.Contracts/Hestia.Contracts.csproj` (`VersionPrefix`), once. A version that exists is skipped.
- On a pull request that changes `src/Hestia.Contracts`: a prerelease, `<version>-pr.<number>.<run>`, for a finance
  pull request that needs the new contracts before this one merges.

When you change a contract, increase `VersionPrefix`, and follow the rules in `IIntegrationEvent`: add a property with a
default value, or make a new event type. `Hestia.Contracts` must not reference other packages or projects (the
architecture tests check this).

## Test

```bash
dotnet test                                     # in-memory database (fast)
HESTIA_TEST_POSTGRES=container HESTIA_TEST_RABBITMQ=container dotnet test   # PostgreSQL and RabbitMQ in Docker (Testcontainers)
```

`HESTIA_TEST_POSTGRES` can also be the connection string of a PostgreSQL server that you run yourself.
Each test host gets its own database, and the tests delete it at the end. `HESTIA_TEST_RABBITMQ` can also be the
`amqp://` address of a broker that you run yourself. Without it, the RabbitMQ tests are skipped.
Each `HestiaApiFactory` records the events that reach its consumers, for every event type (`factory.Events`).
Each `HestiaApiFactory` signs every request in as a test user with full access (`TestSignIn`), so module tests do not
sign in. The header `X-Test-Read-Only` makes a request read-only. `new HestiaApiFactory(signedIn: false)` uses the real
sign-in (see `tests/Hestia.Modules.Identity.Tests`). `FakeFinance` records the requests that reach finance, and
`GatewayToken.Read(request)` checks their gateway token as finance does.

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

`docker-compose.yml` starts the host (`hestia-api`), its PostgreSQL (`hestia-db`, database `Hestia`), and the `backup` service ([Backups](#backups)).
The host joins the external network `finance`, which the finance backend's compose project makes.
Start the finance project first. The host reaches finance there as `http://personal-finance-api:8080`.

```bash
cp .env.example .env          # set POSTGRES_PASSWORD and GATEWAY_SIGNING_KEY
docker compose up -d --build
curl http://localhost:5097/health/ready
HESTIA_USERNAME=... HESTIA_PASSWORD=... docker/smoke-test.sh http://localhost:5097
```

The clients reach the host through the frontend's nginx: `API_UPSTREAM=http://hestia-api:8080` in the frontend's
`.env`. From T03, the host signs users in, and finance (auth mode `Gateway`) accepts only requests from the host.
To go back to finance alone: remove `API_UPSTREAM` from the frontend's `.env`, set `Auth__Mode` back to `Cookie` in
finance (see its README), and restart both (`docker compose up -d`).

`docker/smoke-test.sh` checks a running host end to end: the health checks, sign-in, the session cookie, a module call,
a finance call, sign-out, and 401 without a session. The account comes from `HESTIA_USERNAME` and `HESTIA_PASSWORD`.

CI does the same with a small nginx stand-in for finance (`docker/ci/finance-stub.conf`). It runs the smoke test, and
checks that a finance path reaches finance with its query string, `Host`, `X-Forwarded-Proto`, the other cookies, and a
gateway token, without the session cookie. It also starts a RabbitMQ broker on `finance`, and checks that a module event
reaches its consumer and that the host's queue is bound to the finance exchange.

### Backups

The `backup` service (container `hestia-backup`) dumps the `Hestia` database when the stack starts, and then every
night at `BACKUP_AT` (03:00, Toronto time, `TZ`). The dumps contain all module schemas. They use `pg_dump` 17, from the
same image as the database, in the custom format of `pg_restore`.

**Where the backup files are:** in the Docker volume `hestia_backups`, as `hestia-<date>_<time>.dump`.

- `docker compose exec backup ls -l /backups` lists them.
- `docker volume inspect hestia_backups` shows the folder on this machine (usually `/var/lib/docker/volumes/hestia_backups/_data`, readable by root only).
- `docker compose cp backup:/backups/hestia-2026-10-11_030000.dump .` copies one dump out of the volume.

Dumps older than `BACKUP_KEEP_DAYS` (14) are deleted after each successful dump. The files have mode 600 and belong to
the image's `postgres` user (uid 999). When the newest dump is older than 26 hours, `docker compose ps` shows the
container as unhealthy. `docker compose logs backup` tells you why.

To keep the dumps in a folder on this machine, not in the volume, set `BACKUP_DIR=./backups` in `.env`. Also set
`BACKUP_UID` and `BACKUP_GID` to your user (`id -u` and `id -g`), so the dumps belong to you.

**The dumps are sensitive.** Each dump is the whole database. After T03, it also holds the password hash and the
session data. A backup on the same disk does not survive a disk failure. Copy the dumps to a different place, and
encrypt the copy. The finance backend README shows a way with `age`.

```bash
docker compose exec backup backup.sh once      # make an extra dump now
```

#### Restore a backup

`docker/restore.sh` replaces the current database with a dump. All data since that dump is lost.

1. Go to the directory with `docker-compose.yml`. The stack must run (`docker compose up -d`).
2. Find the dump: `docker compose exec backup ls -l /backups`.
3. Run `docker/restore.sh hestia-2026-10-11_030000.dump` (a file name in the volume).
   For a dump on this machine (for example a decrypted copy), give its path: `docker/restore.sh ~/hestia.dump`.
4. Wait for `Restored ...`. Then check the host: `curl http://localhost:5097/health/ready`.

The script does these steps:

1. It saves the current database as `before-restore-<time>.dump` in the volume. The nightly clean-up does not
   delete these files. Delete them yourself when you do not need them.
2. It stops `hestia-api`.
3. It drops all schemas of the database, and restores the dump, in one transaction. If a step fails, the database
   stays as it was.
4. It starts `hestia-api` again. The host applies the migrations that are newer than the dump.

To undo a restore, restore the `before-restore-<time>.dump` file in the same way.

CI checks the backups in the Docker job (`docker/ci/backup-restore-check.sh`). It adds a Sample item, makes a dump,
restores the dump into an empty database, and checks the `sample` schema and the item. Then it runs `docker/restore.sh`
with a dump from the volume and with a file on the runner.
