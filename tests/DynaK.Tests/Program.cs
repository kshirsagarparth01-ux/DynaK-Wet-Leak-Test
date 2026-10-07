using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DynaK.Service.Configuration;
using DynaK.Service.Data;
using DynaK.Service.Exports;
using DynaK.Service.Logging;
using DynaK.Service.Models;
using DynaK.Service.Plc;
using DynaK.Service.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

await RunSync("overnight shift resolves before midnight", () =>
{
    var resolver = new ShiftResolver(AppSettings.CreateDefaultShifts());
    AssertEqual("SHIFT C", resolver.Resolve(new DateTimeOffset(2026, 8, 13, 23, 30, 0, TimeSpan.Zero)).Name);
});

await RunSync("overnight shift resolves after midnight", () =>
{
    var resolver = new ShiftResolver(AppSettings.CreateDefaultShifts());
    AssertEqual("SHIFT C", resolver.Resolve(new DateTimeOffset(2026, 8, 14, 2, 30, 0, TimeSpan.Zero)).Name);
});

await RunAsync("duplicate station sequence is rejected", async () =>
{
    var temp = Path.Combine(Path.GetTempPath(), $"dynak-test-{Guid.NewGuid():N}.db");
    var settings = Options.Create(new AppSettings { DatabasePath = temp });
    var store = new SettingsStore(settings, NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);
    var repo = new ProductionRepository(database);
    var record = SampleRecord();

    var first = await repo.InsertAsync(record, CancellationToken.None);
    var second = await repo.InsertAsync(record, CancellationToken.None);

    AssertTrue(first.Inserted, "first insert should succeed");
    AssertTrue(second.Duplicate, "second insert should be duplicate");
});

await RunAsync("database preserves Leak Test Value precision", async () =>
{
    var temp = Path.Combine(Path.GetTempPath(), $"dynak-precision-{Guid.NewGuid():N}.db");
    var store = new SettingsStore(Options.Create(new AppSettings { DatabasePath = temp }), NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);
    var records = new ProductionRepository(database);
    await records.InsertAsync(SampleRecord(1002) with { LeakTestValue = 0.48765m }, CancellationToken.None);

    var saved = await records.GetBySequenceIdAsync("TEST-STATION", 1002, CancellationToken.None);
    AssertEqual<decimal?>(0.48765m, saved?.LeakTestValue);

    await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT leak_test_value FROM production_records WHERE plc_sequence_id = 1002;";
    AssertEqual("0.48765", Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture));
});

await RunAsync("fresh database does not import legacy or demo production records", async () =>
{
    var testRoot = Path.Combine(Path.GetTempPath(), $"dynak-migration-{Guid.NewGuid():N}");
    var legacyPath = Path.Combine(testRoot, "legacy", "dynak-wet-leak-test.db");
    var machineRoot = Path.Combine(testRoot, "machine");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);

    var legacyStore = new SettingsStore(
        Options.Create(new AppSettings { DatabasePath = legacyPath }),
        NullLogger<SettingsStore>.Instance);
    var legacyDatabase = new SqliteDatabase(legacyStore, NullLogger<SqliteDatabase>.Instance);
    await legacyDatabase.InitializeAsync(CancellationToken.None);
    await new ProductionRepository(legacyDatabase).InsertAsync(SampleRecord(1050), CancellationToken.None);

    var previousRoot = Environment.GetEnvironmentVariable("DYNAK_DATA_ROOT");
    var previousLegacyPath = Environment.GetEnvironmentVariable("DYNAK_LEGACY_DATABASE_PATH");
    try
    {
        Environment.SetEnvironmentVariable("DYNAK_DATA_ROOT", machineRoot);
        Environment.SetEnvironmentVariable("DYNAK_LEGACY_DATABASE_PATH", legacyPath);

        var freshStore = new SettingsStore(
            Options.Create(new AppSettings { DatabasePath = "data/dynak-wet-leak-test.db" }),
            NullLogger<SettingsStore>.Instance);
        var freshDatabase = new SqliteDatabase(freshStore, NullLogger<SqliteDatabase>.Instance);
        await freshDatabase.InitializeAsync(CancellationToken.None);

        AssertEqual(0, await new ProductionRepository(freshDatabase).CountAsync(CancellationToken.None));
        AssertEqual(1, await new ProductionRepository(legacyDatabase).CountAsync(CancellationToken.None));
        AssertTrue(File.Exists(legacyPath), "legacy database must remain untouched");
        AssertTrue(File.Exists(Path.Combine(machineRoot, "data", "dynak-wet-leak-test.db")), "fresh durable database should exist under the machine data root");
    }
    finally
    {
        Environment.SetEnvironmentVariable("DYNAK_DATA_ROOT", previousRoot);
        Environment.SetEnvironmentVariable("DYNAK_LEGACY_DATABASE_PATH", previousLegacyPath);
    }
});

await RunAsync("v5 logical-part migration backs up and preserves existing history", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"dynak-v5-migration-{Guid.NewGuid():N}");
    var databasePath = Path.Combine(root, "station.db");
    var store = new SettingsStore(
        Options.Create(new AppSettings
        {
            DatabasePath = databasePath,
            LiveLeakValueFilePath = Path.Combine(root, "live_leak_value.txt")
        }),
        NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);
    var records = new ProductionRepository(database);
    await records.InsertAsync(SampleRecord(1060) with { QrCode = "QR-MIGRATION", PartNumber = "MIG-001" }, CancellationToken.None);

    await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
    {
        await SqliteDatabase.ExecuteAsync(connection, """
            PRAGMA foreign_keys=OFF;
            BEGIN TRANSACTION;
            ALTER TABLE logical_parts RENAME TO logical_parts_current;
            CREATE TABLE logical_parts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                station_id TEXT NOT NULL,
                qr_code TEXT NOT NULL,
                part_number TEXT NOT NULL,
                overall_result TEXT NOT NULL,
                latest_attempt_id INTEGER NULL,
                created_timestamp TEXT NOT NULL,
                updated_timestamp TEXT NOT NULL,
                UNIQUE (station_id, qr_code)
            );
            INSERT INTO logical_parts SELECT * FROM logical_parts_current;
            DROP TABLE logical_parts_current;
            DELETE FROM schema_migrations WHERE version = 5;
            PRAGMA user_version = 4;
            COMMIT;
            PRAGMA foreign_keys=ON;
            """, CancellationToken.None);
    }

    await database.InitializeAsync(CancellationToken.None);
    AssertEqual(1, await records.CountAsync(CancellationToken.None));
    await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT user_version FROM pragma_user_version;";
        AssertEqual(7L, (long)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0L));
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('production_records') WHERE name = 'serial_number';";
        AssertEqual(1L, (long)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0L));
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('production_records') WHERE name = 'plc_snapshot_json';";
        AssertEqual(1L, (long)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0L));
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('production_records') WHERE name IN ('cycle_time_seconds','cycle_completed','completed_timestamp');";
        AssertEqual(0L, (long)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0L));
    }
    AssertEqual("MIG-001", (await LogicalPartByNumberAsync(records, "MIG-001")).PartNumber);
    AssertEqual(1, Directory.GetFiles(root, "station.db.pre-v5-*.bak").Length);

    await records.InsertAsync(SampleRecord(1061) with { QrCode = "", PartNumber = "MIG-002" }, CancellationToken.None);
    await records.InsertAsync(SampleRecord(1062) with { QrCode = "", PartNumber = "MIG-003" }, CancellationToken.None);
    AssertEqual(3, await records.CountLogicalPartsAsync(CancellationToken.None));
});

await RunAsync("v7 migration removes obsolete timing lifecycle columns without losing history", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"dynak-v7-migration-{Guid.NewGuid():N}");
    var store = new SettingsStore(Options.Create(new AppSettings
    {
        DatabasePath = Path.Combine(root, "station.db"),
        LiveLeakValueFilePath = Path.Combine(root, "live_leak_value.txt")
    }), NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);
    var records = new ProductionRepository(database);
    await records.InsertAsync(SampleRecord(1090) with { PartNumber = "MIG-V7", QrCode = "QR-MIG-V7" }, CancellationToken.None);

    await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
    {
        await SqliteDatabase.ExecuteAsync(connection, """
            ALTER TABLE production_records ADD COLUMN cycle_time_seconds TEXT NULL;
            ALTER TABLE production_records ADD COLUMN cycle_completed INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE production_records ADD COLUMN completed_timestamp TEXT NULL;
            DELETE FROM schema_migrations WHERE version = 7;
            PRAGMA user_version = 6;
            """, CancellationToken.None);
    }

    await database.InitializeAsync(CancellationToken.None);
    AssertEqual(1, await records.CountAsync(CancellationToken.None));
    AssertEqual("MIG-V7", (await records.QueryAsync(new ProductionRecordQuery(null, null, null, "MIG-V7", null, null), CancellationToken.None)).Single().PartNumber);
    await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('production_records') WHERE name IN ('cycle_time_seconds','cycle_completed','completed_timestamp');";
        AssertEqual(0L, (long)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0L));
        command.CommandText = "SELECT user_version FROM pragma_user_version;";
        AssertEqual(7L, (long)(await command.ExecuteScalarAsync(CancellationToken.None) ?? 0L));
    }
});

await RunAsync("NG and rework attempts share one logical QR part", async () =>
{
    var (repo, _) = await CreateRepositoriesAsync();
    var firstTest = new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.Zero);
    var reworkTest = firstTest.AddMinutes(30);
    await repo.InsertAsync(SampleRecord(1101, firstTest, ProductionResult.NG) with { QrCode = "DYN240812002271" }, CancellationToken.None);
    await repo.InsertAsync(SampleRecord(1102, reworkTest, ProductionResult.OK) with { QrCode = "DYN240812002271" }, CancellationToken.None);

    var parts = await repo.QueryLogicalPartsAsync(new LogicalPartQuery(null, null, null, null, null, null, 10), CancellationToken.None);
    var details = await repo.GetLogicalPartByIdAsync(parts[0].Id, CancellationToken.None);

    AssertEqual(1, parts.Count);
    AssertEqual("NG-REWORK", parts[0].OverallResult);
    AssertEqual(2, details!.Attempts.Count);
    AssertEqual("NG", details.Attempts[0].Result);
    AssertEqual("OK", details.Attempts[1].Result);
});

await RunAsync("recent parts returns ten logical QR parts", async () =>
{
    var (repo, _) = await CreateRepositoriesAsync();
    var start = new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.Zero);
    for (var index = 0; index < 12; index++)
    {
        await repo.InsertAsync(SampleRecord(1200 + index, start.AddMinutes(index), ProductionResult.OK), CancellationToken.None);
    }

    await repo.InsertAsync(SampleRecord(1300, start.AddHours(1), ProductionResult.REWORK) with { QrCode = "DYNTEST1200" }, CancellationToken.None);
    var parts = await repo.QueryLogicalPartsAsync(new LogicalPartQuery(null, null, null, null, null, null, 10), CancellationToken.None);

    AssertEqual(12, await repo.CountLogicalPartsAsync(CancellationToken.None));
    AssertEqual(10, parts.Count);
    AssertEqual(10, parts.Select(part => part.QrCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
});

await RunAsync("overnight shift counters span midnight", async () =>
{
    var temp = Path.Combine(Path.GetTempPath(), $"dynak-test-{Guid.NewGuid():N}.db");
    var settings = Options.Create(new AppSettings { DatabasePath = temp });
    var store = new SettingsStore(settings, NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);
    var repo = new ProductionRepository(database);
    var beforeMidnight = new DateTimeOffset(2026, 8, 13, 23, 30, 0, TimeSpan.Zero);
    var afterMidnight = new DateTimeOffset(2026, 8, 14, 2, 30, 0, TimeSpan.Zero);
    await repo.InsertAsync(SampleRecord(2001, beforeMidnight, ProductionResult.OK), CancellationToken.None);
    await repo.InsertAsync(SampleRecord(2002, afterMidnight, ProductionResult.REWORK), CancellationToken.None);

    var window = new ShiftResolver(AppSettings.CreateDefaultShifts()).ResolveWindow(afterMidnight);
    var counters = await repo.GetCountersForWindowAsync("TEST-STATION", "SHIFT C", window.StartsAt, window.EndsAt, CancellationToken.None);

    AssertEqual(2, counters.Actual);
    AssertEqual(1, counters.Ok);
    AssertEqual(1, counters.Rework);
});

await RunAsync("history filters by date shift part QR and result", async () =>
{
    var (repo, _) = await CreateRepositoriesAsync();
    var morning = new DateTimeOffset(2026, 8, 13, 7, 15, 0, TimeSpan.Zero);
    var afternoon = new DateTimeOffset(2026, 8, 13, 15, 15, 0, TimeSpan.Zero);
    await repo.InsertAsync(SampleRecord(4001, morning, ProductionResult.OK) with { PartNumber = "78654-A01", QrCode = "QR-A" }, CancellationToken.None);
    await repo.InsertAsync(SampleRecord(4002, afternoon, ProductionResult.NG) with { PartNumber = "78654-B02", QrCode = "QR-B" }, CancellationToken.None);

    var query = new ProductionRecordQuery(
        new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 13, 23, 59, 59, TimeSpan.Zero),
        "SHIFT B",
        "B02",
        "QR-B",
        ProductionResult.NG,
        20);

    var records = await repo.QueryAsync(query, CancellationToken.None);

    AssertEqual(1, records.Count);
    AssertEqual(4002L, records[0].PlcSequenceId);
});

