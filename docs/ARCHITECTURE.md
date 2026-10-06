# DynaK Wet Leak Test Architecture

## Overview

The production application has two local Windows processes: a WPF/WebView2 operator shell and an ASP.NET Core background service. The service owns acquisition, persistence, local APIs, static UI hosting, and operator state. The desktop shell is only an operator surface; closing it does not stop an active acquisition session.

## Project Structure

* `src/DynaK.Service` - ASP.NET Core host, background acquisition/report workers, local APIs, SQLite access, PLC abstraction, and offline static UI.
* `src/DynaK.Service/wwwroot` - offline HTML/CSS/JavaScript based on the Stitch reference.
* `src/DynaK.WetLeakTest.Desktop` - single-instance WPF host that starts/verifies the local backend and embeds the existing HMI in WebView2.
* `installer` - NSIS direct-EXE authoring for the desktop, service, shortcuts, machine-data permissions, optional WebView2 prerequisite, and portable launcher.
* `tests/DynaK.Tests` - small assertion-based test runner for critical database and shift behavior.
* `docs/PLC_REGISTER_MAP.md` - placeholder register-map document. Real Mitsubishi mappings are not yet available.

## Runtime Boundaries

1. PLC/data acquisition: `AcquisitionWorker` waits in `STOPPED` until the desktop or operator starts a station session, then owns the single PLC connection and non-overlapping polling loop. Stopping the station ends that session without stopping the API/service host.
2. Persistence/database: `SqliteDatabase`, `ProductionRepository`, and `EventRepository` own all SQLite writes.
3. Local application API/state: minimal local HTTP APIs expose state, records, events, and safe settings.
4. Operator UI: static offline frontend calls only local APIs and never talks to PLC hardware.

## Acquisition Flow

The acquisition worker runs independently of the operator UI:

1. Load the saved Settings snapshot and wait for an explicit `POST /api/station/start` request.
2. Connect to the configured Mitsubishi Modbus TCP endpoint. Connection loss during a requested run uses the controlled reconnect loop.
3. Capture the active PC IPv4/subnet diagnostic and connect to the configured endpoint.
4. Read configured PLC signals as grouped register blocks where possible.
5. Drive PC-to-PLC writes only through the centralized allow-list for the configured System Ready, Communication OK, and Data Saved mappings.
6. Feed each successful read into `PartDataReadyService`, which continuously evaluates the same decoded Part Data Ready and Part Number values exposed through PLC Mapping.
7. Whenever Part Data Ready is HIGH and the current Part Number is valid, non-zero, and absent from History, immediately perform a second complete read of every enabled readable mapping and capture one immutable part-data snapshot.
8. Insert exactly one complete SQLite row for that Part Number. Held HIGH polls skip already-saved Part Numbers, a changed Part Number is captured without waiting for LOW, and a failed insert retries on later HIGH polls without marking the Part Number processed.
9. Atomically replace the configured live Leak Test Value text file with the exact decimal value from the committed snapshot, skipping a rewrite when its content is unchanged.
10. Raise the configured Data Saved ON value only after both SQLite persistence and the live-file step succeed, then write the configured OFF value after about one second.
11. Update in-memory live values and diagnostics for the operator UI independently of production-record completeness.
12. Generate canonical per-day XLSX reports from SQLite rows in the background, recover missed reports after service restart, and replace files atomically without making acquisition depend on Excel success.

## SQLite

SQLite is the durable local source of truth. Initialization enables WAL mode and creates:

* `logical_parts` (one row per station+QR physical part)
* `production_records` (one immutable row per PLC test attempt)
* `machine_events`
* `app_config` (legacy compatibility table; new settings are not stored here)
* `schema_migrations`

Indexes cover logical-part recency, timestamp, QR code, part number, shift, result, and PLC sequence ID. Duplicate pulse protection remains enforced by a unique constraint on `station_id + plc_sequence_id`. Existing production rows are linked to logical parts non-destructively during schema migration 3.

