using DynaK.Service.Models;

namespace DynaK.Service.Plc;

public sealed record PlcSignalValue(
    object? RawValue,
    object? InterpretedValue,
    bool ValueMapMatched,
    string? Error = null);

public sealed record PlcPollResult(
    PlcMachineStatus MachineStatus,
    IReadOnlyDictionary<string, PlcSignalValue> Signals);
