# Add a new module

This checklist copies the `Sample` module into a new module. The example name is `Documents`.
Replace `Documents` and `documents` with the name of your module.

## 1. Copy the projects

1. Copy `src/Modules/Sample/` to `src/Modules/Documents/`.
2. Rename the two folders and the two `.csproj` files:
   - `Hestia.Modules.Documents/Hestia.Modules.Documents.csproj`
   - `Hestia.Modules.Documents.Contracts/Hestia.Modules.Documents.Contracts.csproj`
3. In the internal `.csproj`, change the `ProjectReference` to the new `.Contracts` project.
4. Replace `Sample` with `Documents` in the namespaces, the class names, and the file names.
5. Delete the Sample domain, endpoints, DTOs, and event. Keep the structure:
   `Domain/`, `Application/`, `Infrastructure/`, `Endpoints/`, and `DocumentsModule.cs`.
6. Delete the folder `Infrastructure/Migrations`. You make a new first migration in step 4.

## 2. Keep the module rules

- The internal project references only `Hestia.Platform`, `Hestia.Contracts`, and `.Contracts` projects.
- The `.Contracts` project references only `Hestia.Platform` and `Hestia.Contracts`.
- The `DbContext` derives from `ModuleDbContext`, with the schema `documents` (the lower-case module name).
- The module has exactly one `IModule` class. Its `Name` is `Documents`.
- The module has one registration call each: `services.AddDocumentsModule(config)` and `app.MapDocumentsModule()`.
- The endpoints use the prefix `/api/documents`.
- Other modules read your data only through query interfaces in your `.Contracts` project.
- Every endpoint needs a signed-in user. You do not add anything for this. Do not use `AllowAnonymous()`.
  The user is `HttpContext.User` (a `ClaimsPrincipal` parameter in a minimal API).
- A read-only user (the read-only token) can call GET endpoints only. If a POST endpoint only reads (for example a
  search with its criteria in the body), add `.ReadOnlyQuery()` (`Hestia.Platform.Auth`).
- To read finance data, use `IFinanceClient` (`Hestia.Platform.Finance`). Do not call the host's own proxy, and do not
  read the finance database.
- In background code (a consumer, a timer, a host command), there is no user. Use
  `finance.AsSystem("<Module>.<Class>")`: read-only by default. For a write, pass `FinanceAccess.Full` on purpose.
  Never call `AsSystem` from an endpoint: the client refuses it inside an HTTP request.

The architecture tests check these rules. If a test fails, change the code, not the test.

## 3. Add the module to the host and the build

1. In `src/Hestia.Api/Hestia.Api.csproj`, add a `ProjectReference` to the internal project, in the `Modules` group.
   The host then finds the module when it starts. No other host change is necessary.
2. In `Hestia.slnx`, add a folder `/src/Modules/Documents/` with the two projects, and add the test project.
3. In `Dockerfile`, add the two `COPY` lines for the two `.csproj` files.

## 4. Make the first migration

```bash
dotnet tool restore
dotnet ef migrations add InitialCreate \
  --project src/Modules/Documents/Hestia.Modules.Documents \
  --startup-project src/Hestia.Api \
  --context DocumentsDbContext \
  --output-dir Infrastructure/Migrations
```

Make sure that the migration starts with `EnsureSchema(name: "documents")`, and that all tables are in that schema.
`scripts/check-migrations.sh` finds the new module by its folder. It needs the name `DocumentsDbContext`.

## 5. Add tests

1. Copy `tests/Hestia.Modules.Sample.Tests/` to `tests/Hestia.Modules.Documents.Tests/`, and rename the project.
2. Keep `TestDatabaseFixture.cs`. It starts the test database for the test assembly.
3. Use `HestiaApiFactory` (in `tests/Hestia.Testing`) to run the full host with all modules. It signs every request
   in as a test user. Add one test that a request without sign-in gets 401: `new HestiaApiFactory(signedIn: false)`.
4. Run the tests on both databases:

```bash
dotnet test
HESTIA_TEST_POSTGRES=container dotnet test
```

## 6. Events (when the module has them)

A module event tells other modules that something changed (D7). See the README, section "Events".

1. Put the event in the `.Contracts` project: a `sealed record` that implements `IIntegrationEvent`, with these two
   properties. Example: `SampleItemCreated`.

   ```csharp
   public int Version { get; init; } = 1;
   public Guid EventId { get; init; } = Guid.CreateVersion7();
   ```

   Do not set `EventId` yourself: each new event object gets a new one.
2. Raise it on the module's DbContext before the save: `db.RaiseEvent(new DocumentAdded(...))`. The host publishes it
   after the commit, and never when the save fails or the transaction rolls back.
3. **Choose the mode.** By default, the event goes to the consumers in memory after the commit. If the process stops
   between the commit and the publish, the event is lost. When a lost event leaves other data wrong (for example, the
   timeline or the search index), use **durable events** (T38):
   1. In `AddDocumentsModule`, register the DbContext with the outbox:
      `services.AddModuleDbContext<DocumentsDbContext>(DocumentsDbContext.SchemaName, database => database.UseDurableEvents());`
   2. In `OnModelCreating`, call `modelBuilder.AddEventOutbox();` (namespace `Hestia.Platform.Events`). Then add a
      migration (step 4): it adds the table `EventOutbox` to your schema.
   3. Change nothing else. `RaiseEvent` and the save stay the same. The save stores the event in the outbox with the
      change, and the host's outbox sender delivers it, also after a restart. Do not read or write the outbox rows.
   4. Test it like the Sample module: `tests/Hestia.Modules.Sample.Tests/DurableEventTests.cs`. To test a restart, stop
      the sender (`Events:Outbox:Enabled` = `false`), then use `factory.Restarted(...)`.
