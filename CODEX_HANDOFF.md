# RBDE Technical Handoff & Architecture Specification (For Codex & Engineering Continuation)

This document summarizes all architectural upgrades, data contracts, C# modules, SQLite schemas, Web/3D Viewer capabilities, and the Zero-AI Telegram Query Bot implemented in `RevitBatchDataExtractor` (`RBDE`).

---

## 1. System Architecture & Module Map

| Module / Path | Target / Runtime | Responsibility |
| :--- | :--- | :--- |
| `src/CloudApp/ApsClient.cs` | `.NET 8` | 2-Legged OAuth (`data:read viewables:read`), Windows DPAPI credential storage (`%LOCALAPPDATA%\MMK_BIM\RBDE\credentials.dpapi.json`), Hub/Project/Folder discovery, and parallel Model Derivative extraction (`/metadata` + `/properties?forceget=true`). Maps APS `objectid` (`dbId`), Revit `ElementId`, `UniqueId`, and merges Type parameters onto Instance rows. |
| `src/CloudApp/AecRuleEngine.cs` | `.NET 8` | Evaluates `rules.ids.json` (`RBDE-IDS-Inspired-1.0-JSON`). Separates **Evaluation Status** (`Passed`, `Failed`, `InvalidValue`, `NotEvaluated`) and validates parameter `scope` (`instance` / `type`) + `propertySet` key (`GroupName:Name`) to prevent duplicate-name false positives. |
| `src/CloudApp/MainWindow.xaml(.cs)` | `.NET 8 WPF` | Batch extraction UI & headless runner (`--headless-batch`). Computes **Element-Level Diffs (`Added`, `Removed`, `Modified` with `old_value -> new_value`)** and **Issue Lifecycle (`New`, `Active`, `Fixed`, `Regressed`)** against `output/warehouse.sqlite` using strict `project_id + lineage_id` + `UniqueId` matching. Supports incremental `SkipUnchangedVersions` and per-model fault isolation. |
| `src/Dashboard/Program.cs` | `.NET 8` | Self-hosted `HttpListener` server serving `index.html`, SQLite REST query endpoints, APS Viewer token generation (`api/viewer-token`), 2D/3D Sheet Manifest parser (`api/model-sheets`), Native Cloud Vector PDF binary streamer (`api/download-pdf`), and Telegram Bot endpoints (`api/telegram-*`). |
| `src/Dashboard/TelegramBotEngine.cs` | `.NET 8` | **100% Deterministic (Zero-AI) Telegram Long-Polling Bot (`getUpdates`)**. Provides stateful per-chat hierarchy navigation (`Project/Run -> Model -> Level -> Category`), automatic Quantity Take-Off (`COUNT`, `SUM`, `AVG`, `MIN`, `MAX` for `Area`, `Volume`, `Length`, `Height`, `Width`, `Cost`), numeric comparison queries (`/num Area > 50`), Room schedules (`/rooms`), paginated anti-clutter digest cards, `.CSV` attachment generation, and direct Cloud Vector `.PDF` delivery (`sendDocument`). |
| `src/Dashboard/index.html` | Vanilla JS + APS Viewer v7 | Single-page Decision & Review Dashboard + **APS 2D/3D Coordination Studio (SVF2)** + **Zero-License Survival Exporters** (`.PNG` Titleblock Crop, `.OBJ` 3D Mesh via `VertexBufferReader`, `.IFC` IFC4 Reference Generator with SQLite `IfcPropertySet`, Multi-Page A3 PDF Sheet Set Compiler, Native Cloud Vector PDF Downloader) + **Interactive Telegram Simulator**. |
| `src/Revit/FixAssistantCommand.cs` | `.NET 8 Windows` (Revit 2026 API) | **Modeless (`IExternalEventHandler`) Revit Fix Assistant & Linked-Element Navigator**. Exposes both `IExternalCommand.Execute` and `FixAssistantCommand.Launch(UIApplication)` (for pyRevit `__revit__` without locking DLLs on disk). Resolves elements across both the **Host Document** and loaded **`RevitLinkInstance` documents**, transforms bounding boxes via `linkInstance.GetTotalTransform()`, highlights linked elements (`Reference.CreateLinkReference` + `Selection.SetReferences`), zooms active 2D/3D views (`UIView.ZoomAndCenterRectangle`), applies 3D Section Boxes (`View3D.SetSectionBox`), and previews Type vs. Instance blast-radius before applying parameter fixes. |

