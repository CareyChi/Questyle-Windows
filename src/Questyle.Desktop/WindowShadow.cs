using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Questyle.Desktop;

// A separate, non-activating layered window leaves the main HWND's resize edge
// exactly on the visible border. Its transparent centre never covers the UI.
internal sealed class WindowShadow
{
    private const double Padding = 28;
    private readonly Window owner;
    private readonly Grid surface = CreateSurface();
    private readonly Window shadow;
    private readonly HwndSource ownerSource;
    private IntPtr shadowHandle;

    public WindowShadow(Window owner)
    {
        this.owner = owner;
        ownerSource = HwndSource.FromHwnd(new WindowInteropHelper(owner).Handle);
        ownerSource.AddHook(MoveWithOwner);
        shadow = new Window
        {
            Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowActivated = false,
            ShowInTaskbar = false, IsHitTestVisible = false, Content = surface
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
        var surface = new Grid { CacheMode = new BitmapCache() };
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

    private void Update(object? sender, EventArgs e)
    {
        if (!owner.IsVisible || owner.WindowState != WindowState.Normal) { shadow.Hide(); return; }
        shadow.Width = owner.ActualWidth + Padding * 2; shadow.Height = owner.ActualHeight + Padding * 2;
        ClipSurface(surface, owner.ActualWidth, owner.ActualHeight);
        surface.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(owner).DpiScaleX);
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