Settings are persisted as a typed JSON snapshot in `C:\ProgramData\DynaK\Wet Leak Test Station\config\appsettings.json`, outside the SQLite database whose path it controls. Saves use a temporary file plus replace operation. The snapshot includes the database, live-text-file and report locations, Leak OK limits, automatic daily-export state, station identity, PLC connection values, configured shifts, D-register Modbus offset, editable PLC signal/register ranges, byte order, and value maps. Schema version 7 removes obsolete timing/lifecycle columns and stores the complete configured PLC signal snapshot for each Part Data Ready pulse; `lower_limit` and `upper_limit` preserve the limits used by each historical part.

## Desktop and Windows Service Operation

The backend calls `UseWindowsService`, and the direct NSIS installer registers and starts it as the automatic `DynaK.WetLeakTest.Acquisition` Windows service. The service remains available if the WPF shell closes. Service startup does not start PLC communication because the acquisition worker remains in `STOPPED` until the operator explicitly selects START.

The WPF shell verifies the backend identity before loading any content. It starts the installed service when necessary, or starts the packaged service executable as a hidden child during portable/development use. The child is shut down when the HMI closes only while station state is confirmed `STOPPED`; an active acquisition session is deliberately left running.

Only one WPF shell can run per machine. The shell permits navigation only within the configured loopback backend origin and blocks new windows, downloads, permission prompts, drag-and-drop navigation, browser context menus, autofill, and password saving in Release builds.

## Offline UI

The Stitch export used Tailwind CDN, Google Fonts, and Material Symbols. Runtime CDN dependencies were removed. The implemented UI uses local CSS plus bundled Inter, JetBrains Mono, and Material Symbols font assets, preserving the Stitch typography and icon system while remaining offline. Production rendering is hosted inside the WPF WebView2 control rather than a system-browser tab.

## Storage and Deployment

All writable machine state lives under `C:\ProgramData\DynaK\Wet Leak Test Station` by default. This separates the durable SQLite database, machine configuration, and rotating service/desktop logs from immutable Program Files binaries. First-run database initialization creates schema only. The service never imports a build-relative or legacy database automatically; changing the database path also never copies records from the previously active database.

When the station is stopped, a database change is prepared before activation: the destination folder must be writable, an existing file must pass SQLite integrity and DynaK compatibility checks, and a missing file receives an empty schema. Only after the complete configuration is atomically saved does the service activate the new database and clear the previous in-memory current-part display.

The direct NSIS installer installs self-contained `win-x64` desktop and service publishes, registers the service, and creates Desktop and Start Menu shortcuts that target the desktop executable. It also creates the Public Documents `DynaK Leak Test Report` tree for 2026-2029 without deleting existing reports; runtime creates later years. It detects the machine- or user-level WebView2 Runtime and can optionally embed Microsoft's x64 Evergreen Standalone EXE prerequisite for disconnected PCs.

## PLC Posture

`MitsubishiModbusPlcClient` is the production PLC implementation behind `IPlcClient`. It uses `TcpClient` directly for Modbus TCP, reads all enabled read mappings from Settings, and decodes configured datatypes through the centralized `PlcValueDecoder`. PLC writes are centralized and guarded by signal contract: only System Ready, Communication OK, and Data Saved can write, and each uses its current configured address and inverse-resolved ON/OFF value mapping. Part Data Ready defaults to read-only D1075; any configured or attempted write to it is rejected and logged before transport.

Settings expose `ENABLED`, `SIGNAL NAME`, `ADDRESS`, `ADDRESS TYPE`, `DATA TYPE`, `REGISTER RANGE`, `BYTE ORDER`, and `VALUE MAPPING`. Register count is editable for every row; fixed-width datatypes are rejected unless the count matches their actual width. Byte Order appears only for ranges of at least two registers. Backend and pre-save UI validation reject invalid addresses, incompatible counts, incomplete enabled handshakes, and overlapping enabled ranges without silently moving an address.

The client does not invent a PLC sequence register. The PLC prepares every required value and holds Part Data Ready HIGH; each valid Part Number that is not already in History requests an immediate complete current snapshot. Part Data Ready remains read-only. Data Saved remains a separate PC-to-PLC acknowledgement emitted only after the complete row and live Leak Test Value file are durable.
