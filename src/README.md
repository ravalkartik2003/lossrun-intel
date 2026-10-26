# src — build and test

.NET 10 solution `LossRun.sln` (backend, CLI, tests) and the npm workspace `web/` (workbench, metrics twin, UI kit).

```bash
dotnet build src/LossRun.sln
dotnet test src/LossRun.sln                                   # everything except latency budgets and opt-in Azure tests
dotnet test src/LossRun.Analytics.Tests --filter Category=Performance   # latency budgets, alone
cd src/web && npm ci && npm test                              # metrics twin + workbench unit tests
```

## Test categories

| Category | How it runs | Why |
|---|---|---|
| (none) | `dotnet test`: the default | Unit, contract and end-to-end tests. The SQL end-to-end tests (`SqlFact`) need SQL Server 2022 or later: see *SQL for the tests* below. Without any, they skip with the reason; they never fail for want of SQL |
| `Performance` (`[Trait("Category", "Performance")]`) | Left out by default (`VSTestTestCaseFilter` = `Category!=Performance` in `Directory.Build.props`); run with `--filter Category=Performance`, on its own | A latency budget (ADR 0011; p50 ≤ 50 ms for a 1,000-claim account, about 25 ms on an idle machine). With other test assemblies running in parallel, CPU contention pushes it past the budget |
| `Azure` (`[Trait("Azure", "true")]`) | Skipped unless `LOSSRUN_AZURE_TESTS=1`; they then need `LOSSRUN_AOAI_ENDPOINT`, `LOSSRUN_CU_ENDPOINT`, `LOSSRUN_STORAGE_BLOB_ENDPOINT` (from `terraform output`) and an `az login` with access | Real calls; they cost a few cents |

Any `--filter` replaces the default one, so add `&Category!=Performance` to a filter of your own if you want the default exclusion kept.

CI: `.github/workflows/dotnet.yml` runs the default tests (SQL in Docker), then the `Performance` category alone, and a second job that runs the SQL tests through `LOSSRUN_TEST_SQL` against a SQL Server service container, to prove that mode. `.github/workflows/web.yml` runs the web workspace.

## SQL for the tests

`SqlFixture` (`src/LossRun.Data.Tests`, also used by `LossRun.Orchestration.Tests`) picks the first of these that is available, so Docker is not required (brief 28). Ledger tables, temporal tables and the audit proof run the same way in every mode.

| Mode | How | Notes |
|---|---|---|
| 1. A connection string | `LOSSRUN_TEST_SQL="Server=<test server>;Database=master;Authentication=Active Directory Default;Encrypt=True"` (or a SQL login) | SQL Server 2022 or later, or Azure SQL: a server (or elastic pool) set aside for tests, never the one holding the working database. The login must be allowed to create databases (`dbmanager` on Azure SQL). Each test class gets a database `lossrun_tests_<run>_<name>`, deployed with the normal changesets and dropped at the end. **Cost:** on Azure SQL serverless a run creates and drops a handful of short-lived databases, each billed only for the minutes it exists, so a run costs cents; each create also takes up to a minute |
| 2. LocalDB (Windows) | An existing LocalDB instance of version 16 (SQL Server 2022) or later; nothing to set | Uses `MSSQLLocalDB` if it is version 16 or later, else any other existing instance that is. The tests never create or change LocalDB instances: with only older ones they skip with "LocalDB version too old for ledger tables (need 2022+); install SQL Server 2025 Express LocalDB or set LOSSRUN_TEST_SQL" (to make one yourself: `sqllocaldb create LossRunTests 17.0 -s`). Databases `lossrun_tests_<run>_<name>`, dropped at the end |
| 3. Docker | A running Docker engine | A SQL Server 2022 container per test run (Testcontainers), as before |
| — | none of the above | The SQL tests are skipped: "no SQL available: set LOSSRUN_TEST_SQL or install LocalDB 2022 or Docker" |

## Settings the tools need

No environment's resource names are built in. The CLI (`lossrun`), the simulator, `lossrun-db` and the web capture scripts read `LOSSRUN_*` variables. A missing variable fails with its name and the `terraform output` it comes from. See `docs/operations/runbook.md` §Deploy.
