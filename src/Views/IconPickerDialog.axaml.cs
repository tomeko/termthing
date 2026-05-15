using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace TermThing.Views;

public partial class IconPickerDialog : Window
{
    // -----------------------------------------------------------------------
    // Curated icon list — mapped from existing session SVGs + useful extras.
    // Names are resolved at runtime via Enum.TryParse so unknown names are
    // silently skipped rather than causing compile errors.
    // -----------------------------------------------------------------------
    private static readonly (string Label, string KindName)[] s_allIcons =
    [
        // Session-kind defaults (always shown first)
        ("Monitor",         "Monitor"),
        ("Server",          "Server"),
        ("USB / Serial",    "Usb"),
        ("Terminal",        "Console"),
        // Mapped from SVGs
        ("Android",         "Android"),
        ("Backup",          "Backup"),
        ("Book",            "Book"),
        ("Briefcase",       "Briefcase"),
        ("Crown",           "Crown"),
        ("Dashboard",       "ViewDashboard"),
        ("DB Upload",       "DatabaseArrowUp"),
        ("Code",            "CodeBraces"),
        ("Devices",         "Devices"),
        ("Garage / Home",   "Garage"),
        ("Chart Line",      "ChartLine"),
        ("Chart Bar",       "ChartBar"),
        ("Chart Stacked",   "ChartBarStacked"),
        ("People",          "AccountMultiple"),
        ("Web / HTTP",      "Web"),
        ("LAN",             "Lan"),
        ("Laptop",          "Laptop"),
        ("Music",           "MusicBox"),
        ("Lightbulb",       "Lightbulb"),
        ("Email",           "Email"),
        ("Memory / RAM",    "Memory"),
        ("Network",         "NetworkOutline"),
        ("Wi-Fi",           "Wifi"),
        ("Atom",            "AtomVariant"),
        ("Person",          "Account"),
        ("Person Detail",   "AccountDetails"),
        ("Images",          "ImageMultiple"),
        ("Podcast",         "Podcast"),
        ("Globe",           "Earth"),
        ("School",          "School"),
        ("Cart",            "Cart"),
        ("Skull",           "Skull"),
        ("Traffic Light",   "TrafficLight"),
        ("Video",           "Video"),
        ("Heartbeat",       "HeartPulse"),
        ("Grid",            "ViewGrid"),
        // Bonus useful icons
        ("Folder",          "Folder"),
        ("Database",        "Database"),
        ("Cloud",           "Cloud"),
        ("Lock",            "Lock"),
        ("Key",             "Key"),
        ("Settings",        "Cog"),
        ("Home",            "Home"),
        ("Star",            "Star"),
        ("Heart",           "Heart"),
        ("Fire",            "Fire"),
        ("Flash",           "Flash"),
        ("Robot",           "Robot"),
        ("Docker",          "Docker"),
        ("Git",             "Git"),
        ("Raspberry Pi",    "RaspberryPi"),
        ("Keyboard",        "Keyboard"),
    ];

    // Resolved at startup — only entries whose KindName parses successfully.
    private static readonly (string Label, MaterialIconKind Kind)[] s_icons;

    // Preset color swatches. null = default (no custom color).
    private static readonly (string Label, string? Hex)[] s_colors =
    [
        ("Default",    null),
        ("Blue",       "#64B5F6"),
        ("Sky",        "#4FC3F7"),
        ("Teal",       "#4DB6AC"),
        ("Green",      "#81C784"),
        ("Lime",       "#AED581"),
        ("Amber",      "#FFD54F"),
        ("Orange",     "#FFB74D"),
        ("Red",        "#E57373"),
        ("Pink",       "#F48FB1"),
        ("Purple",     "#CE93D8"),
        ("Indigo",     "#7986CB"),
        ("Grey",       "#B0BEC5"),
        ("White",      "#FFFFFF"),
    ];

    static IconPickerDialog()
    {
        var resolved = new List<(string, MaterialIconKind)>();
        foreach (var (label, name) in s_allIcons)
        {
            if (Enum.TryParse<MaterialIconKind>(name, out var kind))
                resolved.Add((label, kind));
        }
        s_icons = [.. resolved];
    }

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------
    private string? _selectedKind;
    private string? _selectedColor;

    private Button? _selectedIconBtn;
    private Border? _selectedColorBorder;