await RunAsync("part history Excel export uses filtered logical records and production columns", async () =>
{
    var (repo, _) = await CreateRepositoriesAsync();
    var firstTest = new DateTimeOffset(2026, 8, 13, 7, 15, 0, TimeSpan.Zero);
    var reworkTest = new DateTimeOffset(2026, 8, 13, 15, 45, 0, TimeSpan.Zero);
    var otherShift = new DateTimeOffset(2026, 8, 14, 7, 15, 0, TimeSpan.Zero);
    await repo.InsertAsync(SampleRecord(4101, firstTest, ProductionResult.NG) with
    {
        SerialNumber = "SN-OLD-001",
        PartNumber = "78654-B02",
        QrCode = "QR-EXPORT-COMPLETE-001",
        LeakTestValue = 0.123m,
        ResolvedMode = "Manual",
        IsAutoMode = false,
        ErrorCode = "2",
        ErrorDescription = "Fixture clamp low"
    }, CancellationToken.None);
    await repo.InsertAsync(SampleRecord(4102, reworkTest, ProductionResult.OK) with
    {
        SerialNumber = "SN-EXPORT-001",
        PartNumber = "78654-B02",
        QrCode = "QR-EXPORT-COMPLETE-001",
        LeakTestValue = 0.4876m,
        LowerLimit = 0.010m,
        UpperLimit = 0.080m,
        ResolvedMode = "Auto",
        ErrorCode = "0",
        ErrorDescription = "No Error"
    }, CancellationToken.None);
    await repo.InsertAsync(SampleRecord(4103, otherShift, ProductionResult.OK) with
    {
        PartNumber = "78654-A01",
        QrCode = "QR-EXPORT-SKIP-002"
    }, CancellationToken.None);

    var parts = await repo.QueryLogicalPartsAsync(new LogicalPartQuery(
        new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
        "SHIFT B",
        "B02",
        "QR-EXPORT",
        "NG",
        null), CancellationToken.None);
    var exportLocalTime = new DateTime(2026, 8, 17, 16, 25, 0);
    var workbook = PartHistoryExcelExporter.CreateWorkbook(parts, new DateTimeOffset(exportLocalTime, TimeZoneInfo.Local.GetUtcOffset(exportLocalTime)));
    var savedReportPath = Path.Combine(Path.GetTempPath(), $"dynak-history-{Guid.NewGuid():N}.xlsx");
    try
    {
        await File.WriteAllBytesAsync(savedReportPath, workbook);
        AssertEqual(workbook.Length, new FileInfo(savedReportPath).Length);
        PartHistoryExcelExporter.ValidateWorkbook(await File.ReadAllBytesAsync(savedReportPath), parts.Count);
    }
    finally
    {
        File.Delete(savedReportPath);
    }

    var sheet = ReadWorkbookXml(workbook, "xl/worksheets/sheet1.xml");
    var styles = ReadWorkbookXml(workbook, "xl/styles.xml");
    var workbookXml = ReadWorkbookXml(workbook, "xl/workbook.xml");
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    AssertEqual("Part History", workbookXml.Descendants(ns + "sheet").Single().Attribute("name")?.Value);
    // <!-- Changed manually using GPT -->
    AssertEqual("A3:H4", sheet.Descendants(ns + "autoFilter").Single().Attribute("ref")?.Value);
    // AssertEqual("A3:I4", sheet.Descendants(ns + "autoFilter").Single().Attribute("ref")?.Value);
    AssertEqual("frozen", sheet.Descendants(ns + "pane").Single().Attribute("state")?.Value);
    AssertEqual(1, parts.Count);

    var cells = SheetCells(sheet);
    // <!-- Changed manually using GPT -->
    var headers = Enumerable.Range(1, 8).Select(column => CellText(cells[CellName(column, 3)])).ToArray();
    // var headers = Enumerable.Range(1, 9).Select(column => CellText(cells[CellName(column, 3)])).ToArray();
    // <!-- Changed manually using GPT -->
    AssertEqual(
        "SR NO.|Date|Time|Shift|Part No.|Leak Test Value (LPM)|Leak OK Range (LPM)|Result / Status",
        string.Join("|", headers));
    // AssertEqual("SR NO.|Date|Time|Shift|Part No.|QR Code|Leak Test Value|Leak OK Range|Result / Status", string.Join("|", headers));
    AssertEqual("DYNAK WET LEAK TEST - PART HISTORY", CellText(cells["A1"]));
    AssertEqual("Exported: 17-08-2026 16:25", CellText(cells["A2"]));
    AssertEqual("SN-EXPORT-001", CellText(cells["A4"]));
    AssertEqual("SHIFT B", CellText(cells["D4"]));
    AssertEqual("78654-B02", CellText(cells["E4"]));
    // <!-- Changed manually using GPT -->
    AssertEqual("0.4876", CellValue(cells["F4"]));
    AssertEqual("0.01 TO 0.080", CellText(cells["G4"]));
    AssertEqual("NG-REWORK", CellText(cells["H4"]));
    // AssertEqual("QR-EXPORT-COMPLETE-001", CellText(cells["F4"]));
    // AssertEqual("0.4876", CellValue(cells["G4"]));
    // AssertEqual("0.01 TO 0.080", CellText(cells["H4"]));
    // AssertEqual("NG-REWORK", CellText(cells["I4"]));
    AssertTrue(cells["B4"].Attribute("t") is null, "date should be written as a numeric Excel date cell");
    AssertTrue(cells["C4"].Attribute("t") is null, "time should be written as a numeric Excel time cell");
    // <!-- Changed manually using GPT -->
    AssertTrue(cells["G4"].Attribute("t") is null, "leak value should remain numeric");
    // AssertTrue(cells["G4"].Attribute("t") is null, "leak value should remain numeric");
    var leakNumberFormat = styles.Descendants(ns + "numFmt").Single(format => format.Attribute("numFmtId")?.Value == "166");
    AssertEqual("0.0000", leakNumberFormat.Attribute("formatCode")?.Value);
    // <!-- Changed manually using GPT -->
    var leakCellFormat = styles.Descendants(ns + "cellXfs")
        .Single()
        .Elements(ns + "xf")
        .ElementAt(int.Parse(cells["F4"].Attribute("s")!.Value, CultureInfo.InvariantCulture));
    // var leakCellFormat = styles.Descendants(ns + "cellXfs").Single().Elements(ns + "xf").ElementAt(int.Parse(cells["G4"].Attribute("s")!.Value, CultureInfo.InvariantCulture));
    AssertEqual("166", leakCellFormat.Attribute("numFmtId")?.Value);
    // <!-- Changed manually using GPT -->
    // AssertTrue(sheet.Descendants(ns + "col").Any(col => col.Attribute("min")?.Value == "6" && DecimalAttribute(col, "width") >= 30m), "QR column should be wide");
    AssertEqual(1, sheet.Descendants(ns + "row").Count(row => int.Parse(row.Attribute("r")!.Value, CultureInfo.InvariantCulture) >= 4));
});

await RunAsync("configuration saving and loading persists PLC shifts and mappings", async () =>
{
    var testRoot = Path.Combine(Path.GetTempPath(), $"dynak-config-{Guid.NewGuid():N}");
    var temp = Path.Combine(testRoot, "data", "original.db");
    var selectedDatabase = Path.Combine(testRoot, "selected", "station.db");
    var liveLeakValuePath = Path.Combine(testRoot, "live", "leak.txt");
    var reportRoot = Path.Combine(testRoot, "reports", "DynaK Leak Test Report");
    var configurationPath = Path.Combine(testRoot, "config", "appsettings.json");
    var settings = Options.Create(new AppSettings { DatabasePath = temp });
    var store = new SettingsStore(settings, NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);
    var config = new ConfigRepository(database, configurationPath);

    store.Apply(new SettingsUpdate(
        "Station Alpha",
        selectedDatabase,
        "kPa",
        new PlcConnectionSettings
        {
            IpAddress = "192.168.1.10",
            Port = 502,
            UnitId = 1,
            PollingIntervalMs = 250,
            ReconnectIntervalMs = 2000,
            ConnectionTimeoutMs = 1500
        },
        [
            new("SHIFT X", new TimeOnly(8, 0), new TimeOnly(20, 0)),
            new("SHIFT Y", new TimeOnly(20, 0), new TimeOnly(8, 0))
        ],
        [
            new PlcSignalMapping
            {
                SignalName = "Target Parts Per Shift",
                Address = "D2200",
                AddressType = "D Register",
                DataType = "UInt16",
                Direction = "Read",
                Length = 1,
                Enabled = true
            },
            .. PlcSignalMapping.CreateDefaults().Where(m => m.SignalName != "Target Parts Per Shift")
        ],
        null,
        liveLeakValuePath,
        0m,
        0.750m,
        reportRoot,
        false));
    await config.SaveAsync(store.Current, CancellationToken.None);

    var reloadedStore = new SettingsStore(Options.Create(await config.LoadMachineAsync(CancellationToken.None)), NullLogger<SettingsStore>.Instance);
    var reloaded = reloadedStore.Current;

    AssertEqual("Station Alpha", reloaded.StationName);
    AssertEqual("kPa", reloaded.LeakTestUnit);
    AssertEqual("192.168.1.10", reloaded.Plc.IpAddress);
    AssertEqual(502, reloaded.Plc.Port);
    AssertEqual(selectedDatabase, reloaded.DatabasePath);
    AssertEqual(liveLeakValuePath, reloaded.LiveLeakValueFilePath);
    AssertEqual(0m, reloaded.LowerLimit);
    AssertEqual(0.750m, reloaded.UpperLimit);
    AssertEqual(reportRoot, reloaded.ReportRootFolder);
    AssertTrue(!reloaded.AutomaticDailyExportEnabled, "automatic report setting should persist");
    AssertEqual("SHIFT Y", reloaded.Shifts[1].Name);
    AssertTrue(reloaded.SignalMappings.Any(m => m.SignalName == "Target Parts Per Shift" && m.Enabled && m.Address == "D2200"), "mapping should persist");
});

await RunAsync("database switch opens an empty database without copying old production data", async () =>
{
    var testRoot = Path.Combine(Path.GetTempPath(), $"dynak-switch-{Guid.NewGuid():N}");
    var originalPath = Path.Combine(testRoot, "original.db");
    var selectedPath = Path.Combine(testRoot, "selected", "station.db");
    var store = new SettingsStore(Options.Create(new AppSettings { DatabasePath = originalPath }), NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);
    var records = new ProductionRepository(database);
    var existing = SampleRecord(4500);
    await records.InsertAsync(existing, CancellationToken.None);

    var state = new AcquisitionState();
    state.Update(existing);
    var candidate = store.BuildCandidate(new SettingsUpdate(null, selectedPath, null, null, null, null, null));
    await database.PrepareAsync(candidate.DatabasePath, CancellationToken.None);
    store.Activate(candidate);
    database.Activate(candidate.DatabasePath);
    state.ClearProductionData();

    AssertEqual(Path.GetFullPath(selectedPath), database.DatabasePath);
    AssertEqual(0, await records.CountAsync(CancellationToken.None));
    AssertTrue(StateSnapshot(state, store.Current).CurrentPart is null, "database switch must clear the previous in-memory current part");

    var originalStore = new SettingsStore(Options.Create(new AppSettings { DatabasePath = originalPath }), NullLogger<SettingsStore>.Instance);
    var originalDatabase = new SqliteDatabase(originalStore, NullLogger<SqliteDatabase>.Instance);
    await originalDatabase.InitializeAsync(CancellationToken.None);
    AssertEqual(1, await new ProductionRepository(originalDatabase).CountAsync(CancellationToken.None));
});

await RunAsync("database switch rejects an unrelated SQLite file", async () =>
{
    var testRoot = Path.Combine(Path.GetTempPath(), $"dynak-incompatible-{Guid.NewGuid():N}");
    var originalPath = Path.Combine(testRoot, "original.db");
    var unrelatedPath = Path.Combine(testRoot, "unrelated.db");
    var store = new SettingsStore(Options.Create(new AppSettings { DatabasePath = originalPath }), NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);

    await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={unrelatedPath}"))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unrelated_data (id INTEGER PRIMARY KEY);";
        await command.ExecuteNonQueryAsync();
    }

    await AssertThrowsAsync<DatabaseConfigurationException>(() => database.PrepareAsync(unrelatedPath, CancellationToken.None));
    AssertEqual(Path.GetFullPath(originalPath), database.DatabasePath);
});

await RunSync("shift definitions do not contain a target", () =>
{
    var json = JsonSerializer.Serialize(AppSettings.CreateDefaultShifts());
    AssertTrue(!json.Contains("Target", StringComparison.OrdinalIgnoreCase), json);
});

await RunSync("persisted settings cannot redirect the machine-owned database path", () =>
{
    var configured = new AppSettings { DatabasePath = "data/dynak-wet-leak-test.db" };
    var persisted = configured.Clone();
    persisted.DatabasePath = "bin/Debug/data/obsolete.db";
    var store = new SettingsStore(Options.Create(configured), NullLogger<SettingsStore>.Instance);

    store.ApplyPersisted(new Dictionary<string, string>
    {
        ["app_settings_json"] = JsonSerializer.Serialize(persisted, new JsonSerializerOptions(JsonSerializerDefaults.Web))
    });

    AssertEqual(configured.DatabasePath, store.Current.DatabasePath);
});

await RunSync("invalid configuration is rejected", () =>
{
    var store = new SettingsStore(Options.Create(new AppSettings()), NullLogger<SettingsStore>.Instance);

    AssertThrows<SettingsValidationException>(() => store.Apply(new SettingsUpdate(
        "",
        null,
        "bar",
        new PlcConnectionSettings
        {
            IpAddress = "not-an-ip",
            Port = 70000,
            PollingIntervalMs = 1,
            ReconnectIntervalMs = 1,
            ConnectionTimeoutMs = 1
        },
        [new("SHIFT A", new TimeOnly(6, 0), new TimeOnly(14, 0))],
        PlcSignalMapping.CreateDefaults(),
        null)));
});

await RunSync("Leak OK limits require minimum less than maximum", () =>
{
    var store = new SettingsStore(Options.Create(new AppSettings()), NullLogger<SettingsStore>.Instance);
    var exception = AssertThrows<SettingsValidationException>(() => store.BuildCandidate(new SettingsUpdate(
        null, null, null, null, null, null, null, null, 0.500m, 0.500m)));
    AssertTrue(exception.Errors.Contains("Leak OK minimum must be less than Leak OK maximum."), exception.Message);
});

