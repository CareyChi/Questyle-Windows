using Microsoft.Win32;

namespace Questyle.Desktop.Services;

public static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static string Command(string executable) => "\"" + executable + "\"";
    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return string.Equals(key?.GetValue("Questyle") as string, Command(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase);
        }
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled) key.SetValue("Questyle", Command(Environment.ProcessPath!));
        else key.DeleteValue("Questyle", throwOnMissingValue: false);
    }
}
