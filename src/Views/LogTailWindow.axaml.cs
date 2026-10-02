using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using System.Collections.Concurrent;
using TermThing.Editor;
using TermThing.Ssh;

namespace TermThing.Views;

/// <summary>
/// Read-only AvaloniaEdit window that streams an <see cref="ILogSource"/>
/// (e.g. <c>tail -F</c>) into the document, with a bounded ring buffer,
/// pause/resume, auto-scroll, and syntax highlighting. When the source reports
/// <see cref="LogStreamState.Stopped"/> (container exited/removed) the window gets
/// a red frame and banner until the stream resumes.
/// </summary>
public partial class LogTailWindow : Window
{
    private const int DefaultRingSize = 50_000;

    private readonly ILogSource _source;
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly DispatcherTimer _flushTimer;

    private TextEditor _editor = null!;
    private TextBox _pathBox = null!;
    private TextBlock _statusText = null!;
    private Border _frameBorder = null!;
    private Border _stoppedBanner = null!;
    private TextBlock _stoppedText = null!;
    private ComboBox _syntaxCombo = null!;
    private ToggleButton _pauseButton = null!;
    private CheckBox _autoScrollCheck = null!;
    private Button _clearButton = null!;

    private TextMate.Installation? _textMate;
    private int _ringSize = DefaultRingSize;
    private bool _paused;
    private volatile bool _closed;
    private readonly string _baseTitle;

    // True while we move the viewport ourselves, so OnScrollOffsetChanged can tell
    // our own scrolling from the user's.
    private bool _programmaticScroll;
    // True when auto-scroll was switched off by scrolling up (not by the checkbox),
    // so scrolling back to the bottom switches it on again.
    private bool _followDetachedByScroll;

    public LogTailWindow(ILogSource source, string? guessFromPath = null)
    {
        _source = source;
        InitializeComponent();

        _editor          = this.FindControl<TextEditor>("Editor")!;
        _pathBox         = this.FindControl<TextBox>("PathBox")!;
        _statusText      = this.FindControl<TextBlock>("StatusText")!;
        _syntaxCombo     = this.FindControl<ComboBox>("SyntaxCombo")!;
        _pauseButton     = this.FindControl<ToggleButton>("PauseButton")!;
        _autoScrollCheck = this.FindControl<CheckBox>("AutoScrollCheck")!;
        _clearButton     = this.FindControl<Button>("ClearButton")!;
        _frameBorder     = this.FindControl<Border>("FrameBorder")!;
        _stoppedBanner   = this.FindControl<Border>("StoppedBanner")!;
        _stoppedText     = this.FindControl<TextBlock>("StoppedText")!;

        _baseTitle = (source is DockerLogsSource ? "Logs — " : "Tail — ") + source.Label;
        Title = _baseTitle;
        _pathBox.Text = source.Label;

        // Syntax combo
        var entries = SyntaxCatalog.GetAll();
        _syntaxCombo.ItemsSource = entries;
        var initialScope = guessFromPath != null ? SyntaxCatalog.GuessScope(guessFromPath) : null;
        var initial = entries.FirstOrDefault(e => e.ScopeName == initialScope) ?? entries[0];
        _syntaxCombo.SelectedItem = initial;
        _syntaxCombo.SelectionChanged += OnSyntaxChanged;

        // Install TextMate after Loaded so the TextView exists.
        Loaded += (_, _) =>
        {
            _textMate = _editor.InstallTextMate(SyntaxCatalog.GetRegistryOptions());
            ApplyGrammar(initialScope);
            _editor.TextArea.TextView.ScrollOffsetChanged += OnScrollOffsetChanged;
        };

        _pauseButton.IsCheckedChanged += (_, _) => _paused = _pauseButton.IsChecked == true;
        _clearButton.Click            += (_, _) => _editor.Document.Text = string.Empty;
        _autoScrollCheck.IsCheckedChanged += (_, _) =>
        {
            _followDetachedByScroll = false;
            if (_autoScrollCheck.IsChecked == true) ScrollToBottom();
        };
        // A smaller viewport keeps its top offset, which would leave the newest lines hidden.
        _editor.SizeChanged += (_, _) =>
        {
            if (_autoScrollCheck.IsChecked == true)
                Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Loaded);
        };

        // Ctrl+Wheel — zoom font size (tunnel so we intercept before the inner ScrollViewer).
        _editor.AddHandler(InputElement.PointerWheelChangedEvent,
            OnEditorPointerWheelChanged, RoutingStrategies.Tunnel);

        // Wire log source
        _source.LineReceived += OnLineReceived;
        _source.StatusChanged += OnSourceStatusChanged;
        _source.Start();

