using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace Questyle.Desktop;

public static class SmoothScrolling
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScrolling), new PropertyMetadata(false, Enable));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    public static readonly DependencyProperty HandlesWheelProperty = DependencyProperty.RegisterAttached(
        "HandlesWheel", typeof(bool), typeof(SmoothScrolling), new PropertyMetadata(false));
    public static bool GetHandlesWheel(DependencyObject target) => (bool)target.GetValue(HandlesWheelProperty);
    public static void SetHandlesWheel(DependencyObject target, bool value) => target.SetValue(HandlesWheelProperty, value);

    private static readonly ConditionalWeakTable<ScrollViewer, Motion> motions = new();
    private static void Enable(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ScrollViewer)target;
        if ((bool)e.NewValue) motions.Add(viewer, new Motion(viewer));
        else { motions.TryGetValue(viewer, out var motion); motion!.Detach(); motions.Remove(viewer); }
    }

    private sealed class Motion : Animatable
    {
        private static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
            "Offset", typeof(double), typeof(Motion), new PropertyMetadata(0d, OffsetChanged));
        private readonly ScrollViewer viewer;
        private double destination;
        private int direction;

        public Motion(ScrollViewer viewer)
        {
            this.viewer = viewer;
            viewer.PreviewMouseWheel += Wheel;
            viewer.PreviewMouseDown += Interrupt;
            viewer.PreviewKeyDown += Interrupt;
            viewer.Unloaded += Interrupt;
            viewer.ScrollChanged += ScrollChanged;
        }
        protected override Freezable CreateInstanceCore() => new Motion(viewer);
        private static void OffsetChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
            => ((Motion)target).viewer.ScrollToVerticalOffset((double)e.NewValue);

        private void Wheel(object sender, MouseWheelEventArgs e)
        {
            // Let the nearest scrollable viewport, or a numeric wheel editor, own the gesture.
            for (var node = e.OriginalSource as DependencyObject; node != null && node != viewer;
                 node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (GetHandlesWheel(node) || node is ScrollViewer { ScrollableHeight: > 0 }) return;
            }
            if (viewer.ScrollableHeight <= 0 || e.Delta == 0 || SystemParameters.WheelScrollLines == 0) return;
            int nextDirection = Math.Sign(e.Delta);
            double from = viewer.VerticalOffset;
            double origin = HasAnimatedProperties && direction == nextDirection ? destination : from;
            double step = SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight : SystemParameters.WheelScrollLines * 16;
            destination = Math.Clamp(origin - e.Delta / 120d * step, 0, viewer.ScrollableHeight);
            direction = nextDirection;
            SetValue(OffsetProperty, destination);
            var animation = new DoubleAnimation(from, destination, TimeSpan.FromMilliseconds(100))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += (_, _) => { SetValue(OffsetProperty, destination); BeginAnimation(OffsetProperty, null); direction = 0; };
            BeginAnimation(OffsetProperty, animation);
            e.Handled = true;
        }
        private void Interrupt(object sender, RoutedEventArgs e) => Stop();
        private void ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.OriginalSource != viewer || !HasAnimatedProperties) return;
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) Stop();
        }
        private void Stop()
        {
            double current = viewer.VerticalOffset;
            SetValue(OffsetProperty, current);
            BeginAnimation(OffsetProperty, null);
            destination = current; direction = 0;
        }
        public void Detach()
        {
            Stop();
            viewer.PreviewMouseWheel -= Wheel;
            viewer.PreviewMouseDown -= Interrupt;
            viewer.PreviewKeyDown -= Interrupt;
            viewer.Unloaded -= Interrupt;
            viewer.ScrollChanged -= ScrollChanged;
        }
    }
}
