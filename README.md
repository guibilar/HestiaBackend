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
  Hestia.Contracts/                 contracts shared with other processes (finance events), a NuGet package
  Modules/
    Alerts/                         the "Needs attention" list of all modules: rules, store, /api/alerts (T15)
    Documents/                      documents: files on a volume, OCR, full-text search, expiry, links (T11)
    Home/                           rooms and the home inventory: values, warranties, links, CSV export (T22)
    People/                         people: contacts, reach-out reminders, birthdays, gift ideas, loans, links (T20)
    Identity/                       sign-in: users, sessions, the read-only token, /api/Auth (T03)
    Tasks/                          to-dos and repeated home tasks: due dates, completions, links, alerts (T19)
    Today/                          the Today screen: /api/today, the cards of all modules (T08)
    Sample/
      Hestia.Modules.Sample/            internal: Domain, Application, Infrastructure, Endpoints
      Hestia.Modules.Sample.Contracts/  public: DTOs, events, query interfaces
tests/
  Hestia.Testing/                   shared test setup: test database, host factory (not a test project)
  Hestia.Architecture.Tests/        the module rules
  Hestia.Platform.Tests/
  Hestia.Api.Tests/
  Hestia.Modules.Alerts.Tests/
  Hestia.Modules.Documents.Tests/   with small sample files (Samples/) for the upload and OCR tests
  Hestia.Modules.Home.Tests/
  Hestia.Modules.People.Tests/
  Hestia.Modules.Identity.Tests/
  Hestia.Modules.Sample.Tests/
  Hestia.Modules.Tasks.Tests/
  Hestia.Modules.Today.Tests/
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

