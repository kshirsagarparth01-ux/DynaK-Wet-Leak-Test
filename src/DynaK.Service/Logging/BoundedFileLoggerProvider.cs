using System.Collections.Concurrent;
using System.Text.Json;
using DynaK.Service.Configuration;

namespace DynaK.Service.Logging;

public sealed class BoundedFileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, BoundedFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeGate = new();
    private readonly string _path;
    private readonly long _maxFileBytes;
    private readonly int _retainedFileCount;

    public BoundedFileLoggerProvider(LocalLogSettings settings, string baseDirectory)
    {
        _path = ResolvePath(settings.Path, baseDirectory);
        _maxFileBytes = Math.Clamp(settings.MaxFileBytes, 64 * 1024, 100 * 1024 * 1024);
        _retainedFileCount = Math.Clamp(settings.RetainedFileCount, 1, 50);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new BoundedFileLogger(name, this));

    public void Dispose()
    {
        _loggers.Clear();
    }

    internal void Write<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (level == LogLevel.None)
        {
            return;
        }

        try
        {
            lock (_writeGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RotateIfNeeded();
                File.AppendAllText(_path, Format(category, level, eventId, state, exception, formatter) + Environment.NewLine);
            }
        }
        catch
        {
            // Logging providers should not bring down acquisition if the disk is temporarily unavailable.
        }
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < _maxFileBytes)
        {
            return;
        }

        var oldest = RotatedPath(_retainedFileCount);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = _retainedFileCount - 1; index >= 1; index--)
        {
            var source = RotatedPath(index);
            if (File.Exists(source))
            {
                File.Move(source, RotatedPath(index + 1), true);
            }
        }

        File.Move(_path, RotatedPath(1), true);
    }

    private string RotatedPath(int index) => $"{_path}.{index}";

    private static string Format<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (state is IEnumerable<KeyValuePair<string, object?>> structuredState)
        {
            foreach (var item in structuredState)
            {
                if (item.Key != "{OriginalFormat}")
                {
                    properties[item.Key] = item.Value;
                }
            }
        }

        var payload = new
        {
            timestamp = DateTimeOffset.Now.ToString("O"),
            level = level.ToString(),
            category,
            eventId = eventId.Id,
            message = formatter(state, exception),
            exception = exception?.ToString(),
            properties
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string ResolvePath(string configuredPath, string baseDirectory)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));
    }
}

internal sealed class BoundedFileLogger : ILogger
{
    private readonly string _category;
    private readonly BoundedFileLoggerProvider _provider;

    public BoundedFileLogger(string category, BoundedFileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _provider.Write(_category, logLevel, eventId, state, exception, formatter);
    }
}
