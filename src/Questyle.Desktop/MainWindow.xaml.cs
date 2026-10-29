using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using Questyle.Desktop.Services;

namespace Questyle.Desktop;

public partial class MainWindow : Window
{
    private static Brush Color(string hex) => new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
    private readonly Brush accent = Color("#CA263D"), muted = Color("#7B8290"), readAccent = Color("#2077CE"), applyAccent = Color("#4B9B66");
    public static readonly DependencyProperty ConnectedHeadphoneAddressProperty = DependencyProperty.Register(
        nameof(ConnectedHeadphoneAddress), typeof(string), typeof(MainWindow));
    public string? ConnectedHeadphoneAddress { get => (string?)GetValue(ConnectedHeadphoneAddressProperty); private set => SetValue(ConnectedHeadphoneAddressProperty, value); }
    private readonly DongleController dongle = new();
    private readonly HeadphoneHistory history = new(AppPaths.PairingFile);
    private readonly HeadphoneAudioMemory audioMemory = new(Path.Combine(AppPaths.DataDirectory, "audio-preferences.json"));
    private Border audioPanel = null!;
    private const double HeaderHeight = 96, StatusHeight = 34;
    private readonly Dictionary<string, FrameworkElement> pages = [];
    private readonly List<Button> nav = [];
    private readonly Grid content = new();
    private readonly TextBlock title = new(), headerStatus = new(), status = new();
    private readonly StackPanel peqHeaderStatus = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock usbName = new(), usbInfo = new(), headphoneInfo = new(), supportedInfo = new();
    private readonly Image codecLogo = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly ComboBox codecs = new(), ldac = new(), adaptive = new();
    private FrameworkElement ldacField = null!, adaptiveField = null!;
    private readonly CheckBox microphone = new() { Content = "关闭麦克风(蓝牙通话时严重劣化音质)" }, auracast = new() { Content = "Auracast 广播" };
    private readonly TextBox rename = new();
    private readonly ListBox sinks = new() { Height = 192 }, searchResults = new() { MinHeight = 150, MaxHeight = 300 };
    private readonly TextBlock searchStatus = new();
    private readonly BlurEffect dialogBlur = new() { Radius = 0 };
    private Grid? activeDialogOverlay;
    private readonly List<FrameworkElement> usbControls = [];
    private Button readButton = null!, closeButton = null!, disconnectButton = null!;
    private readonly ComboBox language = new();
    private readonly CheckBox startup = new();
    private WindowShadow? shadow;
    private DeviceChangeWatcher? deviceWatcher;
    private bool presenceCheckPending, presenceRefreshing, awaitingInterfaceRecovery;
    private int presenceRevision;
    private bool usbDetected;
    private string selectedPageHeading = "我的设备";
    private readonly TextBox logBox = new() { IsReadOnly = true, Height = 220, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13 };
    private readonly StringBuilder log = new();
    private CancellationTokenSource? operationCancel;
    private AudioSink? pendingPairing;
    private string? originalDeviceName;
    private static string OriginalNameFile => Path.Combine(AppPaths.DataDirectory, "original-device-name.txt");
    private DongleSnapshot? snapshot;
    private (DongleSnapshot BeforeChange, bool Desired)? pendingMicrophoneChange;
    private bool busy, exitRequested, autoReadAttempted;

    public MainWindow()
    {
        InitializeComponent();
        FontFamily = new FontFamily("Microsoft YaHei, SimHei, " + SystemFonts.MessageFontFamily.Source);
        UsbDkTransport.HelperPath = Path.Combine(AppContext.BaseDirectory, "UsbDkHelper.dll");
        Icon = BitmapFrame.Create(new Uri(Path.Combine(AppContext.BaseDirectory, "assets", "Questyle.ico")), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BuildShell();
        BuildUpdateCard();
        UiLanguage.Current.Changed += RefreshLanguage;
        SourceInitialized += (_, _) =>
        {
            shadow = new WindowShadow(this) { Suspended = updating };
            deviceWatcher = new DeviceChangeWatcher(HwndSource.FromHwnd(new WindowInteropHelper(this).Handle), change => HandleDeviceChange(change, RefreshUsbPresenceAsync));
            Dispatcher.BeginInvoke(UpdateWindowSizing);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(UpdateWindowSizing);
        Closed += (_, _) => UiLanguage.Current.Changed -= RefreshLanguage;
        dongle.Log += AddLog;
        BuildSearchDialog();
        BuildPeqExportDialog();
        FrameContent.SizeChanged += (_, _) => UpdateFrameClip();
        Root.MouseLeftButtonDown += MoveFromBackground;
        Activated += async (_, _) => await ReadWhenForeground();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && activeDialogOverlay == SearchOverlay) { DismissSearch(); e.Handled = true; } else if (e.Key == Key.Escape && activeDialogOverlay == PeqExportOverlay) { DismissPeqExport(); e.Handled = true; } };
        try { if (File.Exists(OriginalNameFile)) originalDeviceName = File.ReadAllText(OriginalNameFile); history.Load(); audioMemory.Load(); RefreshRememberedHeadphones(); } catch (Exception ex) { ShowError(ex); }
        Closing += OnClosing;
        UpdateWindowSizing();
    }

