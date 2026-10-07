namespace DynaK.Service.Configuration;

public sealed class AppSettings
{
    public string StationId { get; set; } = "DynaK-WetLeak-01";
    public string StationName { get; set; } = "DynaK Wet Leak Test Station";
    public string DatabasePath { get; set; } = "data/dynak-wet-leak-test.db";
    public string LiveLeakValueFilePath { get; set; } = @"C:\DynaK\live_leak_value.txt";
    public string LeakTestUnit { get; set; } = "LPM";
    public decimal LowerLimit { get; set; } = 0.00m;
    public decimal UpperLimit { get; set; } = 0.500m;
    public string ReportRootFolder { get; set; } = StationDataPaths.DefaultReportRootFolder;
    public bool AutomaticDailyExportEnabled { get; set; } = true;
    public PlcConnectionSettings Plc { get; set; } = new();
    public List<ShiftDefinition> Shifts { get; set; } = [];
    public List<PlcSignalMapping> SignalMappings { get; set; } = [];
    public LocalLogSettings LocalLogs { get; set; } = new();

    public static List<ShiftDefinition> CreateDefaultShifts() =>
    [
        new("SHIFT A", new TimeOnly(6, 0), new TimeOnly(14, 0)),
        new("SHIFT B", new TimeOnly(14, 0), new TimeOnly(22, 0)),
        new("SHIFT C", new TimeOnly(22, 0), new TimeOnly(6, 0))
    ];

    public AppSettings Clone() => new()
    {
        StationId = StationId,
        StationName = StationName,
        DatabasePath = DatabasePath,
        LiveLeakValueFilePath = LiveLeakValueFilePath,
        LeakTestUnit = LeakTestUnit,
        LowerLimit = LowerLimit,
        UpperLimit = UpperLimit,
        ReportRootFolder = ReportRootFolder,
        AutomaticDailyExportEnabled = AutomaticDailyExportEnabled,
        Plc = Plc.Clone(),
        Shifts = Shifts.ToList(),
        SignalMappings = SignalMappings.Select(mapping => mapping.Clone()).ToList(),
        LocalLogs = LocalLogs.Clone()
    };
}
