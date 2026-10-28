using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Questyle.Desktop;

// A separate, non-activating layered window leaves the main HWND's resize edge
// exactly on the visible border. Its transparent centre never covers the UI.
internal sealed class WindowShadow
{
    internal const double Padding = 28;
    private readonly Window owner;
    private readonly ShadowFrame frame = new();
    private readonly Window shadow;
    private readonly HwndSource ownerSource;
    private IntPtr shadowHandle;
    private bool suspended;

    public WindowShadow(Window owner)
    {
        this.owner = owner;
        ownerSource = HwndSource.FromHwnd(new WindowInteropHelper(owner).Handle);
        ownerSource.AddHook(MoveWithOwner);
        shadow = new Window
        {
            Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowActivated = false,
            ShowInTaskbar = false, IsHitTestVisible = false, Content = frame
        };
        shadow.SourceInitialized += (_, _) =>
        {
            var handle = shadowHandle = new WindowInteropHelper(shadow).Handle;
            // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE.
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x080000A0);
            HwndSource.FromHwnd(handle).AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == 0x0084) { handled = true; return new IntPtr(-1); }
                return IntPtr.Zero;
            });
        };
        owner.SizeChanged += Update;
        owner.DpiChanged += (_, _) => Update(owner, EventArgs.Empty);
        owner.StateChanged += Update;
        owner.IsVisibleChanged += (_, _) => Update(owner, EventArgs.Empty);
        owner.Closed += (_, _) => { ownerSource.RemoveHook(MoveWithOwner); shadow.Close(); };
        Update(owner, EventArgs.Empty);
    }

    internal static Grid CreateSurface()
    {
        var surface = new Grid();
        surface.Children.Add(new Border
        {
            Margin = new Thickness(Padding), CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(Color.FromRgb(24, 27, 36)),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 32, ShadowDepth = 0, Opacity = .38 }
        });
        return surface;
    }

    internal static void ClipSurface(Grid surface, double width, double height)
    {
        surface.Clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, width + Padding * 2, height + Padding * 2)),
            new RectangleGeometry(new Rect(Padding, Padding, width, height), 24, 24));
    }

    // Hidden while a newly started update process positions its window.
    public bool Suspended { get => suspended; set { suspended = value; Update(owner, EventArgs.Empty); } }

    // During the update transition only part of the owner is visible; the shadow
    // follows that rounded rectangle with the same timing as the owner's clip.
    public Task AnimateVisibleBounds(Rect from, Rect to, Duration duration, IEasingFunction? easing)
    {
        frame.Prepare(VisualTreeHelper.GetDpi(owner).DpiScaleX);
        return StyleTransitions.Start(frame, ShadowFrame.BoundsProperty, new RectAnimation(from, to, duration) { EasingFunction = easing });
    }

    public void ClearVisibleBounds() => frame.BeginAnimation(ShadowFrame.BoundsProperty, null);

    private void Update(object? sender, EventArgs e)
    {
        if (suspended || !owner.IsVisible || owner.WindowState != WindowState.Normal) { shadow.Hide(); return; }
        shadow.Width = owner.ActualWidth + Padding * 2; shadow.Height = owner.ActualHeight + Padding * 2;
        frame.Prepare(VisualTreeHelper.GetDpi(owner).DpiScaleX);
        frame.Bounds = new Rect(0, 0, owner.ActualWidth, owner.ActualHeight);
        if (!shadow.IsVisible) shadow.Show();
        MoveShadow();
    }

    private IntPtr MoveWithOwner(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0047) MoveShadow(); // WM_WINDOWPOSCHANGED: no WPF layout or new shadow geometry.
        return IntPtr.Zero;
    }

    private void MoveShadow()
    {
        if (!shadow.IsVisible || shadowHandle == IntPtr.Zero) return;
        if (!GetWindowRect(ownerSource.Handle, out var bounds)) throw new System.ComponentModel.Win32Exception();
        int padding = (int)Math.Round(Padding * VisualTreeHelper.GetDpi(owner).DpiScaleX);
        // Keep the existing layered bitmap and size; only move its native HWND.
        if (!SetWindowPos(shadowHandle, IntPtr.Zero, bounds.Left - padding, bounds.Top - padding, 0, 0, 0x0215))
            throw new System.ComponentModel.Win32Exception();
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}

// Draws the shadow of a rounded rectangle of any size from the corners and edges of
// one pre-rendered shadow. Animated bounds only redraw nine bitmaps per frame: no
// layout, blur or clip geometry, which keeps the layered window at full frame rate.
internal sealed class ShadowFrame : FrameworkElement
{
    // Along each edge the blur is uniform beyond the corner radius plus the blur radius.
    private const double Corner = 64;
    public static readonly DependencyProperty BoundsProperty = DependencyProperty.Register(nameof(Bounds), typeof(Rect), typeof(ShadowFrame),
        new FrameworkPropertyMetadata(Rect.Empty, FrameworkPropertyMetadataOptions.AffectsRender));
    private BitmapSource?[] pieces = [];
    private double scale;
    private int corner;

    public ShadowFrame() => RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

    // The visible rectangle in the owner's coordinates.
    public Rect Bounds { get => (Rect)GetValue(BoundsProperty); set => SetValue(BoundsProperty, value); }

    public void Prepare(double dpiScale)
    {
        if (dpiScale == scale) return;
        scale = dpiScale;
        double inner = Corner * 2 + 2, size = inner + WindowShadow.Padding * 2;
        var surface = WindowShadow.CreateSurface(); WindowShadow.ClipSurface(surface, inner, inner);
        surface.Measure(new Size(size, size)); surface.Arrange(new Rect(0, 0, size, size));
        int pixels = (int)Math.Round(size * scale);
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(surface); bitmap.Freeze();
        corner = (int)Math.Round((WindowShadow.Padding + Corner) * scale);
        int[] starts = [0, corner, pixels - corner], lengths = [corner, pixels - corner * 2, corner];
        pieces = new BitmapSource?[9];
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
            {
                if (row == 1 && column == 1) continue;
                var piece = new CroppedBitmap(bitmap, new Int32Rect(starts[column], starts[row], lengths[column], lengths[row])); piece.Freeze();
                pieces[row * 3 + column] = piece;
            }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.IsEmpty || pieces.Length == 0) return;
        // Whole device pixels keep neighbouring pieces seamless.
        double Snap(double value) => Math.Round(value * scale) / scale;
        double side = corner / scale, right = Snap(bounds.Right + WindowShadow.Padding * 2), bottom = Snap(bounds.Bottom + WindowShadow.Padding * 2);
        double[] xs = [Snap(bounds.Left), Snap(bounds.Left) + side, right - side, right];
        double[] ys = [Snap(bounds.Top), Snap(bounds.Top) + side, bottom - side, bottom];
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
                if (pieces[row * 3 + column] is { } piece)
                    context.DrawImage(piece, new Rect(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]));
    }
}