4. Consume an event (from your module, another module, or finance) with a public class whose name ends in `Consumer`,
   with a public `Consume(TEvent message, CancellationToken cancellationToken)` method. Example:
   `SampleItemCreatedConsumer`.
5. **Make each consumer idempotent.** An event can come more than once (RabbitMQ delivers again after a failure), and
   the consumer must change the data only once (T36). Use the module's event inbox:
   1. In `OnModelCreating` of your DbContext, call `modelBuilder.AddEventInbox();` (namespace `Hestia.Platform.Events`).
      Then add a migration (step 4): it adds the table `EventInbox` to your schema.
   2. Take your module's DbContext in the consumer. Give the consumer a stable inbox name, `"<Module>.<Class>"`.
      Do not change the name later: a consumer with a new name handles old events again.
   3. Do the three steps below. The change and the inbox row commit together in one save, or neither commits.

   ```csharp
   public sealed class DocumentAddedConsumer(DocumentsDbContext db)
   {
       public const string InboxName = "Documents.DocumentAddedConsumer";

       public async Task Consume(DocumentAdded message, CancellationToken cancellationToken)
       {
           if (!await db.TryReceiveAsync(message.EventId, InboxName, cancellationToken))
           {
               return; // Handled already.
           }
           db.Index.Add(new IndexEntry { /* ... */ });
           await db.SaveChangesAsync(cancellationToken);
       }
   }
   ```

   - Save once. If the consumer must save more than once, put all the saves in one transaction with the execution
     strategy (`db.Database.CreateExecutionStrategy()`), and call `TryReceiveAsync` inside it.
   - A consumer that changes no data in your schema (for example, it only logs) needs no inbox.
   - A consumer that calls another system (for example a write to finance) cannot make that call part of the save.
     Make the call idempotent too (for example, send the `EventId` with it), or save first and call after.
   - Two deliveries at the same time: one save fails on the inbox key, and its change does not commit. Wolverine
     handles the failure as any consumer error (a retry finds the inbox row and does nothing). The other delivery's
     change stays, so the data changes once.
6. In tests, each `HestiaApiFactory` records what the consumers get: `await factory.Events.WaitForAsync<DocumentAdded>()`.
   Add a test that delivers the same event twice and checks that the data changed once. Example:
   `tests/Hestia.Modules.Sample.Tests/IdempotentConsumerTests.cs`.

## 7. Cards on Today (when the module has them)

The Today screen shows cards from all modules (T08, see the README, section "Today").

1. In the internal `.csproj`, add a `ProjectReference` to `Hestia.Modules.Today.Contracts`.
2. Add a class that implements `ITodayCardProvider`: `Module` is your module name, `Order` is 100 or more (Money is 0).
   Return `TodayCard` items: `Type` is `Tile` (a headline number) or `Attention` (a "Needs attention" item). Use your
   module name in `Module`, and start each `Id` with the lower-case module name, for example `documents.expiring.<id>`.
3. Do not format amounts or dates in a text. Put a placeholder in the text (`{amount}`) and its value in `Values`
   (`TodayValue.Money(...)`, `TodayValue.Day(...)`): the clients format them.
4. Register it in `AddServices`: `services.AddScoped<ITodayCardProvider, DocumentsTodayCards>();`.
5. The provider runs inside the request of the user. A provider that throws gives no cards, and Today shows the other
   modules. If only a part of your data loads, return the cards that you have with `Complete: false`.
6. Test it through `GET /api/today` with `HestiaApiFactory`.

## 8. Tips from the Documents module (T11)

- **A model part for PostgreSQL only** (for example a `tsvector` column): check the provider in `OnModelCreating`
  with `if (Database.IsNpgsql()) { ... } else { entity.Ignore(...); }`. The migrations and the architecture tests use
  PostgreSQL; the in-memory tests get the other model. Give the in-memory database a simple fallback in the query.
- **A new child row** (for example a link of a document) with an id that the code sets: add it with its own `DbSet`
  (`db.Links.Add(link)`), not to the collection of a tracked parent. EF Core thinks that a child with a key value
  exists already, and tries an update.
- **A file upload:** read the form with `request.ReadFormAsync(...)` from an `HttpRequest` parameter. Form binding
  (`IFormFile` parameters) makes the endpoint ask for an antiforgery token. Set the request size limit in the endpoint
  (`IHttpMaxRequestBodySizeFeature`): Kestrel allows 30 MB by default.
- **Background work in a module:** a `BackgroundService` (`services.AddHostedService<...>()`) with a new service scope
  for each unit of work. Keep the state of the work in the module's tables (for example a status column), so a
  restart loses nothing.
- **Today tests:** a new `ITodayCardProvider` adds a module to every `GET /api/today`. The Today tests leave out the
  providers of other modules; test your provider in your module's tests, and read only your module's cards.
- **JSON with enums in tests:** the host reads enums by name only. Send a request body with
  `PutAsJsonAsync(url, body, HestiaApiFactory.Json)`.
- **Times in tests:** PostgreSQL keeps microseconds, .NET keeps 100 ns. Compare a time that went through the database
  with a time from the database, not with the value that the code made.

## 9. Check

- `dotnet build` has no warnings.
- `dotnet test` passes, with the architecture tests.
- `docker compose up -d --build` starts, and `/health/ready` answers 200.
