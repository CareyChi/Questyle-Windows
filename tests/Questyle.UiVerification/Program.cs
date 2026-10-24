using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Questyle.Desktop;
using Questyle.Desktop.Services;

static class Program
{
    [STAThread]
    static void Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        try
        {
            var display = typeof(MainWindow).GetMethod("DisplaySnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var ldac = (ComboBox)typeof(MainWindow).GetField("ldac", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var adaptive = (ComboBox)typeof(MainWindow).GetField("adaptive", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var data = new DongleSnapshot("test", "1.0", DongleController.Codecs, DongleController.Codecs, "LDAC", "", "", 2, 1, false, 0, DateTime.Now);
            display.Invoke(window, [data]);
            if (ldac.SelectedIndex != 1 || adaptive.SelectedIndex != 0)
                throw new Exception("first read lost selection while parent card was disabled");
            Console.WriteLine("PASS first device read preserves option selection after enabling the card");
            display.Invoke(window, [data with { HeadphoneCodecs = [new CodecOption(7, "LHDC")], Adaptive = 0x83 }]);
            if (ldac.IsEnabled || adaptive.IsEnabled || adaptive.SelectedIndex != -1)
                throw new Exception("unsupported setting was shown as a valid option");
            Console.WriteLine("PASS unsupported headphone options are disabled, not clamped to defaults");
            Console.WriteLine("2 USB UI checks passed.");
        }
        finally { window.Close(); application.Shutdown(); }
    }
}