    private static string L(string value) => UiLanguage.Current[value];
    // Read fully at load: an open image file would block moving the program folder during an update.
    private static BitmapImage Asset(params string[] path)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.Combine([AppContext.BaseDirectory, "assets", .. path])); image.EndInit(); image.Freeze();
        return image;
    }
    private static void Localize(DependencyObject target, DependencyProperty property, string key) => BindingOperations.SetBinding(target, property, new Binding("[" + key + "]") { Source = UiLanguage.Current });
    private TextBlock Text(string value, double size = 14, bool bold = false)
    {
        var text = new TextBlock { FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
        Localize(text, TextBlock.TextProperty, value); return text;
    }
    private TextBlock Note(string value) { var t = Text(value, 13); t.Foreground = muted; t.LineHeight = 24; return t; }
    private StackPanel Column(params UIElement[] children) { var p = new StackPanel(); foreach (var c in children) p.Children.Add(c); return p; }
    private StackPanel Row(params UIElement[] children) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var c in children) p.Children.Add(c); return p; }
    private FrameworkElement Field(string name, FrameworkElement control, double spacing = 12) { var t = Text(name); t.Margin = new Thickness(0, 0, 0, spacing); return Column(t, control); }
    private Border Card(string heading, params UIElement[] children)
    {
        var p = new StackPanel(); if (heading.Length > 0) { var h = Text(heading, 19, true); h.Margin = new Thickness(0, 0, 0, 20); p.Children.Add(h); }
        foreach (var c in children) p.Children.Add(c);
        return new() { Tag = "Card", Child = p, Background = Brushes.White, BorderBrush = Color("#E7E9ED"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(24), Margin = new Thickness(0, 0, 0, 18) };
    }
    private FrameworkElement Scroll(params UIElement[] cards) => new ScrollViewer { Focusable = false, Content = Column(cards), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(36, 4, 36, 18) };
    private Button Button(string label, Action action, bool primary = false)
    {
        var b = new Button(); Localize(b, ContentControl.ContentProperty, label); if (primary) { b.Background = accent; b.Foreground = Brushes.White; }
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { ShowError(ex); } }; return b;
    }
    private Button AsyncButton(string label, Func<Task> action, bool primary = false) => Button(label, () => _ = Run(action, label), primary);

    private void BuildShell()
    {
        Root.ColumnDefinitions.Add(new() { Width = new GridLength(210) }); Root.ColumnDefinitions.Add(new());
        var side = new DockPanel();
        var brandText = Text("Questyle", 28, true); brandText.HorizontalAlignment = HorizontalAlignment.Center; brandText.TextAlignment = TextAlignment.Center;
        var brand = Column(brandText); brand.Margin = new Thickness(20, 28, 20, 28);
        brandText.Foreground = Brushes.White; brandText.PreviewMouseLeftButtonDown += (_, _) => CountBrandClick(); DockPanel.SetDock(brand, Dock.Top); side.Children.Add(brand);
        var edition = BuildVersionFooter(); DockPanel.SetDock(edition, Dock.Bottom); side.Children.Add(edition);
        var navigation = new StackPanel(); side.Children.Add(navigation);
        Root.Children.Add(new Border { Background = Color("#181B24"), Child = side });
        var body = new Grid { Background = Color("#F5F6F8") }; Grid.SetColumn(body, 1); Root.Children.Add(body);
        foreach (var height in new[] { new GridLength(HeaderHeight), new GridLength(1, GridUnitType.Star), new GridLength(StatusHeight) }) body.RowDefinitions.Add(new() { Height = height });
        var head = new Grid { Margin = new Thickness(36, 20, 64, 16), Background = Brushes.Transparent };
        head.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new()); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        title.FontSize = 26; title.FontWeight = FontWeights.SemiBold; title.VerticalAlignment = VerticalAlignment.Center;
        peqEnabled.Style = (Style)FindResource("StartupToggle"); peqEnabled.Margin = new Thickness(16, 0, 0, 0); peqEnabled.Visibility = Visibility.Collapsed; Localize(peqEnabled, ToolTipProperty, "启用 PEQ");
        head.Children.Add(Row(title, peqEnabled, BuildPeqModeSelector()));
        Grid.SetColumn(codecLogo, 1); head.Children.Add(codecLogo);
        headerStatus.FontSize = 13; headerStatus.Foreground = muted; headerStatus.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(headerStatus, 2); head.Children.Add(headerStatus);
        peqHeaderStatus.Children.Add(new TextBlock { Text = "●  ", FontSize = 13, Foreground = muted, VerticalAlignment = VerticalAlignment.Center }); peqHeaderStatus.Children.Add(peqState); Grid.SetColumn(peqHeaderStatus, 2); head.Children.Add(peqHeaderStatus); body.Children.Add(head);
        closeButton = Button("✕", Close); closeButton.Style = (Style)FindResource("CloseButtonStyle"); Localize(closeButton, FrameworkElement.ToolTipProperty, "关闭到托盘"); closeButton.FontSize = 18; closeButton.Padding = new Thickness(10, 5, 10, 5); closeButton.Margin = new Thickness(0, 8, 10, 0); closeButton.BorderThickness = new Thickness(0); closeButton.Background = Brushes.Transparent; closeButton.HorizontalAlignment = HorizontalAlignment.Right; closeButton.VerticalAlignment = VerticalAlignment.Top; body.Children.Add(closeButton);
        closeButton.MouseEnter += (_, e) => UpdateClosePointer(e.GetPosition(closeButton));
        closeButton.MouseMove += (_, e) => UpdateClosePointer(e.GetPosition(closeButton));
        closeButton.MouseLeave += (_, _) => closeButton.Tag = null;
        Grid.SetRow(content, 1); body.Children.Add(content);
        AddPage("USB", "设备", "我的设备", BuildUsb(), navigation);
        AddPage("PEQ", "PEQ", "参数均衡器", BuildPeq(), navigation);
        AddPage("SETTINGS", "设置", "设置", BuildSettings(), navigation);
        AddPage("ABOUT", "关于", "关于", BuildAbout(), navigation);
        var statusRow = new DockPanel();
        status.Visibility = Visibility.Hidden; status.FontSize = 13; status.Foreground = muted; status.Text = L("就绪"); status.TextTrimming = TextTrimming.CharacterEllipsis; statusRow.Children.Add(status);
        var statusBorder = new Border { Background = Color("#EDF0F4"), Padding = new Thickness(36, 7, 36, 5), Child = statusRow }; Grid.SetRow(statusBorder, 2); body.Children.Add(statusBorder);
        RegisterShellParts(body, [brand, .. nav, edition, head, closeButton, content, statusBorder]);
        nav[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }
    private void AddPage(string key, string label, string heading, FrameworkElement page, StackPanel navigation)
    {
        pages[key] = page; page.Visibility = Visibility.Collapsed; content.Children.Add(page);
        Button? button = null;
        button = Button("nav:" + label, () => { foreach (var p in pages.Values) p.Visibility = Visibility.Collapsed; page.Visibility = Visibility.Visible; foreach (var b in nav) b.Tag = ReferenceEquals(b, button) ? "Selected" : null; selectedPageHeading = heading; title.Text = L(heading); codecLogo.Visibility = key == "USB" && codecLogo.Source != null ? Visibility.Visible : Visibility.Collapsed; peqEnabled.Visibility = peqModeSelector.Visibility = peqHeaderStatus.Visibility = key == "PEQ" ? Visibility.Visible : Visibility.Collapsed; headerStatus.Visibility = key == "USB" ? Visibility.Visible : Visibility.Collapsed; MonitorPeqPage(key == "PEQ"); });
        button.Style = (Style)FindResource("NavigationButtonStyle"); button.FontSize = 20; button.BorderThickness = new Thickness(0); button.Margin = new Thickness(12, 5, 12, 5); button.Padding = new Thickness(18, 14, 18, 14); nav.Add(button); navigation.Children.Add(button);
    }

    private FrameworkElement BuildUsb()
    {
        usbName.FontSize = 23; usbName.FontWeight = FontWeights.SemiBold; usbName.Text = "Questyle Dongle"; usbName.Margin = new Thickness(0, 4, 0, 8);
        usbInfo.Foreground = muted; usbInfo.FontSize = 13; usbInfo.VerticalAlignment = VerticalAlignment.Center; usbInfo.Margin = new Thickness(12, 0, 0, 0);
        supportedInfo.Foreground = muted; supportedInfo.FontSize = 13; supportedInfo.TextWrapping = TextWrapping.Wrap; supportedInfo.LineHeight = 20; supportedInfo.Height = 40;
        var identity = new WrapPanel(); identity.Children.Add(usbName); identity.Children.Add(usbInfo);
        var hero = new DockPanel(); var image = new Image { Source = Asset("dongle.png"), Width = 100, Height = 80, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 20, 0) }; DockPanel.SetDock(image, Dock.Left); hero.Children.Add(image);
        readButton = AsyncButton("读取设备", ReadUsb, true); readButton.Background = readAccent; readButton.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(readButton, Dock.Right); hero.Children.Add(readButton); hero.Children.Add(Column(identity, supportedInfo));
        codecs.DisplayMemberPath = "Name"; ldac.ItemsSource = new[] { L("音质优先"), L("标准模式"), L("连接优先") }; ldac.SelectedIndex = 1; adaptive.ItemsSource = new[] { L("高音质模式"), L("低延迟模式") }; adaptive.SelectedIndex = 0;
        ldacField = Field("LDAC 模式", ldac, 8); adaptiveField = Field("aptX Adaptive 模式", adaptive, 8);
        ldacField.Margin = adaptiveField.Margin = new Thickness(0, 0, 0, 12); ldacField.Visibility = adaptiveField.Visibility = Visibility.Collapsed;
        microphone.Visibility = auracast.Visibility = Visibility.Collapsed;
        var microphoneLabel = Text("关闭麦克风(蓝牙通话时严重劣化音质)"); microphoneLabel.TextWrapping = TextWrapping.NoWrap;
        microphone.Content = microphoneLabel; microphone.Margin = auracast.Margin = new Thickness(0, 2, 0, 0);
        Localize(auracast, ContentControl.ContentProperty, "Auracast 广播");
        codecs.SelectionChanged += (_, _) => UpdateAudioOptions();
        var protocol = Field("传输协议", codecs, 8); protocol.Margin = new Thickness(0, 0, 0, 16);
        var applyButton = AsyncButton("应用音频设置", ApplySound, true); applyButton.Background = applyAccent;
        var sound = audioPanel = FixedCard("音频设置", Column(protocol, ldacField, adaptiveField, microphone, auracast), applyButton);
        ConfigureHeadphoneList(sinks, "SavedHeadphoneTemplate");
        sinks.ItemContainerStyle = (Style)FindResource("SavedHeadphoneItemStyle");
        sinks.MouseLeftButtonUp += (_, e) => { if (ClickedHeadphone(sinks, e.OriginalSource as DependencyObject) is AudioSink sink) _ = ConnectSink(sink); };
        sinks.KeyDown += (_, e) => { if (e.Key == Key.Enter && e.OriginalSource is not ButtonBase && sinks.SelectedItem is AudioSink sink) { e.Handled = true; _ = ConnectSink(sink); } };
        headphoneInfo.FontSize = 13; headphoneInfo.Foreground = muted; headphoneInfo.TextWrapping = TextWrapping.Wrap; headphoneInfo.LineHeight = 20; headphoneInfo.Margin = new Thickness(0, 0, 0, 10);
        var searchButton = Button("搜索蓝牙耳机", () => _ = OpenSearch()); searchButton.Margin = new Thickness(0); searchButton.Height = 36; searchButton.Padding = new Thickness(17, 6, 17, 6);
        disconnectButton = AsyncButton("断开当前设备", DisconnectSink, true); disconnectButton.IsEnabled = false;
        var headphones = FixedCard("耳机连接", Column(headphoneInfo, searchButton, sinks), disconnectButton); sinks.Margin = new Thickness(0, 10, 0, 12);
        var settings = new Grid(); settings.ColumnDefinitions.Add(new()); settings.ColumnDefinitions.Add(new());
        sound.Margin = new Thickness(0, 0, 10, 0); headphones.Margin = new Thickness(10, 0, 0, 0); sound.VerticalAlignment = headphones.VerticalAlignment = VerticalAlignment.Top;
        settings.Children.Add(sound); Grid.SetColumn(headphones, 1); settings.Children.Add(headphones);
        usbControls.AddRange([sound, headphones]); foreach (var c in usbControls) c.IsEnabled = false;
        var device = Card("", hero); device.Padding = new Thickness(20, 20, 20, 24);
        return Scroll(device, settings);
    }

    private Border FixedCard(string heading, UIElement body, UIElement footer)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = Text(heading, 19, true); title.Margin = new Thickness(0, 0, 0, 12); grid.Children.Add(title);
        Grid.SetRow(body, 1); grid.Children.Add(body);
        ((Button)footer).Height = 40; ((Button)footer).Padding = new Thickness(17, 8, 17, 8); ((FrameworkElement)footer).Margin = new Thickness(0); Grid.SetRow(footer, 2); grid.Children.Add(footer);
        var card = Card(""); card.Child = grid; card.Padding = new Thickness(20); card.Height = 416; return card;
    }

    private void UpdateClosePointer(Point point)
    {
        closeButton.Tag = new Rect(0, 0, closeButton.ActualWidth, closeButton.ActualHeight).Contains(point) ? "Hover" : null;
    }

    private void UpdateWindowSizing()
    {
        var source = PresentationSource.FromVisual(this);
        var available = SystemParameters.WorkArea.Size;
        if (source != null)
        {
            var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
            var size = source.CompositionTarget.TransformFromDevice.Transform(new Point(area.Width, area.Height));
            available = new Size(size.X, size.Y);
        }
        FitDevicePage(available);
    }

    private void UpdateFrameClip()
    {
        double radius = WindowFrame.CornerRadius.TopLeft - WindowFrame.BorderThickness.Left;
        FrameContent.Clip = new RectangleGeometry(new Rect(0, 0, FrameContent.ActualWidth, FrameContent.ActualHeight), radius, radius);
    }

    private Size FitDevicePage(Size available)
    {
        FormatPeqState();
        // Preserve the established Chinese layout width across language changes.
        // WPF measurements remain in DIPs and scale down for a smaller work area.
        var label = new FormattedText("关闭麦克风(蓝牙通话时严重劣化音质)", CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            microphone.FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double panelWidth = (label.WidthIncludingTrailingWhitespace + 18 + 42) * 1.3;
        var readLabel = new FormattedText(L("重新读取设备"), CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(readButton.FontFamily, readButton.FontStyle, readButton.FontWeight, readButton.FontStretch),
            readButton.FontSize, readButton.Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        readButton.Width = Math.Ceiling(readLabel.WidthIncludingTrailingWhitespace) + readButton.Padding.Left + readButton.Padding.Right + readButton.BorderThickness.Left + readButton.BorderThickness.Right;
        WindowFrame.BorderThickness = new Thickness(1 / VisualTreeHelper.GetDpi(this).DpiScaleX);
        UpdateFrameClip();
        double frameInset = WindowFrame.BorderThickness.Left + WindowFrame.BorderThickness.Right;
        double width = Root.ColumnDefinitions[0].Width.Value + 72 + 20 + panelWidth * 2;
        double pageWidth = width - Root.ColumnDefinitions[0].Width.Value;
        var devicePage = (ScrollViewer)pages["USB"];
        title.Measure(new Size(pageWidth - 100, double.PositiveInfinity));
        double titleBottom = 20 + (HeaderHeight - 20 - 16 + title.DesiredSize.Height) / 2;
        double gap = HeaderHeight + devicePage.Padding.Top - titleBottom;
        devicePage.Padding = new Thickness(36, 4, 36, gap);
        var deviceContent = (FrameworkElement)devicePage.Content;
        deviceContent.Measure(new Size(pageWidth - 72, double.PositiveInfinity));
        content.Height = deviceContent.DesiredSize.Height + devicePage.Padding.Top + gap;
        var peqPage = (ScrollViewer)pages["PEQ"];
        peqPage.Padding = new Thickness(36, 4, 36, gap);
        peqCard.Height = content.Height - peqPage.Padding.Top - peqPage.Padding.Bottom;

        // About keeps its diagnostic controls and uses the remaining space for
        // the log, so changing pages does not enlarge this device-sized window.
        var about = (ScrollViewer)pages["ABOUT"];
        about.Padding = new Thickness(36, 4, 36, gap);
        logBox.Height = 0;
        var aboutContent = (FrameworkElement)about.Content;
        // Measure the zero-height baseline through the log card instead of its cached size.
        for (FrameworkElement measure = logBox; measure != aboutContent; measure = (FrameworkElement)measure.Parent)
            measure.InvalidateMeasure();
        aboutContent.InvalidateMeasure();
        aboutContent.Measure(new Size(pageWidth - 72, double.PositiveInfinity));
        logBox.Height = Math.Max(0, content.Height - about.Padding.Top - about.Padding.Bottom - aboutContent.DesiredSize.Height);

        double height = HeaderHeight + content.Height + StatusHeight;
        double scale = Math.Min(1, Math.Min(available.Width * .94 / (width + frameInset), available.Height * .94 / (height + frameInset)));
        Root.LayoutTransform = SearchOverlay.LayoutTransform = new ScaleTransform(scale, scale);
        Width = width * scale + frameInset;
        return new Size(Width, height * scale + frameInset);
    }

    private DongleSnapshot RestoreSavedAudio(DongleController.Session session, DongleSnapshot actual, bool restoreCodec = true) =>
        ApplyAudioChoices(session, actual, restoreCodec, null, null, null, null);

    private DongleSnapshot ApplyAudioChoices(DongleController.Session session, DongleSnapshot actual, bool restoreCodec,
        byte? ldacChoice, byte? adaptiveChoice, bool? microphoneChoice, bool? auraChoice)
    {
        if (!actual.HasConnectedHeadphone) return actual;
        string address = actual.HeadphoneAddress;
        var preference = audioMemory.Find(address);
        var target = preference == null ? null : actual.SupportedCodecs.FirstOrDefault(codec => codec.Code == preference.Codec && actual.HeadphoneCodecs.Any(headphone => headphone.Code == codec.Code));
        if (restoreCodec && target != null && actual.ActiveCodec != target.Name)
        {
            session.SetCodec(target.Code);
            actual = session.Snapshot();
            if (!actual.HasConnectedHeadphone || actual.HeadphoneAddress != address || actual.ActiveCodec != target.Name)
                throw new IOException("设备回读与请求不一致，请重新读取设备设置。");
        }
        bool ldacAvailable = actual.ActiveCodec == "LDAC" && actual.Ldac is >= 1 and <= 3 && actual.HeadphoneCodecs.Any(codec => codec.Code == 8);
        bool adaptiveAvailable = actual.ActiveCodec == "aptX Adaptive" && actual.Adaptive is >= 1 and <= 2 && actual.HeadphoneCodecs.Any(codec => codec.Code == 4);
        bool microphoneAvailable = actual.ActiveCodec != "LC3" && actual.GameMode.HasValue;
        bool auraAvailable = actual.ActiveCodec == "LC3" && actual.Auracast is 0 or 1;
        if ((ldacChoice.HasValue && !ldacAvailable) || (adaptiveChoice.HasValue && !adaptiveAvailable) ||
            (microphoneChoice.HasValue && !microphoneAvailable) || (auraChoice.HasValue && !auraAvailable))
            throw new IOException("当前耳机不支持此选项");
        byte? ldacValue = ldacAvailable ? ldacChoice ?? preference?.Ldac : null;
        byte? adaptiveValue = adaptiveAvailable ? adaptiveChoice ?? preference?.Adaptive : null;
        bool? microphoneValue = microphoneAvailable ? microphoneChoice ?? audioMemory.DisableMicrophone : null;
        bool? auraValue = auraAvailable ? auraChoice ?? audioMemory.Auracast : null;
        bool changeLdac = ldacValue.HasValue && ldacValue != actual.Ldac;
        bool changeAdaptive = adaptiveValue.HasValue && adaptiveValue != actual.Adaptive;
        bool changeMicrophone = microphoneValue.HasValue && microphoneValue != actual.GameMode;
        bool changeAura = auraValue.HasValue && auraValue != (actual.Auracast == 1);
        string expectedCodec = actual.ActiveCodec;
        if (changeLdac) session.SetLdac(ldacValue!.Value);
        if (changeAdaptive) session.SetAdaptive(adaptiveValue!.Value);
        if (changeAura) session.SetAuracast(auraValue!.Value);
        if (changeLdac || changeAdaptive || changeAura)
        {
            actual = session.Snapshot();
            if (!actual.HasConnectedHeadphone || actual.HeadphoneAddress != address || actual.ActiveCodec != expectedCodec ||
                (changeLdac && actual.Ldac != ldacValue) || (changeAdaptive && actual.Adaptive != adaptiveValue) ||
                (changeAura && (actual.Auracast is not (0 or 1) || (actual.Auracast == 1) != auraValue)))
                throw new IOException("设备回读与请求不一致，请重新读取设备设置。");
        }
        if (changeMicrophone)
        {
            session.SetGame(microphoneValue!.Value);
            pendingMicrophoneChange = (actual, microphoneValue.Value);
            return actual;
        }
        if (preference == null) audioMemory.RememberConfirmed(actual);
        return actual;
    }

    private async Task<DongleSnapshot> RunAudioSession(Func<DongleController.Session, DongleSnapshot> action)
    {
        // Read before the session: the endpoint disappears while UsbDk owns the dongle.
        var volume = DongleVolume.Capture();
        bool switched = false;
        var actual = await RunAudioOperation(session =>
        {
            var value = action(session);
            switched = pendingMicrophoneChange.HasValue;
            return value;
        }, RunAudioTransport);
        if (switched && volume != null)
        {
            DisplaySnapshot(actual);
            await volume.ApplyToNewEndpointAsync();
        }
        return actual;
    }

    private async Task<DongleSnapshot> RunAudioTransport(Func<DongleController.Session, DongleSnapshot> action)
    {
        if (pendingMicrophoneChange.HasValue) await HidTransport.WaitForControlInterfaceAsync();
        return await dongle.RunAsync(action);
    }

    private async Task<DongleSnapshot> RunAudioOperation(Func<DongleController.Session, DongleSnapshot> action,
        Func<Func<DongleController.Session, DongleSnapshot>, Task<DongleSnapshot>> run)
    {
        if (pendingMicrophoneChange.HasValue) await ReadMicrophoneReadback(run);
        var actual = await run(action);
        // Pro2 restarts its USB interface after acknowledging 11/01 or 11/02.
        // Finish that session before querying the setting on the new interface.
        if (pendingMicrophoneChange.HasValue) actual = await ReadMicrophoneReadback(run);
        return actual;
    }

    private async Task<DongleSnapshot> ReadMicrophoneReadback(
        Func<Func<DongleController.Session, DongleSnapshot>, Task<DongleSnapshot>> run)
    {
        var pending = pendingMicrophoneChange!.Value;
        // The restarted Pro2 can report no headphone until the Bluetooth link is back.
        // Wait for it in the same session; a missing headphone keeps the request pending.
        var actual = await run(session =>
        {
            var value = session.Snapshot();
            return value.HasConnectedHeadphone || !session.WaitForHeadphone(pending.BeforeChange.HeadphoneAddress, TimeSpan.FromSeconds(15))
                ? value : session.Snapshot();
        });
        if (!actual.HasConnectedHeadphone)
        {
            DisplaySnapshot(actual);
            throw new IOException("耳机尚未重新连接，请稍后重新读取设备。");
        }
        if (actual.HeadphoneAddress != pending.BeforeChange.HeadphoneAddress ||
            actual.ActiveCodec != pending.BeforeChange.ActiveCodec || actual.GameMode != pending.Desired ||
            (actual.ActiveCodec == "LDAC" && pending.BeforeChange.Ldac is >= 1 and <= 3 && actual.Ldac != pending.BeforeChange.Ldac) ||
            (actual.ActiveCodec == "aptX Adaptive" && pending.BeforeChange.Adaptive is >= 1 and <= 2 && actual.Adaptive != pending.BeforeChange.Adaptive))
        {
            pendingMicrophoneChange = null;
            DisplaySnapshot(actual);
            throw new IOException("设备回读与请求不一致，请重新读取设备设置。");
        }
        audioMemory.RememberConfirmed(actual, pending.Desired);
        pendingMicrophoneChange = null;
        return actual;
    }

    private void RefreshLanguage()
    {
        peqEndpointStatus = null;
        FormatPeqState();
        ResizePeqApplyForLanguage();
        SetUpdateCheckStatus(updateCheckStatusKey);
        DrawPeq();
        title.Text = L(selectedPageHeading);
        var ldacIndex = ldac.SelectedIndex; var adaptiveIndex = adaptive.SelectedIndex;
        ldac.ItemsSource = new[] { L("音质优先"), L("标准模式"), L("连接优先") }; ldac.SelectedIndex = ldacIndex;
        adaptive.ItemsSource = new[] { L("高音质模式"), L("低延迟模式") }; adaptive.SelectedIndex = adaptiveIndex;
        UpdateUsbDetection(usbDetected);
        if (snapshot != null) DisplaySnapshot(snapshot);
        SetStatus("就绪"); UpdateWindowSizing();
    }

    private FrameworkElement BuildSettings()
    {
        var nameRow = new DockPanel();
        nameRow.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = readButton });
        var restoreButton = AsyncButton("恢复原名", RestoreOriginalName); DockPanel.SetDock(restoreButton, Dock.Right); nameRow.Children.Add(restoreButton);
        restoreButton.Margin = new Thickness(0);
        var nameButton = AsyncButton("保存名称", Rename); DockPanel.SetDock(nameButton, Dock.Right); nameRow.Children.Add(nameButton);
        restoreButton.Padding = nameButton.Padding = new Thickness(17, 8, 17, 8);
        rename.Padding = new Thickness(12, 0, 12, 0); rename.Margin = new Thickness(0, 0, 12, 0); nameRow.Children.Add(rename);
        var names = Card("设备名称", nameRow);
        usbControls.Add(names); names.IsEnabled = false;
        language.ItemsSource = UiLanguage.Names; language.SelectedIndex = Array.IndexOf(UiLanguage.Codes, UiLanguage.Current.Code);
        language.SelectionChanged += (_, _) => { try { UiLanguage.Current.Select(UiLanguage.Codes[language.SelectedIndex]); } catch (Exception ex) { language.SelectedIndex = Array.IndexOf(UiLanguage.Codes, UiLanguage.Current.Code); ShowError(ex); } };
        startup.Style = (Style)FindResource("StartupToggle"); startup.IsChecked = StartupRegistration.Enabled;
        Localize(startup, FrameworkElement.ToolTipProperty, "开机自启");
        startup.Click += (_, _) =>
        {
            try { StartupRegistration.SetEnabled(startup.IsChecked == true); }
            catch (Exception ex) { startup.IsChecked = StartupRegistration.Enabled; ShowError(ex); }
        };
        var startupRow = new DockPanel { Margin = new Thickness(0, 24, 0, 0) };
        var startupLabel = Text("开机自启"); startupLabel.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(startup, Dock.Right); startupRow.Children.Add(startup); startupRow.Children.Add(startupLabel);
        var languageRow = new DockPanel();
        language.Width = 220; DockPanel.SetDock(language, Dock.Right); languageRow.Children.Add(language);
        var languageLabel = Text("语言"); languageLabel.VerticalAlignment = VerticalAlignment.Center; languageRow.Children.Add(languageLabel);
        return Scroll(Card("软件设置", [languageRow, startupRow, .. BuildUpdateSettings()]), names, BuildDeveloperCard());
    }

    private static string ApplicationVersion => InstalledVersion.Current.Display;
    private FrameworkElement BuildAbout()
    {
        var version = Note("Version: " + ApplicationVersion); version.Margin = new Thickness(0, 0, 0, 22);
        var export = Button("导出诊断日志", ExportLog); export.Margin = new Thickness(0, 0, 0, 14);
        var identity = Card("Questyle for Windows", version,
            Row([Button("打开数据文件夹", () => Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true })), Button("Questyle 官网", () => Process.Start(new ProcessStartInfo("https://www.questyle.com/") { UseShellExecute = true })), .. BuildUpdateCheck()]));
        ((FrameworkElement)((StackPanel)identity.Child).Children[0]).Margin = new Thickness(0, 0, 0, 10);
        var logCard = Card("运行日志", export, logBox); logCard.Margin = new Thickness(0);
        return Scroll(identity, logCard);
    }

    private void HandleDeviceChange(DeviceInterfaceChange change, Func<Task> refresh)
    {
        presenceRevision++; presenceCheckPending = true;
        // UsbDk temporarily re-enumerates interfaces while a short control
        // session owns the dongle. Reconcile those notifications after release.
        if (busy) return;
        if (!change.Arrived && change.IsUsbInterface)
        {
            awaitingInterfaceRecovery = false; UpdateUsbDetection(false);
        }
        _ = refresh();
    }

    private Task RefreshUsbPresenceAsync() => RefreshUsbPresence(() => Task.Run(HidTransport.Enumerate), ReadUsb);

    private async Task RefreshUsbPresence(Func<Task<IReadOnlyList<HidDevice>>> enumerate, Func<Task> read)
    {
        presenceCheckPending = true;
        if (busy || presenceRefreshing || exitRequested) return;
        presenceRefreshing = true;
        try
        {
            do
            {
                presenceCheckPending = false;
                int revision = presenceRevision;
                var devices = await enumerate();
                if (exitRequested) return;
                if (busy) { presenceCheckPending = true; return; }
                if (revision != presenceRevision) continue;
                if (devices.Count == 0 && awaitingInterfaceRecovery) continue;
                awaitingInterfaceRecovery = false; ApplyDetectedDevices(devices);
                if (IsVisible && IsActive) await AutoReadOnce(devices.Count > 0, read);
            } while (presenceCheckPending && !busy && !exitRequested);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { presenceRefreshing = false; }
    }

    private void ApplyDetectedDevices(IReadOnlyList<HidDevice> devices)
    {
        var device = devices.FirstOrDefault(value => value.InputReportId == 0xbb) ?? devices.FirstOrDefault();
        if (snapshot == null || device == null) usbName.Text = device?.Name ?? L("未检测到蓝牙适配器");
        UpdateUsbDetection(device != null);
        AddLog("USB 检测：" + (device != null ? "已连接" : "未连接"));
    }

    private void UpdateUsbDetection(bool connected)
    {
        usbDetected = connected;
        headerStatus.Text = "●  " + L(connected ? "检测到 USB 设备" : "未插入 USB 设备");
        readButton.Content = L(connected ? "重新读取设备" : "读取设备");
        if (!connected)
        {
            // A removed dongle gets one automatic read again when it is plugged back in.
            autoReadAttempted = false;
            snapshot = null; ConnectedHeadphoneAddress = null; usbInfo.Text = supportedInfo.Text = ""; usbName.Text = L("未检测到蓝牙适配器");
            PeqAction(() => SelectPeqProfile(null));
            headphoneInfo.Text = L("未连接蓝牙设备"); sinks.SelectedItem = null; disconnectButton.IsEnabled = false;
            codecLogo.Source = null; codecLogo.Visibility = Visibility.Collapsed;
            codecs.ItemsSource = Array.Empty<CodecOption>(); codecs.SelectedIndex = -1;
            ldacField.Visibility = adaptiveField.Visibility = microphone.Visibility = auracast.Visibility = Visibility.Collapsed;
            foreach (var control in usbControls) control.IsEnabled = false;
        }
    }

    private async Task ReadWhenForeground()
    {
        if (!IsVisible || !IsActive || busy) return;
        await RefreshUsbPresenceAsync();
    }

    private async Task AutoReadOnce(bool connected, Func<Task> read)
    {
        if (!connected || snapshot != null || autoReadAttempted) return;
        autoReadAttempted = true;
        await Run(read, "读取设备");
    }
    private async Task ReadUsb()
    {
        snapshot = pendingMicrophoneChange.HasValue
            ? await ReadMicrophoneReadback(RunAudioTransport)
            : await RunAudioSession(session => RestoreSavedAudio(session, session.Snapshot()));
        RememberOriginalName(snapshot.Name); DisplaySnapshot(snapshot);
    }
    private void DisplaySnapshot(DongleSnapshot value)
    {
        snapshot = value;
        PeqAction(() => SelectPeqProfile(value.HasConnectedHeadphone ? value.HeadphoneName : null));
        ConnectedHeadphoneAddress = value.HasConnectedHeadphone ? value.HeadphoneAddress : null;
        usbName.Text = value.Name; usbInfo.Text = L("固件") + " " + value.Version;
        UpdateUsbDetection(true); rename.Text = value.Name; var currentCodec = value.HasConnectedHeadphone ? value.ActiveCodec : "未连接";
        var available = !value.HasConnectedHeadphone ? Array.Empty<CodecOption>() : value.HeadphoneCodecs.Count > 0 ? value.SupportedCodecs.Where(c => value.HeadphoneCodecs.Any(h => h.Code == c.Code)).ToArray() : value.SupportedCodecs.ToArray();
        codecs.ItemsSource = available; codecs.SelectedItem = available.FirstOrDefault(c => c.Name == value.ActiveCodec) ?? available.FirstOrDefault();
        bool ldacAvailable = value.HasConnectedHeadphone && value.Ldac is >= 1 and <= 3 && value.HeadphoneCodecs.Any(c => c.Code == 8);
        bool adaptiveAvailable = value.HasConnectedHeadphone && value.Adaptive is >= 1 and <= 2 && value.HeadphoneCodecs.Any(c => c.Code == 4);
        ldac.IsEnabled = ldacAvailable; ldac.SelectedIndex = ldacAvailable ? value.Ldac - 1 : -1; ldac.ToolTip = ldacAvailable ? null : L("当前耳机不支持此选项");
        adaptive.IsEnabled = adaptiveAvailable; adaptive.SelectedIndex = adaptiveAvailable ? value.Adaptive - 1 : -1; adaptive.ToolTip = adaptiveAvailable ? null : L("当前耳机不支持此选项");
        microphone.IsChecked = value.GameMode;
        auracast.IsChecked = value.Auracast is 0 or 1 ? value.Auracast == 1 : null;
        UpdateAudioOptions();
        UpdateCodecLogo(currentCodec);
        supportedInfo.Text = L("设备支持：") + string.Join(" / ", value.SupportedCodecs.Select(c => c.Name));
        if (value.HasConnectedHeadphone) supportedInfo.Text += "\n" + L("耳机支持：") + string.Join(" / ", value.HeadphoneCodecs.Select(c => c.Name));
        headphoneInfo.Text = !value.HasConnectedHeadphone ? L("未连接蓝牙设备") : $"{(string.IsNullOrEmpty(value.HeadphoneName) ? L("当前耳机") : value.HeadphoneName)}  ·  {value.HeadphoneAddress}  ·  {value.ActiveCodec}";
        if (value.HasConnectedHeadphone)
        {
            history.Remember(new AudioSink(value.HeadphoneAddress, value.HeadphoneName, 0));
            RefreshRememberedHeadphones(value.HeadphoneAddress);
        }
        else sinks.SelectedItem = null;
        foreach (var c in usbControls) c.IsEnabled = true;
        audioPanel.IsEnabled = disconnectButton.IsEnabled = value.HasConnectedHeadphone;
    }

    private void UpdateAudioOptions()
    {
        var selected = codecs.SelectedItem as CodecOption;
        bool connected = snapshot is { HasConnectedHeadphone: true };
        bool preview = connected && selected != null && selected.Name != snapshot!.ActiveCodec;
        bool ldacSupported = connected && snapshot!.HeadphoneCodecs.Any(codec => codec.Code == 8);
        bool adaptiveSupported = connected && snapshot!.HeadphoneCodecs.Any(codec => codec.Code == 4);
        ldacField.Visibility = selected?.Name == "LDAC" && ldacSupported ? Visibility.Visible : Visibility.Collapsed;
        adaptiveField.Visibility = selected?.Name == "aptX Adaptive" && adaptiveSupported ? Visibility.Visible : Visibility.Collapsed;
        ldac.IsEnabled = ldacSupported && (preview || snapshot!.Ldac is >= 1 and <= 3);
        adaptive.IsEnabled = adaptiveSupported && (preview || snapshot!.Adaptive is >= 1 and <= 2);
        bool lc3 = selected?.Name == "LC3";
        microphone.Visibility = connected && selected != null && !lc3 ? Visibility.Visible : Visibility.Collapsed;
        auracast.Visibility = connected && lc3 ? Visibility.Visible : Visibility.Collapsed;
        microphone.IsEnabled = connected && (preview || snapshot!.GameMode.HasValue);
        auracast.IsEnabled = connected && (preview || snapshot!.Auracast is 0 or 1);
    }

    private async Task ApplySound()
    {
        if (pendingMicrophoneChange.HasValue) throw new InvalidOperationException("请先读取设备。");
        var old = snapshot ?? throw new InvalidOperationException("请先读取设备。");
        if (!old.HasConnectedHeadphone) throw new InvalidOperationException("请先连接耳机。");
        var selected = codecs.SelectedItem as CodecOption;
        bool changingCodec = selected != null && selected.Name != old.ActiveCodec;
        byte? desiredLdac = ldacField.Visibility == Visibility.Visible && ldac.IsEnabled && ldac.SelectedIndex >= 0 ? (byte)(ldac.SelectedIndex + 1) : null;
        byte? desiredAdaptive = adaptiveField.Visibility == Visibility.Visible && adaptive.IsEnabled && adaptive.SelectedIndex >= 0 ? (byte)(adaptive.SelectedIndex + 1) : null;
        bool? desiredGame = microphone.Visibility == Visibility.Visible && microphone.IsEnabled ? microphone.IsChecked : null;
        bool? desiredAura = auracast.Visibility == Visibility.Visible && auracast.IsEnabled ? auracast.IsChecked : null;
        if (!changingCodec && (!desiredLdac.HasValue || desiredLdac == old.Ldac) && (!desiredAdaptive.HasValue || desiredAdaptive == old.Adaptive) && (!desiredGame.HasValue || desiredGame == old.GameMode) && (!desiredAura.HasValue || desiredAura == (old.Auracast == 1))) { audioMemory.RememberConfirmed(old, desiredGame, desiredAura); SetStatus("设置未变化。"); return; }
        snapshot = await RunAudioSession(s =>
        {
            var fresh = s.Snapshot();
            if (!fresh.HasConnectedHeadphone || fresh.HeadphoneAddress != old.HeadphoneAddress) throw new IOException("连接的耳机已发生变化，请重新读取设备后再设置。");
            if (changingCodec)
            {
                s.SetCodec(selected!.Code);
                var switched = s.Snapshot();
                if (!switched.HasConnectedHeadphone || switched.HeadphoneAddress != old.HeadphoneAddress || switched.ActiveCodec != selected.Name)
                    throw new IOException("设备回读与请求不一致，请重新读取设备设置。");
                return ApplyAudioChoices(s, switched, false, desiredLdac, desiredAdaptive, desiredGame, desiredAura);
            }
            return ApplyAudioChoices(s, fresh, false, desiredLdac, desiredAdaptive, desiredGame, desiredAura);
        }); audioMemory.RememberConfirmed(snapshot, desiredGame, desiredAura); DisplaySnapshot(snapshot); SetStatus(changingCodec ? "编解码已切换。" : "声音设置已应用。");
    }
    private void ConfigureHeadphoneList(ListBox list, string template)
    {
        list.FontFamily = FontFamily; list.FontSize = 14;
        list.Padding = new Thickness(0); list.BorderThickness = new Thickness(0); list.Background = Brushes.Transparent;
        list.ItemContainerStyle = (Style)FindResource("HeadphoneItemStyle");
        list.ItemTemplate = (DataTemplate)FindResource(template);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetCanContentScroll(list, false);
    }

    private static AudioSink? ClickedHeadphone(ListBox list, DependencyObject? source)
    {
        if (source == null) return null;
        for (var node = source; node != null && node != list; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase) return null;
        return (ItemsControl.ContainerFromElement(list, source) as ListBoxItem)?.Content as AudioSink;
    }

    private void RefreshRememberedHeadphones(string? selectedAddress = null)
    {
        selectedAddress ??= (sinks.SelectedItem as AudioSink)?.Address;
        sinks.Items.Clear();
        foreach (var sink in history.Headphones) sinks.Items.Add(sink);
        sinks.SelectedItem = sinks.Items.OfType<AudioSink>().FirstOrDefault(item => item.Address == selectedAddress);
    }

    private async void DeleteRememberedHeadphone(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var sink = (AudioSink)((FrameworkElement)sender).DataContext;
        await Run(() => RemoveRememberedHeadphone(sink, DisconnectSink), "删除");
    }

    private bool IsConnectedSink(AudioSink sink) => snapshot != null && IsConnectedTo(snapshot, sink);
    private static bool IsConnectedTo(DongleSnapshot value, AudioSink sink) => value.HasConnectedHeadphone && value.HeadphoneAddress.Equals(sink.Address, StringComparison.OrdinalIgnoreCase);

    private async Task RemoveRememberedHeadphone(AudioSink sink, Func<Task> disconnect)
    {
        if (IsConnectedSink(sink)) await disconnect();
        history.Remove(sink.Address); audioMemory.Remove(sink.Address);
        RefreshRememberedHeadphones();
    }

    private void BuildSearchDialog()
    {
        KeyboardNavigation.SetTabNavigation(SearchOverlay, KeyboardNavigationMode.Cycle);
        ConfigureHeadphoneList(searchResults, "SearchHeadphoneTemplate");
        searchResults.Margin = new Thickness(0, 16, 0, 20);
        searchResults.MouseLeftButtonUp += (_, e) => { if (ClickedHeadphone(searchResults, e.OriginalSource as DependencyObject) is AudioSink sink) ChooseSearchResult(sink); };
        searchResults.KeyDown += (_, e) => { if (e.Key == Key.Enter && searchResults.SelectedItem is AudioSink sink) { e.Handled = true; ChooseSearchResult(sink); } };
        searchStatus.Foreground = muted; searchStatus.Margin = new Thickness(0, 10, 0, 0);
        var panel = Column(Text("搜索蓝牙耳机", 21, true), searchStatus, searchResults, Button("取消搜索", DismissSearch));
        var dialog = new Border { Child = panel, Width = 510, MaxHeight = 500, Padding = new Thickness(28), Margin = new Thickness(30), CornerRadius = new CornerRadius(20), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        SearchOverlay.Children.Add(dialog);
        SearchOverlay.MouseLeftButtonDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, SearchOverlay)) { DismissSearch(); e.Handled = true; } };
    }

    private static void AnimateDialogValue(DependencyObject target, DependencyProperty property, double to, EventHandler? completed = null)
    {
        double from = (double)target.GetValue(property);
        target.SetValue(property, to);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop };
        if (completed != null) animation.Completed += completed;
        var clock = animation.CreateClock();
        if (target is UIElement element) element.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
        else ((Animatable)target).ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
    }

    private void ShowDialogOverlay(Grid overlay)
    {
        activeDialogOverlay = overlay;
        Root.Effect = dialogBlur;
        Root.IsEnabled = false;
        if (overlay.Visibility != Visibility.Visible) overlay.Opacity = 0;
        overlay.IsEnabled = overlay.IsHitTestVisible = true;
        overlay.Visibility = Visibility.Visible;
        AnimateDialogValue(dialogBlur, BlurEffect.RadiusProperty, 10);
        AnimateDialogValue(overlay, OpacityProperty, 1);
    }

    private void DismissDialogOverlay(Grid overlay)
    {
        if (activeDialogOverlay != overlay) return;
        activeDialogOverlay = null;
        overlay.IsEnabled = overlay.IsHitTestVisible = false;
        AnimateDialogValue(dialogBlur, BlurEffect.RadiusProperty, 0);
        AnimateDialogValue(overlay, OpacityProperty, 0, (_, _) =>
        {
            if (activeDialogOverlay == overlay) return;
            overlay.Visibility = Visibility.Collapsed;
            if (activeDialogOverlay == null) { Root.Effect = null; Root.IsEnabled = true; }
        });
    }

    private void ShowSearchDialog()
    {
        searchResults.Items.Clear(); pendingPairing = null;
        searchStatus.Text = L("正在搜索…");
        ShowDialogOverlay(SearchOverlay);
        searchResults.Focus();
    }

    private void DismissSearch()
    {
        operationCancel?.Cancel();
        DismissDialogOverlay(SearchOverlay);
    }

    private void AddSearchResult(AudioSink sink)
    {
        var existing = searchResults.Items.OfType<AudioSink>().FirstOrDefault(item => item.Address == sink.Address);
        if (existing != null) searchResults.Items[searchResults.Items.IndexOf(existing)] = sink;
        else searchResults.Items.Add(sink);
        searchStatus.Text = UiLanguage.Current.Format("找到 {0} 台耳机", searchResults.Items.Count);
    }

    private async Task OpenSearch()
    {
        if (busy) { SetStatus("正在执行上一项操作，请稍候。"); return; }
        ShowSearchDialog();
        var cancellation = new CancellationTokenSource(); operationCancel = cancellation;
        bool released = false;
        try
        {
            bool completed = false;
            await Run(async () =>
            {
                AudioSink[] found;
                try
                {
                    found = await dongle.RunAsync(s => s.Search(TimeSpan.FromSeconds(12), sink => Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(operationCancel, cancellation) && activeDialogOverlay == SearchOverlay) AddSearchResult(sink);
                    })), cancellation.Token);
                    released = true;
                }
                catch (OperationCanceledException) { released = true; throw; }
                searchStatus.Text = UiLanguage.Current.Format("搜索完成，找到 {0} 台耳机", found.Length);
                completed = true;
            }, "搜索耳机");
            if (!completed && activeDialogOverlay == SearchOverlay)
            {
                searchResults.Items.Clear(); searchStatus.Text = status.Text;
            }
        }
        finally { operationCancel = null; cancellation.Dispose(); }
        var selected = pendingPairing; pendingPairing = null;
        if (released && selected != null)
        {
            await PairSink(selected);
        }
    }

    private void ChooseSearchResult(AudioSink sink)
    {
        bool searching = operationCancel != null;
        if (searching) pendingPairing = sink;
        DismissSearch();
        if (!searching) _ = PairSink(sink);
    }

    // APK connectAudioSink allows 15 seconds for Bluetooth to connect.
    private Task ConnectSink(AudioSink sink) => ConnectWith(sink, s =>
    {
        s.ConnectReplacingCurrent(sink.Address);
        return s.WaitForHeadphone(sink.Address, TimeSpan.FromSeconds(15));
    });
    private Task PairSink(AudioSink sink) => ConnectWith(sink, s => s.ConnectDiscovered(sink.Address, TimeSpan.FromSeconds(15)));
    private Task ConnectWith(AudioSink sink, Func<DongleController.Session, bool> connect)
    {
        if (IsConnectedSink(sink)) return Task.CompletedTask;
        return Run(() => ConnectHeadphone(sink, () => RunAudioSession(s =>
        {
            bool connected;
            try { connected = connect(s); }
            catch (IOException error) { AddLog(error.Message); return s.Snapshot(); }
            if (!connected) AddLog("15 秒内未连接目标耳机");
            var updated = s.Snapshot();
            return IsConnectedTo(updated, sink) ? RestoreSavedAudio(s, updated) : updated;
        })), "连接耳机");
    }

    private async Task ConnectHeadphone(AudioSink sink, Func<Task<DongleSnapshot>> connect)
    {
        var actual = await connect();
        // Show the readback even when it is not the target, so the old connection is not left on screen.
        if (!IsConnectedTo(actual, sink)) { DisplaySnapshot(actual); throw new IOException("未能连接该耳机，请确认耳机已开机且未被其他设备占用。"); }
        history.Remember(sink);
        DisplaySnapshot(actual);
    }
    private async Task DisconnectSink()
    {
        snapshot = await dongle.RunAsync(s => { s.Disconnect(); return s.Snapshot(); });
        DisplaySnapshot(snapshot);
        if (snapshot.HasConnectedHeadphone) throw new IOException("耳机仍处于连接状态，请重新读取设备。");
    }
    private Task Rename() => RenameTo(rename.Text);
    private Task RestoreOriginalName() => RenameTo(originalDeviceName ?? throw new InvalidOperationException(L("请先读取设备。")));
    private async Task RenameTo(string name) { await dongle.RunAsync(s => { s.Rename(name); return true; }); await ReadUsb(); }
    private void RememberOriginalName(string name)
    {
        if (originalDeviceName != null) return;
        File.WriteAllText(OriginalNameFile, name); originalDeviceName = name;
    }

    private async Task Run(Func<Task> action, string label)
    {
        if (busy) { SetStatus("正在执行上一项操作，请稍候。"); return; }
        busy = true; SetStatus(label); Cursor = Cursors.AppStarting;
        try { await action(); if (status.Text == L(label)) SetStatus(UiLanguage.Current.Format("{0}完成", L(label))); }
        catch (OperationCanceledException) { SetStatus("操作已取消，USB 控制会话已释放。"); }
        catch (Exception ex) { ShowError(ex); }
        // Failed sessions also release UsbDk and temporarily remove the HID interface.
        finally { awaitingInterfaceRecovery = usbDetected; busy = false; Cursor = null; if (exitRequested) Close(); else if (presenceCheckPending) _ = RefreshUsbPresenceAsync(); }
    }
    private void ShowError(Exception ex)
    {
        string message = ex is DllNotFoundException ? "找不到 UsbDkHelper.dll，请使用完整发布目录。" : ex.Message;
        if (ex is System.ComponentModel.Win32Exception) message += "\nUSB 控制需要系统已安装并运行 UsbDk 驱动。";
        SetStatus(message); AddLog(ex.ToString());
    }
    private void SetStatus(string value) { status.Text = L(value).TrimEnd("。.!！?？,，;；:：… \r\n".ToCharArray()); status.ToolTip = status.Text; }
    private void AddLog(string value)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AddLog(value)); return; }
        var line = $"[{DateTime.Now:HH:mm:ss}] {value}"; log.AppendLine(line); if (log.Length > 200000) log.Remove(0, log.Length - 150000); logBox.Text = log.ToString(); logBox.ScrollToEnd();
    }
    private void ExportLog()
    {
        var path = Path.Combine(AppPaths.DataDirectory, "Questyle-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
        File.WriteAllText(path, log.ToString()); SetStatus("诊断日志已保存");
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exitRequested && updating) { e.Cancel = true; return; }
        if (!exitRequested) { e.Cancel = true; DismissSearch(); closeButton.Tag = null; closeButton.ReleaseMouseCapture(); Hide(); return; }
        if (busy) { e.Cancel = true; operationCancel?.Cancel(); SetStatus("正在结束操作…"); return; }
        deviceWatcher?.Dispose(); deviceWatcher = null;
        StopPeqMonitoring();
        StopUpdateChecks();
        try { File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "last-session.log"), log.ToString()); }
        catch (IOException ex) { Trace.WriteLine(ex); }
    }

    public void Restore()
    {
        closeButton.Tag = null; closeButton.ReleaseMouseCapture();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void RequestExit() { pendingPairing = null; exitRequested = true; DismissSearch(); Close(); }

    private static bool IsDragBackground(DependencyObject source)
    {
        for (DependencyObject? node = source; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase or TextBoxBase or Selector or Thumb || node is FrameworkElement { Tag: "Card" }) return false;
        return true;
    }

    private void MoveFromBackground(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || !IsDragBackground(source)) return;
        if (e.ClickCount == 1) DragMove();
    }

    private void UpdateCodecLogo(string codec)
    {
        var asset = codec switch
        {
            "LDAC" => ("ldac", new Int32Rect(1, 12, 223, 80), 34d),
            "aptX Adaptive" => ("aptx_adaptive", new Int32Rect(0, 0, 527, 104), 42d),
            "aptX HD" => ("aptx_adaptive_hd", new Int32Rect(0, 0, 395, 104), 42d),
            "aptX" => ("aptx", new Int32Rect(0, 0, 391, 104), 42d),
            "LHDC" => ("lhdc", new Int32Rect(0, 8, 156, 36), 32d),
            "AAC" => ("aac", new Int32Rect(0, 16, 254, 76), 34d),
            "LC3" => ("le_audio", new Int32Rect(0, 26, 287, 52), 32d),
            "SBC" => ("sbc", new Int32Rect(0, 12, 204, 80), 34d),
            _ => ("", Int32Rect.Empty, 0d)
        };
        codecLogo.Source = asset.Item1.Length == 0 ? null : new CroppedBitmap(Asset("codecs", asset.Item1 + ".png"), asset.Item2);
        codecLogo.Height = asset.Item3; codecLogo.Width = asset.Item3 == 0 ? 0 : asset.Item2.Width * asset.Item3 / asset.Item2.Height;
        codecLogo.ToolTip = codec;
        codecLogo.Visibility = pages["USB"].Visibility == Visibility.Visible && codecLogo.Source != null ? Visibility.Visible : Visibility.Collapsed;
    }
}
