using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Questyle.Desktop.Services;

namespace Questyle.Desktop;

public partial class MainWindow : Window
{
    private static Brush Color(string hex) => new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
    private readonly Brush accent = Color("#CE2038"), muted = Color("#7B8290");
    private readonly DongleController dongle = new();
    private readonly Dictionary<string, FrameworkElement> pages = [];
    private readonly List<Button> nav = [];
    private readonly Grid content = new();
    private readonly TextBlock title = new(), subtitle = new(), headerStatus = new(), status = new();
    private readonly TextBlock usbName = new(), usbInfo = new(), readAt = new(), activeCodec = new(), headphoneInfo = new(), supportedInfo = new();
    private readonly ComboBox codecs = new(), ldac = new(), adaptive = new();
    private readonly CheckBox game = new() { Content = "游戏模式" }, auracast = new() { Content = "Auracast 广播" };
    private readonly TextBox rename = new(), mac = new() { Width = 185 };
    private readonly ListBox sinks = new() { MinHeight = 52, MaxHeight = 130 };
    private readonly List<FrameworkElement> usbControls = [];
    private Button cancelButton = null!;
    private readonly TextBox logBox = new() { IsReadOnly = true, Height = 220, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 11 };
    private readonly StringBuilder log = new();
    private CancellationTokenSource? operationCancel;
    private DongleSnapshot? snapshot;
    private bool busy;

    public MainWindow()
    {
        InitializeComponent();
        UsbDkTransport.HelperPath = Path.Combine(AppContext.BaseDirectory, "UsbDkHelper.dll");
        BuildShell();
        dongle.Log += AddLog;
        Loaded += (_, _) => DetectUsb();
        Closing += OnClosing;
    }