        // Flush timer — coalesces incoming lines into one batch per 100 ms.
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Flush());
        _flushTimer.Start();

        Closed += OnWindowClosed;
    }

    private void OnLineReceived(object? sender, string line)
    {
        if (_closed) return;
        _incoming.Enqueue(line);

        // Cheap soft-cap on the queue itself so a very fast producer can't run away
        // while the user is paused.
        if (_incoming.Count > _ringSize * 2)
        {
            for (int i = 0; i < _ringSize / 2 && _incoming.TryDequeue(out _); i++) { }
        }
    }

    private void Flush()
    {
        if (_paused || _incoming.IsEmpty) return;

        // Lines are separated, not terminated, by '\n', so the document never ends in an
        // empty line and the newest line sits flush with the bottom of the view.
        var doc = _editor.Document;
        var sb = new System.Text.StringBuilder();
        bool first = doc.TextLength == 0;
        while (_incoming.TryDequeue(out var line))
        {
            if (!first) sb.Append('\n');
            sb.Append(line);
            first = false;
        }
        if (sb.Length == 0) return;

        _programmaticScroll = true;
        try
        {
            doc.Insert(doc.TextLength, sb.ToString());

            // Ring-buffer trim: drop oldest lines if we exceed the cap.
            if (doc.LineCount > _ringSize)
            {
                int dropTo = doc.LineCount - _ringSize;
                var line = doc.GetLineByNumber(dropTo + 1);
                doc.Remove(0, line.Offset);
            }
        }
        finally { _programmaticScroll = false; }

        if (_autoScrollCheck.IsChecked == true)
            ScrollToBottom();
    }

    /// <summary>
    /// Pins the view to the last line. Layout is forced first: scrolling against the
    /// pre-insert extent lands one batch short and the next layout pass makes the view
    /// jump. Only the vertical offset changes, so a horizontal scroll is kept.
    /// </summary>
    private void ScrollToBottom()
    {
        _programmaticScroll = true;
        try
        {
            _editor.UpdateLayout();
            _editor.ScrollToVerticalOffset(Math.Max(0, _editor.ExtentHeight - _editor.ViewportHeight));
        }
        finally { _programmaticScroll = false; }
    }

    private bool IsAtBottom() =>
        _editor.VerticalOffset + _editor.ViewportHeight >= _editor.ExtentHeight - _editor.TextArea.TextView.DefaultLineHeight;

    /// <summary>
    /// Scrolling up away from the bottom switches auto-scroll off so new lines don't
    /// yank the view back; scrolling back down to the bottom switches it on again.
    /// </summary>
    private void OnScrollOffsetChanged(object? sender, EventArgs e)
    {
        if (_programmaticScroll) return;
        bool atBottom = IsAtBottom();
        if (_autoScrollCheck.IsChecked == true && !atBottom)
        {
            _autoScrollCheck.IsChecked = false;
            _followDetachedByScroll = true; // set after the checkbox handler cleared it
        }
        else if (_followDetachedByScroll && atBottom)
        {
            _autoScrollCheck.IsChecked = true;
        }
    }

    private void OnSourceStatusChanged(object? sender, LogStreamStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            Flush();
            bool stopped = status.State == LogStreamState.Stopped;
            _frameBorder.BorderThickness = new Thickness(stopped ? 2 : 0);
            _stoppedBanner.IsVisible = stopped;
            _stoppedText.Text = stopped ? "■ " + status.Message : string.Empty;
            Title = status.State switch
            {
                LogStreamState.Stopped => _baseTitle + " [stopped]",
                LogStreamState.Ended   => _baseTitle + " [ended]",
                _                      => _baseTitle,
            };
            _statusText.IsVisible = status.State == LogStreamState.Ended;
            _statusText.Text = status.State == LogStreamState.Ended ? status.Message : string.Empty;
        });
    }

    private void OnSyntaxChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syntaxCombo.SelectedItem is SyntaxEntry entry)
            ApplyGrammar(entry.ScopeName);
    }

    private void ApplyGrammar(string? scope)
    {
        if (_textMate == null) return;
        try { _textMate.SetGrammar(scope ?? string.Empty); }
        catch { /* grammar not found — plain text */ }
    }

    private void OnEditorPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        var delta = e.Delta.Y > 0 ? 1.0 : -1.0;
        _programmaticScroll = true;
        try { _editor.FontSize = Math.Clamp(_editor.FontSize + delta, 6, 72); }
        finally { _programmaticScroll = false; }
        if (_autoScrollCheck.IsChecked == true) ScrollToBottom();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _flushTimer.Stop();
        _source.LineReceived -= OnLineReceived;
        _source.StatusChanged -= OnSourceStatusChanged;
        try { _source.Dispose(); } catch { }
    }
}
