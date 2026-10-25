# SchemaDiff

SchemaDiff is a fast SQL Server schema comparison, deployment and migration tool. It answers *"what changed between two SQL Server databases?"* and turns the answer into a safe, ordered deployment script.

Instead of building a full semantic model like DacFx / SSDT Schema Compare, SchemaDiff extracts the system catalog in bulk, normalizes T-SQL with ScriptDom and compares objects through XxHash128 hashes, aiming at seconds instead of minutes on databases with 10,000+ objects.

## Features

- **Schema comparison** – bulk catalog extraction, hash-based diff with Add / Delete / Change classification and sub-change drill-down, rename detection
- **Deployment scripts** – dependency-ordered, transactional scripts with safe mode, rollback and bidirectional scripts
- **Risk analysis** – detects data-loss risks (column drops, type narrowing, NOT NULL without default…) using target row counts
- **Migrations** – versioned migration files with a history table and drift detection
- **Snapshots & reports** – offline comparison against saved snapshots, HTML/JSON reports
- **AI assistant** – optional plain-language summaries and risk explanations, using schema metadata only
- **CLI and local web interface**

## Tech Stack

- .NET 8 / C# 12, ASP.NET Core Minimal API + SSE
- Microsoft.Data.SqlClient, ScriptDom, System.IO.Hashing
- Vanilla JavaScript single-page web UI
- Microsoft.Extensions.AI with Ollama (local model by default)
- SQL Server 2016+ / LocalDB, xUnit, GitHub Actions

## Getting Started

Requirements: .NET 8 SDK and SQL Server 2016+ (or LocalDB).

```bash
git clone https://github.com/smailyy13/databasedeployment
cd databasedeployment
dotnet build
dotnet test
```

## Team – SegFault

| Member | Student ID | Development role | Management role |
|---|---|---|---|
| Mehmet Elhan | 150220013 | Job Orchestration / CLI Developer | Project Manager |
| Ömer Faruk Yılmaz | 150220057 | Catalog Extraction & Snapshot Engineer | Configuration Manager |
| Mücahit Günay | 150220027 | Comparison Engine Developer | QA & Code Review Lead |
| İsmail Yeşilyurt | 150220009 | Deployment Script Generation Developer | Product Owner |
| Mustafa Talha Yıldırım | 150220008 | Risk Analysis & Migration Developer | Customer Liaison & Meeting Secretary |
| Enes Birol Tatlı | 150230713 | Web UI & AI Integration Developer | AI Usage & Reporting Coordinator |

## Repository Layout

- `src/SchemaDiff.Core` – connection, extraction, normalization, comparison, script generation, risk, migrations
- `src/SchemaDiff.Cli` – command-line application (jobs, reports, exit codes)
- `src/SchemaDiff.Web` – local web interface (Minimal API + SSE, bound to 127.0.0.1)
- `src/SchemaDiff.AI` – optional AI assistant (Microsoft.Extensions.AI)
- `docs/` – course deliverables (`Charter.md`, `ProjectPlan.md`, `RequirementsSpec.md`, `RequirementsAnalysis.md`, `DesignSpec.md`, `TestSpec.md`)
- `tests/` – all tests, grouped by kind (`unit/`, `integration/`, `e2e/`, `performance/`, …)

## Workflow

- Nobody commits directly to `main`; every change goes through a branch and a pull request reviewed by another member.
- Each document is written on its own `docs/<name>` branch; the submission is a GitHub release (e.g. `charter-v1.0`) whose target is that PR branch, not `main`.
- Every request from a customer meeting becomes an issue labelled `customer-requests` within 48 hours; the PR that resolves it says `Closes #<issue>`.
