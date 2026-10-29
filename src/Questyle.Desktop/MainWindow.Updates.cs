using Questyle.Desktop.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Questyle.Desktop;

public partial class MainWindow
{
    private UpdateSettings updateSettings = new();
    private readonly CheckBox automaticUpdates = new();
    private readonly Border updateChannelSelector = new();
    private readonly TranslateTransform updateChannelIndicator = new();
    private readonly DispatcherTimer updateHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Border versionHoverArea = new();
    private readonly Border newVersionBadge = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock updatePopupTitle = new(), updateCheckStatus = new();
    private readonly Popup updatePopup = new() { AllowsTransparency = true, Placement = PlacementMode.Custom, StaysOpen = true };
    private Button checkUpdatesButton = null!, updateButton = null!;
    private UpdateChecker? updateChecker;
    private CancellationTokenSource? updateRequest;
    private UpdateRelease? availableUpdate;
    private bool updateChecksStarted;
    private const double UpdateChannelSegmentWidth = 109;
    private string? updateCheckStatusKey;
    private string updateCheckVersion = "";
    private Func<UpdateChannel, CancellationToken, Task<UpdateRelease?>>? checkUpdateRelease;

    private FrameworkElement BuildVersionFooter()
    {
        // Natural line height: LineHeight leaves its extra space below the text, lifting it above the badge.
        var edition = Note("v" + ApplicationVersion); edition.TextAlignment = TextAlignment.Center;
        edition.LineHeight = double.NaN; edition.VerticalAlignment = VerticalAlignment.Center;
        newVersionBadge.Background = accent; newVersionBadge.CornerRadius = new CornerRadius(4);
        newVersionBadge.Margin = new Thickness(6, 0, 0, 0); newVersionBadge.Padding = new Thickness(5, 1, 5, 1);
        newVersionBadge.VerticalAlignment = VerticalAlignment.Center;
        newVersionBadge.Child = new TextBlock { Text = "New", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        var versionRow = Row(edition, newVersionBadge); versionRow.HorizontalAlignment = HorizontalAlignment.Center; versionRow.Height = 24;
        versionHoverArea.Child = versionRow; versionHoverArea.Background = Brushes.Transparent;
        versionHoverArea.HorizontalAlignment = HorizontalAlignment.Center;
        versionHoverArea.Padding = new Thickness(14); versionHoverArea.Margin = new Thickness(4, 6, 4, 0);
        versionHoverArea.Focusable = true;
        versionHoverArea.MouseEnter += (_, _) => OpenUpdatePopup();
        versionHoverArea.MouseLeave += (_, _) => updateHoverTimer.Start();
        versionHoverArea.GotKeyboardFocus += (_, _) => OpenUpdatePopup();
        versionHoverArea.LostKeyboardFocus += (_, _) => updateHoverTimer.Start();
        updatePopupTitle.FontSize = 13; updatePopupTitle.Foreground = Foreground;
        updatePopupTitle.Margin = new Thickness(0, 0, 0, 12);
        updateButton = Button("更新", () => _ = InstallUpdateAsync());
        updateButton.Margin = new Thickness(0); updateButton.Height = 32; updateButton.Padding = new Thickness(12, 5, 12, 5);
        var surface = new Border
        {
            Child = Column(updatePopupTitle, updateButton), Background = Brushes.White,
            BorderBrush = (Brush)FindResource("ControlBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(14), Margin = new Thickness(8),
            MinWidth = 158, Effect = (System.Windows.Media.Effects.Effect)FindResource("ControlShadow")
        };
        surface.MouseEnter += (_, _) => OpenUpdatePopup();
        surface.MouseLeave += (_, _) => updateHoverTimer.Start();
        updatePopup.PlacementTarget = versionHoverArea; updatePopup.Child = surface;
        updatePopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            [new(new Point((targetSize.Width - popupSize.Width) / 2 + offset.X, -popupSize.Height + offset.Y), PopupPrimaryAxis.Horizontal)];
        updateHoverTimer.Tick += (_, _) =>
        {
            updateHoverTimer.Stop();
            if (!versionHoverArea.IsMouseOver && !surface.IsMouseOver && !versionHoverArea.IsKeyboardFocusWithin && !surface.IsKeyboardFocusWithin) CloseUpdatePopup();
        };
        var footer = new Grid(); footer.Children.Add(versionHoverArea); footer.Children.Add(updatePopup);
        IsVisibleChanged += (_, _) => { if (!IsVisible) { CloseUpdatePopup(); updatePopup.IsOpen = false; updateHoverTimer.Stop(); } };
        return footer;
    }

    private UIElement[] BuildUpdateSettings()
    {
        updateSettings = UpdateSettings.Load();
        automaticUpdates.Style = (Style)FindResource("StartupToggle"); automaticUpdates.IsChecked = updateSettings.Automatic;
        Localize(automaticUpdates, ToolTipProperty, "自动更新");
        automaticUpdates.Click += (_, _) =>
        {
            try
            {
                var updated = updateSettings with { Automatic = automaticUpdates.IsChecked == true };
                updated.Save(); updateSettings = updated;
            }
            catch (Exception ex) { automaticUpdates.IsChecked = updateSettings.Automatic; ShowError(ex); }
        };
        var automaticRow = SettingsUpdateRow("自动更新", automaticUpdates);
        var channelGrid = new Grid { Width = UpdateChannelSegmentWidth * 2, Height = 36 };
        channelGrid.ColumnDefinitions.Add(new()); channelGrid.ColumnDefinitions.Add(new());
        var indicator = new Border
        {
            Background = Color("#F5F6F8"), CornerRadius = new CornerRadius(7), Margin = new Thickness(2),
            RenderTransform = updateChannelIndicator, IsHitTestVisible = false,
            Effect = (System.Windows.Media.Effects.Effect)FindResource("ControlShadow")
        };
        channelGrid.Children.Add(indicator);
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        var hitArea = new FrameworkElementFactory(typeof(Border)); hitArea.SetValue(Border.BackgroundProperty, Brushes.Transparent); hitArea.AppendChild(presenter); template.VisualTree = hitArea;
        var style = new Style(typeof(Button)); style.Setters.Add(new Setter(TemplateProperty, template)); style.Setters.Add(new Setter(FocusVisualStyleProperty, null)); style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        var stable = Button("正式版本", () => SelectUpdateChannel(UpdateChannel.Stable));
        var development = Button("每日构建版本", () => SelectUpdateChannel(UpdateChannel.Development));
        foreach (var button in new[] { stable, development }) { button.Style = style; button.FontSize = 14; button.Margin = new Thickness(0); button.Padding = new Thickness(0); channelGrid.Children.Add(button); }
        Grid.SetColumn(development, 1);
        updateChannelSelector.Width = 220; updateChannelSelector.Child = channelGrid;
        updateChannelSelector.UseLayoutRounding = false; updateChannelSelector.VerticalAlignment = VerticalAlignment.Center;
        updateChannelSelector.Background = Color("#E7EAF0"); updateChannelSelector.CornerRadius = new CornerRadius(9);
        updateChannelSelector.BorderBrush = (Brush)FindResource("ControlBorderBrush"); updateChannelSelector.BorderThickness = new Thickness(1);
        updateChannelSelector.Effect = (System.Windows.Media.Effects.Effect)FindResource("ControlShadow");
        updateChannelIndicator.X = updateSettings.Channel == UpdateChannel.Stable ? 0 : UpdateChannelSegmentWidth;
        var channelRow = SettingsUpdateRow("更新通道", updateChannelSelector);
        return [automaticRow, channelRow];
    }

    // The manual check sits at the end of the About page's button row; its result follows the button.
    private UIElement[] BuildUpdateCheck()
    {
        checkUpdatesButton = Button("检查版本更新", () => _ = CheckUpdatesAsync(true));
        updateCheckStatus.FontSize = 13; updateCheckStatus.Foreground = muted; updateCheckStatus.VerticalAlignment = VerticalAlignment.Center;
        updateCheckStatus.Margin = new Thickness(4, 0, 0, 0);
        return [checkUpdatesButton, updateCheckStatus];
    }

    private DockPanel SettingsUpdateRow(string label, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(control, Dock.Right); row.Children.Add(control);
        var text = Text(label); text.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(text); return row;
    }

    private void SelectUpdateChannel(UpdateChannel channel)
    {
        if (channel == updateSettings.Channel) return;
        var updated = updateSettings with { Channel = channel }; updated.Save(); updateSettings = updated;
        updateRequest?.Cancel(); SetAvailableUpdate(null); SetUpdateCheckStatus(null);
        double from = updateChannelIndicator.X, target = channel == UpdateChannel.Stable ? 0 : UpdateChannelSegmentWidth;
        updateChannelIndicator.X = target;
        updateChannelIndicator.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        // Switching is an explicit request for that channel, so it is checked even with automatic checks off.
        if (updateChecksStarted) _ = CheckUpdatesAsync(false);
    }

    public void StartUpdateChecks()
    {
        updateChecksStarted = true;
        // A handoff has already installed its update; only a normal launch checks automatically.
        if (updateSettings.Automatic && !updating) _ = CheckStartupUpdateAsync(InstallUpdateAsync);
    }

    private async Task CheckStartupUpdateAsync(Func<Task> install)
    {
        busy = true; FrameContent.IsHitTestVisible = false;
        try
        {
            // Let the first window layout finish before an update can collapse it.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var release = await CheckUpdatesAsync(false);
            busy = false;
            if (!exitRequested && updateSettings.Automatic && release != null && ReferenceEquals(availableUpdate, release)) await install();
        }
        finally
        {
            busy = false; FrameContent.IsHitTestVisible = true;
            if (exitRequested) Close();
        }
        if (!exitRequested) await ReadWhenForeground();
    }

    private async Task<UpdateRelease?> CheckUpdatesAsync(bool manual)
    {
        // A developer simulation stays offered until it is used or the channel changes.
        if (updateRequest is { IsCancellationRequested: false } || availableUpdate?.Simulated == true || updating) return null;
        using var cancellation = new CancellationTokenSource(); updateRequest = cancellation;
        var channel = updateSettings.Channel; checkUpdatesButton.IsEnabled = false;
        if (manual) { updateCheckVersion = ""; SetUpdateCheckStatus("正在检查更新…"); }
        try
        {
            checkUpdateRelease ??= (selected, token) => (updateChecker ??= new UpdateChecker()).CheckAsync(selected, token);
            var latest = await checkUpdateRelease(channel, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var release = latest is not null && latest.IsOfferedTo(InstalledVersion.Current, channel) ? latest : null;
            SetAvailableUpdate(release);
            if (manual)
            {
                updateCheckVersion = latest == null ? "" : " v" + latest.Version.ToString(3) + (latest.Development ? "_" + latest.BuildCommit : "");
                SetUpdateCheckStatus(availableUpdate != null ? "发现新版本" : latest == null ? "暂无可用版本" : "已是最新版本");
            }
            return release;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AddLog("版本检查失败：" + ex);
            if (manual) SetUpdateCheckStatus("检查更新失败");
        }
        finally
        {
            if (ReferenceEquals(updateRequest, cancellation)) { updateRequest = null; checkUpdatesButton.IsEnabled = true; }
        }
        return null;
    }

    private void SetAvailableUpdate(UpdateRelease? release)
    {
        availableUpdate = release; newVersionBadge.Visibility = release == null ? Visibility.Collapsed : Visibility.Visible;
        updatePopupTitle.Text = release?.DisplayName ?? "";
        if (release == null) CloseUpdatePopup();
        else if (versionHoverArea.IsMouseOver) OpenUpdatePopup();
    }
    private void OpenUpdatePopup()
    {
        updateHoverTimer.Stop();
        if (availableUpdate != null && !updating) StyleTransitions.SetDropDownOpen(updatePopup, true);
    }
    private void CloseUpdatePopup() => StyleTransitions.SetDropDownOpen(updatePopup, false);
    private void SetUpdateCheckStatus(string? key)
    {
        if (key == null) updateCheckVersion = "";
        updateCheckStatusKey = key; updateCheckStatus.Text = key == null ? "" : L(key) + updateCheckVersion;
    }
    private void StopUpdateChecks()
    {
        updateHoverTimer.Stop(); CloseUpdatePopup(); updatePopup.IsOpen = false;
        updateRequest?.Cancel(); updateChecker?.Dispose();
    }
}
