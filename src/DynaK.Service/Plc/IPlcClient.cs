using DynaK.Service.Models;

namespace DynaK.Service.Plc;

public interface IPlcClient : IAsyncDisposable
{
    Task ConnectAsync(PlcClientConfiguration configuration, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<bool> IsConnectedAsync(CancellationToken cancellationToken);
    Task<PlcPollResult> ReadPollAsync(CancellationToken cancellationToken);
    Task<PlcMachineStatus> ReadMachineStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, PlcSignalValue>> ReadConfiguredSignalsAsync(CancellationToken cancellationToken);
    Task<PartDataSnapshot?> ReadPartDataSnapshotAsync(CancellationToken cancellationToken);
    Task WriteConfiguredSignalAsync(string signalName, object? value, CancellationToken cancellationToken);
}