**Time zone (T85).** One setting decides which day it is for all modules: `Hestia:TimeZone` (IANA, for example
`America/Edmonton`; appsettings: `America/Toronto`; Docker: `TZ` in `.env`). Use the same zone as finance
(`Reports:TimeZone`). A module setting (`Today:TimeZone`, `Alerts:TimeZone`, `Tasks:TimeZone`, `Home:TimeZone`,
`People:TimeZone`) still works as an override; the host logs the zone at start and a warning for each override that
differs.

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
| `GET /health/ready` | The host can serve requests: each module can connect to its database, finance answers its `/health/ready`, and the host can write to the documents folder (`documents-storage`, T59). JSON with the status of each check (see [Health checks](#health-checks)). No sign-in. |
| `POST /api/Auth/login`, `logout`, `change-password`, `sign-out-others`, `GET /api/Auth/me` | Identity module: sign-in (see [Sign-in](#sign-in)). |
| `GET /api/sample/items?page=1&pageSize=20` | Sample module: a page of items. |
| `POST /api/sample/items` | Sample module: make an item, `{ "name": "Kettle" }`. |
| `GET /api/sample/activity` | Sample module: what its event consumer saw since the host started (`itemsCreated`, `lastItemName`). |
| `/api/documents/...` | Documents module: upload, list and search, download, links (see [Documents](#documents)). |
| `GET /api/today`, `GET /api/alerts` | The Today screen (see [Today](#today)), and the open "Needs attention" items (see [Alerts](#alerts)). |
| `/api/Accounts`, `/api/Transaction`, and the other finance paths | Proxied to the finance backend, with a gateway token. |

All endpoints except the sign-in and the health checks need a signed-in user.

In Development, the host sends the finance paths to `http://localhost:5096` (the finance backend's `dotnet run` address).
Without a finance backend, `/health/ready` answers 503 and the finance paths answer 502. The modules still work.
A local finance backend must run in the auth mode `Gateway`, with the development public key (see its README).

### Health checks

`/health/live` runs no checks: the process answers 200. Docker uses it, so a database or finance outage does not
restart the container.

`/health/ready` runs the readiness checks (tag `HealthTags.Ready`) and answers JSON (T64):

```json
{"status":"Unhealthy","checks":[{"name":"database","status":"Healthy"},{"name":"documents-storage","status":"Healthy"},{"name":"finance","status":"Unhealthy"}]}
```

- `status` is the worst status of all checks: `Healthy`, `Degraded`, or `Unhealthy`. The checks are in name order.
- The status code: 200 for `Healthy` and `Degraded`, 503 for `Unhealthy`.
- The endpoint is open (no sign-in), so the answer has only the names and the statuses: no descriptions, exception
  messages, addresses, or paths. The log of the host has the reason: `docker compose logs hestia-api | grep -i "health check"`.

| Check | Unhealthy when |
| --- | --- |
| `database` | A module cannot connect to the database. |
| `finance` | Finance does not answer its `/health/ready` with a success status. |
| `documents-storage` | The documents folder is missing, or the host cannot write to it (T59). |

```bash
curl -s http://localhost:5097/health/ready | jq -r '.checks[] | select(.status != "Healthy") | "\(.name): \(.status)"'
```

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
- **The scope of the user** (T60): `GET /api/Auth/me` and `POST /api/Auth/login` answer
  `{ "username": "...", "scope": "full" }`. The scope is `full` for a cookie user, and `read-only` for the read-only
  token. It comes from the same rule as the authorization (`HestiaUser.ScopeOf`). The clients read it after the
  sign-in and hide their write actions for a `read-only` user. An old client ignores the field.
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
If an endpoint does not catch it, the client gets 502. Without `AsSystem`, use the client in a request only: outside
a request, there is no user, and the client refuses.

#### Background work: the system identity

An event consumer, a timer, or a host command has no signed-in user. It asks for the **system identity** explicitly
(T42):

```csharp
public sealed class BudgetAlertConsumer(IFinanceClient finance)
{
    public async Task Consume(BudgetLimitReached message, CancellationToken cancellationToken)
    {
        var budgets = await finance.AsSystem("Alerts.BudgetAlertConsumer")
            .GetAsync<List<BudgetDto>>("/api/Budget", cancellationToken);
        // ...
    }
}
```

- The gateway token has `sub` = `hestia-system`, `name` = `system:<caller>`, and the scope `read-only`.
- For a write, ask for it in code: `AsSystem("<caller>", FinanceAccess.Full)` (scope `full`).
- The caller name has 1 to 64 letters, digits, `.`, `-`, or `_`. The host logs each system call with it, at Debug level
  (category `Hestia.Api.Gateway.FinanceClient`).
- The system client refuses to work inside an HTTP request, also when the request has no user. An endpoint always calls
  finance as its signed-in user.
- Module events reach their consumers without the HTTP request that raised them, so a module event consumer can use
  `AsSystem` too.
- Finance needs no change: it sees a normal gateway token with a known scope.

## Today

The Today module (T08) gives the Today screen of both clients all its cards in one answer: `GET /api/today`. The card
logic is in one place, here, and not in the web and Android clients.

- **Tiles.** Each module that shows tiles registers an `ITodayCardProvider` (`Hestia.Modules.Today.Contracts`). The
  Today module calls all providers at the same time, inside the request of the user. See [docs/new-module.md](docs/new-module.md).
- **"Needs attention" items** come from the Alerts module (T15, see [Alerts](#alerts)): Today reads the open alerts of
  all modules (`IAlertQueries`) at the same time as the tiles. The card ids, texts, values, and links did not change
  with T15, so the clients need no change.
- **Finance (Money) comes first** (D11). Its tile provider is in the Today module, because finance is a separate
  service. It reads five finance reports with `IFinanceClient`, as the signed-in user, and never reads the finance
  database. The four tiles: spent this month (against a usual month by now, or against last month so far in January),
  left over this month, net worth and its change since last month, and budgets over their limit. The rules are the same
  as the web dashboard (`dashboard-stats.ts`).
- **Links.** Each card can have a `link`, a client route (D11). The clients keep the query string and the fragment.

  | Card | Link |
  | --- | --- |
  | Net worth tile | `/money/net-worth` |
  | Budgets tile, budget over its limit | `/money/budgets` |
  | No recent import | `/money/transactions#import` (Transactions, with the import open; T57) |
  | Statement to import (one account) | `/money/transactions?importAccount=<accountId>#import` (the import open, with that account selected; T63) |
  | Price change | `/money/subscriptions` |
  | Transactions that no rule sorts | `/money/rules#unsorted` |
  | Document that expires or expired | `/documents/<id>` |
- **Failures:** a provider that throws, or does not answer in `Today:ProviderTimeout`, gives no cards. Its module has
  the status `Failed`, and the answer is still 200. When one finance report does not load, its tile has no value and
  the text "Could not load this", and the module has the status `Partial`. The status of a module includes its alerts:
  `Complete` when the tiles and the last alert check are complete, `Failed` when both failed (then it has no cards),
  else `Partial`.
- **Cache:** a complete answer stays for `Today:CacheDuration` (30 seconds), for each user and scope.
  `GET /api/today?refresh=true` makes a new one (for pull to refresh), and also checks the alert rules first.
- **Formatting is the client's job.** A card text has placeholders, for example `{difference} under a usual month by
  now`, and the card's `values` give each one with its type (`Money`, `Count`, `Percent`, `Date`, `Text`). So the
  clients keep their own currency and date formats, and privacy mode hides the amounts.
- A read-only user can call it (a GET). Finance then gets the `read-only` scope.

| Setting (`Today` section) | Default | Description |
| --- | --- | --- |
| `TimeZone` | (shared) | An override of `Hestia:TimeZone` (see [Run locally](#run-locally)). Leave it empty. |
| `CacheDuration` | `00:00:30` | How long a complete answer stays for a user. `00:00:00` turns the cache off. |
| `ProviderTimeout` | `00:00:15` | How long the host waits for one module's cards. |

## Alerts

The Alerts module (T15, schema `alerts`) owns the "Needs attention" list of all modules. Today shows it, and the clients
can poll it (`GET /api/alerts`, for example for Android notifications later). The list is most severe first;
`GET /api/alerts?order=module` gives the Today order (module order, then the order of each module's rules; T74).

```
module rules (IAlertSource) ──check──> alert store (raise, update, resolve) ──> Today, GET /api/alerts
        ^ timer, refresh, finance events, module events
```

- **Sources.** Each module adds its rules as an `IAlertSource` (`Hestia.Modules.Alerts.Contracts`). A check returns the
  alerts that hold now, and the key prefixes that it looked at completely (`Covered`). Four sources now:
  - `Money.Finance`, in this module, because finance is a separate service. It reads four finance reports as the
    **system identity**, read-only (`AsSystem("Alerts.FinanceAlertSource")`, T42): budgets over their limit, no
    statement import for more than 7 days, accounts whose statements stop before last month, price changes of the last
    45 days, and transactions that no rule sorts. The rules are the same as the web dashboard (`attention.ts`) and the
    Android app (`core/logic/Attention.kt`). They were in the Today module until T15.
  - `Documents.Expiry` and `Documents.Files`, in the Documents module (see [Documents](#documents)).
  - `System.Health`, in the host (`Hestia.Api/Events/SystemHealthAlerts.cs`, T80; see [System alerts](#system-alerts-t80)).
- **The store.** One row per alert. The key is the Today card id (for example `money.budget-over.<budget id>`), so the
  same condition has one open alert. A check that finds a new key raises an alert; a known key with a new text, value,
  or severity is updated (`updatedAt`); an open key that the check covers but did not find is resolved (`resolvedAt`).
  When the condition comes back, it is a new alert with a new `alertId`. A report that does not load covers nothing,
  so its alerts stay as they are. Resolved alerts stay for `Alerts:KeepResolvedDays`, then the timer deletes them.
- **When the rules run.** Always in the background (`AlertsWorker`), never in the request of a user:
  - every `Alerts:CheckInterval` (15 minutes): for rules without an event, for example a document that starts to expire;
  - when Today or `GET /api/alerts` asks with `refresh=true`, or when a source has no complete check since the host
    started (for example the first request after a start). The request waits at most `Alerts:RefreshWait`, then gets
    the stored alerts;
  - after each finance event (`FinanceAlertsConsumer`). Several events before the check starts give one check.
    `BudgetLimitReached` at 100 % also raises its alert at once, from the event (with the inbox, T36);
  - after a module event, when the module asks for it (`IAlertChecks.CheckAsync`, for example `DocumentAlertsConsumer`).
- **`GET /api/alerts?refresh=false`** answers `{ checkedAt, modules, alerts }`. Each alert has the fields of a Today
  attention card (`id`, `module`, `type`, `kind`, `title`, `text`, `values`, `severity`, `link`), and `alertId`,
  `raisedAt`, and `updatedAt`. Most severe first, then newest first. A read-only user can call it (a GET).
- **Module status.** `Complete` when the last check of each source of the module was complete; `Partial` when a report
  did not load, or when the checks failed but the module has stored alerts (they can be old); `Failed` when no check
  worked since the host started and the module has no alerts.

| Setting (`Alerts` section) | Default | Description |
| --- | --- | --- |
| `TimeZone` | (shared) | An override of `Hestia:TimeZone`. Leave it empty. |
| `CheckInterval` | `00:15:00` | How often the host checks all rules without a request. |
| `RefreshWait` | `00:00:10` | How long a request for fresh alerts waits for the check. Keep it under `Today:ProviderTimeout`. |
| `KeepResolvedDays` | `30` | How long a resolved alert stays in the table. |

## Documents

The Documents module (T11, schema `documents`) keeps receipts, warranties, leases, tax slips, insurance policies, and
manuals. A document has details (title, kind, tags, issue and expiry dates, notes), one or more files, and links to
things outside the module (finance transactions and home items).

```
upload ──> files to the volume (by SHA-256) + rows and DocumentAdded (one save) ──> 201, ocrStatus Pending
                                                          OCR worker (background) ──> OcrText, Done or Failed, DocumentChanged
```

**Endpoints** (prefix `/api/documents`; a read-only user can call the GET endpoints only):

| Endpoint | Description |
| --- | --- |
| `POST /` | Upload, as `multipart/form-data`: `files` (one or more), `title` (default: the first file name), `kind` (`Receipt`, `Warranty`, `Lease`, `Tax`, `Insurance`, `Manual`, `Other`; default `Other`), `tags` (repeat the field, or use commas), `issuedOn` and `expiresOn` (`yyyy-MM-dd`), `notes`. Answers 201 at once, with `ocrStatus` `Pending`. |
| `GET /?kind=&tag=&q=&expiresFrom=&expiresTo=&expiring=&page=1&pageSize=20` | A page of documents, newest first. `q` is a full-text search (best match first). `expiring=soon`: from today (the host's time zone) to `Expiry:SoonDays` ahead (T86). |
| `GET /{id}` | One document, with its OCR text. |
| `PUT /{id}` | New details (all of them, JSON). The files stay. |
| `DELETE /{id}` | Deletes the document, its links, and its files. |
| `GET /{id}/files/{fileId}` | The file, as a download. `?inline=true` shows it in the browser. |
| `POST /{id}/ocr` | Runs the OCR again (202, `ocrStatus` `Pending`). |
| `POST /{id}/links`, `DELETE /{id}/links/{linkId}` | A link, `{ "kind": "Money.Transaction", "targetId": "<transaction id>" }`, `Home.Item` (T22), or `People.Person` (T93). |
| `POST /by-link/counts` | `{ kind, targetIds }` (at most 200): how many documents link to each (0 included), for an icon on transaction rows (T72). A read-only query: the read-only user can call it. |
| `GET /by-link?kind=Money.Transaction&id=<transaction id>` | The documents of one thing (for T14). |

```bash
curl -b cookies -F title="Kettle receipt" -F kind=Receipt -F tags=kitchen -F expiresOn=2028-10-13 \
  -F "files=@receipt.pdf" http://localhost:5097/api/documents
curl -b cookies 'http://localhost:5097/api/documents?q=kettle'
```

**Files (D9).** The files are on a volume (`Documents:StoragePath`; in Docker the volume `hestia_documents` at
`/data/documents`). Each file is at `<first two characters of its SHA-256>/<SHA-256>`, so the same content is stored
once, also for two documents. A delete removes a file only when no other document has the same content. An upload
writes to `tmp/` first. The host finds the type in the first bytes of each file (PDF, JPEG, PNG, HEIC, WebP) and never
trusts the type or the name that the client sends. Downloads send `X-Content-Type-Options: nosniff`.

**OCR (D3, D9).** A worker in the host (`OcrWorker`) reads the `Pending` documents, oldest first, one at a time. An
upload wakes it at once. The status is in the database, so a restart loses no work.

- PDF: `ocrmypdf --skip-text` adds a text layer to the pages without text (Tesseract), then `pdftotext` reads all
  the text. A PDF with its own text keeps it.
- JPEG, PNG, WebP: `tesseract`. HEIC: `heif-convert` (or `heif-dec`) makes a PNG first.
- The text of all files goes to `ocrText` (at most 200,000 characters). A failure sets `Failed` with the error of each
  file in `ocrError`, keeps the document, and keeps the text that was found (for example the text layer of an
  encrypted PDF). `POST /{id}/ocr` runs it again. The command `documents-retry-ocr` puts all `Failed` and `NotNeeded`
  documents back to `Pending`: `docker compose run --rm -T hestia-api documents-retry-ocr`, then restart the host.
- The image has the programs and English (`tesseract-ocr-eng`). Another language needs its package in the
  `Dockerfile`, and `DOCUMENTS_OCR_LANGUAGE` (for example `eng+fra`).

**Search.** A PostgreSQL full-text index (a generated `tsvector` column with a GIN index) has the title and the tags
(weight A), the notes (B), and the OCR text (C), each in the `english` configuration (stems: "warranties" finds
"warranty") and the `simple` configuration (names, numbers, other languages). `q` uses the web search syntax
(`"exact phrase"`, `-word`). The in-memory test database searches the plain text instead.

**Links.** A link has a kind and an id, both text, so later modules (Home inventory, People) can use the same table.
The first kind is `Money.Transaction`: the host asks finance with the signed-in user whether the transaction exists
(`POST /api/Transaction/Filter` with the transaction id, a read-only query). It never reads the finance database.
Finance down: 502. The second kind is `Home.Item` (T22): the host checks the item with the Home module
(`IHomeQueries`). When an item is deleted, `DocumentItemLinksConsumer` removes its links (with the inbox), and sends
`DocumentUnlinked` for each one; the documents stay. The third kind is `People.Person` (T93), checked with the People
module (`IPeopleQueries`); `DocumentPersonLinksConsumer` removes the links when the person is deleted.

**Events (durable, T38).** `DocumentAdded`, `DocumentChanged` (`Change`: `Details` or `OcrText`), `DocumentDeleted`,
`DocumentLinked`, and `DocumentUnlinked`, in `Hestia.Modules.Documents.Contracts`. A delete sends `DocumentUnlinked`
for each link first. Other modules read more with `IDocumentQueries` (`FindAsync`, `ListByLinkAsync`).

**Alerts (T15).** An alert for each document that expires in the next 30 days, or expired in the last 7 days
(`documents.expiring.<id>`, `documents.expired.<id>`, link `/documents/<id>`). Today shows them as "Needs attention"
items. The rules are an alert source in this module (`DocumentExpiryAlerts`). After `DocumentAdded`, `DocumentChanged`
(details), and `DocumentDeleted`, the consumer `DocumentAlertsConsumer` checks that one document. The timer of the
Alerts module checks all documents (a document starts to expire because time passes).
A second source (`DocumentFileAlerts`, T80) gives `documents.files-missing` from the weekly file check (see below).

| Setting (`Documents` section) | Default | Description |
| --- | --- | --- |
| `StoragePath` | (none) | The folder of the files. Required. Docker: `/data/documents` (the volume). A relative path is below the content root. |
| `MaxFileSizeMb` | `25` | The largest file. |
| `MaxUploadSizeMb` | `100` | The largest upload (all files). |
| `MaxFilesPerDocument` | `10` | The most files in one upload. |
| `AllowedContentTypes` | all five | A subset of `application/pdf`, `image/jpeg`, `image/png`, `image/heic`, `image/webp`. |
| `Ocr:Enabled` | `true` | `false`: new documents get `NotNeeded`, and the worker does not run. |
| `Ocr:Language` | `eng` | The Tesseract language(s). Docker: `DOCUMENTS_OCR_LANGUAGE`. |
| `Ocr:Timeout` | `00:10:00` | The longest OCR of one file. |
| `Ocr:PollInterval` | `00:01:00` | How often the worker looks for `Pending` documents without an upload signal. |
| `Expiry:SoonDays`, `Expiry:ExpiredDays` | `30`, `7` | The windows of the expiry alerts. `SoonDays` is also the window of `expiring=soon`. |
| `TimeZone` | `Hestia:TimeZone` | The IANA time zone of "today" for `expiring=soon` (T86). |
| `FileCheck:Enabled` | `true` | `false`: no weekly file check (the command still works). |
| `FileCheck:Interval` | `7.00:00:00` | The time between two file checks (at most 49 days). |
| `FileCheck:FirstDelay` | `00:10:00` | The time from the start of the host to the first file check. |

The frontend's nginx must let large uploads through: its `client_max_body_size` must be at least `MaxUploadSizeMb`.

**Readiness check `documents-storage` (T59).** `/health/ready` answers 503 when the folder in `Documents:StoragePath`
does not exist, or when the host cannot write and delete a small file in its `tmp/` folder. In Docker, the cause is a
missing volume, or a volume that belongs to another user (it must belong to uid 1654). `/health/live` does not run
this check, so a volume problem does not restart the container. The answer of `/health/ready` names the check that
failed; the log of the host has the reason (see [Health checks](#health-checks)).

**Unused files: `documents-clean-files` (T59).** A stop between a document delete and its file delete can leave a file
that no document uses. A stopped upload can leave a file in `tmp/`. The command finds these files:

- a document file (`ab/abcdef…`) that no `DocumentFiles` row uses;
- a file in `tmp/`;
- only when the file is older than one hour, so an upload in progress is safe. An upload of content that is stored
  already gives the file a new time.

Without `--delete`, it only prints the files. With `--delete`, it looks at each file again, then deletes it. It never
touches other files on the volume (it prints them as `skipped`). The exit code is 1 when a delete failed.

**Missing files: `--check-missing` (T64).** A `DocumentFiles` row whose file is not on the volume cannot download, and
its OCR fails. With `--check-missing`, the command does only this check: it prints each such row
(`missing   <key> (document <id>, file <id>)`), and deletes nothing. The exit code is 2 when it finds a missing file,
0 when it finds none. To fix it, restore the file from a backup (the `hestia-<time>.documents` folder next to a dump),
or delete the document. You cannot use `--check-missing` and `--delete` together.

```bash
docker compose run --rm -T hestia-api documents-clean-files                  # list the unused files
docker compose run --rm -T hestia-api documents-clean-files --delete         # delete them
docker compose run --rm -T hestia-api documents-clean-files --check-missing  # list the rows without a file
```

**The weekly file check (T64).** A background job in the host (`FileCheckWorker`) does both checks some minutes after
the start, and then every 7 days. It deletes nothing. It logs a warning:

- with the count and the bytes of the unused files: `Documents: 2 unused file(s) on the documents volume, 8423 bytes`;
- for each row whose file is missing, with the document id and the file id.

A clean volume gives one information line. To see the results: `docker compose logs hestia-api | grep "Documents:"`.

**Alert: missing files (T80).** When the last weekly check found rows whose file is missing, Today shows the alert
`documents.files-missing` (severity Bad) in "Needs attention", with the count, the day of the check, and the command
that lists the rows (`--check-missing`). The alert uses the stored result of the check: it does not read the volume
again. At each alert check (every 15 minutes), it looks again at only those rows and files, so the alert goes when you
restore the files or delete the documents. A new missing file shows after the next weekly check. The result is in
memory: after a restart, the open alert stays as it is until the first check (`FileCheck:FirstDelay`). The command
`documents-clean-files` runs in its own process, so its result does not change the alert.

The host can run at the same time. Make a backup first if you are not sure (`docker compose exec backup backup.sh once`).

**Errors.** A request body that cannot be read (for example `"kind": "Spaceship"`, or JSON that is not complete) gets
400 with problem details, in Development and in Production. A body over the size limit of Kestrel gets 413, and a body
with the wrong content type gets 415 (T59).

## Tasks

The Tasks module (T19, schema `tasks`) keeps to-dos and repeated home tasks, for example "Water the plants, every 4
days" or "Smoke detector batteries, every 12 months". A task has a title, notes, a due date (optional), tags, a manual
order inside a day, an optional repeat, completions, and links (to documents and finance transactions).

**Endpoints** (prefix `/api/tasks`; a read-only user can call the GET endpoints only):

| Endpoint | Description |
| --- | --- |
| `POST /` | A new task, JSON: `title` (required, at most 200 characters), `notes`, `dueOn` (`yyyy-MM-dd`), `tags`, `sortOrder`, `repeat` (`{ "every": 4, "unit": "Days", "mode": "FromCompletion" }`), `links` (`[{ "kind": "Documents.Document", "targetId": "<id>" }]`). Answers 201. |
| `GET /?status=&tag=&due=&q=&page=1&pageSize=20` | A page of tasks: by due date (tasks without one last), then `sortOrder`, then the oldest first; with `status=Done`, the last done first (T86). `status`: `Open` or `Done`. `due`: `today`, `overdue` (open and due before today), `upcoming` (the next 7 days after today), `later` (after those days, T86), `none` (no due date). `q`: words in the title, the notes, or the tags. |
| `GET /{id}` | One task, with its last 10 completions (newest first). |
| `PUT /{id}` | New details (all of them, JSON, as for `POST /` without `links`). The status, the completions, and the links stay. |
| `DELETE /{id}` | Deletes the task, its completions, and its links. |
| `POST /{id}/done` | Done. 409 when the task is done already. |
| `POST /{id}/undo` | Undoes the last done. 409 when there is nothing to undo. |
| `POST /{id}/links`, `DELETE /{id}/links/{linkId}` | A link, `{ "kind": "Documents.Document", "targetId": "<document id>" }` or `Money.Transaction`. |
| `GET /by-link?kind=Documents.Document&id=<document id>` | The tasks of one thing. |
| `GET /summary` | The counts of open tasks: `dueToday`, `overdue`, `upcoming` (7 days), with `date`. For the badges. |
| `GET /tags` | The tags in use, by name, with `count` (open and done tasks, T86). For a tag filter. |

```bash
curl -b cookies -H 'Content-Type: application/json' \
  -d '{"title":"Water the plants","repeat":{"every":4,"unit":"Days","mode":"FromCompletion"}}' http://localhost:5097/api/tasks
curl -b cookies -X POST http://localhost:5097/api/tasks/<id>/done
curl -b cookies 'http://localhost:5097/api/tasks?due=overdue'
```

**Days.** "Today", "overdue", and the day a task was done are in the shared time zone `Hestia:TimeZone` (Docker: from
`TZ`). There are no times of day.

**Done and repeat.**

- A task without a repeat: done sets `status` `Done` and `doneAt`.
- A repeated task always has a due date (without one, it is due today). Done keeps it `Open` and moves `dueOn`:
  - `FromDueDate` (the default): a fixed schedule from the due date, for example the 1st of each month. A task that is
    done late moves to the first date of the schedule after the day it was done, so it is not overdue again at once.
    Done early keeps the schedule.
  - `FromCompletion`: from the day it was done, for example "water the plants 4 days after the last time".
  - Months and years keep the day of the month: Jan 31 + 1 month is Feb 28 (Feb 29 in a leap year), and the next month
    is Mar 31 again. Feb 29 every year is Feb 28 in the other years.
- Each done adds a completion (the time, and the due date that was done), so the clients can show "last done"
  (`lastDoneAt`) and the history.
- Undo removes the last completion: a done task is open again, and a repeated task gets back the due date of that
  completion. Undo again removes the one before.

**Links.** As in Documents: a kind and an id, both text. `Documents.Document` is checked with the Documents module
(`IDocumentQueries`); `Money.Transaction` is checked with finance as the signed-in user (finance down: 502). When a
document is deleted, the consumer `DocumentDeletedConsumer` removes its links (with the inbox); the tasks stay.
`Home.Item` (T22) is checked with the Home module (`IHomeQueries`): a task with this link is maintenance of the item.
When an item is deleted, `TaskItemLinksConsumer` removes its links (with the inbox). `People.Person` (T93) is checked
with the People module (`IPeopleQueries`), for example "Call Marco"; `TaskPersonLinksConsumer` removes the links when
the person is deleted.

**Events (durable, T38).** `TaskCreated`, `TaskChanged` (`Change`: `Details` or `Links`), `TaskCompleted` (with the due
date that was done and, for a repeated task, `NextDueOn`), `TaskReopened`, and `TaskDeleted`, in
`Hestia.Modules.Tasks.Contracts`. Other modules read more with `ITaskQueries` (`FindAsync`, `ListByLinkAsync`).

**Today and alerts.**

- Tile `tasks.due-today` ("Tasks"): the count of open tasks due today, with the overdue count as `{overdue}` in the
  text (severity Warning when there are overdue tasks). Link `/tasks`.
- Alerts (source `Tasks.Due`): `tasks.overdue.<id>` (Warning, link `/tasks/<id>`) for each open task with a due date
  before today, with the title, the due date, and the days late. With more than 5 overdue tasks
  (`Tasks:MaxOverdueAlerts`), one alert `tasks.overdue-many` ("7 tasks are overdue", link `/tasks?due=overdue`) takes
  their place. After each task event, `TaskAlertsConsumer` asks for a check, so done, a new due date, and a delete
  resolve the alert at once. A task that becomes overdue because time passes gets its alert at the next check of the
  Alerts module (every 15 minutes).

| Setting (`Tasks` section) | Default | Description |
| --- | --- | --- |
| `TimeZone` | (shared) | An override of `Hestia:TimeZone`. Leave it empty. |
| `UpcomingDays` | `7` | The days after today that `due=upcoming` and the summary count (`due=later` is after them). |
| `MaxOverdueAlerts` | `5` | The most overdue tasks with an alert each. |

**Backups.** The `tasks` schema is in the Hestia database, so the backup job (T24) dumps it with the other modules. The
CI backup check restores a task with its completion (`docker/ci/backup-restore-check.sh`).

## Home

The Home module (T22, schema `home`) keeps the rooms of the home and the things in them: where each thing is, what it
cost, its serial number, and its warranty. An item links to its documents (receipt, manual, warranty), its purchase
(a finance transaction), and its maintenance (tasks). The list can go out as a CSV file for an insurance claim.

**Data.** A room has a name (unique in any letter case, at most 100 characters) and a sort order. The module starts with
no rooms. An item has a name (required, at most 200 characters), a room (optional), a category (free text, for example
`Appliance`), a brand, a model, a serial number, a quantity (default 1), a purchase date, a purchase price, a current
value, the last day of its warranty, notes, and tags. Amounts are plain decimals in the one app currency (the clients
show CAD), rounded to cents. **The prices are for one item.** The value of an item (`value`) is the current value, else
the purchase price, else 0, multiplied by the quantity. The totals and the export use it.

**Endpoints** (prefix `/api/home`; a read-only user can call the GET endpoints only, also the export):

| Endpoint | Description |
| --- | --- |
| `GET /rooms` | All rooms in list order, with `itemCount` and `totalValue`. |
| `POST /rooms` | A new room, JSON: `name`, `sortOrder` (optional; default: after the last room). 409 for a name that exists. |
| `PUT /rooms/{id}` | A new `name`, and `sortOrder` (optional; without it, the place stays). |
| `DELETE /rooms/{id}` | Deletes a room without items. A room with items gets 409. |
| `PUT /rooms/order` | `{ "roomIds": [ ... ] }`: all room ids, each one time, in the new order. |
| `POST /items` | A new item, JSON: `name` (required), `roomId`, `category`, `brand`, `model`, `serialNumber`, `quantity`, `purchasedOn`, `purchasePrice`, `currentValue`, `warrantyUntil`, `notes`, `tags`, `links` (`[{ "kind": "Documents.Document", "targetId": "<id>" }]`). Answers 201. |
| `GET /items?room=&category=&tag=&q=&warranty=&page=1&pageSize=20` | A page of items, by name (with `q`: best match first). `room`: a room id, or `none`. `category`: any letter case. `q`: full-text search. `warranty`: `active` (ends today or later), `expiring` (ends in the next 30 days), `expired` (ended before today). |
| `GET /items/{id}` | One item, with `links`, `documents` (from both sides of the link), and `maintenance` (the tasks, from both sides, by due date). |
| `PUT /items/{id}` | New details (all of them, as for `POST /items` without `links`). The links stay. |
| `DELETE /items/{id}` | Deletes the item and its links. Documents and tasks lose their links to it. |
| `POST /items/{id}/links`, `DELETE /items/{id}/links/{linkId}` | A link: `Documents.Document`, `Money.Transaction`, or `Tasks.Task`. |
| `GET /items/by-link?kind=Documents.Document&id=<document id>` | The items of one thing: the items that link to it, and the items that the document or the task links to. |
| `GET /items/export.csv?room=&category=&tag=&q=&warranty=` | The items (all, or the list filters) as CSV. |
| `GET /summary` | `date`, `itemCount`, `totalValue`, `rooms` (each room, then one entry with `roomId` null for the items without a room), `warrantiesEnding` (30 days). |
| `GET /maintenance` | All maintenance (T89): the open tasks linked to an item from either side (`taskId`, `title`, `dueOn`, the repeat, `lastDoneAt`, `itemId`, `itemName`, `roomName`), by due date. |
| `GET /categories` | The categories in use, by name, with `count` (T89). For a category picker. |

```bash
curl -b cookies -H 'Content-Type: application/json' -d '{"name":"Kitchen"}' http://localhost:5097/api/home/rooms
curl -b cookies -H 'Content-Type: application/json' \
  -d '{"name":"Dishwasher","roomId":"<room id>","brand":"Bosch","purchasePrice":899.99,"warrantyUntil":"2027-03-01"}' \
  http://localhost:5097/api/home/items
curl -b cookies -o inventory.csv http://localhost:5097/api/home/items/export.csv
```

**Search.** `q` is a full-text search (a generated `tsvector` column with a GIN index, as Documents) in the name, the
brand, the model, the serial number, the category, the notes, and the tags, with the web search syntax.

**Links.** As in Documents and Tasks: a kind and an id, both text. The host checks that the thing exists: a document
with `IDocumentQueries`, a task with `ITaskQueries`, a transaction with finance as the signed-in user (finance down:
502). Documents and Tasks have the new link kind `Home.Item`, checked with `IHomeQueries`. Each module removes its links
when the target is deleted, with the inbox:

- Home (`ItemLinkCleanupConsumer`): `DocumentDeleted` and `TaskDeleted`;
- Documents (`DocumentItemLinksConsumer`) and Tasks (`TaskItemLinksConsumer`): `ItemDeleted`.

**Maintenance** has no data of its own: a maintenance job is a (repeated) task with a `Home.Item` link, or a task that
the item links to. `GET /items/{id}` lists them in `maintenance`, with the next due date.

**CSV export.** One row per item: Room, Name, Category, Brand, Model, Serial number, Quantity, Purchase date, Purchase
price, Current value, Warranty until, Documents (the titles, with `; `). Rows by room (room order, items without a room
last), then by name. UTF-8 with a byte order mark, CRLF line ends, RFC 4180 quotes. Dates are `yyyy-MM-dd`, amounts
`0.00` with a point. A text that starts with `=`, `+`, `-`, or `@` gets an apostrophe first, so a spreadsheet does not run
it as a formula.

**Events (durable, T38).** `ItemAdded`, `ItemChanged` (`Change`: `Details` or `Links`), `ItemDeleted`, and `RoomChanged`
(`Change`: `Added`, `Renamed`, `Deleted`; a new order raises no event), in `Hestia.Modules.Home.Contracts`. Other modules
read items with `IHomeQueries` (`FindItemAsync`, `ListByLinkAsync`).

**Alerts.** Source `Home.Warranty`: `home.warranty-ending.<id>` (Neutral, link `/home/items/<id>`) for each item whose
warranty ends today or in the next 30 days, with the name, the day, and the days left. **No double alert:** an item has
no alert when one of its documents expires on the same day (the Documents alert covers it). `HomeAlertsConsumer` asks
for a check after each item event, after a document link to or from an item, and after new document details. Home has no
Today tile in v1.

| Setting (`Home` section) | Default | Description |
| --- | --- | --- |
| `TimeZone` | (shared) | An override of `Hestia:TimeZone`. Leave it empty. |
| `WarrantySoonDays` | `30` | The days of "ends soon": the alerts, `warranty=expiring`, and the summary. |

**Backups.** The `home` schema is in the Hestia database, so the backup job (T24) dumps it. The CI backup check restores
a home item with its room (`docker/ci/backup-restore-check.sh`).

## People

The People module (T20, schema `people`) is a small personal CRM: the people the user cares about, the contacts with
them, a reach-out reminder, birthdays, gift ideas, and things lent or borrowed. A person links to documents, tasks, home
items, and transactions.

**Data.** A person has a name (required, at most 200 characters), a nickname, a relationship (free text, for example
`Friend`), groups (as tags: lower case, `|a|b|`), a birthday (month and day, and the year when known), an email, a phone,
notes, and a reach-out reminder in days (`contactEveryDays`, 1 to 3650). A contact (`interaction`) has a day, a kind
(free text, for example `Call`), and a note; the latest contact is the person's `lastContactOn`. A gift idea has a title,
notes, a price, an occasion, and `givenOn` (empty: still an idea). A loan has a thing, a direction (`Lent` or
`Borrowed`), the day of the loan, a due day, and `returnedOn` (empty: open).

**Reminder.** With `contactEveryDays`, `contactDueOn` is the last contact (or, without one, the day the person was added)
plus the reminder; the person is `contactOverdue` after that day. **Birthdays:** February 29 is February 28 in other
years; `nextBirthday` is today or later.

**Endpoints** (prefix `/api/people`; a read-only user can call the GET endpoints only):

| Endpoint | Description |
| --- | --- |
| `GET /summary` | `date`, `peopleCount`, `reachOut` (overdue reminders, most overdue first), `upcoming` (birthdays in the next 30 days: `date`, `days`, `age`, `giftIdeas`), `openLoans`, `overdueLoans`. |
| `GET /groups` | The groups in use with `count`. |
| `GET /?group=&q=&sort=name\|contact&page=1&pageSize=20` | A page of people. `q`: words in the name, nickname, relationship, groups, email, phone, notes. `sort=contact`: reach-out day first. |
| `POST /`, `GET /{id}`, `PUT /{id}`, `DELETE /{id}` | A person (`PUT` replaces all details). `GET` has the latest 20 contacts, the gift ideas, the loans, the links, and `linkedFrom` (documents and tasks that link to the person, T93). |
| `GET /{id}/interactions`, `POST /{id}/interactions`, `PUT /interactions/{id}`, `DELETE /interactions/{id}` | Contacts (`occurredOn`, default today; not in the future). |
| `POST /{id}/gifts`, `PUT /gifts/{id}`, `DELETE /gifts/{id}` | Gift ideas. |
| `GET /loans?status=open\|returned\|all`, `POST /{id}/loans`, `PUT /loans/{id}`, `DELETE /loans/{id}` | Loans of all people. |
| `POST /{id}/links`, `DELETE /{id}/links/{linkId}`, `GET /by-link?kind=&id=` | Links: `Documents.Document`, `Money.Transaction`, `Tasks.Task`, `Home.Item`. |

```bash
curl -b cookies -H 'Content-Type: application/json' \
  -d '{"name":"Marco Silva","groups":["Friends"],"birthday":{"month":11,"day":2},"contactEveryDays":42}' \
  http://localhost:5097/api/people
curl -b cookies -H 'Content-Type: application/json' -d '{"kind":"Coffee"}' http://localhost:5097/api/people/<id>/interactions
```

**Links.** The host checks the target with `IDocumentQueries`, `ITaskQueries`, `IHomeQueries`, or finance (as the
signed-in user; finance down: 502). `PersonLinkCleanupConsumer` removes the links on `DocumentDeleted`, `TaskDeleted`,
and `ItemDeleted`, with the inbox. Documents and tasks can also link to a person (kind `People.Person`, T93): the person
page lists them in `linkedFrom`, and `GET /by-link` gives the people of a document or a task from both sides of the link.

**Events (durable, T38).** `PersonAdded`, `PersonChanged` (`Details` or `Links`), `PersonDeleted`, `InteractionChanged`,
`GiftChanged`, `LoanChanged` (`Change`: `Added`, `Changed`, `Deleted`), in `Hestia.Modules.People.Contracts`. Other
modules read people with `IPeopleQueries`.

**Alerts.** Source `People.Reminders`, all Neutral, link `/people/<id>`: `people.reach-out.<id>` (an overdue reminder),
`people.birthday.<id>` (today or in the next 7 days; values `person`, `date`, `days`, and `age` when the year is known),
and `people.loan-overdue.<loan id>` (an open loan after its due day). Each check looks at all people (a short list).
`PeopleAlertsConsumer` asks for a check after the person, contact, and loan events. No Today tile in v1.

| Setting (`People` section) | Default | Description |
| --- | --- | --- |
| `TimeZone` | (shared) | An override of `Hestia:TimeZone`. Leave it empty. |
| `BirthdayAlertDays` | `7` | A birthday in these days is an alert. |
| `UpcomingDays` | `30` | The birthdays in the summary. |

**Backups.** The `people` schema is in the Hestia database. The CI backup check restores a person with a contact.

## OpenAPI document

The host describes the module endpoints (not the finance paths) at `/openapi/v1.json`, with `/api/Auth/*`
(`CurrentUserDto` has `username` and `scope`). The Angular and Kotlin clients can generate their models from it. Like every endpoint, it needs a signed-in user, for example the read-only token:

```bash
curl -H "Authorization: Bearer $HESTIA_READONLY_TOKEN" http://localhost:5097/openapi/v1.json > hestia-openapi.json
```

**The document in the repository (T48).** `docs/openapi/hestia.json` is the same document, so the clients can read it
without a running host (the web app can check or generate its models from it, T50). The test `OpenApiDocumentTests`
fails in CI when the file is not current, for example after a change to a contract. To update it:

```bash
HESTIA_UPDATE_OPENAPI=1 dotnet test
git add docs/openapi/hestia.json
```

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
its path to `ReadOnlyQueries` (CI checks this list against finance's `docs/read-only-queries.txt` with
`scripts/check-finance-read-only-queries.sh`, T44). The host does not start with an invalid `FinanceProxy` section. When the host starts, it
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
module B (durable) ──RaiseEvent──> DbContext ──same transaction──> outbox table in its schema ──outbox sender──> consumers
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

**Durable module events (T38).** Every module with events uses them (T78; `DurableModuleEventsTests` checks it). Only
the Sample module keeps the in-memory path above, to show it. A module opts in when it registers its DbContext, and adds
the outbox table to its model:

```csharp
services.AddModuleDbContext<DocumentsDbContext>(DocumentsDbContext.SchemaName, database => database.UseDurableEvents());
// In OnModelCreating:
modelBuilder.AddEventOutbox();   // the table "EventOutbox" in the module's schema; then add a migration
```

The module code does not change: it calls `RaiseEvent` and saves. The save writes each event as a row of the module's
outbox (`<schema>.EventOutbox`), in the same transaction as the change. A failed save or a rollback stores nothing.
After the commit, the host's outbox sender (`Hestia.Api/Events/ModuleOutbox.cs`) delivers each row to all consumers of
the event (Wolverine `InvokeAsync`), and deletes the row when they finished.

- **Never lost after the commit:** a stop before the send keeps the row, and the next start sends it.
- **At least once:** a stop after the delivery and before the delete sends the event again. The consumers are
  idempotent (the inbox, below), so the data changes once.
- **Failures:** a consumer that throws keeps the row. The sender tries again after `RetryDelay`, doubled after each
  failure (up to 10 minutes). After `MaxAttempts` failures, it stops and keeps the row with its last error
  (`LastError`), and logs an error. The readiness check `events-outbox` is then Degraded. To send the row again, run
  `retry-outbox` (see [Event health](#event-health-t37)).
- **Order:** the sender sends the rows of a module in the order of the saves. A failed row does not stop the next ones,
  so a consumer must not depend on the order.
- A commit wakes the sender at once. It also reads the outboxes every `PollInterval`.
- Only the event types of the modules' `.Contracts` projects can go to an outbox. Another type fails the save.
- Each module reads its outbox only with its own DbContext. Module code never uses the outbox rows (the architecture
  tests check this).
- The Sample module shows both modes: the setting `Sample:DurableEvents` (default `false`) turns on its outbox.

Wolverine's own EF Core outbox is not used: it needs Wolverine's message storage in the database, with its own schema,
and does not fit one outbox per module schema. It also does not work with the in-memory test database.

**Finance events (RabbitMQ).** The finance service writes each event to its outbox in the same transaction as the change,
and sends it to the fanout exchange `hestia.finance-events` after the commit. The host binds its queue
`hestia.api.finance-events` to that exchange and acknowledges a message only after its consumers finish. A consumer that
fails gets the retries of the host (below), then the message goes to the dead-letter queue. A consumer can get an event
more than once, so make each consumer idempotent (see below). The events are in `Hestia.Contracts/Finance/FinanceEvents.cs`:
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

**Idempotent consumers (T36).** Each event has an `EventId` (`IIntegrationEvent`): a new GUID for each event, the same
on each delivery of that event. A consumer that changes data uses its module's event inbox, the table `EventInbox` in
the module's schema (event id, consumer name, handled at). `ModuleDbContext.TryReceiveAsync(eventId, consumer)` returns
false when the consumer handled the event already; else it adds the inbox row to the context, and the consumer's one
`SaveChangesAsync` commits the change and the row together. `SampleItemCreatedConsumer` is the template, and
`docs/new-module.md` (step 6) tells a module author what to do. Wolverine's own durable inbox is not used: it does not
cover the in-process module events, and its row is not in the same transaction as the module's change.

**Consumer retries (T52).** Without a policy, Wolverine 6 tries a message once, and the first failure moves it to the
error queue. The host sets this policy (`Hestia.Api/Events/HestiaEvents.cs`, `ConsumerRetries`):

- **A short database failure** (`DbUpdateException`, a transient PostgreSQL error, or the end of the EF Core execution
  strategy retries) gets 3 more tries in the same delivery, after 0.1 s, 0.5 s, and 2 s. This also covers two
  deliveries of one event at the same time: the save that fails on the inbox key tries again and finds the inbox row.
- **After the last try, and at once for all other errors** (a bug does not go away with a retry), Wolverine moves the
  message to the error queue and logs an error ("... was moved to the error queue"):
  - finance events (RabbitMQ): the broker's dead-letter queue `wolverine-dead-letter-queue`;
  - module events in memory (local queues): the host has no Wolverine message storage, so the message is dropped after
    the error log;
  - durable module events (the outbox sender, `InvokeAsync`): the sender gets the error, keeps the row, and tries again
    later (see above).
- A retry runs **all** consumers of the event again (Wolverine combines them in one handler), so each consumer that
  changes data uses the inbox. A test (`ConsumerInboxTests`) checks every consumer method in the modules: it calls
  `TryReceiveAsync`, gives an `AlertTrigger` to the Alerts module, or has `[ChangesNoData("why")]`.

An event from a publisher with an older `Hestia.Contracts` (before 1.1.0) has no `EventId` on the wire. The consumer
then gets a new id for each delivery, so the inbox cannot see a second delivery of that event. Update the publisher.

Settings (section `Events`):

| Setting | Default | Description |
| --- | --- | --- |
| `RabbitMq` | empty | The broker, for example `amqp://hestia:secret@rabbitmq:5672`. Docker: `EVENTS_RABBITMQ_URL` in `.env`. Empty: no finance events; the module events still work. |
| `LogAll` | `false` (`true` in Development) | A debug consumer logs every event (finance and module events) with all its values. The host reads it for each event. Docker: `EVENTS_LOG_ALL`. |
| `Outbox:Enabled` | `true` | The outbox sender of durable module events. `false` keeps the events in their outbox (for tests and diagnostics). Read at start. |
| `Outbox:PollInterval` | `00:00:05` | How often the sender reads the outboxes without a commit signal. |
| `Outbox:RetryDelay` | `00:00:05` | The wait after the first failed delivery of an event. It doubles after each failure, up to 10 minutes (a longer value stays as it is). |
| `Outbox:MaxAttempts` | `10` | After this many failed deliveries, the sender keeps the row and stops. |
| `Outbox:WarnAfter` | `00:10:00` | The readiness check `events-outbox` is Degraded when an outbox row is older than this (T37). |
| `Broker:StartTimeout` | `00:00:05` | How long the start waits for the broker. Then the host starts without it, and connects later (T37). |
| `Broker:ReconnectInterval` | `00:00:15` | How often the host tries the broker again, and starts a listener that does not run (T37). |
| `Broker:AlertAfter` | `00:30:00` | After this time without a broker connection, Today shows the alert `system.broker-down` (T80). |

The broker runs in the finance project (its `events` profile), on the network `finance`. Until a module consumes a
finance event, the host takes it from the queue and drops it (or logs it, with `LogAll`).

### Event health (T37)

**The broker is down when the host starts.** The host waits `Broker:StartTimeout` (5 seconds) for the broker, then
starts without the finance events and logs the error. Finance keeps its events in its outbox meanwhile. The
reconnector (`Hestia.Api/Events/BrokerConnection.cs`, `BrokerReconnector`) tries the broker every
`Broker:ReconnectInterval` (15 seconds). When the broker answers, it declares the exchange, the queue, and the binding,
and starts the listener. No restart is necessary. It logs `Events: the RabbitMQ broker is not reachable` once, and
`Events: connected to the RabbitMQ broker after the start of the host` when it connects.

**The broker stops later.** The RabbitMQ client recovers the connection itself when the broker is back, and Wolverine
rebuilds its channels. The messages stay in the queue of the host, and finance keeps new events in its outbox.

**Readiness checks.** Both are **Degraded**, never Unhealthy, so `/health/ready` stays 200 and the apps still work:

| Check | Degraded when |
| --- | --- |
| `events-broker` | The host has no connection to the broker, or its listener for the finance events does not run. Healthy when `RabbitMq` is empty. |
| `events-outbox` | A durable module outbox has a row that failed `Outbox:MaxAttempts` times, or a row older than `Outbox:WarnAfter`. The log line of the check names each schema and its counts. |

**Stuck outbox rows: `retry-outbox`.** The command gives the rows that failed `Outbox:MaxAttempts` times a new start
(`Attempts` 0, the next try now). The last error stays in `LastError`. The running host sends them at its next poll.
Fix the cause first (the error is in the log of the host, and in `LastError`).

```bash
docker compose run --rm -T hestia-api retry-outbox documents   # one module (its schema)
docker compose run --rm -T hestia-api retry-outbox all         # every module with durable events
```

It prints the count for each schema, for example `documents: 2 row(s) reset`. The exit code is 1 for an unknown schema.

### System alerts (T80)

The host also shows these problems on Today, in "Needs attention", so you do not have to read the log. They are the
alerts of the module `System` (alert source `System.Health`, place 900: after the modules of the user):

| Alert key | Severity | When | It goes when |
| --- | --- | --- | --- |
| `system.outbox-stuck.<schema>` | Bad | The outbox of a module has rows that failed `Outbox:MaxAttempts` times. The text has the count and the `retry-outbox <schema>` command. | No row of that outbox is at `Outbox:MaxAttempts` (for example after `retry-outbox`). |
| `system.broker-down` | Warning | The host has had no working connection to the broker for more than `Broker:AlertAfter` (30 minutes). The text has the day when the outage started. | The connection is back. |

- The reconnector reports the state of the connection after each of its checks (every `Broker:ReconnectInterval`).
  The time of the outage is in memory. After a restart of the host, the 30 minutes start again; an open alert stays
  until the connection is back.
- An outbox row that is only old (`Outbox:WarnAfter`) gives no alert: the readiness check `events-outbox` reports it.
- The alerts have no link. The clients show them with a generic icon.

### The contracts package

The finance service uses the events from the NuGet package `Hestia.Contracts`, on GitHub Packages
(`https://nuget.pkg.github.com/guibilar/index.json`). The CI job `contracts-package` publishes it:

- On `main`: the version in `src/Hestia.Contracts/Hestia.Contracts.csproj` (`VersionPrefix`), once. A version that exists is skipped.
- On a pull request that changes `src/Hestia.Contracts`: a prerelease, `<version>-pr.<number>.<run>`, for a finance
  pull request that needs the new contracts before this one merges. Every push to such a pull request publishes one
  (T51), also a push that does not touch the contracts; the job writes the version as a notice.

When you change a contract, increase `VersionPrefix`, and follow the rules in `IIntegrationEvent`: add a property with a
default value, or make a new event type. `Hestia.Contracts` must not reference other packages or projects (the
architecture tests check this).

## Test

```bash
dotnet test                                     # in-memory database (fast)
HESTIA_TEST_POSTGRES=container HESTIA_TEST_RABBITMQ=container dotnet test   # PostgreSQL and RabbitMQ in Docker (Testcontainers)
```

`HESTIA_TEST_POSTGRES` can also be the connection string of a PostgreSQL server that you run yourself.
The OCR tests of the Documents module run the real programs when `ocrmypdf`, `pdftotext`, and `tesseract` are on the
`PATH` (CI installs them); else they are skipped. The other Documents tests use a fake OCR.
Each test host gets its own database, and the tests delete it at the end. `HESTIA_TEST_RABBITMQ` can also be the
`amqp://` address of a broker that you run yourself. Without it, the RabbitMQ tests are skipped.
Each `HestiaApiFactory` records the events that reach its consumers, for every event type (`factory.Events`).
`factory.Restarted(settings)` makes a new host on the same database, to test a restart (T38).
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

The vulnerability check fails when a shipped package (direct or transitive) has an advisory of moderate severity
or worse. Test-only packages do not block. In CI, the check writes its findings as annotations on the run (T55):

- one error per vulnerable shipped package, with the name, the version, the severity, and the advisory link;
- one notice per vulnerable test-only package.

### Test results as annotations

The two CI test steps write TRX files (`--report-xunit-trx`, from xUnit v3, with no extra package).
After each test step, `scripts/test-annotations.py` reads them and writes annotations on the run:

- one notice per test project, with the totals, for example
  `Tests (in-memory): Hestia.Api.Tests — 120 passed, 0 failed, 2 skipped`;
- one error per failed test, with the test name, the test project, and the first six lines of the message;
- a warning if a test step wrote no TRX file (for example, a crash before the results).

The labels are `in-memory` and `postgres`. The step also runs when the tests fail.
GitHub shows at most 10 errors per step. After 9 failed tests, one more error gives the count of the others.
Task sessions cannot read the CI logs, but they can read these annotations (for example with
`gh api repos/guibilar/HestiaBackend/check-runs/<id>/annotations`).

To see the same output locally:

```bash
dotnet test --results-directory TestResults/local --report-xunit-trx
python3 scripts/test-annotations.py local TestResults/local
```

### CI artifacts

CI uploads no artifacts. Artifacts count against the storage quota of the GitHub account, and when the quota is
full, every upload fails. If a new step must upload one, set a short `retention-days` (7 for pull requests,
30 for `main`) and `continue-on-error: true`. Then add a step that writes a `::warning::` annotation when the
upload fails, so that a full quota does not hide the real result.

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
The document files are in the volume `hestia_documents` (T11). The image has the OCR programs (ocrmypdf, Tesseract with
English, pdftotext, and the HEIF tools).
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

**Document files (T11).** After each dump, the service copies the document files (the volume `hestia_documents`) into a
folder next to the dump, with the same name: `hestia-<date>_<time>.documents/`. A file that the previous copy has
already becomes a hard link to it (a file never changes: its name is its SHA-256), so each copy takes space only for
the new files. The copy comes after the dump: an upload stores its file before its row, and a delete removes the file
after its row, so the files of all rows in the dump are in the copy. The folders go away with their dumps after
`BACKUP_KEEP_DAYS`. One backup job for both keeps the rows and the files together: a separate file backup could
restore files from another day than the rows.

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

1. It saves the current database as `before-restore-<time>.dump` in the volume, with its document files
   (`before-restore-<time>.documents/`). The nightly clean-up does not delete these. Delete them yourself when you do
   not need them.
2. It stops `hestia-api`.
3. It drops all schemas of the database, and restores the dump, in one transaction. If a step fails, the database
   stays as it was.
4. T11: it puts back the document files from the `.documents` folder next to the dump (in the volume, or next to the
   file on this machine), removes the files that came after the dump, and gives the files back to the host's user
   (1654). Without that folder, it says so and does not touch the files.
5. It starts `hestia-api` again. The host applies the migrations that are newer than the dump.

To restore a copy from another place, copy both the `.dump` file and its `.documents` folder.

To undo a restore, restore the `before-restore-<time>.dump` file in the same way.

CI checks the backups in the Docker job (`docker/ci/backup-restore-check.sh`). It adds a Sample item and a document
with a file, makes a dump, restores the dump into an empty database, and checks the `sample` schema, the item, the
document, and the copy of its file. Then it runs `docker/restore.sh` with a dump from the volume and with a file (and
its `.documents` folder) on the runner, and checks that the document downloads its file and that the later document
and its file are gone.
