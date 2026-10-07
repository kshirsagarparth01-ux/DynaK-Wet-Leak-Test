using DynaK.Service.Configuration;
using DynaK.Service.Data;
using DynaK.Service.Exports;
using DynaK.Service.Logging;
using DynaK.Service.Models;
using DynaK.Service.Plc;
using DynaK.Service.Services;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

StationDataPaths.EnsureLayout();
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile(StationDataPaths.MachineConfigurationPath, optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);
builder.Host.UseWindowsService(options => options.ServiceName = "DynaK Wet Leak Test Acquisition");

var loggingSettings = builder.Configuration.GetSection("DynaK:LocalLogs").Get<LocalLogSettings>() ?? new LocalLogSettings();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Logging.AddProvider(new BoundedFileLoggerProvider(loggingSettings, StationDataPaths.RootDirectory));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("DynaK"));
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<SqliteDatabase>();
builder.Services.AddSingleton<ProductionRepository>();
builder.Services.AddSingleton<EventRepository>();
builder.Services.AddSingleton<ConfigRepository>();
builder.Services.AddSingleton<IPlcClient, MitsubishiModbusPlcClient>();
builder.Services.AddSingleton<AcquisitionState>();
builder.Services.AddSingleton<StationRuntimeControl>();
builder.Services.AddSingleton<PlcHandshakeService>();
builder.Services.AddSingleton<LiveLeakValueFileWriter>();
builder.Services.AddSingleton<PartDataReadyService>();
builder.Services.AddSingleton<DailyReportWriter>();
builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService<DailyReportService>();
builder.Services.AddHostedService<AcquisitionWorker>();

var app = builder.Build();
var ownerToken = Environment.GetEnvironmentVariable("DYNAK_DESKTOP_OWNER_TOKEN");

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    }
});

app.MapGet("/api/health", () => Results.Ok(new
{
    Application = "DynaK.WetLeakTest.Service",
    Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
    Ready = true,
    ProcessId = Environment.ProcessId
}));

app.MapGet("/api/build", () => Results.Ok(BuildIdentity.Load()));

if (!string.IsNullOrWhiteSpace(ownerToken))
{
    app.MapPost("/api/host/shutdown", (HttpRequest request, StationRuntimeControl runtime, IHostApplicationLifetime lifetime) =>
    {
        if (!HasValidOwnerToken(request, ownerToken))
        {
            return Results.Unauthorized();
        }

        if (runtime.IsStartRequested)
        {
            return Results.Conflict(new { message = "The station is running. Stop the station before shutting down its owned host process." });
        }

        lifetime.StopApplication();
        return Results.Accepted();
    });
}

app.MapGet("/api/state", async (ProductionRepository records, AcquisitionState state, SettingsStore settings, CancellationToken ct) =>
{
    var current = settings.Current;
    var now = DateTimeOffset.Now;
    var window = new ShiftResolver(current.Shifts).ResolveWindow(now);
    var recent = await records.QueryLogicalPartsAsync(new LogicalPartQuery(null, null, null, null, null, null, 10), ct);
    return Results.Ok(state.Snapshot(recent, current, window, now));
});

app.MapPost("/api/station/start", (StationRuntimeControl runtime, AcquisitionState state) =>
{
    if (runtime.RequestStart())
    {
        state.MarkStarting();
        return Results.Accepted();
    }

    return runtime.IsStartRequested
        ? Results.Ok(new { message = "Station is already started." })
        : Results.Conflict(new { message = "Station is stopping. Wait until it is stopped before starting again." });
});

app.MapPost("/api/station/stop", async (StationRuntimeControl runtime, AcquisitionState state, SettingsStore settings, CancellationToken ct) =>
{
    if (runtime.IsStartRequested)
    {
        state.MarkStopping();
    }

    await runtime.RequestStopAsync(ct);
    state.MarkStopped(settings.Current.Plc.IsConfigured);
    return Results.NoContent();
});

app.MapGet("/api/records", async (
    ProductionRepository records,
    string? from,
    string? to,
    string? shift,
    string? serialNumber,
    string? modelNumber,
    string? result,
    int? limit,
    CancellationToken ct) =>
{
    var query = BuildLogicalPartQuery(from, to, shift, serialNumber, modelNumber, result, limit ?? 200);
    return Results.Ok(await records.QueryLogicalPartsAsync(query, ct));
});

app.MapGet("/api/records/export/excel", async (
    ProductionRepository records,
    HttpResponse response,
    string? from,
    string? to,
    string? shift,
    string? serialNumber,
    string? modelNumber,
    string? result,
    CancellationToken ct) =>
{
    var query = BuildLogicalPartQuery(from, to, shift, serialNumber, modelNumber, result, limit: null);
    var parts = await records.QueryLogicalPartsAsync(query, ct);
    var exportedAt = DateTimeOffset.Now;
    var fileName = PartHistoryExcelExporter.CreateFileName(exportedAt);
    var workbook = PartHistoryExcelExporter.CreateWorkbook(parts, exportedAt);
    response.Headers["Cache-Control"] = "no-store";
    response.Headers["X-DynaK-Record-Count"] = parts.Count.ToString(CultureInfo.InvariantCulture);
    return Results.File(workbook, PartHistoryExcelExporter.ContentType, fileName);
});

