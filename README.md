# DynaK Wet Leak Test Station

DynaK is a Windows industrial wet leak testing application. The production operator application is a WPF shell containing Microsoft WebView2. It displays the existing offline HTML/CSS/JavaScript HMI while the separate ASP.NET Core service owns PLC communication, station lifecycle, APIs, logging, and SQLite persistence.

## Production runtime

1. `DynaK Wet Leak Test Station.exe` enforces a single HMI instance.
2. The desktop host verifies the DynaK backend at `http://127.0.0.1:5055/` by application-specific health response.
3. The installed Windows service is started if required. A packaged child-process fallback is used only when the service is not installed, such as a developer build.
4. WebView2 loads the static HMI served by the backend. No Vite, Node development server, system browser, or internet connection is required.
5. After the HMI loads, the desktop requests the centralized service to begin PLC connection, polling, read-only Part Data Ready level monitoring, and the three configured PC-to-PLC handshake signals. The existing START/STOP control remains available.

The service is installed for automatic Windows startup and is started during installation. The service alone remains in `STATION STOPPED`; opening the HMI starts the station runtime with polling, Part Data Ready monitoring, and handshake writes. Closing the HMI does not stop an active service session.

## PLC write safety

The production build permits PC-to-PLC writes only through the configured `System Ready`, `Communication OK`, and `Data Saved` mappings. Their enabled state, address, address type, datatype, register count, byte order, and ON/OFF value mapping are editable; every other signal name remains write-blocked before a Modbus frame is sent. The Settings diagnostic shows the PC IPv4/subnet, endpoint, protocol, live mapped values, last successful read, classified connection error, and handshake-write status.

## Machine data

Writable station files default to:

```text
C:\ProgramData\DynaK\Wet Leak Test Station\
├── config\appsettings.json
├── data\dynak-wet-leak-test.db
└── logs\
    ├── dynak-service.log
    └── dynak-desktop.log
```

Set `DYNAK_DATA_ROOT` to an absolute directory for isolated development or test runs. Settings are atomically persisted in the machine configuration file, separately from SQLite. A fresh or newly selected database is initialized with schema only and no legacy, demo, or production rows are copied into it automatically.

After each Part Data Ready snapshot is committed to SQLite, an available captured Leak Test Value is atomically exported to `C:\DynaK\live_leak_value.txt` by default. An unavailable or malformed PLC leak value is preserved in the raw snapshot and does not block the History row, but no text-file value is invented. Change `LIVE LEAK VALUE TEXT FILE` in Engineer Settings while the station is stopped. Unchanged values are not rewritten.

Leak OK minimum/maximum values and the report root are persisted in the same machine configuration. Each part row snapshots the effective limits so historical reports do not change when later settings change. Automatic daily reports are written from SQLite to `%PUBLIC%\Documents\DynaK Leak Test Report\YYYY\MM - Month\YYYY-MM-DD.xlsx`; the service creates future year/month folders, repairs missing or stale reports on startup, and atomically replaces the canonical daily file without numbered duplicates.

The Settings page can change the database file while the station is stopped. The service validates the destination, accepts an empty/new database or a compatible DynaK database, initializes schema when needed, then activates the path only after the complete machine configuration is written successfully.

## Development

Build and run the tests:

```powershell
dotnet build DynaK.WetLeakTest.slnx
dotnet run --project tests\DynaK.Tests\DynaK.Tests.csproj
```

The backend can still be debugged independently:

```powershell
$env:DYNAK_DATA_ROOT = Join-Path $env:TEMP 'DynaK-Dev'
dotnet run --project src\DynaK.Service\DynaK.Service.csproj --no-launch-profile
```

Then start `src\DynaK.WetLeakTest.Desktop`. Debug builds retain WebView2 DevTools; Release builds disable DevTools, browser accelerators, context menus, downloads, new windows, external navigation, autofill, and password saving.

## Release and installer

This is not an Electron application. The production desktop is .NET 10 WPF with WebView2, and NSIS builds the single direct setup executable. The install remains per-machine because it registers the acquisition Windows service; setup requests administrator elevation once, while the installed desktop executable uses `asInvoker` and does not require elevation when launched normally.

The release script publishes both .NET projects self-contained for `win-x64`, builds the direct NSIS installer, and creates a portable ZIP. Install NSIS 3.x before building a release. The normal installer uses the already-installed WebView2 Runtime and stays small. To embed Microsoft's x64 Evergreen Standalone WebView2 installer for disconnected PCs that do not have WebView2, supply its path explicitly:

```text
artifacts\prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe
```

Then run either:

```powershell
.\build-release.ps1 -Version 1.0.34

# Optional: embeds the Microsoft EXE prerequisite only when an offline runtime installer is required.
.\build-release.ps1 -Version 1.0.34 `
  -WebView2InstallerPath .\artifacts\prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe
```

Outputs are written to `artifacts\installer`; the complete unpackaged layout used for smoke tests is written to `artifacts\publish\package`. The release directory contains `DynaK-Wet-Leak-Test-Setup-v<version>.exe` and `DynaK-Wet-Leak-Test-Portable-v<version>.zip`. The installer installs WebView2 only when a prerequisite payload was explicitly embedded and the runtime is missing, installs the desktop and service binaries, creates Desktop and Start Menu shortcuts that target the application executable, grants the station service and local operators access to the machine-data directory, and preserves that directory across upgrades and uninstall.

Each release build recreates `release` so it contains the direct installer and portable ZIP only. The setup success page offers a normal, non-elevated launch of the installed application. The application does not contain any updater or setup-relaunch code. The portable launcher writes its persistent state to `%LOCALAPPDATA%\DynaK\Wet Leak Test Station`, not beside the executable.

The release script hashes every authored file under `src\DynaK.Service\wwwroot`, publishes from a clean output directory, and stops the release if any packaged renderer file is missing, stale, or different. Engineer Settings shows the App Version, UI Build ID, UTC Build Date, and Git source revision (or `not available` when the source is not a Git worktree), sourced from the generated `service\build-info.json` included in both the installer and portable package.

Release output is unsigned unless a real code-signing certificate thumbprint and RFC 3161 timestamp server are supplied:

```powershell
.\build-release.ps1 -Version 1.0.34 `
  -SigningCertificateThumbprint '<certificate thumbprint>' `
  -TimestampServerUrl '<certificate-provider timestamp URL>'
```

The service is published as a single-file executable and uses Windows' signed `winsqlite3.dll` through `SQLitePCLRaw.provider.winsqlite3`, so SQLitePCLRaw and native SQLite DLLs are not loose files in the installed service directory. The release script rejects Mark-of-the-Web streams and loose SQLite runtime DLLs. When signing is enabled, it signs every unsigned desktop/service PE binary plus the final setup executable in the correct build order; it does not create, download, or weaken validation for a certificate.
