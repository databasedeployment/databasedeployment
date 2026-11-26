# Requirements Analysis: SchemaDiff

## Cover Page

| Field | Details |
|---|------------|
| **Course** | BLG 411E Software Engineering (2026–2027 Fall Term) |
| **Report name** | Requirements Analysis |
| **Project title** | SchemaDiff: Fast SQL Server Schema Comparison, Deployment & Migration Tool |
| **Group name / number** | SegFault |
| **Group members** | Mehmet Elhan (150220013), Ömer Faruk Yılmaz (150220057), Mücahit Günay (150220027), İsmail Yeşilyurt (150220009), Mustafa Talha Yıldırım (150220008), Enes Birol Tatlı (150230713) |
| **Instructors** | Prof. Dr. Tolga Ovatman, Assoc. Prof. Dr. Ayşe Tosun |
| **Submission date** | 2026-11-28 |
| **Document version** | 1.0 |

<!-- changelog -->

## 1. Introduction

SchemaDiff compares two SQL Server schemas and turns the differences into a safe, ordered deployment script. It
reads the catalog in bulk, normalizes T-SQL with ScriptDom and compares XxHash128 hashes, so 10,000+ objects are
compared in seconds. It also analyzes risk, writes rollback scripts, keeps migrations and offers an optional AI
assistant, through a command line tool (CLI) and a local web interface.

This document extends the Requirements Specification v1.1 with the user interface model (Section 4) and the data
model and data flows (Section 5). IDs follow the Project Plan v1.3. Priorities follow MoSCoW.

## 2. System Requirements

### 2.1 Functional Requirements

Each requirement completes "The system shall …".