app.MapGet("/api/records/{id:long}", async (long id, ProductionRepository records, CancellationToken ct) =>
{
    var record = await records.GetLogicalPartByIdAsync(id, ct);
    return record is null ? Results.NotFound() : Results.Ok(record);
});

app.MapGet("/api/events", async (EventRepository events, int? limit, CancellationToken ct) =>
{
    return Results.Ok(await events.RecentAsync(limit ?? 200, ct));
});

app.MapGet("/api/config", (SettingsStore settings, SqliteDatabase database) =>
{
    var current = settings.Current;
    return Results.Ok(new
    {
        current.StationId,
        current.StationName,
        DatabasePath = database.DatabasePath,
        current.LiveLeakValueFilePath,
        current.LeakTestUnit,
        current.LowerLimit,
        current.UpperLimit,
        current.ReportRootFolder,
        current.AutomaticDailyExportEnabled,
        PlcConfigured = current.Plc.IsConfigured,
        current.Plc,
        Shifts = current.Shifts,
        SignalMappings = current.SignalMappings,
        current.LocalLogs,
        SupportedAddressTypes = PlcSignalMapping.SupportedAddressTypes,
        SupportedDataTypes = PlcSignalMapping.SupportedDataTypes,
        SupportedDataTypeOptions = PlcSignalMapping.SupportedDataTypeOptions,
        SupportedDirections = PlcSignalMapping.SupportedDirections,
        SupportedByteOrders = PlcSignalMapping.SupportedByteOrders,
        SupportedWordOrders = PlcSignalMapping.SupportedWordOrders,
        SupportedEncodings = PlcSignalMapping.SupportedEncodings
    });
});

app.MapPut("/api/config", async (SettingsUpdate update, SettingsStore settings, ConfigRepository config, SqliteDatabase database, EventRepository events, AcquisitionState state, StationRuntimeControl runtime, ILogger<SettingsStore> logger, CancellationToken ct) =>
{
    if (runtime.IsStartRequested)
    {
        return Results.Conflict(new { message = "Stop the station before changing PLC mapping or station settings." });
    }

    try
    {
        var candidate = settings.BuildCandidate(update);
        var previousDatabasePath = database.DatabasePath;
        await database.PrepareAsync(candidate.DatabasePath, ct);
        await config.SaveAsync(candidate, ct);
        settings.Activate(candidate);
        database.Activate(candidate.DatabasePath);
        state.ConfigurePlc(candidate);
        if (!string.Equals(previousDatabasePath, database.DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            state.ClearProductionData();
        }
        await events.AddAsync(candidate.StationId, "CONFIGURATION_CHANGED", "INFO", null, "Configuration saved and activated", DateTimeOffset.Now, ct);
        return Results.NoContent();
    }
    catch (SettingsValidationException ex)
    {
        logger.LogWarning("Configuration validation failure: {Errors}", string.Join(" | ", ex.Errors));
        var current = settings.Current;
        await events.StartActiveAsync(current.StationId, "CONFIGURATION_ERROR", "WARN", "INVALID_CONFIGURATION", ex.Message, DateTimeOffset.Now, ct);
        return Results.BadRequest(new { errors = ex.Errors });
    }
    catch (DatabaseConfigurationException ex)
    {
        return Results.BadRequest(new { errors = new[] { ex.Message } });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { errors = new[] { $"Configuration could not be saved: {ex.Message}" } });
    }
});

app.MapFallbackToFile("index.html");

app.Run();

static DateTimeOffset? ParseDate(string? value)
{
    if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
    {
        return null;
    }

    var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
    return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
}

static LogicalPartQuery BuildLogicalPartQuery(
    string? from,
    string? to,
    string? shift,
    string? serialNumber,
    string? modelNumber,
    string? result,
    int? limit)
{
    return new LogicalPartQuery(
        ParseDate(from),
        ParseDate(to)?.AddDays(1),
        shift,
        serialNumber,
        modelNumber,
        result,
        limit);
}

static bool HasValidOwnerToken(HttpRequest request, string expectedToken)
{
    if (!request.Headers.TryGetValue("X-DynaK-Owner-Token", out var suppliedValues))
    {
        return false;
    }

    var suppliedToken = suppliedValues.ToString();
    var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
    var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
    return expectedBytes.Length == suppliedBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
}

public sealed class DatabaseInitializer : IHostedService
{
    private readonly SqliteDatabase _database;
    private readonly ConfigRepository _config;
    private readonly SettingsStore _settings;

    public DatabaseInitializer(
        SqliteDatabase database,
        ConfigRepository config,
        SettingsStore settings)
    {
        _database = database;
        _config = config;
        _settings = settings;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _database.InitializeAsync(cancellationToken);
        if (!_config.HasMachineConfiguration())
        {
            _settings.ApplyPersisted(await _config.LoadLegacyAsync(cancellationToken));
            await _config.SaveAsync(_settings.Current, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed record BuildIdentity(string AppVersion, string UiBuildId, string BuildDate, string SourceRevision)
{
    public static BuildIdentity Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "build-info.json");
        if (File.Exists(path))
        {
            return JsonSerializer.Deserialize<BuildIdentity>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException($"Build identity is empty: {path}");
        }

        var assembly = Assembly.GetEntryAssembly();
        return new BuildIdentity(
            assembly?.GetName().Version?.ToString() ?? "unknown",
            "development-unpackaged",
            "not packaged",
            "not available");
    }
}