await RunSync("invalid configuration does not replace active settings", () =>
{
    var store = new SettingsStore(Options.Create(new AppSettings()), NullLogger<SettingsStore>.Instance);
    var before = store.Current;

    AssertThrows<SettingsValidationException>(() => store.BuildCandidate(new SettingsUpdate(
        null,
        null,
        null,
        new PlcConnectionSettings { IpAddress = "not-an-ip", Port = 502 },
        null,
        null,
        null)));

    var after = store.Current;
    AssertEqual(before.Plc.IpAddress, after.Plc.IpAddress);
    AssertEqual(before.Plc.Port, after.Plc.Port);
});

await RunSync("configuration candidate activates only after explicit activation", () =>
{
    var store = new SettingsStore(Options.Create(new AppSettings()), NullLogger<SettingsStore>.Instance);
    var candidate = store.BuildCandidate(new SettingsUpdate(
        "Station Beta",
        null,
        null,
        new PlcConnectionSettings { IpAddress = "192.168.10.20", Port = 502 },
        null,
        null,
        null));

    AssertEqual("DynaK Wet Leak Test Station", store.Current.StationName);
    store.Activate(candidate);
    AssertEqual("Station Beta", store.Current.StationName);
    AssertEqual("192.168.10.20", store.Current.Plc.IpAddress);
});

await RunSync("default PLC status and enum mappings are editable signal value maps", () =>
{
    var mappings = PlcSignalMapping.CreateDefaults();
    var ok = mappings.First(m => m.SignalName == PlcSignalMapping.OkResultSignalName);
    var ng = mappings.First(m => m.SignalName == PlcSignalMapping.NgResultSignalName);
    AssertEqual("D1010", ok.Address);
    AssertEqual("1=OK", ok.ValueMap);
    AssertEqual("D1011", ng.Address);
    AssertEqual("1=NG", ng.ValueMap);
    AssertEqual("1=Auto;2=Manual", mappings.First(m => m.SignalName == "Auto / Manual").ValueMap);
    AssertEqual("0=No Error;1=Error 1;2=Error 2;3=Error 3;4=Error 4;5=Error 5", mappings.First(m => m.SignalName == "Error").ValueMap);
    AssertEqual("0=Stopped;1=Running;2=Status 2;3=Status 3;4=Status 4;5=Status 5", mappings.First(m => m.SignalName == "Running Status").ValueMap);
});

await RunSync("legacy combined PLC result mapping migrates to separate OK and NG registers", () =>
{
    var store = new SettingsStore(Options.Create(new AppSettings()), NullLogger<SettingsStore>.Instance);
    var mappings = PlcSignalMapping.CreateDefaults()
        .Where(mapping => mapping.SignalName != PlcSignalMapping.OkResultSignalName && mapping.SignalName != PlcSignalMapping.NgResultSignalName)
        .ToList();
    var combined = PlcSignalMapping.CreateDefaults().First(m => m.SignalName == PlcSignalMapping.OkResultSignalName).Clone();
    combined.SignalName = PlcSignalMapping.LegacyCombinedResultSignalName;
    combined.Address = "D2010";
    combined.DataType = "Int16";
    combined.ValueMap = "1=OK;2=NG / Rework";
    mappings.Insert(2, combined);

    var candidate = store.BuildCandidate(new SettingsUpdate(null, null, null, null, null, mappings, null));
    var ok = candidate.SignalMappings.First(m => m.SignalName == PlcSignalMapping.OkResultSignalName);
    var ng = candidate.SignalMappings.First(m => m.SignalName == PlcSignalMapping.NgResultSignalName);

    AssertEqual("D1010", ok.Address);
    AssertEqual("Int16", ok.DataType);
    AssertEqual("1=OK", ok.ValueMap);
    AssertEqual("D1011", ng.Address);
    AssertEqual("Int16", ng.DataType);
    AssertEqual("1=NG", ng.ValueMap);
    AssertTrue(candidate.SignalMappings.All(m => !PlcSignalMapping.IsCombinedResultSignalName(m.SignalName)), "combined result mapping should not survive normalization");
});

await RunSync("default PLC spans match reserved D-register ranges", () =>
{
    var mappings = PlcSignalMapping.CreateDefaults();
    AssertEqual("AsciiString", mappings.First(m => m.SignalName == "Date").DataType);
    AssertEqual(5, mappings.First(m => m.SignalName == "Date").Length);
    AssertEqual("D1020", mappings.First(m => m.SignalName == "Time").Address);
    AssertEqual("Int16", mappings.First(m => m.SignalName == "Time").DataType);
    AssertEqual(3, mappings.First(m => m.SignalName == "Time").Length);
    AssertEqual("AsciiString", mappings.First(m => m.SignalName == "Leak Test Value").DataType);
    AssertEqual(5, mappings.First(m => m.SignalName == "Leak Test Value").Length);
    AssertEqual(10, mappings.First(m => m.SignalName == "QR Code Value").Length);
    AssertEqual(10, mappings.First(m => m.SignalName == "Part Number").Length);
    var partDataReady = mappings.First(m => m.SignalName == PlcSignalMapping.PartDataReadySignalName);
    AssertEqual("D1075", partDataReady.Address);
    AssertEqual("Read", partDataReady.Direction);
    AssertEqual("0=LOW;1=HIGH", partDataReady.ValueMap);
});

await RunSync("packaged configuration does not index-merge PLC mapping defaults with machine settings", () =>
{
    var packagedPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    using var document = JsonDocument.Parse(File.ReadAllText(packagedPath));
    var packagedMappings = document.RootElement.GetProperty("DynaK").GetProperty("SignalMappings");

    AssertEqual(JsonValueKind.Array, packagedMappings.ValueKind);
    AssertEqual(0, packagedMappings.GetArrayLength());
    AssertEqual(17, PlcSignalMapping.CreateDefaults().Count);
});

await RunSync("Serial Number PLC mapping defaults to editable D2000 start and stays disabled until fully commissioned", () =>
{
    var serial = PlcSignalMapping.CreateDefaults().Single(mapping => mapping.SignalName == "Serial Number");
    AssertTrue(!serial.Enabled, "Serial Number must be disabled by default");
    AssertEqual("D2000", serial.Address);
    AssertEqual("D Register", serial.AddressType);
    AssertEqual("", serial.DataType);
    AssertEqual<int?>(null, serial.Length);
    SettingsStore.Validate(new AppSettings
    {
        Shifts = AppSettings.CreateDefaultShifts(),
        SignalMappings = PlcSignalMapping.CreateDefaults()
    });
    AssertTrue(!PlcSignalMapping.GetOperationalValidationErrors(PlcSignalMapping.CreateDefaults()).Any(error => error.Contains("Serial Number", StringComparison.Ordinal)), "disabled Serial Number must not block operation");

    var legacyMappings = PlcSignalMapping.CreateDefaults()
        .Where(mapping => mapping.SignalName != "Serial Number")
        .ToList();
    var upgraded = new SettingsStore(
        Options.Create(new AppSettings
        {
            Shifts = AppSettings.CreateDefaultShifts(),
            SignalMappings = legacyMappings
        }),
        NullLogger<SettingsStore>.Instance).Current;
    var injectedSerial = upgraded.SignalMappings.Single(mapping => mapping.SignalName == "Serial Number");
    AssertEqual("D2000", injectedSerial.Address);
    AssertEqual("D Register", injectedSerial.AddressType);
    AssertTrue(!injectedSerial.Enabled, "Serial Number must be injected disabled into older PLC mapping configurations");
});

await RunSync("operational PLC mapping validation identifies each blocking field", () =>
{
    var mappings = PlcSignalMapping.CreateDefaults();
    mappings.First(mapping => mapping.SignalName == "System Ready").Enabled = false;
    mappings.First(mapping => mapping.SignalName == "Leak Test Value").Address = null;
    mappings.First(mapping => mapping.SignalName == "QR Code Value").Length = 0;
    mappings.First(mapping => mapping.SignalName == "Communication OK").Address = "D-not-a-register";

    var errors = PlcSignalMapping.GetOperationalValidationErrors(mappings);

    AssertTrue(!errors.Contains("System Ready is disabled."), string.Join(" ", errors));
    AssertTrue(errors.Contains("Leak Test Value has no address."), string.Join(" ", errors));
    AssertTrue(errors.Contains("QR Code Value requires a register length."), string.Join(" ", errors));
    AssertTrue(errors.Contains("Communication OK address 'D-not-a-register' is invalid."), string.Join(" ", errors));
});

await RunSync("PLC diagnostic exposes live values for every configured mapping", () =>
{
    var mappings = PlcSignalMapping.CreateDefaults();
    var values = new Dictionary<string, PlcSignalValue>(StringComparer.OrdinalIgnoreCase)
    {
        [PlcSignalMapping.OkResultSignalName] = new PlcSignalValue(1m, "OK", true),
        [PlcSignalMapping.NgResultSignalName] = new PlcSignalValue(0m, 0m, false),
        ["Time"] = new PlcSignalValue("154218", "15:42:18", false),
        ["QR Code Value"] = new PlcSignalValue("QR-123", "QR-123", false)
    };

    var signals = PlcCommissioningDiagnostic.BuildSignals(mappings, values);

    AssertEqual(mappings.Count, signals.Count);
    AssertEqual("OK", signals.First(s => s.SignalName == PlcSignalMapping.OkResultSignalName).InterpretedValue);
    AssertEqual("15:42:18", signals.First(s => s.SignalName == "Time").InterpretedValue);
    AssertEqual("QR-123", signals.First(s => s.SignalName == "QR Code Value").RawValue);

    AssertTrue(!PlcCommissioningDiagnostic.From(new AppSettings { SignalMappings = mappings }).ReadOnly, "enabled handshake mappings should not be shown as read-only");
    mappings.First(mapping => mapping.SignalName == "Data Saved").Enabled = false;
    AssertTrue(PlcCommissioningDiagnostic.From(new AppSettings { SignalMappings = mappings }).ReadOnly, "a disabled handshake mapping should be shown as read-only");
});

await RunSync("separate PLC result status registers resolve only one active result", () =>
{
    static PlcSignalValue Status(decimal raw, string? interpreted, bool matched) =>
        new(raw, interpreted is null ? raw : interpreted, matched);

    var ok = PlcResultResolver.Resolve(new Dictionary<string, PlcSignalValue>(StringComparer.OrdinalIgnoreCase)
    {
        [PlcSignalMapping.OkResultSignalName] = Status(1, "OK", true),
        [PlcSignalMapping.NgResultSignalName] = Status(0, null, false)
    });
    AssertEqual("1", ok.RawText);
    AssertEqual("OK", ok.ResolvedText);

    var ng = PlcResultResolver.Resolve(new Dictionary<string, PlcSignalValue>(StringComparer.OrdinalIgnoreCase)
    {
        [PlcSignalMapping.OkResultSignalName] = Status(0, null, false),
        [PlcSignalMapping.NgResultSignalName] = Status(1, "NG", true)
    });
    AssertEqual("1", ng.RawText);
    AssertEqual("NG", ng.ResolvedText);

    var conflict = PlcResultResolver.Resolve(new Dictionary<string, PlcSignalValue>(StringComparer.OrdinalIgnoreCase)
    {
        [PlcSignalMapping.OkResultSignalName] = Status(1, "OK", true),
        [PlcSignalMapping.NgResultSignalName] = Status(1, "NG", true)
    });
    AssertEqual("Unknown Result (OK and NG are both active)", conflict.ResolvedText);
});

await RunSync("all configurable PLC mapping fields survive settings normalization", () =>
{
    var store = new SettingsStore(Options.Create(new AppSettings()), NullLogger<SettingsStore>.Instance);
    var mappings = PlcSignalMapping.CreateDefaults();
    var edited = mappings.First(mapping => mapping.SignalName == "QR Code Value");
    edited.Address = "D2070";
    edited.AddressType = "Holding Register";
    edited.DataType = "AsciiString";
    edited.Direction = "ReadWrite";
    edited.Length = 7;
    edited.ScalingFactor = 2.5m;
    edited.Offset = -1.25m;
    edited.ByteOrder = "BADC";
    edited.WordOrder = "LowHigh";
    edited.Encoding = "UTF-8";
    edited.Format = "000000";
    edited.ValueMap = "A=Accepted;R=Rejected";
    edited.Enabled = false;
    edited.Description = "Commissioned mapping";

    var candidate = store.BuildCandidate(new SettingsUpdate(null, null, null, null, null, mappings, null));
    var actual = candidate.SignalMappings.First(mapping => mapping.SignalName == "QR Code Value");

    AssertEqual("D2070", actual.Address);
    AssertEqual("Holding Register", actual.AddressType);
    AssertEqual("AsciiString", actual.DataType);
    AssertEqual("ReadWrite", actual.Direction);
    AssertEqual(7, actual.Length);
    AssertEqual(2.5m, actual.ScalingFactor);
    AssertEqual(-1.25m, actual.Offset);
    AssertEqual("BADC", actual.ByteOrder);
    AssertEqual("LowHigh", actual.WordOrder);
    AssertEqual("UTF-8", actual.Encoding);
    AssertEqual("000000", actual.Format);
    AssertEqual("A=Accepted;R=Rejected", actual.ValueMap);
    AssertTrue(!actual.Enabled, "edited mapping enabled state should survive");
    AssertEqual("Commissioned mapping", actual.Description);
});

await RunSync("PLC value decoder supports required datatypes", () =>
{
    AssertEqual(true, DecodePlc("Bool", [1]));
    AssertEqual(-1m, DecodePlc("Int16", [0xFFFF]));
    AssertEqual(65535m, DecodePlc("UInt16", [0xFFFF]));
    AssertEqual(-2m, DecodePlc("Int32", [0xFFFF, 0xFFFE]));
    AssertEqual(65537m, DecodePlc("UInt32", [0x0001, 0x0001]));
    AssertEqual(-2m, DecodePlc("Int64", [0xFFFF, 0xFFFF, 0xFFFF, 0xFFFE]));
    AssertEqual(4294967297m, DecodePlc("UInt64", [0x0000, 0x0001, 0x0000, 0x0001]));
    AssertEqual(1.5m, DecodePlc("Float32", [0x3FC0, 0x0000]));
    AssertEqual(1.5m, DecodePlc("Float64", [0x3FF8, 0x0000, 0x0000, 0x0000]));
    AssertEqual(43981m, DecodePlc("Word16", [0xABCD]));
    AssertEqual(65538m, DecodePlc("DWord32", [0x0001, 0x0002]));
    AssertEqual(4294967297m, DecodePlc("QWord64", [0x0000, 0x0001, 0x0000, 0x0001]));
    AssertEqual("QR-123ABCD", DecodePlc("AsciiString", [0x5152, 0x2D31, 0x3233, 0x4142, 0x4344], 5));
    AssertEqual(1234m, DecodePlc("Bcd16", [0x1234]));
    AssertEqual(123456m, DecodePlc("Bcd32", [0x0012, 0x3456]));
});