| Requirement ID | Related task ID(s) | Related deliverable ID(s) | Functional requirement: the system shall … | Priority | Verification / acceptance condition |
|------|-----|-------|------------------------------|------|-------------|
| FR-01 | T-01 | D-01, D-04 | Connect with server, Windows or SQL login, credentials, encrypt and trust certificate options. | Must | Unit tests |
| FR-02 | T-01 | D-01, D-04 | Test a connection and show server, version, edition and login, or the first error line. | Must | Unit and API tests |
| FR-03 | T-01 | D-01, D-04 | List the online user databases the login can access. | Must | Fixture test |
| FR-04 | T-01 | D-04 | Keep the 20 most recent connections and let the user reuse or forget each. | Should | Unit tests |
| FR-05 | T-01 | D-01, D-04 | Store a password only on request, DPAPI protected, and never return it to the browser. | Must | File scan, API test |
| FR-06 | T-02 | D-01 | Read the schema with one read-only query per object class, in parallel under a global limit. | Must | Query and gate tests |
| FR-07 | T-02 | D-01 | Continue when an optional query fails and name it in the extraction report. | Must | Fault injection test |
| FR-08 | T-02 | D-01 | Report modules as uncertain, never equal, when VIEW DEFINITION is missing. | Must | Low privilege fixture |
| FR-09 | T-02 | D-01, D-04 | Report progress as finished and total queries and normalized modules. | Should | Unit tests |
| FR-10 | T-02 | D-01, D-03 | Provide a coverage probe that shows which object classes are readable. | Should | Low privilege test |
| FR-11 | T-03 | D-01 | Normalize module bodies on the token stream (whitespace, comments, semicolons, casing, quoting), never changing string literals. | Must | Golden files |
| FR-12 | T-03 | D-01 | Compute XxHash128 hashes per object and per sub-part. | Must | Hash tests |
| FR-13 | T-03 | D-01, D-04 | Apply the chosen comparison options (column order, collation, identity, index options, system named constraints, name casing). | Must | Option tests |
| FR-14 | T-04 | D-01 | Classify every object as added, removed, changed, equal or indeterminate by hash. | Must | End-to-end fixture test |
| FR-15 | T-04 | D-01, D-04 | Show the changed sub-parts of an object as a change tree. | Must | Change tree tests |
| FR-16 | T-04 | D-01 | Compare every object kind in the charter scope and report kinds it cannot script. | Must | Coverage matrix test |
| FR-17 | T-05 | D-01 | Report removed and added objects with identical structure as rename candidates, without sp_rename. | Should | Rename tests |
| FR-18 | T-06 | D-02 | Order the script by dependency (types, tables, modules, security, permissions, settings), keeping cycles in input order with a warning. | Must | Ordering tests |
| FR-19 | T-06 | D-02 | Turn CREATE of an existing module into ALTER at token level. | Must | Token level tests |
| FR-20 | T-06 | D-02 | Wrap the script in one transaction with XACT_ABORT ON. | Must | Fault injection |
| FR-21 | T-06 | D-02, D-04 | Run in safe mode by default, leaving data loss steps out and listing them. | Must | Column drop test |
| FR-22 | T-06 | D-02, D-04 | Script only the objects the user selected. | Must | Selection tests |
| FR-23 | T-06 | D-02 | Generate a rollback script that restores the target schema. | Must | Round trip test |
| FR-24 | T-06 | D-02 | Generate a mixed direction script with the direction chosen per object. | Should | Bidirectional tests |
| FR-25 | T-07 | D-02, D-04 | Classify table changes as safe, in place, blocked if not empty or data loss, using target row counts. | Must | Risk tests |
| FR-26 | T-07 | D-02 | Treat an unknown row count or uncertain change as risky. | Must | Missing row count test |
| FR-27 | T-07 | D-02, D-04 | Mark data dependent blocks as conditional. | Should | Conditional flag tests |
| FR-28 | T-07 | D-02, D-03, D-04 | Report trigger enabled or disabled state in source and target. | Should | Layered fixture report |
| FR-29 | T-08 | D-02, D-03 | Save a comparison as `V###__description.sql` with a rollback file. | Must | Naming tests |
| FR-30 | T-08 | D-02 | Record applied migrations with checksums in `__SchemaDiffMigrations`. | Must | History table test |
| FR-31 | T-08 | D-02, D-03 | List pending migrations and apply them in version order. | Should | Fixture test |
| FR-32 | T-08 | D-02, D-03 | Detect drift between migration history and the live schema. | Should | Manual change reported |
| FR-33 | T-09 | D-02, D-04 | Execute a script split at GO on one connection. | Must | Execution tests |
| FR-34 | T-09 | D-02, D-04 | Stream SSMS style messages, with errors showing number, level, state and script line. | Must | SSMS comparison |
| FR-35 | T-09 | D-02, D-04 | Let the user cancel an execution and roll back the transaction. | Should | Cancel test |
| FR-36 | T-10 | D-03 | Read named jobs from jobs.json with shared defaults, naming the job in validation errors. | Must | Configuration tests |
| FR-37 | T-10 | D-03 | Select jobs interactively or by expression (1,3-5,EDWDM, all). | Must | Selector tests |
| FR-38 | T-10 | D-03 | Run jobs in parallel under one query limit and print each result when it finishes. | Should | Runner test |
| FR-39 | T-10 | D-03, D-06 | Exit with 0 (no differences), 1 (differences) or 2 (error). | Must | GitHub Actions job |
| FR-40 | T-10 | D-03 | Provide compare, script and probe commands with help text. | Must | Help output check |
| FR-41 | T-11 | D-01, D-03 | Export a snapshot to a compressed file with a version header. | Should | Round trip test |
| FR-42 | T-11 | D-01, D-03, D-04 | Compare a live database against a snapshot file. | Should | Self compare |
| FR-43 | T-12 | D-03, D-04 | Export results as standalone HTML and JSON reports. | Should | Report tests |
| FR-44 | T-13 | D-04 | Provide a local web interface with source and target selectors and live progress (Server-Sent Events). | Must | Binding and timing tests |
| FR-45 | T-13 | D-04 | Show a grouped result tree with checkboxes and side-by-side definitions with difference navigation. | Must | Fixture walk-through |
| FR-46 | T-13 | D-04 | Provide a risk view, a settings panel and all texts in English and Turkish. | Must | Localization check |
| FR-47 | T-13 | D-04 | Release session data when the browser tab is closed. | Must | Dispose test |
| FR-48 | T-14 | D-05 | Produce a plain language summary usable as release notes. | Should | Output test |
| FR-49 | T-14 | D-05, D-04 | Explain each deployment risk and suggest a mitigation. | Should | Fixture test |
| FR-50 | T-14 | D-05 | Produce review comments on a deployment script. | Should | Output parsing test |
| FR-51 | T-14 | D-05 | Send only schema metadata to the AI provider, local by default, and work fully without AI. | Must | Prompt logging test |
| FR-55 | T-16 | D-07 | Come with a README, CLI reference, web guide and configuration samples. | Must | Clean machine walkthrough |

**Functional requirement notes:** Uncertain is never equal (FR-08). Only execution (FR-33) and migration apply
(FR-31) change a database. Risk rounds to the riskier side (FR-26). FR-52 to FR-54 were removed in v1.1.

### 2.2 Non-functional Requirements

Targets are those agreed in Project Plan Section 1.4.

