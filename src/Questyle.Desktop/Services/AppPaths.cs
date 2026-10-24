using System.IO;

namespace Questyle.Desktop.Services;

public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var directory = Path.GetFullPath(Environment.GetEnvironmentVariable("QUESTYLE_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuestyleWindows"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
