# PrintQueue — Cloud Print Job Queue Service

A backend service for managing print jobs across registered printers, built with
**C# / ASP.NET Core 8** and **Entity Framework Core**. The job lifecycle is modelled as an
explicit state machine, submission is idempotent so client retries can't double-print, and
the whole thing ships with unit + integration tests and a GitHub Actions pipeline.

[![CI/CD](https://github.com/USERNAME/printqueue/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/USERNAME/printqueue/actions/workflows/ci-cd.yml)

---

## Why this design

**The job lifecycle is a state machine, not a mutable status field.**
`PrintJobStatus` transitions are declared in one table in `PrintJobStateMachine`:

```
Queued     -> Processing | Cancelled
Processing -> Completed  | Failed | Cancelled
Failed     -> Queued           (retry, bounded by PrintJob.MaxRetries = 3)
Completed  -> (terminal)
Cancelled  -> (terminal)
```

An illegal move (say `Queued -> Completed`, skipping the work) throws
`InvalidStateTransitionException` and surfaces as **409 Conflict** rather than silently
corrupting state. Because the rules live in a static table rather than scattered
`if` statements, they're unit-testable with no database and no HTTP.

**Submission is idempotent.**
Printing is not a safely repeatable operation — a client that times out and retries must not
produce a second physical copy. Callers pass an `Idempotency-Key` header; a replay returns
**200 OK** with the original job instead of **201 Created** with a new one. The key is unique
per `(PrinterId, IdempotencyKey)`, so the same key against a different printer is correctly
treated as a different job. A unique index enforces this at the database level, and the
service catches the resulting `DbUpdateException` to resolve the race when two concurrent
submits share a key.

**Errors are consistent.**
`ExceptionHandlingMiddleware` maps domain exceptions to RFC 7807 `ProblemDetails`, so
controllers contain no `try/catch` and every failure path returns the same shape.

---

## API

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/printers` | Register a printer → 201 |
| `GET` | `/api/printers` | List printers |
| `GET` | `/api/printers/{printerId}` | Get one printer |
| `PUT` | `/api/printers/{printerId}/online?isOnline=` | Mark online/offline → 204 |
| `POST` | `/api/jobs` | Submit a job → 201, or 200 on idempotent replay |
| `GET` | `/api/jobs/{jobId}` | Get a job, including `allowedNextStates` |
| `GET` | `/api/jobs?printerId=&status=` | List/filter jobs |
| `POST` | `/api/jobs/{jobId}/transitions` | Move to a new state → 409 if illegal |
| `DELETE` | `/api/jobs/{jobId}` | Cancel a job |
| `GET` | `/health` | Health check incl. database connectivity |

Swagger UI is served at `/swagger` in Development.

### Example

```bash
# Register a printer
curl -X POST http://localhost:5000/api/printers \
  -H 'Content-Type: application/json' \
  -d '{"name":"HP-LaserJet-01","location":"Floor 3 / Hyderabad"}'

# Submit a job — safe to retry thanks to the idempotency key
curl -X POST http://localhost:5000/api/jobs \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: payroll-2026-08-15' \
  -d '{"printerId":"<PRINTER_ID>","documentName":"payroll.pdf","pageCount":10}'

# Pick it up, then complete it
curl -X POST http://localhost:5000/api/jobs/<JOB_ID>/transitions \
  -H 'Content-Type: application/json' -d '{"status":"Processing"}'

curl -X POST http://localhost:5000/api/jobs/<JOB_ID>/transitions \
  -H 'Content-Type: application/json' -d '{"status":"Completed"}'
```

---

## Running locally

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet restore
dotnet build
dotnet test                                  # 54 tests
dotnet run --project src/PrintQueue.Api      # http://localhost:5000
```

The schema is created automatically at startup via `EnsureCreated()` against a local
SQLite file (`printqueue.db`). No database server needed.

---

## Tests

**54 tests, all passing.** Three layers:

| File | Layer | Covers |
|---|---|---|
| `PrintJobStateMachineTests` | Unit (pure) | Every legal and illegal transition, terminal states, retry bounding |
| `PrintJobServiceTests` | Service + in-memory DB | Idempotency, per-printer key scoping, offline rejection, filtering |
| `SqliteProviderTests` | Service + real SQLite | Query translation, ordering, DB-level unique index, persistence round-trip |
| `ApiIntegrationTests` | Full HTTP pipeline | Status codes, model validation, ProblemDetails, end-to-end lifecycle |

`SqliteProviderTests` exists for a specific reason. The queries originally ordered by a
`DateTimeOffset` column: EF Core's in-memory provider sorts that happily, but SQLite throws
`NotSupportedException`. The suite was green while `GET /api/jobs` returned 500 against the real
database. Timestamps are now `DateTime` (UTC), and anything that depends on query translation is
tested against the provider the app actually ships with.

Integration tests use `WebApplicationFactory<Program>` with the SQLite provider swapped for
EF Core InMemory, so they exercise real routing, model binding and middleware without
touching disk.

```bash
dotnet test --collect:"XPlat Code Coverage"
```

---

## Project layout

```
printqueue/
├── .github/workflows/ci-cd.yml       # build → test → publish → deploy
├── PrintQueue.sln
├── src/PrintQueue.Api/
│   ├── Program.cs                    # DI, logging, health checks, startup
│   ├── ExceptionHandlingMiddleware.cs
│   ├── Domain/
│   │   ├── PrintJobStatus.cs
│   │   ├── PrintJob.cs               # entity; transitions guarded
│   │   ├── PrintJobStateMachine.cs   # the lifecycle rules
│   │   └── Printer.cs
│   ├── Data/PrintQueueContext.cs     # EF Core model + unique idempotency index
│   ├── Contracts/Dtos.cs             # request/response records
│   ├── Services/PrintJobService.cs   # application logic
│   └── Controllers/                  # PrintersController, JobsController
└── tests/PrintQueue.Tests/
    ├── PrintJobStateMachineTests.cs
    ├── PrintJobServiceTests.cs
    ├── SqliteProviderTests.cs
    └── ApiIntegrationTests.cs
```

---

## CI/CD

`.github/workflows/ci-cd.yml` runs on every push and PR to `main`:

1. **Build & Test** — restore, build in Release, run all tests with coverage collection,
   upload results as an artifact.
2. **Deploy** — on pushes to `main` only, publishes and deploys to Azure App Service.

### Enabling deployment

The deploy job is inert until you configure two things in the repository
(**Settings → Secrets and variables → Actions**):

| Kind | Name | Value |
|---|---|---|
| Variable | `AZURE_WEBAPP_NAME` | Your App Service name |
| Secret | `AZURE_WEBAPP_PUBLISH_PROFILE` | Contents of the publish profile downloaded from the App Service **Overview → Get publish profile** |

Azure setup (free F1 tier):

```bash
az group create --name printqueue-rg --location centralindia
az appservice plan create --name printqueue-plan --resource-group printqueue-rg --sku F1 --is-linux
az webapp create --name <YOUR-APP-NAME> --resource-group printqueue-rg \
  --plan printqueue-plan --runtime "DOTNETCORE:8.0"
```

---

## Possible extensions

- Replace `EnsureCreated()` with EF Core migrations once the schema needs to evolve
- Background worker to drain `Queued` jobs instead of driving transitions over HTTP
- Azure Blob Storage for document payloads, with the job holding only a reference
- Optimistic concurrency (`rowversion`) on `PrintJob` for multi-worker pickup
- Per-printer queue depth limits and priority ordering

---