| Requirement ID | Quality attribute | Non-functional requirement: the system shall … | Applies to | Verification / measurement |
|------|-------|----------------------------|---------|-----------|
| NFR-01 | Security | Keep passwords DPAPI protected and out of logs, reports, prompts and responses. | FR-05, FR-43, FR-51 | Secret scan |
| NFR-02 | Security | Only read the catalog, except for execution and migration apply. | FR-06, FR-31, FR-33 | Query safety tests |
| NFR-03 | Security | Bind the web backend to 127.0.0.1 only. | FR-44 | Listen address test |
| NFR-04 | Performance | Compare two 10,000+ object databases within 30 s on a local network and at least 5× faster than DacFx. | FR-06, FR-14 | Benchmark (WBS-15.5) |
| NFR-05 | Performance | Show the first progress update within 1 s. | FR-09, FR-44 | Timing log |
| NFR-06 | Usability | Follow the SSDT Schema Compare layout so SSDT users need no training. | UT-01 to UT-04 | 3 user walk-through |
| NFR-07 | Usability | Offer every text in English and Turkish and help text for every CLI command. | FR-40, FR-46 | Completeness check |
| NFR-08 | Reliability | Report unreadable objects as indeterminate, never as equal. | FR-08, FR-14 | Low privilege fixture |
| NFR-09 | Reliability | Apply a script completely or leave the target unchanged, and restore it with the rollback. | FR-20, FR-23 | Round trip test |
| NFR-10 | Reliability | Report failed queries and lost connections clearly and keep other CLI jobs running. | FR-07, FR-38 | Fault injection tests |
| NFR-11 | Maintainability | Keep Core, CLI, Web and AI separate on the WBS-2.1 contracts, with 80% Core coverage. | Whole system | CI coverage report |
| NFR-12 | Compatibility | Support SQL Server 2016+ and Azure SQL, degrading optional queries on older versions. | FR-06, FR-07 | Version test runs |
| NFR-13 | Portability | Run CLI and Core on Windows, macOS and Linux with .NET 8, degrading Windows-only features. | D-01, D-03 | Windows CI, macOS runs |
| NFR-14 | Privacy | Keep AI optional, local by default and limited to schema metadata. | FR-48 to FR-51 | Prompt logging test |

**Notes:** "Local network" means one data center. The 30 s target excludes script generation and AI. The full
feature set needs Windows 10+ and a current browser.

## 3. Use Cases

### 3.1 User Types

| User type ID | User type / actor | Description and responsibilities | Goals / typical actions | Related requirement ID(s) |
|-----|--------|--------------|------------|----------|
| UT-01 | Database Developer | Writes schema objects in development and prepares releases. | Compare, review, script selected objects, save migrations. | FR-01 to FR-24, FR-29, FR-44 to FR-46 |
| UT-02 | DBA | Owns test and production servers and approves deployments. | Check risk, execute scripts, export snapshots, check triggers. | FR-08, FR-21, FR-25 to FR-28, FR-33 to FR-35, FR-41 |
| UT-03 | BI / ETL Developer | Maintains staging, core and mart layers. | Compare all layers from the CLI, find renames. | FR-17, FR-36 to FR-40 |
| UT-04 | Release Manager | Plans and documents releases. | Release notes, reports, pending migrations, drift. | FR-31, FR-32, FR-43, FR-48 |
| UT-05 | CI/CD pipeline (external) | Runs the CLI in automated jobs. | React to exit codes, publish reports. | FR-36 to FR-40, FR-43 |
| UT-06 | SQL Server (external) | Source, target and execution target. | Answer catalog queries, run scripts, keep history. | FR-06 to FR-10, FR-30, FR-33 |
| UT-07 | AI provider (external) | Local Ollama by default, optionally OpenAI-compatible. | Turn metadata into explanations. | FR-48 to FR-51 |

**Access or role distinctions:** There are no accounts. Comparison needs VIEW DEFINITION (VIEW DATABASE STATE for
row counts), execution and migrations need DDL rights, so the DBA runs them. Snapshots (FR-42) serve developers
without production access.

### 3.2 User Scenarios

#### Personas

| Persona ID | Persona name | User type ID | Goals and context | Relevant needs or constraints |
|-----|------|-----|--------------|--------------|
| P-01 | Elif, 29 | UT-01 | Core banking developer, releases every two weeks. | Sees only her real changes and scripts only her objects. |
| P-02 | Murat, 41 | UT-02 | Senior DBA, signs off every production deployment. | Knows before running whether a script blocks or loses data. |
| P-03 | Deniz, 34 | UT-03 | Data warehouse engineer with seven layered databases. | Nightly compare of all layers, alerts only on differences. |
| P-04 | Selin, 38 | UT-04 | Release manager coordinating three teams. | Readable release notes and a report for the change request. |
