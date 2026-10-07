using DynaK.Service.Configuration;
using DynaK.Service.Services;
using Microsoft.Data.Sqlite;

namespace DynaK.Service.Data;

public sealed class SqliteDatabase
{
    private readonly SettingsStore _settings;
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _pathGate = new();
    private string? _databasePath;

    static SqliteDatabase()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
    }

    public SqliteDatabase(SettingsStore settings, ILogger<SqliteDatabase> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string DatabasePath
    {
        get
        {
            lock (_pathGate)
            {
                return _databasePath ?? StationDataPaths.ResolveMachinePath(_settings.Current.DatabasePath);
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var configuredPath = _settings.Current.DatabasePath;
        var databasePath = StationDataPaths.ResolveMachinePath(configuredPath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await InitializeAtPathAsync(databasePath, cancellationToken);
        Activate(configuredPath);
    }

    public async Task<string> PrepareAsync(string configuredPath, CancellationToken cancellationToken)
    {
        var databasePath = StationDataPaths.ResolveMachinePath(configuredPath);
        await InitializeAtPathAsync(databasePath, cancellationToken);
        return databasePath;
    }

    public void Activate(string configuredPath)
    {
        var databasePath = StationDataPaths.ResolveMachinePath(configuredPath);
        lock (_pathGate)
        {
            _databasePath = databasePath;
        }

        _logger.LogInformation("SQLite database activated at {Path}", databasePath);
    }

    private async Task InitializeAtPathAsync(string databasePath, CancellationToken cancellationToken)
    {
        await ValidateDestinationAsync(databasePath, cancellationToken);
        await using var writeLock = await AcquireWriteLockAsync(cancellationToken);
        await using var connection = await OpenConnectionAtPathAsync(databasePath, cancellationToken);
        await ExecuteAsync(connection, """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            """, cancellationToken);

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS production_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                logical_part_id INTEGER NULL,
                attempt_kind TEXT NOT NULL DEFAULT 'INITIAL',
                station_id TEXT NOT NULL,
                plc_sequence_id INTEGER NOT NULL,
                serial_number TEXT NULL,
                qr_code TEXT NOT NULL,
                part_number TEXT NOT NULL,
                date TEXT NOT NULL,
                time TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                shift TEXT NOT NULL,
                target_parts_per_shift INTEGER NOT NULL,
                actual_part_count INTEGER NOT NULL,
                ok_count INTEGER NOT NULL,
                ng_count INTEGER NOT NULL,
                rework_count INTEGER NOT NULL,
                leak_test_value TEXT NULL,
                leak_test_unit TEXT NOT NULL,
                lower_limit TEXT NOT NULL,
                upper_limit TEXT NOT NULL,
                result TEXT NOT NULL,
                raw_result_value TEXT NULL,
                resolved_result TEXT NOT NULL,
                auto_manual_mode TEXT NOT NULL,
                raw_mode_value TEXT NULL,
                resolved_mode TEXT NOT NULL,
                machine_running INTEGER NOT NULL CHECK (machine_running IN (0,1)),
                error_code TEXT NULL,
                error_description TEXT NULL,
                raw_running_status_value TEXT NULL,
                resolved_running_status TEXT NULL,
                plc_snapshot_json TEXT NOT NULL DEFAULT '{}',
                created_timestamp TEXT NOT NULL,
                UNIQUE (station_id, plc_sequence_id)
            );

            CREATE TABLE IF NOT EXISTS machine_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                station_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                severity TEXT NOT NULL,
                code TEXT NULL,
                description TEXT NOT NULL,
                started_timestamp TEXT NOT NULL,
                cleared_timestamp TEXT NULL,
                created_timestamp TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_config (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_timestamp TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_timestamp TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_production_records_timestamp ON production_records(timestamp);
            CREATE INDEX IF NOT EXISTS ix_production_records_qr_code ON production_records(qr_code);
            CREATE INDEX IF NOT EXISTS ix_production_records_part_number ON production_records(part_number);
            CREATE INDEX IF NOT EXISTS ix_production_records_shift ON production_records(shift);
            CREATE INDEX IF NOT EXISTS ix_production_records_result ON production_records(result);
            CREATE INDEX IF NOT EXISTS ix_production_records_plc_sequence_id ON production_records(plc_sequence_id);
            CREATE INDEX IF NOT EXISTS ix_machine_events_started ON machine_events(started_timestamp);
            CREATE INDEX IF NOT EXISTS ix_machine_events_active ON machine_events(station_id, event_type, code, cleared_timestamp);
            """, cancellationToken);

        await EnsureProductionRecordsSchemaAsync(connection, cancellationToken);
        await EnsureSerialNumberSchemaAsync(connection, cancellationToken);
        await EnsureLogicalPartSchemaAsync(connection, cancellationToken);
        await EnsureLogicalPartIdentitySchemaAsync(connection, databasePath, cancellationToken);
        await EnsurePartDataSnapshotSchemaAsync(connection, cancellationToken);
        await EnsureProductionRecordIndexesAsync(connection, cancellationToken);

        await ExecuteAsync(connection, """
            INSERT OR IGNORE INTO schema_migrations (version, applied_timestamp)
            VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            INSERT OR IGNORE INTO schema_migrations (version, applied_timestamp)
            VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            INSERT OR IGNORE INTO schema_migrations (version, applied_timestamp)
            VALUES (3, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            INSERT OR IGNORE INTO schema_migrations (version, applied_timestamp)
            VALUES (4, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            INSERT OR IGNORE INTO schema_migrations (version, applied_timestamp)
            VALUES (5, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            INSERT OR IGNORE INTO schema_migrations (version, applied_timestamp)
            VALUES (6, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            INSERT OR IGNORE INTO schema_migrations (version, applied_timestamp)
            VALUES (7, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            INSERT OR IGNORE INTO schema_migrations (version, applied_timestamp)
            VALUES (8, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            PRAGMA user_version = 8;
            """, cancellationToken);

        _logger.LogInformation("SQLite database initialized at {Path}", databasePath);
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        return await OpenConnectionAtPathAsync(DatabasePath, cancellationToken);
    }

    private static async Task<SqliteConnection> OpenConnectionAtPathAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;", cancellationToken);
        return connection;
    }

    public async ValueTask<IAsyncDisposable> AcquireWriteLockAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        return new WriteLock(_writeGate);
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureProductionRecordsSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(connection, "production_records", cancellationToken);
        var createSql = await GetCreateSqlAsync(connection, "production_records", cancellationToken);
        var requiredColumns = new[]
        {
            "raw_result_value",
            "resolved_result",
            "raw_mode_value",
            "resolved_mode",
            "raw_running_status_value",
            "resolved_running_status"
        };

        var requiresRebuild = requiredColumns.Any(column => !columns.Contains(column)) ||
            createSql.Contains("CHECK (result", StringComparison.OrdinalIgnoreCase) ||
            createSql.Contains("CHECK (auto_manual_mode", StringComparison.OrdinalIgnoreCase);

        if (!requiresRebuild)
        {
            return;
        }

        await ExecuteAsync(connection, """
            PRAGMA foreign_keys=OFF;
            BEGIN TRANSACTION;

            ALTER TABLE production_records RENAME TO production_records_legacy;

            CREATE TABLE production_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                station_id TEXT NOT NULL,
                plc_sequence_id INTEGER NOT NULL,
                serial_number TEXT NULL,
                qr_code TEXT NOT NULL,
                part_number TEXT NOT NULL,
                date TEXT NOT NULL,
                time TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                shift TEXT NOT NULL,
                target_parts_per_shift INTEGER NOT NULL,
                actual_part_count INTEGER NOT NULL,
                ok_count INTEGER NOT NULL,
                ng_count INTEGER NOT NULL,
                rework_count INTEGER NOT NULL,
                leak_test_value TEXT NOT NULL,
                leak_test_unit TEXT NOT NULL,
                lower_limit TEXT NOT NULL,
                upper_limit TEXT NOT NULL,
                result TEXT NOT NULL,
                raw_result_value TEXT NULL,
                resolved_result TEXT NOT NULL,
                auto_manual_mode TEXT NOT NULL,
                raw_mode_value TEXT NULL,
                resolved_mode TEXT NOT NULL,
                machine_running INTEGER NOT NULL CHECK (machine_running IN (0,1)),
                error_code TEXT NULL,
                error_description TEXT NULL,
                raw_running_status_value TEXT NULL,
                resolved_running_status TEXT NULL,
                plc_snapshot_json TEXT NOT NULL DEFAULT '{}',
                created_timestamp TEXT NOT NULL,
                UNIQUE (station_id, plc_sequence_id)
            );

            INSERT INTO production_records (
                id, station_id, plc_sequence_id, serial_number, qr_code, part_number, date, time, timestamp, shift,
                target_parts_per_shift, actual_part_count, ok_count, ng_count, rework_count,
                leak_test_value, leak_test_unit, lower_limit, upper_limit, result, raw_result_value,
                resolved_result, auto_manual_mode, raw_mode_value, resolved_mode, machine_running,
                error_code, error_description, raw_running_status_value, resolved_running_status,
                plc_snapshot_json, created_timestamp
            )
            SELECT
                id, station_id, plc_sequence_id, NULL, qr_code, part_number, date, time, timestamp, shift,
                target_parts_per_shift, actual_part_count, ok_count, ng_count, rework_count,
                leak_test_value, leak_test_unit, lower_limit, upper_limit, result, NULL,
                result, auto_manual_mode, NULL, auto_manual_mode, machine_running,
                error_code, error_description, NULL,
                CASE WHEN machine_running = 1 THEN 'Running' ELSE 'Stopped' END,
                '{}', created_timestamp
            FROM production_records_legacy;

            DROP TABLE production_records_legacy;

            COMMIT;
            PRAGMA foreign_keys=ON;
            """, cancellationToken);
    }

    private static async Task EnsureSerialNumberSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(connection, "production_records", cancellationToken);
        if (!columns.Contains("serial_number"))
        {
            await ExecuteAsync(connection, "ALTER TABLE production_records ADD COLUMN serial_number TEXT NULL;", cancellationToken);
        }
    }

    private static Task EnsureProductionRecordIndexesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        return ExecuteAsync(connection, """
            CREATE INDEX IF NOT EXISTS ix_production_records_timestamp ON production_records(timestamp);
            CREATE INDEX IF NOT EXISTS ix_production_records_qr_code ON production_records(qr_code);
            CREATE INDEX IF NOT EXISTS ix_production_records_serial_number ON production_records(station_id, serial_number);
            CREATE INDEX IF NOT EXISTS ix_production_records_part_number ON production_records(part_number);
            CREATE INDEX IF NOT EXISTS ix_production_records_shift ON production_records(shift);
            CREATE INDEX IF NOT EXISTS ix_production_records_result ON production_records(result);
            CREATE INDEX IF NOT EXISTS ix_production_records_plc_sequence_id ON production_records(plc_sequence_id);
            CREATE INDEX IF NOT EXISTS ix_production_records_logical_part_id ON production_records(logical_part_id, timestamp, id);
            """, cancellationToken);
    }

    private static async Task EnsurePartDataSnapshotSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(connection, "production_records", cancellationToken);
        if (!columns.Contains("plc_snapshot_json"))
        {
            await ExecuteAsync(connection, "ALTER TABLE production_records ADD COLUMN plc_snapshot_json TEXT NOT NULL DEFAULT '{}';", cancellationToken);
            columns.Add("plc_snapshot_json");
        }

        if (!columns.Contains("cycle_time_seconds") &&
            !columns.Contains("cycle_completed") &&
            !columns.Contains("completed_timestamp"))
        {
            return;
        }

        await ExecuteAsync(connection, """
            PRAGMA foreign_keys=OFF;
            BEGIN TRANSACTION;

            ALTER TABLE production_records RENAME TO production_records_cycle_legacy;

            CREATE TABLE production_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                logical_part_id INTEGER NULL,
                attempt_kind TEXT NOT NULL DEFAULT 'INITIAL',
                station_id TEXT NOT NULL,
                plc_sequence_id INTEGER NOT NULL,
                serial_number TEXT NULL,
                qr_code TEXT NOT NULL,
                part_number TEXT NOT NULL,
                date TEXT NOT NULL,
                time TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                shift TEXT NOT NULL,
                target_parts_per_shift INTEGER NOT NULL,
                actual_part_count INTEGER NOT NULL,
                ok_count INTEGER NOT NULL,
                ng_count INTEGER NOT NULL,
                rework_count INTEGER NOT NULL,
                leak_test_value TEXT NULL,
                leak_test_unit TEXT NOT NULL,
                lower_limit TEXT NOT NULL,
                upper_limit TEXT NOT NULL,
                result TEXT NOT NULL,
                raw_result_value TEXT NULL,
                resolved_result TEXT NOT NULL,
                auto_manual_mode TEXT NOT NULL,
                raw_mode_value TEXT NULL,
                resolved_mode TEXT NOT NULL,
                machine_running INTEGER NOT NULL CHECK (machine_running IN (0,1)),
                error_code TEXT NULL,
                error_description TEXT NULL,
                raw_running_status_value TEXT NULL,
                resolved_running_status TEXT NULL,
                plc_snapshot_json TEXT NOT NULL DEFAULT '{}',
                created_timestamp TEXT NOT NULL,
                UNIQUE (station_id, plc_sequence_id)
            );

            INSERT INTO production_records (
                id, logical_part_id, attempt_kind, station_id, plc_sequence_id, serial_number,
                qr_code, part_number, date, time, timestamp, shift, target_parts_per_shift,
                actual_part_count, ok_count, ng_count, rework_count, leak_test_value,
                leak_test_unit, lower_limit, upper_limit, result, raw_result_value,
                resolved_result, auto_manual_mode, raw_mode_value, resolved_mode,
                machine_running, error_code, error_description, raw_running_status_value,
                resolved_running_status, plc_snapshot_json, created_timestamp
            )
            SELECT
                id, logical_part_id, attempt_kind, station_id, plc_sequence_id, serial_number,
                qr_code, part_number, date, time, timestamp, shift, target_parts_per_shift,
                actual_part_count, ok_count, ng_count, rework_count, leak_test_value,
                leak_test_unit, lower_limit, upper_limit, result, raw_result_value,
                resolved_result, auto_manual_mode, raw_mode_value, resolved_mode,
                machine_running, error_code, error_description, raw_running_status_value,
                resolved_running_status, plc_snapshot_json, created_timestamp
            FROM production_records_cycle_legacy;

            DROP TABLE production_records_cycle_legacy;

            COMMIT;
            PRAGMA foreign_keys=ON;
            """, cancellationToken);
    }

    private static async Task EnsureLogicalPartSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(connection, "production_records", cancellationToken);
        if (!columns.Contains("logical_part_id"))
        {
            await ExecuteAsync(connection, "ALTER TABLE production_records ADD COLUMN logical_part_id INTEGER NULL;", cancellationToken);
        }

        if (!columns.Contains("attempt_kind"))
        {
            await ExecuteAsync(connection, "ALTER TABLE production_records ADD COLUMN attempt_kind TEXT NOT NULL DEFAULT 'INITIAL';", cancellationToken);
        }

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS logical_parts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                station_id TEXT NOT NULL,
                qr_code TEXT NOT NULL,
                part_number TEXT NOT NULL,
                overall_result TEXT NOT NULL,
                latest_attempt_id INTEGER NULL,
                created_timestamp TEXT NOT NULL,
                updated_timestamp TEXT NOT NULL,
                UNIQUE (station_id, qr_code, part_number)
            );

            CREATE INDEX IF NOT EXISTS ix_logical_parts_updated ON logical_parts(updated_timestamp DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_logical_parts_part_number ON logical_parts(part_number);
            CREATE INDEX IF NOT EXISTS ix_logical_parts_result ON logical_parts(overall_result);

            INSERT OR IGNORE INTO logical_parts (
                station_id, qr_code, part_number, overall_result, latest_attempt_id,
                created_timestamp, updated_timestamp
            )
            SELECT
                station_id, qr_code, '', '', NULL,
                MIN(created_timestamp), MAX(timestamp)
            FROM production_records
            WHERE trim(qr_code) <> ''
            GROUP BY station_id, qr_code;

            UPDATE production_records AS current
            SET attempt_kind = CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM production_records AS earlier
                    WHERE earlier.station_id = current.station_id
                      AND earlier.qr_code = current.qr_code
                      AND (earlier.timestamp < current.timestamp OR (earlier.timestamp = current.timestamp AND earlier.id < current.id))
                ) THEN 'REWORK'
                WHEN upper(current.resolved_result) = 'NG'
                  OR upper(current.resolved_result) LIKE 'NG %'
                  OR upper(current.resolved_result) LIKE 'NG/%' THEN 'NG'
                ELSE 'INITIAL'
            END
            WHERE current.logical_part_id IS NULL;

            UPDATE production_records
            SET logical_part_id = (
                SELECT logical_parts.id
                FROM logical_parts
                WHERE logical_parts.station_id = production_records.station_id
                  AND logical_parts.qr_code = production_records.qr_code
            )
            WHERE logical_part_id IS NULL;

            UPDATE logical_parts
            SET latest_attempt_id = (
                    SELECT attempt.id
                    FROM production_records AS attempt
                    WHERE attempt.logical_part_id = logical_parts.id
                    ORDER BY attempt.timestamp DESC, attempt.id DESC
                    LIMIT 1
                ),
                part_number = COALESCE((
                    SELECT attempt.part_number
                    FROM production_records AS attempt
                    WHERE attempt.logical_part_id = logical_parts.id
                    ORDER BY attempt.timestamp DESC, attempt.id DESC
                    LIMIT 1
                ), part_number),
                overall_result = CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM production_records AS attempt
                        WHERE attempt.logical_part_id = logical_parts.id
                          AND (upper(attempt.resolved_result) = 'NG'
                            OR upper(attempt.resolved_result) LIKE 'NG %'
                            OR upper(attempt.resolved_result) LIKE 'NG/%')
                    ) THEN 'NG-REWORK'
                    ELSE COALESCE((
                        SELECT attempt.resolved_result
                        FROM production_records AS attempt
                        WHERE attempt.logical_part_id = logical_parts.id
                        ORDER BY attempt.timestamp DESC, attempt.id DESC
                        LIMIT 1
                    ), overall_result)
                END,
                updated_timestamp = COALESCE((
                    SELECT attempt.timestamp
                    FROM production_records AS attempt
                    WHERE attempt.logical_part_id = logical_parts.id
                    ORDER BY attempt.timestamp DESC, attempt.id DESC
                    LIMIT 1
                ), updated_timestamp);
            """, cancellationToken);
    }

    private async Task EnsureLogicalPartIdentitySchemaAsync(
        SqliteConnection connection,
        string databasePath,
        CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(connection, "logical_parts", cancellationToken);
        var createSql = await GetCreateSqlAsync(connection, "logical_parts", cancellationToken);
        var requiresRebuild = !columns.Contains("serial_number") ||
            createSql.Contains("UNIQUE (station_id, qr_code, part_number)", StringComparison.OrdinalIgnoreCase);

        if (requiresRebuild)
        {
            var backupPath = $"{databasePath}.pre-v8-serial-identity-{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
            await using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                await backup.OpenAsync(cancellationToken);
                connection.BackupDatabase(backup);
            }

            _logger.LogInformation("Created pre-serial-identity SQLite backup at {BackupPath}", backupPath);

            var legacyHasSerial = columns.Contains("serial_number");
            var serialProjection = legacyHasSerial ? "serial_number" : "NULL";

            await ExecuteAsync(connection, $"""
                PRAGMA foreign_keys=OFF;
                BEGIN TRANSACTION;

                ALTER TABLE logical_parts RENAME TO logical_parts_legacy;

                CREATE TABLE logical_parts (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    station_id TEXT NOT NULL,
                    serial_number TEXT NULL,
                    qr_code TEXT NOT NULL DEFAULT '',
                    part_number TEXT NOT NULL,
                    overall_result TEXT NOT NULL,
                    latest_attempt_id INTEGER NULL,
                    created_timestamp TEXT NOT NULL,
                    updated_timestamp TEXT NOT NULL
                );

                INSERT INTO logical_parts (
                    id, station_id, serial_number, qr_code, part_number, overall_result, latest_attempt_id,
                    created_timestamp, updated_timestamp
                )
                SELECT
                    id, station_id, {serialProjection}, qr_code, part_number, overall_result, latest_attempt_id,
                    created_timestamp, updated_timestamp
                FROM logical_parts_legacy;

                DROP TABLE logical_parts_legacy;

                COMMIT;
                PRAGMA foreign_keys=ON;
                """, cancellationToken);
        }

        // Backfill only when all nonblank attempts under one logical part agree on one serial.
        // This preserves legacy rows without guessing identity.
        await ExecuteAsync(connection, """
            UPDATE logical_parts
            SET serial_number = (
                SELECT MIN(trim(attempt.serial_number))
                FROM production_records AS attempt
                WHERE attempt.logical_part_id = logical_parts.id
                  AND trim(COALESCE(attempt.serial_number, '')) <> ''
                HAVING COUNT(DISTINCT trim(attempt.serial_number)) = 1
            )
            WHERE trim(COALESCE(serial_number, '')) = '';

            -- If legacy data assigned the same serial to more than one logical part,
            -- keep every historical part and leave the conflicting identity unresolved.
            UPDATE logical_parts AS current
            SET serial_number = NULL
            WHERE trim(COALESCE(current.serial_number, '')) <> ''
              AND EXISTS (
                  SELECT 1
                  FROM logical_parts AS other
                  WHERE other.id <> current.id
                    AND other.station_id = current.station_id
                    AND other.serial_number = current.serial_number
              );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_logical_parts_station_serial
                ON logical_parts(station_id, serial_number)
                WHERE serial_number IS NOT NULL AND trim(serial_number) <> '';

            CREATE INDEX IF NOT EXISTS ix_logical_parts_serial_number
                ON logical_parts(serial_number);
            CREATE INDEX IF NOT EXISTS ix_logical_parts_updated
                ON logical_parts(updated_timestamp DESC, id DESC);
            CREATE INDEX IF NOT EXISTS ix_logical_parts_part_number
                ON logical_parts(part_number);
            CREATE INDEX IF NOT EXISTS ix_logical_parts_result
                ON logical_parts(overall_result);
            """, cancellationToken);
    }

    private static async Task<HashSet<string>> GetColumnsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<string> GetCreateSqlAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? "";
    }

    private static async Task ValidateDestinationAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (Directory.Exists(databasePath))
        {
            throw new DatabaseConfigurationException("Database location must include a database file name, not a folder.");
        }

        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new DatabaseConfigurationException("Database location must include a parent folder.");

        try
        {
            Directory.CreateDirectory(directory);
            var probePath = Path.Combine(directory, $".dynak-write-test-{Guid.NewGuid():N}.tmp");
            await using (var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                await probe.WriteAsync(new byte[] { 0 }, cancellationToken);
            }

            if (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0)
            {
                return;
            }

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);

            await using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            var integrityResult = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new DatabaseConfigurationException($"The selected SQLite database failed its integrity check: {integrityResult ?? "no result"}.");
            }

            await using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await tables.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tableNames.Add(reader.GetString(0));
            }

            if (tableNames.Count > 0 && !tableNames.Overlaps(["production_records", "machine_events", "logical_parts", "app_config", "schema_migrations"]))
            {
                throw new DatabaseConfigurationException("The selected SQLite file is not a compatible DynaK station database.");
            }
        }
        catch (DatabaseConfigurationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw new DatabaseConfigurationException($"Database location is not writable or compatible: {ex.Message}", ex);
        }
    }

    private sealed class WriteLock : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate;

        public WriteLock(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public ValueTask DisposeAsync()
        {
            _gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class DatabaseConfigurationException : Exception
{
    public DatabaseConfigurationException(string message)
        : base(message)
    {
    }

    public DatabaseConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
