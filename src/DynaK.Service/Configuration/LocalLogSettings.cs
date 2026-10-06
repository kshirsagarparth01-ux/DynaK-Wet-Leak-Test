namespace DynaK.Service.Configuration;

public sealed class LocalLogSettings
{
    public string Path { get; set; } = "logs/dynak-service.log";
    public long MaxFileBytes { get; set; } = 5 * 1024 * 1024;
    public int RetainedFileCount { get; set; } = 5;

    public LocalLogSettings Clone() => new()
    {
        Path = Path,
        MaxFileBytes = MaxFileBytes,
        RetainedFileCount = RetainedFileCount
    };
}