    private TextBlock Text(string value, double size = 13, bool bold = false) => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
    private TextBlock Note(string value) { var t = Text(value, 12); t.Foreground = muted; t.LineHeight = 22; return t; }
    private StackPanel Column(params UIElement[] children) { var p = new StackPanel(); foreach (var c in children) p.Children.Add(c); return p; }
    private StackPanel Row(params UIElement[] children) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var c in children) p.Children.Add(c); return p; }
    private FrameworkElement Field(string name, FrameworkElement control) { var t = Text(name, 12); t.Margin = new Thickness(0, 0, 0, 8); return Column(t, control); }
    private FrameworkElement Columns(params FrameworkElement[] children)
    {
        var p = new UniformGrid { Columns = children.Length };
        foreach (var c in children) { c.Margin = new Thickness(0, 0, 16, 0); p.Children.Add(c); }
        return p;
    }
    private Border Card(string heading, params UIElement[] children)
    {
        var p = new StackPanel(); if (heading.Length > 0) { var h = Text(heading, 17, true); h.Margin = new Thickness(0, 0, 0, 18); p.Children.Add(h); }
        foreach (var c in children) p.Children.Add(c);
        return new() { Child = p, Background = Brushes.White, BorderBrush = Color("#E7E9ED"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(24), Margin = new Thickness(0, 0, 0, 18) };
    }
    private FrameworkElement Scroll(params UIElement[] cards) => new ScrollViewer { Content = Column(cards), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(36, 4, 32, 18) };
    private Button Button(string label, Action action, bool primary = false)
    {
        var b = new Button { Content = label }; if (primary) { b.Background = accent; b.Foreground = Brushes.White; b.BorderThickness = new Thickness(0); }
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { ShowError(ex); } }; return b;
    }
    private Button AsyncButton(string label, Func<Task> action, bool primary = false) => Button(label, () => _ = Run(action, label), primary);

    private void BuildShell()
    {
        Root.ColumnDefinitions.Add(new() { Width = new GridLength(210) }); Root.ColumnDefinitions.Add(new());
        var side = new DockPanel();
        var brand = Column(Text("Questyle", 28, true), Note("旷世 · 聆听更多")); brand.Margin = new Thickness(28, 36, 20, 36);
        ((TextBlock)brand.Children[0]).Foreground = Brushes.White; DockPanel.SetDock(brand, Dock.Top); side.Children.Add(brand);
        var edition = Column(Note("WINDOWS EDITION"), Note("非官方移植版 · 0.1.0")); edition.Margin = new Thickness(28, 20, 16, 24); DockPanel.SetDock(edition, Dock.Bottom); side.Children.Add(edition);
        var navigation = new StackPanel(); side.Children.Add(navigation);
        Root.Children.Add(new Border { Background = Color("#181B24"), Child = side });
        var body = new Grid(); Grid.SetColumn(body, 1); Root.Children.Add(body);
        foreach (var height in new[] { new GridLength(96), new GridLength(1, GridUnitType.Star), new GridLength(34) }) body.RowDefinitions.Add(new() { Height = height });
        var head = new DockPanel { Margin = new Thickness(36, 24, 36, 16) };
        headerStatus.FontSize = 11; headerStatus.Foreground = muted; headerStatus.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(headerStatus, Dock.Right); head.Children.Add(headerStatus);
        title.FontSize = 26; title.FontWeight = FontWeights.SemiBold; subtitle.Foreground = muted; subtitle.Margin = new Thickness(0, 6, 0, 0); head.Children.Add(Column(title, subtitle)); body.Children.Add(head);
        Grid.SetRow(content, 1); body.Children.Add(content);
        AddPage("USB", "◈　USB 设备", "我的设备", "让每一台设备，发挥它的声音。", BuildUsb(), navigation);
        AddPage("ABOUT", "ⓘ　关于与移植状态", "关于此版本", "功能范围、运行记录与诊断。", BuildAbout(), navigation);
        var statusRow = new DockPanel(); cancelButton = Button("取消搜索", () => operationCancel?.Cancel()); cancelButton.Padding = new Thickness(8, 0, 8, 0); cancelButton.Visibility = Visibility.Collapsed; DockPanel.SetDock(cancelButton, Dock.Right); statusRow.Children.Add(cancelButton);
        status.FontSize = 11; status.Foreground = muted; status.Text = "就绪"; status.TextTrimming = TextTrimming.CharacterEllipsis; statusRow.Children.Add(status);
        var statusBorder = new Border { Background = Color("#EDF0F4"), Padding = new Thickness(20, 7, 16, 5), Child = statusRow }; Grid.SetRow(statusBorder, 2); body.Children.Add(statusBorder);
        nav[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }
    private void AddPage(string key, string label, string heading, string detail, FrameworkElement page, StackPanel navigation)
    {
        pages[key] = page; page.Visibility = Visibility.Collapsed; content.Children.Add(page);
        Button? button = null;
        button = Button(label, () => { foreach (var p in pages.Values) p.Visibility = Visibility.Collapsed; page.Visibility = Visibility.Visible; foreach (var b in nav) { b.Background = Brushes.Transparent; b.Foreground = Color("#C5CBD6"); } button!.Background = Color("#34303B"); button.Foreground = Brushes.White; title.Text = heading; subtitle.Text = detail; });
        button.Background = Brushes.Transparent; button.Foreground = Color("#C5CBD6"); button.BorderThickness = new Thickness(0); button.Margin = new Thickness(12, 4, 12, 4); button.Padding = new Thickness(18, 15, 18, 15); nav.Add(button); navigation.Children.Add(button);
    }

    private FrameworkElement BuildUsb()
    {
        usbName.FontSize = 25; usbName.FontWeight = FontWeights.SemiBold; usbName.Text = "Questyle Dongle"; usbName.Margin = new Thickness(0, 9, 0, 9);
        usbInfo.Foreground = muted; readAt.Foreground = muted; readAt.FontSize = 11; readAt.Margin = new Thickness(0, 8, 0, 0);
        var hero = new DockPanel(); var image = new Image { Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "dongle.png"))), Width = 150, Height = 125, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 30, 0) }; DockPanel.SetDock(image, Dock.Left); hero.Children.Add(image);
        var read = AsyncButton("读取设备", ReadUsb, true); read.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(read, Dock.Right); hero.Children.Add(read); hero.Children.Add(Column(Note("USB AUDIO TRANSMITTER"), usbName, usbInfo, readAt));
        codecs.DisplayMemberPath = "Name"; ldac.ItemsSource = new[] { "音质优先", "标准模式", "连接优先" }; ldac.SelectedIndex = 1; adaptive.ItemsSource = new[] { "高音质模式", "低延迟模式" }; adaptive.SelectedIndex = 0;
        activeCodec.Foreground = muted; activeCodec.FontSize = 11; activeCodec.Margin = new Thickness(0, 9, 0, 0); supportedInfo.Foreground = muted; supportedInfo.FontSize = 11; supportedInfo.TextWrapping = TextWrapping.Wrap; supportedInfo.Margin = new Thickness(0, 8, 0, 16);
        var sound = Card("声音与传输", Columns(Field("音频编解码", Column(codecs, activeCodec)), Field("LDAC 模式", ldac), Field("aptX Adaptive 模式", adaptive)), Row(game, auracast), supportedInfo, AsyncButton("应用声音设置", ApplySound, true));
        var pairActions = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var actions = Row(AsyncButton("连接", ConnectSink), AsyncButton("断开当前耳机", DisconnectSink), AsyncButton("忘记选中耳机", ForgetSink)); DockPanel.SetDock(actions, Dock.Right); pairActions.Children.Add(actions); mac.ToolTip = "蓝牙地址，例如 AA:BB:CC:DD:EE:FF"; pairActions.Children.Add(mac);
        sinks.SelectionChanged += (_, _) => { if (sinks.SelectedItem is AudioSink sink) mac.Text = sink.Address; };
        headphoneInfo.Foreground = muted; headphoneInfo.Margin = new Thickness(0, 0, 0, 12);
        var headphones = Card("耳机连接", headphoneInfo, AsyncButton("搜索蓝牙耳机（约 12 秒）", SearchSinks), sinks, pairActions); sinks.Margin = new Thickness(0, 14, 0, 0);
        var nameRow = new DockPanel(); var nameButton = AsyncButton("保存名称", Rename); DockPanel.SetDock(nameButton, Dock.Right); nameRow.Children.Add(nameButton); rename.Margin = new Thickness(0, 0, 12, 0); nameRow.Children.Add(rename);
        var names = Card("设备名称", nameRow);
        usbControls.AddRange([sound, headphones, names]); foreach (var c in usbControls) c.IsEnabled = false;
        var warning = new Border { Background = Color("#FFF2DD"), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 18), Child = Note("USB 设置会短暂暂停电脑音频。每次操作结束后自动释放 Dongle，由 Windows 恢复音频接口。") };
        return Scroll(Card("", hero), warning, sound, headphones, names);
    }

    private FrameworkElement BuildAbout() => Scroll(Card("Questyle for Windows", Note("从 Android 1.8.0 APK 逆向重建的 Windows 原生客户端"),
        AboutText("USB Dongle", "已接入双阶段握手、设备与固件信息、耳机搜索和连接、编解码切换、LDAC、aptX Adaptive、游戏模式、Auracast 和改名。QCC Dongle Pro2 已完成实机查询验证；设置是否生效以设备响应及回读为准。"),
        AboutText("仍在移植的功能", "官方云端曲库、账号与反馈、完整 EQ 音效和固件 OTA 尚未完成。本版本不执行固件写入。"),
        AboutText("运行与数据", "Windows 10 2004 或更新版本，64 位。USB 控制依赖系统已有的 UsbDk 驱动，操作期间音频暂时断开，完成后自动释放。日志位于 LocalAppData\\QuestyleWindows。"),
        Row(Button("打开数据文件夹", () => Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true })), Button("Questyle 官网", () => Process.Start(new ProcessStartInfo("https://www.questyle.com/") { UseShellExecute = true })))), Card("运行日志", Button("导出诊断日志", ExportLog), logBox));
    private UIElement AboutText(string heading, string body) { var h = Text(heading, 16, true); h.Margin = new Thickness(0, 24, 0, 10); return Column(h, Note(body)); }

    private void DetectUsb()
    {
        try
        {
            var devices = HidTransport.Enumerate(); var device = devices.FirstOrDefault(d => d.InputReportId == 0xbb) ?? devices.FirstOrDefault();
            usbName.Text = device?.Name ?? "未检测到 Dongle"; usbInfo.Text = device != null ? "USB 已连接 · 点击读取设备获取实时设置" : "请将 Questyle Dongle 插入电脑 USB 接口。";
            headerStatus.Text = device != null ? "●  USB 已检测到" : "○  未检测到 USB"; AddLog("启动检测：" + usbName.Text);
        }
        catch (Exception ex) { usbInfo.Text = ex.Message; AddLog(ex.Message); }
    }
    private async Task ReadUsb() { snapshot = await dongle.ReadAsync(); DisplaySnapshot(snapshot); }
    private void DisplaySnapshot(DongleSnapshot value)
    {
        usbName.Text = value.Name; usbInfo.Text = "固件 " + value.Version + " · USB 控制可用"; readAt.Text = "上次读取：" + value.ReadAt.ToString("HH:mm:ss") + " · 控制会话已释放";
        headerStatus.Text = "●  USB 实机已读取"; rename.Text = value.Name; activeCodec.Text = "当前：" + value.ActiveCodec;
        var available = value.HeadphoneCodecs.Count > 0 ? value.SupportedCodecs.Where(c => value.HeadphoneCodecs.Any(h => h.Code == c.Code)).ToArray() : value.SupportedCodecs.ToArray();
        codecs.ItemsSource = available; codecs.SelectedItem = available.FirstOrDefault(c => c.Name == value.ActiveCodec) ?? available.FirstOrDefault();
        bool ldacAvailable = value.Ldac is >= 1 and <= 3 && value.HeadphoneCodecs.Any(c => c.Code == 8);
        bool adaptiveAvailable = value.Adaptive is >= 1 and <= 2 && value.HeadphoneCodecs.Any(c => c.Code == 4);
        ldac.IsEnabled = ldacAvailable; ldac.SelectedIndex = ldacAvailable ? value.Ldac - 1 : -1; ldac.ToolTip = ldacAvailable ? null : "当前耳机不支持此选项";
        adaptive.IsEnabled = adaptiveAvailable; adaptive.SelectedIndex = adaptiveAvailable ? value.Adaptive - 1 : -1; adaptive.ToolTip = adaptiveAvailable ? null : "当前耳机不支持此选项";
        game.IsChecked = value.GameMode; auracast.IsEnabled = value.Auracast is 0 or 1; auracast.IsChecked = value.Auracast == 1;
        supportedInfo.Text = "Dongle 支持：" + string.Join(" / ", value.SupportedCodecs.Select(c => c.Name)) + "\n耳机支持：" + string.Join(" / ", value.HeadphoneCodecs.Select(c => c.Name));
        headphoneInfo.Text = string.IsNullOrEmpty(value.HeadphoneAddress) ? "未检测到已连接耳机" : $"{(string.IsNullOrEmpty(value.HeadphoneName) ? "当前耳机" : value.HeadphoneName)}  ·  {value.HeadphoneAddress}  ·  {value.ActiveCodec}";
        if (value.HeadphoneAddress.Length > 0) { mac.Text = value.HeadphoneAddress; if (!sinks.Items.OfType<AudioSink>().Any(s => s.Address == value.HeadphoneAddress)) sinks.Items.Add(new AudioSink(value.HeadphoneAddress, value.HeadphoneName.Length > 0 ? value.HeadphoneName : "当前耳机", 0)); }
        foreach (var c in usbControls) c.IsEnabled = true;
    }
    private async Task ApplySound()
    {
        var old = snapshot ?? throw new InvalidOperationException("请先读取设备。");
        var selected = codecs.SelectedItem as CodecOption; byte? desiredLdac = ldac.IsEnabled && ldac.SelectedIndex >= 0 ? (byte)(ldac.SelectedIndex + 1) : null, desiredAdaptive = adaptive.IsEnabled && adaptive.SelectedIndex >= 0 ? (byte)(adaptive.SelectedIndex + 1) : null; bool desiredGame = game.IsChecked == true; bool? desiredAura = auracast.IsEnabled ? auracast.IsChecked == true : null;
        if ((selected == null || selected.Name == old.ActiveCodec) && (!desiredLdac.HasValue || desiredLdac == old.Ldac) && (!desiredAdaptive.HasValue || desiredAdaptive == old.Adaptive) && desiredGame == old.GameMode && (!desiredAura.HasValue || desiredAura == (old.Auracast == 1))) { SetStatus("设置未变化。"); return; }
        snapshot = await dongle.RunAsync(s =>
        {
            var fresh = s.Snapshot();
            if (fresh.HeadphoneAddress != old.HeadphoneAddress) throw new IOException("连接的耳机已发生变化，请重新读取设备后再设置。");
            if (selected != null && selected.Name != old.ActiveCodec) s.SetCodec(selected.Code);
            if (desiredLdac.HasValue && desiredLdac != old.Ldac) s.SetLdac(desiredLdac.Value);
            if (desiredAdaptive.HasValue && desiredAdaptive != old.Adaptive) s.SetAdaptive(desiredAdaptive.Value);
            if (desiredGame != old.GameMode) s.SetGame(desiredGame);
            if (desiredAura.HasValue && desiredAura != (old.Auracast == 1)) s.SetAuracast(desiredAura.Value);
            var updated = s.Snapshot();
            if ((desiredLdac.HasValue && updated.Ldac != desiredLdac) || (desiredAdaptive.HasValue && updated.Adaptive != desiredAdaptive) || updated.GameMode != desiredGame || (desiredAura.HasValue && (updated.Auracast == 1) != desiredAura)) throw new IOException("设备回读与请求不一致，请重新读取设备设置。");
            return updated;
        }); DisplaySnapshot(snapshot); SetStatus("声音设置已获设备确认，并已回读。");
    }
    private async Task SearchSinks()
    {
        operationCancel = new(); cancelButton.Visibility = Visibility.Visible;
        try
        {
            var found = await dongle.RunAsync(s => s.Search(TimeSpan.FromSeconds(12)), operationCancel.Token);
            sinks.Items.Clear(); foreach (var sink in found) sinks.Items.Add(sink); SetStatus($"搜索完成，找到 {found.Length} 台耳机。");
        }
        finally { cancelButton.Visibility = Visibility.Collapsed; operationCancel.Dispose(); operationCancel = null; }
    }
    private string SelectedAddress() => mac.Text.Trim();
    private async Task ConnectSink() { var address = SelectedAddress(); snapshot = await dongle.RunAsync(s => { s.Connect(address); Thread.Sleep(500); return s.Snapshot(); }); DisplaySnapshot(snapshot); }
    private async Task DisconnectSink() { snapshot = await dongle.RunAsync(s => { s.Disconnect(); return s.Snapshot(); }); DisplaySnapshot(snapshot); }
    private async Task ForgetSink() { var address = SelectedAddress(); if (MessageBox.Show(this, "忘记耳机 " + address + "？", "忘记耳机", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return; await dongle.RunAsync(s => { s.Forget(address); return true; }); sinks.Items.Remove(sinks.SelectedItem); }
    private async Task Rename() { var name = rename.Text; await dongle.RunAsync(s => { s.Rename(name); return true; }); await ReadUsb(); }

    private async Task Run(Func<Task> action, string label)
    {
        if (busy) { SetStatus("正在执行上一项操作，请稍候。"); return; }
        busy = true; SetStatus(label + "…"); Cursor = Cursors.AppStarting;
        try { await action(); if (status.Text == label + "…") SetStatus(label + "完成。"); }
        catch (OperationCanceledException) { SetStatus("操作已取消，USB 控制会话已释放。"); }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; Cursor = null; }
    }
    private void ShowError(Exception ex)
    {
        string message = ex is DllNotFoundException ? "找不到 UsbDkHelper.dll，请使用完整发布目录。" : ex.Message;
        if (ex is System.ComponentModel.Win32Exception) message += "\nUSB 控制需要系统已安装并运行 UsbDk 驱动。";
        SetStatus(message); AddLog(ex.ToString()); MessageBox.Show(this, message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void SetStatus(string value) { status.Text = value; status.ToolTip = value; }
    private void AddLog(string value)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AddLog(value)); return; }
        var line = $"[{DateTime.Now:HH:mm:ss}] {value}"; log.AppendLine(line); if (log.Length > 200000) log.Remove(0, log.Length - 150000); logBox.Text = log.ToString(); logBox.ScrollToEnd();
    }
    private void ExportLog() { var dialog = new SaveFileDialog { Filter = "文本日志|*.txt", FileName = "Questyle-diagnostics.txt" }; if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, log.ToString()); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; operationCancel?.Cancel(); SetStatus("正在结束操作并释放设备，请稍后关闭。"); return; }
        try { File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "last-session.log"), log.ToString()); } catch (IOException) { }
    }
}