await RunSync("scalar PLC mappings accept configured ranges at or above their minimum", () =>
{
    foreach (var count in new[] { 1, 2, 5 })
    {
        var mappings = PlcSignalMapping.CreateDefaults();
        var actual = mappings.First(mapping => mapping.SignalName == "Actual Part Count");
        actual.DataType = "Int16";
        actual.Length = count;

        SettingsStore.Validate(new AppSettings { Shifts = AppSettings.CreateDefaultShifts(), SignalMappings = mappings });
        AssertEqual(count, PlcSignalMapping.RegisterCount(actual));
        AssertEqual(123m, DecodePlc("Int16", Enumerable.Repeat((ushort)123, count).ToArray(), count));
    }
});

await RunSync("ASCII PLC mapping decodes its entire configured range", () =>
{
    AssertEqual("0123456789", DecodePlc("AsciiString", [0x3031, 0x3233, 0x3435, 0x3637, 0x3839], 5));
});

await RunSync("numeric Time range decodes D1020-D1022 as hour minute second", () =>
{
    var mapping = new PlcSignalMapping
    {
        SignalName = "Time",
        DataType = "Int16",
        Length = 3,
        ByteOrder = "ABCD",
        WordOrder = "HighLow"
    };

    AssertEqual("16:42:18", PlcValueDecoder.Decode(mapping, [16, 42, 18]).Value);
    AssertThrows<PlcSignalMappingException>(() => PlcValueDecoder.Decode(mapping, [24, 0, 0]));
});

await RunSync("PLC ASCII decoder handles spans nulls padding and decimal text", () =>
{
    AssertEqual("0.034", DecodePlc("AsciiString", [0x302E, 0x3033, 0x3420, 0x0000, 0x0000], 5));
    AssertEqual("ABC", DecodePlc("AsciiString", [0x4142, 0x4300, 0x5A5A], 3));
    AssertEqual("PN-1", DecodePlc("AsciiString", [0x504E, 0x2D31, 0x2020, 0x2020], 4));
    AssertEqual("é", DecodePlc("AsciiString", [0xC3A9, 0x0000], 2, "UTF-8"));
});

await RunSync("invalid PLC datatype and register spans are rejected", () =>
{
    AssertThrows<PlcSignalMappingException>(() => DecodePlc("Int32", [0x0001]));
    AssertThrows<PlcSignalMappingException>(() => DecodePlc("NotAType", [0x0001]));
    AssertThrows<PlcSignalMappingException>(() => DecodePlc("Bcd16", [0x12FA]));

    var overlap = PlcSignalMapping.CreateDefaults();
    overlap.First(m => m.SignalName == "Date").Length = 7;
    AssertThrows<SettingsValidationException>(() => SettingsStore.Validate(new AppSettings { SignalMappings = overlap }));

    var badPartSpan = PlcSignalMapping.CreateDefaults();
    badPartSpan.First(m => m.SignalName == "Part Number").Length = 41;
    AssertThrows<SettingsValidationException>(() => SettingsStore.Validate(new AppSettings { SignalMappings = badPartSpan }));

    var zeroStringLength = PlcSignalMapping.CreateDefaults();
    zeroStringLength.First(m => m.SignalName == "QR Code Value").Length = 0;
    AssertThrows<SettingsValidationException>(() => SettingsStore.Validate(new AppSettings { SignalMappings = zeroStringLength }));

    var inverseHandshake = PlcSignalMapping.CreateDefaults();
    inverseHandshake.First(m => m.SignalName == "System Ready").ValueMap = "0=ON;1=OFF";
    SettingsStore.Validate(new AppSettings { Shifts = AppSettings.CreateDefaultShifts(), SignalMappings = inverseHandshake });

    var incompleteHandshake = PlcSignalMapping.CreateDefaults();
    incompleteHandshake.First(m => m.SignalName == "Data Saved").ValueMap = "1=ON";
    var incomplete = AssertThrows<SettingsValidationException>(() => SettingsStore.Validate(new AppSettings { SignalMappings = incompleteHandshake }));
    AssertTrue(incomplete.Errors.Any(error => error.Contains("requires distinct ON and OFF", StringComparison.Ordinal)), string.Join(" ", incomplete.Errors));

    var writableTrigger = PlcSignalMapping.CreateDefaults();
    writableTrigger.First(m => m.SignalName == PlcSignalMapping.PartDataReadySignalName).Direction = "ReadWrite";
    var writable = AssertThrows<SettingsValidationException>(() => SettingsStore.Validate(new AppSettings { SignalMappings = writableTrigger }));
    AssertTrue(writable.Errors.Any(error => error.Contains("Part Data Ready", StringComparison.Ordinal) && error.Contains("read-only", StringComparison.Ordinal)), string.Join(" ", writable.Errors));

    var invalidTriggerMap = PlcSignalMapping.CreateDefaults();
    invalidTriggerMap.First(m => m.SignalName == PlcSignalMapping.PartDataReadySignalName).ValueMap = "0=OFF;1=ON";
    var invalidTrigger = AssertThrows<SettingsValidationException>(() => SettingsStore.Validate(new AppSettings { SignalMappings = invalidTriggerMap }));
    AssertTrue(invalidTrigger.Errors.Any(error => error.Contains("HIGH and LOW", StringComparison.Ordinal)), string.Join(" ", invalidTrigger.Errors));
});

await RunAsync("live leak value writer atomically replaces only changed invariant values", async () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"dynak-live-value-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "live_leak_value.txt");
    var writer = new LiveLeakValueFileWriter();

    AssertTrue(await writer.WriteIfChangedAsync(path, 0.327m, CancellationToken.None), "first value should update the file");
    AssertEqual("0.327", await File.ReadAllTextAsync(path));
    AssertTrue(!File.Exists(Path.ChangeExtension(path, ".tmp")), "atomic temporary file should be removed");
    AssertTrue(!await writer.WriteIfChangedAsync(path, 0.327m, CancellationToken.None), "unchanged value should not rewrite the file");
    AssertTrue(await writer.WriteIfChangedAsync(path, 0.111m, CancellationToken.None), "changed value should replace the file");
    AssertEqual("0.111", await File.ReadAllTextAsync(path));
});

await RunAsync("empty PLC enum mapping survives persisted settings reload", async () =>
{
    var temp = Path.Combine(Path.GetTempPath(), $"dynak-test-{Guid.NewGuid():N}.db");
    var settings = Options.Create(new AppSettings { DatabasePath = temp });
    var store = new SettingsStore(settings, NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);
    var config = new ConfigRepository(database, temp + ".json");
    var mappings = PlcSignalMapping.CreateDefaults();
    mappings.First(m => m.SignalName == "Error").ValueMap = "";
    store.Apply(new SettingsUpdate(null, null, null, null, null, mappings, null));
    await config.SaveAsync(store.Current, CancellationToken.None);

    var reloadedStore = new SettingsStore(Options.Create(await config.LoadMachineAsync(CancellationToken.None)), NullLogger<SettingsStore>.Instance);

    AssertEqual("", reloadedStore.Current.SignalMappings.First(m => m.SignalName == "Error").ValueMap);
});

await RunAsync("machine event active faults are not duplicated and can clear", async () =>
{
    var (_, events) = await CreateRepositoriesAsync();
    var now = DateTimeOffset.Now;
    var first = await events.StartActiveAsync("TEST-STATION", "PLC_DISCONNECTED", "ERROR", null, "PLC disconnected", now, CancellationToken.None);
    var second = await events.StartActiveAsync("TEST-STATION", "PLC_DISCONNECTED", "ERROR", null, "PLC disconnected", now.AddSeconds(1), CancellationToken.None);
    var cleared = await events.ClearActiveAsync("TEST-STATION", "PLC_DISCONNECTED", null, now.AddSeconds(2), CancellationToken.None);
    var recent = await events.RecentAsync(10, CancellationToken.None);

    AssertTrue(first, "first active event should be stored");
    AssertTrue(!second, "duplicate active event should be skipped");
    AssertTrue(cleared, "active event should clear");
    AssertEqual(1, recent.Count);
    AssertTrue(recent[0].ClearedTimestamp is not null, "event should have cleared timestamp");
});

await RunSync("production result serializes as string for UI", () =>
{
    var options = new JsonSerializerOptions();
    options.Converters.Add(new JsonStringEnumConverter());
    var json = JsonSerializer.Serialize(new { Result = ProductionResult.REWORK }, options);
    AssertTrue(json.Contains("\"REWORK\""), json);
});

await RunSync("service state exposes explicit PLC status", () =>
{
    var acquisition = new AcquisitionState();
    var settings = new AppSettings();
    var window = new ShiftResolver(settings.Shifts).ResolveWindow(DateTimeOffset.Now);

    acquisition.MarkStatus(PlcServiceStatus.ConfigurationError, false, "PLC register mapping is incomplete.");
    var snapshot = acquisition.Snapshot([], settings, window, DateTimeOffset.Now);

    AssertEqual(PlcServiceStatus.ConfigurationError, snapshot.ServiceStatus);
    AssertEqual("CONFIGURATION ERROR", snapshot.ServiceStatusText);
    AssertEqual("PLC register mapping is incomplete.", snapshot.LastError);

    acquisition.MarkStatus(PlcServiceStatus.Error, false, "Unexpected Modbus register response.");
    snapshot = acquisition.Snapshot([], settings, window, DateTimeOffset.Now);
    AssertEqual("CONNECTED BUT READ FAILED", snapshot.ServiceStatusText);
});

await RunAsync("station runtime requires explicit start and completes stop", async () =>
{
    using var runtime = new StationRuntimeControl();
    AssertTrue(!runtime.IsStartRequested, "runtime must be stopped by default");
    AssertTrue(runtime.RequestStart(), "first start should be accepted");
    var session = await runtime.WaitForStartAsync(CancellationToken.None);
    AssertTrue(runtime.IsStartRequested, "runtime should remain requested after browser-independent start");
    var stopped = runtime.RequestStopAsync(CancellationToken.None);
    runtime.CompleteStop(session);
    await stopped;
    AssertTrue(!runtime.IsStartRequested, "runtime should be stopped after worker confirmation");
});

await RunAsync("new unsaved Part Number stays pending while LOW and saves once when D1075 is HIGH", async () =>
{
    await using var harness = await PartDataHarness.CreateAsync();
    var settings = harness.Settings.Current;
    harness.Plc.Enqueue(PartSnapshot(8101, "READY-100", "QR-READY-100", 0.250m, "PLC-SN-001", 0.500m));

    await harness.Trigger.ProcessPollAsync(settings, PartDataReadySignals(false, "READY-100"), CancellationToken.None);
    AssertEqual(0, harness.Plc.SnapshotReadCount);
    AssertEqual(0, await harness.Records.CountAsync(CancellationToken.None));

    await harness.Trigger.ProcessPollAsync(settings, PartDataReadySignals(true, "READY-100"), CancellationToken.None);
    AssertEqual(1, harness.Plc.SnapshotReadCount);
    AssertEqual(1, await harness.Records.CountAsync(CancellationToken.None));

    for (var poll = 0; poll < 20; poll++)
    {
        await harness.Trigger.ProcessPollAsync(settings, PartDataReadySignals(true, "READY-100"), CancellationToken.None);
    }
    AssertEqual(1, harness.Plc.SnapshotReadCount);
    AssertEqual(1, await harness.Records.CountAsync(CancellationToken.None));

    var changedSettings = settings.Clone();
    changedSettings.UpperLimit = 0.750m;
    harness.Settings.Activate(changedSettings);
    harness.Plc.Enqueue(PartSnapshot(8102, "READY-101", "QR-READY-101", 0.300m, "PLC-SN-002", 0.750m));
    await harness.Trigger.ProcessPollAsync(changedSettings, PartDataReadySignals(true, "READY-101"), CancellationToken.None);

    AssertEqual(2, harness.Plc.SnapshotReadCount);
    AssertEqual(2, await harness.Records.CountAsync(CancellationToken.None));
    await harness.Trigger.ProcessPollAsync(changedSettings, PartDataReadySignals(true, "READY-101"), CancellationToken.None);
    await harness.Trigger.ProcessPollAsync(changedSettings, PartDataReadySignals(true, "READY-100"), CancellationToken.None);
    AssertEqual(2, harness.Plc.SnapshotReadCount);
    AssertEqual(2, await harness.Records.CountAsync(CancellationToken.None));

    harness.Trigger.ResetSession();
    await harness.Trigger.ProcessPollAsync(changedSettings, PartDataReadySignals(true, "READY-100"), CancellationToken.None);
    AssertEqual(2, harness.Plc.SnapshotReadCount);
    AssertEqual(2, await harness.Records.CountAsync(CancellationToken.None));

    var first = (await harness.Records.QueryAsync(new ProductionRecordQuery(null, null, null, "READY-100", null, null), CancellationToken.None)).Single();
    var second = (await harness.Records.QueryAsync(new ProductionRecordQuery(null, null, null, "READY-101", null, null), CancellationToken.None)).Single();
    AssertEqual("PLC-SN-001", first.SerialNumber);
    AssertEqual(0.250m, first.LeakTestValue);
    AssertEqual(0.500m, first.UpperLimit);
    AssertEqual(0.750m, second.UpperLimit);
    AssertTrue(first.PlcSnapshotJson.Contains("customProductionValue", StringComparison.OrdinalIgnoreCase), first.PlcSnapshotJson);

    var workbook = PartHistoryExcelExporter.CreateDailyWorkbook([first, second], first.Date);
    var cells = SheetCells(ReadWorkbookXml(workbook, "xl/worksheets/sheet1.xml"));
    // <!-- Changed manually using GPT -->
    AssertTrue(cells["F4"].Attribute("t") is null && cells["F5"].Attribute("t") is null, "daily Leak Test Values should remain numeric");
    AssertEqual(cells["F4"].Attribute("s")?.Value, cells["F5"].Attribute("s")?.Value);
    AssertEqual("0.00 TO 0.500", CellText(cells["G4"]));
    AssertEqual("0.00 TO 0.750", CellText(cells["G5"]));
    // AssertTrue(cells["G4"].Attribute("t") is null && cells["G5"].Attribute("t") is null, "daily Leak Test Values should remain numeric");
    // AssertEqual(cells["G4"].Attribute("s")?.Value, cells["G5"].Attribute("s")?.Value);
    // AssertEqual("0.00 TO 0.500", CellText(cells["H4"]));
    // AssertEqual("0.00 TO 0.750", CellText(cells["H5"]));
});

