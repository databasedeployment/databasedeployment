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
| FR-06 | T-02 | D-01 | The system shall read the schema with one read-only catalog query per object class, running the queries on parallel connections under a global concurrency limit. | Must | Query tests prove every query is read-only and reads only sys views. Gate tests prove the limit is never exceeded. |
| FR-07 | T-02 | D-01 | The system shall continue the comparison when an optional catalog query fails because of an older server version or a missing permission, and shall name the failed query in the extraction report. | Must | Fault injection test makes an optional query fail and the comparison still completes with a warning that names it. |
| FR-08 | T-02 | D-01 | The system shall detect missing VIEW DEFINITION permission and report the affected modules as uncertain, never as equal. | Must | A low privilege fixture login yields uncertain modules and a warning. No uncertain object is counted as equal. |
| FR-09 | T-02 | D-01, D-04 | The system shall report extraction progress as the number of finished and total queries and normalized modules while a comparison runs. | Should | Progress tests check counters, phases that only move forward and percentage bands. |
| FR-10 | T-02 | D-01, D-03 | The system shall provide a coverage probe that lists, for every object class, whether it can be read with the current permissions. | Should | Probe output on a low privilege login marks the unreadable classes. Probe queries are read-only. |
| FR-16 | T-04 | D-01 | The system shall compare every object kind listed in the charter scope, and shall report explicitly any kind it cannot script. | Must | Coverage matrix test: every object kind is either scripted or listed as report only. |
| FR-41 | T-11 | D-01, D-03 | The system shall export a database snapshot to a compressed file with a version header. | Should | Round trip test: an imported snapshot equals the live extraction. |
| FR-42 | T-11 | D-01, D-03, D-04 | The system shall compare a live database against a snapshot file without access to the database the snapshot came from. | Should | Comparing a fixture with its own snapshot reports no differences. |

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
| SC-01 | WBS-2.1 | Technical activity: the shared domain model and module contracts. It has no direct user story and enables every story of T-02 to T-14. | FR-06, FR-14 |
| SC-01 | WBS-2.2 | As a database developer, I want the schema of a large database read in seconds, so that comparing before every release is not a burden. | FR-06 |
| SC-03 | WBS-2.3 | As a BI developer, I want extraction to run in parallel without overloading the server, so that comparing many layers at once stays safe. | FR-06, FR-09 |
| SC-02 | WBS-2.4 | As a DBA, I want objects I cannot read to be reported as uncertain and a probe that shows what my login can read, so that missing permissions never look like equal objects. | FR-07, FR-08, FR-10 |
| SC-02 | WBS-4.3 | As a DBA, I want every object kind I use (indexes, permissions, partitions, extended properties and more) compared, so that nothing is deployed by surprise. | FR-16 |
| SC-07 | WBS-11.1 | As a database developer without production access, I want to compare against a production snapshot, so that I see what production will receive. | FR-41, FR-42 |

<!-- pagebreak -->

### 3.3 Use Case Diagrams

Use case names, actors and identifiers match Section 2 and Section 3.4. The diagrams use UML use case
notation and were drawn with PlantUML. Their sources are kept next to the images in
`docs/diagrams/requirements`, so they can be updated together with this document.

### 3.4 Use Cases

Every task of the Project Plan has at least one detailed use case below.

#### 3.4.3 UC-02 — Extract a schema

| Field | Description |
|---|---|
| **Use case ID** | UC-02 |
| **Use case name** | Extract a schema |
| **Goal / Purpose** | Read the complete schema of a database quickly and safely so it can be compared. |
| **Actor(s)** | System, started by UC-04 or UC-11 on behalf of UT-01 to UT-03. UT-06 SQL Server (secondary). |
| **Related Project Plan task(s)** | T-02 |
| **Related functional requirement(s)** | FR-06 to FR-10 |

**Pre-condition(s):**
A valid connection exists (UC-01). The login has at least VIEW DEFINITION for a complete result.

**Main success flow:**

1. **System:** Runs a preflight query for the server name, database, version and VIEW DEFINITION permission.
2. **System:** Queues one read-only query per object class and reports the total to the progress counter.
3. **System:** Runs the queries on parallel connections without exceeding the global concurrency limit.
4. **System:** Normalizes module bodies and builds the canonical text and hashes of every object.
5. **System:** Builds the reference graph from module dependencies and foreign keys.
6. **System:** Returns the snapshot together with an extraction report of timings, row counts and warnings.

**Alternative flows:**

- **A1 — Coverage probe first (branches from step 1):**
  1. The actor runs the probe command before the comparison.
  2. The system lists for each object class whether it can be read with the current permissions.
  3. The actor fixes permissions or accepts the report and the flow continues at step 1.
- **A2 — Optional query fails (branches from step 3):**
  1. A query that needs a newer version or an extra permission fails.
  2. The system records its name in the failed queries and adds a warning.
  3. The flow continues with the remaining queries.

**Exception / error flows:**

- **E1 — Definitions not readable (occurs at step 4):** Modules with a NULL definition are marked indeterminate with a reason and a warning names the missing permission. They are never reported as equal.
- **E2 — Required query fails (occurs at step 3):** The extraction stops with an error that names the query. The comparison is not shown, because a partial schema would produce false differences.

**Post-condition(s):**
A snapshot of every readable object exists with hashes per object and per part, and the report says exactly what could not be read.

#### 3.4.12 UC-11 — Compare against a snapshot

| Field | Description |
|---|---|
| **Use case ID** | UC-11 |
| **Use case name** | Compare against a snapshot |
| **Goal / Purpose** | Compare with an environment that cannot be accessed directly. |
| **Actor(s)** | UT-02 DBA (exports), UT-01 Database Developer (compares). |
| **Related Project Plan task(s)** | T-11 |
| **Related functional requirement(s)** | FR-41, FR-42 |

**Pre-condition(s):**
The DBA can connect to the reference database. The developer can connect to the database to compare.

**Main success flow:**

1. **Actor:** The DBA exports a snapshot of the reference database.
2. **System:** Extracts the schema (UC-02) and writes a compressed file with a version header.
3. **Actor:** The developer selects the snapshot file as the target and a live database as the source.
4. **System:** Loads the snapshot, extracts the live database and compares them (UC-04).

**Alternative flows:**

- **A1 — Snapshot from the CLI (branches from step 1):**
  1. The DBA runs the export command in a scheduled job.
  2. The system writes a new snapshot file every time.

**Exception / error flows:**

- **E1 — Incompatible snapshot version (occurs at step 4):** The system refuses the file and names the version it supports, instead of comparing against data it may misread.

**Post-condition(s):**
The comparison result is the same as comparing with the live reference database at the time of the export.

---

## 4. Changelog