---

## 2. SQLite Database Schemas

### A. Per-Run Database (`output/batch-YYYYMMDD-HHMMSS/extract.sqlite`)

1. **`models`**:
   `model_id INTEGER PRIMARY KEY, path TEXT, status TEXT, started_utc TEXT, finished_utc TEXT, element_count INTEGER, version_number INTEGER, view_name TEXT, delta_elements INTEGER, added_elements INTEGER, removed_elements INTEGER, modified_elements INTEGER, derivative_urn TEXT`
2. **`elements`**:
   `model_id INTEGER, element_id INTEGER, unique_id TEXT, kind TEXT, category_id TEXT, category TEXT, family TEXT, type_name TEXT, type_id INTEGER, name TEXT, object_id INTEGER`
   *(Note: `object_id` is the APS Viewer SVF2 `dbId` for bidirectional 2D/3D selection sync).*
3. **`parameters`**:
   `model_id INTEGER, element_id INTEGER, scope TEXT, parameter_key TEXT, name TEXT, storage_type TEXT, has_value INTEGER, raw_value TEXT, display_value TEXT, unit_type_id TEXT`
   *(Note: When `storage_type IN ('Double','Integer')`, `CAST(raw_value AS REAL)` provides clean numeric values stripped of unit suffixes).*
4. **`issues`**:
   `issue_id INTEGER PRIMARY KEY AUTOINCREMENT, model_id INTEGER, element_id INTEGER, unique_id TEXT, category TEXT, family TEXT, type_name TEXT, rule_id TEXT, requirement_id TEXT, discipline TEXT, severity TEXT, parameter_name TEXT, actual_value TEXT, expected_rule TEXT, evaluation_status TEXT, lifecycle_state TEXT, message TEXT`
5. **`element_diffs`**:
   `diff_id INTEGER PRIMARY KEY AUTOINCREMENT, model_id INTEGER, change_type TEXT, element_id INTEGER, unique_id TEXT, category TEXT, family TEXT, type_name TEXT, parameter_name TEXT, old_value TEXT, new_value TEXT`
6. **`confidence_summary`**:
   `applicable_checks INTEGER, evaluated_checks INTEGER, passed_checks INTEGER, failed_checks INTEGER, invalid_value_checks INTEGER, not_evaluated_checks INTEGER, fixed_issues INTEGER, new_issues INTEGER, active_issues INTEGER, regressed_issues INTEGER, added_elements INTEGER, removed_elements INTEGER, modified_elements INTEGER`

### B. Persistent Cross-Run Warehouse (`output/warehouse.sqlite`)

1. **`runs`**: Tracks every batch execution (`run_id`, `project_id`, `project_name`, `started_utc`, `model_count`, `element_count`, `parameter_count`, `issue_count`, `fixed_count`, `new_issue_count`, `regressed_count`).
2. **`model_versions`**: Tracks extracted versions by `(run_id, project_id, lineage_id)` with `version_id` (used to derive Base64URL `derivative_urn` and skip unchanged models).
3. **`element_snapshots`**: Stores the latest known state per `(project_id, lineage_id, unique_id)` with `params_json` to compute exact `Added`, `Removed`, and `Modified (Before -> After)` element diffs.
4. **`issue_registry`**: Tracks each rule violation per `(project_id, lineage_id, unique_id, rule_id, requirement_id)` with `first_seen_run`, `last_seen_run`, and `state` (`Open` / `Fixed`) to detect `New`, `Active`, `Fixed`, and `Regressed` issues.

