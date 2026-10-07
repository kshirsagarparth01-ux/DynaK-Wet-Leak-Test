using System.Globalization;
using DynaK.Service.Models;
using Microsoft.Data.Sqlite;

namespace DynaK.Service.Data;

public sealed class ProductionRepository
{
    private readonly SqliteDatabase _database;

    public ProductionRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<InsertRecordResult> InsertAsync(ProductionRecord record, CancellationToken cancellationToken)
    {
        await using var writeLock = await _database.AcquireWriteLockAsync(cancellationToken);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var logicalPart = await GetOrCreateLogicalPartAsync(connection, (SqliteTransaction)transaction, record, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        //code change by chatgpt
        command.CommandText = """
            INSERT INTO production_records (
                logical_part_id, attempt_kind, station_id, plc_sequence_id, serial_number, qr_code, part_number, date, time, timestamp, shift,
                target_parts_per_shift, actual_part_count, ok_count, ng_count, rework_count,
                leak_test_value, leak_test_unit, lower_limit, upper_limit, result, raw_result_value,
                resolved_result, auto_manual_mode, raw_mode_value, resolved_mode, machine_running,
                error_code, error_description, raw_running_status_value, resolved_running_status,
                plc_snapshot_json, created_timestamp
            )
            VALUES (
                -- $logical_part_id, $attempt_kind, $station_id, $plc_sequence_id, $serial_number, $qr_code, $part_number, $date, $time, $timestamp, $shift,
                $logical_part_id, $attempt_kind, $station_id, $plc_sequence_id, $serial_number, '', $part_number, $date, $time, $timestamp, $shift,
                $target_parts_per_shift, $actual_part_count, $ok_count, $ng_count, $rework_count,
                $leak_test_value, $leak_test_unit, $lower_limit, $upper_limit, $result, $raw_result_value,
                $resolved_result, $auto_manual_mode, $raw_mode_value, $resolved_mode, $machine_running,
                $error_code, $error_description, $raw_running_status_value, $resolved_running_status,
                $plc_snapshot_json, $created_timestamp
            );
            """;
        command.Parameters.AddWithValue("$logical_part_id", logicalPart.Id);
        command.Parameters.AddWithValue("$attempt_kind", logicalPart.AttemptCount == 0
            ? IsNg(record.Result) ? "NG" : "INITIAL"
            : "REWORK");
        AddRecordParameters(command, record);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            var id = await GetLastInsertIdAsync(connection, (SqliteTransaction)transaction, cancellationToken);
            await UpdateLogicalPartAsync(connection, (SqliteTransaction)transaction, logicalPart, id, record, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new InsertRecordResult(true, false, id);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.Message.Contains("production_records.station_id, production_records.plc_sequence_id", StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new InsertRecordResult(false, true, null);
        }
    }

    public async Task<ProductionRecord?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM production_records WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRecord(reader) : null;
    }

    public async Task<ProductionRecord?> GetBySequenceIdAsync(string stationId, long sequenceId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM production_records WHERE station_id = $station_id AND plc_sequence_id = $plc_sequence_id;";
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$plc_sequence_id", sequenceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRecord(reader) : null;
    }

    public async Task<bool> ExistsByPartNumberAsync(string stationId, string partNumber, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM production_records WHERE station_id = $station_id AND part_number = $part_number);";
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$part_number", partNumber);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task<long> GetNextSequenceIdAsync(string stationId, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(plc_sequence_id), 0) + 1 FROM production_records WHERE station_id = $station_id;";
        command.Parameters.AddWithValue("$station_id", stationId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM production_records;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ProductionRecord>> QueryAsync(ProductionRecordQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = new List<string>();

        if (query.From is not null)
        {
            where.Add("timestamp >= $from");
            command.Parameters.AddWithValue("$from", FormatTimestamp(query.From.Value));
        }

        if (query.To is not null)
        {
            where.Add("timestamp <= $to");
            command.Parameters.AddWithValue("$to", FormatTimestamp(query.To.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.Shift))
        {
            where.Add("shift = $shift");
            command.Parameters.AddWithValue("$shift", query.Shift.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.PartNumber))
        {
            where.Add("part_number LIKE $part_number");
            command.Parameters.AddWithValue("$part_number", $"%{query.PartNumber.Trim()}%");
        }

        //code change by chatgpt
        // if (!string.IsNullOrWhiteSpace(query.QrCode))
        // {
        //     where.Add("qr_code LIKE $qr_code");
        //     command.Parameters.AddWithValue("$qr_code", $"%{query.QrCode.Trim()}%");
        // }

        if (query.Result is not null)
        {
            AddResultFilter(where, command, query.Result.Value);
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 1000));
        command.CommandText = "SELECT * FROM production_records"
            + (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where))
            + " ORDER BY timestamp DESC, id DESC LIMIT $limit;";

        var records = new List<ProductionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(MapRecord(reader));
        }

        return records;
    }

