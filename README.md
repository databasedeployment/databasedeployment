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
cd SchemaDiff
dotnet build
dotnet test
```

## Team – SegFault

- İsmail Yeşilyurt – Product Owner, Deployment Script Generation
- Mehmet Elhan – Project Manager, Job Orchestration & CLI
- Ömer Faruk Yılmaz – Catalog Extraction & Snapshots
- Mücahit Günay – Comparison Engine
- Mustafa Talha Yıldırım – Risk Analysis & Migrations
- Enes Birol Tatlı – Web UI & AI Integration
