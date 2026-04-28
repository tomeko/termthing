using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using TermThing.Editor;

namespace TermThing.Views;

/// <summary>
/// Independent top-level window that hosts an AvaloniaEdit text editor for a
/// single remote SFTP file.  Multiple instances can coexist (one per file).
/// </summary>
public partial class TextEditorWindow : Window
{
    private readonly TextEditorContext _ctx;
    private TextEditor _editor = null!;
    private TextBlock _statusText = null!;
    private Button _saveButton = null!;
    private ComboBox _syntaxCombo = null!;
    private TextBox _pathBox = null!;

    private TextMate.Installation? _textMate;
    private bool _isDirty;
    private bool _skipConfirm;          // "Remember for this file" was checked
    private bool _sessionEnded;         // SSH connection died — upload no longer possible
    private bool _forceClosing;         // ForceClose() was called; skip our own Closing handler

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    public TextEditorWindow(TextEditorContext ctx, string initialText, RegistryOptions registryOptions)
    {
        _ctx = ctx;
        InitializeComponent();

        _editor     = this.FindControl<TextEditor>("Editor")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _saveButton = this.FindControl<Button>("SaveButton")!;
        _syntaxCombo = this.FindControl<ComboBox>("SyntaxCombo")!;
        _pathBox    = this.FindControl<TextBox>("PathBox")!;

        Title = $"{Path.GetFileName(ctx.RemotePath)} — {ctx.DisplayHost}";
        _pathBox.Text = ctx.RemotePath;

        // Editor options
        _editor.Options.IndentationSize = 4;
        _editor.Options.ConvertTabsToSpaces = true;

        // Set the text in the constructor so it is visible even before TextMate
        // has had a chance to tokenise.  UndoStack is cleared so the initial
        // load doesn't show up as an undoable action.
        _editor.Document.Text = initialText;
        _editor.Document.UndoStack.ClearAll();

        // Install TextMate after the control is attached to the visual tree.
        // Doing it in the constructor causes the colourising transformer to have
        // no TextView yet, so nothing renders.
        this.Loaded += (_, _) =>
        {
            _textMate = _editor.InstallTextMate(registryOptions);
            ApplyGrammar(SyntaxCatalog.GuessScope(ctx.RemotePath));

            // Sync the combo to the detected grammar
            var guessedScope = SyntaxCatalog.GuessScope(ctx.RemotePath);
            var match = (_syntaxCombo.ItemsSource as IEnumerable<SyntaxEntry>)
                        ?.FirstOrDefault(e => e.ScopeName == guessedScope);
            if (match != null) _syntaxCombo.SelectedItem = match;
        };

        // Now watch for changes (after text is loaded so initial set isn't dirty)
        _editor.TextChanged += OnTextChanged;

        // Syntax dropdown — populate items now so the combo has content when the window opens
        var entries = SyntaxCatalog.GetAll();
        _syntaxCombo.ItemsSource = entries;
        var current = entries.FirstOrDefault(e => e.ScopeName == SyntaxCatalog.GuessScope(ctx.RemotePath))
                      ?? entries[0]; // Plain Text
        _syntaxCombo.SelectedItem = current;
        _syntaxCombo.SelectionChanged += OnSyntaxChanged;

        // Toolbar
        _saveButton.Click += async (_, _) => await SaveAsync();

        // Ctrl+S
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.S, KeyModifiers.Control),
            Command = new DelegateCommand(async () => await SaveAsync()),
        });

        // Ctrl+Wheel — zoom font size.
        // Use Tunnel (preview) strategy so we intercept before the inner
        // ScrollViewer handles the wheel event for scrolling.
        _editor.AddHandler(InputElement.PointerWheelChangedEvent,
            OnEditorPointerWheelChanged, RoutingStrategies.Tunnel);

        Closing += OnWindowClosing;
    }

    // -----------------------------------------------------------------------
    // Public API used by EditorRegistry
    // -----------------------------------------------------------------------

    /// <summary>True when the document has unsaved changes.</summary>
    public bool IsDirty => _isDirty;

    /// <summary>
    /// Replaces the editor content without marking it dirty (used after a
    /// server-side reload).
    /// </summary>
    public void SetContent(string text)
    {
        _editor.TextChanged -= OnTextChanged;
        _editor.Document.Text = text;
        _editor.Document.UndoStack.ClearAll();
        _editor.TextChanged += OnTextChanged;
        SetDirty(false);
    }

    /// <summary>
    /// Shows the "reload / keep / cancel" prompt. Called when the file is opened
    /// again from the SFTP browser while this window already has unsaved edits.
    /// </summary>
    public async Task<ReloadChoice> PromptReloadAsync()
    {
        var tcs = new TaskCompletionSource<ReloadChoice>();

        var reloadBtn  = new Button { Content = "Reload from server", IsDefault = true, Padding = new Avalonia.Thickness(12, 4) };
        var keepBtn    = new Button { Content = "Keep local changes",  Padding = new Avalonia.Thickness(12, 4) };
        var cancelBtn  = new Button { Content = "Cancel",              IsCancel = true, Padding = new Avalonia.Thickness(12, 4) };

        var dialog = new Window
        {
            Title = "File already open",
            Width = 400,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Avalonia.Controls.StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{Path.GetFileName(_ctx.RemotePath)} is already open with unsaved changes.\nWhat would you like to do?",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new Avalonia.Controls.StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { reloadBtn, keepBtn, cancelBtn },
                    },
                },
            },
        };

        reloadBtn.Click  += (_, _) => dialog.Close(ReloadChoice.Reload);
        keepBtn.Click    += (_, _) => dialog.Close(ReloadChoice.Keep);
        cancelBtn.Click  += (_, _) => dialog.Close(ReloadChoice.Cancel);

        var result = await dialog.ShowDialog<ReloadChoice>(this);
        return result;
    }

    /// <summary>
    /// Called by <see cref="EditorRegistry.CloseSessionEditorsAsync"/> to
    /// prompt the user to save before the session is torn down.
    /// Returns <see langword="true"/> if the window should be closed.
    /// </summary>
    public async Task<bool> RequestCloseAsync()
    {
        if (!_isDirty)
            return true;

        // Try to upload then close
        var saved = await SaveAsync(allowCancel: true);
        return saved; // false = user cancelled upload → keep window open
    }

    /// <summary>
    /// Closes the window unconditionally, bypassing our own closing handler.
    /// </summary>
    public void ForceClose()
    {
        _forceClosing = true;
        Close();
    }

    /// <summary>
    /// Marks the session as dead so the Save button is disabled and a notice is
    /// shown.  Called by <see cref="EditorRegistry.NotifySessionEnded"/>.
    /// </summary>
    public void NotifySessionEnded()
    {
        _sessionEnded = true;
        _saveButton.IsEnabled = false;
        ShowStatus("Session ended — remote file is no longer accessible. Temp copy: " + _ctx.LocalTempPath);
    }

    // -----------------------------------------------------------------------
    // Private — change tracking & grammar
    // -----------------------------------------------------------------------

    private void OnTextChanged(object? sender, EventArgs e) => SetDirty(true);

    private void SetDirty(bool dirty)
    {
        _isDirty = dirty;
        _saveButton.IsEnabled = dirty && !_sessionEnded;
        Title = dirty
            ? $"● {Path.GetFileName(_ctx.RemotePath)} — {_ctx.DisplayHost}"
            : $"{Path.GetFileName(_ctx.RemotePath)} — {_ctx.DisplayHost}";
    }

    private void OnSyntaxChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syntaxCombo.SelectedItem is SyntaxEntry entry)
            ApplyGrammar(entry.ScopeName);
    }

    private void ApplyGrammar(string? scopeName)
    {
        if (_textMate is null) return;
        try
        {
            _textMate.SetGrammar(scopeName ?? string.Empty);
        }
        catch
        {
            // Grammar not found — fall back gracefully (plain text)
        }
    }

    private void OnEditorPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        var delta = e.Delta.Y > 0 ? 1.0 : -1.0;
        _editor.FontSize = Math.Clamp(_editor.FontSize + delta, 6, 72);
    }

    // -----------------------------------------------------------------------
    // Save / upload
    // -----------------------------------------------------------------------

    /// <summary>
    /// Performs the save flow: optionally shows a confirm dialog, uploads the file,
    /// then clears the dirty flag.
    /// </summary>
    /// <param name="allowCancel">
    /// When <see langword="true"/> (session teardown path) a Cancel result keeps the
    /// window open and returns <see langword="false"/>. When <see langword="false"/>
    /// (Ctrl+S / Save button) a Cancel does nothing and returns <see langword="false"/>.
    /// </param>
    private async Task<bool> SaveAsync(bool allowCancel = false)
    {
        if (_sessionEnded || !_isDirty) return true;

        if (!_skipConfirm)
        {
            var dialog = new SaveConfirmDialog(_ctx.RemotePath, _ctx.DisplayHost);
            var result = await dialog.ShowDialog<SaveConfirmResult?>(this);
            if (result is null || !result.Upload)
                return !allowCancel; // Cancel pressed

            _skipConfirm = result.Remember;
        }

        ShowStatus("Saving…");
        _saveButton.IsEnabled = false;

        try
        {
            var text = _editor.Document.Text;
            // Detect existing BOM; re-encode with the same encoding
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            await _ctx.UploadAsync(bytes, CancellationToken.None);

            // Write back to local temp so the on-disk copy stays in sync
            await File.WriteAllBytesAsync(_ctx.LocalTempPath, bytes);

            SetDirty(false);
            ShowStatus("Saved.", autoHideMs: 3000);
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus($"Upload failed: {ex.Message}");
            _saveButton.IsEnabled = _isDirty && !_sessionEnded;
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Closing guard
    // -----------------------------------------------------------------------

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClosing) return; // ForceClose() — let it through
        if (!_isDirty) return;     // Clean — let it through

        // Cancel the close and show our own prompt
        e.Cancel = true;

        var saved = await SaveAsync(allowCancel: true);
        if (saved)
        {
            _forceClosing = true;
            Close();
        }
        // else — user cancelled; window stays open
    }

    // -----------------------------------------------------------------------
    // Status bar helpers
    // -----------------------------------------------------------------------

    private CancellationTokenSource? _statusHideCts;

    private void ShowStatus(string message, int autoHideMs = 0)
    {
        _statusHideCts?.Cancel();
        _statusHideCts = null;

        _statusText.Text = message;
        _statusText.IsVisible = true;

        if (autoHideMs > 0)
        {
            var cts = new CancellationTokenSource();
            _statusHideCts = cts;
            _ = Task.Delay(autoHideMs, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                    Dispatcher.UIThread.Post(() =>
                    {
                        _statusText.IsVisible = false;
                        _statusText.Text = string.Empty;
                    });
            }, TaskScheduler.Default);
        }
    }
}

/// <summary>Minimal <see cref="System.Windows.Input.ICommand"/> adapter for key bindings.</summary>
file sealed class DelegateCommand(Func<Task> execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _ = execute();
}
