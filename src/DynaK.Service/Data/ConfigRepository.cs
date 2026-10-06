using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynaK.Service.Configuration;

namespace DynaK.Service.Data;

public sealed class ConfigRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SqliteDatabase _database;
    private readonly string _configurationPath;

    public ConfigRepository(SqliteDatabase database)
        : this(database, StationDataPaths.MachineConfigurationPath)
    {
    }

    public ConfigRepository(SqliteDatabase database, string configurationPath)
    {
        _database = database;
        _configurationPath = Path.GetFullPath(configurationPath);
    }

    public bool HasMachineConfiguration()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_configurationPath));
            return document.RootElement.TryGetProperty("DynaK", out var settings) &&
                settings.ValueKind == JsonValueKind.Object;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadLegacyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM app_config;";

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var path = _configurationPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var document = new MachineConfigurationDocument { DynaK = settings.Clone() };
        var json = JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine;

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<AppSettings> LoadMachineAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_configurationPath);
        var document = await JsonSerializer.DeserializeAsync<MachineConfigurationDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Machine configuration is empty.");
        return document.DynaK.Clone();
    }

    private sealed class MachineConfigurationDocument
    {
        [JsonPropertyName("DynaK")]
        public AppSettings DynaK { get; init; } = new();
    }
}
