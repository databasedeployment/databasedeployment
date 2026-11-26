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
