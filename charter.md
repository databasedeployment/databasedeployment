# Project Charter: SchemaDiff — Fast SQL Server Schema Comparison, Deployment & Migration Tool

## 1. Project Purpose and Objectives

SchemaDiff is a DacFx / SSDT "Schema Compare"-like tool that answers *"what changed between two SQL Server databases?"* and turns the answer into a safe, ordered deployment script. Instead of building a full semantic model like DacFx, it extracts the system catalog in bulk, normalizes T-SQL with ScriptDom and compares objects through XxHash128 hashes, aiming at **seconds instead of minutes** on databases with 10,000+ objects. Beyond comparison, the tool analyzes deployment risk (data loss on non-empty tables), generates rollback scripts, produces versioned migration files with a history table, and offers an AI assistant that explains changes and risks in plain language. It is delivered as a CLI (CI-friendly) and a local web interface.

## 2. Stakeholders & Roles

- **Team:** SegFault
- **Product Owner:** İsmail Yeşilyurt (150220009)
- **Software Engineering Team:**
  - **Mehmet Elhan (150220013):** Project Manager & Job Orchestration / CLI Developer — multi-job configuration, parallel streaming runner, CLI commands, console/HTML/JSON reports
  - **Ömer Faruk Yılmaz (150220057):** Catalog Extraction & Snapshot Engineer — catalog queries, parallel extraction with concurrency limits, permission checks, coverage probe, snapshot export/import
  - **Mücahit Günay (150220027):** Comparison Engine Developer — T-SQL normalization, hashing, hash-based diff, change tree, rename detection
  - **İsmail Yeşilyurt (150220009):** Deployment Script Generation Developer — table/module/type/role/permission/extended-property/settings script generators, dependency ordering, rollback and bidirectional scripts
  - **Mustafa Talha Yıldırım (150220008):** Risk Analysis & Migration Developer — deployment risk analyzer, trigger status report, migration file generation and history, script execution engine
  - **Enes Birol Tatlı (150230713):** Web UI & AI Integration Developer — minimal API + SSE backend endpoints, single-page web interface, connection management, AI assistant
- **GH Repo/Project URL:** *{Do not fill this field in yet; to be filled after charter approval}*

## 3. Project Scope

### Project Domain

Database DevOps / release management for SQL Server-based enterprise systems (e.g. banking, insurance, telecom and data warehouse/BI platforms with multiple layers such as staging, core and data mart).

**Possible customers:** organizations that keep several environments (dev, test, prod) of the same SQL Server databases and deploy schema changes regularly.

**Key users:** database developers, DBAs, BI/ETL developers and release managers who need to see differences between environments, generate deployment scripts and know *before* deployment whether a change will fail or lose data.

### In-Scope

- **Connection Management** — SSDT-style connect dialog (server, authentication, encrypt/trust certificate, database list from server), connection test, recent connections, secure password storage with Windows DPAPI.
- **Bulk Catalog Extraction** — one read-only query per object class (~50 queries) run on parallel connections with a global concurrency limit; optional queries degrade gracefully on older versions or missing permissions; `VIEW DEFINITION` detection so unreadable objects are reported as "uncertain", never as "equal".
- **Normalization & Hash-Based Comparison** — ScriptDom token-based normalization (whitespace/comment/case options), XxHash128 per object and per sub-part (columns, indexes, constraints, body, permissions…), Add/Delete/Change classification with drill-down to sub-changes (Add Column, Alter Column, Drop Index…).
- **Broad Object Coverage** — schemas, tables (computed/identity/sparse/temporal columns, defaults), indexes (incl. columnstore, XML, spatial, full-text), statistics, PK/UQ/CHECK/FK constraints, views, procedures, functions, DML/DDL triggers, sequences, synonyms, user-defined and table types, partition functions/schemes, XML schema collections, roles, permissions, extended properties, plan guides, database scoped configurations.
- **Rename Detection** — heuristic detection of removed+added pairs with identical structure, reported as rename candidates (to avoid data-losing drop+create).
- **Deployment Script Generation** — dependency-ordered script (types → tables → modules → roles → permissions/extended properties → settings), CREATE → ALTER conversion at token level, single transaction with `XACT_ABORT`, safe mode excluding data-loss steps, per-object selection, rollback script and bidirectional (mixed-direction) scripts.
- **Deployment Risk Analysis** — classify each table change (type narrowing, column drop, NOT NULL without default…) together with target row counts from `sys.dm_db_partition_stats`; uncertain cases rounded to the riskier side; trigger enabled/disabled status report across environments.
- **Migration Generation & History** — save a comparison as a versioned migration file (`V001__description.sql` + matching rollback), a `__SchemaDiffMigrations` history table with checksums on the target database, list/apply pending migrations and detect drift between the history and the live schema.
- **Script Execution** — run the generated script against the target from the web UI with GO-batch splitting and SSMS-style live messages (PRINT output, rows affected, errors with line numbers).
- **Multi-Job Orchestration & CLI** — named source/target jobs in `jobs.json`, interactive or expression-based selection (`1,3-5,name`), parallel runs streaming results as each job finishes, CI-friendly exit codes (0 no diff / 1 diff / 2 error), coverage probe.
- **Snapshot Export & Offline Compare (new)** — save a database snapshot to a compressed file and compare a live database against a snapshot (no access to production needed at compare time; dacpac-like baseline).
- **Reporting (new)** — export comparison and risk results as standalone HTML and JSON reports for code review and release documentation.
- **Web Interface** — local SPA laid out like SSDT Schema Compare: source ⇄ target selectors, grouped result tree with checkboxes, side-by-side object definitions with diff navigation, deployment risk view, settings panel, live progress via SSE, full EN/TR localization, session cleanup on tab close.
- **AI Assistant (new)** — natural-language summary of a comparison ("release notes"), plain-language explanation of each risk warning and suggested mitigation, and review comments on the generated script; works on schema metadata only (never row data) and is optional/pluggable (local model by default).
- **Testing & Quality** — xUnit unit tests for normalization, comparison, risk classification and script generation; coverage matrix test ensuring every object kind is either scripted or explicitly reported; end-to-end fixtures (dev/prod/layered databases) and a benchmark against DacFx.

