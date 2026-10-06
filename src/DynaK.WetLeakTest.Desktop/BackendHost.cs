using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynaK.WetLeakTest.Desktop;

internal sealed class BackendHost : IAsyncDisposable
{
    private const string ExpectedApplicationId = "DynaK.WetLeakTest.Service";
    private readonly DesktopSettings _settings;
    private readonly HttpClient _httpClient;
    private Process? _ownedProcess;
    private string? _ownerToken;
    private bool _usingInstalledService;

    public BackendHost(DesktopSettings settings)
    {
        _settings = settings;
        _httpClient = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = settings.BackendUri,
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    public async Task EnsureReadyAsync(Action<string> reportStatus, CancellationToken cancellationToken)
    {
        var initialProbe = await ProbeAsync(cancellationToken);
        if (initialProbe == HealthProbe.Compatible)
        {
            DesktopLog.Information($"Connected to an existing DynaK backend at {_settings.BackendUri}.");
            return;
        }

        if (initialProbe == HealthProbe.Incompatible)
        {
            throw new InvalidOperationException($"Port {_settings.BackendUri.Port} is occupied by a process that is not the DynaK backend.");
        }

        reportStatus("Starting service...");
        if (await TryStartInstalledServiceAsync(cancellationToken))
        {
            _usingInstalledService = true;
            await WaitForHealthAsync(cancellationToken);
            DesktopLog.Information($"DynaK Windows service is ready at {_settings.BackendUri}.");
            return;
        }

        reportStatus("Starting local backend...");
        StartOwnedProcess();
        try
        {
            await WaitForHealthAsync(cancellationToken);
            DesktopLog.Information($"Owned DynaK backend process {_ownedProcess!.Id} is ready at {_settings.BackendUri}.");
        }
        catch
        {
            StopFailedOwnedProcess();
            throw;
        }
    }

    public async Task<string?> GetRuntimeStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await _httpClient.GetFromJsonAsync<StationState>("api/state", cancellationToken);
            return state?.RuntimeStatus;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            DesktopLog.Warning("Could not read station state while closing the HMI.", ex);
            return null;
        }
    }

    public async Task StartReadOnlyPollingAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync("api/station/start", content: null, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.Accepted or HttpStatusCode.OK))
        {
            throw new InvalidOperationException($"The backend rejected READ-ONLY PLC startup with HTTP {(int)response.StatusCode}.");
        }

        DesktopLog.Information("READ-ONLY PLC connection and polling requested.");
    }

    public async Task<OwnedBackendShutdownResult> ShutdownOwnedIfStoppedAsync(CancellationToken cancellationToken)
    {
        if (_ownedProcess is null || _ownedProcess.HasExited)
        {
            return OwnedBackendShutdownResult.NotOwned;
        }

        var runtimeStatus = await GetRuntimeStatusAsync(cancellationToken);
        if (!string.Equals(runtimeStatus, "Stopped", StringComparison.OrdinalIgnoreCase))
        {
            DesktopLog.Information("Owned backend remains running because the station is active or its state could not be confirmed as STOPPED.");
            return OwnedBackendShutdownResult.LeftRunning;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/host/shutdown");
        request.Headers.Add("X-DynaK-Owner-Token", _ownerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            DesktopLog.Warning($"Owned backend rejected graceful shutdown with HTTP {(int)response.StatusCode}.");
            return OwnedBackendShutdownResult.LeftRunning;
        }

        try
        {
            await _ownedProcess.WaitForExitAsync(cancellationToken);
            DesktopLog.Information("Owned backend stopped gracefully.");
            return OwnedBackendShutdownResult.Stopped;
        }
        catch (OperationCanceledException)
        {
            DesktopLog.Warning("Timed out waiting for the owned backend to stop; it was left running to avoid an abrupt termination.");
            return OwnedBackendShutdownResult.LeftRunning;
        }
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        _ownedProcess?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<bool> TryStartInstalledServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var service = new ServiceController(_settings.ServiceName);
            var status = service.Status;
            if (status == ServiceControllerStatus.Running)
            {
                return true;
            }

            if (status is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused)
            {
                service.Start();
            }

            await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, _settings.StartupTimeout), cancellationToken);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            DesktopLog.Warning("The installed DynaK Windows service could not be started; trying the packaged backend executable.", ex);
            return false;
        }
        catch (System.ServiceProcess.TimeoutException ex)
        {
            throw new System.TimeoutException("The DynaK Windows service did not reach the running state before the startup timeout.", ex);
        }
    }

    private void StartOwnedProcess()
    {
        var executablePath = ResolveBackendExecutablePath();
        _ownerToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["ASPNETCORE_URLS"] = _settings.BackendUri.GetLeftPart(UriPartial.Authority);
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        startInfo.Environment["DYNAK_DESKTOP_OWNER_TOKEN"] = _ownerToken;

        _ownedProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _ownedProcess.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                DesktopLog.Information($"Backend: {eventArgs.Data}");
            }
        };
        _ownedProcess.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                DesktopLog.Warning($"Backend: {eventArgs.Data}");
            }
        };

        if (!_ownedProcess.Start())
        {
            throw new InvalidOperationException("Windows did not start the DynaK backend process.");
        }

        _ownedProcess.BeginOutputReadLine();
        _ownedProcess.BeginErrorReadLine();
        DesktopLog.Information($"Started owned backend process {_ownedProcess.Id} from {executablePath}.");
    }

    private string ResolveBackendExecutablePath()
    {
        var packagedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _settings.BackendExecutablePath));
        if (File.Exists(packagedPath))
        {
            return packagedPath;
        }

