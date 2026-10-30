using Microsoft.Win32;
using Questyle.Desktop.Services;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace Questyle.Desktop;

public partial class MainWindow
{
    private readonly CheckBox peqEnabled = new();
    private readonly CheckBox peqBandsEnabled = new() { IsChecked = true };
    private readonly TextBox peqPreamp = new() { Width = 64, Height = 36, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center, Text = "0" };
    private readonly TextBlock peqState = new();
    private string peqStateText = "";
    private readonly Canvas peqCurve = new() { Height = 162, ClipToBounds = true, Background = Brushes.Transparent };
    private readonly TextBlock peqWarning = new() { Foreground = Color("#CE2038"), FontSize = 14, Margin = new Thickness(40, 10, 12, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed, Opacity = 0 };
    private bool peqWarningShown;
    private readonly Border peqModeSelector = new() { Visibility = Visibility.Collapsed };
    private readonly TranslateTransform peqModeIndicator = new();
    private const double PeqModeSegmentWidth = 90;
    private Border peqApplySlot = null!;
    private bool peqLive = true;
    private Func<PeqSettings, long> peqWriteConfiguration = PeqInstallation.WriteConfiguration;
    private Func<PeqEndpoint?> peqFindEndpoint = PeqEndpoint.FindActive;
    private long? peqLiveRevision;
    private readonly ItemsControl peqRows = new();
    private readonly List<PeqRow> peqEditors = [];
    private readonly DispatcherTimer peqSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer peqFileTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer peqStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer peqRangeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer peqNoticeTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private PeqRangeHint? peqRangeHint;
    private Button peqInstall = null!, peqApply = null!, peqAdd = null!, peqImport = null!, peqExport = null!, peqReset = null!;
    private PeqSettings? peqBeforeReset;
    private Border peqCard = null!;
    private ScrollViewer peqTable = null!;
    private FileSystemWatcher? peqWatcher;
    private string? peqFile, peqFileContent, peqEndpointStatus;
    private PeqSettings? peqPendingSettings;
    private bool peqLoading, peqProfileLoaded;
    private double peqPreviewRate = 48000;
    private double peqFrequencyScale = 2;
    private Point? peqCurveDrag;
    private double peqCurveDragScale;
    private bool peqDrawPending, peqResponseDirty, peqAxisDirty;
    private Point? peqHoverPosition;
    private (double Frequency, double Db)[] peqSamples = [];
    private readonly List<(double Frequency, Line Line, TextBlock Label, double Width)> peqFrequencyMarks = [];
    private readonly List<(Line Line, TextBlock Label)> peqGainMarks = [];
    private readonly HashSet<TextBlock> peqVisibleFrequencyLabels = [];
    private PeqCurveVisual? peqResponseLine;
    private readonly TranslateTransform peqDotPosition = new(), peqTagPosition = new();
    private readonly Ellipse peqHoverDot = new() { Width = 6, Height = 6, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = .45, IsHitTestVisible = false, Visibility = Visibility.Collapsed, SnapsToDevicePixels = false, UseLayoutRounding = false, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = .22 } };
    private readonly TextBlock peqHoverText = new() { FontSize = 10, Foreground = Brushes.Black };
    private readonly Border peqHoverTag = new() { Background = Brushes.White, BorderBrush = Brushes.Black, BorderThickness = new Thickness(.45), CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 1, 4, 1), IsHitTestVisible = false, Visibility = Visibility.Collapsed, SnapsToDevicePixels = false, UseLayoutRounding = false, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = .22 } };
    private double peqPlotLeft, peqPlotRight, peqPlotTop, peqPlotBottom, peqPlotUpper, peqPlotMaximum, peqPlotMinimum;
    private double peqMapWeight, peqLogSpan, peqExpandedSpan, peqMap250, peqMap500, peqMap1000, peqMidTransfer, peqHighTransfer;
    private bool peqValueDragging, peqVerticalAnimating;
    private double peqTargetMaximum, peqTargetMinimum;
    private long peqRangeFrameTime;
    private bool peqSmoothEditPending, peqResponseAnimating, peqFrequencyAnimating;
    private double[] peqResponseFrom = [], peqResponseTarget = [];
    private double peqRangeFromMaximum, peqRangeFromMinimum, peqFrequencyFrom, peqFrequencyTarget;
    private long peqResponseStart, peqFrequencyStart;
    private const double PeqTransitionSeconds = .18;
    private PeqEndpoint? peqEndpoint;
    private sealed record PeqRow(CheckBox Enabled, ComboBox Type, TextBox Frequency, TextBox Gain, TextBox Q);

    private FrameworkElement BuildPeq()
    {
        peqHoverDot.RenderTransform = peqDotPosition; peqHoverTag.RenderTransform = peqTagPosition;
        var preampLabel = Text("前级"); preampLabel.VerticalAlignment = VerticalAlignment.Center; preampLabel.Margin = new Thickness(0, 0, 10, 0);
        var preampInput = new Grid { Width = 76 };
        preampInput.Children.Add(peqPreamp);
        var preampUnit = new TextBlock { Text = "dB", FontSize = 11, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0), IsHitTestVisible = false };
        preampInput.Children.Add(preampUnit);
        peqState.FontSize = 13; peqState.Foreground = muted; peqState.TextTrimming = TextTrimming.CharacterEllipsis; peqState.VerticalAlignment = VerticalAlignment.Center; peqState.MaxWidth = 260;
        var plotContent = new Grid(); plotContent.Children.Add(peqCurve); plotContent.Children.Add(peqWarning);
        var plot = new Border { Background = Color("#F7F9FC"), BorderBrush = Color("#E7E9ED"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Child = plotContent, Margin = new Thickness(0, 0, 0, 8) };
        var tableHeader = PeqGrid(); tableHeader.HorizontalAlignment = HorizontalAlignment.Left;
        string[] labels = ["启用", "类型", "频率 (Hz)", "增益 (dB)", "Q", ""];
        for (int i = 0; i < labels.Length; i++)
        {
            var label = Text(labels[i], 13); label.Foreground = muted;
            label.Margin = i is >= 1 and <= 4 ? new Thickness(24, 5, 15, 5) : new Thickness(0, 5, 0, 5);
            FrameworkElement cell = label;
            if (i == 0)
            {
                label.VerticalAlignment = VerticalAlignment.Center;
                peqBandsEnabled.Style = (Style)FindResource("StartupToggle");
                peqBandsEnabled.LayoutTransform = new ScaleTransform(2d / 3, 2d / 3);
                peqBandsEnabled.Margin = new Thickness(6, 0, 0, 0);
                Localize(peqBandsEnabled, ToolTipProperty, "启用滤波器");
                cell = Row(label, peqBandsEnabled); cell.HorizontalAlignment = HorizontalAlignment.Center; cell.Margin = new Thickness(0, 0, 7, 0);
            }
            Grid.SetColumn(cell, i); tableHeader.Children.Add(cell);
        }
        var rowPanel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        peqRows.ItemsPanel = new ItemsPanelTemplate(rowPanel);
        VirtualizingPanel.SetIsVirtualizing(peqRows, true); VirtualizingPanel.SetVirtualizationMode(peqRows, VirtualizationMode.Recycling); VirtualizingPanel.SetScrollUnit(peqRows, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLength(peqRows, new VirtualizationCacheLength(0));
        var rowViewport = new FrameworkElementFactory(typeof(ScrollViewer), "PeqViewport");
        rowViewport.SetValue(ScrollViewer.CanContentScrollProperty, true);
        rowViewport.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        rowViewport.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        rowViewport.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        peqRows.Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = rowViewport };
        peqRows.ApplyTemplate(); peqTable = (ScrollViewer)peqRows.Template.FindName("PeqViewport", peqRows);
        peqTable.ScrollChanged += (_, e) =>
        {
            if (e.OriginalSource != peqTable) return;
            if (e.VerticalChange != 0) HidePeqRangeHint();
            if (e.ViewportWidthChange != 0) { tableHeader.Width = peqTable.ViewportWidth; SizePeqRows(); }
        };
        peqAdd = Button("添加滤波器", () => PeqUserAction(() => { if (peqEditors.Count == 64) throw new InvalidDataException(L("最多支持 64 段滤波器。")); AddPeqRow(new(true, "PK", 1000, 0, 1)); PeqEdited(); ShowPeqNotice(L("已添加滤波器")); }));
        peqAdd.Background = readAccent; peqAdd.Foreground = Brushes.White;
        peqImport = Button("导入", () => PeqUserAction(ImportPeq));
        peqExport = Button("导出", ShowPeqExport);
        peqApply = Button("应用", () => _ = ApplyPeq()); peqApply.Background = applyAccent; peqApply.Foreground = Brushes.White;
        peqApply.Padding = new Thickness(10);
        peqApply.Margin = new Thickness(8, 0, 0, 4);
        peqApplySlot = new Border { Child = peqApply, Width = 0, Opacity = 0, ClipToBounds = true, IsHitTestVisible = false };
        peqInstall = Button("安装音频驱动", () => _ = ChangePeqInstallation(peqEndpoint is { Installed: false }));
        peqReset = Button("重置", () => PeqUserAction(ResetPeq));
        peqReset.Background = accent; peqReset.Foreground = Brushes.White;
        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 12), VerticalAlignment = VerticalAlignment.Center };
        foreach (FrameworkElement item in new FrameworkElement[] { preampLabel, preampInput, peqAdd, peqApplySlot, peqImport, peqExport, peqInstall, peqReset })
        {
            if (item is Button button) { button.Margin = new Thickness(8, 0, 0, 4); button.Padding = new Thickness(10); }
            controls.Children.Add(item);
        }
        var editor = new Grid();
        Localize(peqWarning, TextBlock.TextProperty, "EQ增益高于前级衰减，可能导致破音");
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) editor.RowDefinitions.Add(new() { Height = height });
        UIElement[] sections = [controls, plot, tableHeader, peqRows];
        for (int i = 0; i < sections.Length; i++) { Grid.SetRow(sections[i], i); editor.Children.Add(sections[i]); }
        peqCard = Card(""); peqCard.Child = editor; peqCard.Padding = new Thickness(20); peqCard.Margin = new Thickness(0);
        var page = Scroll(peqCard);
        peqEnabled.Click += (_, _) => PeqEdited(); peqCurve.SizeChanged += (_, _) => DrawPeq();
        peqBandsEnabled.Click += (_, _) =>
        {
            UpdatePeqBandToggles(); PeqEdited();
            PeqAction(FlushPeq);
        };
        ConfigurePeqInput(peqPreamp, "Preamp");
        SmoothScrolling.SetHandlesWheel(peqPreamp, true);
        peqPreamp.PreviewMouseWheel += (_, e) => { if (double.TryParse(peqPreamp.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)) { SetPeqInputValue(peqPreamp, "Preamp", value + (e.Delta > 0 ? 1 : -1)); e.Handled = true; } };
        peqCurve.MouseLeftButtonDown += (_, e) => { peqFrequencyAnimating = false; peqCurveDrag = e.GetPosition(peqCurve); peqCurveDragScale = peqFrequencyScale; peqCurve.CaptureMouse(); e.Handled = true; };
        peqCurve.MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Right) return;
            peqCurveDrag = null;
            peqCurve.ReleaseMouseCapture();
            peqFrequencyFrom = peqFrequencyScale; peqFrequencyTarget = 2;
            peqFrequencyStart = System.Diagnostics.Stopwatch.GetTimestamp(); peqFrequencyAnimating = true;
            RequestPeqDraw(false);
            e.Handled = true;
        };
        peqCurve.MouseMove += (_, e) =>
        {
            var point = e.GetPosition(peqCurve);
            peqHoverPosition = new Rect(peqCurve.RenderSize).Contains(point) ? point : null;
            if (peqCurveDrag is { } start) { peqFrequencyScale = Math.Clamp(peqCurveDragScale - (point.X - start.X) / 180, 1, 3); RequestPeqDraw(false); e.Handled = true; }
            else RequestPeqDraw(false, false);
        };
        peqCurve.MouseLeave += (_, _) => { peqHoverPosition = null; UpdatePeqHover(); };
        peqCurve.MouseLeftButtonUp += (_, e) => { if (peqCurveDrag != null) { peqCurve.ReleaseMouseCapture(); e.Handled = true; } };
        peqCurve.LostMouseCapture += (_, _) => peqCurveDrag = null;
        peqSaveTimer.Tick += (_, _) => { peqSaveTimer.Stop(); PeqAction(FlushPeq); };
        peqFileTimer.Tick += (_, _) => { peqFileTimer.Stop(); PeqAction(ReloadPeqFile); };
        peqStatusTimer.Tick += (_, _) =>
        {
            if (!IsVisible || busy) return;
            try { RefreshPeqEndpoint(); } catch (Exception ex) { peqStatusTimer.Stop(); PeqError(ex); }
        };
        peqRangeTimer.Tick += (_, _) =>
        {
            peqRangeTimer.Stop();
            if (peqRangeHint is not { } hint) return;
            var fade = new DoubleAnimation(hint.Opacity, 0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => { if (peqRangeHint == hint) HidePeqRangeHint(); };
            hint.BeginAnimation(OpacityProperty, fade);
        };
        peqNoticeTimer.Tick += (_, _) =>
        {
            peqNoticeTimer.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) => PeqNotice.Visibility = Visibility.Collapsed;
            PeqNotice.BeginAnimation(OpacityProperty, fade);
        };
        SetPeq(PeqSettings.Flat); UpdatePeqControls();
        return page;
    }
    private static Grid PeqGrid()
    {
        var grid = new Grid();
        foreach (var width in new[] { new GridLength(104), new GridLength(100), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(1, GridUnitType.Star), new GridLength(44) }) grid.ColumnDefinitions.Add(new() { Width = width });
        return grid;
    }
    private FrameworkElement BuildPeqModeSelector()
    {
        var grid = new Grid { Width = PeqModeSegmentWidth * 2, Height = 36 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(PeqModeSegmentWidth) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(PeqModeSegmentWidth) });
        var indicator = new Border { Background = Color("#F5F6F8"), CornerRadius = new CornerRadius(7), Margin = new Thickness(2), RenderTransform = peqModeIndicator, IsHitTestVisible = false, Effect = (System.Windows.Media.Effects.Effect)FindResource("ControlShadow") };
        grid.Children.Add(indicator);
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var hitArea = new FrameworkElementFactory(typeof(Border));
        hitArea.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        hitArea.AppendChild(presenter); template.VisualTree = hitArea;
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(TemplateProperty, template)); style.Setters.Add(new Setter(FocusVisualStyleProperty, null)); style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        var live = Button("实时生效", () => SetPeqMode(true)); var manual = Button("手动保存", () => SetPeqMode(false));
        foreach (var button in new[] { live, manual }) { button.Style = style; button.FontSize = 14; button.Padding = new Thickness(0); button.Margin = new Thickness(0); grid.Children.Add(button); }
        Grid.SetColumn(manual, 1);
        peqModeSelector.UseLayoutRounding = false;
        peqModeSelector.Background = Color("#E7EAF0"); peqModeSelector.BorderBrush = (Brush)FindResource("ControlBorderBrush"); peqModeSelector.Effect = (System.Windows.Media.Effects.Effect)FindResource("ControlShadow"); peqModeSelector.BorderThickness = new Thickness(1);
        peqModeSelector.CornerRadius = new CornerRadius(9); peqModeSelector.Margin = new Thickness(28, 0, 0, 0); peqModeSelector.VerticalAlignment = VerticalAlignment.Center; peqModeSelector.Child = grid;
        return peqModeSelector;
    }
    private void SetPeqMode(bool live)
    {
        if (peqLive == live) return;
        PeqAction(FlushPeq);
        peqLive = live;
        if (!live) peqLiveRevision = null;
        double indicatorX = peqModeIndicator.X, targetX = live ? 0 : PeqModeSegmentWidth;
        peqModeIndicator.X = targetX;
        peqModeIndicator.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(indicatorX, targetX, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        double currentWidth = peqApplySlot.ActualWidth, targetWidth = live ? 0 : PeqApplyWidth();
        double currentOpacity = peqApplySlot.Opacity;
        peqApplySlot.Width = currentWidth; peqApplySlot.Opacity = currentOpacity; peqApplySlot.IsHitTestVisible = false;
        var widthAnimation = new DoubleAnimation(currentWidth, targetWidth, TimeSpan.FromMilliseconds(190)) { BeginTime = TimeSpan.FromMilliseconds(170), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        widthAnimation.Completed += (_, _) => { peqApplySlot.Width = targetWidth; peqApplySlot.BeginAnimation(WidthProperty, null); peqApplySlot.IsHitTestVisible = !peqLive; };
        peqApplySlot.BeginAnimation(WidthProperty, widthAnimation);
        var opacityAnimation = new DoubleAnimation(currentOpacity, live ? 0 : 1, TimeSpan.FromMilliseconds(160)) { BeginTime = TimeSpan.FromMilliseconds(170) };
        opacityAnimation.Completed += (_, _) => { peqApplySlot.Opacity = live ? 0 : 1; peqApplySlot.BeginAnimation(OpacityProperty, null); };
        peqApplySlot.BeginAnimation(OpacityProperty, opacityAnimation);
        if (live && peqProfileLoaded) PeqAction(() => WriteLivePeq(GetPeq()));
    }
    private void ResizePeqApplyForLanguage()
    {
        if (peqLive) return;
        peqApplySlot.BeginAnimation(WidthProperty, null); peqApplySlot.Width = PeqApplyWidth(); peqApplySlot.IsHitTestVisible = true;
    }
    private double PeqApplyWidth()
    {
        // Translation bindings can update after the language callback; measure the new
        // translated label directly rather than the old constrained button content.
        var label = new TextBlock { Text = L("应用"), FontFamily = peqApply.FontFamily, FontSize = peqApply.FontSize, FontWeight = peqApply.FontWeight, FontStyle = peqApply.FontStyle, FontStretch = peqApply.FontStretch };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = label.DesiredSize.Width + peqApply.Padding.Left + peqApply.Padding.Right + peqApply.BorderThickness.Left + peqApply.BorderThickness.Right + peqApply.Margin.Left + peqApply.Margin.Right;
        double dpi = VisualTreeHelper.GetDpi(peqApply).DpiScaleX;
        return (Math.Ceiling(width * dpi) + 1) / dpi;
    }
    private static (double Minimum, double Maximum, string Unit) PeqInputRange(string parameter) => parameter switch
    {
        "Frequency" => (10, 20000, "Hz"),
        "Gain" => (-20, 20, "dB"),
        "Q" => (.1, 30, ""),
        "Preamp" => (-20, 10, "dB"),
        _ => throw new ArgumentException("Unknown PEQ parameter", nameof(parameter))
    };
    private static bool PeqInputSyntax(string parameter, string text)
    {
        if (parameter == "Frequency") return Regex.IsMatch(text, "\\A[0-9]*\\z");
        var separator = Regex.Escape(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
        var pattern = parameter is "Gain" or "Preamp" ? $"\\A-?[0-9]*(?:{separator}[0-9]*)?\\z" : $"\\A[0-9]*(?:{separator}[0-9]{{0,2}})?\\z";
        return Regex.IsMatch(text, pattern);
    }
    private static bool ValidPeqInput(string parameter, string text)
    {
        if (!PeqInputSyntax(parameter, text)) return false;
        if (text.Length == 0 || text == "-" || text == CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator || text == "-" + CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator) return true;
        var (minimum, maximum, _) = PeqInputRange(parameter);
        // Positive lower-bound prefixes remain editable (e.g. 3 -> 31 Hz, 0 -> 0.1 Q).
        return double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) && value <= maximum && value >= Math.Min(0, minimum);
    }
    private void ConfigurePeqInput(TextBox box, string parameter)
    {
        InputMethod.SetIsInputMethodEnabled(box, false);
        string accepted = box.Text;
        int selectionStart = box.SelectionStart, selectionLength = box.SelectionLength;
        bool restoring = false;
        bool accepts(string insert)
        {
            var candidate = box.Text.Remove(box.SelectionStart, box.SelectionLength).Insert(box.SelectionStart, insert);
            if (!PeqInputSyntax(parameter, candidate)) return false;
            if (ValidPeqInput(parameter, candidate)) return true;
            ShowPeqRangeHint(box, parameter); return false;
        }
        box.PreviewTextInput += (_, e) => e.Handled = !accepts(e.Text);
        DataObject.AddPastingHandler(box, (_, e) => { if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) || !accepts((string)e.DataObject.GetData(DataFormats.UnicodeText))) e.CancelCommand(); });
        box.SelectionChanged += (_, _) => { if (!restoring && box.Text == accepted) { selectionStart = box.SelectionStart; selectionLength = box.SelectionLength; } };
        box.TextChanged += (_, e) =>
        {
            if (restoring) return;
            var text = box.Text;
            if (!peqLoading)
            {
                var (minimum, _, _) = PeqInputRange(parameter);
                bool deletion = e.Changes.Any(change => change.RemovedLength > 0) && e.Changes.All(change => change.AddedLength == 0);
                bool number = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value);
                if (text.Length == 0 || deletion && (!number || value < minimum))
                {
                    text = minimum.ToString("G", CultureInfo.CurrentCulture);
                    ShowPeqRangeHint(box, parameter);
                }
                else if (!ValidPeqInput(parameter, text))
                {
                    if (PeqInputSyntax(parameter, text)) ShowPeqRangeHint(box, parameter);
                    restoring = true;
                    try { box.Text = accepted; box.Select(Math.Min(selectionStart, accepted.Length), Math.Min(selectionLength, accepted.Length - Math.Min(selectionStart, accepted.Length))); }
                    finally { restoring = false; }
                    return;
                }
                else if (number && value < minimum) ShowPeqRangeHint(box, parameter);
            }
            if (box.Text != text)
            {
                restoring = true;
                try { box.Text = text; box.CaretIndex = text.Length; }
                finally { restoring = false; }
            }
            accepted = box.Text; selectionStart = box.SelectionStart; selectionLength = box.SelectionLength;
            PeqEdited();
        };
        void commit()
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || value < PeqInputRange(parameter).Minimum)
            {
                ShowPeqRangeHint(box, parameter);
                box.Text = PeqInputRange(parameter).Minimum.ToString("G", CultureInfo.CurrentCulture);
            }
        }
        box.LostKeyboardFocus += (_, _) => commit();
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { commit(); e.Handled = true; } };
        box.Unloaded += (_, _) => { if (peqRangeHint?.AdornedElement == box) HidePeqRangeHint(); };
    }
    private void ShowPeqRangeHint(TextBox box, string parameter)
    {
        if (peqRangeHint?.AdornedElement != box)
        {
            HidePeqRangeHint();
            var (minimum, maximum, unit) = PeqInputRange(parameter);
            peqRangeHint = new PeqRangeHint(box, $"{minimum:G} – {maximum:G}{(unit.Length == 0 ? "" : " " + unit)}");
            peqRangeHint.Layer.Add(peqRangeHint);
        }
        double opacity = peqRangeHint.Opacity;
        peqRangeHint.Opacity = 1;
        peqRangeHint.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
        peqRangeTimer.Stop(); peqRangeTimer.Start();
    }
    private void HidePeqRangeHint()
    {
        peqRangeTimer.Stop();
        if (peqRangeHint is not { } hint) return;
        peqRangeHint = null; hint.BeginAnimation(OpacityProperty, null);
        hint.Layer.Remove(hint);
    }
    private void SetPeqInputValue(TextBox box, string parameter, double value)
    {
        var (minimum, maximum, _) = PeqInputRange(parameter);
        if (value < minimum || value > maximum) ShowPeqRangeHint(box, parameter);
        double clamped = Math.Clamp(value, minimum, maximum);
        box.Text = (parameter == "Frequency" ? Math.Round(clamped) : parameter == "Preamp" ? clamped : Math.Round(clamped, 2)).ToString("G", CultureInfo.CurrentCulture);
    }
    private void AddPeqRow(PeqBand band)
    {
        var enabled = new CheckBox { IsChecked = band.Enabled, IsEnabled = peqBandsEnabled.IsChecked == true, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        enabled.Tag = "PeqBandToggle";
        Localize(enabled, ToolTipProperty, "启用滤波器");
        var type = new ComboBox { ItemsSource = PeqSettings.Types, SelectedItem = band.Type, Height = 29.7, MinHeight = 0, Padding = new Thickness(8, 1, 28, 1) };
        TextBox number(double value, string format = "G") => new() { Text = value.ToString(format, CultureInfo.CurrentCulture), Height = 29.7, Padding = new Thickness(8, 1, 8, 1), VerticalContentAlignment = VerticalAlignment.Center };
        var row = new PeqRow(enabled, type, number(band.Frequency), number(band.Gain), number(band.Q, "0.##"));
        var grid = PeqGrid(); grid.Margin = new Thickness(0, 2.2, 0, 2.2);
        var remove = Button("×", () => PeqUserAction(() => { peqEditors.Remove(row); peqRows.Items.Remove(grid); PeqEdited(); ShowPeqNotice(L("已删除滤波器")); })); remove.Height = 29.7; remove.Padding = new Thickness(8, 0, 8, 0); Localize(remove, ToolTipProperty, "删除滤波器");
        FrameworkElement[] cells = [enabled, type, row.Frequency, row.Gain, row.Q, remove];
        for (int i = 0; i < cells.Length; i++) { double spacing = i is >= 1 and <= 4 ? 15 : 6; cells[i].Margin = new Thickness(spacing, 0, spacing, 0); Grid.SetColumn(cells[i], i); grid.Children.Add(cells[i]); }
        ConfigurePeqInput(row.Frequency, "Frequency"); ConfigurePeqInput(row.Gain, "Gain"); ConfigurePeqInput(row.Q, "Q");
        ConfigurePeqValueDrag(row.Frequency, "Frequency"); ConfigurePeqValueDrag(row.Gain, "Gain"); ConfigurePeqValueDrag(row.Q, "Q");
        type.SelectionChanged += (_, _) => { row.Gain.IsEnabled = type.SelectedItem is "PK" or "LSC" or "HSC"; if (!row.Gain.IsEnabled) row.Gain.Text = "0"; PeqEdited(); };
        row.Gain.IsEnabled = band.Type is "PK" or "LSC" or "HSC";
        enabled.Click += (_, _) => PeqEdited();
        peqEditors.Add(row); peqRows.Items.Add(grid);
        SizePeqRow(row, grid);
    }
    private void SizePeqRows()
    {
        for (int i = 0; i < peqEditors.Count; i++) SizePeqRow(peqEditors[i], (Grid)peqRows.Items[i]);
    }
    private void SizePeqRow(PeqRow row, Grid grid)
    {
        // Fix each editor's measured width once the viewport exists, so text changes
        // only lay out that editor instead of propagating intrinsic sizes through every row.
        if (peqTable.ViewportWidth <= 248) return;
        grid.Width = peqTable.ViewportWidth; grid.Height = 29.7;
        row.Type.Width = 70;
        row.Frequency.Width = row.Gain.Width = row.Q.Width = (peqTable.ViewportWidth - 248) / 3 - 30;
    }
    private void SetPeq(PeqSettings settings)
    {
        settings.Validate(); peqLoading = true;
        peqVerticalAnimating = peqResponseAnimating = peqSmoothEditPending = false; peqRangeFrameTime = 0;
        HidePeqRangeHint();
        ClearPeqUndo();
        try
        {
            peqEnabled.IsChecked = settings.Enabled; peqBandsEnabled.IsChecked = settings.BandsEnabled; peqPreamp.Text = settings.Preamp.ToString("G", CultureInfo.CurrentCulture);
            peqEditors.Clear(); peqRows.Items.Clear(); foreach (var band in settings.Bands) AddPeqRow(band);
        }
        finally { peqLoading = false; }
        DrawPeq();
    }
    private void ClearPeqUndo()
    {
        if (peqBeforeReset == null) return;
        peqBeforeReset = null;
        Localize(peqReset, ContentControl.ContentProperty, "重置");
    }
    private void ResetPeq()
    {
        if (peqBeforeReset is { } previous)
        {
            SetPeq(previous); PeqEdited(); ShowPeqNotice(L("已撤销重置"));
            return;
        }
        var current = GetPeq();
        SetPeq(current with { Bands = [new(true, "PK", 1000, 0, 1)] });
        PeqEdited();
        peqBeforeReset = current;
        Localize(peqReset, ContentControl.ContentProperty, "撤销重置");
        ShowPeqNotice(L("滤波器已重置"));
    }
    private PeqSettings GetPeq()
    {
        double number(TextBox box) => double.Parse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture);
        var settings = new PeqSettings(peqEnabled.IsChecked == true, number(peqPreamp), [.. peqEditors.Select(row => new PeqBand(row.Enabled.IsChecked == true, (string)row.Type.SelectedItem, number(row.Frequency), number(row.Gain), number(row.Q)))]) { BandsEnabled = peqBandsEnabled.IsChecked == true };
        settings.Validate(); return settings;
    }
    private void ConfigurePeqValueDrag(TextBox box, string parameter)
    {
        Point? start = null;
        double initial = 0;
        SmoothScrolling.SetHandlesWheel(box, true);
        box.PreviewMouseWheel += (_, e) =>
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)) return;
            SetPeqInputValue(box, parameter, PeqDragTarget(parameter, value, e.Delta / 12d));
            e.Handled = true;
        };
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var point = e.GetPosition(box);
            if (point.X < box.ActualWidth / 2 || !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out initial)) return;
            start = e.GetPosition(this); peqValueDragging = true; box.CaptureMouse(); e.Handled = true;
        };
        box.PreviewMouseMove += (_, e) =>
        {
            var point = e.GetPosition(box);
            box.Cursor = start != null || point.X >= box.ActualWidth / 2 ? parameter == "Frequency" ? Cursors.SizeWE : Cursors.SizeNS : Cursors.IBeam;
            if (start is not { } origin) return;
            var windowPoint = e.GetPosition(this);
            double delta = parameter == "Frequency" ? windowPoint.X - origin.X : origin.Y - windowPoint.Y;
            double target = PeqDragTarget(parameter, initial, delta);
            var (minimum, maximum, _) = PeqInputRange(parameter);
            if (target < minimum || target > maximum) ShowPeqRangeHint(box, parameter);
            box.Text = PeqDraggedValue(parameter, initial, delta).ToString("G", CultureInfo.CurrentCulture);
            e.Handled = true;
        };
        box.PreviewMouseLeftButtonUp += (_, e) => { if (start != null) { box.ReleaseMouseCapture(); e.Handled = true; } };
        box.LostMouseCapture += (_, _) => { start = null; peqValueDragging = false; };
        box.MouseLeave += (_, _) => { if (start == null) box.Cursor = Cursors.IBeam; };
    }
    private static double PeqDragTarget(string parameter, double initial, double delta) => parameter switch
    {
        "Frequency" => initial * Math.Pow(2, delta / 120),
        "Gain" => initial + delta * .05,
        "Q" => initial + delta * .01,
        _ => throw new ArgumentException("Unknown PEQ parameter", nameof(parameter))
    };
    private static double PeqDraggedValue(string parameter, double initial, double delta)
    {
        var (minimum, maximum, _) = PeqInputRange(parameter);
        double value = Math.Clamp(PeqDragTarget(parameter, initial, delta), minimum, maximum);
        return parameter == "Frequency" ? Math.Round(value) : Math.Round(value, 2);
    }
    private void SetPeqFrequencyScale(double scale)
    {
        peqFrequencyAnimating = false;
        peqFrequencyScale = Math.Clamp(scale, 1, 3);
        RepositionPeq();
    }
    private static PeqSettings ReadPeqJson(string text)
    {
        var settings = JsonSerializer.Deserialize<PeqSettings>(text) ?? throw new InvalidDataException("PEQ 配置无效");
        if (settings.Bands == null) throw new InvalidDataException("PEQ 配置无效");
        settings.Validate(); return settings;
    }
    private void BuildPeqExportDialog()
    {
        KeyboardNavigation.SetTabNavigation(PeqExportOverlay, KeyboardNavigationMode.Cycle);
        var panel = Column(Text("导出", 21, true));
        foreach (var format in new[] { "Equalizer APO", "Wavelet", "EasyEffects" })
        {
            var button = Button(format, () => ExportPeq(format)); button.Margin = new Thickness(0, 14, 0, 0); panel.Children.Add(button);
        }
        var cancel = Button("取消", DismissPeqExport); cancel.Margin = new Thickness(0, 20, 0, 0); panel.Children.Add(cancel);
        PeqExportOverlay.Children.Add(new Border { Child = panel, Width = 510, Padding = new Thickness(28), Margin = new Thickness(30), CornerRadius = new CornerRadius(20), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        PeqExportOverlay.MouseLeftButtonDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, PeqExportOverlay)) { DismissPeqExport(); e.Handled = true; } };
    }
    private void ShowPeqExport()
    {
        ShowDialogOverlay(PeqExportOverlay);
        PeqExportOverlay.Children.OfType<Border>().SelectMany(border => ((StackPanel)border.Child).Children.OfType<Button>()).First().Focus();
    }
    private void DismissPeqExport() => DismissDialogOverlay(PeqExportOverlay);
    private void ExportPeq(string format)
    {
        try
        {
            var settings = GetPeq();
            string text = format switch { "Equalizer APO" => settings.Export(), "Wavelet" => PeqExport.Wavelet(settings), "EasyEffects" => PeqExport.EasyEffects(settings), _ => throw new ArgumentException("Unknown PEQ export format", nameof(format)) };
            string extension = format == "EasyEffects" ? "json" : "txt";
            var dialog = new SaveFileDialog { Filter = extension == "json" ? "JSON (*.json)|*.json" : "Text (*.txt)|*.txt", FileName = Path.GetFileNameWithoutExtension(peqFile) + "-" + format.Replace(" ", "-") + "." + extension };
            if (dialog.ShowDialog(this) != true) { ShowPeqNotice(L("已取消导出")); return; }
            File.WriteAllText(dialog.FileName, text); DismissPeqExport(); SetPeqState(L("PEQ 配置已导出")); ShowPeqNotice(peqStateText);
        }
        catch (Exception ex) { DismissPeqExport(); PeqError(ex); ShowPeqNotice(peqStateText, true); }
    }
    private void ImportPeq()
    {
        var dialog = new OpenFileDialog { Filter = "EQ (*.txt;*.json)|*.txt;*.json" };
        if (dialog.ShowDialog(this) != true) { ShowPeqNotice(L("已取消导入")); return; }
        var text = File.ReadAllText(dialog.FileName);
        SetPeq(Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase) ? ReadPeqJson(text) : PeqSettings.Parse(text));
        PeqEdited(); ShowPeqNotice(L("PEQ 配置已导入"));
    }
    private void ShowPeqNotice(string text, bool error = false, bool pending = false)
    {
        PeqNotice.BeginAnimation(OpacityProperty, null);
        PeqNoticeText.Text = text; PeqNoticeText.Foreground = error ? Color("#CE2038") : Foreground;
        PeqNoticeCircle.Fill = error ? Color("#CE2038") : pending ? muted : applyAccent;
        PeqNoticeMark.Data = Geometry.Parse(error ? "M 6,6 L 14,14 M 14,6 L 6,14" : pending ? "M 10,5 L 10,10 L 13,12" : "M 5.5,10 L 8.5,13 L 14.5,7");
        PeqNotice.Visibility = Visibility.Visible;
        PeqNotice.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
        peqNoticeTimer.Stop(); peqNoticeTimer.Start();
    }
    private void HidePeqNotice()
    {
        peqNoticeTimer.Stop(); PeqNotice.BeginAnimation(OpacityProperty, null); PeqNotice.Visibility = Visibility.Collapsed;
    }
    private void PeqUserAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { PeqError(ex); ShowPeqNotice(peqStateText, true); }
    }
    private void SelectPeqProfile(string? model)
    {
        string? file = null;
        if (!string.IsNullOrWhiteSpace(model))
        {
            var name = string.Concat(model.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.');
            file = Path.Combine(AppPaths.EqConfigDirectory, name + ".json");
        }
        if (string.Equals(file, peqFile, StringComparison.OrdinalIgnoreCase)) return;
        try { FlushPeq(); }
        finally { peqPendingSettings = null; peqFileTimer.Stop(); peqWatcher?.Dispose(); peqWatcher = null; peqFile = null; peqProfileLoaded = false; UpdatePeqControls(); }
        peqFile = file; peqFileContent = null;
        // Clear the old model before reading, so a failed file load cannot be saved as another model.
        SetPeq(PeqSettings.Flat); UpdatePeqControls();
        if (file == null) return;
        Directory.CreateDirectory(AppPaths.EqConfigDirectory);
        var watcher = new FileSystemWatcher(AppPaths.EqConfigDirectory) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        peqWatcher = watcher;
        void changed(object sender, FileSystemEventArgs e)
        {
            if (!string.Equals(e.FullPath, file, StringComparison.OrdinalIgnoreCase) && !(e is RenamedEventArgs renamed && string.Equals(renamed.OldFullPath, file, StringComparison.OrdinalIgnoreCase))) return;
            Dispatcher.BeginInvoke(() => { if (peqWatcher != watcher) return; peqFileTimer.Stop(); peqFileTimer.Start(); });
        }
        watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed; watcher.Renamed += (sender, e) => changed(sender, e);
        watcher.Error += (_, e) => Dispatcher.BeginInvoke(() => { if (peqWatcher != watcher) return; watcher.Dispose(); peqWatcher = null; PeqError(e.GetException()); });
        watcher.EnableRaisingEvents = true;
        if (File.Exists(file)) ReloadPeqFile();
        else { SavePeq(PeqSettings.Flat); peqProfileLoaded = true; UpdatePeqControls(); WriteLivePeq(PeqSettings.Flat); }
    }
    private void ReloadPeqFile()
    {
        if (peqFile == null) return;
        var text = File.ReadAllText(peqFile);
        if (text == peqFileContent) return;
        peqSaveTimer.Stop(); peqPendingSettings = null;
        var settings = ReadPeqJson(text);
        SetPeq(settings); peqFileContent = text; peqProfileLoaded = true; UpdatePeqControls();
        if (peqEndpointStatus != null) SetPeqState(peqEndpointStatus);
        WriteLivePeq(GetPeq());
    }
    private void PeqEdited()
    {
        if (peqLoading) return;
        ClearPeqUndo(); peqSaveTimer.Stop(); peqPendingSettings = null;
        // Incomplete numeric text is left in the editor; only complete valid settings are saved.
        PeqSettings settings;
        try { settings = GetPeq(); }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException) { peqResponseDirty = false; return; }
        RequestPeqDraw(true);
        if (!peqProfileLoaded) return;
        peqPendingSettings = settings;
        peqSaveTimer.Start();
    }
    private void FlushPeq()
    {
        peqSaveTimer.Stop();
        if (peqPendingSettings is not { } settings) return;
        SavePeq(settings); peqPendingSettings = null; WriteLivePeq(settings);
    }
    private void WriteLivePeq(PeqSettings settings)
    {
        if (!peqLive || peqEndpoint is not { Installed: true }) return;
        peqLiveRevision = peqWriteConfiguration(settings);
        SetPeqState(L("配置已写入，尚未确认音频处理"));
    }
    private void SavePeq(PeqSettings settings)
    {
        if (peqFile == null) throw new InvalidOperationException("请先连接耳机");
        var text = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        if (text == peqFileContent) return;
        var temporary = peqFile + ".tmp";
        File.WriteAllText(temporary, text); File.Move(temporary, peqFile, true);
        peqFileContent = text;
        if (peqEndpointStatus != null) SetPeqState(peqEndpointStatus);
    }
    private void UpdatePeqControls()
    {
        peqPreamp.IsEnabled = peqEnabled.IsEnabled = peqBandsEnabled.IsEnabled = peqAdd.IsEnabled = peqImport.IsEnabled = peqExport.IsEnabled = peqReset.IsEnabled = peqTable.IsEnabled = peqProfileLoaded;
        peqApply.IsEnabled = peqProfileLoaded;
        UpdatePeqBandToggles();
    }
    private void UpdatePeqBandToggles()
    {
        foreach (var row in peqEditors) row.Enabled.IsEnabled = peqBandsEnabled.IsChecked == true;
    }
    private void MonitorPeqPage(bool active)
    {
        peqStatusTimer.Stop();
        if (!active) { HidePeqRangeHint(); HidePeqNotice(); return; }
        PeqAction(RefreshPeqEndpoint); peqStatusTimer.Start();
    }
    private void StopPeqMonitoring()
    {
        peqVerticalAnimating = peqResponseAnimating = peqFrequencyAnimating = false; peqRangeFrameTime = 0;
        CompositionTarget.Rendering -= RenderPeqFrame; peqDrawPending = peqResponseDirty = peqAxisDirty = false;
        HidePeqRangeHint();
        HidePeqNotice();
        PeqAction(FlushPeq);
        peqWatcher?.Dispose(); peqWatcher = null;
        peqSaveTimer.Stop(); peqFileTimer.Stop(); peqStatusTimer.Stop();
    }
    private void PeqAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { PeqError(ex); }
    }
    private void PeqError(Exception ex)
    {
        SetPeqState(L(ex switch
        {
            FormatException or OverflowException => "请输入有效数值",
            InvalidDataException => ex.Message,
            UnauthorizedAccessException => "无法写入 PEQ 配置，请检查权限",
            JsonException => "PEQ 配置无效",
            System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } => "已取消安装",
            _ => "PEQ 操作失败"
        }));
        AddLog(ex.ToString());
    }
    private void RefreshPeqEndpoint()
    {
        var previousEndpoint = peqEndpoint;
        peqEndpoint = peqFindEndpoint();
        peqInstall.IsEnabled = peqEndpoint != null; UpdatePeqControls();
        Localize(peqInstall, ContentControl.ContentProperty, peqEndpoint is { Installed: true } ? "卸载音频驱动" : "安装音频驱动");
        if (peqEndpoint == null) { SetPeqEndpointStatus(L("未找到 Dongle 播放端点")); return; }
        if (!peqEndpoint.Installed) { SetPeqEndpointStatus(L("尚未安装音频驱动")); return; }
        var status = peqEndpoint.ReadStatus();
        if (status.Error < 0) { System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(status.Error); }
        if (status.Processing && peqPreviewRate != status.Rate) { peqPreviewRate = status.Rate; DrawPeq(); }
        SetPeqEndpointStatus(L(status.Processing ? "音频驱动正在处理音频" : "音频驱动已安装，等待音频流"));
        if (peqProfileLoaded && previousEndpoint != peqEndpoint) WriteLivePeq(GetPeq());
        else if (status.Processing && peqLiveRevision == status.Revision) { SetPeqState(L("PEQ 配置已加载")); peqLiveRevision = null; }
    }
    private void SetPeqEndpointStatus(string text)
    {
        if (text != peqEndpointStatus) SetPeqState(text);
        peqEndpointStatus = text;
    }
    private void SetPeqState(string text)
    {
        peqStateText = text;
        FormatPeqState();
    }
    private void FormatPeqState()
    {
        string text = peqStateText;
        int separator = text.IndexOf('·');
        if (UiLanguage.Current.Code == "en-US" && separator >= 0)
        {
            var measure = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(peqState.FontFamily, peqState.FontStyle, peqState.FontWeight, peqState.FontStretch),
                peqState.FontSize, peqState.Foreground, VisualTreeHelper.GetDpi(peqState).PixelsPerDip);
            if (measure.WidthIncludingTrailingWhitespace > peqState.MaxWidth)
                text = text[..separator].TrimEnd() + "\n" + text[(separator + 1)..].TrimStart();
        }
        peqState.Text = text;
    }
    private async Task ChangePeqInstallation(bool install)
    {
        if (busy) return;
        busy = true;
        ShowPeqNotice(L(install ? "正在安装音频驱动" : "正在卸载音频驱动"), pending: true);
        try
        {
            var endpoint = peqFindEndpoint() ?? throw new InvalidOperationException("未找到 Dongle 播放端点");
            await PeqInstallation.ChangeAsync(endpoint, install);
            RefreshPeqEndpoint();
            if (!install) SetPeqState(L("音频驱动已卸载；必要时重新插拔 Dongle"));
            ShowPeqNotice(peqStateText);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AddLog(ex.ToString()); SetPeqState(L(install ? "已取消安装" : "已取消卸载")); ShowPeqNotice(peqStateText);
        }
        catch (Exception ex) { PeqError(ex); ShowPeqNotice(peqStateText, true); }
        finally { FinishPeqOperation(); }
    }
    private async Task ApplyPeq()
    {
        if (busy) return;
        busy = true;
        ShowPeqNotice(L("正在应用 PEQ 配置"), pending: true);
        try
        {
            var settings = GetPeq(); SavePeq(settings); peqSaveTimer.Stop(); peqPendingSettings = null;
            var endpoint = peqFindEndpoint();
            if (endpoint is not { Installed: true }) { SetPeqState(L("PEQ 配置已保存") + " · " + L(endpoint == null ? "未找到 Dongle 播放端点" : "尚未安装音频驱动")); ShowPeqNotice(peqStateText); return; }
            var revision = peqWriteConfiguration(settings);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (true)
            {
                var status = endpoint.ReadStatus();
                if (status.Error < 0) System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(status.Error);
                if (status.Processing && status.Revision == revision) { SetPeqState(L("PEQ 配置已加载")); peqPreviewRate = status.Rate; DrawPeq(); break; }
                if (DateTime.UtcNow >= deadline) { SetPeqState(L("配置已写入，尚未确认音频处理")); break; }
                await Task.Delay(100);
            }
            ShowPeqNotice(peqStateText);
        }
        catch (Exception ex) { PeqError(ex); ShowPeqNotice(peqStateText, true); }
        finally { FinishPeqOperation(); }
    }
    private void FinishPeqOperation()
    {
        busy = false;
        if (exitRequested) Close(); else if (presenceCheckPending) _ = RefreshUsbPresenceAsync();
    }
    private void RequestPeqDraw(bool responseChanged, bool axisChanged = true)
    {
        peqResponseDirty |= responseChanged;
        peqAxisDirty |= axisChanged;
        if (peqDrawPending) return;
        peqDrawPending = true;
        CompositionTarget.Rendering += RenderPeqFrame;
    }
    private void RenderPeqFrame(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= RenderPeqFrame;
        peqDrawPending = false;
        bool recalculate = peqResponseDirty, axisChanged = peqAxisDirty; peqResponseDirty = peqAxisDirty = false;
        if (recalculate) { peqSmoothEditPending = !peqValueDragging; DrawPeq(); }
        else if (axisChanged || peqVerticalAnimating || peqResponseAnimating || peqFrequencyAnimating) { AdvancePeqTransitions(); AdvancePeqRange(); UpdatePeqGainMarks(); RepositionPeq(); }
        else UpdatePeqHover();
        if (peqVerticalAnimating || peqResponseAnimating || peqFrequencyAnimating) RequestPeqDraw(false, false);
    }
    private void DrawPeq()
    {
        CompositionTarget.Rendering -= RenderPeqFrame; peqDrawPending = peqResponseDirty = peqAxisDirty = false;
        if (peqLoading || peqCurve.ActualWidth < 100) return;
        try
        {
            var current = GetPeq();
            var settings = current with { Preamp = 0, BandsEnabled = true };
            peqPlotLeft = 30; peqPlotRight = peqCurve.ActualWidth - 14; peqPlotTop = 8; peqPlotBottom = peqCurve.Height - 22;
            peqPlotUpper = Math.Min(20000, peqPreviewRate * .49);
            // A fixed log + linear grid stays dense at both ends without recalculating DSP during axis drags.
            var frequencies = Enumerable.Range(0, 400).Select(i => 20 * Math.Pow(peqPlotUpper / 20, i / 399d))
                .Concat(Enumerable.Range(0, 400).Select(i => 20 + (peqPlotUpper - 20) * i / 399d))
                .Concat(settings.Bands.Where(b => b.Enabled && b.Frequency >= 20 && b.Frequency <= peqPlotUpper).Select(b => b.Frequency)).Distinct().Order().ToArray();
            var response = settings.ResponseCurve(frequencies, peqPreviewRate);
            var samples = frequencies.Select((f, i) => (Frequency: f, Db: Math.Max(-60, response[i]))).ToArray();
            double maximum = samples.Max(s => s.Db), minimum = samples.Min(s => s.Db);
            SetPeqWarning(current.Enabled && (current.BandsEnabled ? maximum : 0) + current.Preamp > .000001);
            peqTargetMaximum = Math.Max(3, Math.Ceiling(maximum / 3) * 3);
            peqTargetMinimum = Math.Min(-3, Math.Floor(minimum / 3) * 3);
            peqResponseAnimating = peqSmoothEditPending && peqSamples.Length > 1 && peqResponseLine != null;
            peqSmoothEditPending = false;
            if (peqResponseAnimating)
            {
                peqResponseFrom = new double[samples.Length]; peqResponseTarget = new double[samples.Length];
                int previous = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    double frequency = samples[i].Frequency;
                    while (previous < peqSamples.Length - 2 && peqSamples[previous + 1].Frequency < frequency) previous++;
                    var low = peqSamples[previous]; var high = peqSamples[previous + 1];
                    double fraction = Math.Clamp((frequency - low.Frequency) / (high.Frequency - low.Frequency), 0, 1);
                    peqResponseFrom[i] = low.Db + (high.Db - low.Db) * fraction;
                    peqResponseTarget[i] = samples[i].Db;
                    samples[i].Db = peqResponseFrom[i];
                }
                peqRangeFromMaximum = peqPlotMaximum; peqRangeFromMinimum = peqPlotMinimum;
                peqResponseStart = System.Diagnostics.Stopwatch.GetTimestamp(); peqVerticalAnimating = false;
            }
            else if (peqValueDragging || peqVerticalAnimating)
            {
                peqVerticalAnimating = Math.Abs(peqPlotMaximum - peqTargetMaximum) + Math.Abs(peqPlotMinimum - peqTargetMinimum) > .002;
                AdvancePeqRange();
            }
            else { peqPlotMaximum = peqTargetMaximum; peqPlotMinimum = peqTargetMinimum; }
            peqSamples = samples;
            if (peqResponseLine == null)
            {
                peqCurve.Children.Clear(); peqGainMarks.Clear(); peqFrequencyMarks.Clear();
                for (int i = 0; i < 7; i++)
                {
                    var line = new Line { Stroke = Color(i == 3 ? "#9DAFC5" : "#DEE4ED"), StrokeThickness = i == 3 ? 1.35 : 1, IsHitTestVisible = false };
                    var label = new TextBlock { FontSize = 10, Foreground = muted, TextAlignment = TextAlignment.Right, IsHitTestVisible = false };
                    peqGainMarks.Add((line, label)); peqCurve.Children.Add(line); peqCurve.Children.Add(label);
                }
                foreach (double frequency in new[] { 20, 30, 60, 100, 250, 500, 1000, 2000, 4000, 6000, 8000, 10000, 12000, 16000 })
                {
                    var line = new Line { Stroke = Color("#E7ECF2"), StrokeThickness = 1, IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
                    var label = new TextBlock { Text = frequency < 1000 ? frequency.ToString(CultureInfo.InvariantCulture) : (frequency / 1000).ToString(CultureInfo.InvariantCulture) + "k", FontSize = 10, Foreground = muted, IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
                    StyleTransitions.SetEnabled(label, true);
                    Canvas.SetLeft(label, 0);
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    peqFrequencyMarks.Add((frequency, line, label, label.DesiredSize.Width)); peqCurve.Children.Add(line); peqCurve.Children.Add(label);
                }
                peqResponseLine = new PeqCurveVisual(readAccent); peqCurve.Children.Add(peqResponseLine);
                peqHoverTag.Child = peqHoverText; peqCurve.Children.Add(peqHoverDot); peqCurve.Children.Add(peqHoverTag);
            }
            peqResponseLine.Width = peqCurve.ActualWidth; peqResponseLine.Height = peqCurve.Height;
            UpdatePeqGainMarks();
            RepositionPeq();
            if (peqVerticalAnimating || peqResponseAnimating || peqFrequencyAnimating) RequestPeqDraw(false, false);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException or System.Runtime.InteropServices.COMException) { ClearPeqPlot("参数无效"); }
        catch (Exception ex) { ClearPeqPlot("频响计算失败"); AddLog(ex.ToString()); }
    }
    private void ClearPeqPlot(string error)
    {
        peqVerticalAnimating = peqResponseAnimating = peqFrequencyAnimating = false; peqRangeFrameTime = 0;
        peqCurve.Children.Clear(); peqResponseLine = null; peqSamples = [];
        SetPeqWarning(false);
        peqHoverDot.Visibility = peqHoverTag.Visibility = Visibility.Collapsed;
        peqCurve.Children.Add(Text(error, 13));
    }
    private void SetPeqWarning(bool visible)
    {
        if (peqWarningShown == visible) return;
        peqWarningShown = visible;
        double from = peqWarning.Opacity;
        if (visible) peqWarning.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(from, visible ? 1 : 0, TimeSpan.FromMilliseconds(140));
        if (!visible) fade.Completed += (_, _) => { if (!peqWarningShown) peqWarning.Visibility = Visibility.Collapsed; };
        peqWarning.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }
    private void AdvancePeqTransitions()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double Progress(long start)
        {
            double t = Math.Clamp((now - start) / (double)System.Diagnostics.Stopwatch.Frequency / PeqTransitionSeconds, 0, 1);
            return t * t * (3 - 2 * t);
        }
        if (peqFrequencyAnimating)
        {
            double progress = Progress(peqFrequencyStart);
            peqFrequencyScale = peqFrequencyFrom + (peqFrequencyTarget - peqFrequencyFrom) * progress;
            peqFrequencyAnimating = progress < 1;
        }
        if (peqResponseAnimating)
        {
            double progress = Progress(peqResponseStart);
            for (int i = 0; i < peqSamples.Length; i++)
                peqSamples[i].Db = peqResponseFrom[i] + (peqResponseTarget[i] - peqResponseFrom[i]) * progress;
            peqPlotMaximum = peqRangeFromMaximum + (peqTargetMaximum - peqRangeFromMaximum) * progress;
            peqPlotMinimum = peqRangeFromMinimum + (peqTargetMinimum - peqRangeFromMinimum) * progress;
            peqResponseAnimating = progress < 1;
        }
    }
    private void AdvancePeqRange()
    {
        if (!peqVerticalAnimating) { peqRangeFrameTime = 0; return; }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = peqRangeFrameTime == 0 ? 1 / 120d : (now - peqRangeFrameTime) / (double)System.Diagnostics.Stopwatch.Frequency;
        peqRangeFrameTime = now;
        double progress = 1 - Math.Exp(-dt / .035);
        peqPlotMaximum += (peqTargetMaximum - peqPlotMaximum) * progress;
        peqPlotMinimum += (peqTargetMinimum - peqPlotMinimum) * progress;
        if (Math.Abs(peqPlotMaximum - peqTargetMaximum) + Math.Abs(peqPlotMinimum - peqTargetMinimum) < .002)
        {
            peqPlotMaximum = peqTargetMaximum; peqPlotMinimum = peqTargetMinimum;
            peqVerticalAnimating = false; peqRangeFrameTime = 0;
        }
    }
    private void UpdatePeqGainMarks()
    {
        double[] gains = [peqPlotMinimum, peqPlotMinimum * 2 / 3, peqPlotMinimum / 3, 0, peqPlotMaximum / 3, peqPlotMaximum * 2 / 3, peqPlotMaximum];
        for (int i = 0; i < gains.Length; i++)
        {
            var (line, label) = peqGainMarks[i]; double py = PeqY(gains[i]);
            line.X1 = peqPlotLeft; line.X2 = peqPlotRight; line.Y1 = line.Y2 = py;
            label.Text = gains[i].ToString("0;-0;0"); label.Width = peqPlotLeft - 5;
            Canvas.SetLeft(label, 0); Canvas.SetTop(label, py - 7);
        }
    }
    private double PeqX(double frequency)
    {
        // Move 30% of the 12–16k span to 10–12k, at every axis position.
        double t = frequency <= 12000 ? (frequency - 10000) / 2000 : (16000 - frequency) / 4000;
        t = Math.Clamp(t, 0, 1);
        double basis = PeqFrequencyBasis(frequency);
        double position = basis + peqMidTransfer * t * t * (3 - 2 * t);
        if (peqPlotUpper > 16000)
        {
            // Move a small share of the final octave fragment into 12–16 kHz.
            double highT = Math.Clamp(frequency <= 16000 ? (frequency - 12000) / 4000 : (peqPlotUpper - frequency) / (peqPlotUpper - 16000), 0, 1);
            position += peqHighTransfer * highT * highT * (3 - 2 * highT);
        }
        // Keep the low-frequency change proportional and return its width within 500–1000 Hz.
        double removed = Math.Clamp(basis / peqMap250, 0, 1);
        double added = Math.Clamp((basis - peqMap500) / (peqMap1000 - peqMap500), 0, 1);
        position += .03 * peqMap250 * (added - removed);
        return peqPlotLeft + position * (peqPlotRight - peqPlotLeft);
    }
    private static double PeqExpandedFrequency(double frequency) => frequency <= 8000 ? frequency - 20 : 7980 + 4000 * Math.Log(1 + (frequency - 8000) / 4000);
    private double PeqFrequencyBasis(double frequency) => (1 - peqMapWeight) * Math.Log10(frequency / 20) / peqLogSpan + peqMapWeight * PeqExpandedFrequency(frequency) / peqExpandedSpan;
    private void UpdatePeqMapping()
    {
        peqMapWeight = (peqFrequencyScale - 1) / 2;
        peqLogSpan = Math.Log10(peqPlotUpper / 20); peqExpandedSpan = PeqExpandedFrequency(peqPlotUpper);
        peqMap250 = PeqFrequencyBasis(250); peqMap500 = PeqFrequencyBasis(500); peqMap1000 = PeqFrequencyBasis(1000);
        peqMidTransfer = .3 * (PeqFrequencyBasis(16000) - PeqFrequencyBasis(12000));
        peqHighTransfer = .15 * (PeqFrequencyBasis(peqPlotUpper) - PeqFrequencyBasis(16000));
    }
    private double PeqY(double db) => peqPlotTop + (peqPlotMaximum - db) / (peqPlotMaximum - peqPlotMinimum) * (peqPlotBottom - peqPlotTop);
    private void RepositionPeq()
    {
        if (peqResponseLine == null) return;
        UpdatePeqMapping();
        peqVisibleFrequencyLabels.Clear();
        double previousRight = double.NegativeInfinity;
        foreach (var (frequency, line, label, labelWidth) in peqFrequencyMarks)
        {
            bool visible = frequency <= peqPlotUpper; line.Opacity = visible ? 1 : 0;
            double px = PeqX(frequency); ((TranslateTransform)line.RenderTransform).X = px; line.Y1 = peqPlotTop; line.Y2 = peqPlotBottom;
            double labelX = Math.Clamp(px - labelWidth / 2, 0, peqCurve.ActualWidth - labelWidth);
            if (labelX < previousRight + 2 && previousRight + 2 - labelX <= 6) labelX = previousRight + 2;
            visible &= labelX >= previousRight + 2;
            if (visible) { peqVisibleFrequencyLabels.Add(label); ((TranslateTransform)label.RenderTransform).X = labelX; Canvas.SetTop(label, peqPlotBottom + 3); previousRight = labelX + labelWidth; }
        }
        peqResponseLine.SetStroke(peqBandsEnabled.IsChecked == true ? readAccent : muted);
        var points = new PointCollection(peqSamples.Select(s => new Point(PeqX(s.Frequency), PeqY(s.Db)))); points.Freeze(); peqResponseLine.Draw(points);
        UpdatePeqHover();
    }
    private void UpdatePeqHover()
    {
        if (peqHoverPosition is not { } position || peqResponseLine == null || peqSamples.Length < 2)
        {
            peqHoverDot.Visibility = peqHoverTag.Visibility = Visibility.Collapsed; SetPeqFrequencyLabels(); return;
        }
        double px = Math.Clamp(position.X, peqPlotLeft, peqPlotRight);
        int lo = 0, hi = peqSamples.Length - 1;
        var points = peqResponseLine.Points;
        while (hi - lo > 1) { int mid = (lo + hi) / 2; if (points[mid].X <= px) lo = mid; else hi = mid; }
        double fraction = (px - points[lo].X) / (points[hi].X - points[lo].X);
        double py = points[lo].Y + (points[hi].Y - points[lo].Y) * fraction;
        double lowFrequency = 20, highFrequency = peqPlotUpper;
        for (int i = 0; i < 32; i++) { double mid = (lowFrequency + highFrequency) / 2; if (PeqX(mid) < px) lowFrequency = mid; else highFrequency = mid; }
        peqHoverText.Text = Math.Round((lowFrequency + highFrequency) / 2).ToString(CultureInfo.InvariantCulture);
        peqHoverDot.Visibility = peqHoverTag.Visibility = Visibility.Visible;
        peqHoverTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        peqDotPosition.X = px - 3; peqDotPosition.Y = py - 3;
        double tagLeft = Math.Clamp(px - peqHoverTag.DesiredSize.Width / 2, 0, peqCurve.ActualWidth - peqHoverTag.DesiredSize.Width);
        peqTagPosition.X = tagLeft; peqTagPosition.Y = peqPlotBottom + 1;
        SetPeqFrequencyLabels(tagLeft, peqHoverTag.DesiredSize.Width);
    }
    private void SetPeqFrequencyLabels(double? tagLeft = null, double tagWidth = 0)
    {
        foreach (var mark in peqFrequencyMarks)
        {
            bool covered = tagLeft is { } left && mark.Label.RenderTransform.Value.OffsetX < left + tagWidth + 2 && mark.Label.RenderTransform.Value.OffsetX + mark.Width > left - 2;
            mark.Label.Opacity = peqVisibleFrequencyLabels.Contains(mark.Label) && !covered ? 1 : 0;
        }
    }
}
