# Requirements Specification: SchemaDiff

## Cover Page

| Field | Details |
|---|---|
| **Course** | BLG 411E Software Engineering (2026–2027 Fall Term) |
| **Report name** | Requirements Specification |
| **Project title** | SchemaDiff: Fast SQL Server Schema Comparison, Deployment & Migration Tool |
| **Group name / number** | SegFault |
| **Group members** | Mehmet Elhan — 150220013, Ömer Faruk Yılmaz — 150220057, Mücahit Günay — 150220027, İsmail Yeşilyurt — 150220009, Mustafa Talha Yıldırım — 150220008, Enes Birol Tatlı — 150230713 |
| **Product Owner** | İsmail Yeşilyurt (150220009) |
| **Instructors** | Prof. Dr. Tolga Ovatman, Assoc. Prof. Dr. Ayşe Tosun |
| **Submission date** | 2026-11-06 |
| **Document version** | 1.0 |

---

## 1. Introduction

SchemaDiff answers the question "what changed between two SQL Server databases?" and turns the answer into
a safe, ordered deployment script. Teams that run several environments of the same databases (development,
test, production, and the staging, core and mart layers of a data warehouse) need this answer before every
release. Existing tools such as SSDT Schema Compare build a full semantic model and can take minutes on
databases with 10,000+ objects. SchemaDiff reads the system catalog in bulk, normalizes T-SQL on the ScriptDom
token stream and compares objects by XxHash128 hashes. On top of the comparison it analyzes deployment risk,
generates deployment and rollback scripts, keeps versioned migrations and offers an optional AI assistant
that explains changes in plain language. It is used from a CI-friendly command line tool and a local web
interface.

This document specifies what the system must do. It refines the tasks of the Project Plan (T-01 to T-16)
and the deliverables (D-01 to D-07) into requirements and use cases, and keeps every identifier consistent
with the plan so each requirement can be traced to a task, a deliverable and the WBS activities that
implement it.

- **Section 2** lists the functional requirements (FR) with their task, deliverable, priority and acceptance
  condition, followed by the non-functional requirements (NFR) with how each one is measured.
- **Section 3** describes the users: user types, personas and scenarios, and a user story for every WBS
  activity of the plan. It then shows the use case diagrams for the whole system and for each major
  functionality, a coverage matrix from tasks to use cases, and a detailed use case for every task with
  main, alternative and error flows.
- **Section 4** records the changes to the Project Plan since version 1.0, as requested for this deliverable.

Priorities follow MoSCoW: **Must** is required for the final release, **Should** is planned and expected,
**Could** is delivered if the critical path keeps its schedule.

---

## 2. System Requirements

### 2.1 Functional Requirements

Each requirement is written as an observable behavior and linked to the task(s) and deliverable(s) of the
Project Plan.

| Requirement ID | Related task ID(s) | Related deliverable ID(s) | Functional requirement (natural language) | Priority | Verification / acceptance condition |
|---|---|---|---|---|---|

### 2.2 Non-functional Requirements

| Requirement ID | Quality attribute | Non-functional requirement (natural language) | Applies to | Verification / measurement |
|---|---|---|---|---|

---

## 3. Use Cases

### 3.1 User Types

| User type ID | User type / actor | Description and responsibilities | Goals / typical actions | Related requirement ID(s) |
|---|---|---|---|---|
| UT-01 | Database Developer | Writes tables, views, procedures and functions in a development database and prepares them for release. | Compare dev with test, review differences, generate a script for selected objects, save a migration. | FR-01 to FR-24, FR-29, FR-44 to FR-46 |
| UT-02 | Database Administrator (DBA) | Owns the test and production servers and approves what is deployed there. | Check deployment risk, run scripts in a transaction, compare production with a snapshot, check trigger states. | FR-08, FR-21, FR-25 to FR-28, FR-33 to FR-35, FR-42 |
| UT-03 | BI / ETL Developer | Maintains the staging, core and mart layers of a data warehouse. | Compare all layers at once from the CLI, find renamed columns, keep layers in sync. | FR-17, FR-36 to FR-40 |
| UT-04 | Release Manager | Plans and documents releases across environments. | Get release notes and HTML reports, follow pending migrations and drift. | FR-31, FR-32, FR-43, FR-48 |
| UT-05 | CI/CD pipeline (external system) | Runs SchemaDiff from the command line in automated jobs. | Detect schema drift through exit codes, publish reports as build artifacts. | FR-36 to FR-40, FR-43, FR-53 |
| UT-06 | SQL Server instance (external system) | Source or target of a comparison and target of script execution. | Answers catalog queries, executes scripts and stores the migration history. | FR-06 to FR-10, FR-30, FR-33 |
| UT-07 | AI provider (external system) | Local Ollama model by default, optionally an OpenAI-compatible service. | Turns schema metadata into summaries, explanations and review comments. | FR-48 to FR-51 |

**Access or role distinctions:**

The tool runs locally and has no accounts of its own. What a user can do depends on the SQL Server permissions
of the login used for the connection. Comparison needs VIEW DEFINITION and, for row counts, VIEW DATABASE STATE.
Script execution and migration apply need DDL permissions on the target and are therefore usually done by a
DBA (UT-02). Developers (UT-01, UT-03) typically have full rights on development and read-only rights on
production, which is why comparing against a snapshot (FR-42) matters for them.

#### Personas

| Persona ID | Name | User type | Background | Main need |
|---|---|---|---|---|
| P-01 | Elif, 29 | UT-01 | Database developer in the core banking team, writes stored procedures every day and releases every two weeks. | See exactly what she changed since the last release and get a script for only her objects. |
| P-02 | Murat, 41 | UT-02 | Senior DBA responsible for the production servers, signs off every deployment. | Know before running a script whether it will be blocked or lose data. |
| P-03 | Deniz, 34 | UT-03 | Data warehouse engineer maintaining seven layered databases. | Compare all layers in one run every night and get notified only when something differs. |
| P-04 | Selin, 38 | UT-04 | Release manager coordinating three teams. | Readable release notes and a report she can attach to the change request. |

### 3.2 User Scenarios

#### User Stories Linked to the Project Plan

Every WBS activity of the Project Plan has a user story, or an explanation when the activity is purely technical.

| Scenario ID | Project Plan WBS activity ID | User story | Related requirement ID(s) |
|---|---|---|---|

<!-- pagebreak -->

### 3.3 Use Case Diagrams

Use case names, actors and identifiers match Section 2 and Section 3.4. The diagrams use UML use case
notation and were drawn with PlantUML. Their sources are kept next to the images in
`docs/diagrams/requirements`, so they can be updated together with this document.

### 3.4 Use Cases

Every task of the Project Plan has at least one detailed use case below.

---

## 4. Changelog
