using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Questyle.Desktop;

// Keep the existing style targets and bindings; interpolate only their visual changes.
public static class StyleTransitions
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(StyleTransitions), new PropertyMetadata(false, Enable));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    public static readonly DependencyProperty DropDownOpenProperty = DependencyProperty.RegisterAttached(
        "DropDownOpen", typeof(bool), typeof(StyleTransitions), new PropertyMetadata(false, ChangeDropDownOpen));
    public static bool GetDropDownOpen(DependencyObject target) => (bool)target.GetValue(DropDownOpenProperty);
    public static void SetDropDownOpen(DependencyObject target, bool value) => target.SetValue(DropDownOpenProperty, value);

    private static void ChangeDropDownOpen(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var popup = (Popup)target;
        var child = (FrameworkElement)popup.Child;
        bool open = (bool)e.NewValue;
        double from = open && !popup.IsOpen ? 0 : child.Opacity;
        child.BeginAnimation(UIElement.OpacityProperty, null);
        popup.IsHitTestVisible = open;
        if (open) popup.IsOpen = true;
        var fade = new DoubleAnimation(from, open ? 1 : 0, TimeSpan.FromMilliseconds(100)) { FillBehavior = FillBehavior.Stop };
        if (!open) fade.Completed += (_, _) => { if (!GetDropDownOpen(popup)) popup.IsOpen = false; };
        var clock = fade.CreateClock();
        child.ApplyAnimationClock(UIElement.OpacityProperty, clock, HandoffBehavior.SnapshotAndReplace);
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
    }

    // Clocks started in one dispatcher operation share the same first frame,
    // which keeps separate windows (main surface and shadow) moving together.
    public static Task Start(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
    {
        var completed = new TaskCompletionSource();
        animation.Completed += (_, _) => completed.TrySetResult();
        var clock = animation.CreateClock();
        target.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
        clock.Controller!.Begin();
        clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
        return completed.Task;
    }

    private static void Enable(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue) return;
        var element = (FrameworkElement)target;
        var properties = new List<DependencyProperty> { UIElement.OpacityProperty };
        if (element is Control) properties.Add(Control.ForegroundProperty);
        if (element is Border) properties.AddRange([Border.BackgroundProperty, Border.BorderBrushProperty]);
        var changes = properties.Select(property => new Transition(element, property)).ToArray();
        element.Loaded += (_, _) => { foreach (var change in changes) change.Attach(); };
        element.Unloaded += (_, _) => { foreach (var change in changes) change.Detach(); };
        foreach (var change in changes) change.Attach();
    }

    private sealed class Transition(FrameworkElement element, DependencyProperty property)
    {
        private readonly DependencyPropertyDescriptor descriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType());
        private object previous = element.GetValue(property), destination = element.GetAnimationBaseValue(property);
        private bool attached, selected;
        private bool IsSelected() => (element as Button ?? element.TemplatedParent as Button)?.Tag is "Selected";

        public void Attach()
        {
            if (attached) return;
            previous = element.GetValue(property); destination = element.GetAnimationBaseValue(property);
            selected = IsSelected();
            descriptor.AddValueChanged(element, Changed); attached = true;
        }
        public void Detach()
        {
            descriptor.RemoveValueChanged(element, Changed); attached = false;
            element.BeginAnimation(property, null);
        }
        private void Changed(object? sender, EventArgs e)
        {
            var next = element.GetAnimationBaseValue(property);
            if (Equals(next, destination)) { previous = element.GetValue(property); return; }
            bool selectedNow = IsSelected(), deselecting = selected && !selectedNow;
            selected = selectedNow;
            var from = previous; destination = next;
            var duration = TimeSpan.FromMilliseconds(deselecting || element is ComboBoxItem || element.TemplatedParent is ComboBoxItem ? 100 : 140);
            AnimationTimeline? animation = next switch
            {
                SolidColorBrush brush when from is SolidColorBrush old => new BrushTransition { From = old, To = brush },
                double value when from is double old => new DoubleAnimation(old, value, duration),
                _ => null
            };
            if (animation != null)
            {
                animation.Duration = duration;
                animation.FillBehavior = FillBehavior.Stop;
                var clock = animation.CreateClock();
                element.ApplyAnimationClock(property, clock, HandoffBehavior.SnapshotAndReplace);
                clock.Controller!.Begin();
                clock.Controller.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
            }
            previous = element.GetValue(property);
        }
    }

    private sealed class BrushTransition : AnimationTimeline
    {
        public Brush From { get; init; } = Brushes.Transparent;
        public Brush To { get; init; } = Brushes.Transparent;
        public override Type TargetPropertyType => typeof(Brush);
        protected override Freezable CreateInstanceCore() => new BrushTransition { From = From, To = To };
        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock clock)
        {
            var from = ((SolidColorBrush)From).Color; var to = ((SolidColorBrush)To).Color;
            if (from.A == 0) from = Color.FromArgb(0, to.R, to.G, to.B);
            if (to.A == 0) to = Color.FromArgb(0, from.R, from.G, from.B);
            double progress = clock.CurrentProgress ?? 0;
            progress = progress * progress * (3 - 2 * progress);
            byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * progress);
            var brush = new SolidColorBrush(Color.FromArgb(Mix(from.A, to.A), Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B)));
            brush.Freeze(); return brush;
        }
    }
}