await RunAsync("snapshot failure retries on the next poll while D1075 remains HIGH", async () =>
{
    await using var harness = await PartDataHarness.CreateAsync();
    var settings = harness.Settings.Current;
    harness.Plc.Enqueue(null);

    await harness.Trigger.ProcessPollAsync(settings, PartDataReadySignals(true, "READY-AFTER-FAILURE"), CancellationToken.None);
    AssertEqual(1, harness.Plc.SnapshotReadCount);
    AssertEqual(0, await harness.Records.CountAsync(CancellationToken.None));

    harness.Plc.Enqueue(PartSnapshot(8103, "READY-AFTER-FAILURE", "QR-AFTER-FAILURE", 0.125m));
    await harness.Trigger.ProcessPollAsync(settings, PartDataReadySignals(true, "READY-AFTER-FAILURE"), CancellationToken.None);

    AssertEqual(2, harness.Plc.SnapshotReadCount);
    AssertEqual(1, await harness.Records.CountAsync(CancellationToken.None));
});

await RunAsync("D1075 HIGH ignores empty or zero Part Numbers without reading a production snapshot", async () =>
{
    await using var harness = await PartDataHarness.CreateAsync();
    var settings = harness.Settings.Current;

    await harness.Trigger.ProcessPollAsync(settings, PartDataReadySignals(true, ""), CancellationToken.None);
    await harness.Trigger.ProcessPollAsync(settings, PartDataReadySignals(true, "0000"), CancellationToken.None);

    AssertEqual(0, harness.Plc.SnapshotReadCount);
    AssertEqual(0, await harness.Records.CountAsync(CancellationToken.None));
});

await RunAsync("D1075 HIGH saves a valid Part Number when optional PLC values are unavailable", async () =>
{
    await using var harness = await PartDataHarness.CreateAsync();
    var settings = harness.Settings.Current;
    harness.Plc.Enqueue(PartSnapshot(8104, "000022", "PAR411", null));

    await harness.Trigger.ProcessPollAsync(settings, PartDataReadySignals(true, "000022"), CancellationToken.None);

    var saved = (await harness.Records.QueryAsync(
        new ProductionRecordQuery(null, null, null, "000022", null, null),
        CancellationToken.None)).Single();
    AssertEqual<decimal?>(null, saved.LeakTestValue);
    AssertTrue(!File.Exists(settings.LiveLeakValueFilePath), "a missing PLC leak value must not create a fake live leak-value file");
});

await RunAsync("daily reports recover and roll over month year and canonical files", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"dynak-daily-report-{Guid.NewGuid():N}");
    var reportRoot = Path.Combine(root, "DynaK Leak Test Report");
    var store = new SettingsStore(Options.Create(new AppSettings
    {
        DatabasePath = Path.Combine(root, "station.db"),
        LiveLeakValueFilePath = Path.Combine(root, "live_leak_value.txt"),
        ReportRootFolder = reportRoot
    }), NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);
    var records = new ProductionRepository(database);
    var dates = new[]
    {
        new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 24, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 31, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 12, 31, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2027, 1, 1, 8, 0, 0, TimeSpan.Zero)
    };
    for (var index = 0; index < dates.Length; index++)
    {
        await records.InsertAsync(SampleRecord(7000 + index, dates[index]) with
        {
            SerialNumber = $"DAILY-SN-{index + 1:000}",
            QrCode = $"DAILY-QR-{index + 1:000}",
            PartNumber = $"DAILY-PART-{index + 1:000}"
        }, CancellationToken.None);
    }

    var writer = new DailyReportWriter(records, store, NullLogger<DailyReportWriter>.Instance);
    await writer.RecoverAsync(CancellationToken.None);

    var august23 = DailyReportWriter.GetReportPath(reportRoot, new DateOnly(2026, 8, 23));
    var august24 = DailyReportWriter.GetReportPath(reportRoot, new DateOnly(2026, 8, 24));
    var august31 = DailyReportWriter.GetReportPath(reportRoot, new DateOnly(2026, 8, 31));
    var september1 = DailyReportWriter.GetReportPath(reportRoot, new DateOnly(2026, 9, 1));
    var december31 = DailyReportWriter.GetReportPath(reportRoot, new DateOnly(2026, 12, 31));
    var january1 = DailyReportWriter.GetReportPath(reportRoot, new DateOnly(2027, 1, 1));
    foreach (var path in new[] { august23, august24, august31, september1, december31, january1 })
    {
        AssertTrue(File.Exists(path), $"daily report was not recovered: {path}");
    }
    AssertTrue(august31.Contains(Path.Combine("2026", "08 - August"), StringComparison.Ordinal), august31);
    AssertTrue(september1.Contains(Path.Combine("2026", "09 - September"), StringComparison.Ordinal), september1);
    AssertTrue(january1.Contains(Path.Combine("2027", "01 - January"), StringComparison.Ordinal), january1);
    AssertEqual(12, Directory.GetDirectories(Path.Combine(reportRoot, "2027")).Length);

    File.Delete(august23);
    await new DailyReportWriter(records, store, NullLogger<DailyReportWriter>.Instance).RecoverAsync(CancellationToken.None);
    AssertTrue(File.Exists(august23), "startup recovery should recreate a missing daily report");

    await File.WriteAllBytesAsync(august24, [1, 2, 3]);
    await writer.RecoverAsync(CancellationToken.None);
    PartHistoryExcelExporter.ValidateWorkbook(await File.ReadAllBytesAsync(august24), 1);

    await records.InsertAsync(SampleRecord(7100, dates[0].AddHours(1)) with
    {
        SerialNumber = "DAILY-SN-SECOND",
        QrCode = "DAILY-QR-SECOND",
        PartNumber = "DAILY-PART-SECOND",
        UpperLimit = 0.750m
    }, CancellationToken.None);
    await writer.ExportDateAsync(new DateOnly(2026, 8, 23), createWhenEmpty: false, CancellationToken.None);
    await writer.ExportDateAsync(new DateOnly(2026, 8, 23), createWhenEmpty: false, CancellationToken.None);
    AssertEqual(1, Directory.GetFiles(Path.GetDirectoryName(august23)!, "2026-08-23*.xlsx").Length);
    AssertEqual(0, Directory.GetFiles(reportRoot, "*.tmp", SearchOption.AllDirectories).Length);
    PartHistoryExcelExporter.ValidateWorkbook(await File.ReadAllBytesAsync(august23), 2);
});

await RunAsync("Data Saved pulses HIGH for about two seconds and returns LOW only after a durable save", async () =>
{
    await using var harness = await PartDataHarness.CreateAsync(enableDataSaved: true);
    var settings = harness.Settings.Current;

    harness.Plc.Enqueue(PartSnapshot(8200, "ACK-PULSE", "QR-PULSE", 0.100m));
    var processing = harness.Trigger.ProcessPollAsync(
        settings,
        PartDataReadySignals(true, "ACK-PULSE"),
        CancellationToken.None);

    await WaitUntilAsync(
        () => harness.Plc.SuccessfulWrites.Any(write =>
            write.SignalName == PlcSafety.DataSavedSignalName &&
            write.Value is true),
        TimeSpan.FromSeconds(2),
        "DATA SAVED did not go HIGH after the durable save");

    AssertEqual(1, await harness.Records.CountAsync(CancellationToken.None));

    var high = harness.Plc.SuccessfulWrites.Single(write =>
        write.SignalName == PlcSafety.DataSavedSignalName &&
        write.Value is true);

    await Task.Delay(1100);

    AssertTrue(
        !harness.Plc.SuccessfulWrites.Any(write =>
            write.SignalName == PlcSafety.DataSavedSignalName &&
            write.Value is false &&
            write.At >= high.At),
        "DATA SAVED returned LOW before the required two-second pulse completed");

    await processing;

    var low = harness.Plc.SuccessfulWrites.Single(write =>
        write.SignalName == PlcSafety.DataSavedSignalName &&
        write.Value is false &&
        write.At >= high.At);

    AssertTrue(
        low.At - high.At >= TimeSpan.FromMilliseconds(1800),
        $"DATA SAVED pulse was too short: {low.At - high.At}");
});

await RunAsync("failed Data Saved acknowledgement stays pending and retries without another History insert", async () =>
{
    await using var harness = await PartDataHarness.CreateAsync(
        enableDataSaved: true,
        failDataSavedHighWritesRemaining: 1);
    var settings = harness.Settings.Current;

    harness.Plc.Enqueue(PartSnapshot(8201, "ACK100", "QR100", 0.100m));
    await harness.Trigger.ProcessPollAsync(
        settings,
        PartDataReadySignals(true, "ACK100"),
        CancellationToken.None);

    AssertEqual(1, await harness.Records.CountAsync(CancellationToken.None));
    AssertEqual(1, harness.Plc.SnapshotReadCount);

    harness.Plc.Enqueue(PartSnapshot(8202, "ACK101", "QR101", 0.101m));
    await harness.Trigger.ProcessPollAsync(
        settings,
        PartDataReadySignals(true, "ACK101"),
        CancellationToken.None);

    AssertEqual(1, await harness.Records.CountAsync(CancellationToken.None));
    AssertEqual(1, harness.Plc.SnapshotReadCount);

    await Task.Delay(TimeSpan.FromSeconds(5.1));
    await harness.Trigger.ProcessPollAsync(
        settings,
        PartDataReadySignals(true, "ACK100"),
        CancellationToken.None);

    AssertEqual(1, await harness.Records.CountAsync(CancellationToken.None));
    AssertEqual(1, harness.Plc.SnapshotReadCount);

    await harness.Trigger.ProcessPollAsync(
        settings,
        PartDataReadySignals(true, "ACK101"),
        CancellationToken.None);

    AssertEqual(2, await harness.Records.CountAsync(CancellationToken.None));
    AssertEqual(2, harness.Plc.SnapshotReadCount);
});

await RunAsync("failed Data Saved LOW write retries the acknowledgement without duplicating the durable row", async () =>
{
    await using var harness = await PartDataHarness.CreateAsync(
        enableDataSaved: true,
        failDataSavedLowWritesRemaining: 1);
    var settings = harness.Settings.Current;

    harness.Plc.Enqueue(PartSnapshot(8203, "ACK-LOW", "QR-LOW", 0.102m));
    await harness.Trigger.ProcessPollAsync(
        settings,
        PartDataReadySignals(true, "ACK-LOW"),
        CancellationToken.None);

    AssertEqual(1, await harness.Records.CountAsync(CancellationToken.None));
    AssertEqual(1, harness.Plc.SnapshotReadCount);

    await Task.Delay(TimeSpan.FromSeconds(5.1));
    await harness.Trigger.ProcessPollAsync(
        settings,
        PartDataReadySignals(true, "ACK-LOW"),
        CancellationToken.None);

    AssertEqual(1, await harness.Records.CountAsync(CancellationToken.None));
    AssertEqual(1, harness.Plc.SnapshotReadCount);
    AssertEqual(
        2,
        harness.Plc.SuccessfulWrites.Count(write =>
            write.SignalName == PlcSafety.DataSavedSignalName &&
            write.Value is true));
});

await RunAsync("session reset with D1075 still HIGH re-acknowledges an already durable part without another insert", async () =>
{
    await using var harness = await PartDataHarness.CreateAsync(
        enableDataSaved: true,
        failDataSavedHighWritesRemaining: 1);
    var settings = harness.Settings.Current;

    harness.Plc.Enqueue(PartSnapshot(8204, "ACK-RESTART", "QR-RESTART", 0.103m));
    await harness.Trigger.ProcessPollAsync(
        settings,
        PartDataReadySignals(true, "ACK-RESTART"),
        CancellationToken.None);

    AssertEqual(1, await harness.Records.CountAsync(CancellationToken.None));
    AssertEqual(1, harness.Plc.SnapshotReadCount);

    harness.Trigger.ResetSession();

    await harness.Trigger.ProcessPollAsync(
        settings,
        PartDataReadySignals(true, "ACK-RESTART"),
        CancellationToken.None);

    AssertEqual(1, await harness.Records.CountAsync(CancellationToken.None));
    AssertEqual(1, harness.Plc.SnapshotReadCount);
    AssertTrue(
        harness.Plc.SuccessfulWrites.Any(write =>
            write.SignalName == PlcSafety.DataSavedSignalName &&
            write.Value is true),
        "already durable part was not re-acknowledged after session reset");
});