---

## 3. Dashboard HTTP API Contract (`src/Dashboard/Program.cs`)

All routes are served under `http://localhost:<port>/<session-token>/`:

- `GET api/overview` — Returns models (with `derivative_urn`), categories, levels, totals, `confidenceSummary`, `issueSummary`, `diffSummary`, `historyRuns`, `priorityColumns`, and `availableParameters`.
- `GET api/elements` — Filtered & paginated element rows + attached `params` dictionary + `completeness` stats + `qto` (`Area`, `Volume`, `Length` sums).
- `GET api/detail?model=<id>&element=<id>` — All instance and type parameters for a single element.
- `GET api/issues` — Filtered IDS issues with `evaluation_status` and `lifecycle_state`.
- `GET api/diffs` — Filtered `element_diffs` (`Added`, `Removed`, `Modified`).
- `GET api/viewer-token` — Generates a 2-legged APS token scoped to `viewables:read data:read` using the DPAPI-encrypted credentials.
- `GET api/viewer-theming?model=<id>` — Returns `object_id` (`dbId`) mappings for 3D Heatmaps (Issues, Teams, Diffs).
- `GET api/lookup-by-dbid?model=<id>&dbid=<dbId>` — Resolves an APS Viewer 3D click (`dbId`) back to the SQLite `element_id` row.
- `GET api/level-dbids?model=<id>&level=<levelName>` — Returns all 3D `object_id`s on a given building story for automatic 3D Section Box slicing.
- `GET api/model-sheets?urn=<urn>` — Parses the Autodesk Model Derivative manifest for published **2D Drawing Sheets (`role: "2d"`)**, **3D Views (`role: "3d"`)**, and child **Native Vector PDF Derivative URNs (`role: "pdf-page"` / `application/pdf`)**.
- `GET api/download-pdf?urn=<urn>&derivativeUrn=<pdfUrn>&name=<file.pdf>` — Streams the native vector `.pdf` derivative directly from Autodesk Cloud.
- `GET api/telegram-status` / `api/telegram-start?botToken=...` / `api/telegram-stop` / `api/telegram-simulate?cmd=...&cb=0|1` — Manages and simulates the deterministic Telegram Query Bot.

---

## 4. Recommended Next Steps for Codex Continuation

1. **Issue Assignment, Due Dates & Sign-Off Workflow (`issue_registry` Extension):**
   - Add `assignee`, `due_date`, `comment`, and `reviewer_signoff` columns to `issue_registry` in `output/warehouse.sqlite`.
   - Expose an inline status/assignment editor in Tab 03 (`Rules & Team Action Queue`) and allow Telegram users to acknowledge or comment on an issue via `/ack <IssueId> <Note>`.
2. **BCF 2.1 / 3.0 Issue Export (`.bcfzip`):**
   - Combine the APS Viewer camera state (`getState().viewport`), cropped `.PNG` snapshot, and `UniqueId` (`IfcGuid`) from `RBDE.Dashboard` to export standard `.bcfzip` packages for Navisworks, Solibri, Revizto, and BIMcollab.
3. **Multi-Model Federated Loading in APS Viewer:**
   - Extend `load3DModelByModelId` in `src/Dashboard/index.html` with an `"Overlay Link Model"` button that calls `apsViewer.loadDocumentNode(doc, node, { keepCurrentModels: true, globalOffset: ... })` so Architecture, Structure, and MEP SVF2 models can be sectioned together in a single viewport.
4. **Automated Scheduled Extraction + Proactive Telegram Digest Push:**
   - In `MainWindow.RunHeadlessScheduledBatchAsync`, after `SaveToDatabaseAndCsv` completes, automatically invoke `TelegramBotEngine` to push a concise **Morning BIM Readiness & Diff Digest** (plus `issues.csv`) to configured project Telegram group chats.
