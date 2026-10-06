using System.Globalization;
using System.Text;
using DynaK.Service.Configuration;

namespace DynaK.Service.Services;

public sealed class LiveLeakValueFileWriter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _lastPath;
    private string? _lastValue;

    public async Task<bool> WriteIfChangedAsync(string configuredPath, decimal value, CancellationToken cancellationToken)
    {
        var path = StationDataPaths.ResolveMachinePath(configuredPath);
        var content = value.ToString("0.############################", CultureInfo.InvariantCulture);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(_lastPath, path) &&
                StringComparer.Ordinal.Equals(_lastValue, content) &&
                File.Exists(path))
            {
                return false;
            }

            if (File.Exists(path) && StringComparer.Ordinal.Equals(await File.ReadAllTextAsync(path, cancellationToken), content))
            {
                _lastPath = path;
                _lastValue = content;
                return false;
            }

            var directory = Path.GetDirectoryName(path)
                ?? throw new IOException("Live leak value text-file location has no parent folder.");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.ChangeExtension(path, ".tmp");
            if (StringComparer.OrdinalIgnoreCase.Equals(temporaryPath, path))
            {
                temporaryPath = $"{path}.write.tmp";
            }
            try
            {
                await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), cancellationToken);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            _lastPath = path;
            _lastValue = content;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