await RunAsync("production acquisition saves new Part Numbers once while D1075 remains HIGH and retries failed inserts", async () =>
{
    await using var server = await ModbusTestServer.StartAsync(ProductionRegisters(actual: 1, leak: "0.327", qrCode: "QR001", partNumber: "PART001", partDataReady: 1));
    var testRoot = Path.Combine(Path.GetTempPath(), $"dynak-part-data-ready-{Guid.NewGuid():N}");
    var temp = Path.Combine(testRoot, "station.db");
    var liveLeakValuePath = Path.Combine(testRoot, "live_leak_value.txt");
    var mappings = CompleteMappings();
    ConfigureHandshake(mappings, "System Ready", "D2120", "0=ON;1=OFF");
    ConfigureHandshake(mappings, "Communication OK", "D2121", "0=ON;1=OFF");
    ConfigureHandshake(mappings, "Data Saved", "D2122", "0=ON;1=OFF");
    var configured = new AppSettings
    {
        DatabasePath = temp,
        LiveLeakValueFilePath = liveLeakValuePath,
        Plc = new PlcConnectionSettings
        {
            IpAddress = "127.0.0.1",
            Port = server.Port,
            UnitId = 1,
            PollingIntervalMs = 100,
            ReconnectIntervalMs = 100,
            ConnectionTimeoutMs = 1000
        },
        SignalMappings = mappings
    };
    var store = new SettingsStore(Options.Create(configured), NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);
    var records = new ProductionRepository(database);
    var events = new EventRepository(database);
    await using var plc = new MitsubishiModbusPlcClient();
    var acquisition = new AcquisitionState();
    using var runtime = new StationRuntimeControl();
    using var handshake = new PlcHandshakeService(plc, store, NullLogger<PlcHandshakeService>.Instance);
    var liveLeakValueFile = new LiveLeakValueFileWriter();
    var partDataReady = new PartDataReadyService(records, events, acquisition, handshake, liveLeakValueFile, plc, NullLogger<PartDataReadyService>.Instance);
    var worker = new AcquisitionWorker(plc, events, store, acquisition, runtime, handshake, partDataReady, NullLogger<AcquisitionWorker>.Instance);
    await worker.StartAsync(CancellationToken.None);

    await Task.Delay(150);
    AssertEqual(StationRuntimeStatus.Stopped, StateSnapshot(acquisition, store.Current).RuntimeStatus);
    AssertTrue(!server.Registers.ContainsKey(2120), "PLC must not be contacted before operator START");

    AssertTrue(runtime.RequestStart(), "operator START should be accepted");
    await WaitUntilAsync(() => StateSnapshot(acquisition, store.Current).RuntimeStatus == StationRuntimeStatus.Running, TimeSpan.FromSeconds(5), "station did not enter Running");
    await WaitUntilAsync(() => StateSnapshot(acquisition, store.Current).PlcDiagnostic.LastSuccessfulRead is not null, TimeSpan.FromSeconds(5), "PLC read did not succeed");
    var liveState = StateSnapshot(acquisition, store.Current);
    AssertEqual<int?>(450, liveState.TargetPartsPerShift);
    AssertEqual<int?>(1, liveState.ActualPartCount);
    AssertEqual("1", liveState.LiveResultRaw);
    AssertEqual("OK", liveState.LiveResult);
    AssertEqual(1, Convert.ToInt32(liveState.LivePlcSignals[PlcSignalMapping.PartDataReadySignalName].RawValue, CultureInfo.InvariantCulture));
    await WaitUntilAsync(
        () => server.Registers.TryGetValue(2120, out var systemReady) && systemReady == 0 &&
            server.Registers.TryGetValue(2121, out var communicationOk) && communicationOk == 0,
        TimeSpan.FromSeconds(5),
        "configured System Ready and Communication OK signals did not become active");
    AssertEqual((ushort)0, server.Registers[2120]);
    AssertEqual((ushort)0, server.Registers[2121]);

    await WaitUntilAsync(() => records.CountAsync(CancellationToken.None).GetAwaiter().GetResult() == 1, TimeSpan.FromSeconds(5), "PART001 was not saved immediately from the first D1075=1 poll");
    await WaitUntilAsync(
        () => server.ObservedWriteValues.Any(write => write.Start == 2122 && write.Value == 0),
        TimeSpan.FromSeconds(5),
        "configured DATA SAVED did not pulse ON after the complete database and text-file writes");
    AssertEqual(1, await records.CountLogicalPartsAsync(CancellationToken.None));
    AssertEqual("0.327", await File.ReadAllTextAsync(liveLeakValuePath));
    var part1 = (await records.QueryAsync(new ProductionRecordQuery(null, null, null, "PART001", null, null), CancellationToken.None)).Single();
    AssertEqual("PART001", part1.PartNumber);
    AssertEqual("QR001", part1.QrCode);
    AssertEqual(0.327m, part1.LeakTestValue);
    AssertTrue(part1.PlcSnapshotJson.Contains("Part Data Ready", StringComparison.Ordinal), part1.PlcSnapshotJson);
    AssertTrue(server.ObservedReadRanges.Any(read => read.Start == 1075 && read.Count == 1), "D1075 did not resolve to zero-based Modbus holding-register address 1075 with offset 0");
    AssertTrue(!server.ObservedWriteValues.Any(write => write.Start == 1075), "the PC must never write HIGH or LOW to D1075");

    server.Registers[2005] = 7;
    AddAsciiRegisters(server.Registers, 2050, "QR-CHANGED", 10);
    await Task.Delay(400);
    AssertEqual(1, await records.CountAsync(CancellationToken.None));
    AssertEqual("PART001", (await records.GetByIdAsync(part1.Id, CancellationToken.None))!.PartNumber);

    await WaitUntilAsync(() => server.Registers.TryGetValue(2122, out var dataSaved) && dataSaved == 1, TimeSpan.FromSeconds(5), "configured DATA SAVED did not return OFF");
    AssertEqual(1, server.ObservedWriteValues.Count(write => write.Start == 2122 && write.Value == 0));

    server.Registers[2005] = 2;
    server.Registers[1010] = 1;
    server.Registers[1011] = 0;
    AddAsciiRegisters(server.Registers, 2025, "0.111", 5);
    AddAsciiRegisters(server.Registers, 2050, "QR002", 10);
    AddAsciiRegisters(server.Registers, 2060, "PART002", 10);
    await WaitUntilAsync(() => records.CountAsync(CancellationToken.None).GetAwaiter().GetResult() == 2, TimeSpan.FromSeconds(5), "PART002 was not saved after Part Number changed while D1075 stayed HIGH");
    await WaitUntilAsync(() => server.ObservedWriteValues.Count(write => write.Start == 2122 && write.Value == 0) == 2, TimeSpan.FromSeconds(5), "PART002 DATA SAVED pulse was not emitted");
    AssertEqual("0.111", await File.ReadAllTextAsync(liveLeakValuePath));
    var savedParts = await records.QueryAsync(new ProductionRecordQuery(null, null, null, null, null, null), CancellationToken.None);
    AssertEqual(2, savedParts.Count);
    AssertEqual(0.327m, savedParts.Single(record => record.PartNumber == "PART001").LeakTestValue);
    AssertEqual("QR001", savedParts.Single(record => record.PartNumber == "PART001").QrCode);
    AssertEqual(0.111m, savedParts.Single(record => record.PartNumber == "PART002").LeakTestValue);
    AssertEqual("QR002", savedParts.Single(record => record.PartNumber == "PART002").QrCode);
    AssertTrue(!server.ObservedWriteValues.Any(write => write.Start == 1075), "D1075 must remain read-only across all pulses");

    var dataSavedPulsesBeforeReconnect = server.ObservedWriteValues.Count(write => write.Start == 2122 && write.Value == 0);
    server.DropConnection();
    await WaitUntilAsync(() => StateSnapshot(acquisition, store.Current).RuntimeStatus == StationRuntimeStatus.ConnectionError, TimeSpan.FromSeconds(5), "station did not mark connection error");
    await WaitUntilAsync(() => StateSnapshot(acquisition, store.Current).RuntimeStatus == StationRuntimeStatus.Running, TimeSpan.FromSeconds(5), "station did not reconnect");
    await WaitUntilAsync(
        () => server.ObservedWriteValues.Count(write => write.Start == 2122 && write.Value == 0) > dataSavedPulsesBeforeReconnect,
        TimeSpan.FromSeconds(10),
        "pending PART002 DATA SAVED acknowledgement was not retried after reconnect");
    await WaitUntilAsync(
        () => server.Registers.TryGetValue(2122, out var dataSaved) && dataSaved == 1,
        TimeSpan.FromSeconds(5),
        "retried PART002 DATA SAVED acknowledgement did not return OFF");
    AssertEqual(2, await records.CountAsync(CancellationToken.None));

    await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
    {
        await SqliteDatabase.ExecuteAsync(connection, "CREATE TRIGGER reject_production_insert BEFORE INSERT ON production_records BEGIN SELECT RAISE(ABORT, 'forced persistence failure'); END;", CancellationToken.None);
    }
    var pulsesBeforeFailure = server.ObservedWriteValues.Count(write => write.Start == 2122 && write.Value == 0);
    server.Registers[2005] = 3;
    AddAsciiRegisters(server.Registers, 2025, "0.222", 5);
    AddAsciiRegisters(server.Registers, 2050, "QR003", 10);
    AddAsciiRegisters(server.Registers, 2060, "PART003", 10);
    await WaitUntilAsync(() => partDataReady.State == PartDataReadyState.SAVING, TimeSpan.FromSeconds(3), "PART003 insert failure did not enter retry state");
    await Task.Delay(600);
    AssertEqual(2, await records.CountAsync(CancellationToken.None));
    AssertEqual(pulsesBeforeFailure, server.ObservedWriteValues.Count(write => write.Start == 2122 && write.Value == 0));
    AssertEqual("0.111", await File.ReadAllTextAsync(liveLeakValuePath));

    server.Registers[1075] = 0;

    await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
    {
        await SqliteDatabase.ExecuteAsync(connection, "DROP TRIGGER reject_production_insert;", CancellationToken.None);
    }
    await WaitUntilAsync(() => records.CountAsync(CancellationToken.None).GetAwaiter().GetResult() == 3, TimeSpan.FromSeconds(5), "PART003 database insert was not retried after D1075 returned LOW");
    AssertEqual("PART003", (await records.QueryAsync(new ProductionRecordQuery(null, null, null, "PART003", null, null), CancellationToken.None)).Single().PartNumber);

    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await runtime.RequestStopAsync(stopTimeout.Token);
    AssertEqual(StationRuntimeStatus.Stopped, StateSnapshot(acquisition, store.Current).RuntimeStatus);
    AssertEqual((ushort)1, server.Registers[2120]);
    AssertEqual((ushort)1, server.Registers[2121]);

    await worker.StopAsync(CancellationToken.None);
});

await RunAsync("runtime PLC client does not generate fake snapshots while disconnected", async () =>
{
    await using var client = new MitsubishiModbusPlcClient();
    await AssertThrowsAsync<InvalidOperationException>(() => client.ReadPartDataSnapshotAsync(CancellationToken.None));
});

await RunAsync("PLC client connect honors cancellation", async () =>
{
    await using var client = new MitsubishiModbusPlcClient();
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await AssertThrowsAsync<OperationCanceledException>(() => client.ConnectAsync(
        PlcClientConfiguration.From(new AppSettings
        {
            Plc = new PlcConnectionSettings { IpAddress = "192.168.1.10", Port = 502 },
            SignalMappings = CompleteMappings()
        }),
        cts.Token));
});

await RunAsync("Modbus client reads D registers and only writes handshake allow-list", async () =>
{
    var registers = ProductionRegisters();
    AddAsciiRegisters(registers, 2070, "PLC-SERIAL-009", 10);
    await using var server = await ModbusTestServer.StartAsync(registers);
    await using var client = new MitsubishiModbusPlcClient();
    var mappings = CompleteMappings();
    var serialMapping = PlcSignalMapping.CreateDefaults().Single(mapping => mapping.SignalName == "Serial Number").Clone();
    serialMapping.Enabled = true;
    serialMapping.Address = "D2070";
    serialMapping.AddressType = "D Register";
    serialMapping.DataType = "AsciiString";
    serialMapping.Length = 10;
    serialMapping.Encoding = "ASCII";
    mappings.Add(serialMapping);
    ConfigureHandshake(mappings, "System Ready", "D2130", "0=ON;1=OFF");
    ConfigureHandshake(mappings, "Communication OK", "D2131", "0=ON;1=OFF");
    ConfigureHandshake(mappings, "Data Saved", "D2132", "0=ON;1=OFF");

    await client.ConnectAsync(PlcClientConfiguration.From(new AppSettings
    {
        Plc = new PlcConnectionSettings { IpAddress = "127.0.0.1", Port = server.Port, UnitId = 1 },
        SignalMappings = mappings
    }), CancellationToken.None);

    var snapshot = await client.ReadPartDataSnapshotAsync(CancellationToken.None) ??
        throw new InvalidOperationException("expected a production snapshot");
    await client.WriteConfiguredSignalAsync("System Ready", true, CancellationToken.None);
    await client.WriteConfiguredSignalAsync("Communication OK", true, CancellationToken.None);
    await client.WriteConfiguredSignalAsync(PlcSafety.DataSavedSignalName, true, CancellationToken.None);

    AssertEqual("QR-123", snapshot.QrCode);
    AssertEqual("PN-999", snapshot.PartNumber);
    AssertEqual("PLC-SERIAL-009", snapshot.SerialNumber);
    AssertEqual(450, snapshot.TargetPartsPerShift);
    AssertEqual(12, snapshot.ActualPartCount);
    AssertEqual("1", snapshot.RawResultValue);
    AssertEqual("OK", snapshot.Result);
    AssertEqual("1", snapshot.RawModeValue);
    AssertEqual("Auto", snapshot.ResolvedMode);
    AssertEqual("0", snapshot.ErrorCode);
    AssertEqual("No Error", snapshot.ErrorDescription);
    AssertEqual("1", snapshot.RawRunningStatusValue);
    AssertEqual("Running", snapshot.ResolvedRunningStatus);
    AssertTrue(server.ObservedReadRanges.Any(read => read.Start == 1020 && read.Count == 3), "configured Time range D1020-D1022 was not read");
    AssertTrue(snapshot.IsAutoMode, "auto/manual value map should decode AUTO");
    AssertTrue(snapshot.IsMachineRunning, "running value map should decode RUNNING");
    AssertEqual((ushort)0, server.Registers[2130]);
    AssertEqual((ushort)0, server.Registers[2131]);
    AssertEqual((ushort)0, server.Registers[2132]);
    await AssertThrowsAsync<PlcWriteBlockedException>(() => client.WriteConfiguredSignalAsync("Target Parts Per Shift", 1, CancellationToken.None));
    await AssertThrowsAsync<PlcWriteBlockedException>(() => client.WriteConfiguredSignalAsync(PlcSignalMapping.PartDataReadySignalName, true, CancellationToken.None));
    AssertTrue(!server.ObservedWriteValues.Any(write => write.Start == 1075), "D1075 must never receive a PLC write frame");

    AddAsciiRegisters(server.Registers, 2050, "0", 10);
    AddAsciiRegisters(server.Registers, 2060, "0", 10);
    var zeroValueSnapshot = await client.ReadPartDataSnapshotAsync(CancellationToken.None) ??
        throw new InvalidOperationException("D1075 snapshots must not be rejected based on QR Code or Part Number values");
    AssertEqual("0", zeroValueSnapshot.QrCode);
    AssertEqual("0", zeroValueSnapshot.PartNumber);

    await client.DisconnectAsync(CancellationToken.None);
});

