using Questyle.Desktop.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace Questyle.Desktop;

// Update transition: the old process collapses into the update card, installs,
// and starts the new version, which draws the identical card at the same screen
// pixels before the old window disappears, then expands into its own interface.
public partial class MainWindow
{
    private const double UpdateCardWidth = 380, UpdateCardHeight = 160, UpdateTrackWidth = 316, WindowRadius = 24;
    private static readonly string[] SizeUnits = ["KB", "MB", "GB"];
    private readonly Canvas updateLayer = new() { Visibility = Visibility.Collapsed };
    private readonly Border updateCard = new(), updateFill = new();
    private readonly TextBlock updateTitle = new();
    private readonly DispatcherTimer updateProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private FixedWidthText updateReceived = null!, updateTotal = null!;
    private FrameworkElement[] updateRows = [], shellParts = [];
    private FrameworkElement shellBody = null!;
    private Border developerCard = null!;
    private long updateReceivedBytes, updateTotalBytes;
    private double updateFillTarget;
    private bool updating;
    private int brandClicks;
    private DateTime lastBrandClick;

    private void BuildUpdateCard()
    {
        var sizeBrush = Color("#A3AAB7");
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        updateTitle.FontSize = 17; updateTitle.FontWeight = FontWeights.SemiBold; updateTitle.Foreground = Brushes.White;
        updateTitle.HorizontalAlignment = HorizontalAlignment.Center; updateTitle.TextTrimming = TextTrimming.CharacterEllipsis; updateTitle.MaxWidth = UpdateTrackWidth;
        updateFill.HorizontalAlignment = HorizontalAlignment.Left; updateFill.CornerRadius = new CornerRadius(3);
        updateFill.Background = new LinearGradientBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString("#2077CE"), (System.Windows.Media.Color)ColorConverter.ConvertFromString("#5AACF6"), 0);
        updateFill.Effect = new DropShadowEffect { Color = (System.Windows.Media.Color)ColorConverter.ConvertFromString("#3A92EA"), BlurRadius = 10, ShadowDepth = 0, Opacity = .5 };
        var track = new Border { Width = UpdateTrackWidth, Height = 6, CornerRadius = new CornerRadius(3), Background = Color("#2C313C"), Margin = new Thickness(0, 20, 0, 14), Child = updateFill };
        updateReceived = new FixedWidthText(typeface, 13, sizeBrush, true, SizeUnits);
        updateTotal = new FixedWidthText(typeface, 13, sizeBrush, false, SizeUnits);
        var slash = new TextBlock { Text = "/", FontSize = 13, Foreground = sizeBrush, Margin = new Thickness(8, 0, 8, 0) };
        var sizes = new Grid { Width = UpdateTrackWidth };
        sizes.ColumnDefinitions.Add(new()); sizes.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); sizes.ColumnDefinitions.Add(new());
        Grid.SetColumn(slash, 1); Grid.SetColumn(updateTotal, 2); sizes.Children.Add(updateReceived); sizes.Children.Add(slash); sizes.Children.Add(updateTotal);
        updateRows = [updateTitle, track, sizes];
        foreach (var row in updateRows) row.RenderTransform = new TranslateTransform();
        var rows = Column(updateRows); rows.HorizontalAlignment = HorizontalAlignment.Center; rows.VerticalAlignment = VerticalAlignment.Center;
        updateCard.Width = UpdateCardWidth; updateCard.Height = UpdateCardHeight; updateCard.Background = Brushes.Transparent; updateCard.Child = rows;
        updateCard.Focusable = true; updateCard.FocusVisualStyle = null;
        updateCard.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1) DragMove(); };
        updateLayer.Children.Add(updateCard); WindowSurface.Children.Add(updateLayer);
        updateProgressTimer.Tick += (_, _) => ShowUpdateProgress(Interlocked.Read(ref updateReceivedBytes));
    }

    // Called after the shell exists: the parts fade one after another in reading order.
    private void RegisterShellParts(FrameworkElement body, params FrameworkElement[] parts)
    {
        shellBody = body; shellParts = parts;
        foreach (var part in parts) { part.RenderTransformOrigin = new Point(.5, .5); part.RenderTransform = new ScaleTransform(); }
    }

    private Border BuildDeveloperCard()
    {
        var simulate = Button("模拟更新流程", SimulateUpdate); simulate.HorizontalAlignment = HorizontalAlignment.Left; simulate.Margin = new Thickness(0);
        developerCard = Card("开发者选项", simulate); developerCard.Visibility = Visibility.Collapsed;
        return developerCard;
    }

    private void CountBrandClick()
    {
        var now = DateTime.UtcNow;
        brandClicks = pages["SETTINGS"].Visibility == Visibility.Visible && now - lastBrandClick < TimeSpan.FromMilliseconds(600) ? brandClicks + 1 : 1;
        lastBrandClick = now;
        if (brandClicks < 8 || pages["SETTINGS"].Visibility != Visibility.Visible) return;
        developerCard.Visibility = Visibility.Visible; developerCard.UpdateLayout(); developerCard.BringIntoView();
    }

    private void SimulateUpdate()
    {
        var installed = InstalledVersion.Current; var version = installed.Version;
        updateRequest?.Cancel();
        SetAvailableUpdate(updateSettings.Channel == UpdateChannel.Stable
            ? new UpdateRelease(new Version(version.Major, version.Minor, version.Build + 1), 0, DateTimeOffset.Now)
            : new UpdateRelease(new Version(version.Major, version.Minor, version.Build), installed.BuildNumber + 1, DateTimeOffset.Now));
    }

    private static (string Number, string Unit) FormatSize(long bytes)
    {
        double value = bytes / 1024d; int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1) { value /= 1024; unit++; }
        return (value.ToString("0.00", CultureInfo.InvariantCulture), SizeUnits[unit]);
    }

    private void ShowUpdateProgress(long received)
    {
        var (number, unit) = FormatSize(received); updateReceived.Set(number, unit);
        double target = UpdateTrackWidth * Math.Min(1, (double)received / updateTotalBytes);
        if (target == updateFillTarget) return;
        updateFillTarget = target;
        updateFill.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(120)));
    }

    private async Task InstallUpdateAsync()
    {
        if (availableUpdate is not { } release || updating) return;
        if (busy) { SetStatus("正在执行上一项操作，请稍候。"); return; }
        busy = updating = true; updateTimer.Stop(); updateRequest?.Cancel(); CloseUpdatePopup();
        var cancellation = new CancellationTokenSource(); operationCancel = cancellation;
        UpdateInstallation? installation = null; Process? successor = null; bool collapsed = false;
        AddLog((release.Simulated ? "开始模拟更新：" : "开始更新：") + release.DisplayName);
        try
        {
            UpdateInstaller.ResetWorkspace();
            updateTotalBytes = release.Simulated ? UpdateInstaller.MeasureInstallation(AppPaths.ApplicationDirectory) : release.PackageSize;
            Interlocked.Exchange(ref updateReceivedBytes, 0);
            updateTitle.Text = UiLanguage.Current.Format("正在下载: {0}", release.Label);
            var (total, totalUnit) = FormatSize(updateTotalBytes); updateTotal.Set(total, totalUnit);
            updateFill.BeginAnimation(WidthProperty, null); updateFill.Width = updateFillTarget = 0; ShowUpdateProgress(0);
            collapsed = true; await CollapseToUpdateCardAsync();
            updateProgressTimer.Start();
            if (release.Simulated) await SimulateDownloadAsync(cancellation.Token);
            else await (updateChecker ??= new UpdateChecker()).DownloadAsync(release, UpdateInstaller.PackagePath,
                received => Interlocked.Exchange(ref updateReceivedBytes, received), cancellation.Token);
            updateProgressTimer.Stop(); ShowUpdateProgress(updateTotalBytes);
            // From here installation either completes the handoff or rolls back; exit waits for it.
            operationCancel = null;
            await Task.Delay(160);
            await SwitchUpdateTitleAsync(UiLanguage.Current.Format("正在安装: {0}", release.Label));
            var packageRoot = await Task.Run(() => UpdateInstaller.Extract(UpdateInstaller.PackagePath));
            // Created before the files are replaced so the old process loads nothing from the new release.
            var start = new ProcessStartInfo(IOPath.Combine(AppPaths.ApplicationDirectory, "Questyle.exe")) { UseShellExecute = false, WorkingDirectory = AppPaths.ApplicationDirectory };
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\Questyle.Windows.Update." + Environment.ProcessId);
            installation = await Task.Run(() => UpdateInstaller.Install(packageRoot, AppPaths.ApplicationDirectory));
            AddLog("更新文件已替换");
            var handle = new WindowInteropHelper(this).Handle;
            if (!GetWindowRect(handle, out var bounds)) throw new Win32Exception();
            var (x, y) = UpdateCardOffset(bounds, VisualTreeHelper.GetDpi(this).DpiScaleX);
            new UpdateHandoff(Environment.ProcessId, @"Local\Questyle.Windows.Update." + Environment.ProcessId, bounds.Left + x, bounds.Top + y,
                updateTitle.Text, updateReceived.Number, updateReceived.Unit, updateTotal.Number, updateTotal.Unit).Save(UpdateInstaller.HandoffPath);
            start.ArgumentList.Add("--update-handoff"); start.ArgumentList.Add(UpdateInstaller.HandoffPath);
            successor = Process.Start(start)!;
            AllowSetForegroundWindow((uint)successor.Id);
            var readyWait = Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(30)));
            if (await Task.WhenAny(readyWait, successor.WaitForExitAsync()) != readyWait || !await readyWait)
                throw new InvalidOperationException(successor.HasExited ? "新版本启动失败" : "新版本启动超时");
            // The new window now covers this one with identical pixels.
            Hide(); busy = false; RequestExit();
        }
        catch (Exception ex)
        {
            updateProgressTimer.Stop(); operationCancel = null;
            var error = ex;
            try
            {
                if (successor is { HasExited: false }) { successor.Kill(); await successor.WaitForExitAsync(); }
                installation?.Rollback();
            }
            catch (Exception rollback) { AddLog("更新失败：" + ex); error = rollback; }
            if (collapsed) await ExpandFromUpdateCardAsync();
            busy = updating = false;
            if (exitRequested) { Close(); return; }
            if (error is not OperationCanceledException) ShowError(error);
            if (updateSettings.Automatic && updateChecksStarted) updateTimer.Start();
        }
        finally { successor?.Dispose(); cancellation.Dispose(); }
    }

    // Developer simulation: download timing is simulated while the installed files
    // are packaged; extraction, replacement and the process handoff stay real.
    private async Task SimulateDownloadAsync(CancellationToken cancellationToken)
    {
        var package = Task.Run(() => UpdateInstaller.CreatePackage(AppPaths.ApplicationDirectory, UpdateInstaller.PackagePath, cancellationToken), cancellationToken);
        var random = new Random(); var clock = Stopwatch.StartNew();
        double received = 0; long previous = 0;
        while (received < updateTotalBytes)
        {
            await Task.Delay(50, cancellationToken);
            long now = clock.ElapsedMilliseconds;
            received += updateTotalBytes / 5000d * (now - previous) * (.5 + random.NextDouble());
            previous = now;
            Interlocked.Exchange(ref updateReceivedBytes, (long)Math.Min(received, updateTotalBytes));
        }
        await package;
    }

    // The new process: draw the card where the old one is, signal it, then expand.
    public async Task ContinueUpdateAsync(UpdateHandoff handoff, Action startPrimary)
    {
        using var previous = Process.GetProcessById(handoff.ProcessId);
        busy = updating = true;
        updateTitle.Text = handoff.Title; updateReceived.Set(handoff.Downloaded, handoff.DownloadedUnit); updateTotal.Set(handoff.Total, handoff.TotalUnit);
        updateFill.Width = updateFillTarget = UpdateTrackWidth;
        foreach (var part in shellParts) HideShellPart(part);
        _ = StyleTransitions.Start(shellBody, OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
        updateLayer.Visibility = Visibility.Visible; FrameContent.IsHitTestVisible = false;
        WindowSurface.Opacity = 0;
        var rendered = new TaskCompletionSource();
        ContentRendered += (_, _) => rendered.TrySetResult();
        Show();
        await rendered.Task;
        var card = await PlaceAtHandoffAsync(handoff);
        updateCard.Focus();
        WindowSurface.Clip = new RectangleGeometry(card, WindowRadius, WindowRadius);
        _ = shadow!.AnimateVisibleBounds(card, card, TimeSpan.Zero, null);
        shadow.Suspended = false;
        WindowSurface.Opacity = 1;
        await RenderedFrames(4);
        using (var ready = EventWaitHandle.OpenExisting(handoff.ReadyEvent)) ready.Set();
        await previous.WaitForExitAsync();
        startPrimary();
        AddLog("更新完成：Version " + ApplicationVersion);
        _ = Task.Run(UpdateInstaller.DeleteWorkspace).ContinueWith(task => AddLog("清理更新临时文件失败：" + task.Exception!.InnerException),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        await ExpandFromUpdateCardAsync();
        busy = updating = false;
        await ReadWhenForeground();
    }

    private async Task<Rect> PlaceAtHandoffAsync(UpdateHandoff handoff)
    {
        var handle = new WindowInteropHelper(this).Handle;
        // Moving to another monitor can change DPI and window size; place again until stable.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!GetWindowRect(handle, out var bounds)) throw new Win32Exception();
            var (x, y) = UpdateCardOffset(bounds, VisualTreeHelper.GetDpi(this).DpiScaleX);
            if (bounds.Left == handoff.CardLeft - x && bounds.Top == handoff.CardTop - y) return PlaceUpdateCard();
            if (!SetWindowPos(handle, IntPtr.Zero, handoff.CardLeft - x, handoff.CardTop - y, 0, 0, 0x0015)) throw new Win32Exception();
        }
        throw new InvalidOperationException("无法定位更新窗口");
    }

    // Whole physical pixels from the window corner, identical in both processes.
    private static (int X, int Y) UpdateCardOffset(NativeRect bounds, double scale) =>
        ((bounds.Right - bounds.Left - (int)Math.Round(UpdateCardWidth * scale)) / 2,
         (bounds.Bottom - bounds.Top - (int)Math.Round(UpdateCardHeight * scale)) / 2);

    private Rect PlaceUpdateCard()
    {
        if (!GetWindowRect(new WindowInteropHelper(this).Handle, out var bounds)) throw new Win32Exception();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var (x, y) = UpdateCardOffset(bounds, scale);
        Canvas.SetLeft(updateCard, x / scale); Canvas.SetTop(updateCard, y / scale);
        return new Rect(x / scale, y / scale, UpdateCardWidth, UpdateCardHeight);
    }

    private async Task CollapseToUpdateCardAsync()
    {
        var card = PlaceUpdateCard(); var full = new Rect(WindowSurface.RenderSize);
        foreach (var row in updateRows)
        {
            _ = StyleTransitions.Start(row, OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
            _ = StyleTransitions.Start(row.RenderTransform, TranslateTransform.YProperty, new DoubleAnimation(8, TimeSpan.Zero));
        }
        updateLayer.Visibility = Visibility.Visible; FrameContent.IsHitTestVisible = false; updateCard.Focus();
        // The caches are drawn on an unchanged frame, so the first animated frame does no extra work.
        CacheShellParts(true); await RenderedFrames(1);
        for (int i = 0; i < shellParts.Length; i++) _ = AnimateShellPart(shellParts[i], false, i * 28);
        _ = StyleTransitions.Start(shellBody, OpacityProperty, Delayed(1, 0, 160, 360, new CubicEase { EasingMode = EasingMode.EaseInOut }));
        await Task.Delay(140);
        var clip = new RectangleGeometry(full, WindowRadius, WindowRadius); WindowSurface.Clip = clip;
        await ResizeVisibleArea(clip, full, card);
        Task shown = Task.CompletedTask;
        for (int i = 0; i < updateRows.Length; i++)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _ = StyleTransitions.Start(updateRows[i].RenderTransform, TranslateTransform.YProperty, Delayed(8, 0, 40 + i * 90, 300, ease));
            shown = StyleTransitions.Start(updateRows[i], OpacityProperty, Delayed(0, 1, 40 + i * 90, 300, ease));
        }
        await shown;
        // Settled rows are drawn without clocks, exactly as the new process draws them.
        foreach (var row in updateRows) { row.BeginAnimation(OpacityProperty, null); row.RenderTransform.BeginAnimation(TranslateTransform.YProperty, null); }
    }

    private async Task ExpandFromUpdateCardAsync()
    {
        var card = new Rect(Canvas.GetLeft(updateCard), Canvas.GetTop(updateCard), UpdateCardWidth, UpdateCardHeight);
        var full = new Rect(WindowSurface.RenderSize);
        CacheShellParts(true);
        for (int i = 0; i < updateRows.Length; i++)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            _ = StyleTransitions.Start(updateRows[i].RenderTransform, TranslateTransform.YProperty, Delayed(0, -6, i * 70, 220, ease));
            _ = StyleTransitions.Start(updateRows[i], OpacityProperty, Delayed(1, 0, i * 70, 220, ease));
        }
        await Task.Delay(300);
        var clip = new RectangleGeometry(card, WindowRadius, WindowRadius); WindowSurface.Clip = clip;
        var expanded = ResizeVisibleArea(clip, card, full);
        var parts = new List<Task> { expanded, StyleTransitions.Start(shellBody, OpacityProperty, Delayed(0, 1, 220, 400, new CubicEase { EasingMode = EasingMode.EaseInOut })) };
        for (int i = 0; i < shellParts.Length; i++) parts.Add(AnimateShellPart(shellParts[i], true, 340 + i * 36));
        await Task.WhenAll(parts);
        // The animated end values equal the base values, so removing the clocks changes nothing visible.
        WindowSurface.Clip = null; shadow?.ClearVisibleBounds(); updateLayer.Visibility = Visibility.Collapsed;
        shellBody.BeginAnimation(OpacityProperty, null); CacheShellParts(false);
        foreach (var part in shellParts)
        {
            part.BeginAnimation(OpacityProperty, null);
            part.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null); part.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        FrameContent.IsHitTestVisible = true;
    }

    // The window's visible rounded rectangle and its shadow share one easing and start frame.
    private Task ResizeVisibleArea(RectangleGeometry clip, Rect from, Rect to)
    {
        var duration = TimeSpan.FromMilliseconds(700); var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var resized = StyleTransitions.Start(clip, RectangleGeometry.RectProperty, new RectAnimation(from, to, duration) { EasingFunction = ease });
        return shadow == null ? resized : Task.WhenAll(resized, shadow.AnimateVisibleBounds(from, to, duration, ease));
    }

    // Cached parts fade and scale as device-resolution bitmaps instead of redrawing their content every frame.
    private void CacheShellParts(bool cached)
    {
        var cache = cached ? new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX) { SnapsToDevicePixels = true } : null;
        foreach (var part in shellParts) part.CacheMode = cache;
    }

    private Task AnimateShellPart(FrameworkElement part, bool show, double delay)
    {
        double duration = show ? 340 : 280;
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };
        var scale = part.RenderTransform;
        StyleTransitions.Start(scale, ScaleTransform.ScaleXProperty, Delayed(show ? .97 : 1, show ? 1 : .97, delay, duration, ease));
        StyleTransitions.Start(scale, ScaleTransform.ScaleYProperty, Delayed(show ? .97 : 1, show ? 1 : .97, delay, duration, ease));
        return StyleTransitions.Start(part, OpacityProperty, Delayed(show ? 0 : 1, show ? 1 : 0, delay, duration, ease));
    }

    private static void HideShellPart(FrameworkElement part)
    {
        StyleTransitions.Start(part, OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
        StyleTransitions.Start(part.RenderTransform, ScaleTransform.ScaleXProperty, new DoubleAnimation(.97, TimeSpan.Zero));
        StyleTransitions.Start(part.RenderTransform, ScaleTransform.ScaleYProperty, new DoubleAnimation(.97, TimeSpan.Zero));
    }

    // Holds the start value through the delay; BeginTime would show the base value instead.
    private static DoubleAnimationUsingKeyFrames Delayed(double from, double to, double delay, double duration, IEasingFunction ease) => new()
    {
        KeyFrames =
        {
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))),
            new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + duration)), ease)
        }
    };

    private async Task SwitchUpdateTitleAsync(string text)
    {
        await StyleTransitions.Start(updateTitle, OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        updateTitle.Text = text;
        await StyleTransitions.Start(updateTitle, OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        updateTitle.BeginAnimation(OpacityProperty, null);
    }

    private static Task RenderedFrames(int count)
    {
        var rendered = new TaskCompletionSource();
        void Next(object? sender, EventArgs e) { if (--count == 0) { CompositionTarget.Rendering -= Next; rendered.SetResult(); } }
        CompositionTarget.Rendering += Next;
        return rendered.Task;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
}
