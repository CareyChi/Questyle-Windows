using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;
using System.Text.Json;
using System.Globalization;
using System.Runtime.InteropServices;
using Questyle.Desktop.Services;
using Forms = System.Windows.Forms;

namespace Questyle.Desktop;

public partial class App : Application
{
    public App()
    {
        if (Environment.GetCommandLineArgs() is [_, "--peq-install" or "--peq-restore", _, var dataDirectory])
            Environment.SetEnvironmentVariable("QUESTYLE_DATA_DIR", Path.GetFullPath(dataDirectory));
        AppPaths.InitializeRuntimeDirectories();
        LanguageResources.Register();
    }

    private SingleInstance? instance;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Font? trayFont;
    private System.Drawing.Icon? trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--peq-install" or "--peq-restore", var endpoint, _])
        {
            Shutdown(PeqInstallation.RunElevated(endpoint, e.Args[0] == "--peq-install"));
            return;
        }
        if (e.Args is ["--verify-package", var report])
        {
            var verificationWindow = new MainWindow();
            double peqResponse = new PeqSettings(true, -6, [new(true, "PK", 1000, 6, 1)]).Response(1000, 48000);
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                ApplicationDirectory = AppPaths.ApplicationDirectory,
                RuntimeDirectory = AppContext.BaseDirectory,
                DataDirectory = AppPaths.DataDirectory,
                DefaultDataDirectory = AppPaths.DefaultDataDirectory,
                TemporaryDirectory = Path.GetTempPath(),
                FontFamily = verificationWindow.FontFamily.Source,
                WpfAssembly = typeof(Window).Assembly.Location,
                FormsAssembly = typeof(Forms.NotifyIcon).Assembly.Location,
                NativeRuntime = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().Single(module => module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase)).FileName,
                HelperFound = File.Exists(UsbDkTransport.HelperPath),
                PeqResponseDb = peqResponse,
                PeqNativeModule = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().Single(module => module.ModuleName.Equals("Questyle.Peq.Native.dll", StringComparison.OrdinalIgnoreCase)).FileName,
                WindowVisible = verificationWindow.IsVisible,
                WindowWidth = verificationWindow.Width,
                ResizeEnabled = verificationWindow.ResizeMode != ResizeMode.NoResize,
                AutoHeightEnabled = verificationWindow.SizeToContent == SizeToContent.Height,
                PerMonitorDpiV2 = AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new IntPtr(-4)),
                LanguageAssemblies = Directory.GetDirectories(AppPaths.LanguageDirectory).Select(directory =>
                {
                    var culture = CultureInfo.GetCultureInfo(Path.GetFileName(directory));
                    return new
                    {
                        Culture = culture.Name,
                        Wpf = typeof(Window).Assembly.GetSatelliteAssembly(culture).Location,
                        Forms = typeof(Forms.NotifyIcon).Assembly.GetSatelliteAssembly(culture).Location
                    };
                }).ToArray()
            }));
            verificationWindow.RequestExit();
            Shutdown();
            return;
        }
        if (e.Args is ["--update-handoff", var handoffFile])
        {
            _ = ContinueUpdateAsync(handoffFile);
            return;
        }
        instance = new SingleInstance("Questyle.Windows", () => Dispatcher.BeginInvoke(() => ((MainWindow)MainWindow).Restore()));
        if (!instance.IsPrimary)
        {
            instance.ActivateExisting();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        StartPrimary(window);
        window.Show();
    }

    // Started by the previous version after it installed this one. The single-instance
    // lock and tray icon are taken over only after the previous process has exited.
    private async Task ContinueUpdateAsync(string handoffFile)
    {
        var window = new MainWindow();
        MainWindow = window;
        try
        {
            await window.ContinueUpdateAsync(UpdateHandoff.Load(handoffFile), () =>
            {
                instance = new SingleInstance("Questyle.Windows", () => Dispatcher.BeginInvoke(() => ((MainWindow)MainWindow).Restore()));
                if (!instance.IsPrimary) throw new InvalidOperationException("Another Questyle instance started during the update.");
                StartPrimary(window);
            });
        }
        catch (Exception ex)
        {
            // Before the ready signal, the previous version rolls back once this process exits.
            File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "update-handoff-error.log"), ex.ToString());
            Shutdown(1);
        }
    }

    private void StartPrimary(MainWindow window)
    {
        window.StartUpdateChecks();
        var menu = new Forms.ContextMenuStrip();
        using (var installedFonts = new System.Drawing.Text.InstalledFontCollection())
        {
            var installedNames = installedFonts.Families.Select(family => family.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fontName = new[] { "Microsoft YaHei", "SimHei" }.FirstOrDefault(installedNames.Contains) ?? SystemFonts.MessageFontFamily.Source;
            trayFont = new System.Drawing.Font(fontName, 10.5f);
            menu.Font = trayFont;
        }
        var openItem = menu.Items.Add(UiLanguage.Current["打开 Questyle"], null, (_, _) => window.Restore());
        var exitItem = menu.Items.Add(UiLanguage.Current["退出"], null, (_, _) => window.RequestExit());
        UiLanguage.Current.Changed += () => { openItem.Text = UiLanguage.Current["打开 Questyle"]; exitItem.Text = UiLanguage.Current["退出"]; };
        trayIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "assets", "Questyle.ico"), 32, 32);
        tray = new Forms.NotifyIcon
        {
            Text = "Questyle",
            Icon = trayIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => window.Restore();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (tray != null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        instance?.Dispose();
        trayFont?.Dispose();
        trayIcon?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
}