await RunAsync("one undecodable PLC mapping does not block the shared live snapshot", async () =>
{
    var registers = ProductionRegisters(target: 999, actual: 50, leak: "01234", mode: 1, error: 2);
    registers[2200] = 123;
    registers[2201] = 456;
    registers[2202] = 789;
    registers[2203] = 101;
    registers[2204] = 112;
    await using var server = await ModbusTestServer.StartAsync(registers);
    await using var client = new MitsubishiModbusPlcClient();
    var mappings = CompleteMappings();
    var target = mappings.First(mapping => mapping.SignalName == "Target Parts Per Shift");
    target.Address = "D2200";
    target.DataType = "Int16";
    target.Length = 5;
    var invalidTime = mappings.First(mapping => mapping.SignalName == "Time");
    invalidTime.DataType = "Int32";
    invalidTime.Length = 1;

    var settings = new AppSettings
    {
        Plc = new PlcConnectionSettings { IpAddress = "127.0.0.1", Port = server.Port, UnitId = 1 },
        SignalMappings = mappings
    };
    await client.ConnectAsync(PlcClientConfiguration.From(settings), CancellationToken.None);

    var signals = await client.ReadConfiguredSignalsAsync(CancellationToken.None);
    AssertEqual(13, signals.Count);
    AssertEqual(123m, signals["Target Parts Per Shift"].InterpretedValue);
    AssertEqual(50m, signals["Actual Part Count"].InterpretedValue);
    AssertEqual(1234m, signals["Leak Test Value"].InterpretedValue);
    AssertEqual("Auto", signals["Auto / Manual"].InterpretedValue);
    AssertEqual("Error 2", signals["Error"].InterpretedValue);
    AssertTrue(signals["Time"].Error?.Contains("requires at least 2 register(s)", StringComparison.Ordinal) == true, signals["Time"].Error ?? "missing per-signal decode error");
    AssertTrue(server.ObservedReadRanges.Any(read => read.Start == 2200 && read.Count == 5), "configured scalar range D2200-D2204 was not read");

    var acquisition = new AcquisitionState();
    var sharedSignals = acquisition.UpdatePlcRead(settings, signals, DateTimeOffset.Now);
    var state = StateSnapshot(acquisition, settings);
    AssertTrue(ReferenceEquals(sharedSignals, state.LivePlcSignals), "dashboard and acquisition should use the same decoded PLC snapshot");
    AssertEqual(1234m, state.LivePlcSignals["Leak Test Value"].InterpretedValue);
    AssertEqual("Auto", state.LivePlcSignals["Auto / Manual"].InterpretedValue);
    AssertEqual("Error 2", state.LivePlcSignals["Error"].InterpretedValue);
    AssertEqual("1234", state.PlcDiagnostic.Signals.First(signal => signal.SignalName == "Leak Test Value").InterpretedValue);
    AssertTrue(state.PlcDiagnostic.Signals.First(signal => signal.SignalName == "Time").Error is not null, "diagnostic should mark only Time as undecodable");
});

await RunAsync("production snapshot normalizes whitespace-padded PLC leak values", async () =>
{
    var registers = ProductionRegisters(partNumber: "000022", leak: "0 .004", partDataReady: 1);
    registers[1015] = 25;
    await using var server = await ModbusTestServer.StartAsync(registers);
    await using var client = new MitsubishiModbusPlcClient();
    var mappings = CompleteMappings();
    var date = mappings.First(mapping => mapping.SignalName == "Date");
    date.Address = "D1015";
    date.DataType = "Int16";
    date.Length = 1;

    await client.ConnectAsync(PlcClientConfiguration.From(new AppSettings
    {
        Plc = new PlcConnectionSettings { IpAddress = "127.0.0.1", Port = server.Port, UnitId = 1 },
        SignalMappings = mappings
    }), CancellationToken.None);

    var beforeCapture = DateTimeOffset.Now;
    var snapshot = await client.ReadPartDataSnapshotAsync(CancellationToken.None) ??
        throw new InvalidOperationException("valid Part Number 000022 should produce a snapshot");
    var afterCapture = DateTimeOffset.Now;

    AssertEqual("000022", snapshot.PartNumber);
    AssertEqual<decimal?>(0.004m, snapshot.LeakTestValue);
    AssertEqual("25", Convert.ToString(snapshot.Signals["Date"].RawValue, CultureInfo.InvariantCulture));
    AssertEqual("0 .004", Convert.ToString(snapshot.Signals["Leak Test Value"].RawValue, CultureInfo.InvariantCulture));
    AssertEqual(0.004m, snapshot.Signals["Leak Test Value"].InterpretedValue);
    AssertTrue(snapshot.Timestamp >= beforeCapture && snapshot.Timestamp <= afterCapture, "invalid PLC Date must fall back to capture time");
});

await RunAsync("Modbus failed and partial multi-register reads are rejected", async () =>
{
    await using var failedServer = await ModbusTestServer.StartAsync(ProductionRegisters(), failingReadStarts: [2015]);
    await using var failedClient = new MitsubishiModbusPlcClient();
    await failedClient.ConnectAsync(PlcClientConfiguration.From(new AppSettings
    {
        Plc = new PlcConnectionSettings { IpAddress = "127.0.0.1", Port = failedServer.Port, UnitId = 1 },
        SignalMappings = CompleteMappings()
    }), CancellationToken.None);
    await AssertThrowsAsync<IOException>(() => failedClient.ReadPartDataSnapshotAsync(CancellationToken.None));

    await using var partialServer = await ModbusTestServer.StartAsync(ProductionRegisters(), partialReadStarts: [2050]);
    await using var partialClient = new MitsubishiModbusPlcClient();
    await partialClient.ConnectAsync(PlcClientConfiguration.From(new AppSettings
    {
        Plc = new PlcConnectionSettings { IpAddress = "127.0.0.1", Port = partialServer.Port, UnitId = 1 },
        SignalMappings = CompleteMappings()
    }), CancellationToken.None);
    await AssertThrowsAsync<IOException>(() => partialClient.ReadPartDataSnapshotAsync(CancellationToken.None));
});

await RunAsync("unknown PLC enum values are stored as raw values with unknown meanings", async () =>
{
    await using var server = await ModbusTestServer.StartAsync(ProductionRegisters(
        actual: 13,
        okStatus: 9,
        ngStatus: 0,
        time: "101501",
        mode: 8,
        error: 7,
        running: 6,
        qrCode: "QR-UNK",
        partNumber: "PN-UNK"));
    await using var client = new MitsubishiModbusPlcClient();

    await client.ConnectAsync(PlcClientConfiguration.From(new AppSettings
    {
        Plc = new PlcConnectionSettings { IpAddress = "127.0.0.1", Port = server.Port, UnitId = 1 },
        SignalMappings = CompleteMappings()
    }), CancellationToken.None);

    var snapshot = await client.ReadPartDataSnapshotAsync(CancellationToken.None);

    AssertEqual("OK=9;NG=0", snapshot!.RawResultValue);
    AssertEqual("Unknown Result (OK=9;NG=0)", snapshot.Result);
    AssertEqual("8", snapshot.RawModeValue);
    AssertEqual("Unknown Mode (8)", snapshot.ResolvedMode);
    AssertEqual("7", snapshot.ErrorCode);
    AssertEqual("Unknown Error (7)", snapshot.ErrorDescription);
    AssertEqual("6", snapshot.RawRunningStatusValue);
    AssertEqual("Unknown Status (6)", snapshot.ResolvedRunningStatus);
    AssertTrue(!snapshot.IsAutoMode, "unknown mode should not be treated as auto");
    AssertTrue(!snapshot.IsMachineRunning, "unknown running status should not be treated as running");
});

await RunSync("bounded file logger rotates with retained file limit", () =>
{
    var dir = Path.Combine(Path.GetTempPath(), $"dynak-logs-{Guid.NewGuid():N}");
    var path = Path.Combine(dir, "service.log");
    using var provider = new BoundedFileLoggerProvider(new LocalLogSettings { Path = path, MaxFileBytes = 1024, RetainedFileCount = 2 }, AppContext.BaseDirectory);
    var logger = provider.CreateLogger("test");

    for (var index = 0; index < 1000; index++)
    {
        logger.LogInformation("rotation test record {Index} {Payload}", index, new string('x', 512));
    }

    var files = Directory.GetFiles(dir, "service.log*");
    AssertTrue(files.Length > 1, "expected log rotation to create retained files");
    AssertTrue(files.Length <= 3, $"expected active log plus two retained files, found {files.Length}");
});

if (Environment.ExitCode == 0)
{
    Console.WriteLine("All tests passed.");
}

static async Task<(ProductionRepository Records, EventRepository Events)> CreateRepositoriesAsync()
{
    var temp = Path.Combine(Path.GetTempPath(), $"dynak-test-{Guid.NewGuid():N}.db");
    var settings = Options.Create(new AppSettings { DatabasePath = temp });
    var store = new SettingsStore(settings, NullLogger<SettingsStore>.Instance);
    var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
    await database.InitializeAsync(CancellationToken.None);
    return (new ProductionRepository(database), new EventRepository(database));
}

static XDocument ReadWorkbookXml(byte[] workbook, string path)
{
    using var stream = new MemoryStream(workbook);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    var entry = archive.GetEntry(path) ?? throw new InvalidOperationException($"Workbook part {path} was not found.");
    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
    return XDocument.Load(reader);
}

static Dictionary<string, XElement> SheetCells(XDocument sheet)
{
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    return sheet.Descendants(ns + "c").ToDictionary(cell => cell.Attribute("r")!.Value, StringComparer.OrdinalIgnoreCase);
}

static string CellName(int column, int row)
{
    var letters = "";
    while (column > 0)
    {
        column--;
        letters = (char)('A' + column % 26) + letters;
        column /= 26;
    }

    return $"{letters}{row}";
}

static string CellText(XElement cell)
{
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    return string.Concat(cell.Descendants(ns + "t").Select(text => text.Value));
}

static string CellValue(XElement cell)
{
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    return cell.Element(ns + "v")?.Value ?? "";
}

// <!-- Changed manually using GPT -->
// static decimal DecimalAttribute(XElement element, string name)
// {
//     return decimal.Parse(element.Attribute(name)?.Value ?? "0", CultureInfo.InvariantCulture);
// }

static List<PlcSignalMapping> CompleteMappings()
{
    return PlcSignalMapping.RequiredSignals
        .Select(signal => PlcSignalMapping.CreateDefaults().First(mapping => mapping.SignalName == signal).Clone())
        .ToList();
}

static void ConfigureHandshake(List<PlcSignalMapping> mappings, string signalName, string address, string valueMap)
{
    var mapping = mappings.First(item => item.SignalName == signalName);
    mapping.Enabled = true;
    mapping.Address = address;
    mapping.AddressType = "D Register";
    mapping.DataType = "UInt16";
    mapping.Direction = "Write";
    mapping.Length = 1;
    mapping.ValueMap = valueMap;
}

static Dictionary<int, ushort> ProductionRegisters(
    ushort target = 450,
    ushort actual = 12,
    ushort okStatus = 1,
    ushort ngStatus = 0,
    string date = "20260814",
    string time = "101500",
    string leak = "0.018",
    ushort mode = 1,
    ushort error = 0,
    ushort running = 1,
    string qrCode = "QR-123",
    string partNumber = "PN-999",
    ushort partDataReady = 0)
{
    var registers = new Dictionary<int, ushort>
    {
        [2000] = target,
        [2005] = actual,
        [1010] = okStatus,
        [1011] = ngStatus,
        [1075] = partDataReady,
        [2030] = mode,
        [2035] = error,
        [2040] = running
    };

    AddAsciiRegisters(registers, 2015, date, 5);
    var parsedTime = TimeOnly.ParseExact(time, "HHmmss", CultureInfo.InvariantCulture);
    registers[1020] = (ushort)parsedTime.Hour;
    registers[1021] = (ushort)parsedTime.Minute;
    registers[1022] = (ushort)parsedTime.Second;
    AddAsciiRegisters(registers, 2025, leak, 5);
    AddAsciiRegisters(registers, 2050, qrCode, 10);
    AddAsciiRegisters(registers, 2060, partNumber, 10);
    return registers;
}

static object? DecodePlc(string dataType, ushort[] registers, int? length = null, string encoding = "ASCII")
{
    var mapping = new PlcSignalMapping
    {
        SignalName = "TEST",
        DataType = dataType,
        Length = length ?? registers.Length,
        ByteOrder = "ABCD",
        WordOrder = "HighLow",
        Encoding = encoding
    };
    return PlcValueDecoder.Decode(mapping, registers).Value;
}

static void AddAsciiRegisters(Dictionary<int, ushort> registers, int start, string value, int registerCount)
{
    var bytes = new byte[registerCount * 2];
    Encoding.ASCII.GetBytes(value.AsSpan(), bytes.AsSpan());
    for (var index = 0; index < registerCount; index++)
    {
        registers[start + index] = (ushort)((bytes[index * 2] << 8) | bytes[(index * 2) + 1]);
    }
}

