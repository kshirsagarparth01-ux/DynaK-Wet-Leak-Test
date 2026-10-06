using System.Globalization;
using DynaK.Service.Data;
using DynaK.Service.Services;

namespace DynaK.Service.Exports;

public sealed class DailyReportWriter
{
    private readonly ProductionRepository _records;
    private readonly SettingsStore _settings;
    private readonly ILogger<DailyReportWriter> _logger;

    public DailyReportWriter(
        ProductionRepository records,
        SettingsStore settings,
        ILogger<DailyReportWriter> logger)
    {
        _records = records;
        _settings = settings;
        _logger = logger;
    }

    public async Task<string?> ExportDateAsync(DateOnly date, bool createWhenEmpty, CancellationToken cancellationToken)
    {
        var revision = await _records.GetDailyReportRevisionAsync(date, cancellationToken);
        if (revision.RecordCount == 0 && !createWhenEmpty)
        {
            return null;
        }

        var root = Path.GetFullPath(_settings.Current.ReportRootFolder);
        EnsureYearFolders(root, date.Year);
        var reportPath = GetReportPath(root, date);
        if (IsCurrent(reportPath, revision))
        {
            return reportPath;
        }

        var records = await _records.QueryForDateAsync(date, cancellationToken);
        var workbook = PartHistoryExcelExporter.CreateDailyWorkbook(records, date);
        await WriteAtomicallyAsync(reportPath, workbook, cancellationToken);
        PartHistoryExcelExporter.ValidateWorkbook(await File.ReadAllBytesAsync(reportPath, cancellationToken), records.Count);
        _logger.LogInformation("Daily production report written for {ReportDate}: {ReportPath} ({RecordCount} records)", date, reportPath, records.Count);
        return reportPath;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var date in await _records.GetProductionDatesAsync(cancellationToken))
        {
            try
            {
                await ExportDateAsync(date, createWhenEmpty: false, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Daily production report recovery failed for {ReportDate}; the database record remains authoritative and recovery will retry", date);
            }
        }
    }

    public static string GetReportPath(string root, DateOnly date) =>
        Path.Combine(
            Path.GetFullPath(root),
            date.Year.ToString("0000", CultureInfo.InvariantCulture),
            $"{date.Month:00} - {CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(date.Month)}",
            $"{date:yyyy-MM-dd}.xlsx");

    public static void EnsureYearFolders(string root, int year)
    {
        var yearPath = Path.Combine(Path.GetFullPath(root), year.ToString("0000", CultureInfo.InvariantCulture));
        for (var month = 1; month <= 12; month++)
        {
            Directory.CreateDirectory(Path.Combine(
                yearPath,
                $"{month:00} - {CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month)}"));
        }
    }

    private bool IsCurrent(string reportPath, DailyReportRevision revision)
    {
        if (!File.Exists(reportPath) ||
            revision.LatestRecordTimestamp.HasValue &&
            File.GetLastWriteTimeUtc(reportPath) < revision.LatestRecordTimestamp.Value.UtcDateTime)
        {
            return false;
        }

        try
        {
            PartHistoryExcelExporter.ValidateWorkbook(File.ReadAllBytes(reportPath), revision.RecordCount);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Daily production report is missing, stale, or invalid and will be regenerated: {ReportPath}", reportPath);
            return false;
        }
    }

    private static async Task WriteAtomicallyAsync(string reportPath, byte[] workbook, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(reportPath)
            ?? throw new InvalidOperationException("Daily report path has no parent folder.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(reportPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65536,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(workbook, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(reportPath))
            {
                File.Replace(temporaryPath, reportPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, reportPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public sealed class DailyReportService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromHours(1);
    private readonly DailyReportWriter _writer;
    private readonly SettingsStore _settings;
    private readonly ILogger<DailyReportService> _logger;

    public DailyReportService(
        DailyReportWriter writer,
        SettingsStore settings,
        ILogger<DailyReportService> logger)
    {
        _writer = writer;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateOnly? lastExportedClosedDate = null;
        var nextRecoveryAt = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_settings.Current.AutomaticDailyExportEnabled)
            {
                var now = DateTimeOffset.Now;
                var today = DateOnly.FromDateTime(now.LocalDateTime);
                var closedDate = today.AddDays(-1);
                try
                {
                    DailyReportWriter.EnsureYearFolders(_settings.Current.ReportRootFolder, today.Year);
                    if (lastExportedClosedDate != closedDate)
                    {
                        await _writer.ExportDateAsync(closedDate, createWhenEmpty: true, stoppingToken);
                        lastExportedClosedDate = closedDate;
                    }

                    if (now >= nextRecoveryAt)
                    {
                        await _writer.RecoverAsync(stoppingToken);
                        nextRecoveryAt = now.Add(RecoveryInterval);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Automatic daily production report export failed; production data remains safe in SQLite and export will retry");
                }
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }
}
