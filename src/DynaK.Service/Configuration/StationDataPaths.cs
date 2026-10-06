using System.Text;

namespace DynaK.Service.Configuration;

public static class StationDataPaths
{
    private const string DataRootEnvironmentVariable = "DYNAK_DATA_ROOT";

    public static string RootDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DynaK", "Wet Leak Test Station")
                : Path.GetFullPath(configured.Trim());
        }
    }

    public static string ConfigurationDirectory => Path.Combine(RootDirectory, "config");
    public static string DataDirectory => Path.Combine(RootDirectory, "data");
    public static string LogDirectory => Path.Combine(RootDirectory, "logs");
    public static string MachineConfigurationPath => Path.Combine(ConfigurationDirectory, "appsettings.json");
    public static string DefaultReportRootFolder
    {
        get
        {
            var commonDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            return Path.Combine(
                string.IsNullOrWhiteSpace(commonDocuments) ? RootDirectory : commonDocuments,
                "DynaK Leak Test Report");
        }
    }

    public static void EnsureLayout()
    {
        Directory.CreateDirectory(ConfigurationDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);

        if (!File.Exists(MachineConfigurationPath))
        {
            File.WriteAllText(MachineConfigurationPath, "{}" + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    public static string ResolveMachinePath(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(RootDirectory, configuredPath));
    }

}
