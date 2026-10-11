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
3. Use `HestiaApiFactory` (in `tests/Hestia.Testing`) to run the full host with all modules.
4. Run the tests on both databases:

```bash
dotnet test
HESTIA_TEST_POSTGRES=container dotnet test
```

## 6. Events (when the module has them)

A module event tells other modules that something changed (D7). See the README, section "Events".

1. Put the event in the `.Contracts` project: a `sealed record` that implements `IIntegrationEvent`, with
   `public int Version { get; init; } = 1;`. Example: `SampleItemCreated`.
2. Raise it on the module's DbContext before the save: `db.RaiseEvent(new DocumentAdded(...))`. The host publishes it
   after the commit, and never when the save fails or the transaction rolls back.
3. Consume an event (from your module, another module, or finance) with a public class whose name ends in `Consumer`,
   with a public `Consume(TEvent message)` method. Example: `SampleItemCreatedConsumer`. Make it idempotent: an event can
   come more than once.
4. In tests, `services.AddEventRecorder(recorder, typeof(DocumentAdded))` (in `tests/Hestia.Testing`) records what the
   consumers get. Use `recorder.WaitForAsync<DocumentAdded>()`.

## 7. Check

- `dotnet build` has no warnings.
- `dotnet test` passes, with the architecture tests.
- `docker compose up -d --build` starts, and `/health/ready` answers 200.