    public string? ResultKind  { get; private set; }
    public string? ResultColor { get; private set; }

    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------
    public IconPickerDialog(string? currentKind, string? currentColor)
    {
        InitializeComponent();

        _selectedKind  = currentKind;
        _selectedColor = currentColor;

        var searchBox = this.FindControl<TextBox>("SearchBox")!;
        searchBox.TextChanged += (_, _) => RebuildIconGrid(searchBox.Text ?? "");

        this.FindControl<Button>("OkBtn")!.Click += (_, _) =>
        {
            ResultKind  = _selectedKind;
            ResultColor = _selectedColor;
            Close();
        };

        this.FindControl<Button>("CancelBtn")!.Click += (_, _) =>
        {
            ResultKind  = currentKind;
            ResultColor = currentColor;
            Close();
        };

        this.FindControl<Button>("ClearBtn")!.Click += (_, _) =>
        {
            _selectedKind = null;
            SelectIconBtn(null);
        };

        BuildColorSwatches();
        RebuildIconGrid("");
    }

    // -----------------------------------------------------------------------
    // Icon grid
    // -----------------------------------------------------------------------
    private void RebuildIconGrid(string filter)
    {
        var wrap = this.FindControl<WrapPanel>("IconWrap")!;
        wrap.Children.Clear();
        _selectedIconBtn = null;

        var filtered = string.IsNullOrWhiteSpace(filter)
            ? s_icons
            : s_icons.Where(x => x.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                               || x.Kind.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .ToArray();

        foreach (var (label, kind) in filtered)
        {
            var btn = BuildIconButton(label, kind);
            if (kind.ToString() == _selectedKind)
                SetSelectedIconBtn(btn);
            wrap.Children.Add(btn);
        }
    }

    private Button BuildIconButton(string label, MaterialIconKind kind)
    {
        var icon = new MaterialIcon
        {
            Kind   = kind,
            Width  = 28,
            Height = 28,
        };

        var text = new TextBlock
        {
            Text      = label,
            FontSize  = 9,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            TextWrapping  = TextWrapping.Wrap,
            MaxWidth  = 68,
            Margin    = new Thickness(0, 3, 0, 0),
        };

        var btn = new Button
        {
            Width   = 76,
            Height  = 76,
            Padding = new Thickness(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment   = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children            = { icon, text },
            },
        };

        ToolTip.SetTip(btn, kind.ToString());

        btn.Click += (_, _) =>
        {
            _selectedKind = kind.ToString();
            SetSelectedIconBtn(btn);
        };

        return btn;
    }

    private void SetSelectedIconBtn(Button? btn)
    {
        // Remove highlight from previous
        if (_selectedIconBtn is not null)
            _selectedIconBtn.Background = null;

        _selectedIconBtn = btn;

        if (btn is not null)
            btn.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x21, 0x96, 0xF3));
    }

    private void SelectIconBtn(Button? btn)
    {
        SetSelectedIconBtn(btn);
        if (btn is null) _selectedKind = null;
    }

    // -----------------------------------------------------------------------
    // Color swatches
    // -----------------------------------------------------------------------
    private void BuildColorSwatches()
    {
        var panel = this.FindControl<WrapPanel>("ColorPanel")!;

        foreach (var (label, hex) in s_colors)
        {
            var swatch = BuildColorSwatch(label, hex);
            if (hex == _selectedColor)
                SetSelectedColorBorder(swatch);
            panel.Children.Add(swatch);
        }
    }

    private Border BuildColorSwatch(string label, string? hex)
    {
        IBrush fill = hex is null
            ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint   = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Color.Parse("#555555"), 0),
                    new GradientStop(Color.Parse("#555555"), 0.45),
                    new GradientStop(Color.Parse("#FF4444"), 0.45),
                    new GradientStop(Color.Parse("#FF4444"), 0.55),
                    new GradientStop(Color.Parse("#555555"), 0.55),
                ],
            }
            : new SolidColorBrush(Color.Parse(hex));

        var border = new Border
        {
            Width          = 28,
            Height         = 28,
            CornerRadius   = new CornerRadius(14),
            Background     = fill,
            BorderThickness = new Thickness(2),
            BorderBrush    = Brushes.Transparent,
            Margin         = new Thickness(0, 0, 6, 6),
            Cursor         = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        ToolTip.SetTip(border, label);

        border.PointerPressed += (_, _) =>
        {
            _selectedColor = hex;
            SetSelectedColorBorder(border);
        };

        return border;
    }

    private void SetSelectedColorBorder(Border? border)
    {
        if (_selectedColorBorder is not null)
            _selectedColorBorder.BorderBrush = Brushes.Transparent;

        _selectedColorBorder = border;

        if (border is not null)
            border.BorderBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    }
}
