using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media.Effects;
using System.Windows.Documents;
using System.Windows.Input;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using Questyle.Desktop;
using Questyle.Desktop.Services;

static class Program
{
    private static int count;
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); count++; }
    private static void VerifyPeqHeaderAlignment(MainWindow window)
    {
        var table = Field<ScrollViewer>(window, "peqTable");
        var header = ((Grid)Field<Border>(window, "peqCard").Child).Children.OfType<Grid>().Single(grid => grid.ColumnDefinitions.Count == 6);
        var row = Descendants<Grid>(table).First(grid => grid.ColumnDefinitions.Count == 6);
        var root = (FrameworkElement)window.Content;
        if (Math.Abs(header.ActualWidth - table.ViewportWidth) > 1) throw new Exception("PEQ header does not follow the table viewport width");
        foreach (var label in header.Children.OfType<TextBlock>().Where(label => Grid.GetColumn(label) is >= 1 and <= 4))
        {
            var editor = row.Children.OfType<Control>().Single(control => Grid.GetColumn(control) == Grid.GetColumn(label));
            double editorX = editor.TranslatePoint(new Point(editor.Padding.Left + editor.BorderThickness.Left, 0), root).X;
            if (Math.Abs(label.TranslatePoint(new Point(), root).X - editorX) > 1.5) throw new Exception("PEQ heading is not aligned with its editor content: " + label.Text);
        }
        var enabledLabel = header.Children.OfType<FrameworkElement>().Single(label => Grid.GetColumn(label) == 0);
        var enabled = row.Children.OfType<CheckBox>().Single(); enabled.ApplyTemplate();
        var check = (Border)enabled.Template.FindName("CheckSurface", enabled);
        double labelCentre = enabledLabel.TranslatePoint(new Point(enabledLabel.ActualWidth / 2, 0), root).X;
        if (Math.Abs(labelCentre - check.TranslatePoint(new Point(check.ActualWidth / 2, 0), root).X) > 1.5) throw new Exception("PEQ enabled heading is not centered above its checkbox");
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args is ["--activate", var name])
        {
            using var secondary = new SingleInstance(name, () => throw new Exception("Secondary registered a listener"));
            if (secondary.IsPrimary) throw new Exception("Second process became primary");
            secondary.ActivateExisting();
            return;
        }

        if (Environment.GetEnvironmentVariable("QUESTYLE_DATA_DIR") == null) throw new Exception("UI verification requires an isolated QUESTYLE_DATA_DIR.");
        Directory.CreateDirectory(AppPaths.DataDirectory);
        AppPaths.InitializeRuntimeDirectories();
        Check(AppPaths.DefaultDataDirectory == Path.Combine(AppPaths.ApplicationDirectory, "data") && Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) == Path.Combine(AppPaths.DataDirectory, "temp"), "application defaults and temporary files stay under the application data directory");
        File.Delete(AppPaths.PairingFile);
        File.Delete(Path.Combine(AppPaths.DataDirectory, "settings.json"));
        File.Delete(Path.Combine(AppPaths.DataDirectory, "updates.json"));
        File.Delete(Path.Combine(AppPaths.DataDirectory, "audio-preferences.json"));
        File.Delete(Path.Combine(AppPaths.DataDirectory, "original-device-name.txt"));
        File.Delete(Path.Combine(AppPaths.DataDirectory, "peq.json"));
        var remembered = new AudioSink("01:02:03:04:05:06", "已配对耳机", 4);
        var saved = new HeadphoneHistory(AppPaths.PairingFile);
        saved.Load(); saved.Remember(remembered); saved.Remember(remembered with { Name = "测试耳机" });
        var reloaded = new HeadphoneHistory(AppPaths.PairingFile); reloaded.Load();
        Check(reloaded.Headphones.Count == 1 && reloaded.Headphones[0].Name == "测试耳机", "paired headphones persist and update by address without duplicate records");
        var invalidPath = Path.Combine(AppPaths.DataDirectory, "invalid-history.json"); File.WriteAllText(invalidPath, "invalid");
        try { new HeadphoneHistory(invalidPath).Load(); throw new Exception("Corrupt history accepted"); } catch (JsonException) { Check(true, "invalid saved history reports its error"); }
        var unwritable = new HeadphoneHistory(Path.Combine(AppPaths.DataDirectory, Guid.NewGuid().ToString("N"), "missing.json"));
        try { unwritable.Remember(remembered); throw new Exception("Missing directory accepted"); } catch (DirectoryNotFoundException) { Check(unwritable.Headphones.Count == 0, "failed save does not claim the headphone was persisted"); }

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow();
        if (args is ["--updates-only"])
        {
            try { Render(window, "update-anchor", 1240, 800); VerifyUpdates(window); Console.WriteLine($"{count} focused update checks passed."); }
            finally { window.RequestExit(); application.Shutdown(); }
            return;
        }
        var livePeqWrites = new List<PeqSettings>();
        typeof(MainWindow).GetField("peqWriteConfiguration", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, (Func<PeqSettings, long>)(settings => { livePeqWrites.Add(settings); return livePeqWrites.Count; }));
        Invoke(window, "RememberOriginalName", "首次设备名称");
        Invoke(window, "RememberOriginalName", "修改后的名称");
        var reopened = new MainWindow();
        Check(Field<string>(reopened, "originalDeviceName") == "首次设备名称", "original device name is saved once and survives reopening without being replaced by a renamed value");
        reopened.RequestExit();
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            var reads = 0;
            Func<Task> simulatedRead = () => { reads++; return Task.CompletedTask; };
            ((Task)Invoke(window, "AutoReadOnce", false, simulatedRead)!).GetAwaiter().GetResult();
            ((Task)Invoke(window, "AutoReadOnce", true, simulatedRead)!).GetAwaiter().GetResult();
            ((Task)Invoke(window, "AutoReadOnce", true, simulatedRead)!).GetAwaiter().GetResult();
            Check(reads == 1, "unread detected device requests one automatic read without repeated sessions");
            Invoke(window, "UpdateUsbDetection", false);
            ((Task)Invoke(window, "AutoReadOnce", true, simulatedRead)!).GetAwaiter().GetResult();
            ((Task)Invoke(window, "AutoReadOnce", true, simulatedRead)!).GetAwaiter().GetResult();
            Check(reads == 2, "a removed and reinserted device requests one new automatic read");
            ((Task)Invoke(window, "ReadWhenForeground")!).GetAwaiter().GetResult();
            Check(!window.IsVisible && Field<TextBlock>(window, "supportedInfo").Text.Length == 0, "hidden window performs no automatic USB read and has no read-instruction text");
            Render(window, "device-unread", 1240, 800);
            var ldac = Field<ComboBox>(window, "ldac");
            var adaptive = Field<ComboBox>(window, "adaptive");
            var ldacField = Field<FrameworkElement>(window, "ldacField");
            var adaptiveField = Field<FrameworkElement>(window, "adaptiveField");
            var microphone = Field<CheckBox>(window, "microphone");
            var auracast = Field<CheckBox>(window, "auracast");
            var logo = Field<Image>(window, "codecLogo");
            var data = new DongleSnapshot("QCC Dongle Pro2", "3.71.3.1", DongleController.Codecs, DongleController.Codecs, "LDAC", remembered.Address, "测试耳机", 2, 1, false, 0, DateTime.Now);
            var deviceViewport = (ScrollViewer)Field<Dictionary<string, FrameworkElement>>(window, "pages")["USB"];
            var startupSurface = (FrameworkElement)window.Content;
            var startupRead = Field<Button>(window, "readButton");
            var startupBounds = startupRead.TransformToAncestor(startupSurface).TransformBounds(new Rect(startupRead.RenderSize));
            double startupWidth = window.Width, startupHeight = Field<Grid>(window, "content").Height;
            bool startupScrollbar = false;
            EventHandler observeStartupLayout = (_, _) => startupScrollbar |= deviceViewport.ComputedVerticalScrollBarVisibility == Visibility.Visible;
            startupSurface.LayoutUpdated += observeStartupLayout;
            Invoke(window, "UpdateUsbDetection", true);
            startupSurface.UpdateLayout();
            Invoke(window, "DisplaySnapshot", data);
            startupSurface.UpdateLayout();
            startupSurface.LayoutUpdated -= observeStartupLayout;
            var readBounds = startupRead.TransformToAncestor(startupSurface).TransformBounds(new Rect(startupRead.RenderSize));
            Check(!startupScrollbar && deviceViewport.ScrollableHeight < .5 && Math.Abs(startupBounds.X - readBounds.X) < .5 && Math.Abs(startupBounds.Y - readBounds.Y) < .5 && Math.Abs(startupBounds.Width - readBounds.Width) < .5 && window.Width == startupWidth && Field<Grid>(window, "content").Height == startupHeight, "initial detection and first snapshot keep the reread button and viewport stable without a transient page scrollbar");
            Check(Field<TextBlock>(window, "supportedInfo").Text.Contains("耳机支持："), "connected headphones display their supported protocols");
            Check(ldac.SelectedIndex == 1 && adaptive.SelectedIndex == 0, "first read preserves option selections after enabling the cards");
            Check(ldacField.Visibility == Visibility.Visible && adaptiveField.Visibility == Visibility.Collapsed && microphone.IsChecked == false && auracast.Visibility == Visibility.Collapsed, "LDAC shows its mode and leaves close-microphone unchecked when game mode is off");
            Check(Field<Button>(window, "readButton").Content.Equals("重新读取设备") && Field<TextBlock>(window, "headerStatus").Text == "●  检测到 USB 设备", "connected device uses the reread action and concise USB status");
            Render(window, "usb-ldac", 1240, 800);
            var codecSelection = Field<ComboBox>(window, "codecs");
            var previewSnapshot = Field<DongleSnapshot>(window, "snapshot");
            var previewLog = Field<TextBox>(window, "logBox").Text;
            codecSelection.SelectedItem = codecSelection.Items.OfType<CodecOption>().Single(codec => codec.Name == "aptX Adaptive");
            Check(adaptiveField.Visibility == Visibility.Visible && ldacField.Visibility == Visibility.Collapsed && auracast.Visibility == Visibility.Collapsed, "selecting Adaptive immediately shows its mode before applying");
            codecSelection.SelectedItem = codecSelection.Items.OfType<CodecOption>().Single(codec => codec.Name == "SBC");
            Check(ldacField.Visibility == Visibility.Collapsed && adaptiveField.Visibility == Visibility.Collapsed && auracast.Visibility == Visibility.Collapsed, "selecting SBC immediately hides codec-specific modes and Auracast");
            codecSelection.SelectedItem = codecSelection.Items.OfType<CodecOption>().Single(codec => codec.Name == "LC3");
            Check(auracast.Visibility == Visibility.Visible && microphone.Visibility == Visibility.Collapsed && ldacField.Visibility == Visibility.Collapsed && adaptiveField.Visibility == Visibility.Collapsed && ReferenceEquals(previewSnapshot, Field<DongleSnapshot>(window, "snapshot")) && Field<TextBox>(window, "logBox").Text == previewLog, "LC3 selection immediately updates controls without changing actual state or accessing USB");
            Invoke(window, "DisplaySnapshot", data with { Adaptive = 0x83, Auracast = 0x83 });
            codecSelection.SelectedItem = codecSelection.Items.OfType<CodecOption>().Single(codec => codec.Name == "aptX Adaptive");
            Check(adaptiveField.Visibility == Visibility.Visible && adaptive.IsEnabled && adaptive.SelectedIndex == -1, "an inactive codec can accept an explicit mode choice without fabricating a current mode");
            codecSelection.SelectedItem = codecSelection.Items.OfType<CodecOption>().Single(codec => codec.Name == "LC3");
            Check(auracast.Visibility == Visibility.Visible && auracast.IsEnabled && auracast.IsChecked == null, "unknown inactive Auracast state stays unset in the LC3 preview");
            Invoke(window, "DisplaySnapshot", data);

            Invoke(window, "DisplaySnapshot", data with { ActiveCodec = "aptX Adaptive", GameMode = true });
            Check(adaptiveField.Visibility == Visibility.Visible && ldacField.Visibility == Visibility.Collapsed && microphone.IsChecked == true, "Adaptive shows its mode; close-microphone checked means game mode on");
            Render(window, "usb-adaptive", 1240, 800);
            Render(window, "usb-minimum", 1060, 740);

            Invoke(window, "DisplaySnapshot", data with { ActiveCodec = "LHDC", HeadphoneCodecs = [new CodecOption(7, "LHDC")], Adaptive = 0x83 });
            Check(!ldac.IsEnabled && !adaptive.IsEnabled && adaptive.SelectedIndex == -1 && ldacField.Visibility == Visibility.Collapsed && adaptiveField.Visibility == Visibility.Collapsed, "LHDC does not display LDAC or unsupported Adaptive settings");
            Render(window, "usb-lhdc", 1240, 800);

            var lc3 = data with { ActiveCodec = "LC3", GameMode = null, Auracast = 1 };
            Invoke(window, "DisplaySnapshot", lc3);
            Check(microphone.Visibility == Visibility.Collapsed && auracast.Visibility == Visibility.Visible && auracast.IsChecked == true && ldacField.Visibility == Visibility.Collapsed && adaptiveField.Visibility == Visibility.Collapsed, "LC3 shows only Auracast and hides microphone and codec modes");
            Render(window, "usb-lc3", 1240, 800);
            Invoke(window, "DisplaySnapshot", lc3 with { Auracast = 0x83 });
            Check(!auracast.IsEnabled, "LC3 rejects unknown Auracast values");
            Invoke(window, "DisplaySnapshot", data with { GameMode = null });
            Check(!microphone.IsEnabled && microphone.IsChecked == null, "unknown game response cannot appear as an enabled microphone");

            var gallery = new StackPanel { Background = Brushes.White, Width = 420 };
            foreach (var codec in DongleController.Codecs)
            {
                Invoke(window, "DisplaySnapshot", data with { ActiveCodec = codec.Name });
                if (logo.Source == null || logo.Height < 30 || logo.Width > 220) throw new Exception("Missing or oversized logo: " + codec.Name);
                gallery.Children.Add(new TextBlock { Text = codec.Name, Margin = new Thickness(16, 10, 0, 6) });
                gallery.Children.Add(new Image { Source = logo.Source, Width = logo.Width, Height = logo.Height, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(16, 0, 0, 10) });
            }
            RenderElement(gallery, "codec-logos", 420, 760);
            Invoke(window, "DisplaySnapshot", data with { ActiveCodec = "未识别" });
            Check(logo.Source == null && logo.Visibility == Visibility.Collapsed, "all eight codec logos load; unknown codec does not reuse a stale logo");

            Invoke(window, "DisplaySnapshot", lc3);
            typeof(MainWindow).GetField("snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, lc3);
            // Hidden fields deliberately contain edits: applying LC3 must still do no USB I/O.
            ldac.SelectedIndex = 0; adaptive.SelectedIndex = 1; microphone.IsChecked = false;
            ((Task)Invoke(window, "ApplySound")!).GetAwaiter().GetResult();
            Check(Field<TextBlock>(window, "status").Text == "设置未变化", "hidden settings are excluded from applying unchanged LC3 settings");
            Invoke(window, "SetStatus", "读取设备完成。 ");
            Check(Field<TextBlock>(window, "status").Text == "读取设备完成", "status text removes trailing punctuation and whitespace");

            Field<List<Button>>(window, "nav")[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var peqPages = Field<Dictionary<string, FrameworkElement>>(window, "pages");
            Check(peqPages.Keys.SequenceEqual(new[] { "USB", "PEQ", "SETTINGS", "ABOUT" }) && peqPages["PEQ"].Visibility == Visibility.Visible, "PEQ navigation is immediately before Settings");
            var peqDefinition = PeqSettings.Parse("Preamp: -5 dB\nFilter: ON PK Fc 1000 Hz Gain 6 dB Q 1\nFilter 2: OFF HSC Fc 8000 Hz Gain -3 dB Q 0.7");
            var bypassedBands = peqDefinition with { BandsEnabled = false };
            Check(Math.Abs(bypassedBands.Response(1000, 48000) + 5) < .001 && bypassedBands.Bands.SequenceEqual(peqDefinition.Bands), "band bypass keeps preamp processing and every original per-band choice");
            var bypassRoundtrip = JsonSerializer.Deserialize<PeqSettings>(JsonSerializer.Serialize(bypassedBands))!;
            Check(!bypassRoundtrip.BandsEnabled && bypassRoundtrip.Bands.SequenceEqual(peqDefinition.Bands) && Math.Abs((bypassRoundtrip with { BandsEnabled = true }).Response(1000, 48000) - 1) < .001, "reopening and reenabling bypassed bands restores the original response");
            Check(JsonSerializer.Deserialize<PeqSettings>("{\"Enabled\":true,\"Preamp\":0,\"Bands\":[]}")!.BandsEnabled, "existing presets default to enabled bands without migration");
            Check(Math.Abs(peqDefinition.Response(1000, 48000) - 1) < 0.001, "PEQ preview uses the independent native response and includes preamp");
            Check(PeqSettings.Parse(peqDefinition.Export()).Bands.SequenceEqual(peqDefinition.Bands), "PEQ text roundtrip preserves disabled bands and precise parameters");
            var wavelet = PeqExport.Wavelet(peqDefinition);
            var waveletPairs = wavelet.Trim()["GraphicEQ: ".Length..].Split("; ");
            var wavelet1032 = double.Parse(waveletPairs.Single(pair => pair.StartsWith("1032 "))[5..], System.Globalization.CultureInfo.InvariantCulture);
            Check(wavelet.StartsWith("GraphicEQ: 20 ") && waveletPairs.Length == 127 && waveletPairs[^1].StartsWith("19871 ") && Math.Abs(wavelet1032 - (peqDefinition with { Preamp = 0 }).Response(1032, 48000)) < .011, "Wavelet export uses its required frequency grid and the filter response");
            using (var easy = JsonDocument.Parse(PeqExport.EasyEffects(peqDefinition)))
            {
                var output = easy.RootElement.GetProperty("output"); var eq = output.GetProperty("equalizer#0");
                Check(output.GetProperty("plugins_order")[0].GetString() == "equalizer#0" && eq.GetProperty("num-bands").GetInt32() == 2 && eq.GetProperty("input-gain").GetDouble() == -5 && eq.GetProperty("left").GetProperty("band0").GetProperty("type").GetString() == "Bell" && eq.GetProperty("right").GetProperty("band1").GetProperty("mute").GetBoolean(), "EasyEffects export preserves preamp, filters, channel settings and disabled bands");
            }
            try { PeqExport.EasyEffects(peqDefinition with { Bands = Enumerable.Repeat(peqDefinition.Bands[0], 33).ToArray() }); throw new Exception("Oversized EasyEffects preset accepted"); } catch (InvalidDataException) { Check(true, "EasyEffects export rejects more than 32 filters without dropping any"); }
            foreach (var rejected in new[] { "Device: all", "Include: other.txt", "Channel: L", "Convolution: ir.wav", "Filter: ON PK Fc 1000 Hz Gain 1 dB Q NaN" })
            {
                try { PeqSettings.Parse(rejected); throw new Exception("Unsupported PEQ accepted"); }
                catch (InvalidDataException) { }
            }
            Check(true, "PEQ import rejects routing, file execution, unsupported effects and nonfinite parameters");
            double[] previewFrequencies = [20, 31, 100, 1000, 11547, 16000, 19999];
            foreach (var type in PeqSettings.Types)
            {
                var preset = new PeqSettings(true, -3, [new(true, type, 2000, type is "PK" or "LSC" or "HSC" ? 6 : 0, 1), new(false, "PK", 100, 10, 1)]);
                Check(preset.ResponseCurve(previewFrequencies, 48000).Zip(previewFrequencies).All(pair => Math.Abs(pair.First - preset.Response(pair.Second, 48000)) < 1e-9), "batched preview matches the existing single-point DSP for " + type);
            }
            Check((peqDefinition with { Enabled = false }).ResponseCurve(previewFrequencies, 48000).All(db => db == 0), "batched preview preserves bypass");
            foreach (var type in PeqSettings.Types)
            foreach (var (center, q, rate) in new[] { (31d, .1, 48000d), (31d, 30d, 384000d), (1000d, 30d, 48000d), (20000d, 30d, 48000d), (3900d, .7, 8000d) })
            {
                var preset = new PeqSettings(true, -3, [new(true, type, center, type is "PK" or "LSC" or "HSC" ? 6 : 0, q)]);
                var frequencies = Enumerable.Range(0, 121).Select(i => 20 * Math.Pow(Math.Min(20000, rate * .49) / 20, i / 120d)).Append(center).ToArray();
                var response = preset.ResponseCurve(frequencies, rate);
                for (int i = 0; i < frequencies.Length; i++)
                {
                    double error = Math.Abs(Math.Max(-60, response[i]) - Math.Max(-60, preset.Response(frequencies[i], rate)));
                    if (!double.IsFinite(error) || error > .0001) throw new Exception($"Preview magnitude differs from DSP: {type}/{center}/{q}/{rate}: {error}");
                }
            }
            Check(true, "fast real-valued preview matches the original complex DSP through high-Q centers, notches and 8-384 kHz rates within 0.0001 dB");
            try { peqDefinition.ResponseCurve([24000], 48000); throw new Exception("Invalid preview frequency accepted"); } catch (ArgumentException) { Check(true, "batched preview rejects frequencies at Nyquist"); }
            var atomicConfiguration = Path.Combine(AppPaths.DataDirectory, "atomic-configuration.bin"); File.WriteAllBytes(atomicConfiguration, [1]);
            using (var reader = new FileStream(atomicConfiguration, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                PeqInstallation.WriteAtomically(atomicConfiguration, [2, 3]);
                Check(reader.ReadByte() == 1 && File.ReadAllBytes(atomicConfiguration).SequenceEqual(new byte[] { 2, 3 }) && !File.Exists(atomicConfiguration + ".tmp"), "PEQ configuration replacement succeeds while the audio engine has the file open and leaves that reader on the previous contents");
            }
            File.Delete(atomicConfiguration);
            Invoke(window, "SetPeq", peqDefinition);
            Render(window, "peq", 1240, 800);
            var allBandsToggle = Field<CheckBox>(window, "peqBandsEnabled");
            var bandsToggleSize = allBandsToggle.LayoutTransform.TransformBounds(new Rect(allBandsToggle.RenderSize));
            Check(bandsToggleSize.Width is >= 31 and <= 33 && bandsToggleSize.Height is >= 18 and <= 20, "the filter heading switch fits beside its label at approximately 32 by 19 DIP");
            // The offscreen render arranges a fixed size; settle back to the window's own layout before sampling.
            SettleTransitions(); Invoke(window, "DrawPeq");
            var originalCurve = Field<PeqCurveVisual>(window, "peqResponseLine").Points.ToArray();
            var bandChoices = Descendants<CheckBox>(Field<ScrollViewer>(window, "peqTable")).ToArray();
            var originalChoices = bandChoices.Select(box => box.IsChecked).ToArray();
            allBandsToggle.IsChecked = false; allBandsToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            SettleTransitions();
            Invoke(window, "DrawPeq");
            var bypassCurve = Field<PeqCurveVisual>(window, "peqResponseLine");
            var bypassDrawing = (DrawingVisual)VisualTreeHelper.GetChild(bypassCurve, 0);
            var bypassStroke = ((GeometryDrawing)bypassDrawing.Drawing.Children.Single()).Pen.Brush;
            var bypassPoints = bypassCurve.Points.ToArray();
            double curveDifference = bypassPoints.Length == originalCurve.Length ? bypassPoints.Zip(originalCurve).Max(pair => Math.Max(Math.Abs(pair.First.X - pair.Second.X), Math.Abs(pair.First.Y - pair.Second.Y))) : double.PositiveInfinity;
            Check(curveDifference == 0, $"bypassing filters retains the exact curve geometry ({originalCurve.Length}/{bypassPoints.Length} points, largest difference {curveDifference})");
            Check(((SolidColorBrush)bypassStroke).Color == ((SolidColorBrush)Field<Brush>(window, "muted")).Color, $"bypassed filters render the curve grey (stroke {((SolidColorBrush)bypassStroke).Color})");
            Render(window, "peq-bands-paused", 1240, 800);
            var bypassedUi = (PeqSettings)Invoke(window, "GetPeq")!;
            Check(bandChoices.All(box => !box.IsEnabled) && bandChoices.Select(box => box.IsChecked).SequenceEqual(originalChoices) && !bypassedUi.BandsEnabled && bypassedUi.Preamp == peqDefinition.Preamp, "filter master switch disables all row toggles without changing their choices or preamp");
            Check(bandChoices.All(box => ((SolidColorBrush)((Border)box.Template.FindName("CheckSurface", box)).Background).Color == Color.FromRgb(0x9B, 0xA1, 0xAB)), "bypassed filter checkboxes use a grey surface while retaining their checkmarks");
            allBandsToggle.IsChecked = true; allBandsToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Invoke(window, "DrawPeq");
            var restoredDrawing = (DrawingVisual)VisualTreeHelper.GetChild(Field<PeqCurveVisual>(window, "peqResponseLine"), 0);
            Check(((SolidColorBrush)((GeometryDrawing)restoredDrawing.Drawing.Children.Single()).Pen.Brush).Color == ((SolidColorBrush)Field<Brush>(window, "readAccent")).Color, "reenabling filters restores the curve's active color");
            Check(bandChoices.All(box => box.IsEnabled) && bandChoices.Select(box => box.IsChecked).SequenceEqual(originalChoices), "reenabling all bands preserves the individual selection pattern");
            VerifyPeqHeaderAlignment(window);
            Check(true, "PEQ headings align with editor content and the checkbox after narrowing the controls");
            Check(Field<Canvas>(window, "peqCurve").Children.OfType<PeqCurveVisual>().Count() == 1, "PEQ frequency response renders offscreen");
            var curve = Field<Canvas>(window, "peqCurve");
            double initialKhz = (double)Invoke(window, "PeqX", 1000d)!;
            Invoke(window, "SetPeqFrequencyScale", 1d);
            double rightLimitKhz = (double)Invoke(window, "PeqX", 1000d)!;
            Invoke(window, "SetPeqFrequencyScale", 3d);
            double leftLimitKhz = (double)Invoke(window, "PeqX", 1000d)!;
            Check(Math.Abs(initialKhz - (rightLimitKhz + leftLimitKhz) / 2) < .001, "PEQ opens halfway between the two frequency-axis drag limits");
            Invoke(window, "SetPeqFrequencyScale", 2d);
            var reset = Field<Button>(window, "peqReset");
            var originalPreset = (PeqSettings)Invoke(window, "GetPeq")!;
            reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var resetPreset = (PeqSettings)Invoke(window, "GetPeq")!;
            Check(resetPreset.Bands is [{ Enabled: true, Type: "PK", Frequency: 1000, Gain: 0, Q: 1 }] && resetPreset.Preamp == originalPreset.Preamp && resetPreset.Enabled == originalPreset.Enabled && Equals(reset.Content, "撤销重置"), "reset replaces all filters with one default band and offers undo");
            Invoke(window, "SetPeqFrequencyScale", 2d);
            Check(Equals(reset.Content, "撤销重置"), "view-only axis changes keep reset undo available");
            reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var restoredPreset = (PeqSettings)Invoke(window, "GetPeq")!;
            Check(restoredPreset.Bands.SequenceEqual(originalPreset.Bands) && restoredPreset.Preamp == originalPreset.Preamp && Equals(reset.Content, "重置"), "undo restores every previous band and parameter");
            reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Field<ScrollViewer>(window, "peqTable").UpdateLayout();
            Descendants<TextBox>(Field<ScrollViewer>(window, "peqTable")).Skip(1).First().Text = "-";
            Check(Equals(reset.Content, "重置"), "starting an incomplete parameter edit also expires reset undo");
            Invoke(window, "SetPeq", peqDefinition); Invoke(window, "SetPeqFrequencyScale", 1d);
            Check(((SolidColorBrush)reset.Background).Color == Color.FromRgb(0xCA, 0x26, 0x3D), "reset uses the shared red action colour");
            var axisLabels = curve.Children.OfType<TextBlock>().Select(label => label.Text).ToArray();
            Check(axisLabels.Contains("-3") && axisLabels.Contains("6") && axisLabels.Contains("0") && axisLabels.All(label => !label.StartsWith('+')), "PEQ preview uses independent three-decibel axis limits and unsigned positive labels");
            var points = curve.Children.OfType<PeqCurveVisual>().Single().Points.ToArray();
            Invoke(window, "SetPeq", peqDefinition with { Preamp = 7 });
            Check(points.SequenceEqual(curve.Children.OfType<PeqCurveVisual>().Single().Points), "PEQ curve is independent of preamp");
            var peqButtons = Descendants<Button>(peqPages["PEQ"]).ToArray();
            var peqActions = new[] { "peqAdd", "peqApply", "peqImport", "peqExport", "peqInstall" }.Select(name => Field<Button>(window, name)).ToArray();
            Check(peqActions.SequenceEqual(peqButtons.Where(peqActions.Contains)) && Field<Border>(window, "peqApplySlot").Width == 0 && Field<bool>(window, "peqLive"), "PEQ defaults to live mode, hides the apply action and places export after import");
            Invoke(window, "ShowPeqExport");
            var exportOverlay = (Grid)typeof(MainWindow).GetField("PeqExportOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Check(exportOverlay.Visibility == Visibility.Visible && !Field<Grid>(window, "Root").IsEnabled && new[] { "Equalizer APO", "Wavelet", "EasyEffects" }.All(name => Descendants<Button>(exportOverlay).Any(button => Equals(button.Content, name))), "export opens a modal overlay with all three target formats");
            var dialogRoot = Field<Grid>(window, "Root");
            Check(exportOverlay.Opacity == 0 && dialogRoot.Effect is BlurEffect { Radius: 0 }, "export starts transparent with an unblurred background");
            Check(ObserveTransition(400, () => exportOverlay.Opacity > 0 && exportOverlay.Opacity < 1 && dialogRoot.Effect is BlurEffect { Radius: > 0 and < 10 }), "export and its backdrop fade while the background gradually blurs");
            SettleTransitions(245);
            Check(exportOverlay.Opacity == 1 && dialogRoot.Effect is BlurEffect { Radius: 10 }, "export reaches full opacity and the existing blur strength");
            Invoke(window, "DismissPeqExport");
            Check(!exportOverlay.IsEnabled && !exportOverlay.IsHitTestVisible && exportOverlay.Visibility == Visibility.Visible && !dialogRoot.IsEnabled, "closing export immediately blocks dialog input and keeps the page modal during the fade");
            Check(ObserveTransition(400, () => exportOverlay.Opacity > 0 && exportOverlay.Opacity < 1 && dialogRoot.Effect is BlurEffect { Radius: > 0 and < 10 }), "closing export fades out and gradually clears the background");
            double closingOpacity = exportOverlay.Opacity;
            double closingBlur = ((BlurEffect)dialogRoot.Effect).Radius;
            Invoke(window, "ShowPeqExport");
            Check(Math.Abs(exportOverlay.Opacity - closingOpacity) < .01 && Math.Abs(((BlurEffect)dialogRoot.Effect).Radius - closingBlur) < .01, "reopening export continues from its current opacity and blur");
            SettleTransitions();
            Check(exportOverlay.Visibility == Visibility.Visible && exportOverlay.IsHitTestVisible && !dialogRoot.IsEnabled && dialogRoot.Effect is BlurEffect { Radius: 10 }, "an interrupted close cannot hide the reopened export or unlock the page");
            Invoke(window, "DismissPeqExport"); SettleTransitions();
            Check(exportOverlay.Visibility == Visibility.Collapsed && dialogRoot.Effect == null && dialogRoot.IsEnabled, "export close finishes by removing blur and restoring page input");
            Check(Field<TextBlock>(window, "title").Text == "参数均衡器" && Field<CheckBox>(window, "peqEnabled").Visibility == Visibility.Visible && Field<StackPanel>(window, "peqHeaderStatus").Visibility == Visibility.Visible && Field<TextBlock>(window, "headerStatus").Visibility == Visibility.Collapsed, "PEQ header shows its title, switch and endpoint status");
            Check(curve.Height == 162 && curve.ReadLocalValue(FrameworkElement.CursorProperty) == DependencyProperty.UnsetValue && curve.Children.OfType<System.Windows.Shapes.Line>().Count(line => line.Y1 == line.Y2) == 7 && new[] { "-2", "-1", "2", "4" }.All(axisLabels.Contains) && new[] { "20", "30", "60", "100", "250", "500", "1k", "2k", "4k", "6k", "8k", "10k", "12k", "16k" }.All(axisLabels.Contains), "PEQ plot keeps the default pointer and shows the requested frequency labels including 10k");
            var zeroLine = curve.Children.OfType<System.Windows.Shapes.Line>().Where(line => line.Y1 == line.Y2).OrderBy(line => Math.Abs(line.Y1 - points[0].Y)).First();
            Check(zeroLine.StrokeThickness > 1 && ((SolidColorBrush)zeroLine.Stroke).Color == Color.FromRgb(0x9D, 0xAF, 0xC5), "zero-decibel reference has a stronger slightly thicker stroke");
            var table = Field<ScrollViewer>(window, "peqTable");
            Render(window, "peq", 1240, 800);
            Check(Descendants<TextBox>(table).All(box => box.Height == 29.7 && Math.Abs(box.ActualHeight - 29.7) < 1 && box.Margin == new Thickness(15, 0, 15, 0)) && Descendants<ComboBox>(table).All(box => box.Margin == new Thickness(15, 0, 15, 0)) && Field<TextBox>(window, "peqPreamp").Height == 36 && table.ActualHeight > 136, "PEQ editors exchange twelve more DIP of width for matching side spacing while retaining their columns");
            var warning = Field<TextBlock>(window, "peqWarning");
            var plotContent = (Grid)warning.Parent;
            var warningBounds = warning.TransformToAncestor(plotContent).TransformBounds(new Rect(warning.RenderSize));
            Check(ReferenceEquals(curve.Parent, plotContent) && warningBounds.Left >= 30 && warningBounds.Top >= 8 && warningBounds.Right < curve.ActualWidth && warningBounds.Bottom < curve.ActualHeight && !warning.IsHitTestVisible, "clipping warning overlays the upper-left plot area without intercepting chart input");
            var warningLayoutRoot = (FrameworkElement)window.Content;
            var plotBeforeWarning = curve.TransformToAncestor(warningLayoutRoot).TransformBounds(new Rect(curve.RenderSize));
            var tableBeforeWarning = table.TransformToAncestor(warningLayoutRoot).TransformBounds(new Rect(table.RenderSize));
            warning.Visibility = Visibility.Collapsed; warningLayoutRoot.UpdateLayout();
            Check(plotBeforeWarning == curve.TransformToAncestor(warningLayoutRoot).TransformBounds(new Rect(curve.RenderSize)) && tableBeforeWarning == table.TransformToAncestor(warningLayoutRoot).TransformBounds(new Rect(table.RenderSize)), "showing or hiding the overlay warning does not move or resize the chart or filter list");
            warning.Visibility = Visibility.Visible; warningLayoutRoot.UpdateLayout();
            var preampInput = (Grid)Field<TextBox>(window, "peqPreamp").Parent;
            var preampUnit = preampInput.Children.OfType<TextBlock>().Single();
            var unitInput = Field<TextBox>(window, "peqPreamp");
            Check(preampUnit.Text == "dB" && !preampUnit.IsHitTestVisible && preampUnit.HorizontalAlignment == HorizontalAlignment.Right && !unitInput.Text.Contains("dB") && unitInput.Padding.Right == 8 && unitInput.Width == 64, "shorter preamp input retains its full text viewport under the decorative unit");
            DependencyObject? unitHit = VisualTreeHelper.HitTest(unitInput, new Point(unitInput.ActualWidth - 12, unitInput.ActualHeight / 2))?.VisualHit;
            while (unitHit != null && unitHit != unitInput) unitHit = VisualTreeHelper.GetParent(unitHit);
            Check(unitHit == unitInput, "the decorative unit does not intercept the input hit area");
            var eqToggle = Field<CheckBox>(window, "peqEnabled");
            eqToggle.ApplyTemplate();
            SettleTransitions();
            Check(((SolidColorBrush)((Border)eqToggle.Template.FindName("Track", eqToggle)).Background).Color == Color.FromRgb(0x20, 0x77, 0xCE), "sliding switches use the blue enabled background");
            var bandCheck = Descendants<CheckBox>(table).First(); bandCheck.ApplyTemplate();
            Check(((SolidColorBrush)((Border)bandCheck.Template.FindName("CheckSurface", bandCheck)).Background).Color == Color.FromRgb(0x20, 0x77, 0xCE), "enabled checkboxes use the same blue background");
            Check(((SolidColorBrush)Field<Button>(window, "peqAdd").Background).Color == Color.FromRgb(0x20, 0x77, 0xCE), "add-filter action uses the shared blue accent");
            double DefaultOneKhzX() => curve.Children.OfType<TextBlock>().Single(label => label.Text == "1k").RenderTransform.Value.OffsetX;
            double defaultKhz = DefaultOneKhzX();
            Invoke(window, "SetPeqFrequencyScale", 3d);
            Check(DefaultOneKhzX() < defaultKhz && curve.Children.OfType<PeqCurveVisual>().Single().Points.All(point => double.IsFinite(point.X)), "dragging toward the high-frequency limit expands high-frequency space with a valid curve");
            // Use gridline coordinates so font measurement cannot hide unequal frequency spacing.
            var frequencyLines = curve.Children.OfType<System.Windows.Shapes.Line>().Where(line => line.X1 == line.X2).ToArray();
            double[] gaps = Enumerable.Range(8, 2).Select(i => (frequencyLines[i + 1].X1 + frequencyLines[i + 1].RenderTransform.Value.OffsetX) - (frequencyLines[i].X1 + frequencyLines[i].RenderTransform.Value.OffsetX)).ToArray();
            Check(gaps.Max() - gaps.Min() < .001 && curve.Children.OfType<TextBlock>().Any(t => t.Text == "10k"), "expanded frequency axis keeps 4-6k and 6-8k equal in width");
            double chartRight = curve.Children.OfType<PeqCurveVisual>().Single().Points[^1].X;
            double linearHzWidth = (chartRight - (frequencyLines[0].X1 + frequencyLines[0].RenderTransform.Value.OffsetX)) / 19980;
            Check((frequencyLines[12].X1 + frequencyLines[12].RenderTransform.Value.OffsetX) - (frequencyLines[11].X1 + frequencyLines[11].RenderTransform.Value.OffsetX) > (frequencyLines[13].X1 + frequencyLines[13].RenderTransform.Value.OffsetX) - (frequencyLines[12].X1 + frequencyLines[12].RenderTransform.Value.OffsetX) && (frequencyLines[13].X1 + frequencyLines[13].RenderTransform.Value.OffsetX) - (frequencyLines[12].X1 + frequencyLines[12].RenderTransform.Value.OffsetX) < 4000 * linearHzWidth && chartRight - (frequencyLines[13].X1 + frequencyLines[13].RenderTransform.Value.OffsetX) < 4000 * linearHzWidth && frequencyLines.Zip(frequencyLines.Skip(1)).All(pair => pair.First.X1 + pair.First.RenderTransform.Value.OffsetX < pair.Second.X1 + pair.Second.RenderTransform.Value.OffsetX), "expanded axis gives more width to 10-12k than 12-16k and retains frequency order");
            Render(window, "peq-expanded-axis", 1240, 800);
            Invoke(window, "SetPeqFrequencyScale", 20d); Check(Field<double>(window, "peqFrequencyScale") == 3, "frequency-axis expansion has a finite upper limit");
            Invoke(window, "SetPeqFrequencyScale", -1d); Check(Field<double>(window, "peqFrequencyScale") == 1 && DefaultOneKhzX() == defaultKhz, "frequency-axis compression stops at the original logarithmic distribution");
            var retainedElements = curve.Children.Cast<UIElement>().ToArray();
            var scaleWatch = Stopwatch.StartNew();
            for (int i = 0; i < 120; i++)
            {
                Invoke(window, "SetPeqFrequencyScale", 1 + i / 119d * 2);
                double X(double frequency) => (double)Invoke(window, "PeqX", frequency)!;
                if (X(12000) - X(10000) <= X(16000) - X(12000)) throw new Exception("High-frequency width redistribution failed");
                var samples = curve.Children.OfType<PeqCurveVisual>().Single().Points;
                if (!samples.Zip(samples.Skip(1)).All(pair => pair.First.X < pair.Second.X)) throw new Exception("Frequency axis is not monotonic");
            }
            scaleWatch.Stop();
            Check(retainedElements.SequenceEqual(curve.Children.Cast<UIElement>()), "axis dragging reuses the existing curve, lines and labels at every scale");
            Console.WriteLine($"PERF axis: {scaleWatch.Elapsed.TotalMilliseconds / 120:F2} ms/update (120 updates)");
            curve.UpdateLayout();
            Invoke(window, "SetPeqFrequencyScale", 2.5d);
            Check(curve.IsMeasureValid && curve.Children.OfType<PeqCurveVisual>().Single().IsMeasureValid, "axis motion changes drawing data and transforms without invalidating chart layout");
            double hoverX = (double)Invoke(window, "PeqX", 11547d)!;
            typeof(MainWindow).GetField("peqHoverPosition", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new Point(hoverX, 50));
            Invoke(window, "UpdatePeqHover");
            Check(Field<TextBlock>(window, "peqHoverText").Text == "11547" && Field<Border>(window, "peqHoverTag").Visibility == Visibility.Visible && Math.Abs(Field<TranslateTransform>(window, "peqDotPosition").X + 3 - hoverX) < .001, "hover follows the curve and overlays the exact unshortened frequency");
            var hoverTag = Field<Border>(window, "peqHoverTag"); var hoverDot = Field<System.Windows.Shapes.Ellipse>(window, "peqHoverDot");
            var tagShadow = (DropShadowEffect)hoverTag.Effect; var dotShadow = (DropShadowEffect)hoverDot.Effect;
            Check(hoverTag.BorderThickness.Left == hoverDot.StrokeThickness && hoverTag.BorderBrush == Brushes.Black && tagShadow.BlurRadius == dotShadow.BlurRadius && tagShadow.ShadowDepth == dotShadow.ShadowDepth && tagShadow.Opacity == dotShadow.Opacity, "frequency tag uses the dot's fine black border and light shadow");
            var fadingAxisLabel = curve.Children.OfType<TextBlock>().Single(label => label.Text == "1k");
            SettleTransitions(190);
            typeof(MainWindow).GetField("peqHoverPosition", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new Point((double)Invoke(window, "PeqX", 1000d)!, 50));
            Invoke(window, "UpdatePeqHover");
            Check((double)fadingAxisLabel.GetAnimationBaseValue(UIElement.OpacityProperty) == 0 && fadingAxisLabel.Opacity > 0, "the frequency label under the pointer tag starts fading instead of disappearing immediately");
            Check(ObserveTransition(300, () => fadingAxisLabel.Opacity is > 0 and < 1), "covered axis text passes through an intermediate opacity");
            double axisOpacityBeforeRepeat = fadingAxisLabel.Opacity; Invoke(window, "UpdatePeqHover");
            Check(fadingAxisLabel.Opacity <= axisOpacityBeforeRepeat + .001, "repeated hover updates do not restart the axis label fade");
            SettleTransitions(205);
            typeof(MainWindow).GetField("peqHoverPosition", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, null); Invoke(window, "UpdatePeqHover");
            Check((double)fadingAxisLabel.GetAnimationBaseValue(UIElement.OpacityProperty) == 1 && fadingAxisLabel.Opacity < 1, "moving the tag away restores the axis label with a fade-in");
            SettleTransitions(190);
            Check(fadingAxisLabel.Opacity == 1, "uncovered axis text returns to full opacity");
            var gainLabels = curve.Children.OfType<TextBlock>().Where(label => double.TryParse(label.Text, out _) && label.RenderTransform is not TranslateTransform && Canvas.GetLeft(label) == 0).ToArray();
            Check(gainLabels.Length == 7 && gainLabels.All(label => label.TextAlignment == TextAlignment.Right && Math.Abs(label.Width + 5 - Field<double>(window, "peqPlotLeft")) < .01), "gain labels are right aligned five DIP from the vertical axis");
            Render(window, "peq-hover", 1240, 800);
            typeof(MainWindow).GetField("peqHoverPosition", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, null); Invoke(window, "UpdatePeqHover");
            Check(Field<Border>(window, "peqHoverTag").Visibility == Visibility.Collapsed && Field<System.Windows.Shapes.Ellipse>(window, "peqHoverDot").Visibility == Visibility.Collapsed, "leaving the plot hides both hover elements");
            foreach (double edge in new[] { 1d, 3d })
            {
                Invoke(window, "SetPeqFrequencyScale", edge);
                string beforeMiddlePreset = ((PeqSettings)Invoke(window, "GetPeq")!).Export();
                curve.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = Mouse.MouseDownEvent });
                Check(!Field<bool>(window, "peqFrequencyAnimating"), "middle-click leaves the axis position unchanged");
                curve.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = Mouse.MouseDownEvent });
                Check(Field<double>(window, "peqFrequencyScale") == edge && Field<bool>(window, "peqFrequencyAnimating"), "right-click reset starts from the displayed axis position at limit " + edge);
                typeof(MainWindow).GetField("peqFrequencyStart", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * .09));
                Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
                double middleScale = Field<double>(window, "peqFrequencyScale");
                Check(edge == 1 ? middleScale is > 1 and < 2 : middleScale is > 2 and < 3, "right-click reset passes through an intermediate axis position");
                typeof(MainWindow).GetField("peqFrequencyStart", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, Stopwatch.GetTimestamp() - Stopwatch.Frequency);
                Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
                Check(Field<double>(window, "peqFrequencyScale") == 2 && !Field<bool>(window, "peqFrequencyAnimating") && beforeMiddlePreset == ((PeqSettings)Invoke(window, "GetPeq")!).Export(), "right-click transition ends at the exact center without changing filters");
            }
            Invoke(window, "SetPeq", peqDefinition);
            table.UpdateLayout();
            var animatedGain = Descendants<TextBox>(table).Skip(1).First();
            double Peak() => Field<(double Frequency, double Db)[]>(window, "peqSamples").Single(sample => sample.Frequency == 1000).Db;
            double originalPeak = Peak();
            animatedGain.Text = "12"; Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
            Check(Math.Abs(Peak() - originalPeak) < .001 && Field<bool>(window, "peqResponseAnimating") && Field<double>(window, "peqPlotMaximum") == 6, "typed gain changes preserve the displayed response and range at animation start");
            typeof(MainWindow).GetField("peqResponseStart", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * .09));
            Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
            Check(Peak() is > 6 and < 12 && Field<double>(window, "peqPlotMaximum") is > 6 and < 12, "typed gain changes interpolate both the response and vertical range");
            double retargetPeak = Peak(); animatedGain.Text = "-3"; Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
            Check(Math.Abs(Peak() - retargetPeak) < .001, "a new typed value retargets the transition from its current displayed response");
            typeof(MainWindow).GetField("peqResponseStart", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, Stopwatch.GetTimestamp() - Stopwatch.Frequency);
            Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
            Check(Math.Abs(Peak() + 3) < .001 && !Field<bool>(window, "peqResponseAnimating"), "response transition finishes at the exact newly entered gain");
            int bandsBeforeNotice = ((PeqSettings)Invoke(window, "GetPeq")!).Bands.Length;
            Field<Button>(window, "peqAdd").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var peqNotice = (Border)window.FindName("PeqNotice");
            var peqNoticeText = (TextBlock)window.FindName("PeqNoticeText");
            Check(((PeqSettings)Invoke(window, "GetPeq")!).Bands.Length == bandsBeforeNotice + 1 && peqNotice.Visibility == Visibility.Visible && peqNoticeText.Text == UiLanguage.Current["已添加滤波器"], "adding a filter shows the corresponding nonmodal feedback");
            var addedRow = Field<ItemsControl>(window, "peqRows").Items.Cast<Grid>().Last();
            addedRow.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(((PeqSettings)Invoke(window, "GetPeq")!).Bands.Length == bandsBeforeNotice && peqNoticeText.Text == UiLanguage.Current["已删除滤波器"], "removing a filter replaces the previous notice with the corresponding result");
            SettleTransitions(); Render(window, "peq-notice", 1240, 800);
            var noticeContainer = (FrameworkElement)window.FindName("FrameContent");
            var noticeBounds = peqNotice.TransformToAncestor(noticeContainer).TransformBounds(new Rect(peqNotice.RenderSize));
            Check(Math.Abs(noticeBounds.Left + noticeBounds.Width / 2 - noticeContainer.ActualWidth / 2) < 1 && noticeBounds.Top >= 60 && !peqNotice.IsHitTestVisible,
                $"PEQ operation feedback is centered near the top and does not intercept clicks (notice {noticeBounds}, container width {noticeContainer.ActualWidth}, {peqNotice.Visibility}, \"{peqNoticeText.Text}\")");
            SettleTransitions(2700);
            Check(peqNotice.Visibility == Visibility.Collapsed && !Field<DispatcherTimer>(window, "peqNoticeTimer").IsEnabled, "PEQ operation feedback fades away and stops its timer");
            Invoke(window, "PeqUserAction", (Action)(() => throw new IOException("simulated PEQ action failure")));
            Check(peqNotice.Visibility == Visibility.Visible && peqNoticeText.Text == UiLanguage.Current["PEQ 操作失败"] && ((SolidColorBrush)peqNoticeText.Foreground).Color == Color.FromRgb(0xCE, 0x20, 0x38), "a user action failure shows a short error notice while preserving the detailed log");
            Invoke(window, "MonitorPeqPage", false);
            Check(peqNotice.Visibility == Visibility.Collapsed && !Field<DispatcherTimer>(window, "peqNoticeTimer").IsEnabled, "leaving PEQ dismisses operation feedback immediately");
            var manyBands = peqDefinition with { Bands = Enumerable.Repeat(peqDefinition.Bands[0], 64).ToArray() };
            Invoke(window, "SetPeq", manyBands);
            Render(window, "peq-64-performance", 1240, 800);
            VerifyPeqHeaderAlignment(window);
            Check(true, "PEQ headings follow the editor columns when the scrollbar reduces the viewport width");
            var editBoxes = Descendants<TextBox>(Field<ScrollViewer>(window, "peqTable")).ToArray();
            Check(editBoxes.Length < 64 * 3 && ((PeqSettings)Invoke(window, "GetPeq")!).Bands.Length == 64, "only visible parameter rows are laid out while all 64 bands remain in the active settings");
            var beforeFrame = curve.Children.OfType<PeqCurveVisual>().Single().Points;
            editBoxes[1].Text = "5"; editBoxes[1].Text = "4"; editBoxes[1].Text = "3";
            Check(ReferenceEquals(beforeFrame, curve.Children.OfType<PeqCurveVisual>().Single().Points) && Field<bool>(window, "peqDrawPending"), "multiple parameter moves coalesce before the next drawing frame");
            Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
            Check(!ReferenceEquals(beforeFrame, curve.Children.OfType<PeqCurveVisual>().Single().Points) && Field<bool>(window, "peqDrawPending") && Field<bool>(window, "peqResponseAnimating"), "the drawing frame begins a transition toward the latest coalesced parameter value");
            var editWatch = Stopwatch.StartNew();
            for (int i = 0; i < 60; i++) { editBoxes[1].Text = (3 + i / 100d).ToString(CultureInfo.CurrentCulture); Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty); }
            editWatch.Stop(); Console.WriteLine($"PERF 64-band parameter edit: {editWatch.Elapsed.TotalMilliseconds / 60:F2} ms/frame (60 frames)");
            var chartBitmap = new RenderTargetBitmap((int)Math.Ceiling(curve.ActualWidth), (int)Math.Ceiling(curve.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            var rasterWatch = Stopwatch.StartNew();
            double responseTime = 0, layoutTime = 0, rasterTime = 0;
            for (int i = 0; i < 24; i++)
            {
                long phase = Stopwatch.GetTimestamp();
                editBoxes[1].Text = (4 + i / 100d).ToString(CultureInfo.CurrentCulture); Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
                responseTime += Stopwatch.GetElapsedTime(phase).TotalMilliseconds; phase = Stopwatch.GetTimestamp();
                ((FrameworkElement)window.Content).UpdateLayout();
                layoutTime += Stopwatch.GetElapsedTime(phase).TotalMilliseconds; phase = Stopwatch.GetTimestamp();
                chartBitmap.Clear(); chartBitmap.Render(curve);
                rasterTime += Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
            }
            rasterWatch.Stop(); Console.WriteLine($"PERF 64-band update + layout + chart raster: {rasterWatch.Elapsed.TotalMilliseconds / 24:F2} ms/frame (24 frames)");
            Console.WriteLine($"PERF phase update/layout/raster: {responseTime / 24:F2}/{layoutTime / 24:F2}/{rasterTime / 24:F2} ms/frame");
            SettleTransitions(300);
            var hoverCurve = curve.Children.OfType<PeqCurveVisual>().Single().Points;
            var hoverWatch = Stopwatch.StartNew();
            for (int i = 0; i < 60; i++)
            {
                typeof(MainWindow).GetField("peqHoverPosition", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new Point(40 + i * 8, 50));
                Invoke(window, "RequestPeqDraw", false, false); Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
                curve.UpdateLayout(); chartBitmap.Clear(); chartBitmap.Render(curve);
            }
            hoverWatch.Stop(); Console.WriteLine($"PERF hover + layout + chart raster: {hoverWatch.Elapsed.TotalMilliseconds / 60:F2} ms/frame (60 frames)");
            Check(ReferenceEquals(hoverCurve, curve.Children.OfType<PeqCurveVisual>().Single().Points), "cursor and label frames do not rebuild or recalculate the response curve");
            typeof(MainWindow).GetField("peqHoverPosition", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, null); Invoke(window, "UpdatePeqHover");
            Invoke(window, "SetPeq", peqDefinition);
            Field<ScrollViewer>(window, "peqTable").UpdateLayout();
            typeof(MainWindow).GetField("peqValueDragging", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
            Descendants<TextBox>(Field<ScrollViewer>(window, "peqTable")).Skip(1).First().Text = "7";
            Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
            double animatedMaximum = Field<double>(window, "peqPlotMaximum");
            Check(animatedMaximum is > 6 and < 9 && Field<bool>(window, "peqVerticalAnimating"), "crossing an axis limit during a parameter drag interpolates instead of jumping by three dB");
            typeof(MainWindow).GetField("peqValueDragging", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
            SettleTransitions(400);
            Check(Math.Abs(Field<double>(window, "peqPlotMaximum") - 9) < .003 && !Field<bool>(window, "peqVerticalAnimating"), "vertical range animation settles at the correct limit after release");
            Invoke(window, "SetPeq", peqDefinition with { Preamp = 7 }); Invoke(window, "SetPeqFrequencyScale", 1d);
            var peqValueDragMethod = typeof(MainWindow).GetMethod("PeqDraggedValue", BindingFlags.NonPublic | BindingFlags.Static)!;
            double Drag(string parameter, double initial, double delta) => (double)peqValueDragMethod.Invoke(null, [parameter, initial, delta])!;
            Check(Drag("Frequency", 1000, 120) > 1000 && Drag("Frequency", 1000, -120) < 1000 && Drag("Gain", 0, 20) > 0 && Drag("Q", 1, -20) < 1 && Drag("Frequency", 20000, 1000) == 20000 && Drag("Gain", -20, -1000) == -20 && Drag("Q", .1, -1000) == .1, "numeric drags respect the editor limits");
            var wheelTable = Field<ScrollViewer>(window, "peqTable"); wheelTable.UpdateLayout();
            var wheelBoxes = Descendants<TextBox>(wheelTable).Take(3).ToArray();
            double tableOffset = wheelTable.VerticalOffset;
            foreach (var (index, parameter, initial, minimum, maximum) in new[] { (0, "Frequency", 1000d, 10d, 20000d), (1, "Gain", 0d, -20d, 20d), (2, "Q", 1d, .1, 30d) })
            {
                var box = wheelBoxes[index];
                foreach (int delta in new[] { 120, -120, 240, -240 })
                {
                    box.Text = initial.ToString("G", CultureInfo.CurrentCulture);
                    var numericWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                    box.RaiseEvent(numericWheel);
                    Check(numericWheel.Handled && double.Parse(box.Text, CultureInfo.CurrentCulture) == Drag(parameter, initial, delta / 12d), parameter + " wheel follows the drag calculation for delta " + delta);
                }
                box.Text = maximum.ToString("G", CultureInfo.CurrentCulture);
                box.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                bool upperStopped = double.Parse(box.Text, CultureInfo.CurrentCulture) == maximum;
                box.Text = minimum.ToString("G", CultureInfo.CurrentCulture);
                box.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Check(upperStopped && double.Parse(box.Text, CultureInfo.CurrentCulture) == minimum && Field<Adorner>(window, "peqRangeHint").AdornedElement == box, parameter + " wheel stops at both bounds and shows the range hint");
            }
            wheelBoxes[0].Text = "10";
            wheelBoxes[0].RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Check(wheelBoxes[0].Text == "11", "frequency wheel advances even at the lowest editable frequency");
            SettleTransitions(150);
            Check(wheelTable.VerticalOffset == tableOffset, "numeric wheel gestures do not scroll the parameter list");
            Invoke(window, "SetPeq", peqDefinition with { Preamp = 7 });
            var validPeqInput = typeof(MainWindow).GetMethod("ValidPeqInput", BindingFlags.NonPublic | BindingFlags.Static)!;
            bool Valid(string parameter, string text) => (bool)validPeqInput.Invoke(null, [parameter, text])!;
            Check(Valid("Frequency", "20000") && !Valid("Frequency", "20001") && !Valid("Frequency", "1.5") && !Valid("Frequency", "abc") && Valid("Gain", "-20") && !Valid("Gain", "20.1") && Valid("Q", "0.71") && !Valid("Q", "0.711"), "frequency, gain and Q input limits reject invalid typing and paste candidates");
            Check(Valid("Preamp", "10") && Valid("Preamp", "-20") && !Valid("Preamp", "10.1") && !Valid("Preamp", "-20.1"), "preamp typing and paste respect minus 20 through 10 dB");
            VerifyPeqEditing(window, peqDefinition);
            foreach (var value in new[] { -20.1, 10.1 })
            {
                try { (peqDefinition with { Preamp = value }).Validate(); throw new Exception("Out-of-range preamp accepted"); }
                catch (InvalidDataException) { }
            }
            Check(true, "out-of-range imported preamps are rejected without clamping");
            SettleTransitions(190);
            Check(Field<TextBlock>(window, "peqWarning").Visibility == Visibility.Visible && warning.FontSize == Field<Button>(window, "peqAdd").FontSize && warning.Opacity == 1, "clipping warning reaches full opacity and uses the same text size as action buttons");
            Invoke(window, "SetPeq", peqDefinition with { Preamp = -7 });
            Check(warning.Visibility == Visibility.Visible && warning.Opacity > 0, "clipping warning remains visible while its fade-out starts");
            Check(ObserveTransition(300, () => warning.Opacity is > 0 and < 1), "clipping warning fades through an intermediate opacity");
            SettleTransitions(205);
            Check(Field<TextBlock>(window, "peqWarning").Visibility == Visibility.Collapsed, "adequate attenuation hides the clipping warning");
            typeof(MainWindow).GetField("peqEndpoint", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new PeqEndpoint("{00000000-0000-0000-0000-000000000000}", "test", true));
            var preampBox = Field<TextBox>(window, "peqPreamp");
            preampBox.Text = "10"; preampBox.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Check(preampBox.Text == "10", "preamp wheel stops at 10 dB");
            preampBox.Text = "-20"; preampBox.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Check(preampBox.Text == "-20" && preampBox.Margin.Right == 12 && preampBox.Width + preampBox.Margin.Right == 76, "preamp wheel stops at minus 20 dB and uses the shortened input width");
            preampBox.Text = "-7";
            preampBox.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Check(preampBox.Text == "-6", "mouse wheel increments the preamp");
            preampBox.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Check(preampBox.Text == "-7", "mouse wheel decrements the preamp");
            preampBox.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            SettleTransitions(450);
            Check(livePeqWrites.Count > 0 && livePeqWrites[^1].Preamp == -6, "valid edits write the current preset in live mode using an isolated writer");
            var modeSelector = Field<Border>(window, "peqModeSelector");
            var modeButtons = Descendants<Button>(modeSelector).ToArray();
            var plainButton = Field<Button>(window, "peqImport");
            Check(modeSelector.BorderThickness == plainButton.BorderThickness && ((SolidColorBrush)modeSelector.BorderBrush).Color == ((SolidColorBrush)plainButton.BorderBrush).Color, "mode selector shares the ordinary button border");
            Check(Math.Abs(modeSelector.ActualWidth + modeSelector.Margin.Left - 210) < .5 && modeSelector.Margin.Left == 28 && Math.Abs(modeSelector.ActualHeight - 38) < .5 && modeButtons.All(button => VisualTreeHelper.HitTest(button, new Point(3, 3))?.VisualHit is Border && VisualTreeHelper.HitTest(button, new Point(button.ActualWidth - 3, button.ActualHeight - 3))?.VisualHit is Border), "mode selector preserves its original total width while shifting right and accepts full-block hits");
            var modeIndicator = (Border)((Grid)modeSelector.Child).Children[0];
            Check(modeIndicator.Effect is DropShadowEffect { BlurRadius: 4, ShadowDepth: 1, Opacity: .12 } && modeButtons.All(button => button.FontSize == plainButton.FontSize), "mode indicator has a faint shadow and its labels match ordinary button text size");
            double modeLeftInset = modeIndicator.TransformToAncestor(modeSelector).TransformBounds(new Rect(modeIndicator.RenderSize)).Left;
            modeButtons[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(ObserveTransition(300, () => Field<TranslateTransform>(window, "peqModeIndicator").X > 0 && Field<Border>(window, "peqApplySlot").ActualWidth == 0), "mode indicator moves before the lower action row starts");
            SettleTransitions(410);
            double modeRightInset = modeSelector.ActualWidth - modeIndicator.TransformToAncestor(modeSelector).TransformBounds(new Rect(modeIndicator.RenderSize)).Right;
            Check(Math.Abs(modeLeftInset - modeRightInset) < .01 && Math.Abs(modeLeftInset - 3) < .01, "mode indicator has equal three-DIP insets at both ends under fractional display scaling");
            int beforeManualEdit = livePeqWrites.Count;
            Check(Field<Border>(window, "peqApplySlot").Width > 0 && !Field<bool>(window, "peqLive"), "manual mode animates the apply action into the row");
            Invoke(window, "UpdatePeqControls");
            var findEndpointField = typeof(MainWindow).GetField("peqFindEndpoint", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var originalFindEndpoint = findEndpointField.GetValue(window);
            findEndpointField.SetValue(window, (Func<PeqEndpoint?>)(() => null));
            Invoke(window, "RefreshPeqEndpoint"); Render(window, "peq-apply-offline", 1240, 800);
            Check(Field<Button>(window, "peqApply").IsEnabled && Field<Border>(window, "peqApplySlot").IsHitTestVisible, "manual Apply remains enabled and accepts pointer input without an installed audio driver");
            int writesBeforeOffline = livePeqWrites.Count;
            Field<Button>(window, "peqApply").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(livePeqWrites.Count == writesBeforeOffline && Field<TextBlock>(window, "peqState").Text.StartsWith("PEQ 配置已保存") && Field<TextBlock>(window, "peqState").Text.Contains("未找到 Dongle 播放端点"), "offline Apply saves the model without writing audio settings or claiming processing");
            findEndpointField.SetValue(window, (Func<PeqEndpoint?>)(() => new("{00000000-0000-0000-0000-000000000000}", "test", false)));
            Invoke(window, "RefreshPeqEndpoint");
            Field<Button>(window, "peqApply").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(Field<Button>(window, "peqApply").IsEnabled && livePeqWrites.Count == writesBeforeOffline && Field<TextBlock>(window, "peqState").Text.Contains("尚未安装音频驱动"), "an uninstalled endpoint keeps Apply clickable and reports its real driver state");
            findEndpointField.SetValue(window, originalFindEndpoint);
            typeof(MainWindow).GetField("peqEndpoint", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new PeqEndpoint("{00000000-0000-0000-0000-000000000000}", "test", true));
            UiLanguage.Current.Select("en-US");
            Check(Field<Border>(window, "peqApplySlot").Width >= Field<Button>(window, "peqApply").DesiredSize.Width, "manual apply action resizes when the language changes");
            Field<Button>(window, "peqReset").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Render(window, "peq-english-undo-manual", 1240, 800);
            Check(Equals(Field<Button>(window, "peqReset").Content, "Undo reset") && Equals(Field<Button>(window, "peqInstall").Content, "Install audio driver"), "English driver installation restores the audio wording");
            var resetButton = Field<Button>(window, "peqReset"); var installButton = Field<Button>(window, "peqInstall");
            Check(Math.Abs(resetButton.TranslatePoint(new Point(), installButton).Y) < 1 && resetButton.TranslatePoint(new Point(), installButton).X > installButton.ActualWidth, "manual-mode undo reset remains at the right end of the same action row");
            var driverLabelBinding = System.Windows.Data.BindingOperations.GetBindingBase(installButton, ContentControl.ContentProperty)!;
            foreach (var code in UiLanguage.Codes)
            {
                UiLanguage.Current.Select(code);
                installButton.Content = UiLanguage.Current["卸载音频驱动"];
                Render(window, "peq-uninstall-undo-" + code, 1240, 800);
                if (Math.Abs(resetButton.TranslatePoint(new Point(), installButton).Y) > 1) throw new Exception("Manual uninstall and undo actions wrapped: " + code);
                var applyButton = Field<Button>(window, "peqApply");
                var applyText = Descendants<TextBlock>(applyButton).Single();
                var applyMeasure = new FormattedText(UiLanguage.Current["应用"], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(applyButton.FontFamily, applyButton.FontStyle, applyButton.FontWeight, applyButton.FontStretch), applyButton.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(applyButton).PixelsPerDip);
                if (applyText.ActualWidth + 1 < applyMeasure.WidthIncludingTrailingWhitespace) throw new Exception("Apply label is clipped after translation: " + code);
            }
            System.Windows.Data.BindingOperations.SetBinding(installButton, ContentControl.ContentProperty, driverLabelBinding);
            UiLanguage.Current.Select("en-US");
            Check(true, "all three languages keep manual Apply, the full uninstall audio label and Undo reset on one row");
            var headerState = Field<TextBlock>(window, "peqState");
            Invoke(window, "SetPeqState", "Ready · Idle");
            Render(window, "peq-status-short-en", 1240, 800);
            Check(headerState.Text == "Ready · Idle", "short English status keeps its separator and stays on one line");
            foreach (var key in new[] { "配置已写入，尚未确认音频处理", "音频驱动已卸载；必要时重新插拔 Dongle" })
            {
                string message = UiLanguage.Current[key];
                Invoke(window, "SetPeqState", message);
                Render(window, "peq-status-" + Array.IndexOf(new[] { "配置已写入，尚未确认音频处理", "音频驱动已卸载；必要时重新插拔 Dongle" }, key), 1240, 800);
                var lines = headerState.Text.Split('\n');
                if (lines.Length != 2 || string.Join(" · ", lines) != message) throw new Exception("Long English status did not split exactly at its separator");
                foreach (var line in lines)
                {
                    var measure = new FormattedText(line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(headerState.FontFamily, headerState.FontStyle, headerState.FontWeight, headerState.FontStretch), headerState.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(headerState).PixelsPerDip);
                    if (measure.WidthIncludingTrailingWhitespace > headerState.MaxWidth) throw new Exception("English status line still exceeds its display width");
                }
            }
            Check(true, "long English driver and processing statuses split into two complete lines at the middle dot");
            string savedStatus = UiLanguage.Current["PEQ 配置已保存"] + " · " + UiLanguage.Current["尚未安装音频驱动"];
            Invoke(window, "SetPeqState", savedStatus); Render(window, "peq-status-saved-en", 1240, 800);
            Check(headerState.Text == savedStatus.Replace(" · ", "\n") && Field<StackPanel>(window, "peqHeaderStatus").Children.OfType<TextBlock>().Count(text => text.Text.StartsWith('●')) == 1, "saved-preset status wraps at its delimiter while preserving the separate leading dot");
            UiLanguage.Current.Select("zh-CN");
            Invoke(window, "SetPeqState", "PEQ 配置已保存 · 尚未安装音频驱动");
            Check(!headerState.Text.Contains('\n'), "Chinese status keeps its existing single-line layout");
            Field<Button>(window, "peqReset").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            UiLanguage.Current.Select("zh-CN");
            preampBox.Text = "-8"; SettleTransitions(450);
            Check(livePeqWrites.Count == beforeManualEdit, "manual edits do not write the audio configuration");
            Invoke(window, "SetPeqMode", true); SettleTransitions(410);
            Check(Field<Border>(window, "peqApplySlot").Width == 0 && livePeqWrites[^1].Preamp == -8, "returning to live mode applies the latest settings and closes the apply slot");
            var modelFile = Path.Combine(AppPaths.EqConfigDirectory, data.HeadphoneName + ".json");
            Field<TextBox>(window, "peqPreamp").Text = "-7"; SettleTransitions(450);
            Check(JsonSerializer.Deserialize<PeqSettings>(File.ReadAllText(modelFile))!.Preamp == -7 && !modelFile.Contains(data.HeadphoneAddress), "valid edits autosave to an isolated model-named configuration");
            Invoke(window, "DisplaySnapshot", data with { HeadphoneName = "另一型号" });
            Field<TextBox>(window, "peqPreamp").Text = "-9";
            Invoke(window, "DisplaySnapshot", data with { HeadphoneAddress = "10:20:30:40:50:60" });
            Check(Field<TextBox>(window, "peqPreamp").Text == "-7" && JsonSerializer.Deserialize<PeqSettings>(File.ReadAllText(Path.Combine(AppPaths.EqConfigDirectory, "另一型号.json")))!.Preamp == -9, "model changes flush pending edits, keep separate profiles and share settings across MAC addresses");
            Field<HeadphoneHistory>(window, "history").Remove("10:20:30:40:50:60");
            Invoke(window, "DisplaySnapshot", data);
            var external = peqDefinition with { Preamp = -11 };
            File.WriteAllText(modelFile, JsonSerializer.Serialize(external)); SettleTransitions(550);
            Check(Field<TextBox>(window, "peqPreamp").Text == "-11" && ((PeqSettings)Invoke(window, "GetPeq")!).Bands.SequenceEqual(external.Bands), "external configuration edits are detected and reloaded without a refresh action");
            File.WriteAllText(modelFile, "invalid JSON"); SettleTransitions(550);
            Check(Field<TextBox>(window, "peqPreamp").Text == "-11" && File.ReadAllText(modelFile) == "invalid JSON" && Field<TextBlock>(window, "peqState").Text == "PEQ 配置无效", "invalid external configuration reports an error and preserves the last valid editor values without overwriting the file");
            File.WriteAllText(modelFile, JsonSerializer.Serialize(external with { Preamp = -12 })); SettleTransitions(550);
            Check(Field<TextBox>(window, "peqPreamp").Text == "-12", "configuration monitoring recovers when the external file is corrected");
            Field<TextBox>(window, "peqPreamp").Text = "-"; SettleTransitions(400);
            Check(JsonSerializer.Deserialize<PeqSettings>(File.ReadAllText(modelFile))!.Preamp == -12, "incomplete numeric editing does not replace a valid configuration");
            Invoke(window, "SetPeq", peqDefinition with { Bands = Enumerable.Repeat(peqDefinition.Bands[0], 64).ToArray() });
            Render(window, "peq-many-rows", 1240, 800);
            var peqScrollBar = (ScrollBar)table.Template.FindName("PART_VerticalScrollBar", table);
            var peqTrack = Descendants<Track>(peqScrollBar).Single();
            var thumb = peqTrack.Thumb;
            Check(table.ScrollableHeight > 0 && thumb.MinHeight == 0 && thumb.ActualHeight <= peqTrack.ActualHeight && Descendants<Border>(thumb).Single().CornerRadius == new CornerRadius(3), "long PEQ lists keep both scrollbar thumb caps within the allocated track");
            table.ScrollToTop(); table.UpdateLayout(); SettleTransitions();
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            table.RaiseEvent(wheel);
            Check(wheel.Handled && table.VerticalOffset == 0, "PEQ wheel begins a pixel animation without an immediate jump");
            ObserveTransition(200, () => { table.UpdateLayout(); return table.VerticalOffset > 0; }); double halfwayOffset = table.VerticalOffset;
            SettleTransitions(120); table.UpdateLayout(); double finalOffset = table.VerticalOffset;
            Check(halfwayOffset > 0 && halfwayOffset < finalOffset && finalOffset > 0, $"PEQ wheel moves through intermediate offsets and settles promptly ({halfwayOffset}, {finalOffset})");
            table.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            SampleTransition(35); table.UpdateLayout(); double turnOffset = table.VerticalOffset;
            table.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            SettleTransitions(150); table.UpdateLayout();
            Check(table.VerticalOffset < turnOffset, "reversing the wheel cancels the previous scroll destination");
            Invoke(window, "SetPeq", PeqSettings.Flat);
            Field<List<Button>>(window, "nav")[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(Field<CheckBox>(window, "peqEnabled").Visibility == Visibility.Collapsed && Field<StackPanel>(window, "peqHeaderStatus").Visibility == Visibility.Collapsed && Field<TextBlock>(window, "headerStatus").Visibility == Visibility.Collapsed, "Settings hides the header status and PEQ controls");
            var pages = Field<Dictionary<string, FrameworkElement>>(window, "pages");
            Check(pages["SETTINGS"].Visibility == Visibility.Visible && pages["USB"].Visibility == Visibility.Collapsed && logo.Visibility == Visibility.Collapsed && Field<TextBox>(window, "rename").Text == data.Name, "Settings owns rename control and hides the USB header logo");
            Render(window, "settings", 1240, 800);
            Field<List<Button>>(window, "nav")[3].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Render(window, "about", 1240, 800);
            Check(Field<TextBox>(window, "logBox").ActualHeight > 100, $"about log viewport keeps the remaining page height: {Field<TextBox>(window, "logBox").ActualHeight}");
            Check(window.FontFamily.Source.StartsWith("Microsoft YaHei, SimHei, ") && Field<List<Button>>(window, "nav").All(button => button.FontSize == 20) && Field<ComboBox>(window, "codecs").FontSize == microphone.FontSize, "font fallback order, balanced navigation labels and peer control text sizes are applied");
            Invoke(window, "ExportLog");
            Check(Directory.GetFiles(AppPaths.DataDirectory, "Questyle-diagnostics-*.txt").Length > 0 && Field<TextBlock>(window, "status").Text == "诊断日志已保存", "diagnostic export writes to the installation data directory without a save dialog");

            var protocolCombo = Field<ComboBox>(window, "codecs"); protocolCombo.ApplyTemplate();
            var dropToggle = (ToggleButton)protocolCombo.Template.FindName("DropDownButton", protocolCombo); dropToggle.ApplyTemplate();
            var comboSurface = (Border)dropToggle.Template.FindName("ComboSurface", dropToggle);
            var arrow = (System.Windows.Shapes.Path)protocolCombo.Template.FindName("DropArrow", protocolCombo);
            Check(comboSurface.CornerRadius == new CornerRadius(7) && arrow.StrokeStartLineCap == PenLineCap.Round && arrow.StrokeEndLineCap == PenLineCap.Round && arrow.StrokeLineJoin == PenLineJoin.Round, "protocol combo has matching rounded corners and a rounded-stroke chevron");
            var popup = (System.Windows.Controls.Primitives.Popup)protocolCombo.Template.FindName("PART_Popup", protocolCombo);
            Check(popup != null && ((Border)popup.Child).CornerRadius == new CornerRadius(7) && System.Windows.Data.BindingOperations.GetBinding(dropToggle, ToggleButton.IsCheckedProperty)?.Mode == System.Windows.Data.BindingMode.TwoWay, "rounded dropdown retains the normal popup and two-way open/close binding");
            var popupScroll = (ScrollViewer)((Border)popup!.Child).Child;
            popupScroll.Measure(new Size(240, 300));
            var optionProbe = new ComboBoxItem { Content = "LPQ", Style = (Style)window.FindResource(typeof(ComboBoxItem)) }; optionProbe.ApplyTemplate();
            var optionSurface = (Border)optionProbe.Template.FindName("ItemSurface", optionProbe);
            Check(SmoothScrolling.GetEnabled(popupScroll) && !popupScroll.CanContentScroll && optionProbe.Padding == new Thickness(10, 6, 10, 6) && optionSurface.BorderThickness == new Thickness(0) && optionSurface.Effect == null, "dropdown options retain their original padding and flat borderless appearance with smooth scrolling");
            Check(popup.PopupAnimation == PopupAnimation.None && popup.ReadLocalValue(Popup.IsOpenProperty) == DependencyProperty.UnsetValue, "dropdown uses the timed fade instead of the system popup animation");
            var fadeChild = new Border(); var fadePopup = new OffscreenPopup { Child = fadeChild };
            StyleTransitions.SetDropDownOpen(fadePopup, true);
            Check(!fadePopup.IsOpen && fadeChild.Opacity == 0, "simulated dropdown starts its fade-in from transparent without creating a native popup window");
            SettleTransitions(140);
            Check(fadeChild.Opacity == 1 && fadePopup.IsHitTestVisible, "dropdown fade-in settles promptly");
            StyleTransitions.SetDropDownOpen(fadePopup, false);
            Check(fadeChild.Opacity == 1 && !fadePopup.IsHitTestVisible, "simulated dropdown starts its fade-out from the displayed opacity and stops accepting clicks immediately");
            StyleTransitions.SetDropDownOpen(fadePopup, true); SettleTransitions(140);
            Check(StyleTransitions.GetDropDownOpen(fadePopup) && fadeChild.Opacity == 1 && fadePopup.IsHitTestVisible, "reopening a dropdown replaces the unfinished fade-out");
            StyleTransitions.SetDropDownOpen(fadePopup, false); SettleTransitions(140);
            Check(Field<TextBlock>(window, "status").Visibility == Visibility.Hidden && Field<TextBlock>(window, "status").Parent is DockPanel, "bottom status text is hidden while its layout remains reserved");

            var closeButton = Field<Button>(window, "closeButton"); closeButton.ApplyTemplate();
            var closeSurface = (Border)closeButton.Template.FindName("CloseSurface", closeButton);
            var normalBackground = ((SolidColorBrush)closeSurface.Background).Color;
            closeButton.Tag = "Hover"; SettleTransitions();
            var hoverBackground = ((SolidColorBrush)closeSurface.Background).Color;
            SetControlState(closeButton, typeof(UIElement), "IsKeyboardFocusedPropertyKey", true);
            Check(hoverBackground != normalBackground, "close button highlights only while the pointer is over it");
            closeButton.Tag = null; SettleTransitions();
            Check(((SolidColorBrush)closeSurface.Background).Color == normalBackground, "keyboard focus alone does not leave a close-button highlight");
            closeButton.Tag = "Hover"; SettleTransitions();
            Render(window, "close-focused", 1240, 800);
            SetControlState(closeButton, typeof(ButtonBase), "IsPressedPropertyKey", true); SettleTransitions();
            Check(((SolidColorBrush)closeSurface.Background).Color != hoverBackground && ((SolidColorBrush)closeButton.Foreground).Color == Colors.White, "held close button has a distinct pressed background and white text");
            Render(window, "close-pressed", 1240, 800);
            Invoke(window, "UpdateClosePointer", new Point(-10, -10)); SettleTransitions();
            Check(((SolidColorBrush)closeSurface.Background).Color == normalBackground, "dragging a held close button outside clears its highlight despite mouse capture");
            SetControlState(closeButton, typeof(ButtonBase), "IsPressedPropertyKey", false);
            SetControlState(closeButton, typeof(UIElement), "IsKeyboardFocusedPropertyKey", false);
            closeButton.Tag = null;

            var transitionProbe = new Border { Background = Brushes.Red };
            StyleTransitions.SetEnabled(transitionProbe, true);
            transitionProbe.Background = Brushes.Blue;
            Check(ObserveTransition(400, () => ((SolidColorBrush)transitionProbe.Background).Color is var color && color != Colors.Red && color != Colors.Blue), "interaction brushes visibly interpolate instead of jumping between states");
            SettleTransitions(245);
            Check(((SolidColorBrush)transitionProbe.Background).Color == Colors.Blue, "interaction transitions finish on the requested style");

            var navigationActions = Field<List<Button>>(window, "nav");
            navigationActions[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); SettleTransitions();
            var settingsNav = navigationActions[2];
            var settingsSurface = (Border)settingsNav.Template.FindName("NavigationSurface", settingsNav);
            settingsNav.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(ObserveTransition(300, () => ((SolidColorBrush)settingsSurface.Background).Color is { A: > 0 and < 255, R: 52, G: 55, B: 64 }), "sidebar selection fades through neutral grey without a white or pink flash");
            SettleTransitions(245);
            SetControlState(settingsNav, typeof(UIElement), "IsMouseOverPropertyKey", true);
            SetControlState(settingsNav, typeof(ButtonBase), "IsPressedPropertyKey", true); SettleTransitions();
            settingsNav.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            SetControlState(settingsNav, typeof(ButtonBase), "IsPressedPropertyKey", false); SettleTransitions();
            Check(((SolidColorBrush)settingsNav.Foreground).Color == Colors.White && settingsSurface.Opacity == 1 && ((SolidColorBrush)settingsSurface.Background).Color == Color.FromRgb(59, 63, 73), "selected sidebar text stays white after mouse release while only the background changes");
            SetControlState(settingsNav, typeof(UIElement), "IsMouseOverPropertyKey", false); SettleTransitions();
            navigationActions[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); SettleTransitions();
            SetControlState(settingsNav, typeof(UIElement), "IsMouseOverPropertyKey", true); SettleTransitions();
            SetControlState(settingsNav, typeof(ButtonBase), "IsPressedPropertyKey", true); SettleTransitions(450);
            var heldNavigationColor = ((SolidColorBrush)settingsSurface.Background).Color;
            SetControlState(settingsNav, typeof(ButtonBase), "IsPressedPropertyKey", false);
            settingsNav.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(((SolidColorBrush)settingsSurface.Background).Color == heldNavigationColor, "releasing a held unselected sidebar button preserves the displayed color before selection animates");
            Check(ObserveTransition(300, () => ((SolidColorBrush)settingsSurface.Background).Color is { R: > 59 and < 69, G: > 63 and < 74, B: > 73 and < 86 }), "selection after a long press interpolates from pressed to selected hover without a flash");
            SettleTransitions(245);
            SetControlState(settingsNav, typeof(UIElement), "IsMouseOverPropertyKey", false); SettleTransitions();
            Render(window, "sidebar-selection", 1240, 800);
            navigationActions[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); SettleTransitions(115);
            Check(((SolidColorBrush)settingsSurface.Background).Color.A == 0 && ((SolidColorBrush)settingsNav.Foreground).Color == Color.FromRgb(0xC5, 0xCB, 0xD6), "sidebar deselection fades out within the shorter transition while retaining its original colors");

            var shadowType = typeof(MainWindow).Assembly.GetType("Questyle.Desktop.WindowShadow")!;
            var shadowSurface = (Grid)shadowType.GetMethod("CreateSurface", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            shadowType.GetMethod("ClipSurface", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [shadowSurface, 400d, 300d]);
            RenderElement(shadowSurface, "window-shadow", 456, 356);
            var shadowBitmap = new BitmapImage(new Uri(Path.Combine(AppPaths.DataDirectory, "window-shadow.png")));
            var pixels = new byte[456 * 356 * 4]; shadowBitmap.CopyPixels(pixels, 456 * 4, 0);
            Check(pixels[(178 * 456 + 228) * 4 + 3] == 0 && pixels[(18 * 456 + 228) * 4 + 3] > 0, "separate shadow renders outside the body with a transparent centre");
            int ShadowAlpha(int x, int y) => pixels[(y * 456 + x) * 4 + 3];
            bool equalShadowEdges = Enumerable.Range(0, 28).All(distance =>
            {
                int[] edges = [ShadowAlpha(27 - distance, 178), ShadowAlpha(428 + distance, 178), ShadowAlpha(228, 27 - distance), ShadowAlpha(228, 328 + distance)];
                return edges.Max() - edges.Min() <= 2;
            });
            Check(equalShadowEdges, "window shadow has matching alpha falloff at equal distances on all four edges");
            var shadowFrame = (FrameworkElement)Activator.CreateInstance(typeof(MainWindow).Assembly.GetType("Questyle.Desktop.ShadowFrame")!)!;
            shadowFrame.GetType().GetMethod("Prepare")!.Invoke(shadowFrame, [1d]);
            shadowFrame.GetType().GetProperty("Bounds")!.SetValue(shadowFrame, new Rect(0, 0, 400, 300));
            RenderElement(shadowFrame, "window-shadow-frame", 456, 356);
            var framePixels = new byte[456 * 356 * 4];
            new BitmapImage(new Uri(Path.Combine(AppPaths.DataDirectory, "window-shadow-frame.png"))).CopyPixels(framePixels, 456 * 4, 0);
            Check(Enumerable.Range(0, 456 * 356).Max(i => Math.Abs(framePixels[i * 4 + 3] - pixels[i * 4 + 3])) <= 2, "the animated shadow, assembled from pre-rendered corners and edges, matches the blurred shadow");
            var frameBorder = (Border)window.FindName("WindowFrame");
            Check(frameBorder.Margin == new Thickness(0) && frameBorder.Effect == null, "main window bounds match the visible frame without shadow padding");
            Check(Math.Abs(frameBorder.BorderThickness.Left * VisualTreeHelper.GetDpi(window).DpiScaleX - 1) < .01, "window border remains one physical pixel thick with antialiased curves");
            Render(window, "window-rounded-edge", 1240, 800);
            var edgeBitmap = new FormatConvertedBitmap(new BitmapImage(new Uri(Path.Combine(AppPaths.DataDirectory, "window-rounded-edge.png"))), PixelFormats.Bgra32, null, 0);
            var edgePixels = new byte[edgeBitmap.PixelWidth * edgeBitmap.PixelHeight * 4]; edgeBitmap.CopyPixels(edgePixels, edgeBitmap.PixelWidth * 4, 0);
            bool darkCorners = true, antialiased = false;
            foreach (int y in Enumerable.Range(0, 28).Concat(Enumerable.Range(edgeBitmap.PixelHeight - 28, 28)))
                for (int x = 0; x < 28; x++)
                {
                    int i = (y * edgeBitmap.PixelWidth + x) * 4; byte alpha = edgePixels[i + 3];
                    if (alpha is > 32 and < 255) antialiased = true;
                    if (alpha > 32 && (edgePixels[i] > 40 || edgePixels[i + 1] > 40 || edgePixels[i + 2] > 40)) darkCorners = false;
                }
            Check(darkCorners && antialiased, "rendered left corners retain antialiasing without a light fringe");
            Field<List<Button>>(window, "nav")[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Render(window, "device-spacing", 1240, 800);
            var cards = Field<List<FrameworkElement>>(window, "usbControls").OfType<Border>().Where(card => !double.IsNaN(card.Height)).ToArray();
            Check(cards.Length == 2 && cards[0].Height == cards[1].Height && cards[0].Height == 416, "audio and headphones cards both give four DIP to the device information card");
            var searchAction = Descendants<Button>(cards[1]).Single(button => Equals(button.Content, "搜索蓝牙耳机"));
            var headphoneStatus = Field<TextBlock>(window, "headphoneInfo"); var headphoneList = Field<ListBox>(window, "sinks");
            double searchTop = searchAction.TransformToAncestor(cards[1]).Transform(new Point()).Y;
            double statusBottom = headphoneStatus.TransformToAncestor(cards[1]).Transform(new Point(0, headphoneStatus.ActualHeight)).Y;
            double searchBottom = searchTop + searchAction.ActualHeight;
            double listTop = headphoneList.TransformToAncestor(cards[1]).Transform(new Point()).Y;
            Check(Math.Abs(searchTop - statusBottom - 10) < 1 && Math.Abs(listTop - searchBottom - 10) < 1, $"headphone search button has ten DIP above and below without an empty reserved text line ({searchTop - statusBottom:F2}, {listTop - searchBottom:F2})");
            var deviceCard = Descendants<Border>(pages["USB"]).Single(border => border.Tag is "Card" && Descendants<TextBlock>(border).Contains(Field<TextBlock>(window, "usbName")));
            var deviceName = Field<TextBlock>(window, "usbName"); var capabilities = Field<TextBlock>(window, "supportedInfo");
            double nameTop = deviceName.TransformToAncestor(deviceCard).Transform(new Point()).Y;
            double capabilitiesBottom = capabilities.TransformToAncestor(deviceCard).Transform(new Point(0, capabilities.ActualHeight)).Y;
            Check(Math.Abs(nameTop - (deviceCard.ActualHeight - capabilitiesBottom)) < .1, "device information has equal top and bottom content spacing");
            Field<List<Button>>(window, "nav")[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Render(window, "settings-rounded-input", 1240, 800);
            var nameInput = Field<TextBox>(window, "rename"); nameInput.ApplyTemplate();
            Check(((Border)nameInput.Template.FindName("InputSurface", nameInput)).CornerRadius == comboSurface.CornerRadius && Descendants<Button>(pages["SETTINGS"]).Any(button => button.Content.Equals("恢复原名")), "device name input matches dropdown rounding and offers original-name restoration");
            var nameButtons = Descendants<Button>(pages["SETTINGS"]).Where(button => Equals(button.Content, "保存名称") || Equals(button.Content, "恢复原名")).ToArray();
            Check(nameButtons.Length == 2 && nameButtons.All(button => Math.Abs(button.ActualHeight - Field<Button>(window, "readButton").ActualHeight) < .1) && Math.Abs(nameInput.ActualHeight - Field<Button>(window, "readButton").ActualHeight) < .1, "name input and both name actions match the read button height");
            var nameViewport = Descendants<ScrollContentPresenter>(nameInput).Single();
            Check(nameViewport.TransformToAncestor(nameInput).TransformBounds(new Rect(nameViewport.RenderSize)).Contains(nameInput.GetRectFromCharacterIndex(0)), "compact name input keeps the device name fully inside its text viewport");
            double firstCharacterLeft = nameInput.GetRectFromCharacterIndex(0).Left;
            Check(firstCharacterLeft >= nameInput.Padding.Left && firstCharacterLeft < nameInput.Padding.Left + 5, $"text input uses one padding plus the native text inset: {firstCharacterLeft}");
            VerifySettingsAlignment(window);
            VerifyUpdates(window);
            var footerVersion = Descendants<TextBlock>((DependencyObject)window.Content).Single(text => text.Text == "v" + InstalledVersion.Current.Display && text.TextAlignment == TextAlignment.Center);
            Check(footerVersion.TextAlignment == TextAlignment.Center && footerVersion.Margin.Left == footerVersion.Margin.Right, "sidebar version uses the v prefix and symmetric horizontal alignment");

            Check(((SolidColorBrush)frameBorder.Background).Color == Color.FromRgb(24, 27, 36) && !frameBorder.SnapsToDevicePixels, "antialiased rounded edge has the sidebar colour underneath instead of a light corner fringe");
            Field<List<Button>>(window, "nav")[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var applyAction = Descendants<Button>(cards[0]).Single(); var disconnectAction = Descendants<Button>(cards[1]).Last();
            Check(((SolidColorBrush)Field<Button>(window, "readButton").Background).Color == Color.FromRgb(32, 119, 206) && ((SolidColorBrush)applyAction.Background).Color == Color.FromRgb(75, 155, 102) && ((SolidColorBrush)disconnectAction.Background).Color == Color.FromRgb(202, 38, 61), "read, apply and disconnect actions use blue, muted green and slightly muted red");
            Check(window.Icon != null && new FileInfo(Path.Combine(AppContext.BaseDirectory, "assets", "Questyle.ico")).Length > 1000, "window uses the supplied multi-resolution icon");
            Check(StartupRegistration.Command(@"C:\Questyle App\Questyle.exe") == "\"C:\\Questyle App\\Questyle.exe\"" && Field<CheckBox>(window, "startup").Template != null, "startup executable paths are quoted and the switch has a custom template");
            var language = Field<ComboBox>(window, "language");
            Field<List<Button>>(window, "nav")[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            language.SelectedIndex = 2; window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Check(Field<TextBlock>(window, "title").Text == "Settings" && Field<Button>(window, "readButton").Content.Equals("Read device again") && File.ReadAllText(Path.Combine(AppPaths.DataDirectory, "settings.json")).Contains("en-US"), "language changes update the visible UI and persist locally");
            Render(window, "settings-english", 1240, 800);
            language.SelectedIndex = 1; window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Check(Field<TextBlock>(window, "title").Text == "設定", "Traditional Chinese applies immediately");
            language.SelectedIndex = 0; window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Field<List<Button>>(window, "nav")[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Invoke(window, "DisplaySnapshot", data with { HeadphoneAddress = "00:00:00:00:00:00", ActiveCodec = "aptX Adaptive" });
            Check(!Field<TextBlock>(window, "supportedInfo").Text.Contains("耳机支持：") && Field<TextBlock>(window, "supportedInfo").Text.StartsWith("设备支持："), "disconnected snapshots hide stale headphone capabilities while retaining device capabilities");
            Check(Field<TextBlock>(window, "headphoneInfo").Text == "未连接蓝牙设备" && !disconnectAction.IsEnabled && logo.Visibility == Visibility.Collapsed && adaptiveField.Visibility == Visibility.Collapsed && Field<ListBox>(window, "sinks").SelectedItem == null && !Field<Border>(window, "audioPanel").IsEnabled && Field<ComboBox>(window, "codecs").Items.Count == 0 && Field<ComboBox>(window, "codecs").SelectedItem == null, "disconnected state uses the Bluetooth label, disables disconnect and clears audio controls");
            Render(window, "device-disconnected", 1240, 800);
            Invoke(window, "UpdateUsbDetection", false);
            Check(Field<TextBlock>(window, "headerStatus").Text == "●  未插入 USB 设备", "unplugged USB status retains its dot");
            Invoke(window, "DisplaySnapshot", data);
            var savedList = Field<ListBox>(window, "sinks");
            Check(savedList.Items.Count == 1 && ((AudioSink)savedList.Items[0]).Name == "测试耳机", "main headphone list restores saved records at startup");
            Invoke(window, "DisplaySnapshot", data with { HeadphoneAddress = remembered.Address, HeadphoneName = "测试耳机" });
            Field<List<Button>>(window, "nav")[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Render(window, "device-remembered", 1240, 800);
            var selectedItem = (ListBoxItem)savedList.ItemContainerGenerator.ContainerFromIndex(0);
            var rowName = Descendants<TextBlock>(selectedItem).Single(text => text.Text == "测试耳机");
            var rowAddress = Descendants<TextBlock>(selectedItem).Single(text => text.Text == remembered.Address);
            var connectedSnapshot = Field<DongleSnapshot>(window, "snapshot");
            var connectedLog = Field<TextBox>(window, "logBox").Text;
            var connectedStatus = Field<TextBlock>(window, "status").Text;
            rowName.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            ((Task)Invoke(window, "ConnectSink", remembered)!).GetAwaiter().GetResult();
            Check(ReferenceEquals(connectedSnapshot, Field<DongleSnapshot>(window, "snapshot")) && selectedItem.IsSelected && disconnectAction.IsEnabled && Field<TextBox>(window, "logBox").Text == connectedLog && Field<TextBlock>(window, "status").Text == connectedStatus && !Field<bool>(window, "busy"), "activating the connected headphone does nothing and opens no USB operation");
            var nameOrigin = rowName.TransformToAncestor(selectedItem).Transform(new Point()); var addressOrigin = rowAddress.TransformToAncestor(selectedItem).Transform(new Point());
            Check(Math.Abs(nameOrigin.Y - addressOrigin.Y) < 1 && addressOrigin.X > nameOrigin.X, "headphone MAC is on the same line to the right of its name");
            var delete = Descendants<Button>(selectedItem).Single(button => Equals(button.Content, "删除"));
            var controlBorder = (SolidColorBrush)window.FindResource("ControlBorderBrush");
            var outlinedActions = new[] { Field<Button>(window, "readButton"), disconnectAction, delete }.Concat(Descendants<Button>(cards[0])).ToArray();
            Check(outlinedActions.All(button => button.BorderThickness == new Thickness(1) && ((SolidColorBrush)button.BorderBrush).Color == controlBorder.Color), "colored device actions and delete buttons share the ordinary button border");
            string[] surfaceNames = ["B", "Track", "CheckSurface"];
            var interactiveSurfaces = Descendants<Border>((DependencyObject)window.Content).Where(border => surfaceNames.Contains(border.Name)).Append(Field<Border>(window, "peqModeSelector")).ToArray();
            Check(interactiveSurfaces.Length > 20 && interactiveSurfaces.All(border => border.BorderThickness == new Thickness(1) && border.Effect is DropShadowEffect { BlurRadius: 4, ShadowDepth: 1, Opacity: .12 }) && navigationActions.All(button => button.BorderThickness == new Thickness(0) && button.Template.FindName("NavigationSurface", button) is Border { BorderThickness.Left: 0, Effect: null }), "controls retain their border and faint shadow while sidebar labels keep their original borderless appearance");
            SettleTransitions();
            Check(((SolidColorBrush)((Border)selectedItem.Template.FindName("ItemBorder", selectedItem)).Background).Color == System.Windows.Media.Color.FromRgb(232, 242, 255), "connected headphone highlight is light blue");
            Check(((SolidColorBrush)delete.Background).Color == Color.FromRgb(202, 38, 61), "headphone delete action uses slightly muted red");
            Check(ReferenceEquals(delete.Template, disconnectAction.Template) && ((Border)delete.Template.FindName("B", delete)).CornerRadius == new CornerRadius(7) && delete.Height == 32 && delete.Padding == new Thickness(12, 6, 12, 6), "row delete shares the disconnect button template with rounded corners and comfortable padding");
            Check(selectedItem.IsSelected && delete.GetAnimationBaseValue(UIElement.OpacityProperty).Equals(0d) && !delete.IsHitTestVisible, "selected headphone hides its delete button until hovered");
            SetControlState(selectedItem, typeof(UIElement), "IsMouseOverPropertyKey", true);
            Check(delete.GetAnimationBaseValue(UIElement.OpacityProperty).Equals(1d) && delete.IsHitTestVisible, "hover exposes the delete action on that headphone row");
            Render(window, "device-row-hover", 1240, 800);
            var addressBounds = rowAddress.TransformToAncestor(selectedItem).TransformBounds(new Rect(rowAddress.RenderSize));
            var deleteBounds = delete.TransformToAncestor(selectedItem).TransformBounds(new Rect(delete.RenderSize));
            Check(Math.Abs(addressBounds.Right - (selectedItem.ActualWidth - selectedItem.Padding.Right - 1)) < 1 && Math.Abs(addressBounds.X - addressOrigin.X) < .1 && deleteBounds.IntersectsWith(addressBounds), "MAC stays at the row's right edge and the delete button overlays it without reserving space");
            SetControlState(selectedItem, typeof(UIElement), "IsMouseOverPropertyKey", false);
            var drag = typeof(MainWindow).GetMethod("IsDragBackground", BindingFlags.NonPublic | BindingFlags.Static)!;
            Check((bool)drag.Invoke(null, [window.FindName("Root")])! && !(bool)drag.Invoke(null, [delete])! && !(bool)drag.Invoke(null, [microphone])!, "background dragging excludes buttons and card contents");
            var doubleClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = window.FindName("Root") };
            typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(doubleClick, 2);
            Invoke(window, "MoveFromBackground", window.FindName("Root"), doubleClick);
            Check(window.WindowState == WindowState.Normal && doubleClick.ClickCount == 2, "double-clicking the background does not maximize the window");
            var uiAudioMemory = Field<HeadphoneAudioMemory>(window, "audioMemory");
            uiAudioMemory.RememberConfirmed(connectedSnapshot);
            Func<Task> failedDisconnect = () => Task.FromException(new IOException("simulated disconnect failure"));
            try { ((Task)Invoke(window, "RemoveRememberedHeadphone", remembered, failedDisconnect)!).GetAwaiter().GetResult(); throw new Exception("Failed disconnect removed memory"); }
            catch (IOException) { Check(savedList.Items.Count == 1 && uiAudioMemory.Find(remembered.Address) != null, "failed disconnect preserves the headphone record and audio preferences"); }
            var otherSink = remembered with { Address = "01:02:03:04:05:07", Name = "其他耳机" };
            Field<HeadphoneHistory>(window, "history").Remember(otherSink);
            ((Task)Invoke(window, "RemoveRememberedHeadphone", otherSink, (Func<Task>)(() => throw new Exception("Disconnected the wrong headphone")))!).GetAwaiter().GetResult();
            Check(ReferenceEquals(connectedSnapshot, Field<DongleSnapshot>(window, "snapshot")) && disconnectAction.IsEnabled && savedList.Items.Count == 1, "deleting an inactive record preserves the current Bluetooth connection");
            int disconnects = 0;
            Func<Task> simulatedDisconnect = () =>
            {
                Check(Field<HeadphoneHistory>(window, "history").Headphones.Count == 1 && uiAudioMemory.Find(remembered.Address) != null, "connected record remains until disconnect completes");
                disconnects++;
                Invoke(window, "DisplaySnapshot", data with { HeadphoneAddress = "00:00:00:00:00:00", ActiveCodec = "未连接" });
                return Task.CompletedTask;
            };
            ((Task)Invoke(window, "RemoveRememberedHeadphone", remembered, simulatedDisconnect)!).GetAwaiter().GetResult();
            reloaded.Load();
            Check(disconnects == 1 && savedList.Items.Count == 0 && reloaded.Headphones.Count == 0 && uiAudioMemory.Find(remembered.Address) == null && !disconnectAction.IsEnabled, "connected deletion disconnects first then persists removal of device and audio memory");
            Invoke(window, "DisplaySnapshot", data with { HeadphoneAddress = "00:00:00:00:00:00", ActiveCodec = "未连接" });
            Field<HeadphoneHistory>(window, "history").Remember(otherSink); Invoke(window, "RefreshRememberedHeadphones", otherSink.Address);
            Render(window, "device-disconnected-record", 1240, 800);
            var inactiveItem = (ListBoxItem)savedList.ItemContainerGenerator.ContainerFromIndex(0);
            Descendants<Button>(inactiveItem).Single().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(savedList.Items.Count == 0 && !Field<bool>(window, "busy"), "row delete routes through the serialized operation and removes an inactive record without USB");

            var pairingSession = new TaskCompletionSource<DongleSnapshot>();
            var pairing = (Task)Invoke(window, "ConnectHeadphone", remembered, (Func<Task<DongleSnapshot>>)(() => pairingSession.Task))!;
            var waitFrame = new DispatcherFrame(); application.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => waitFrame.Continue = false)); Dispatcher.PushFrame(waitFrame);
            Check(savedList.Items.Count == 0 && window.ConnectedHeadphoneAddress == null, "connecting does not remember or highlight the headphone before its session confirms the connection");
            pairingSession.SetResult(data);
            var pairingFrame = new DispatcherFrame(); pairing.ContinueWith(_ => application.Dispatcher.BeginInvoke(() => pairingFrame.Continue = false)); Dispatcher.PushFrame(pairingFrame); pairing.GetAwaiter().GetResult();
            Check(savedList.Items.Count == 1 && window.ConnectedHeadphoneAddress == remembered.Address && Field<Button>(window, "disconnectButton").IsEnabled, "the confirmed connection readback updates the saved list and connected UI without manual refresh");
            try { ((Task)Invoke(window, "ConnectHeadphone", otherSink, (Func<Task<DongleSnapshot>>)(() => Task.FromException<DongleSnapshot>(new IOException("simulated connect session failure"))))!).GetAwaiter().GetResult(); throw new Exception("Failed pairing was saved"); }
            catch (IOException) { Check(savedList.Items.Count == 1 && window.ConnectedHeadphoneAddress == remembered.Address, "a failed connection session saves nothing and keeps the last confirmed state"); }
            var notYetConnected = data with { HeadphoneAddress = "00:00:00:00:00:00", ActiveCodec = "未连接" };
            try { ((Task)Invoke(window, "ConnectHeadphone", otherSink, (Func<Task<DongleSnapshot>>)(() => Task.FromResult(notYetConnected)))!).GetAwaiter().GetResult(); throw new Exception("Unconfirmed pairing was accepted"); }
            catch (IOException) { Check(ReferenceEquals(notYetConnected, Field<DongleSnapshot>(window, "snapshot")) && window.ConnectedHeadphoneAddress == null && !Field<Button>(window, "disconnectButton").IsEnabled && savedList.Items.Count == 1, "an unconfirmed connection shows the actual readback instead of the previous headphone and saves nothing"); }
            Invoke(window, "DisplaySnapshot", data with { HeadphoneAddress = "00:00:00:00:00:00", ActiveCodec = "未连接" });
            Field<HeadphoneHistory>(window, "history").Remove(remembered.Address); Invoke(window, "RefreshRememberedHeadphones", new object[] { null! });

            Field<HeadphoneHistory>(window, "history").Remember(otherSink);
            Invoke(window, "DisplaySnapshot", data); Render(window, "headphone-highlight", 1240, 800); SettleTransitions();
            var connectedRow = (ListBoxItem)savedList.ItemContainerGenerator.ContainerFromItem(savedList.Items.OfType<AudioSink>().Single(sink => sink.Address == remembered.Address));
            var inactiveRow = (ListBoxItem)savedList.ItemContainerGenerator.ContainerFromItem(savedList.Items.OfType<AudioSink>().Single(sink => sink.Address == otherSink.Address));
            Color RowColor(ListBoxItem item) => ((SolidColorBrush)((Border)item.Template.FindName("ItemBorder", item)).Background).Color;
            savedList.SelectedItem = inactiveRow.Content; SettleTransitions();
            Check(RowColor(connectedRow) == Color.FromRgb(232, 242, 255) && RowColor(inactiveRow) == Color.FromRgb(245, 246, 248), "keyboard selection does not move the blue connection highlight to another headphone");
            Invoke(window, "DisplaySnapshot", data with { HeadphoneAddress = "00:00:00:00:00:00", ActiveCodec = "未连接" }); SettleTransitions();
            Check(RowColor(connectedRow) == Color.FromRgb(245, 246, 248) && RowColor(inactiveRow) == Color.FromRgb(245, 246, 248), "a disconnected snapshot immediately removes every connection highlight");
            foreach (var item in Field<HeadphoneHistory>(window, "history").Headphones.ToArray()) Field<HeadphoneHistory>(window, "history").Remove(item.Address);
            Invoke(window, "RefreshRememberedHeadphones", new object[] { null! });

            for (int i = 0; i < 12; i++) savedList.Items.Add(new AudioSink($"01:02:03:04:05:{i:X2}", "耳机 " + i, 4));
            Render(window, "device-list-scroll", 1240, 800);
            var listScroll = Descendants<ScrollViewer>(savedList).First();
            var scrollbar = Descendants<ScrollBar>(savedList).Single(bar => bar.Orientation == Orientation.Vertical);
            Check(savedList.ActualHeight == 192 && listScroll.ScrollableHeight > 0 && scrollbar.Visibility == Visibility.Visible && scrollbar.Width == 8 && Descendants<Thumb>(scrollbar).Any(), "fixed headphone list scrolls with a narrow rounded thumb");
            var firstFour = Enumerable.Range(0, 4).Select(index => (ListBoxItem)savedList.ItemContainerGenerator.ContainerFromIndex(index)).ToArray();
            Check(firstFour.All(item => Math.Abs(item.ActualHeight - 42.75) < .01 && item.Margin == new Thickness(0, 0, 0, 5.25)) && firstFour.Skip(1).All(item => Math.Abs(item.TranslatePoint(new Point(), savedList).Y - firstFour[Array.IndexOf(firstFour, item) - 1].TranslatePoint(new Point(), savedList).Y - 48) < .01) && firstFour[3].TransformToAncestor(savedList).Transform(new Point(0, firstFour[3].ActualHeight)).Y <= savedList.ActualHeight, "saved headphone rows shrink to 95 percent with the removed height in their margin, preserving 48-DIP slots and four complete devices");
            Check(firstFour.All(item => item.Effect is DropShadowEffect { BlurRadius: 4, ShadowDepth: 1, Opacity: .04 }) && ((DropShadowEffect)firstFour[0].Effect).Opacity < ((DropShadowEffect)interactiveSurfaces[0].Effect).Opacity, "every saved headphone row has a very faint shadow lighter than the action buttons");
            var audioHeading = Descendants<TextBlock>(cards[0]).Single(text => text.Text == "音频设置");
            var protocolHeading = Descendants<TextBlock>(cards[0]).Single(text => text.Text == "传输协议");
            var page = (ScrollViewer)pages["USB"];
            var firstCard = ((StackPanel)page.Content).Children[0];
            double Left(FrameworkElement element) => element.TransformToAncestor((Visual)window.Content).Transform(new Point()).X;
            Check(Math.Abs(Left(audioHeading) - Left(protocolHeading)) < 1 && Math.Abs(Left(Field<TextBlock>(window, "title")) - Left((FrameworkElement)firstCard)) < 1 && Math.Abs(Left(Field<TextBlock>(window, "status")) - Left((FrameworkElement)firstCard)) < 1, "card headings and protocol labels align; page title and status align with the content card edge");
            var brand = Descendants<TextBlock>((DependencyObject)window.Content).Single(text => text.Text == "Questyle");
            var root = (Grid)window.FindName("Root");
            var brandCenter = brand.TransformToAncestor(root).Transform(new Point(brand.ActualWidth / 2, 0)).X;
            Check(Math.Abs(brandCenter - root.ColumnDefinitions[0].ActualWidth / 2) < 1, "Questyle brand is centred horizontally in the sidebar");
            var audioFooter = Descendants<Button>(cards[0]).Single(button => Equals(button.Content, "应用音频设置"));
            var headphoneFooter = Descendants<Button>(cards[1]).Single(button => Equals(button.Content, "断开当前设备"));
            Check(Math.Abs(audioFooter.TransformToAncestor(root).Transform(new Point()).Y - headphoneFooter.TransformToAncestor(root).Transform(new Point()).Y) < 1 && savedList.TransformToAncestor(cards[1]).Transform(new Point(0, savedList.ActualHeight)).Y + 8 <= headphoneFooter.TransformToAncestor(cards[1]).Transform(new Point()).Y, "panel footer buttons align and leave breathing room below the four-row list");
            savedList.Items.Clear();

            Invoke(window, "ShowSearchDialog");
            Invoke(window, "AddSearchResult", remembered);
            Invoke(window, "AddSearchResult", remembered with { Name = "发现的耳机" });
            var overlay = (Grid)window.FindName("SearchOverlay");
            Check(overlay.Visibility == Visibility.Visible && root.Effect is BlurEffect && Field<ListBox>(window, "searchResults").Items.Count == 1, "search popup blurs the app and updates live results without duplicates");
            Check(overlay.Opacity == 0 && root.Effect is BlurEffect { Radius: 0 }, "search starts transparent without an instant background blur");
            Check(ObserveTransition(400, () => overlay.Opacity > 0 && overlay.Opacity < 1 && root.Effect is BlurEffect { Radius: > 0 and < 10 }), "search uses the same gradual popup and blur transition as export");
            SettleTransitions(245);
            Render(window, "device-search", 1240, 800);
            var allViewports = Descendants<ScrollViewer>((DependencyObject)window.Content).ToArray();
            Check(allViewports.Length > 5 && allViewports.All(viewport => SmoothScrolling.GetEnabled(viewport) && (viewport == table || viewport.TemplatedParent is TextBox || !viewport.CanContentScroll)) && VirtualizingPanel.GetScrollUnit(Field<ItemsControl>(window, "peqRows")) == ScrollUnit.Pixel, "all lists and pages use smooth pixel scrolling; the virtual PEQ list and text hosts retain their native scroll contracts");
            using (var cancellation = new CancellationTokenSource())
            {
                typeof(MainWindow).GetField("operationCancel", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, cancellation);
                overlay.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Check(cancellation.IsCancellationRequested && !overlay.IsEnabled && !overlay.IsHitTestVisible, "clicking outside search cancels discovery immediately while the popup fades out");
                SettleTransitions();
                Check(overlay.Visibility == Visibility.Collapsed && root.Effect == null && root.IsEnabled, "search close removes the blur and restores page input after the fade");
                typeof(MainWindow).GetField("operationCancel", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, null);
            }
            using (var cancellation = new CancellationTokenSource())
            {
                Invoke(window, "ShowSearchDialog");
                typeof(MainWindow).GetField("operationCancel", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, cancellation);
                Invoke(window, "ChooseSearchResult", remembered);
                Check(cancellation.IsCancellationRequested && Field<AudioSink>(window, "pendingPairing") == remembered && !overlay.IsEnabled, "choosing a discovery result during fade-in cancels search before scheduling its connection");
                SettleTransitions();
                Check(overlay.Visibility == Visibility.Collapsed && root.Effect == null && root.IsEnabled, "dismissal during search fade-in still clears the popup and blur");
                typeof(MainWindow).GetField("operationCancel", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, null);
            }

            double? referenceLanguageWidth = null;
            foreach (var code in UiLanguage.Codes)
            {
                language.SelectedIndex = Array.IndexOf(UiLanguage.Codes, code);
                var languageSize = (Size)Invoke(window, "FitDevicePage", new Size(1920, 1080))!;
                referenceLanguageWidth ??= languageSize.Width;
                if (Math.Abs(languageSize.Width - referenceLanguageWidth.Value) > .01) throw new Exception("Language changed the established window width: " + code);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Field<List<Button>>(window, "nav")[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                foreach (var codec in DongleController.Codecs)
                {
                    Invoke(window, "DisplaySnapshot", data with { ActiveCodec = codec.Name });
                    Render(window, "minimum-" + code + "-" + codec.Code, 1060, 740);
                    if (((ScrollViewer)pages["USB"]).ScrollableHeight > .5) throw new Exception("Device page overflows at minimum size: " + code + "/" + codec.Name + ": " + ((ScrollViewer)pages["USB"]).ScrollableHeight);
                }
                var deviceLastRow = (FrameworkElement)((StackPanel)((ScrollViewer)pages["USB"]).Content).Children[1];
                double deviceBottom = deviceLastRow.TransformToAncestor(root).Transform(new Point(0, deviceLastRow.ActualHeight)).Y;
                foreach (var index in new[] { 1, 2, 3 })
                {
                    Field<List<Button>>(window, "nav")[index].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Render(window, "minimum-" + code + "-page-" + index, 1060, 740);
                    var selectedPage = (ScrollViewer)pages[index == 1 ? "PEQ" : index == 2 ? "SETTINGS" : "ABOUT"];
                    if (selectedPage.ScrollableHeight > .5) throw new Exception("Page overflows at minimum size: " + code + "/" + index + ": " + selectedPage.ScrollableHeight);
                    if (index == 2) VerifySettingsAlignment(window);
                    if (index == 1) VerifyPeqHeaderAlignment(window);
                    if (index is 1 or 3)
                    {
                        var lastCard = ((StackPanel)selectedPage.Content).Children.OfType<Border>().Last();
                        double cardBottom = lastCard.TransformToAncestor(root).Transform(new Point(0, lastCard.ActualHeight)).Y;
                        if (Math.Abs(cardBottom - deviceBottom) > 1) throw new Exception($"Last-card bottom differs from Device: {code}/{index}: {cardBottom} vs {deviceBottom}");
                    }
                    if (index is 2 or 3 && (Field<TextBlock>(window, "headerStatus").Visibility != Visibility.Collapsed || Field<StackPanel>(window, "peqHeaderStatus").Visibility != Visibility.Collapsed)) throw new Exception("Settings and About must not show header status");
                }
            }
            Check(true, "all languages preserve window width, aligned settings controls and fit all pages and eight codecs without page scrolling");
            language.SelectedIndex = 0;
            Check(window.ResizeMode == ResizeMode.NoResize && window.SizeToContent == SizeToContent.Height, "window cannot be manually resized and follows measured content height");
            Field<List<Button>>(window, "nav")[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Invoke(window, "DisplaySnapshot", data);
            Render(window, "device-content-height", 1240, 800);
            var devicePanel = (ScrollViewer)pages["USB"];
            var lowerCards = ((StackPanel)devicePanel.Content).Children[1];
            double Top(FrameworkElement element) => element.TransformToAncestor(root).Transform(new Point()).Y;
            double Bottom(FrameworkElement element) => element.TransformToAncestor(root).Transform(new Point(0, element.ActualHeight)).Y;
            var statusBorder = Descendants<Border>(root).Single(border => border.Child is DockPanel panel && panel.Children.Contains(Field<TextBlock>(window, "status")));
            Check(Math.Abs((Top((FrameworkElement)firstCard) - Bottom(Field<TextBlock>(window, "title"))) - (Top(statusBorder) - Bottom((FrameworkElement)lowerCards))) < 2, "space below device cards matches the gap between the title and first card");
            Check(!Descendants<TextBlock>(Field<Border>(window, "audioPanel")).Any(text => text.Text.StartsWith("当前：")) && Field<ComboBox>(window, "codecs").FocusVisualStyle == null && devicePanel.FocusVisualStyle == null, "duplicate current-codec text and dotted control focus styles are removed");
            foreach (var factor in new[] { 1d, 1.25, 1.5, 1.75, 2d })
            {
                var workArea = new Size(1920 / factor, 1080 / factor);
                var fitted = (Size)Invoke(window, "FitDevicePage", workArea)!;
                RenderElement((FrameworkElement)window.Content, "dpi-workarea-" + factor.ToString(System.Globalization.CultureInfo.InvariantCulture), (int)Math.Ceiling(fitted.Width), (int)Math.Ceiling(fitted.Height));
                if (fitted.Width > workArea.Width || fitted.Height > workArea.Height || devicePanel.ScrollableHeight > .5) throw new Exception("Content does not fit the scaled work area: " + factor);
            }
            Check(true, "DIP work-area models from 100 to 200 percent scaling keep the measured window and device content within bounds");

            var watcherType = typeof(MainWindow).Assembly.GetType("Questyle.Desktop.Services.DeviceChangeWatcher")!;
            var usbClass = Guid.Parse("A5DCBF10-6530-11D2-901F-00C04FB951ED");
            var hidClass = Guid.Parse("4D1E55B2-F16F-11CF-88CB-001111000030");
            var parseChange = watcherType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check(parseChange.Invoke(null, [0x8000, usbClass, @"\\?\usb#VID_0A12&PID_4007#verification"]) is DeviceInterfaceChange { Arrived: true, IsUsbInterface: true } && parseChange.Invoke(null, [0x8004, hidClass, @"\\?\hid#vid_0e6a&pid_4007#verification"]) is DeviceInterfaceChange { Arrived: false } && parseChange.Invoke(null, [0x8000, usbClass, @"\\?\usb#vid_1234&pid_4007#other"]) == null, "native notification parsing accepts both dongle IDs and filters unrelated adapters");
            var nativeChanges = new List<DeviceInterfaceChange>();
            using (var notificationSource = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Questyle.NotificationVerification") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 }))
            using (var nativeWatcher = (IDisposable)Activator.CreateInstance(watcherType, notificationSource, (Action<DeviceInterfaceChange>)nativeChanges.Add)!)
            {
                byte[] nativePath = System.Text.Encoding.Unicode.GetBytes(@"\\?\usb#vid_0a12&pid_4007#verification" + "\0");
                var notification = System.Runtime.InteropServices.Marshal.AllocHGlobal(28 + nativePath.Length);
                try
                {
                    System.Runtime.InteropServices.Marshal.WriteInt32(notification, 28 + nativePath.Length);
                    System.Runtime.InteropServices.Marshal.WriteInt32(notification, 4, 5); System.Runtime.InteropServices.Marshal.WriteInt32(notification, 8, 0);
                    System.Runtime.InteropServices.Marshal.StructureToPtr(usbClass, notification + 12, false);
                    System.Runtime.InteropServices.Marshal.Copy(nativePath, 0, notification + 28, nativePath.Length);
                    watcherType.GetMethod("HandleMessage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(nativeWatcher, [notificationSource.Handle, 0x0219, new IntPtr(0x8000), notification, false]);
                    Check(nativeChanges is [{ Arrived: true, IsUsbInterface: true }], "hidden message-only HWND registers device notifications and decodes a synthetic WM_DEVICECHANGE");
                }
                finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(notification); }
            }
            int refreshRequests = 0;
            Func<Task> queuedRefresh = () => { refreshRequests++; return Task.CompletedTask; };
            Invoke(window, "HandleDeviceChange", new DeviceInterfaceChange(false, true), queuedRefresh);
            Check(!Field<bool>(window, "usbDetected") && Field<TextBlock>(window, "usbName").Text == "未检测到蓝牙适配器" && refreshRequests == 1, "idle USB removal clears the adapter UI immediately without waiting for enumeration");
            var fakeHid = new HidDevice("verification", "QCC Dongle Pro2", 0x0a12, 0x4007, 1, 1, 64, 64, 0xbb, 0xcc);
            Func<Task<IReadOnlyList<HidDevice>>> inserted = () => Task.FromResult<IReadOnlyList<HidDevice>>([fakeHid]);
            Func<Task<IReadOnlyList<HidDevice>>> removed = () => Task.FromResult<IReadOnlyList<HidDevice>>([]);
            Func<Task> noRead = () => throw new Exception("Hidden verification window tried to take USB control");
            ((Task)Invoke(window, "RefreshUsbPresence", inserted, noRead)!).GetAwaiter().GetResult();
            Check(Field<bool>(window, "usbDetected") && Field<Button>(window, "readButton").Content.Equals("重新读取设备") && !window.IsVisible, "insertion refreshes detection even without reactivating the hidden window, without USB I/O");
            typeof(MainWindow).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Invoke(window, "HandleDeviceChange", new DeviceInterfaceChange(false, true), queuedRefresh);
            Check(Field<bool>(window, "usbDetected") && refreshRequests == 1 && Field<bool>(window, "presenceCheckPending"), "USB control-session notifications wait for release instead of clearing active state");
            typeof(MainWindow).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            typeof(MainWindow).GetField("awaitingInterfaceRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            ((Task)Invoke(window, "RefreshUsbPresence", removed, noRead)!).GetAwaiter().GetResult();
            Check(Field<bool>(window, "usbDetected"), "temporary HID absence after a successful control session preserves the current state");
            Invoke(window, "DisplaySnapshot", data);
            var beforeCancelSnapshot = Field<DongleSnapshot>(window, "snapshot");
            using (var searchCancellation = new CancellationTokenSource())
            {
                var searchProbe = new TaskCompletionSource<IReadOnlyList<HidDevice>>(); int searchProbes = 0;
                Func<Task<IReadOnlyList<HidDevice>>> probeDuringSearch = () => ++searchProbes == 1 ? searchProbe.Task : Task.FromResult<IReadOnlyList<HidDevice>>([]);
                var searchPresence = (Task)Invoke(window, "RefreshUsbPresence", probeDuringSearch, noRead)!;
                var searchCleanup = new TaskCompletionSource();
                Func<Task> simulatedSearch = () =>
                {
                    Invoke(window, "ShowSearchDialog");
                    typeof(MainWindow).GetField("operationCancel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, searchCancellation);
                    Invoke(window, "HandleDeviceChange", new DeviceInterfaceChange(false, true), queuedRefresh);
                    return searchCleanup.Task;
                };
                var canceledSearch = (Task)Invoke(window, "Run", simulatedSearch, "搜索耳机")!;
                Descendants<Button>((DependencyObject)window.FindName("SearchOverlay")).Single(button => Equals(button.Content, "取消搜索")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(searchCancellation.IsCancellationRequested && Field<bool>(window, "busy") && Field<AudioSink?>(window, "pendingPairing") == null && ReferenceEquals(beforeCancelSnapshot, Field<DongleSnapshot>(window, "snapshot")), "cancel without pairing waits for search cleanup while retaining the current connection");
                searchCleanup.SetCanceled(searchCancellation.Token);
                var cancelFrame = new DispatcherFrame(); canceledSearch.ContinueWith(_ => application.Dispatcher.BeginInvoke(() => cancelFrame.Continue = false)); Dispatcher.PushFrame(cancelFrame); canceledSearch.GetAwaiter().GetResult();
                typeof(MainWindow).GetField("operationCancel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
                searchProbe.SetResult([]);
                var searchPresenceFrame = new DispatcherFrame(); searchPresence.ContinueWith(_ => application.Dispatcher.BeginInvoke(() => searchPresenceFrame.Continue = false)); Dispatcher.PushFrame(searchPresenceFrame); searchPresence.GetAwaiter().GetResult();
                SettleTransitions();
                Check(searchProbes == 2 && !Field<bool>(window, "busy") && Field<bool>(window, "usbDetected") && ReferenceEquals(beforeCancelSnapshot, Field<DongleSnapshot>(window, "snapshot")) && Field<Border>(window, "audioPanel").IsEnabled && Field<Button>(window, "disconnectButton").IsEnabled && Field<ListBox>(window, "sinks").SelectedItem is AudioSink selectedSink && selectedSink.Address == data.HeadphoneAddress, "canceled search preserves connection and controls during temporary empty HID enumeration");
                ((Task)Invoke(window, "RefreshUsbPresence", inserted, noRead)!).GetAwaiter().GetResult();
                Check(!Field<bool>(window, "awaitingInterfaceRecovery") && ReferenceEquals(beforeCancelSnapshot, Field<DongleSnapshot>(window, "snapshot")), "HID return after canceled search finishes recovery without rereading or pairing");
            }
            foreach (bool previouslyConnected in new[] { false, true })
            {
                var beforeFailure = previouslyConnected ? data : data with { HeadphoneAddress = "00:00:00:00:00:00", ActiveCodec = "未连接" };
                Invoke(window, "DisplaySnapshot", beforeFailure);
                int recordsBeforeFailure = Field<HeadphoneHistory>(window, "history").Headphones.Count;
                var failedProbe = new TaskCompletionSource<IReadOnlyList<HidDevice>>(); int failureProbes = 0;
                Func<Task<IReadOnlyList<HidDevice>>> probeAfterFailure = () => ++failureProbes == 1 ? failedProbe.Task : Task.FromResult<IReadOnlyList<HidDevice>>([]);
                var failurePresence = (Task)Invoke(window, "RefreshUsbPresence", probeAfterFailure, noRead)!;
                Func<Task<DongleSnapshot>> failedSession = () =>
                {
                    Invoke(window, "HandleDeviceChange", new DeviceInterfaceChange(false, true), queuedRefresh);
                    return Task.FromException<DongleSnapshot>(new IOException("simulated connection failure"));
                };
                Func<Task> failedPairing = () => (Task)Invoke(window, "ConnectHeadphone", otherSink, failedSession)!;
                ((Task)Invoke(window, "Run", failedPairing, "连接耳机")!).GetAwaiter().GetResult();
                failedProbe.SetResult([]);
                var failureFrame = new DispatcherFrame(); failurePresence.ContinueWith(_ => application.Dispatcher.BeginInvoke(() => failureFrame.Continue = false)); Dispatcher.PushFrame(failureFrame); failurePresence.GetAwaiter().GetResult();
                Check(failureProbes == 2 && Field<bool>(window, "usbDetected") && ReferenceEquals(beforeFailure, Field<DongleSnapshot>(window, "snapshot")) && savedList.IsEnabled && Field<Border>(window, "audioPanel").IsEnabled == previouslyConnected && disconnectAction.IsEnabled == previouslyConnected,
                    $"connection failure (connected={previouslyConnected}) preserves the list and appropriate controls during temporary HID absence");
                Check(Field<HeadphoneHistory>(window, "history").Headphones.Count == recordsBeforeFailure && Field<TextBlock>(window, "status").Text == "simulated connection failure",
                    "failed connection retains its error without saving or displaying an unconfirmed headphone");
                ((Task)Invoke(window, "RefreshUsbPresence", inserted, noRead)!).GetAwaiter().GetResult();
                bool nextOperationRan = false;
                ((Task)Invoke(window, "Run", (Func<Task>)(() => { nextOperationRan = true; return Task.CompletedTask; }), "test after failed connection")!).GetAwaiter().GetResult();
                Check(nextOperationRan && savedList.IsEnabled && ReferenceEquals(beforeFailure, Field<DongleSnapshot>(window, "snapshot")), "HID return allows the next operation without a manual reread");
            }
            Invoke(window, "HandleDeviceChange", new DeviceInterfaceChange(false, true), queuedRefresh);
            Check(!Field<bool>(window, "usbDetected"), "an idle physical USB removal still clears state during interface recovery");
            var initialEnumeration = new TaskCompletionSource<IReadOnlyList<HidDevice>>(); int enumerations = 0;
            Func<Task<IReadOnlyList<HidDevice>>> racingEnumeration = () => ++enumerations == 1 ? initialEnumeration.Task : Task.FromResult<IReadOnlyList<HidDevice>>([]);
            var pendingPresence = (Task)Invoke(window, "RefreshUsbPresence", racingEnumeration, noRead)!;
            Invoke(window, "HandleDeviceChange", new DeviceInterfaceChange(false, true), queuedRefresh);
            initialEnumeration.SetResult([fakeHid]);
            var presenceFrame = new DispatcherFrame(); pendingPresence.ContinueWith(_ => application.Dispatcher.BeginInvoke(() => presenceFrame.Continue = false)); Dispatcher.PushFrame(presenceFrame); pendingPresence.GetAwaiter().GetResult();
            Check(enumerations == 2 && !Field<bool>(window, "usbDetected"), "an outdated enumeration cannot resurrect an adapter after its removal event");
            ((Task)Invoke(window, "RefreshUsbPresence", inserted, noRead)!).GetAwaiter().GetResult();

            var busyEnumeration = new TaskCompletionSource<IReadOnlyList<HidDevice>>();
            Func<Task<IReadOnlyList<HidDevice>>> delayedWhileBusy = () => busyEnumeration.Task;
            var interruptedPresence = (Task)Invoke(window, "RefreshUsbPresence", delayedWhileBusy, noRead)!;
            typeof(MainWindow).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            busyEnumeration.SetResult([]);
            var busyFrame = new DispatcherFrame(); interruptedPresence.ContinueWith(_ => application.Dispatcher.BeginInvoke(() => busyFrame.Continue = false)); Dispatcher.PushFrame(busyFrame); interruptedPresence.GetAwaiter().GetResult();
            Check(Field<bool>(window, "usbDetected") && Field<bool>(window, "presenceCheckPending"), "a presence probe that overlaps a new USB operation defers its result until release");
            typeof(MainWindow).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            ((Task)Invoke(window, "RefreshUsbPresence", inserted, noRead)!).GetAwaiter().GetResult();
            var exitEnumeration = new TaskCompletionSource<IReadOnlyList<HidDevice>>();
            Func<Task<IReadOnlyList<HidDevice>>> delayedUntilExit = () => exitEnumeration.Task;
            var exitingPresence = (Task)Invoke(window, "RefreshUsbPresence", delayedUntilExit, noRead)!;
            typeof(MainWindow).GetField("exitRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            exitEnumeration.SetResult([]);
            var exitFrame = new DispatcherFrame(); exitingPresence.ContinueWith(_ => application.Dispatcher.BeginInvoke(() => exitFrame.Continue = false)); Dispatcher.PushFrame(exitFrame); exitingPresence.GetAwaiter().GetResult();
            Check(Field<bool>(window, "usbDetected"), "a probe completed after exit begins does not update disposed window state");
            typeof(MainWindow).GetField("exitRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);

            var memoryPath = Path.Combine(AppPaths.DataDirectory, "automatic-audio-memory.json"); File.Delete(memoryPath);
            var memory = new HeadphoneAudioMemory(memoryPath);
            var headphoneA = data with { Ldac = 3, GameMode = true };
            var headphoneB = data with { HeadphoneAddress = "01:02:03:04:05:07", ActiveCodec = "aptX Adaptive", Adaptive = 2, GameMode = false };
            var headphoneC = data with { HeadphoneAddress = "01:02:03:04:05:08", ActiveCodec = "LC3", Auracast = 1 };
            memory.RememberConfirmed(headphoneA, true); memory.RememberConfirmed(headphoneB); memory.RememberConfirmed(headphoneC, null, true);
            var reloadMemory = new HeadphoneAudioMemory(memoryPath); reloadMemory.Load();
            Check(reloadMemory.Find(headphoneA.HeadphoneAddress)?.Codec == 8 && reloadMemory.Find(headphoneB.HeadphoneAddress)?.Codec == 4 && reloadMemory.DisableMicrophone == true && reloadMemory.Auracast == true, "protocols and modes persist per headphone; microphone and Auracast remain global");
            typeof(MainWindow).GetField("audioMemory", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, reloadMemory);
            DongleSnapshot AudioOperation(AudioStateTransport transport, Func<DongleController.Session, DongleSnapshot> action)
            {
                Func<Func<DongleController.Session, DongleSnapshot>, Task<DongleSnapshot>> run = work =>
                {
                    transport.BeginSession();
                    return Task.FromResult(work(new DongleController.Session(transport, _ => { }, CancellationToken.None)));
                };
                try { return ((Task<DongleSnapshot>)Invoke(window, "RunAudioOperation", action, run)!).GetAwaiter().GetResult(); }
                catch (TargetInvocationException error) when (error.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            }
            DongleSnapshot Restore(AudioStateTransport transport) => AudioOperation(transport, session => (DongleSnapshot)Invoke(window, "RestoreSavedAudio", session, transport.State, true)!);
            var restoreA = new AudioStateTransport(headphoneA with { ActiveCodec = "SBC", Ldac = 1, GameMode = false });
            var restoredA = Restore(restoreA);
            Check(restoredA.ActiveCodec == "LDAC" && restoredA.Ldac == 3 && restoredA.GameMode == true && !restoreA.Writes.Any(command => command.Item1 == 0x22), "automatic restore applies headphone A protocol and mode plus the global microphone without Auracast writes");
            var restoreB = new AudioStateTransport(headphoneB with { ActiveCodec = "LDAC", Adaptive = 1, GameMode = false });
            var restoredB = Restore(restoreB);
            Check(restoredB.ActiveCodec == "aptX Adaptive" && restoredB.Adaptive == 2 && restoredB.GameMode == true, "headphone B restores its own protocol and mode while retaining the same global microphone selection");
            var restoreC = new AudioStateTransport(headphoneC with { Auracast = 0, GameMode = false });
            var restoredC = Restore(restoreC);
            Check(restoredC.Auracast == 1 && !restoreC.Writes.Any(command => command.Item1 == 0x11), "LC3 restores the global Auracast selection and never sends a microphone command");
            foreach (byte[] reply in new byte[][] { [], [0, 1], [0x83] })
            {
                var unknownAura = new AudioStateTransport(headphoneC) { AuracastQueryReply = reply };
                var unknownSnapshot = new DongleController.Session(unknownAura, _ => { }, CancellationToken.None).Snapshot();
                Check(unknownSnapshot.Auracast is not (0 or 1), "malformed Auracast query remains unknown: " + Convert.ToHexString(reply));
                try { AudioOperation(unknownAura, session => (DongleSnapshot)Invoke(window, "ApplyAudioChoices", session, unknownAura.State, false, null!, null!, null!, false)!); throw new Exception("Unknown Auracast readback was accepted"); }
                catch (IOException) { Check(reloadMemory.Auracast == true, "unknown Auracast readback cannot confirm or save the requested off state: " + Convert.ToHexString(reply)); }
            }
            var editedMode = new AudioStateTransport(headphoneA with { Ldac = 1, GameMode = true });
            var editedResult = AudioOperation(editedMode, session => (DongleSnapshot)Invoke(window, "ApplyAudioChoices", session, editedMode.State, false, (byte)2, null!, false, null!)!);
            Check(editedResult.Ldac == 2 && editedResult.GameMode == false && !editedMode.Writes.Any(command => command.Item1 == 0x50), "explicit preview choices override saved mode and global microphone settings after a codec switch");
            reloadMemory.RememberConfirmed(headphoneA, false, true);
            var restartingMicrophone = new AudioStateTransport(headphoneA with { GameMode = false }) { RestartOnGame = true };
            var microphoneResult = AudioOperation(restartingMicrophone, session => (DongleSnapshot)Invoke(window, "ApplyAudioChoices", session, restartingMicrophone.State, false, null!, null!, true, null!)!);
            Check(microphoneResult.GameMode == true && restartingMicrophone.SessionCommands.Count == 2 && restartingMicrophone.SessionCommands[0].Last() == (0x11, 2) && restartingMicrophone.SessionCommands[1].All(command => command.Item1 != 0x11 || command.Item2 == 255) && reloadMemory.DisableMicrophone == true, "microphone ACK ends the old session and a new readback confirms and saves the enabled state without reverting it");
            reloadMemory.RememberConfirmed(headphoneA, false, true);
            var reconnectingMicrophone = new AudioStateTransport(headphoneA with { GameMode = false }) { RestartOnGame = true, HeadphoneAbsentAfterGame = 2 };
            var reconnectedResult = AudioOperation(reconnectingMicrophone, session => (DongleSnapshot)Invoke(window, "ApplyAudioChoices", session, reconnectingMicrophone.State, false, null!, null!, true, null!)!);
            Check(reconnectedResult.GameMode == true && reconnectedResult.HeadphoneAddress == headphoneA.HeadphoneAddress && reconnectingMicrophone.SessionCommands.Count == 2 && reconnectingMicrophone.Writes.SequenceEqual(new[] { ((byte)0x11, (byte)2) }) && reloadMemory.DisableMicrophone == true, "readback waits in the new session while the headphone reconnects after the microphone restart");

            reloadMemory.RememberConfirmed(headphoneA, false, true);
            var delayedMicrophone = new AudioStateTransport(headphoneA with { GameMode = false }) { RestartOnGame = true };
            int microphoneRuns = 0;
            Func<Func<DongleController.Session, DongleSnapshot>, Task<DongleSnapshot>> delayedRun = work =>
            {
                if (++microphoneRuns == 2) return Task.FromException<DongleSnapshot>(new IOException("simulated interface recovery failure"));
                delayedMicrophone.BeginSession();
                return Task.FromResult(work(new DongleController.Session(delayedMicrophone, _ => { }, CancellationToken.None)));
            };
            Func<DongleController.Session, DongleSnapshot> applyMicrophone = session => (DongleSnapshot)Invoke(window, "ApplyAudioChoices", session, delayedMicrophone.State, false, null!, null!, true, null!)!;
            try { ((Task<DongleSnapshot>)Invoke(window, "RunAudioOperation", applyMicrophone, delayedRun)!).GetAwaiter().GetResult(); throw new Exception("Failed recovery read was accepted"); }
            catch (IOException) { Check(microphoneRuns == 2 && Field<object?>(window, "pendingMicrophoneChange") != null && reloadMemory.DisableMicrophone == false, "failed restart read keeps the acknowledged request pending without saving success or automatically retrying"); }
            try { ((Task)Invoke(window, "ApplySound")!).GetAwaiter().GetResult(); throw new Exception("Unconfirmed state was applied"); }
            catch (InvalidOperationException) { Check(Field<object?>(window, "pendingMicrophoneChange") != null && reloadMemory.DisableMicrophone == false, "an unresolved microphone readback cannot be overwritten by applying stale UI choices"); }
            var manuallyRecovered = ((Task<DongleSnapshot>)Invoke(window, "ReadMicrophoneReadback", delayedRun)!).GetAwaiter().GetResult();
            Check(manuallyRecovered.GameMode == true && microphoneRuns == 3 && delayedMicrophone.Writes.SequenceEqual(new[] { ((byte)0x11, (byte)2) }) && reloadMemory.DisableMicrophone == true && Field<object?>(window, "pendingMicrophoneChange") == null, "the next manual read confirms the pending microphone choice instead of restoring the old disabled preference");

            reloadMemory.RememberConfirmed(headphoneA, false, true);
            var ignoredMicrophone = new AudioStateTransport(headphoneA with { GameMode = false }) { RestartOnGame = true, IgnoreWrites = true };
            try { AudioOperation(ignoredMicrophone, session => (DongleSnapshot)Invoke(window, "ApplyAudioChoices", session, ignoredMicrophone.State, false, null!, null!, true, null!)!); throw new Exception("Unchanged microphone readback was accepted"); }
            catch (IOException) { Check(reloadMemory.DisableMicrophone == false && Field<object?>(window, "pendingMicrophoneChange") == null, "ACK without a matching microphone readback is reported and never saved as success"); }
            reloadMemory.RememberConfirmed(headphoneA, true, true);
            var unavailableMode = new AudioStateTransport(headphoneA with { Ldac = 0x83 });
            try { Invoke(window, "ApplyAudioChoices", new DongleController.Session(unavailableMode, _ => { }, CancellationToken.None), unavailableMode.State, false, (byte)2, null!, null!, null!); throw new Exception("Unknown mode accepted"); }
            catch (TargetInvocationException error) when (error.InnerException is IOException) { Check(unavailableMode.Writes.Count == 0, "an unavailable mode after switching is reported without turning an unknown reply into a setting"); }
            var disconnected = new AudioStateTransport(headphoneA with { HeadphoneAddress = "00:00:00:00:00:00" }); Restore(disconnected);
            Check(disconnected.Commands.Count == 0, "disconnected snapshots trigger no automatic setting commands");
            var unsupported = new AudioStateTransport(headphoneA with { ActiveCodec = "SBC", HeadphoneCodecs = [new CodecOption(11, "SBC")] }); Restore(unsupported);
            Check(unsupported.Writes.Count == 0 && reloadMemory.Find(headphoneA.HeadphoneAddress)?.Codec == 8, "unavailable saved codec is not sent or replaced with a fabricated successful preference");
            var unknownGame = new AudioStateTransport(headphoneA with { GameMode = null }); Restore(unknownGame);
            Check(!unknownGame.Writes.Any(command => command.Item1 == 0x11), "unknown microphone query does not become an automatic write");
            try { Restore(new AudioStateTransport(headphoneA with { ActiveCodec = "SBC" }) { RejectWrites = true }); throw new Exception("Rejected restore was accepted"); }
            catch (IOException) { Check(true, "automatic restore reports a rejected ACK"); }
            try { Restore(new AudioStateTransport(headphoneA with { ActiveCodec = "SBC" }) { IgnoreWrites = true }); throw new Exception("Unchanged readback was accepted"); }
            catch (IOException) { Check(true, "automatic restore requires an actual matching readback after an ACK"); }
            reloadMemory.Remove(headphoneA.HeadphoneAddress);
            Check(reloadMemory.Find(headphoneA.HeadphoneAddress) == null && reloadMemory.Find(headphoneB.HeadphoneAddress) != null && reloadMemory.DisableMicrophone == true && reloadMemory.Auracast == true, "removing headphone-specific memory preserves other headphones and both global choices");
            closeButton.Tag = "Hover";
            window.Close();
            Check(!closed && !window.IsVisible && window.WindowStyle == WindowStyle.None && closeButton.Tag == null, "borderless window close hides the UI without ending its lifetime");
            var completion = new TaskCompletionSource();
            var running = (Task)Invoke(window, "Run", (Func<Task>)(() => completion.Task), "test operation")!;
            window.RequestExit();
            Check(!closed, "tray exit waits for the active operation");
            completion.SetResult();
            var frame = new DispatcherFrame();
            running.ContinueWith(_ => application.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            running.GetAwaiter().GetResult();
            Check(closed && File.Exists(Path.Combine(AppPaths.DataDirectory, "last-session.log")), "operation completion finishes exit and saves the session log");

            using var activated = new ManualResetEventSlim();
            var instanceName = "Questyle.UiVerification." + Guid.NewGuid().ToString("N");
            using (var primary = new SingleInstance(instanceName, activated.Set))
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--activate"); start.ArgumentList.Add(instanceName);
                using var child = Process.Start(start)!;
                if (!child.WaitForExit(5000)) throw new Exception("Activation client did not exit");
                Check(primary.IsPrimary && child.ExitCode == 0 && activated.Wait(2000), "second process exits and signals the existing instance");
            }
            using (var replacement = new SingleInstance(instanceName, () => { }))
                Check(replacement.IsPrimary, "single-instance resources are released for the next launch");
            Console.WriteLine($"{count} USB UI and lifecycle checks passed. Offscreen renders: {AppPaths.DataDirectory}");
        }
        finally { if (!closed) window.RequestExit(); application.Shutdown(); }
    }

    private static void VerifyPeqEditing(MainWindow window, PeqSettings definition)
    {
        Field<ScrollViewer>(window, "peqTable").UpdateLayout();
        var preamp = Field<TextBox>(window, "peqPreamp");
        var boxes = Descendants<TextBox>(Field<ScrollViewer>(window, "peqTable")).Take(3).ToArray();
        var inputs = new[] { (Box: preamp, Parameter: "Preamp", Minimum: "-20"), (Box: boxes[0], Parameter: "Frequency", Minimum: "10"), (Box: boxes[1], Parameter: "Gain", Minimum: "-20"), (Box: boxes[2], Parameter: "Q", Minimum: "0.1") };
        foreach (var code in UiLanguage.Codes)
        {
            UiLanguage.Current.Select(code);
            foreach (var (box, parameter, _) in inputs)
            {
                Invoke(window, "HidePeqRangeHint");
                box.SelectAll();
                string original = box.Text;
                foreach (var text in new[] { "a", "汉", "１２", "1e3", "1\n", "--1" })
                {
                    var composition = new TextComposition(InputManager.Current, box, text);
                    var input = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
                    box.RaiseEvent(input);
                    if (!input.Handled || box.Text != original || Field<Adorner?>(window, "peqRangeHint") != null) throw new Exception($"Invalid composed text was not silently rejected: {code}/{parameter}/{text}");
                }
                if (InputMethod.GetIsInputMethodEnabled(box)) throw new Exception("Numeric editor still accepts IME composition");
                box.Select(0, 1); box.Text = "非法abc";
                if (box.Text != original || Field<Adorner?>(window, "peqRangeHint") != null) throw new Exception("Text mutation bypassed numeric validation");
                var paste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "abc"), false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent };
                box.RaiseEvent(paste);
                if (!paste.CommandCancelled || box.Text != original) throw new Exception("Invalid paste was accepted");
            }
        }
        UiLanguage.Current.Select("zh-CN");
        Check(true, "all three UI languages reject illegal composed text, direct mutations and paste silently with numeric IME composition disabled");
        var responseVisual = Field<Canvas>(window, "peqCurve").Children.OfType<PeqCurveVisual>().Single();
        var lastValidCurve = responseVisual.Points;
        string oldGain = boxes[1].Text; boxes[1].Text = "-";
        Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
        Check(ReferenceEquals(lastValidCurve, responseVisual.Points) && !Field<Canvas>(window, "peqCurve").Children.OfType<TextBlock>().Any(text => text.Text == "参数无效"), "incomplete numeric prefixes retain the last valid curve without an error flash");
        boxes[1].Text = oldGain; Invoke(window, "RenderPeqFrame", null!, EventArgs.Empty);
        foreach (var (box, parameter, minimum) in inputs)
        {
            box.SelectAll(); box.SelectedText = "";
            if (box.Text != minimum) throw new Exception($"Clearing {parameter} did not select its minimum: {box.Text}");
        }
        Check(true, "deleting all text immediately replaces each numeric field with its own minimum");
        boxes[0].Text = "10"; boxes[0].Select(1, 1); boxes[0].SelectedText = "";
        boxes[2].Text = "0.1"; boxes[2].Select(2, 1); boxes[2].SelectedText = "";
        Check(boxes[0].Text == "10" && boxes[2].Text == "0.1", "deleting below frequency and Q minimums clamps instead of leaving an out-of-range value");
        boxes[0].SelectAll();
        var outOfRangePaste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "20001"), false, DataFormats.UnicodeText) { RoutedEvent = DataObject.PastingEvent };
        boxes[0].RaiseEvent(outOfRangePaste);
        Check(outOfRangePaste.CommandCancelled && boxes[0].Text == "10" && Field<Adorner>(window, "peqRangeHint").AdornedElement == boxes[0], "out-of-range numeric paste is rejected and shows a range hint above its field");
        Invoke(window, "HidePeqRangeHint");
        preamp.Text = "10"; preamp.SelectAll();
        var numericComposition = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, preamp, "10.1")) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
        preamp.RaiseEvent(numericComposition);
        Check(numericComposition.Handled && preamp.Text == "10" && Field<Adorner>(window, "peqRangeHint").AdornedElement == preamp, "numeric text above the allowed maximum shows a hint without changing the input");
        Invoke(window, "HidePeqRangeHint");
        foreach (var (box, parameter, _) in inputs)
        {
            Invoke(window, "SetPeqInputValue", box, parameter, -100000d);
            if (Field<Adorner>(window, "peqRangeHint").AdornedElement != box) throw new Exception("Range hint did not follow the bounded numeric adjustment");
        }
        Check(true, "bounded numeric adjustments show their matching range hint and clamp to their lower limit");
        Invoke(window, "SetPeqInputValue", preamp, "Preamp", 11d);
        Check(ObserveTransition(400, () => Field<Adorner>(window, "peqRangeHint") is { Opacity: > 0 and < 1, IsHitTestVisible: false }), "range hint fades in without intercepting pointer input");
        var hint = Field<Adorner>(window, "peqRangeHint");
        SettleTransitions(185);
        Render(window, "peq-preamp-range-hint", 1240, 800);
        var rangeLabel = Descendants<Border>(hint).Single();
        Check(rangeLabel.TransformToAncestor(hint).TransformBounds(new Rect(rangeLabel.RenderSize)).Bottom < 0 && Descendants<TextBlock>(hint).Single().Text.Contains("-20") && Descendants<TextBlock>(hint).Single().Text.Contains("10"), "range hint sits above the input and displays its actual minimum and maximum");
        SettleTransitions(2150);
        Check(Field<Adorner?>(window, "peqRangeHint") == null, "range hint fades out and releases its adorner after two seconds");
        Invoke(window, "SetPeq", definition with { Preamp = 7 });
    }

    private static void VerifyUpdates(MainWindow window)
    {
        object Release(string tag, bool prerelease, string published, string body = "", bool draft = false) => new
        {
            tag_name = tag, prerelease, published_at = published, body, draft,
            assets = new[] { new { name = "questyle-windows-x64.zip", state = "uploaded", url = "https://api.github.com/repos/CareyChi/Questyle-Windows/releases/assets/1", size = 3, digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("zip"u8)).ToLowerInvariant() } }
        };
        var json = JsonSerializer.Serialize(new[]
        {
            Release("v0.2.0", false, "2026-10-29T05:00:00Z"),
            Release("v0.2.1+1a2b3c4", true, "2026-10-29T06:00:00Z", "App-Version: 0.2.0"),
            Release("dev-build-0f0f0f0", true, "2026-10-29T11:00:00Z", "App-Version: 0.2.0"),
            Release("dev-build-1a2b3c4d", true, "2026-10-29T12:00:00Z", "App-Version: 0.2.0"),
            Release("v0.2.1+1a2b3c4d", true, "2026-10-29T10:00:00Z", "App-Version: 0.2.0"),
            Release("v9.0.0", false, "2026-10-29T07:00:00Z", draft: true),
            Release("dev-build-77", true, "2026-10-29T09:00:00Z", "App-Version: 0.2.0"),
            Release("unrecognized", true, "2026-10-29T08:00:00Z")
        });
        using var catalog = JsonDocument.Parse(json);
        var stable = UpdateChecker.SelectRelease(catalog.RootElement, UpdateChannel.Stable)!;
        var development = UpdateChecker.SelectRelease(catalog.RootElement, UpdateChannel.Development)!;
        Check(stable.DisplayName == "New version: 0.2.0" && development.DisplayName == "Dev build 1a2b3c4", "release channels select stable or the newest published commit-identified development release and reject drafts, numbered and unknown tags");
        Check(development.Version == new Version(0, 2, 0), "the development update uses App-Version rather than the next-patch sorting tag");
        var olderDev = new InstalledVersion(new Version(0, 2, 0, 0), "0f0f0f0", DateTimeOffset.Parse("2026-10-29T04:00:00Z"));
        var sameDev = olderDev with { BuildCommit = "1a2b3c4", BuiltAt = DateTimeOffset.Parse("2026-10-29T06:00:00Z") };
        Check(development.IsNewerThan(olderDev) && !development.IsNewerThan(sameDev) && stable.IsNewerThan(olderDev) && !stable.IsNewerThan(sameDev), "version comparison handles dev build numbers and a stable release newer than the installed dev build without reporting older releases");
        var installedStable = new InstalledVersion(new Version(0, 2, 0, 0), "", DateTimeOffset.Parse("2026-10-29T05:30:00Z"));
        Check(olderDev.Display == "0.2.0_0f0f0f0" && installedStable.Display == "0.2.0", "development builds show their base version followed by the commit; stable builds show the version alone");
        var olderStable = stable with { Version = new Version(0, 1, 0) };
        Check(stable.IsOfferedTo(sameDev, UpdateChannel.Stable) && development.IsOfferedTo(installedStable, UpdateChannel.Development)
            && (development with { Version = new Version(0, 1, 0), BuildCommit = "0c0c0c0" }).IsOfferedTo(installedStable, UpdateChannel.Development),
            "after switching channels the selected channel's latest release is offered even when it is not newer");
        Check(!development.IsOfferedTo(sameDev, UpdateChannel.Development) && !stable.IsOfferedTo(installedStable, UpdateChannel.Development) && !olderStable.IsOfferedTo(installedStable, UpdateChannel.Stable),
            "the installed release is never offered again and older releases are not offered within the installed channel");
        using (var checker = new UpdateChecker(new UpdateResponseHandler(request =>
        {
            if (request.RequestUri!.Host != "api.github.com" || !request.Headers.UserAgent.Any()) throw new Exception("Update request is missing its API identity");
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        })))
            Check(checker.CheckAsync(UpdateChannel.Development, CancellationToken.None).GetAwaiter().GetResult()!.BuildCommit == "1a2b3c4", "update HTTP client parses a simulated GitHub response without real network access");
        using (var checker = new UpdateChecker(new UpdateResponseHandler(_ => new(HttpStatusCode.Forbidden))))
        {
            try { checker.CheckAsync(UpdateChannel.Stable, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Failed HTTP request accepted"); }
            catch (HttpRequestException) { Check(true, "HTTP errors are not reported as an up-to-date success"); }
        }
        var automatic = Field<CheckBox>(window, "automaticUpdates");
        Check(automatic.IsChecked == true && UpdateSettings.Load().Channel == UpdateChannel.Stable && Field<DispatcherTimer>(window, "updateTimer").Interval == TimeSpan.FromMinutes(5), "update checks default to automatic stable checks at five-minute intervals");
        automatic.IsChecked = false; automatic.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(!UpdateSettings.Load().Automatic && !Field<DispatcherTimer>(window, "updateTimer").IsEnabled, "automatic checks can be disabled and the choice is persisted");
        Invoke(window, "SelectUpdateChannel", UpdateChannel.Development); SettleTransitions();
        Check(UpdateSettings.Load().Channel == UpdateChannel.Development && Field<TranslateTransform>(window, "updateChannelIndicator").X > 0, "the update channel selector slides and persists independently of automatic checking");
        var checkField = typeof(MainWindow).GetField("checkUpdateRelease", BindingFlags.NonPublic | BindingFlags.Instance)!;
        checkField.SetValue(window, (Func<UpdateChannel, CancellationToken, Task<UpdateRelease?>>)((_, _) => Task.FromResult<UpdateRelease?>(stable)));
        ((Task)Invoke(window, "CheckUpdatesAsync", true)!).GetAwaiter().GetResult();
        var badge = Field<Border>(window, "newVersionBadge");
        Check(badge.Visibility == Visibility.Visible && Field<TextBlock>(window, "updatePopupTitle").Text == "New version: 0.2.0" && !Field<Popup>(window, "updatePopup").IsOpen, "manual checking works with automatic checks off and shows a badge without opening a window");
        Check(Field<TextBlock>(window, "updateCheckStatus").Text == UiLanguage.Current["发现新版本"] + " v0.2.0", "manual update results append the channel release version rather than the installed version");
        var footer = Field<Border>(window, "versionHoverArea"); footer.UpdateLayout();
        var versionRow = (StackPanel)footer.Child;
        Check(versionRow.HorizontalAlignment == HorizontalAlignment.Center && footer.HorizontalAlignment == HorizontalAlignment.Center && footer.Padding.Left > 0, "the version and New badge stay centered together inside a larger hover target");
        // The underscore of a development build lies below the baseline, so ink is measured on the version alone.
        var versionText = (TextBlock)versionRow.Children[0]; var shownVersion = versionText.Text;
        versionText.Text = "v" + InstalledVersion.Current.Version.ToString(3); footer.UpdateLayout();
        var rowBitmap = new RenderTargetBitmap((int)Math.Ceiling(versionRow.ActualWidth * 6), (int)Math.Ceiling(versionRow.ActualHeight * 6), 576, 576, PixelFormats.Pbgra32);
        var rowVisual = new DrawingVisual();
        using (var context = rowVisual.RenderOpen()) context.DrawRectangle(new VisualBrush(versionRow), null, new Rect(0, 0, versionRow.ActualWidth, versionRow.ActualHeight));
        rowBitmap.Render(rowVisual);
        int rowWidth = rowBitmap.PixelWidth, rowHeight = rowBitmap.PixelHeight; var rowPixels = new byte[rowWidth * rowHeight * 4]; rowBitmap.CopyPixels(rowPixels, rowWidth * 4, 0);
        int badgeStart = (int)(((FrameworkElement)versionRow.Children[0]).ActualWidth * 6) + 3, versionTop = rowHeight, versionBottom = 0, newTop = rowHeight, newBottom = 0;
        for (int y = 0; y < rowHeight; y++)
            for (int x = 0; x < rowWidth; x++)
            {
                int i = (y * rowWidth + x) * 4;
                // Version ink is grey on transparent; the badge text is white on the accent fill.
                if (x < badgeStart && rowPixels[i + 3] > 128) { versionTop = Math.Min(versionTop, y); versionBottom = Math.Max(versionBottom, y); }
                if (x >= badgeStart && rowPixels[i + 3] == 255 && rowPixels[i + 1] > 200) { newTop = Math.Min(newTop, y); newBottom = Math.Max(newBottom, y); }
            }
        Check(Math.Abs((versionTop + versionBottom) - (newTop + newBottom)) / 12d <= .75, "the version text and New badge text share one visual center line");
        versionText.Text = "v0.1.0_4c0701e"; footer.UpdateLayout();
        Check(versionRow.ActualWidth >= versionRow.DesiredSize.Width && versionText.ActualHeight < 30 && footer.TranslatePoint(new Point(14 + versionRow.ActualWidth, 0), (UIElement)window.Content).X <= 210, "a development build version fits on one line beside the New badge inside the sidebar");
        versionText.Text = shownVersion; footer.UpdateLayout();
        var popupField = typeof(MainWindow).GetField("updatePopup", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var originalPopup = Field<Popup>(window, "updatePopup");
        var popupContent = (FrameworkElement)originalPopup.Child;
        originalPopup.Child = null;
        var hoverPopup = new OffscreenPopup { Child = popupContent, Resources = window.Resources };
        popupField.SetValue(window, hoverPopup);
        try
        {
            Invoke(window, "SetAvailableUpdate", (object)null!);
            footer.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Check(!hoverPopup.OpenRequested && !StyleTransitions.GetDropDownOpen(hoverPopup) && badge.Visibility == Visibility.Collapsed, "hovering without a detected update never opens the version popup");
            Invoke(window, "SetAvailableUpdate", stable);
            footer.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Check(hoverPopup.OpenRequested && popupContent.Opacity == 0, "hovering with a detected update opens the popup starting from transparent");
            Check(ObserveTransition(300, () => popupContent.Opacity is > 0 and < 1), "the update popup has intermediate fade-in frames");
            SettleTransitions(145);
            Check(popupContent.Opacity == 1 && hoverPopup.IsHitTestVisible, "the update popup settles fully visible and accepts pointer input");
            footer.UpdateLayout();
            var footerGrid = (FrameworkElement)footer.Parent;
            Check(footer.Padding == new Thickness(14) && footer.Margin.Bottom == 0, "all four sides share the expanded bottom hover distance without moving the version text");
            foreach (double width in new[] { 158d, 174d, 220d })
            {
                var position = originalPopup.CustomPopupPlacementCallback(new Size(width, 108), footer.RenderSize, new Point()).Single().Point;
                Check(originalPopup.Placement == PlacementMode.Custom && originalPopup.PlacementTarget == footer && Math.Abs(position.X + width / 2 - footer.ActualWidth / 2) < .001 && position.Y == -108 && Math.Abs(footer.TranslatePoint(new Point(footer.ActualWidth / 2, 0), footerGrid).X - footerGrid.ActualWidth / 2) < 1, "the popup stays above and centered on the sidebar for width " + width);
            }
            Invoke(window, "SetAvailableUpdate", development);
            Check(hoverPopup.OpenRequested && Field<TextBlock>(window, "updatePopupTitle").Text == "Dev build 1a2b3c4", "an open popup refreshes to the development build title without closing");
            Invoke(window, "SetAvailableUpdate", stable);
            popupContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            RenderElement(popupContent, "version-hover-update", (int)Math.Ceiling(popupContent.DesiredSize.Width), (int)Math.Ceiling(popupContent.DesiredSize.Height));
            footer.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            Check(ObserveTransition(500, () => hoverPopup.OpenRequested && popupContent.Opacity is > 0 and < 1 && !hoverPopup.IsHitTestVisible), "leaving starts a visible fade-out after the hover grace period");
            popupContent.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            SettleTransitions();
            Check(StyleTransitions.GetDropDownOpen(hoverPopup) && hoverPopup.OpenRequested && popupContent.Opacity == 1 && hoverPopup.IsHitTestVisible, "entering the popup cancels its unfinished fade-out and keeps it usable");
            Invoke(window, "CloseUpdatePopup"); SettleTransitions();
            Check(!hoverPopup.OpenRequested, "the popup closes only after fading out");
            Invoke(window, "SetAvailableUpdate", (object)null!);
            footer.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            Check(!hoverPopup.OpenRequested && !StyleTransitions.GetDropDownOpen(hoverPopup), "clearing an update restores the no-popup hover behavior");
            Invoke(window, "SetAvailableUpdate", stable);
        }
        finally { hoverPopup.Child = null; originalPopup.Child = popupContent; popupField.SetValue(window, originalPopup); Field<DispatcherTimer>(window, "updateHoverTimer").Stop(); }
        VerifyUpdateInstallation(window, stable);
        checkField.SetValue(window, (Func<UpdateChannel, CancellationToken, Task<UpdateRelease?>>)((_, _) => Task.FromException<UpdateRelease?>(new HttpRequestException("simulated failure"))));
        ((Task)Invoke(window, "CheckUpdatesAsync", true)!).GetAwaiter().GetResult();
        Check(Field<TextBlock>(window, "updateCheckStatus").Text == UiLanguage.Current["检查更新失败"] && Field<Button>(window, "checkUpdatesButton").IsEnabled, "manual update failures show a short error and release the check button");
        var checksStarted = typeof(MainWindow).GetField("updateChecksStarted", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // CI development builds carry a build number, so the installed channel depends on how this test was built.
        var installedChannel = InstalledVersion.Current.Channel;
        var otherChannel = installedChannel == UpdateChannel.Stable ? UpdateChannel.Development : UpdateChannel.Stable;
        var crossChannel = otherChannel == UpdateChannel.Development ? development with { Version = new Version(0, 0, 1), BuildCommit = "0c0c0c0" } : stable with { Version = new Version(0, 0, 1) };
        var checkedChannels = new List<UpdateChannel>();
        checkField.SetValue(window, (Func<UpdateChannel, CancellationToken, Task<UpdateRelease?>>)((channel, _) =>
        {
            checkedChannels.Add(channel);
            return Task.FromResult<UpdateRelease?>(channel == otherChannel ? crossChannel : null);
        }));
        Invoke(window, "SelectUpdateChannel", otherChannel);
        checksStarted.SetValue(window, true);
        Invoke(window, "SelectUpdateChannel", installedChannel);
        Check(badge.Visibility == Visibility.Collapsed && checkedChannels.SequenceEqual([installedChannel]), "switching channels clears the previous channel's update badge and checks the new channel at once with automatic checks off");
        Invoke(window, "SelectUpdateChannel", otherChannel); SettleTransitions();
        Check(badge.Visibility == Visibility.Visible && Field<TextBlock>(window, "updatePopupTitle").Text == crossChannel.DisplayName, "switching away from the installed build's channel immediately offers that channel's latest release even with an older version");
        checksStarted.SetValue(window, false);
        Invoke(window, "SelectUpdateChannel", installedChannel);
        var current = InstalledVersion.Current;
        var latestForStatus = stable with { Version = new Version(current.Version.Major, current.Version.Minor, current.Version.Build), BuildCommit = current.BuildCommit };
        checkField.SetValue(window, (Func<UpdateChannel, CancellationToken, Task<UpdateRelease?>>)((_, _) => Task.FromResult<UpdateRelease?>(latestForStatus)));
        ((Task)Invoke(window, "CheckUpdatesAsync", true)!).GetAwaiter().GetResult();
        Check(badge.Visibility == Visibility.Collapsed && Field<TextBlock>(window, "updateCheckStatus").Text == UiLanguage.Current["已是最新版本"] + " v" + current.Display, "up-to-date results include the fetched channel version");
        latestForStatus = latestForStatus with { Version = new Version(0, 0, 1) };
        ((Task)Invoke(window, "CheckUpdatesAsync", true)!).GetAwaiter().GetResult();
        Check(Field<TextBlock>(window, "updateCheckStatus").Text == UiLanguage.Current["已是最新版本"] + " v0.0.1" + (current.BuildCommit.Length > 0 ? "_" + current.BuildCommit : ""), "a newer installed build still shows the older version actually returned by the selected channel");
        Invoke(window, "SelectUpdateChannel", otherChannel);
        Check(Field<TextBlock>(window, "updateCheckStatus").Text == "", "changing channels clears the previous check result and version");
        latestForStatus = development;
        ((Task)Invoke(window, "CheckUpdatesAsync", true)!).GetAwaiter().GetResult();
        var checkResult = Field<TextBlock>(window, "updateCheckStatus");
        Field<List<Button>>(window, "nav")[3].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        foreach (var code in UiLanguage.Codes)
        {
            UiLanguage.Current.Select(code);
            Render(window, "about-update-result-" + code, 1240, 800);
            var resultRow = (FrameworkElement)checkResult.Parent;
            var identityCard = (Border)((FrameworkElement)resultRow.Parent).Parent;
            Check(checkResult.Text == UiLanguage.Current["发现新版本"] + " v0.2.0_1a2b3c4" && checkResult.ActualHeight < 30
                && checkResult.TransformToAncestor(identityCard).TransformBounds(new Rect(checkResult.RenderSize)).Right <= identityCard.ActualWidth - identityCard.Padding.Right + .5,
                "the localized check result preserves the fetched development version and fits beside the button in " + code);
        }
        UiLanguage.Current.Select("zh-CN");
        checkField.SetValue(window, (Func<UpdateChannel, CancellationToken, Task<UpdateRelease?>>)((_, _) => Task.FromResult<UpdateRelease?>(null)));
        ((Task)Invoke(window, "CheckUpdatesAsync", true)!).GetAwaiter().GetResult();
        Check(checkResult.Text == UiLanguage.Current["暂无可用版本"], "an empty release catalog clears the prior version instead of displaying an installed or stale version");
        Invoke(window, "SelectUpdateChannel", UpdateChannel.Stable); SettleTransitions();
        var aboutButtons = (StackPanel)Field<Button>(window, "checkUpdatesButton").Parent;
        Check(aboutButtons.Children[^2] == Field<Button>(window, "checkUpdatesButton") && aboutButtons.Children[^1] == Field<TextBlock>(window, "updateCheckStatus")
            && ((ScrollViewer)Field<Dictionary<string, FrameworkElement>>(window, "pages")["ABOUT"]).IsAncestorOf(aboutButtons), "the update check button ends the About page button row with its result beside it");
        automatic.IsChecked = true; automatic.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Invoke(window, "SetAvailableUpdate", (object)null!); Invoke(window, "SetUpdateCheckStatus", (object)null!);
        checkField.SetValue(window, null);
        Render(window, "settings-updates", 1240, 800);
    }

    private static void VerifyUpdateInstallation(MainWindow window, UpdateRelease stable)
    {
        var download = Path.Combine(AppPaths.DataDirectory, "download-check.zip");
        long reported = 0;
        using (var checker = new UpdateChecker(new UpdateResponseHandler(request =>
        {
            if (request.RequestUri != stable.Package || request.Headers.Accept.Single().MediaType != "application/octet-stream") throw new Exception("Update download does not request the binary asset");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent("zip"u8.ToArray()) };
        })))
        {
            checker.DownloadAsync(stable, download, bytes => reported = bytes, CancellationToken.None).GetAwaiter().GetResult();
            Check(File.ReadAllText(download) == "zip" && reported == 3, "the release asset downloads with progress and passes size and SHA-256 verification");
            try { checker.DownloadAsync(stable with { PackageSha256 = new string('0', 64) }, download, _ => { }, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Corrupt update package accepted"); }
            catch (InvalidDataException) { Check(true, "a package whose SHA-256 differs from the release digest is rejected"); }
        }
        File.Delete(download);

        var app = Path.Combine(AppPaths.DataDirectory, "update-app");
        if (Directory.Exists(app)) Directory.Delete(app, true);
        string[] applicationFiles = ["Questyle.exe", "runtime/Questyle.dll", "language/en-US.json"];
        void Write(string relative, string text) { var path = Path.Combine(app, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        bool AllRead(string text) => applicationFiles.All(file => File.ReadAllText(Path.Combine(app, file)) == text);
        foreach (var file in applicationFiles) Write(file, "new");
        Write("data/settings.json", "user"); Write("eq-config/headphone.json", "user"); Write("paired-headphones.json", "user");
        UpdateInstaller.ResetWorkspace();
        Check(UpdateInstaller.MeasureInstallation(app) == 9, "the simulated download size counts only application files");
        UpdateInstaller.CreatePackage(app, UpdateInstaller.PackagePath, CancellationToken.None);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(UpdateInstaller.PackagePath))
            Check(zip.Entries.Select(entry => entry.FullName).ToHashSet().SetEquals(applicationFiles.Select(file => "questyle-windows-x64/" + file)), "the simulated package uses the release ZIP layout without user data");
        foreach (var file in applicationFiles) Write(file, "previous");
        var root = UpdateInstaller.Extract(UpdateInstaller.PackagePath);
        using (File.Open(Path.Combine(app, "runtime", "Questyle.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { UpdateInstaller.Install(root, app); throw new Exception("Locked installation accepted"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Check(AllRead("previous") && applicationFiles.All(file => File.Exists(Path.Combine(root, file))), "an entry that cannot be replaced restores the entries already replaced"); }
        }
        // The running process holds its loaded assemblies open like this, so the folder itself cannot be renamed.
        UpdateInstallation installation;
        using (File.Open(Path.Combine(app, "runtime", "Questyle.dll"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            installation = UpdateInstaller.Install(root, app);
        Check(AllRead("new") && File.ReadAllText(Path.Combine(app, "data", "settings.json")) == "user" && File.ReadAllText(Path.Combine(app, "paired-headphones.json")) == "user" && File.Exists(Path.Combine(app, "eq-config", "headphone.json")), "installation replaces files held open by the running process and keeps user data in place");
        installation.Rollback();
        Check(AllRead("previous"), "a failed handoff restores the previous application files");
        UpdateInstaller.DeleteWorkspace(); Directory.Delete(app, true);
        Render(window, "update-assets-loaded", 1240, 800);
        var heldAssets = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "assets"), "*", SearchOption.AllDirectories).Where(file =>
        {
            // DELETE access fails while another handle lacks FILE_SHARE_DELETE, which would also block moving the file.
            var handle = CreateFileW(file, 0x00010000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return true;
            CloseHandle(handle); return false;
        }).ToArray();
        Check(heldAssets.Length == 0, "the running window keeps no image asset open, so an update can move the program folder: " + string.Join(", ", heldAssets.Select(Path.GetFileName)));

        var formatSize = typeof(MainWindow).GetMethod("FormatSize", BindingFlags.NonPublic | BindingFlags.Static)!;
        Check(formatSize.Invoke(null, [1536L])!.Equals(("1.50", "KB")) && formatSize.Invoke(null, [69_206_016L])!.Equals(("66.00", "MB")) && formatSize.Invoke(null, [2_147_483_648L])!.Equals(("2.00", "GB")), "sizes use two decimals with KB, MB or GB");
        object SetSize(string field, string number, string unit) { var text = typeof(MainWindow).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!; text.GetType().GetMethod("Set")!.Invoke(text, [number, unit]); return text; }
        double Width(string number, string unit) { var text = SetSize("updateReceived", number, unit); return (double)text.GetType().GetProperty("ContentWidth")!.GetValue(text)!; }
        Check(new[] { Width("1.11", "KB"), Width("8.88", "MB"), Width("0.00", "GB") }.Distinct().Count() == 1, "download sizes keep fixed glyph cells for every digit and unit");

        // Offscreen preview of the update window while downloading.
        var card = Field<Border>(window, "updateCard"); var layer = (Canvas)card.Parent;
        Field<TextBlock>(window, "updateTitle").Text = UiLanguage.Current.Format("正在下载: {0}", stable.Label);
        SetSize("updateReceived", "37.42", "MB"); SetSize("updateTotal", "66.00", "MB");
        Field<Border>(window, "updateFill").Width = 316 * 37.42 / 66;
        layer.Children.Remove(card);
        var preview = new Border { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#181B24")), Child = card };
        preview.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
        try { RenderElement(preview, "update-window", 380, 160); }
        finally { preview.Child = null; layer.Children.Add(card); }

        Field<List<Button>>(window, "nav")[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var developer = Field<Border>(window, "developerCard");
        for (int i = 0; i < 7; i++) Invoke(window, "CountBrandClick");
        Check(developer.Visibility == Visibility.Collapsed, "developer options stay hidden before the eighth title click");
        Invoke(window, "CountBrandClick");
        Check(developer.Visibility == Visibility.Visible, "eight quick sidebar title clicks on the settings page reveal developer options");
        Invoke(window, "SimulateUpdate");
        var simulated = Field<UpdateRelease>(window, "availableUpdate");
        Check(simulated.Simulated && simulated.IsNewerThan(InstalledVersion.Current) && Field<Border>(window, "newVersionBadge").Visibility == Visibility.Visible, "the simulated update is offered as a newer release of the selected channel");
        ((Task)Invoke(window, "CheckUpdatesAsync", true)!).GetAwaiter().GetResult();
        Check(ReferenceEquals(Field<UpdateRelease>(window, "availableUpdate"), simulated), "update checks do not replace a pending simulated update");
        developer.Visibility = Visibility.Collapsed; Invoke(window, "SetAvailableUpdate", stable);
    }

    private static void VerifySettingsAlignment(MainWindow window)
    {
        var page = Field<Dictionary<string, FrameworkElement>>(window, "pages")["SETTINGS"];
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(page).TransformBounds(new Rect(element.RenderSize));
        double CenterY(FrameworkElement element) { var bounds = Bounds(element); return bounds.Top + bounds.Height / 2; }
        var toggle = Field<CheckBox>(window, "startup");
        var label = Descendants<TextBlock>(page).Single(text => text.Text == UiLanguage.Current["开机自启"]);
        var restore = Descendants<Button>(page).Single(button => Equals(button.Content, UiLanguage.Current["恢复原名"]));
        var save = Descendants<Button>(page).Single(button => Equals(button.Content, UiLanguage.Current["保存名称"]));
        var name = Field<TextBox>(window, "rename");
        var language = Field<ComboBox>(window, "language");
        var languageLabel = Descendants<TextBlock>(page).Single(text => text.Text == UiLanguage.Current["语言"]);
        if (Math.Abs(CenterY(languageLabel) - CenterY(language)) > 1 || Bounds(language).Left < Bounds(languageLabel).Right - 1 || Math.Abs(CenterY(label) - CenterY(toggle)) > 1 || Math.Abs(CenterY(name) - CenterY(save)) > 1 || Math.Abs(CenterY(name) - CenterY(restore)) > 1)
            throw new Exception("Settings controls are not vertically aligned: " + UiLanguage.Current.Code);
        if (Math.Abs(Bounds(language).Right - Bounds(toggle).Right) > 1 || Math.Abs(Bounds(language).Right - Bounds(restore).Right) > 1)
            throw new Exception("Settings control rows do not share their left and right edges: " + UiLanguage.Current.Code);
    }

    // For checks that a transition passes through intermediate states: every rendered frame within
    // the window is inspected, so irregular frame timing can neither skip past nor precede the state.
    // Stops on the first matching frame, leaving the transition in that intermediate state.
    private static bool ObserveTransition(int milliseconds, Func<bool> intermediate)
    {
        bool seen = false; var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        void Rendered(object? sender, EventArgs e)
        {
            seen = intermediate();
            if (seen || clock.ElapsedMilliseconds >= milliseconds) { CompositionTarget.Rendering -= Rendered; frame.Continue = false; }
        }
        CompositionTarget.Rendering += Rendered; Dispatcher.PushFrame(frame);
        return seen;
    }

    // Reads the values as of the moment the wait ends.
    private static void SampleTransition(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    // For checks that a transition has finished.
    private static void SettleTransitions(int milliseconds = 190)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        // Animation values only advance on rendered frames; end on the next frame so they reflect the elapsed time.
        void Rendered(object? sender, EventArgs e) { CompositionTarget.Rendering -= Rendered; frame.Continue = false; }
        timer.Tick += (_, _) => { timer.Stop(); CompositionTarget.Rendering += Rendered; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void Render(MainWindow window, string name, int width, int height)
    {
        var size = (Size)Invoke(window, "FitDevicePage", new Size(width, height))!;
        RenderElement((FrameworkElement)window.Content, name, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
    }
    private static void SetControlState(DependencyObject control, Type owner, string keyName, bool value)
    {
        var key = (DependencyPropertyKey)owner.GetField(keyName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        control.SetValue(key, value);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private static void RenderElement(FrameworkElement element, string name, int width, int height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppPaths.DataDirectory, name + ".png")); encoder.Save(file);
    }
}


sealed class AudioStateTransport(DongleSnapshot state) : IQuestyleTransport
{
    public DongleSnapshot State { get; private set; } = state;
    public bool RejectWrites { get; init; }
    public bool IgnoreWrites { get; init; }
    public byte[]? AuracastQueryReply { get; init; }
    public bool RestartOnGame { get; init; }
    public int HeadphoneAbsentAfterGame { get; init; }
    private bool restarted;
    private int headphoneAbsent;
    public List<List<(byte, byte)>> SessionCommands { get; } = [];
    public void BeginSession() { restarted = false; SessionCommands.Add([]); }
    public List<(byte, byte)> Commands { get; } = [];
    public List<(byte, byte)> Writes { get; } = [];
    public byte[] Exchange(byte[] packet, TimeSpan timeout)
    {
        if (restarted) throw new IOException("The acknowledged game command retired this USB interface.");
        byte command = packet[3], sub = packet[4]; Commands.Add((command, sub));
        if (SessionCommands.Count > 0) SessionCommands[^1].Add((command, sub));
        byte[] payload;
        bool write = command == 0x50 || (command is 0xe0 or 0xf0 or 0x11 or 0x22 && sub != 255);
        if (write)
        {
            Writes.Add((command, sub)); payload = [RejectWrites ? (byte)0x83 : (byte)0x80];
            if (!RejectWrites && !IgnoreWrites)
                State = command switch
                {
                    0x50 => State with { ActiveCodec = DongleController.Codecs.Single(codec => codec.Code == sub).Name },
                    0xe0 => State with { Ldac = sub }, 0xf0 => State with { Adaptive = sub },
                    0x11 => State with { GameMode = sub == 2 }, 0x22 => State with { Auracast = sub },
                    _ => throw new Exception("Unexpected write")
                };
            if (RestartOnGame && command == 0x11 && !RejectWrites) { restarted = true; headphoneAbsent = HeadphoneAbsentAfterGame; }
        }
        else
        {
            byte[] Mask(IEnumerable<CodecOption> codecs)
            {
                int mask = codecs.Aggregate(0, (value, codec) => value | (1 << (codec.Code - 1)));
                return [(byte)(mask >> 8), (byte)mask];
            }
            payload = command switch
            {
                0x90 => System.Text.Encoding.UTF8.GetBytes(State.Name),
                0x20 => [3, 71, 0, 0, 0, 0, 3, 1],
                0x80 => Mask(sub == 1 ? State.HeadphoneCodecs : State.SupportedCodecs),
                0x30 when sub == 2 && headphoneAbsent-- > 0 => new byte[10],
                0x30 when sub == 2 => [.. Mask(DongleController.Codecs.Where(codec => codec.Name == State.ActiveCodec)), 0, 0, .. Convert.FromHexString(State.HeadphoneAddress.Replace(":", ""))],
                0x30 => [.. Convert.FromHexString(State.HeadphoneAddress.Replace(":", "")), .. System.Text.Encoding.UTF8.GetBytes(State.HeadphoneName), 4],
                0xe0 => [State.Ldac], 0xf0 => [State.Adaptive],
                0x11 => [State.GameMode is true ? (byte)2 : State.GameMode is false ? (byte)1 : (byte)0x83],
                0x22 => AuracastQueryReply ?? [State.Auracast],
                _ => throw new Exception("Unexpected query " + command)
            };
        }
        var reply = new byte[payload.Length + 6];
        reply[0] = 0x51; reply[1] = 0x4c; reply[2] = packet[2]; reply[3] = 0x41; reply[4] = (byte)payload.Length;
        payload.CopyTo(reply, 5); reply[^1] = unchecked((byte)-reply.Sum(value => value));
        return reply;
    }
}

sealed class OffscreenPopup : Popup
{
    public bool OpenRequested { get; private set; }
    static OffscreenPopup() => IsOpenProperty.OverrideMetadata(typeof(OffscreenPopup), new FrameworkPropertyMetadata(false, null, (target, value) => { ((OffscreenPopup)target).OpenRequested = (bool)value; return false; }));
}

sealed class UpdateResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
}
