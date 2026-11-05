# Tests

All tests live under this folder, grouped by kind:

- `unit/` – xUnit unit tests (normalization, hashing, comparison, risk, script generation)
- `integration/` – tests against LocalDB / SQL Server fixture databases
- `e2e/` – CLI and web end-to-end runs on the dev/prod/layered fixtures
- `performance/` – BenchmarkDotNet comparison against DacFx
- `fixtures/` – scripts that create the fixture databases