    public async Task<IReadOnlyList<DateOnly>> GetProductionDatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT date FROM production_records ORDER BY date;";
        var dates = new List<DateOnly>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dates.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return dates;
    }

    public async Task<DailyReportRevision> GetDailyReportRevisionAsync(DateOnly date, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MAX(timestamp) FROM production_records WHERE date = $date;";
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var count = reader.GetInt32(0);
        DateTimeOffset? latest = reader.IsDBNull(1)
            ? null
            : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new DailyReportRevision(count, latest);
    }

    public async Task<IReadOnlyList<ProductionRecord>> QueryForDateAsync(DateOnly date, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM production_records WHERE date = $date ORDER BY timestamp, id;";
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var records = new List<ProductionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(MapRecord(reader));
        }

        return records;
    }

    public async Task<IReadOnlyList<LogicalPart>> QueryLogicalPartsAsync(LogicalPartQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var filters = new List<string> { "attempt.logical_part_id = logical.id" };
        var logicalFilters = new List<string>();

        if (query.From is not null)
        {
            filters.Add("attempt.timestamp >= $from");
            command.Parameters.AddWithValue("$from", FormatTimestamp(query.From.Value));
        }

        if (query.ToExclusive is not null)
        {
            filters.Add("attempt.timestamp < $to");
            command.Parameters.AddWithValue("$to", FormatTimestamp(query.ToExclusive.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.Shift))
        {
            filters.Add("attempt.shift = $shift");
            command.Parameters.AddWithValue("$shift", query.Shift.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.PartNumber))
        {
            filters.Add("attempt.part_number LIKE $part_number");
            command.Parameters.AddWithValue("$part_number", $"%{query.PartNumber.Trim()}%");
        }

        //code change by chatgpt
        // if (!string.IsNullOrWhiteSpace(query.QrCode))
        // {
        //     logicalFilters.Add("logical.qr_code LIKE $qr_code");
        //     command.Parameters.AddWithValue("$qr_code", $"%{query.QrCode.Trim()}%");
        // }

        AddLogicalResultFilter(logicalFilters, command, query.Result);
        logicalFilters.Add($"EXISTS (SELECT 1 FROM production_records AS attempt WHERE {string.Join(" AND ", filters)})");
        command.CommandText = $"""
            SELECT
                logical.id AS logical_id,
                logical.station_id AS logical_station_id,
                --code change by chatgpt
                -- logical.qr_code AS logical_qr_code,
                logical.part_number AS logical_part_number,
                logical.overall_result,
                logical.created_timestamp AS logical_created_timestamp,
                logical.updated_timestamp AS logical_updated_timestamp,
                latest.*
            FROM logical_parts AS logical
            JOIN production_records AS latest ON latest.id = logical.latest_attempt_id
            WHERE {string.Join(" AND ", logicalFilters)}
            ORDER BY logical.updated_timestamp DESC, logical.id DESC
            """;
        if (query.Limit is not null)
        {
            command.CommandText += " LIMIT $limit";
            command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit.Value, 1, 1000));
        }

        command.CommandText += ";";

        var parts = new List<LogicalPart>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            parts.Add(MapLogicalPart(reader, []));
        }

        return parts;
    }

    public async Task<LogicalPart?> GetLogicalPartByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        LogicalPart? part;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    logical.id AS logical_id,
                    logical.station_id AS logical_station_id,
                    --code change by chatgpt
                    -- logical.qr_code AS logical_qr_code,
                    logical.part_number AS logical_part_number,
                    logical.overall_result,
                    logical.created_timestamp AS logical_created_timestamp,
                    logical.updated_timestamp AS logical_updated_timestamp,
                    latest.*
                FROM logical_parts AS logical
                JOIN production_records AS latest ON latest.id = logical.latest_attempt_id
                WHERE logical.id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            part = await reader.ReadAsync(cancellationToken) ? MapLogicalPart(reader, []) : null;
        }

        if (part is null)
        {
            return null;
        }

        var attempts = new List<ProductionRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT *
                FROM production_records
                WHERE logical_part_id = $id
                ORDER BY timestamp, id;
                """;
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                attempts.Add(MapRecord(reader));
            }
        }

        return part with { Attempts = attempts };
    }

    public async Task<int> CountLogicalPartsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM logical_parts;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<ProductionCounters> GetCountersAsync(string stationId, string shift, DateOnly date, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*) AS actual,
                SUM(CASE WHEN upper(result) = 'OK' THEN 1 ELSE 0 END) AS ok_count,
                SUM(CASE WHEN upper(result) = 'NG' OR upper(result) LIKE 'NG %' OR upper(result) LIKE 'NG/%' THEN 1 ELSE 0 END) AS ng_count,
                SUM(CASE WHEN upper(result) = 'REWORK' THEN 1 ELSE 0 END) AS rework_count
            FROM production_records
            WHERE station_id = $station_id AND shift = $shift AND date = $date;
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$shift", shift);
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ProductionCounters(0, 0, 0, 0);
        }

        return new ProductionCounters(
            reader.GetInt32(reader.GetOrdinal("actual")),
            ReadInt(reader, "ok_count"),
            ReadInt(reader, "ng_count"),
            ReadInt(reader, "rework_count"));
    }

    public async Task<ProductionCounters> GetCountersForWindowAsync(string stationId, string shift, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*) AS actual,
                SUM(CASE WHEN upper(result) = 'OK' THEN 1 ELSE 0 END) AS ok_count,
                SUM(CASE WHEN upper(result) = 'NG' OR upper(result) LIKE 'NG %' OR upper(result) LIKE 'NG/%' THEN 1 ELSE 0 END) AS ng_count,
                SUM(CASE WHEN upper(result) = 'REWORK' THEN 1 ELSE 0 END) AS rework_count
            FROM production_records
            WHERE station_id = $station_id
              AND shift = $shift
              AND timestamp >= $from
              AND timestamp < $to;
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$shift", shift);
        command.Parameters.AddWithValue("$from", FormatTimestamp(from));
        command.Parameters.AddWithValue("$to", FormatTimestamp(to));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ProductionCounters(0, 0, 0, 0);
        }

        return new ProductionCounters(
            reader.GetInt32(reader.GetOrdinal("actual")),
            ReadInt(reader, "ok_count"),
            ReadInt(reader, "ng_count"),
            ReadInt(reader, "rework_count"));
    }

    private static void AddRecordParameters(SqliteCommand command, ProductionRecord record)
    {
        command.Parameters.AddWithValue("$station_id", record.StationId);
        command.Parameters.AddWithValue("$plc_sequence_id", record.PlcSequenceId);
        command.Parameters.AddWithValue("$serial_number", (object?)record.SerialNumber ?? DBNull.Value);
        //code change by chatgpt
        // command.Parameters.AddWithValue("$qr_code", record.QrCode);
        command.Parameters.AddWithValue("$part_number", record.PartNumber);
        command.Parameters.AddWithValue("$date", record.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$time", record.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$timestamp", FormatTimestamp(record.Timestamp));
        command.Parameters.AddWithValue("$shift", record.Shift);
        command.Parameters.AddWithValue("$target_parts_per_shift", record.TargetPartsPerShift);
        command.Parameters.AddWithValue("$actual_part_count", record.ActualPartCount);
        command.Parameters.AddWithValue("$ok_count", record.OkCount);
        command.Parameters.AddWithValue("$ng_count", record.NgCount);
        command.Parameters.AddWithValue("$rework_count", record.ReworkCount);
        command.Parameters.AddWithValue("$leak_test_value", FormatNullableDecimal(record.LeakTestValue));
        command.Parameters.AddWithValue("$leak_test_unit", record.LeakTestUnit);
        command.Parameters.AddWithValue("$lower_limit", FormatDecimal(record.LowerLimit));
        command.Parameters.AddWithValue("$upper_limit", FormatDecimal(record.UpperLimit));
        command.Parameters.AddWithValue("$result", record.Result);
        command.Parameters.AddWithValue("$raw_result_value", (object?)record.RawResultValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$resolved_result", record.ResolvedResult);
        command.Parameters.AddWithValue("$auto_manual_mode", record.ResolvedMode);
        command.Parameters.AddWithValue("$raw_mode_value", (object?)record.RawModeValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$resolved_mode", record.ResolvedMode);
        command.Parameters.AddWithValue("$machine_running", record.IsMachineRunning ? 1 : 0);
        command.Parameters.AddWithValue("$error_code", (object?)record.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$error_description", (object?)record.ErrorDescription ?? DBNull.Value);
        command.Parameters.AddWithValue("$raw_running_status_value", (object?)record.RawRunningStatusValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$resolved_running_status", (object?)record.ResolvedRunningStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$plc_snapshot_json", record.PlcSnapshotJson);
        command.Parameters.AddWithValue("$created_timestamp", FormatTimestamp(record.CreatedTimestamp));
    }

    private static ProductionRecord MapRecord(SqliteDataReader reader)
    {
        return new ProductionRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("station_id")),
            reader.GetInt64(reader.GetOrdinal("plc_sequence_id")),
            ReadNullableString(reader, "serial_number"),
            //code change by chatgpt
            // reader.GetString(reader.GetOrdinal("qr_code")),
            reader.GetString(reader.GetOrdinal("part_number")),
            DateOnly.ParseExact(reader.GetString(reader.GetOrdinal("date")), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly.ParseExact(reader.GetString(reader.GetOrdinal("time")), "HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("timestamp")), CultureInfo.InvariantCulture),
            reader.GetString(reader.GetOrdinal("shift")),
            reader.GetInt32(reader.GetOrdinal("target_parts_per_shift")),
            reader.GetInt32(reader.GetOrdinal("actual_part_count")),
            reader.GetInt32(reader.GetOrdinal("ok_count")),
            reader.GetInt32(reader.GetOrdinal("ng_count")),
            reader.GetInt32(reader.GetOrdinal("rework_count")),
            ReadNullableDecimal(reader, "leak_test_value"),
            reader.GetString(reader.GetOrdinal("leak_test_unit")),
            ReadDecimal(reader, "lower_limit"),
            ReadDecimal(reader, "upper_limit"),
            ReadNullableString(reader, "raw_result_value"),
            reader.GetString(reader.GetOrdinal("resolved_result")),
            ReadNullableString(reader, "raw_mode_value"),
            reader.GetString(reader.GetOrdinal("resolved_mode")),
            IsAutoMode(reader.GetString(reader.GetOrdinal("resolved_mode"))),
            reader.GetInt32(reader.GetOrdinal("machine_running")) == 1,
            ReadNullableString(reader, "error_code"),
            ReadNullableString(reader, "error_description"),
            ReadNullableString(reader, "raw_running_status_value"),
            ReadNullableString(reader, "resolved_running_status"),
            reader.GetString(reader.GetOrdinal("plc_snapshot_json")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_timestamp")), CultureInfo.InvariantCulture));
    }

    private static LogicalPart MapLogicalPart(SqliteDataReader reader, IReadOnlyList<ProductionRecord> attempts)
    {
        return new LogicalPart(
            reader.GetInt64(reader.GetOrdinal("logical_id")),
            reader.GetString(reader.GetOrdinal("logical_station_id")),
            //code change by chatgpt
            // reader.GetString(reader.GetOrdinal("logical_qr_code")),
            reader.GetString(reader.GetOrdinal("logical_part_number")),
            reader.GetString(reader.GetOrdinal("overall_result")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("logical_created_timestamp")), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("logical_updated_timestamp")), CultureInfo.InvariantCulture),
            MapRecord(reader),
            attempts);
    }

    private static async Task<LogicalPartState> GetOrCreateLogicalPartAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionRecord record,
        CancellationToken cancellationToken)
    {
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT logical.id, logical.overall_result, COUNT(attempt.id) AS attempt_count
                FROM logical_parts AS logical
                LEFT JOIN production_records AS attempt ON attempt.logical_part_id = logical.id
                WHERE logical.station_id = $station_id
                  --code change by chatgpt
                  -- AND logical.qr_code = $qr_code
                  AND logical.part_number = $part_number
                -- GROUP BY logical.id, logical.overall_result;
                GROUP BY logical.id, logical.overall_result, logical.updated_timestamp
                ORDER BY logical.updated_timestamp DESC, logical.id DESC
                LIMIT 1;
                """;
            query.Parameters.AddWithValue("$station_id", record.StationId);
            //code change by chatgpt
            // query.Parameters.AddWithValue("$qr_code", record.QrCode);
            query.Parameters.AddWithValue("$part_number", record.PartNumber);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new LogicalPartState(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2));
            }
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO logical_parts (
                station_id, qr_code, part_number, overall_result, latest_attempt_id,
                created_timestamp, updated_timestamp
            )
            //code change by chatgpt
            -- VALUES ($station_id, $qr_code, $part_number, $overall_result, NULL, $created_timestamp, $updated_timestamp);
            VALUES ($station_id, '', $part_number, $overall_result, NULL, $created_timestamp, $updated_timestamp);
            """;
        insert.Parameters.AddWithValue("$station_id", record.StationId);
        //code change by chatgpt
        // insert.Parameters.AddWithValue("$qr_code", record.QrCode);
        insert.Parameters.AddWithValue("$part_number", record.PartNumber);
        insert.Parameters.AddWithValue("$overall_result", IsNg(record.Result) ? "NG-REWORK" : record.Result);
        insert.Parameters.AddWithValue("$created_timestamp", FormatTimestamp(record.CreatedTimestamp));
        insert.Parameters.AddWithValue("$updated_timestamp", FormatTimestamp(record.Timestamp));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return new LogicalPartState(await GetLastInsertIdAsync(connection, transaction, cancellationToken), "", 0);
    }

    private static async Task UpdateLogicalPartAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LogicalPartState logicalPart,
        long attemptId,
        ProductionRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        //code change by chatgpt
        command.CommandText = """
            UPDATE logical_parts
            -- SET qr_code = $qr_code,
            SET part_number = $part_number,
                overall_result = $overall_result,
                latest_attempt_id = $latest_attempt_id,
                updated_timestamp = $updated_timestamp
            WHERE id = $id;
            """;
        //code change by chatgpt
        // command.Parameters.AddWithValue("$qr_code", record.QrCode);
        command.Parameters.AddWithValue("$part_number", record.PartNumber);
        command.Parameters.AddWithValue("$overall_result", logicalPart.AttemptCount > 0 || IsNg(record.Result) || IsNg(logicalPart.OverallResult)
            ? "NG-REWORK"
            : record.Result);
        command.Parameters.AddWithValue("$latest_attempt_id", attemptId);
        command.Parameters.AddWithValue("$updated_timestamp", FormatTimestamp(record.Timestamp));
        command.Parameters.AddWithValue("$id", logicalPart.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddLogicalResultFilter(List<string> where, SqliteCommand command, string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return;
        }

        var normalized = result.Trim().ToUpperInvariant();
        if (normalized == "NG")
        {
            where.Add("upper(logical.overall_result) IN ('NG', 'NG-REWORK')");
            return;
        }

        if (normalized == "REWORK")
        {
            where.Add("upper(logical.overall_result) IN ('REWORK', 'NG-REWORK')");
            return;
        }

        where.Add("upper(logical.overall_result) = $logical_result");
        command.Parameters.AddWithValue("$logical_result", normalized);
    }

    private static bool IsNg(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? "";
        return normalized == "NG" || normalized.StartsWith("NG ", StringComparison.Ordinal) || normalized.StartsWith("NG/", StringComparison.Ordinal) || normalized == "NG-REWORK";
    }

    private static void AddResultFilter(List<string> where, SqliteCommand command, ProductionResult result)
    {
        if (result == ProductionResult.NG)
        {
            where.Add("(upper(result) = $result OR upper(result) LIKE 'NG %' OR upper(result) LIKE 'NG/%')");
        }
        else
        {
            where.Add("upper(result) = $result");
        }

        command.Parameters.AddWithValue("$result", result.ToString());
    }

    private static bool IsAutoMode(string value)
    {
        return value.Equals("AUTO", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Auto", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
    }

    private static decimal ReadDecimal(SqliteDataReader reader, string name)
    {
        return decimal.Parse(reader.GetString(reader.GetOrdinal(name)), CultureInfo.InvariantCulture);
    }

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, string name)
    {
        var value = ReadNullableString(reader, name);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string FormatDecimal(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static object FormatNullableDecimal(decimal? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";

    private static string FormatTimestamp(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static async Task<long> GetLastInsertIdAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_insert_rowid();";
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }
}

public sealed record InsertRecordResult(bool Inserted, bool Duplicate, long? Id);

public sealed record ProductionCounters(int Actual, int Ok, int Ng, int Rework);

public sealed record DailyReportRevision(int RecordCount, DateTimeOffset? LatestRecordTimestamp);

internal sealed record LogicalPartState(long Id, string OverallResult, int AttemptCount);