static PlantState StateSnapshot(AcquisitionState acquisition, AppSettings settings)
{
    var now = DateTimeOffset.Now;
    return acquisition.Snapshot([], settings, new ShiftResolver(settings.Shifts).ResolveWindow(now), now);
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string? timeoutMessage = null)
{
    var deadline = DateTimeOffset.Now.Add(timeout);
    while (!condition())
    {
        if (DateTimeOffset.Now >= deadline)
        {
            throw new InvalidOperationException(timeoutMessage ?? "condition timed out");
        }

        await Task.Delay(20);
    }
}

static ProductionRecord SampleRecord(long sequenceId = 1001, DateTimeOffset? timestamp = null, ProductionResult result = ProductionResult.OK)
{
    var now = timestamp ?? DateTimeOffset.Now;
    var shift = new ShiftResolver(AppSettings.CreateDefaultShifts()).Resolve(now);
    return new ProductionRecord(
        0,
        "TEST-STATION",
        sequenceId,
        null,
        $"DYNTEST{sequenceId}",
        "78654-A01",
        DateOnly.FromDateTime(now.DateTime),
        TimeOnly.FromDateTime(now.DateTime),
        now,
        shift.Name,
        450,
        1,
        result == ProductionResult.OK ? 1 : 0,
        result == ProductionResult.NG ? 1 : 0,
        result == ProductionResult.REWORK ? 1 : 0,
        0.018m,
        "bar",
        0.00m,
        0.500m,
        result switch
        {
            ProductionResult.OK => "1",
            ProductionResult.NG => "2",
            ProductionResult.REWORK => "3",
            _ => null
        },
        result.ToString(),
        "1",
        "Auto",
        true,
        true,
        "0",
        "No Error",
        "1",
        "Running",
        "{}",
        now);
}

static async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
        Environment.ExitCode = 1;
    }
}

static Task RunSync(string name, Action test)
{
    return RunAsync(name, () =>
    {
        test();
        return Task.CompletedTask;
    });
}

static void AssertEqual<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"expected {expected}, got {actual} at line {line}");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static TException AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException ex)
    {
        return ex;
    }

    throw new InvalidOperationException($"expected {typeof(TException).Name}");
}

static async Task AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"expected {typeof(TException).Name}");
}

static IReadOnlyDictionary<string, PlcSignalValue> PartDataReadySignals(bool high, string? partNumber = null)
{
    var signals = new Dictionary<string, PlcSignalValue>(StringComparer.OrdinalIgnoreCase)
    {
        [PlcSignalMapping.PartDataReadySignalName] = new PlcSignalValue(high ? 1 : 0, high ? "HIGH" : "LOW", true)
    };
    if (partNumber is not null)
    {
        signals["Part Number"] = new PlcSignalValue(partNumber, partNumber, false);
    }
    return signals;
}

static PartDataSnapshot PartSnapshot(
    long sequenceId,
    string partNumber,
    string qrCode,
    decimal? leakValue,
    string? serialNumber = null,
    decimal upperLimit = 0.500m)
{
    var timestamp = new DateTimeOffset(2026, 8, 25, 10, 15, 0, TimeSpan.Zero).AddSeconds(sequenceId % 60);
    var signals = new Dictionary<string, PlcSignalValue>(StringComparer.OrdinalIgnoreCase)
    {
        ["Part Number"] = new PlcSignalValue(partNumber, partNumber, false),
        ["QR Code Value"] = new PlcSignalValue(qrCode, qrCode, false),
        ["Leak Test Value"] = new PlcSignalValue(leakValue, leakValue, false),
        ["Custom Production Value"] = new PlcSignalValue(42, "customProductionValue", false),
        [PlcSignalMapping.PartDataReadySignalName] = new PlcSignalValue(1, "HIGH", true)
    };
    return new PartDataSnapshot(
        "TEST-STATION",
        serialNumber,
        qrCode,
        partNumber,
        450,
        null,
        leakValue,
        "bar",
        0m,
        upperLimit,
        "1",
        "OK",
        "1",
        "Auto",
        true,
        true,
        "0",
        "No Error",
        "1",
        "Running",
        timestamp,
        signals);
}

static async Task<LogicalPart> LogicalPartByNumberAsync(ProductionRepository records, string partNumber)
{
    var parts = await records.QueryLogicalPartsAsync(
        new LogicalPartQuery(null, null, null, partNumber, null, null, 20),
        CancellationToken.None);
    return parts.Single(part => string.Equals(part.PartNumber, partNumber, StringComparison.Ordinal));
}

internal sealed class PartDataHarness : IAsyncDisposable
{
    private readonly PlcHandshakeService _handshake;

    private PartDataHarness(
        SettingsStore settings,
        ProductionRepository records,
        TestPlcClient plc,
        PlcHandshakeService handshake,
        PartDataReadyService trigger)
    {
        Settings = settings;
        Records = records;
        Plc = plc;
        _handshake = handshake;
        Trigger = trigger;
    }

    public SettingsStore Settings { get; }
    public ProductionRepository Records { get; }
    public TestPlcClient Plc { get; }
    public PartDataReadyService Trigger { get; }

    public static async Task<PartDataHarness> CreateAsync(
        bool enableDataSaved = false,
        bool failDataSavedWrites = false,
        int failDataSavedHighWritesRemaining = 0,
        int failDataSavedLowWritesRemaining = 0)
    {
        var root = Path.Combine(Path.GetTempPath(), $"dynak-part-data-{Guid.NewGuid():N}");
        var settings = new AppSettings
        {
            DatabasePath = Path.Combine(root, "station.db"),
            LiveLeakValueFilePath = Path.Combine(root, "live_leak_value.txt"),
            SignalMappings = PlcSignalMapping.CreateDefaults()
        };
        settings.SignalMappings.First(mapping => mapping.SignalName == PlcSafety.DataSavedSignalName).Enabled = enableDataSaved;
        var store = new SettingsStore(Options.Create(settings), NullLogger<SettingsStore>.Instance);
        var database = new SqliteDatabase(store, NullLogger<SqliteDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        var records = new ProductionRepository(database);
        var events = new EventRepository(database);
        var acquisition = new AcquisitionState();
        var plc = new TestPlcClient
        {
            FailWrites = failDataSavedWrites,
            FailDataSavedHighWritesRemaining = failDataSavedHighWritesRemaining,
            FailDataSavedLowWritesRemaining = failDataSavedLowWritesRemaining
        };
        var handshake = new PlcHandshakeService(plc, store, NullLogger<PlcHandshakeService>.Instance);
        var trigger = new PartDataReadyService(records, events, acquisition, handshake, new LiveLeakValueFileWriter(), plc, NullLogger<PartDataReadyService>.Instance);
        return new PartDataHarness(store, records, plc, handshake, trigger);
    }

    public async ValueTask DisposeAsync()
    {
        _handshake.Dispose();
        await Plc.DisposeAsync();
    }
}

internal sealed class TestPlcClient : IPlcClient
{
    private readonly Queue<PartDataSnapshot?> _snapshots = new();

    public bool FailWrites { get; init; }
    public int FailDataSavedHighWritesRemaining { get; set; }
    public int FailDataSavedLowWritesRemaining { get; set; }
    public int SnapshotReadCount { get; private set; }
    public List<TestPlcWrite> WriteAttempts { get; } = [];
    public List<TestPlcWrite> SuccessfulWrites { get; } = [];

    public void Enqueue(PartDataSnapshot? snapshot) => _snapshots.Enqueue(snapshot);

    public Task ConnectAsync(PlcClientConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> IsConnectedAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<PlcPollResult> ReadPollAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<PlcMachineStatus> ReadMachineStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new PlcMachineStatus(true, false, null, null, DateTimeOffset.Now));
    public Task<IReadOnlyDictionary<string, PlcSignalValue>> ReadConfiguredSignalsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, PlcSignalValue>>(new Dictionary<string, PlcSignalValue>());
    public Task<PartDataSnapshot?> ReadPartDataSnapshotAsync(CancellationToken cancellationToken)
    {
        SnapshotReadCount++;
        return Task.FromResult(_snapshots.Count == 0 ? null : _snapshots.Dequeue());
    }

    public Task WriteConfiguredSignalAsync(string signalName, object? value, CancellationToken cancellationToken)
    {
        var write = new TestPlcWrite(signalName, value, DateTimeOffset.Now);
        WriteAttempts.Add(write);

        if (FailWrites)
        {
            return Task.FromException(new IOException("Simulated PLC handshake rejection."));
        }

        if (string.Equals(signalName, PlcSafety.DataSavedSignalName, StringComparison.OrdinalIgnoreCase) &&
            value is true &&
            FailDataSavedHighWritesRemaining > 0)
        {
            FailDataSavedHighWritesRemaining--;
            return Task.FromException(new IOException("Simulated DATA SAVED HIGH rejection."));
        }

        if (string.Equals(signalName, PlcSafety.DataSavedSignalName, StringComparison.OrdinalIgnoreCase) &&
            value is false &&
            FailDataSavedLowWritesRemaining > 0)
        {
            FailDataSavedLowWritesRemaining--;
            return Task.FromException(new IOException("Simulated DATA SAVED LOW rejection."));
        }

        SuccessfulWrites.Add(write);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed record TestPlcWrite(
    string SignalName,
    object? Value,
    DateTimeOffset At);

internal sealed class ModbusTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly HashSet<int> _failingReadStarts;
    private readonly HashSet<int> _partialReadStarts;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serverTask;
    private TcpClient? _activeClient;

    private ModbusTestServer(Dictionary<int, ushort> registers, IEnumerable<int>? failingReadStarts, IEnumerable<int>? partialReadStarts)
    {
        Registers = registers;
        _failingReadStarts = failingReadStarts?.ToHashSet() ?? [];
        _partialReadStarts = partialReadStarts?.ToHashSet() ?? [];
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _serverTask = RunAsync(_stop.Token);
    }

    public Dictionary<int, ushort> Registers { get; }
    public System.Collections.Concurrent.ConcurrentQueue<byte> ObservedFunctionCodes { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<(int Start, int Count)> ObservedReadRanges { get; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<(int Start, ushort Value)> ObservedWriteValues { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static Task<ModbusTestServer> StartAsync(
        Dictionary<int, ushort> registers,
        IEnumerable<int>? failingReadStarts = null,
        IEnumerable<int>? partialReadStarts = null)
    {
        return Task.FromResult(new ModbusTestServer(registers, failingReadStarts, partialReadStarts));
    }

    public void DropConnection() => _activeClient?.Close();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _serverTask;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
        {
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            _activeClient = client;
            try
            {
                await using var stream = client.GetStream();
                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    var header = new byte[7];
                    await ReadExactAsync(stream, header, cancellationToken);
                    var transactionId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
                    var unitId = header[6];
                    var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                    var pdu = new byte[length - 1];
                    await ReadExactAsync(stream, pdu, cancellationToken);
                    await HandleRequestAsync(stream, transactionId, unitId, pdu, cancellationToken);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is IOException or SocketException or ObjectDisposedException)
            {
            }
            finally
            {
                _activeClient = null;
            }
        }
    }

    private Task HandleRequestAsync(NetworkStream stream, ushort transactionId, byte unitId, byte[] pdu, CancellationToken cancellationToken)
    {
        var function = pdu[0];
        ObservedFunctionCodes.Enqueue(function);
        return function switch
        {
            3 or 4 => SendReadRegistersAsync(stream, transactionId, unitId, function, pdu, cancellationToken),
            16 => SendWriteRegistersAsync(stream, transactionId, unitId, function, pdu, cancellationToken),
            _ => SendExceptionAsync(stream, transactionId, unitId, function, 1, cancellationToken)
        };
    }

    private async Task SendReadRegistersAsync(NetworkStream stream, ushort transactionId, byte unitId, byte function, byte[] pdu, CancellationToken cancellationToken)
    {
        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
        ObservedReadRanges.Enqueue((start, count));
        if (_failingReadStarts.Contains(start))
        {
            await SendExceptionAsync(stream, transactionId, unitId, function, 4, cancellationToken);
            return;
        }

        if (_partialReadStarts.Contains(start))
        {
            var truncatedCount = Math.Max(0, count - 1);
            var partial = new byte[2 + (truncatedCount * 2)];
            partial[0] = function;
            partial[1] = (byte)(count * 2);
            for (var index = 0; index < truncatedCount; index++)
            {
                Registers.TryGetValue(start + index, out var value);
                BinaryPrimitives.WriteUInt16BigEndian(partial.AsSpan(2 + (index * 2), 2), value);
            }

            await SendAsync(stream, transactionId, unitId, partial, cancellationToken);
            return;
        }

        var response = new byte[2 + (count * 2)];
        response[0] = function;
        response[1] = (byte)(count * 2);
        for (var index = 0; index < count; index++)
        {
            Registers.TryGetValue(start + index, out var value);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2 + (index * 2), 2), value);
        }

        await SendAsync(stream, transactionId, unitId, response, cancellationToken);
    }

    private async Task SendWriteRegistersAsync(NetworkStream stream, ushort transactionId, byte unitId, byte function, byte[] pdu, CancellationToken cancellationToken)
    {
        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
        for (var index = 0; index < count; index++)
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + (index * 2), 2));
            Registers[start + index] = value;
            ObservedWriteValues.Enqueue((start + index, value));
        }

        var response = new byte[5];
        response[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(1, 2), start);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(3, 2), count);
        await SendAsync(stream, transactionId, unitId, response, cancellationToken);
    }

    private static Task SendExceptionAsync(NetworkStream stream, ushort transactionId, byte unitId, byte function, byte code, CancellationToken cancellationToken)
    {
        return SendAsync(stream, transactionId, unitId, [unchecked((byte)(function | 0x80)), code], cancellationToken);
    }

    private static async Task SendAsync(NetworkStream stream, ushort transactionId, byte unitId, byte[] pdu, CancellationToken cancellationToken)
    {
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), (ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame.AsSpan(7));
        await stream.WriteAsync(frame, cancellationToken);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Client disconnected.");
            }

            offset += read;
        }
    }
}
