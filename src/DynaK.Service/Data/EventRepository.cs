using System.Globalization;
using DynaK.Service.Models;
using Microsoft.Data.Sqlite;

namespace DynaK.Service.Data;

public sealed class EventRepository
{
    private readonly SqliteDatabase _database;

    public EventRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<long> AddAsync(string stationId, string eventType, string severity, string? code, string description, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var writeLock = await _database.AcquireWriteLockAsync(cancellationToken);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO machine_events (station_id, event_type, severity, code, description, started_timestamp, cleared_timestamp, created_timestamp)
            VALUES ($station_id, $event_type, $severity, $code, $description, $started_timestamp, NULL, $created_timestamp);
            """;
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$event_type", eventType);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$started_timestamp", FormatTimestamp(startedAt));
        command.Parameters.AddWithValue("$created_timestamp", FormatTimestamp(DateTimeOffset.Now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetLastInsertIdAsync(connection, cancellationToken);
    }

    public async Task<bool> StartActiveAsync(string stationId, string eventType, string severity, string? code, string description, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var writeLock = await _database.AcquireWriteLockAsync(cancellationToken);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var exists = connection.CreateCommand())
        {
            exists.Transaction = (SqliteTransaction)transaction;
            exists.CommandText = """
                SELECT id FROM machine_events
                WHERE station_id = $station_id
                  AND event_type = $event_type
                  AND IFNULL(code, '') = IFNULL($code, '')
                  AND cleared_timestamp IS NULL
                LIMIT 1;
                """;
            exists.Parameters.AddWithValue("$station_id", stationId);
            exists.Parameters.AddWithValue("$event_type", eventType);
            exists.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
            var active = await exists.ExecuteScalarAsync(cancellationToken);
            if (active is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO machine_events (station_id, event_type, severity, code, description, started_timestamp, cleared_timestamp, created_timestamp)
                VALUES ($station_id, $event_type, $severity, $code, $description, $started_timestamp, NULL, $created_timestamp);
                """;
            insert.Parameters.AddWithValue("$station_id", stationId);
            insert.Parameters.AddWithValue("$event_type", eventType);
            insert.Parameters.AddWithValue("$severity", severity);
            insert.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
            insert.Parameters.AddWithValue("$description", description);
            insert.Parameters.AddWithValue("$started_timestamp", FormatTimestamp(startedAt));
            insert.Parameters.AddWithValue("$created_timestamp", FormatTimestamp(DateTimeOffset.Now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ClearActiveAsync(string stationId, string eventType, string? code, DateTimeOffset clearedAt, CancellationToken cancellationToken)
    {
        await using var writeLock = await _database.AcquireWriteLockAsync(cancellationToken);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE machine_events
            SET cleared_timestamp = $cleared_timestamp
            WHERE station_id = $station_id
              AND event_type = $event_type
              AND IFNULL(code, '') = IFNULL($code, '')
              AND cleared_timestamp IS NULL;
            """;
        command.Parameters.AddWithValue("$cleared_timestamp", FormatTimestamp(clearedAt));
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$event_type", eventType);
        command.Parameters.AddWithValue("$code", (object?)code ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> ClearActiveByTypeAsync(string stationId, string eventType, DateTimeOffset clearedAt, CancellationToken cancellationToken)
    {
        await using var writeLock = await _database.AcquireWriteLockAsync(cancellationToken);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE machine_events
            SET cleared_timestamp = $cleared_timestamp
            WHERE station_id = $station_id
              AND event_type = $event_type
              AND cleared_timestamp IS NULL;
            """;
        command.Parameters.AddWithValue("$cleared_timestamp", FormatTimestamp(clearedAt));
        command.Parameters.AddWithValue("$station_id", stationId);
        command.Parameters.AddWithValue("$event_type", eventType);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MachineEvent>> RecentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM machine_events
            ORDER BY started_timestamp DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var events = new List<MachineEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(MapEvent(reader));
        }

        return events;
    }

    private static MachineEvent MapEvent(SqliteDataReader reader)
    {
        return new MachineEvent(
            reader.GetInt64(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("station_id")),
            reader.GetString(reader.GetOrdinal("event_type")),
            reader.GetString(reader.GetOrdinal("severity")),
            ReadNullableString(reader, "code"),
            reader.GetString(reader.GetOrdinal("description")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_timestamp")), CultureInfo.InvariantCulture),
            ReadNullableString(reader, "cleared_timestamp") is { } cleared
                ? DateTimeOffset.Parse(cleared, CultureInfo.InvariantCulture)
                : null,
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_timestamp")), CultureInfo.InvariantCulture));
    }

    private static string? ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static async Task<long> GetLastInsertIdAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid();";
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }
}
