using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Linq;
using Material.Icons;
using TermThing.Sessions;
using Avalonia;

namespace TermThing.Views;

public partial class SessionTreeView : UserControl
{
    // Raised when the user double-clicks a session to launch it
    public event EventHandler<SessionDefinition>? SessionLaunchRequested;

    // Raised when the user requests editing a session's settings
    public event EventHandler<SessionDefinition>? SessionEditRequested;

    /// <summary>
    /// Raised when the user picks <i>New Session ▸ SSH</i> from the context menu.
    /// MainWindow handles this by opening the full SSH connection dialog and
    /// adding the resulting saved session to the supplied group.
    /// </summary>
    public event EventHandler<SessionGroup>? SshSessionCreateRequested;

    // Raised when the tree data changes (add/rename/delete/move/duplicate)
    public event EventHandler? TreeChanged;

    // Clipboard for cut/copy/paste within the tree
    private SessionDefinition? _clipboard;
    private bool _isCut;

    // Drag-and-drop state
    private enum DropZone { Before, After, Into }
    private SessionTreeNode? _dragCandidate;
    private PointerPressedEventArgs? _dragPressArgs;
    private Point _dragStartPos;
    private SessionTreeNode? _currentDropTarget;
    private DropZone _currentDropZone;
    // Custom DataFormat sentinel + static slot (DataTransfer only carries files/text natively)
    private static readonly DataFormat<string> TreeDragFormat =
        DataFormat.CreateStringApplicationFormat("termthing-tree-node");
    private static SessionTreeNode? s_dragPayload;