### Out-of-Scope

- Data comparison and data synchronization/migration (row-level diff, data transfer between environments).
- Database management systems other than Microsoft SQL Server / Azure SQL (PostgreSQL, MySQL, Oracle…).
- Table rebuilds for column order changes and physical placement changes (`ON [filegroup]`).
- Automatic DDL generation for CLR assemblies/types, Always Encrypted keys and in-memory OLTP objects (detected and reported only).
- Automatic `sp_rename` generation for detected renames (reported only, user decides).
- Database users and logins (environment-specific; ignored by default).
- Multi-user deployment of the web interface: authentication/authorization, network exposure, cloud hosting (the tool runs locally on `127.0.0.1`).
- Orchestration of other release steps such as SSIS package deployment, SQL Agent job triggering or SSISDB parameter updates.
- Visual Studio / VS Code extension packaging.
- AI-driven automatic changes to databases (the AI assistant only explains and suggests; it never executes).

## 4. Technology Stack

### Backend Stack

- .NET 8 / C# 12
- ASP.NET Core Minimal API + Server-Sent Events (SSE)
- Microsoft.Data.SqlClient (catalog queries and script execution)
- Microsoft.SqlServer.TransactSql.ScriptDom (T-SQL tokenizer/parser used by SSDT)
- System.IO.Hashing (XxHash128)
- System.Security.Cryptography.ProtectedData (Windows DPAPI for stored passwords)
- System.Text.Json (job configuration, snapshot and report files)
- Microsoft SQL Server 2016+ / SQL Server LocalDB (target platform and test fixtures)
- xUnit (unit and integration tests), BenchmarkDotNet / DacFx (performance comparison)
- Git & GitHub, GitHub Actions (build + test CI)

### Frontend Stack

- HTML5 & CSS3 (custom styles, light/dark friendly)
- Vanilla JavaScript single-page application (dependency-free, single `app.js`)
- EventSource API (SSE) for live comparison/execution progress
- Fetch API + `navigator.sendBeacon` (REST calls and session disposal)
- Built-in EN/TR localization

### AI Stack

- Microsoft.Extensions.AI (provider-independent chat client abstraction in .NET)
- Ollama with a local open-weight code model (e.g. Qwen2.5-Coder / Llama 3.1) as the default, offline provider
- Optional OpenAI-compatible cloud LLM API as an alternative provider
- Prompt templates fed with structured diff/risk metadata (JSON) — schema metadata only, no row data
- Structured (JSON) output parsing for risk explanations and script review comments

## 5. Project Milestones

| Deliverable | Deadline |
|---|---|
| Project Charter | 11.10.2026 23:59 |
| Project Plan | 19.10.2026 23:59 |
| Requirement Specification Report | 02.11.2026 23:59 |
| Requirement Analysis Report | 23.11.2026 23:59 |
| Software Design Report | 07.12.2026 23:59 |
| Software Test Report | 21.12.2026 23:59 |
| Codebase | 25.12.2026 23:59 |
| Demo | 26.12.2026 23:59 |
| Presentation | 28.12.2026 23:59 |