#if DEBUG
        var developmentPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "DynaK.Service", "bin", "Debug", "net10.0", "DynaK.Service.exe"));
        if (File.Exists(developmentPath))
        {
            return developmentPath;
        }
#endif

        throw new FileNotFoundException(
            "The DynaK backend executable was not found. Reinstall the application or build DynaK.Service before starting the desktop host.",
            packagedPath);
    }

    private async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.StartupTimeout);

        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_ownedProcess is { HasExited: true })
                {
                    throw new InvalidOperationException($"The DynaK backend exited during startup with code {_ownedProcess.ExitCode}.");
                }

                if (_usingInstalledService && IsInstalledServiceStopped())
                {
                    throw new InvalidOperationException($"The DynaK Windows service stopped before backend initialization completed. {ReadLatestServiceFailure()}");
                }

                var probe = await ProbeAsync(timeout.Token);
                if (probe == HealthProbe.Compatible)
                {
                    return;
                }

                if (probe == HealthProbe.Incompatible)
                {
                    throw new InvalidOperationException($"Port {_settings.BackendUri.Port} is occupied by a process that is not the DynaK backend.");
                }

                await Task.Delay(250, timeout.Token);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new System.TimeoutException($"The backend health endpoint did not confirm service initialization within {_settings.StartupTimeout.TotalSeconds:0} seconds. {ReadLatestServiceFailure()}", ex);
        }
    }

    private bool IsInstalledServiceStopped()
    {
        try
        {
            using var service = new ServiceController(_settings.ServiceName);
            return service.Status == ServiceControllerStatus.Stopped;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string ReadLatestServiceFailure()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("DYNAK_DATA_ROOT");
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DynaK", "Wet Leak Test Station")
            : Path.GetFullPath(configuredRoot.Trim());
        var logPath = Path.Combine(root, "logs", "dynak-service.log");

        try
        {
            var cutoff = DateTimeOffset.Now.AddMinutes(-10);
            foreach (var line in File.ReadLines(logPath).TakeLast(200).Reverse())
            {
                using var json = JsonDocument.Parse(line);
                var entry = json.RootElement;
                if (!entry.TryGetProperty("level", out var level) || !string.Equals(level.GetString(), "Error", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (entry.TryGetProperty("timestamp", out var timestamp) &&
                    DateTimeOffset.TryParse(timestamp.GetString(), out var parsed) && parsed < cutoff)
                {
                    continue;
                }

                var message = entry.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
                var exception = entry.TryGetProperty("exception", out var exceptionElement) ? exceptionElement.GetString() : null;
                var exceptionSummary = exception?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                var summary = string.Join(" - ", new[] { message, exceptionSummary }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    return $"Latest service error: {summary}. Service log: {logPath}";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DesktopLog.Warning($"Could not read the service startup diagnostic from {logPath}.", ex);
        }

        return $"No recent service error was readable. Service log: {logPath}";
    }

    private async Task<HealthProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("api/health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return HealthProbe.Incompatible;
            }

            var health = await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken: cancellationToken);
            return health is { Ready: true } && string.Equals(health.Application, ExpectedApplicationId, StringComparison.Ordinal)
                ? HealthProbe.Compatible
                : HealthProbe.Incompatible;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthProbe.Unavailable;
        }
    }

    private void StopFailedOwnedProcess()
    {
        if (_ownedProcess is null || _ownedProcess.HasExited)
        {
            return;
        }

        try
        {
            _ownedProcess.Kill(entireProcessTree: true);
            _ownedProcess.WaitForExit(5000);
            DesktopLog.Warning("Stopped an owned backend process that failed before becoming API-ready.");
        }
        catch (Exception ex)
        {
            DesktopLog.Error("Could not stop the failed owned backend process.", ex);
        }
    }

    private enum HealthProbe
    {
        Unavailable,
        Compatible,
        Incompatible
    }

    private sealed record HealthResponse(
        [property: JsonPropertyName("application")] string? Application,
        [property: JsonPropertyName("ready")] bool Ready);

    private sealed record StationState(
        [property: JsonPropertyName("runtimeStatus")] string? RuntimeStatus);
}

internal enum OwnedBackendShutdownResult
{
    NotOwned,
    Stopped,
    LeftRunning
}
