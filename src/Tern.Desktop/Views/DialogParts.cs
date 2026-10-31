using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Tern.Desktop.Themes;

namespace Tern.Desktop.Views;

/// <summary>Shared construction helpers for the code-built dialog windows.</summary>
internal static class DialogParts
{
    public static void Setup(Window window, string title, double width, double height)
    {
        window.Title = title;
        window.Width = width;
        window.Height = height;
        window.CanResize = true;
        window.CanMaximize = false;
        window.CanMinimize = false;
        window.MinWidth = Math.Min(width, 360);
        window.MinHeight = Math.Min(height, 240);
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Icon = DialogStyle.AppIcon();
        window[!Window.BackgroundProperty] = window.GetResourceObservable(ThemeManager.Panel).ToBinding();
        AutomationProperties.SetName(window, title);
        window.Classes.Add("tern-dialog");
        var observed = new HashSet<Control>();
        var pending = false;
        var fitting = false;
        var opened = false;
        var closed = false;
        void QueueFit()
        {
            if (!opened || closed || pending || fitting) return;
            pending = true;
            Dispatcher.UIThread.Post(() =>
            {
                pending = false;
                if (closed) return;
                fitting = true;
                try
                {
                    ObserveContent();
                    // Expanding help/details may use the available screen. Ordinary
                    // forms and editors retain their own, separately chosen viewport.
                    var expanded = observed.OfType<Expander>().Any(expander => expander.IsEffectivelyVisible && expander.IsExpanded);
                    var textScale = Math.Max(1, GuideParts.Scaled(14) / 14);
                    FitContentHeight(window, expanded ? window.MaxHeight : height * textScale);
                    KeepInWorkingArea(window);
                }
                finally { fitting = false; }
            }, DispatcherPriority.Loaded);
        }
        void ContentChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name is "Text" or "IsVisible" or "IsExpanded" or "Content" or "ItemsSource" or "FontSize" or "SelectedItem")
                QueueFit();
        }
        void ObserveContent()
        {
            if (window.Content is not Control content) return;
            foreach (var control in content.GetVisualDescendants().OfType<Control>().Prepend(content))
                if (observed.Add(control)) control.PropertyChanged += ContentChanged;
        }
        void LayoutUpdated(object? sender, EventArgs e)
        {
            var previous = observed.Count;
            ObserveContent();
            if (observed.Count != previous) QueueFit();
        }
        window.Opened += (_, _) =>
        {
            opened = true;
            FitWorkingArea(window);
            ObserveContent();
            // Wait until native decorations and control templates have a real size.
            // Measuring once inside Opened can retain the initial empty viewport.
            QueueFit();
            window.LayoutUpdated += LayoutUpdated;
        };
        window.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) QueueFit();
            if (opened && !closed) KeepInWorkingArea(window);
        };
        window.Closed += (_, _) =>
        {
            closed = true;
            window.LayoutUpdated -= LayoutUpdated;
            foreach (var control in observed) control.PropertyChanged -= ContentChanged;
            observed.Clear();
        };
    }

    /// <summary>Fit short forms to their content while keeping long views in a bounded scrollable viewport.</summary>
    internal static void FitContentHeight(Window window, double preferredHeight)
    {
        if (window.Content is not Control content) return;
        var width = window.ClientSize.Width > 0 ? window.ClientSize.Width : window.Width;
        content.Measure(new Size(width, double.PositiveInfinity));
        var naturalHeight = Math.Ceiling(content.DesiredSize.Height);
        // The probe must not become the live measure for a star-row dialog. If
        // the bounded Height stays unchanged (for example after validation),
        // there is otherwise no resize to restore the finite viewport and the
        // footer can be arranged below the client area.
        content.InvalidateMeasure();
        if (!double.IsFinite(naturalHeight) || naturalHeight <= 0) return;
        // TextBox templates have their own ScrollViewer; they do not make the
        // containing form scrollable or protect its action buttons from clipping.
        var scrollable = content is ScrollViewer || content.GetVisualDescendants().OfType<ScrollViewer>()
            .Any(viewer => !viewer.GetVisualAncestors().OfType<TextBox>().Any());
        var ceiling = scrollable ? Math.Min(preferredHeight, window.MaxHeight) : window.MaxHeight;
        // Large editors and long lists still keep a bounded, scrollable viewport.
        // A small form must not inherit a minimum intended for a larger dialog.
        var height = Math.Min(naturalHeight, ceiling);
        window.MinHeight = scrollable ? Math.Min(window.MinHeight, height) : height;
        window.Height = height;
    }

    private static void FitWorkingArea(Window window)
    {
        if (window.Screens.ScreenFromWindow(window) is not { } screen) return;
        var width = Math.Max(1, screen.WorkingArea.Width / screen.Scaling - 32);
        var height = Math.Max(1, screen.WorkingArea.Height / screen.Scaling - 48);
        window.MinWidth = Math.Min(window.MinWidth, width);
        window.MinHeight = Math.Min(window.MinHeight, height);
        window.MaxWidth = Math.Min(window.MaxWidth, width);
        window.MaxHeight = Math.Min(window.MaxHeight, height);
        window.Width = Math.Clamp(window.Width, window.MinWidth, window.MaxWidth);
        window.Height = Math.Clamp(window.Height, window.MinHeight, window.MaxHeight);
    }

    private static void KeepInWorkingArea(Window window)
    {
        if (window.Screens.ScreenFromWindow(window) is not { } screen) return;
        // Height is client size. Include the native title/borders when deciding
        // whether growth would push the actions below the taskbar or screen.
        var decoration = window.FrameSize is { } frame
            ? new Size(Math.Max(0, frame.Width - window.ClientSize.Width), Math.Max(0, frame.Height - window.ClientSize.Height))
            : new Size(0, 32);
        var area = screen.WorkingArea;
        var width = (int)Math.Ceiling((window.Width + decoration.Width) * screen.Scaling);
        var height = (int)Math.Ceiling((window.Height + decoration.Height) * screen.Scaling);
        var left = Math.Clamp(window.Position.X, area.X + 8, Math.Max(area.X + 8, area.Right - width - 8));
        var top = Math.Clamp(window.Position.Y, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - height - 8));
        if (left != window.Position.X || top != window.Position.Y) window.Position = new PixelPoint(left, top);
    }

    public static Control Labeled(string label, Control control)
    {
        AutomationProperties.SetName(control, label);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), Margin = new Thickness(0, 3) };
        var text = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(text);
        grid.Children.Add(control);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        return grid;
    }

    public static CheckBox Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 3) };
        AutomationProperties.SetName(box, label);
        box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);
        return box;
    }

    public static NumericUpDown Number(double value, double min, double max, double step, Action<double> set)
    {
        var box = new NumericUpDown { Value = (decimal)value, Minimum = min == 0 ? 0 : (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, Width = 120, FormatString = "0.##" };
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v)
            {
                set(Math.Clamp((double)v, min, max));
            }
        };
        return box;
    }
}