    // Overlay visuals for DnD feedback
    private Canvas _overlayCanvas = null!;
    private readonly Border _dropLine = new()
    {
        Height = 2,
        Background = new SolidColorBrush(Color.Parse("#2196F3")),
        IsVisible = false,
        IsHitTestVisible = false,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
    };
    private readonly Border _dropHighlight = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x40, 0x21, 0x96, 0xF3)),
        IsVisible = false,
        IsHitTestVisible = false,
    };

    private TreeView _tree = null!;

    // Context menu items whose enabled state depends on what is selected
    private readonly MenuItem  _mnuEdit;
    private readonly MenuItem  _mnuRename;
    private readonly MenuItem  _mnuDuplicate;
    private readonly MenuItem  _mnuCut;
    private readonly MenuItem  _mnuCopy;
    private readonly MenuItem  _mnuPaste;
    private readonly MenuItem  _mnuDelete;
    // Multi-select items (hidden in single-select mode)
    private readonly MenuItem  _mnuMoveToGroup;
    private readonly MenuItem  _mnuBulkDelete;
    private readonly Separator _mnuSepEditClipboard;
    private readonly Separator _mnuSepDelete;
    private readonly MenuItem  _mnuIconChange;

    // The root group to display (set by MainWindow after loading config)
    public SessionGroup? RootGroup { get; private set; }

    public SessionTreeView()
    {
        InitializeComponent();

        _tree = this.FindControl<TreeView>("SessionTree")!;
        _overlayCanvas = this.FindControl<Canvas>("DropOverlayCanvas")!;
        _overlayCanvas.Children.Add(_dropLine);
        _overlayCanvas.Children.Add(_dropHighlight);

        // Register drag-and-drop handlers
        _tree.AddHandler(DragDrop.DragOverEvent,  OnTreeDragOver);
        _tree.AddHandler(DragDrop.DropEvent,       OnTreeDrop);
        _tree.AddHandler(DragDrop.DragLeaveEvent,  OnTreeDragLeave);
        _tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);

        // Build the context menu in code so we hold direct references to
        // selection-sensitive items and can enable/disable them reliably.
        var mnuNewLocal   = new MenuItem { Header = "🖥 Local…"  }; mnuNewLocal.Click   += (_, _) => NewSession(SessionKind.Local);
        var mnuNewSsh     = new MenuItem { Header = "🌐 SSH…"    }; mnuNewSsh.Click     += (_, _) => SshSessionCreateRequested?.Invoke(this, SelectedGroup() ?? RootGroup!);
        var mnuNewSerial  = new MenuItem { Header = "🔌 Serial…" }; mnuNewSerial.Click  += (_, _) => NewSession(SessionKind.Serial);

        var mnuNewSession = new MenuItem { Header = "New Session" };
        mnuNewSession.Items.Add(mnuNewLocal);
        mnuNewSession.Items.Add(mnuNewSsh);
        mnuNewSession.Items.Add(mnuNewSerial);

        var mnuNewSubgroup = new MenuItem { Header = "New Subgroup" };
        mnuNewSubgroup.Click += OnNewSubgroupClicked;

        _mnuRename    = new MenuItem { Header = "Rename"    }; _mnuRename.Click    += OnRenameClicked;
        _mnuDuplicate = new MenuItem { Header = "Duplicate" }; _mnuDuplicate.Click += OnDuplicateClicked;
        _mnuCut       = new MenuItem { Header = "Cut"       }; _mnuCut.Click       += OnCutClicked;
        _mnuCopy      = new MenuItem { Header = "Copy"      }; _mnuCopy.Click      += OnCopyClicked;
        _mnuPaste     = new MenuItem { Header = "Paste"     }; _mnuPaste.Click     += OnPasteClicked;
        _mnuDelete    = new MenuItem { Header = "Delete"    }; _mnuDelete.Click    += OnDeleteClicked;

        _mnuEdit = new MenuItem { Header = "Edit…" };
        _mnuEdit.Click += OnEditClicked;

        _mnuIconChange = new MenuItem { Header = "Change Icon…" };
        _mnuIconChange.Click += OnChangeIconClicked;

        _mnuMoveToGroup = new MenuItem { Header = "Move to group", IsVisible = false };
        _mnuBulkDelete  = new MenuItem { Header = "Delete selected", IsVisible = false };
        _mnuBulkDelete.Click += OnBulkDeleteClicked;

        _mnuSepEditClipboard = new Separator();
        _mnuSepDelete        = new Separator();

        var menu = new ContextMenu();
        menu.Opening += OnContextMenuOpening;
        menu.Items.Add(mnuNewSession);
        menu.Items.Add(mnuNewSubgroup);
        menu.Items.Add(new Separator());
        menu.Items.Add(_mnuEdit);
        menu.Items.Add(_mnuIconChange);
        menu.Items.Add(_mnuRename);
        menu.Items.Add(_mnuDuplicate);
        menu.Items.Add(_mnuSepEditClipboard);
        menu.Items.Add(_mnuCut);
        menu.Items.Add(_mnuCopy);
        menu.Items.Add(_mnuPaste);
        menu.Items.Add(_mnuSepDelete);
        menu.Items.Add(_mnuDelete);
        menu.Items.Add(_mnuMoveToGroup);
        menu.Items.Add(_mnuBulkDelete);

        _tree.ContextMenu = menu;

        // Right-click should select the item under the cursor before the
        // context menu opens (Avalonia only selects on left-click by default).
        _tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);

        // Delete key shortcut
        _tree.KeyDown += OnTreeKeyDown;
    }

    public void SetRoot(SessionGroup root)
    {
        RootGroup = root;
        RebuildTree();
    }

    // -----------------------------------------------------------------------
    // Pointer / selection
    // -----------------------------------------------------------------------

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(null).Properties;

        if (props.IsRightButtonPressed)
        {
            // Walk up from the event source to find the nearest TreeViewItem so
            // that SelectedItem is correct when OnContextMenuOpening fires.
            var tvi = (e.Source as Control)?.FindAncestorOfType<TreeViewItem>();
            if (tvi?.DataContext is SessionTreeNode clickedNode)
            {
                // Preserve multi-selection when right-clicking an already-selected item.
                if (_tree.SelectedItems?.Contains(clickedNode) != true)
                    _tree.SelectedItem = clickedNode;
            }
            else
            {
                _tree.SelectedItem = null;
            }
        }

        if (props.IsLeftButtonPressed)
        {
            var tvi = (e.Source as Control)?.FindAncestorOfType<TreeViewItem>();
            if (tvi?.DataContext is SessionTreeNode node)
            {
                _dragCandidate = node;
                _dragStartPos  = e.GetPosition(_tree);
                _dragPressArgs = e;
            }
            else
            {
                _dragCandidate = null;
                _dragPressArgs = null;
            }
        }
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var selectedSessions = GetSelectedSessions();
        bool multi = selectedSessions.Count > 1;

        // Toggle between single-select and multi-select menu layouts.
        _mnuEdit.IsVisible              = !multi;
        _mnuIconChange.IsVisible        = !multi;
        _mnuRename.IsVisible            = !multi;
        _mnuDuplicate.IsVisible         = !multi;
        _mnuSepEditClipboard.IsVisible  = !multi;
        _mnuCut.IsVisible               = !multi;
        _mnuCopy.IsVisible              = !multi;
        _mnuPaste.IsVisible             = !multi;
        _mnuDelete.IsVisible            = !multi;
        _mnuMoveToGroup.IsVisible       = multi;
        _mnuBulkDelete.IsVisible        = multi;

        if (multi)
        {
            _mnuBulkDelete.Header = $"Delete {selectedSessions.Count} sessions…";
            _mnuMoveToGroup.Items.Clear();
            PopulateGroupSubmenu(_mnuMoveToGroup, RootGroup!, selectedSessions, 0);
        }
        else
        {
            var node    = _tree.SelectedItem as SessionTreeNode;
            bool hasAny = node is not null;
            bool isSess = node?.IsSession == true;
            bool canDel = hasAny && (isSess || (node!.Tag is SessionGroup g && g != RootGroup));

            _mnuRename.IsEnabled    = hasAny;
            _mnuIconChange.IsEnabled = isSess;
            _mnuDuplicate.IsEnabled = isSess;
            _mnuCut.IsEnabled       = isSess;
            _mnuCopy.IsEnabled      = isSess;
            _mnuPaste.IsEnabled     = _clipboard is not null;
            _mnuDelete.IsEnabled    = canDel;
        }
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_tree.SelectedItem is SessionTreeNode { Tag: SessionDefinition def })
            SessionLaunchRequested?.Invoke(this, def);
    }

    private async void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;

        var sessions = GetSelectedSessions();
        if (sessions.Count > 1)
        {
            await DeleteSessionsAsync(sessions);
        }
        else if (_tree.SelectedItem is SessionTreeNode node)
        {
            if (!await ConfirmDeleteAsync(node.Header)) return;
            switch (node.Tag)
            {
                case SessionDefinition def: RemoveSession(def, RootGroup!); break;
                case SessionGroup grp when grp != RootGroup: RemoveGroup(grp, RootGroup!); break;
                default: return;
            }
            RebuildTree();
            TreeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // -----------------------------------------------------------------------
    // Multi-select helpers
    // -----------------------------------------------------------------------

    private List<SessionDefinition> GetSelectedSessions() =>
        _tree.SelectedItems?
            .OfType<SessionTreeNode>()
            .Where(n => n.IsSession)
            .Select(n => (SessionDefinition)n.Tag)
            .ToList() ?? [];

    private void PopulateGroupSubmenu(MenuItem parent, SessionGroup group,
        List<SessionDefinition> sessions, int depth)
    {
        var indent = depth > 0 ? new string('\u00a0', depth * 3) : string.Empty;
        var item = new MenuItem { Header = $"{indent}{group.Name}" };
        item.Click += (_, _) => MoveSessionsToGroup(sessions, group);
        parent.Items.Add(item);
        foreach (var sub in group.Subgroups)
            PopulateGroupSubmenu(parent, sub, sessions, depth + 1);
    }

    private void MoveSessionsToGroup(List<SessionDefinition> sessions, SessionGroup target)
    {
        foreach (var def in sessions)
        {
            RemoveSession(def, RootGroup!);
            def.GroupId = target.Id;
            target.Sessions.Add(def);
        }
        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnBulkDeleteClicked(object? sender, RoutedEventArgs e)
    {
        var sessions = GetSelectedSessions();
        if (sessions.Count == 0) return;
        await DeleteSessionsAsync(sessions);
    }

    private async Task DeleteSessionsAsync(List<SessionDefinition> sessions)
    {
        if (!await ConfirmDeleteAsync(sessions.Count)) return;
        foreach (var def in sessions)
            RemoveSession(def, RootGroup!);
        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    // -----------------------------------------------------------------------
    // Context menu handlers
    // -----------------------------------------------------------------------

    private void NewSession(SessionKind kind)
    {
        var group = SelectedGroup() ?? RootGroup!;
        var def = new SessionDefinition
        {
            Kind     = kind,
            Name     = $"New {kind} Session",
            GroupId  = group.Id,
            Settings = kind switch
            {
                SessionKind.Local  => new LocalSettings(),
                SessionKind.Ssh    => new SshSettings(),
                SessionKind.Serial => new SerialSettings(),
                _                  => null,
            },
        };
        group.Sessions.Add(def);
        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds <paramref name="def"/> to <paramref name="group"/> (or root if null),
    /// rebuilds the tree, and raises <see cref="TreeChanged"/>. Used by
    /// MainWindow after the SSH connect dialog returns successfully from the
    /// <see cref="SshSessionCreateRequested"/> handler.
    /// </summary>
    public void AddSessionToGroup(SessionGroup? group, SessionDefinition def)
    {
        var target = group ?? RootGroup!;
        def.GroupId = target.Id;
        target.Sessions.Add(def);
        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnNewSubgroupClicked(object? sender, RoutedEventArgs e)
    {
        var parent = SelectedGroup() ?? RootGroup!;
        var dialog = new RenameDialog("New Group") { Title = "New Subgroup" };
        var name = await dialog.ShowDialog<string?>(TopLevel.GetTopLevel(this) as Window);
        if (name is null) return;
        var newGroup = new SessionGroup { Name = name };
        parent.Subgroups.Add(newGroup);
        RebuildTree();
        // Select the new node so right-click → Rename works immediately
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var node = FindNode(_tree.ItemsSource?.Cast<SessionTreeNode>(), newGroup);
            if (node is not null) _tree.SelectedItem = node;
        });
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static SessionTreeNode? FindNode(IEnumerable<SessionTreeNode>? nodes, object tag)
    {
        if (nodes is null) return null;
        foreach (var node in nodes)
        {
            if (node.Tag == tag) return node;
            var found = FindNode(node.Children, tag);
            if (found is not null) return found;
        }
        return null;
    }

    private async void OnRenameClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is not SessionTreeNode node) return;

        var currentName = node.Tag switch
        {
            SessionDefinition d => d.Name,
            SessionGroup g      => g.Name,
            _                   => string.Empty,
        };

        var dialog = new RenameDialog(currentName);
        var result = await dialog.ShowDialog<string?>(TopLevel.GetTopLevel(this) as Window);
        if (result is null) return;

        switch (node.Tag)
        {
            case SessionDefinition d: d.Name = result; break;
            case SessionGroup      g: g.Name = result; break;
        }

        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDuplicateClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is not SessionTreeNode { Tag: SessionDefinition def }) return;
        (FindGroupContaining(def, RootGroup!) ?? RootGroup!).Sessions.Add(def.Duplicate());
        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is not SessionTreeNode node) return;

        if (!await ConfirmDeleteAsync(node.Header)) return;

        switch (node.Tag)
        {
            case SessionDefinition def:
                RemoveSession(def, RootGroup!);
                break;
            case SessionGroup grp when grp != RootGroup:
                RemoveGroup(grp, RootGroup!);
                break;
        }

        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCutClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is SessionTreeNode { Tag: SessionDefinition def })
        {
            _clipboard = def;
            _isCut = true;
        }
    }

    private void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is SessionTreeNode { Tag: SessionDefinition def })
        {
            _clipboard = def.Clone();
            _isCut = false;
        }
    }

    private void OnPasteClicked(object? sender, RoutedEventArgs e)
    {
        if (_clipboard is null) return;
        var target = SelectedGroup() ?? RootGroup!;

        SessionDefinition toAdd;
        if (_isCut)
        {
            RemoveSession(_clipboard, RootGroup!);
            toAdd  = _clipboard;
            _isCut = false;
        }
        else
        {
            toAdd = _clipboard.Duplicate();
        }

        toAdd.GroupId = target.Id;
        target.Sessions.Add(toAdd);
        _clipboard = null;
        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    // -----------------------------------------------------------------------
    // Tree building
    // -----------------------------------------------------------------------

    private void OnEditClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is SessionTreeNode { Tag: SessionDefinition def })
            SessionEditRequested?.Invoke(this, def);
        else if (_tree.SelectedItem is SessionTreeNode { Tag: SessionGroup })
            OnRenameClicked(sender, e!);
    }

    private async void OnChangeIconClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is not SessionTreeNode { Tag: SessionDefinition def }) return;

        var dialog = new IconPickerDialog(def.IconKind, def.IconColor);
        await dialog.ShowDialog(TopLevel.GetTopLevel(this) as Window);

        def.IconKind  = dialog.ResultKind;
        def.IconColor = dialog.ResultColor;

        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildTree()
    {
        if (RootGroup is null) return;
        var root = WrapGroup(RootGroup);
        _tree.ItemsSource = new[] { root };
    }

    private static SessionTreeNode WrapGroup(SessionGroup group)
    {
        // Read persisted expansion state; default to true (expanded) when not yet recorded.
        bool expanded = Configuration.SettingsService.Temp.SessionTreeExpansion
            .GetValueOrDefault(group.Id, defaultValue: true);

        var node = new SessionTreeNode
        {
            Header       = group.Name,
            Tag          = group,
            IsExpanded   = expanded,
            ChildCount   = CountSessions(group),
            IconKindEnum = MaterialIconKind.Folder,
            IconBrush    = new SolidColorBrush(Color.Parse("#BDBDBD")),
        };
        foreach (var sub in group.Subgroups)
            node.Children.Add(WrapGroup(sub));
        foreach (var s in group.Sessions)
            node.Children.Add(new SessionTreeNode
            {
                Header       = s.Name,
                Tag          = s,
                IconKindEnum = ResolveIconKind(s),
                IconBrush    = ParseIconBrush(s.IconColor),
            });
        return node;
    }

    /// <summary>Recursively counts all sessions under <paramref name="group"/>.</summary>
    private static int CountSessions(SessionGroup group)
    {
        int count = group.Sessions.Count;
        foreach (var sub in group.Subgroups)
            count += CountSessions(sub);
        return count;
    }

    private static MaterialIconKind ResolveIconKind(SessionDefinition s)
    {
        if (s.IconKind is not null && Enum.TryParse<MaterialIconKind>(s.IconKind, out var custom))
            return custom;
        return s.Kind switch
        {
            SessionKind.Local  => MaterialIconKind.Monitor,
            SessionKind.Ssh    => MaterialIconKind.Server,
            SessionKind.Serial => MaterialIconKind.Usb,
            _                  => MaterialIconKind.HelpCircleOutline,
        };
    }

    private static IBrush ParseIconBrush(string? hex)
    {
        if (hex is not null)
        {
            try { return new SolidColorBrush(Color.Parse(hex)); }
            catch { }
        }
        return new SolidColorBrush(Color.Parse("#BDBDBD"));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private SessionGroup? SelectedGroup()
    {
        if (_tree?.SelectedItem is SessionTreeNode { Tag: SessionGroup g }) return g;
        return null;
    }

    private static SessionGroup? FindGroupContaining(SessionDefinition def, SessionGroup root)
    {
        if (root.Sessions.Contains(def)) return root;
        foreach (var sub in root.Subgroups)
        {
            var found = FindGroupContaining(def, sub);
            if (found is not null) return found;
        }
        return null;
    }

    private static bool RemoveSession(SessionDefinition def, SessionGroup root)
    {
        if (root.Sessions.Remove(def)) return true;
        foreach (var sub in root.Subgroups)
            if (RemoveSession(def, sub)) return true;
        return false;
    }

    private static bool RemoveGroup(SessionGroup grp, SessionGroup root)
    {
        if (root.Subgroups.Remove(grp)) return true;
        foreach (var sub in root.Subgroups)
            if (RemoveGroup(grp, sub)) return true;
        return false;
    }

    private Task<bool> ConfirmDeleteAsync(string name) =>
        ShowConfirmDeleteDialogAsync($"Delete '{name}'?");

    private Task<bool> ConfirmDeleteAsync(int count) =>
        ShowConfirmDeleteDialogAsync($"Delete {count} session{(count == 1 ? "" : "s")}?");

    private async Task<bool> ShowConfirmDeleteDialogAsync(string message)
    {
        var deleteBtn = new Button { Content = "Delete", IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true };
        var win = new Window
        {
            Title = "Confirm Delete",
            Width = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin  = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation         = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { deleteBtn, cancelBtn },
                    }
                }
            }
        };
        var tcs = new TaskCompletionSource<bool>();
        deleteBtn.Click += (_, _) => { tcs.TrySetResult(true);  win.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); win.Close(); };
        await win.ShowDialog(TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException());
        return await tcs.Task;
    }

    // -----------------------------------------------------------------------
    // Drag-and-drop
    // -----------------------------------------------------------------------

    // Avalonia 12 DataTransfer only carries files/text; use a static slot for in-process custom payloads.

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is null || _dragPressArgs is null) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            _dragCandidate = null;
            _dragPressArgs = null;
            return;
        }

        var pos = e.GetPosition(_tree);
        if (Math.Abs(pos.X - _dragStartPos.X) < 5 && Math.Abs(pos.Y - _dragStartPos.Y) < 5)
            return;

        var candidate  = _dragCandidate;
        var pressArgs  = _dragPressArgs;
        _dragCandidate = null; // prevent re-entry
        _dragPressArgs = null;
        s_dragPayload  = candidate;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(TreeDragFormat, candidate.Header ?? string.Empty));
        await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Move);
        s_dragPayload = null;
        ClearDropVisuals();
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Formats.Contains(TreeDragFormat) || s_dragPayload is not SessionTreeNode draggedNode)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropVisuals();
            return;
        }

        var pos = e.GetPosition(_tree);
        var tvi = FindTreeViewItemAt(_tree.InputHitTest(pos) as Control);

        if (tvi?.DataContext is not SessionTreeNode targetNode || targetNode == draggedNode)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropVisuals();
            _currentDropTarget = null;
            return;
        }

        var tviTopInTree = tvi.TranslatePoint(new Point(0, 0), _tree);
        if (!tviTopInTree.HasValue) { ClearDropVisuals(); return; }

        var tviHeight  = tvi.Bounds.Height;
        var relativeY  = pos.Y - tviTopInTree.Value.Y;
        var (canDrop, zone) = ComputeDropZone(draggedNode, targetNode, relativeY, tviHeight);

        if (!canDrop)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropVisuals();
            _currentDropTarget = null;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled     = true;
        _currentDropTarget = targetNode;
        _currentDropZone   = zone;

        var tviTopInCanvas = tvi.TranslatePoint(new Point(0, 0), _overlayCanvas);
        if (tviTopInCanvas.HasValue)
            ShowDropVisual(tviTopInCanvas.Value.Y, tviHeight, zone);
    }

    private void OnTreeDragLeave(object? sender, DragEventArgs e)
    {
        ClearDropVisuals();
        _currentDropTarget = null;
    }

    private void OnTreeDrop(object? sender, DragEventArgs e)
    {
        ClearDropVisuals();

        if (!e.DataTransfer.Formats.Contains(TreeDragFormat) || s_dragPayload is not SessionTreeNode draggedNode)
            return;
        if (_currentDropTarget is null) return;

        var target = _currentDropTarget;
        var zone   = _currentDropZone;
        _currentDropTarget = null;

        PerformDrop(draggedNode, target, zone);
    }

    private static (bool canDrop, DropZone zone) ComputeDropZone(
        SessionTreeNode dragged, SessionTreeNode target, double relativeY, double height)
    {
        bool targetIsGroup = target.IsGroup;
        bool sourceIsGroup = dragged.IsGroup;

        if (!targetIsGroup)
        {
            // Target is a session — groups cannot drop onto sessions
            if (sourceIsGroup) return (false, default);
            return (true, relativeY < height * 0.5 ? DropZone.Before : DropZone.After);
        }

        // Target is a group
        bool before = relativeY < height * 0.3;
        bool after  = relativeY > height * 0.7;

        if (sourceIsGroup)
        {
            // Group onto group
            if (before) return (true, DropZone.Before);
            if (after)  return (true, DropZone.After);
            // Into: prevent dropping a group into itself or a descendant
            var dragGrp   = (SessionGroup)dragged.Tag;
            var targetGrp = (SessionGroup)target.Tag;
            return IsAncestorOrSelf(dragGrp, targetGrp) ? (false, default) : (true, DropZone.Into);
        }
        else
        {
            // Session onto group — only Into (middle) allowed; before/after is ambiguous
            if (!before && !after) return (true, DropZone.Into);
            return (false, default);
        }
    }

    private void PerformDrop(SessionTreeNode draggedNode, SessionTreeNode targetNode, DropZone zone)
    {
        if (RootGroup is null) return;

        if (draggedNode.Tag is SessionDefinition def)
        {
            var sourceGroup = FindGroupContaining(def, RootGroup);
            if (sourceGroup is null) return;

            if (targetNode.Tag is SessionDefinition targetDef)
            {
                // Session onto session: reorder (within same or across groups)
                var targetGroup = FindGroupContaining(targetDef, RootGroup);
                if (targetGroup is null) return;
                sourceGroup.Sessions.Remove(def);
                var idx = targetGroup.Sessions.IndexOf(targetDef);
                if (zone == DropZone.After) idx++;
                idx = Math.Clamp(idx, 0, targetGroup.Sessions.Count);
                def.GroupId = targetGroup.Id;
                targetGroup.Sessions.Insert(idx, def);
            }
            else if (targetNode.Tag is SessionGroup destGroup)
            {
                // Session into group
                sourceGroup.Sessions.Remove(def);
                def.GroupId = destGroup.Id;
                destGroup.Sessions.Add(def);
            }
            else return;
        }
        else if (draggedNode.Tag is SessionGroup grp)
        {
            var sourceParent = FindGroupContainingGroup(grp, RootGroup);
            if (sourceParent is null) return;

            if (targetNode.Tag is SessionGroup targetGrp)
            {
                if (zone == DropZone.Into)
                {
                    // Move group into another group
                    if (IsAncestorOrSelf(grp, targetGrp)) return;
                    sourceParent.Subgroups.Remove(grp);
                    targetGrp.Subgroups.Add(grp);
                }
                else
                {
                    // Reorder groups among siblings
                    var targetParent = FindGroupContainingGroup(targetGrp, RootGroup);
                    if (targetParent is null) return;
                    sourceParent.Subgroups.Remove(grp);
                    var idx = targetParent.Subgroups.IndexOf(targetGrp);
                    if (zone == DropZone.After) idx++;
                    idx = Math.Clamp(idx, 0, targetParent.Subgroups.Count);
                    targetParent.Subgroups.Insert(idx, grp);
                }
            }
            else return; // group onto session — not allowed
        }
        else return;

        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowDropVisual(double topY, double itemHeight, DropZone zone)
    {
        var w = _overlayCanvas.Bounds.Width;

        if (zone == DropZone.Into)
        {
            Canvas.SetLeft(_dropHighlight, 0);
            Canvas.SetTop(_dropHighlight, topY);
            _dropHighlight.Width   = w;
            _dropHighlight.Height  = itemHeight;
            _dropHighlight.IsVisible = true;
            _dropLine.IsVisible      = false;
        }
        else
        {
            var lineY = zone == DropZone.Before ? topY : topY + itemHeight - 2;
            Canvas.SetLeft(_dropLine, 0);
            Canvas.SetTop(_dropLine, lineY);
            _dropLine.Width    = w;
            _dropLine.IsVisible      = true;
            _dropHighlight.IsVisible = false;
        }
    }

    private void ClearDropVisuals()
    {
        _dropLine.IsVisible      = false;
        _dropHighlight.IsVisible = false;
    }

    private static TreeViewItem? FindTreeViewItemAt(Control? hit)
    {
        if (hit is TreeViewItem tvi) return tvi;
        return hit?.FindAncestorOfType<TreeViewItem>();
    }

    private static SessionGroup? FindGroupContainingGroup(SessionGroup grp, SessionGroup root)
    {
        if (root.Subgroups.Contains(grp)) return root;
        foreach (var sub in root.Subgroups)
        {
            var found = FindGroupContainingGroup(grp, sub);
            if (found is not null) return found;
        }
        return null;
    }

    private static bool IsAncestorOrSelf(SessionGroup ancestor, SessionGroup query)
    {
        if (ancestor == query) return true;
        foreach (var sub in ancestor.Subgroups)
            if (IsAncestorOrSelf(sub, query)) return true;
        return false;
    }
}
