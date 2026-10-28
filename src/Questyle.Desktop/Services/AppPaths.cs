using System.IO;

namespace Questyle.Desktop.Services;

public static class AppPaths
{
    public static string ApplicationDirectory => Path.GetDirectoryName(Environment.ProcessPath!)!;
    public static string DefaultDataDirectory => Path.Combine(ApplicationDirectory, "data");
    public static string PairingFile => Path.Combine(Environment.GetEnvironmentVariable("QUESTYLE_DATA_DIR") ?? ApplicationDirectory, "paired-headphones.json");
    public static string LanguageDirectory => Path.Combine(ApplicationDirectory, "language");
    public static string EqConfigDirectory => Path.Combine(Environment.GetEnvironmentVariable("QUESTYLE_DATA_DIR") ?? ApplicationDirectory, "eq-config");

    public static void InitializeRuntimeDirectories()
    {
        var temporaryDirectory = Path.Combine(DataDirectory, "temp");
        Directory.CreateDirectory(temporaryDirectory);
        Environment.SetEnvironmentVariable("TEMP", temporaryDirectory);
        Environment.SetEnvironmentVariable("TMP", temporaryDirectory);
    }

    public static string DataDirectory
    {
        get
        {
            var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("QUESTYLE_DATA_DIR")
                ?? DefaultDataDirectory);
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
