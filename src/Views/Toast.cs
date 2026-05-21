using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace TermThing.Views;

/// <summary>
/// Lightweight transient notification overlay shown near the top of the host
/// window. Used as quick feedback for actions whose result isn't otherwise
/// visible (e.g. terminal copy succeeded).
///
/// Usage: <c>Toast.Show(anyVisualInTheWindow, "Copied");</c>
///
/// Only one toast is shown at a time per window — calling <see cref="Show"/>
/// while a toast is already up replaces it.
///
/// Implementation note: <see cref="OverlayLayer"/> is a <see cref="Canvas"/>
/// in Avalonia 12, so direct children don't honour HorizontalAlignment /
/// VerticalAlignment — they land at (0, 0). To get top-centre positioning,
/// the toast is wrapped in a transparent <see cref="Panel"/> whose Width and
/// Height track the overlay's bounds, and the toast inside the wrapper uses
/// alignment as normal.
/// </summary>
public static class Toast
{
    private static readonly Dictionary<OverlayLayer, Panel> _active = new();

    public static void Show(Visual host, string message, TimeSpan? duration = null)
    {
        var overlay = OverlayLayer.GetOverlayLayer(host);
        if (overlay == null) return;

        // Replace any in-flight toast on this overlay so we never stack them.
        if (_active.TryGetValue(overlay, out var prev))
        {
            overlay.Children.Remove(prev);
            _active.Remove(overlay);
        }

        var toast = new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(0xE6, 0x22, 0x22, 0x22)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(0x90, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(16, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin = new Thickness(0, 28, 0, 0),
            IsHitTestVisible = false,
            Opacity = 0,
            Child = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
            },
        };

        var fill = new Panel { IsHitTestVisible = false };
        fill.Bind(Layoutable.WidthProperty,  new Binding("Bounds.Width")  { Source = overlay });
        fill.Bind(Layoutable.HeightProperty, new Binding("Bounds.Height") { Source = overlay });
        fill.Children.Add(toast);

        overlay.Children.Add(fill);
        _active[overlay] = fill;

        var lifetime = duration ?? TimeSpan.FromMilliseconds(1400);
        _ = AnimateAsync(overlay, fill, toast, lifetime);
    }

    private static async Task AnimateAsync(OverlayLayer overlay, Panel fill, Border target, TimeSpan lifetime)
    {
        try
        {
            await FadeAsync(target, 0.0, 1.0, TimeSpan.FromMilliseconds(140));
            await Task.Delay(lifetime);
            await FadeAsync(target, 1.0, 0.0, TimeSpan.FromMilliseconds(220));
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_active.TryGetValue(overlay, out var current) && ReferenceEquals(current, fill))
                    _active.Remove(overlay);
                if (fill.Parent is OverlayLayer ol)
                    ol.Children.Remove(fill);
            });
        }
    }

    private static Task FadeAsync(Border target, double from, double to, TimeSpan duration)
    {
        var anim = new Animation
        {
            Duration = duration,
            Easing   = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, to) } },
            },
        };
        return anim.RunAsync(target);
    }
}
