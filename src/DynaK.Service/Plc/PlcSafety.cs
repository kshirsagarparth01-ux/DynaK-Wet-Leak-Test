namespace DynaK.Service.Plc;

public static class PlcSafety
{
    public const string SystemReadySignalName = "System Ready";
    public const string CommunicationOkSignalName = "Communication OK";
    public const string DataSavedSignalName = "Data Saved";

    private static readonly HashSet<string> AllowedWriteSignals = new(StringComparer.OrdinalIgnoreCase)
    {
        SystemReadySignalName,
        CommunicationOkSignalName,
        DataSavedSignalName
    };

    public static bool IsWriteFunction(byte functionCode) => functionCode is 5 or 6 or 15 or 16;

    public static bool IsAllowedWrite(string signalName, string? address, string? addressType, int registerCount)
    {
        return AllowedWriteSignals.Contains(signalName.Trim()) &&
            !string.IsNullOrWhiteSpace(address) &&
            !string.IsNullOrWhiteSpace(addressType) &&
            registerCount > 0;
    }
}

public sealed class PlcWriteBlockedException : InvalidOperationException
{
    public PlcWriteBlockedException(string operation)
        : base($"PLC write operation '{operation}' was blocked by the production write allow-list.")
    {
    }
}
