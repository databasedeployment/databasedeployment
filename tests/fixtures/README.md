# Fixture databases

Scripted databases used by the integration and end-to-end tests. Every difference between a pair
of fixtures is intentional and documented, so a test can assert the exact comparison result.

| Script | Creates | Purpose |
|---|---|---|
| `dev.sql` | `SchemaDiff_Dev` | Source side of the basic pair |
| `prod.sql` | `SchemaDiff_Prod` | Target side of the basic pair |
| `layers.sql` | one database per run (`DbName`, `Variant`) | Layered warehouse (staging, core, mart) with risky table changes |
| `expected.md` | | Expected result of `dev.sql` against `prod.sql` |

## Running

The scripts need SQL Server 2016 or later. LocalDB works on Windows. On macOS and Linux use the
SQL Server container image.

```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -b -i tests/fixtures/dev.sql
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -b -i tests/fixtures/prod.sql
```

Each script drops and recreates its database, so it can be run again at any time.
