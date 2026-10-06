using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynaK.WetLeakTest.Desktop;

internal sealed record DesktopSettings(Uri BackendUri, string ServiceName, string BackendExecutablePath, TimeSpan StartupTimeout)
{
    public static DesktopSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "desktopsettings.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Required desktop configuration was not found.", path);
        }

        var file = JsonSerializer.Deserialize<DesktopSettingsFile>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Desktop configuration is empty or invalid.");
        var backend = file.Backend ?? throw new InvalidDataException("Desktop configuration is missing the backend section.");

        if (!Uri.TryCreate(backend.BaseUrl, UriKind.Absolute, out var backendUri) ||
            backendUri.Scheme != Uri.UriSchemeHttp ||
            !backendUri.IsLoopback ||
            backendUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(backendUri.Query) ||
            !string.IsNullOrEmpty(backendUri.Fragment))
        {
            throw new InvalidDataException("The backend base URL must be an HTTP loopback root URL such as http://127.0.0.1:5055/.");
        }

        if (string.IsNullOrWhiteSpace(backend.ServiceName))
        {
            throw new InvalidDataException("The backend Windows service name is required.");
        }

        if (string.IsNullOrWhiteSpace(backend.ExecutablePath) || Path.IsPathRooted(backend.ExecutablePath))
        {
            throw new InvalidDataException("The backend executable path must be relative to the desktop application directory.");
        }

        if (backend.StartupTimeoutSeconds is < 5 or > 120)
        {
            throw new InvalidDataException("The backend startup timeout must be between 5 and 120 seconds.");
        }

        return new DesktopSettings(
            backendUri,
            backend.ServiceName.Trim(),
            backend.ExecutablePath.Replace('/', Path.DirectorySeparatorChar),
            TimeSpan.FromSeconds(backend.StartupTimeoutSeconds));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class DesktopSettingsFile
    {
        public BackendSettings? Backend { get; init; }
    }

    private sealed class BackendSettings
    {
        public string? BaseUrl { get; init; }
        public string? ServiceName { get; init; }
        public string? ExecutablePath { get; init; }
        public int StartupTimeoutSeconds { get; init; }
    }
}

internal static class DesktopLog
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int RetainedFiles = 5;
    private static readonly object Gate = new();

    public static void Information(string message) => Write("Information", message, null);
    public static void Warning(string message, Exception? exception = null) => Write("Warning", message, exception);
    public static void Error(string message, Exception? exception = null) => Write("Error", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                var path = ResolveLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Rotate(path);
                var line = JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.Now.ToString("O"),
                    level,
                    message,
                    exception = exception?.ToString()
                });
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Desktop logging must not replace the original startup error shown to the operator.
        }
    }

    private static string ResolveLogPath()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("DYNAK_DATA_ROOT");
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DynaK", "Wet Leak Test Station")
            : Path.GetFullPath(configuredRoot.Trim());
        return Path.Combine(root, "logs", "dynak-desktop.log");
    }

    private static void Rotate(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < MaxFileBytes)
        {
            return;
        }

        var oldest = $"{path}.{RetainedFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = RetainedFiles - 1; index >= 1; index--)
        {
            var source = $"{path}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{path}.{index + 1}", true);
            }
        }

        File.Move(path, $"{path}.1", true);
    }
}
