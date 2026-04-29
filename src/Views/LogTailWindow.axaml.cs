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
/// pause/resume, auto-scroll, and syntax highlighting.
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
    private ComboBox _syntaxCombo = null!;
    private ToggleButton _pauseButton = null!;
    private CheckBox _autoScrollCheck = null!;
    private Button _clearButton = null!;

    private TextMate.Installation? _textMate;
    private int _ringSize = DefaultRingSize;
    private bool _paused;
    private volatile bool _closed;

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

        Title = "Tail — " + source.Label;
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
        };

        _pauseButton.IsCheckedChanged += (_, _) => _paused = _pauseButton.IsChecked == true;
        _clearButton.Click            += (_, _) => _editor.Document.Text = string.Empty;

        // Ctrl+Wheel — zoom font size (tunnel so we intercept before the inner ScrollViewer).
        _editor.AddHandler(InputElement.PointerWheelChangedEvent,
            OnEditorPointerWheelChanged, RoutingStrategies.Tunnel);

        // Wire log source
        _source.LineReceived += OnLineReceived;
        _source.Closed       += OnSourceClosed;
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

        var sb = new System.Text.StringBuilder();
        while (_incoming.TryDequeue(out var line))
        {
            sb.Append(line);
            sb.Append('\n');
        }
        if (sb.Length == 0) return;

        var doc = _editor.Document;
        doc.Insert(doc.TextLength, sb.ToString());

        // Ring-buffer trim: drop oldest lines if we exceed the cap.
        if (doc.LineCount > _ringSize)
        {
            int dropTo = doc.LineCount - _ringSize;
            var line = doc.GetLineByNumber(dropTo + 1);
            doc.Remove(0, line.Offset);
        }

        if (_autoScrollCheck.IsChecked == true)
            _editor.ScrollToEnd();
    }

    private void OnSourceClosed(object? sender, string? error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Flush();
            _statusText.IsVisible = true;
            _statusText.Text = error == null
                ? "(stream ended)"
                : $"(stream ended: {error})";
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
        _editor.FontSize = Math.Clamp(_editor.FontSize + delta, 6, 72);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _flushTimer.Stop();
        _source.LineReceived -= OnLineReceived;
        _source.Closed       -= OnSourceClosed;
        try { _source.Dispose(); } catch { }
    }
}
