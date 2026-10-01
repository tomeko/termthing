using System.Globalization;
using System.Text;

namespace TermThing.Panes;

/// <summary>How a split arranges its children. Names follow tmux's layout syntax.</summary>
public enum SplitAxis
{
    /// <summary>Children side by side (tmux <c>{…}</c>, "split right").</summary>
    LeftRight,
    /// <summary>Children stacked (tmux <c>[…]</c>, "split down").</summary>
    TopBottom,
}

public enum PaneDirection { Left, Right, Up, Down }

/// <summary>
/// A node of a <see cref="PaneLayout"/>: either a leaf holding one pane, or a split
/// whose children run along <see cref="Axis"/>. Geometry is in character cells, with a
/// one-cell separator between neighbouring children, exactly as tmux lays out panes.
/// </summary>
public sealed class LayoutNode
{
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }

    /// <summary>Pane id for a leaf; -1 for a split.</summary>
    public int PaneId { get; internal set; } = -1;

    public SplitAxis Axis { get; internal set; }
    public List<LayoutNode> Children { get; } = new();
    public LayoutNode? Parent { get; internal set; }

    /// <summary>
    /// This node's share of its parent's length along the parent's axis. Window
    /// resizes reflow from shares rather than from the rounded cell sizes, so
    /// proportions don't drift as the window is resized back and forth.
    /// </summary>
    internal double Share { get; set; } = 1;

    public bool IsLeaf => Children.Count == 0;

    public int SizeAlong(SplitAxis axis) => axis == SplitAxis.LeftRight ? Width : Height;

    internal void SetSize(SplitAxis axis, int size)
    {
        if (axis == SplitAxis.LeftRight) Width = size; else Height = size;
    }

    public IEnumerable<LayoutNode> Leaves()
    {
        if (IsLeaf) { yield return this; yield break; }
        foreach (var c in Children)
            foreach (var l in c.Leaves())
                yield return l;
    }

    public override string ToString() =>
        IsLeaf ? $"pane {PaneId} {Width}x{Height}+{X}+{Y}" : $"{Axis} {Width}x{Height}+{X}+{Y} ({Children.Count})";
}

/// <summary>
/// Split-pane layout as a tree of cells, shaped like a tmux window layout so the
/// same model can describe local split panes and, later, panes of a tmux session in
/// control mode (<see cref="ToTmuxString"/> / <see cref="ParseTmux"/>). Pure data:
/// no UI types, all operations synchronous.
/// </summary>
public sealed class PaneLayout
{
    /// <summary>Smallest pane a user split or resize may produce. Window shrinks may go below.</summary>
    public const int MinPaneWidth = 8;
    public const int MinPaneHeight = 2;

    public LayoutNode Root { get; private set; }
    public int Width => Root.Width;
    public int Height => Root.Height;

    private PaneLayout(LayoutNode root) => Root = root;

    public static PaneLayout Single(int paneId, int width, int height) =>
        new(new LayoutNode { PaneId = paneId, Width = Math.Max(1, width), Height = Math.Max(1, height) });

    public IEnumerable<LayoutNode> Leaves() => Root.Leaves();

    public int PaneCount => Root.Leaves().Count();

    public LayoutNode? Find(int paneId) => Root.Leaves().FirstOrDefault(l => l.PaneId == paneId);

    /// <summary>
    /// When <paramref name="other"/> has the same tree (axes, child counts, pane ids),
    /// copies its sizes and positions into this layout's nodes and returns true; the
    /// nodes themselves stay, so anything holding on to them (divider bars) stays valid.
    /// </summary>
    public bool TryUpdateGeometry(PaneLayout other)
    {
        if (!SameShape(Root, other.Root)) return false;
        CopyGeometry(Root, other.Root);
        return true;

        static bool SameShape(LayoutNode a, LayoutNode b) =>
            a.IsLeaf == b.IsLeaf
            && (a.IsLeaf
                ? a.PaneId == b.PaneId
                : a.Axis == b.Axis && a.Children.Count == b.Children.Count
                  && a.Children.Zip(b.Children).All(p => SameShape(p.First, p.Second)));

        static void CopyGeometry(LayoutNode to, LayoutNode from)
        {
            to.X = from.X; to.Y = from.Y; to.Width = from.Width; to.Height = from.Height;
            for (int i = 0; i < to.Children.Count; i++) CopyGeometry(to.Children[i], from.Children[i]);
            if (!to.IsLeaf) RecomputeShares(to);
        }
    }

    // -----------------------------------------------------------------------
    // Split / remove
    // -----------------------------------------------------------------------

    public bool CanSplit(int paneId, SplitAxis axis)
    {
        var leaf = Find(paneId);
        return leaf is not null && leaf.SizeAlong(axis) >= 2 * MinAlong(axis) + 1;
    }

    /// <summary>
    /// Halves pane <paramref name="paneId"/> along <paramref name="axis"/> and puts
    /// <paramref name="newPaneId"/> in the second half (the first when
    /// <paramref name="newFirst"/>). Like tmux, a split along the same axis as the
    /// parent becomes a new sibling instead of a nested split.
    /// </summary>
    public bool Split(int paneId, SplitAxis axis, int newPaneId, bool newFirst = false)
    {
        if (!CanSplit(paneId, axis)) return false;
        var leaf = Find(paneId)!;

        // tmux gives the odd cell to the first half: 80 columns split as 40 | 39.
        int total = leaf.SizeAlong(axis);
        int second = (total - 1) / 2;
        int first = total - 1 - second;

        var added = new LayoutNode { PaneId = newPaneId, Width = leaf.Width, Height = leaf.Height };
        added.SetSize(axis, newFirst ? first : second);

        var parent = leaf.Parent;
        if (parent is not null && parent.Axis == axis)
        {
            // Sibling in the existing split; the two halves share the old pane's share.
            leaf.SetSize(axis, newFirst ? second : first);
            var share = leaf.Share / 2;
            leaf.Share = added.Share = share;
            added.Parent = parent;
            parent.Children.Insert(parent.Children.IndexOf(leaf) + (newFirst ? 0 : 1), added);
        }
        else
        {
            // Turn the leaf into a split holding the old pane and the new one.
            var kept = new LayoutNode { PaneId = leaf.PaneId, Width = leaf.Width, Height = leaf.Height, Share = 0.5 };
            kept.SetSize(axis, newFirst ? second : first);
            added.Share = 0.5;
            leaf.PaneId = -1;
            leaf.Axis = axis;
            kept.Parent = added.Parent = leaf;
            leaf.Children.Add(newFirst ? added : kept);
            leaf.Children.Add(newFirst ? kept : added);
        }

        Position(Root, Root.X, Root.Y);
        return true;
    }

    /// <summary>
    /// Removes a pane. Its space (plus the separator) goes to the previous sibling, or
    /// the next one if it was first, as tmux does. Returns false for the last pane.
    /// </summary>
    public bool Remove(int paneId)
    {
        var leaf = Find(paneId);
        var parent = leaf?.Parent;
        if (leaf is null || parent is null) return false;

        var axis = parent.Axis;
        int index = parent.Children.IndexOf(leaf);
        var heir = parent.Children[index > 0 ? index - 1 : 1];
        parent.Children.RemoveAt(index);
        FitAlong(heir, axis, heir.SizeAlong(axis) + leaf.SizeAlong(axis) + 1);
        heir.Share += leaf.Share;

        if (parent.Children.Count == 1)
            Collapse(parent);

        Position(Root, Root.X, Root.Y);
        return true;
    }

    /// <summary>
    /// Replaces a split that has one child left with that child. A remaining split on
    /// the grandparent's axis stays nested rather than being merged into it — tmux
    /// leaves it that way too, so layouts stay identical to tmux's.
    /// </summary>
    private void Collapse(LayoutNode split)
    {
        var only = split.Children[0];
        var grand = split.Parent;
        only.Share = split.Share;

        if (grand is null)
        {
            only.Parent = null;
            Root = only;
            return;
        }

        int at = grand.Children.IndexOf(split);
        grand.Children[at] = only;
        only.Parent = grand;
    }

    // -----------------------------------------------------------------------
    // Resizing
    // -----------------------------------------------------------------------

    /// <summary>Reflows the whole layout to a new window size, keeping proportions.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        FitAlong(Root, SplitAxis.LeftRight, width);
        FitAlong(Root, SplitAxis.TopBottom, height);
        Position(Root, 0, 0);
    }

    /// <summary>
    /// Moves the separator after child <paramref name="index"/> of <paramref name="split"/>
    /// by <paramref name="delta"/> cells (positive grows that child). Clamped so neither
    /// side drops below the minimum pane size. Returns the distance actually moved.
    /// </summary>
    public int MoveDivider(LayoutNode split, int index, int delta)
    {
        if (split.IsLeaf || index < 0 || index + 1 >= split.Children.Count || delta == 0) return 0;
        var axis = split.Axis;
        var a = split.Children[index];
        var b = split.Children[index + 1];

        int maxGrow = b.SizeAlong(axis) - MinSize(b, axis);
        int maxShrink = a.SizeAlong(axis) - MinSize(a, axis);
        delta = Math.Clamp(delta, -Math.Max(0, maxShrink), Math.Max(0, maxGrow));
        if (delta == 0) return 0;

        FitAlong(a, axis, a.SizeAlong(axis) + delta);
        FitAlong(b, axis, b.SizeAlong(axis) - delta);
        RecomputeShares(split);
        Position(Root, Root.X, Root.Y);
        return delta;
    }

    /// <summary>
    /// Grows or shrinks a pane towards <paramref name="direction"/> by moving the
    /// nearest separator on that side (like tmux <c>resize-pane -L/-R/-U/-D</c>).
    /// </summary>
    public bool ResizePane(int paneId, PaneDirection direction, int cells)
    {
        var axis = direction is PaneDirection.Left or PaneDirection.Right ? SplitAxis.LeftRight : SplitAxis.TopBottom;
        bool towardsEnd = direction is PaneDirection.Right or PaneDirection.Down;

        // Walk up to the nearest split on this axis that has a separator on the wanted side.
        for (var node = Find(paneId); node?.Parent is { } parent; node = parent)
        {
            if (parent.Axis != axis) continue;
            int i = parent.Children.IndexOf(node);
            if (towardsEnd && i + 1 < parent.Children.Count)
                return MoveDivider(parent, i, cells) != 0;
            if (!towardsEnd && i > 0)
                return MoveDivider(parent, i - 1, -cells) != 0;
        }
        return false;
    }

    /// <summary>Gives every child of <paramref name="split"/> the same size (tmux "even" layouts).</summary>
    public void Equalize(LayoutNode split)
    {
        if (split.IsLeaf) return;
        foreach (var c in split.Children) c.Share = 1.0 / split.Children.Count;
        FitAlong(split, split.Axis, split.SizeAlong(split.Axis), force: true);
        Position(Root, Root.X, Root.Y);
    }

    /// <summary>
    /// Sets <paramref name="node"/>'s length along <paramref name="axis"/> and reflows
    /// its subtree: children across the axis all take the new length; children along
    /// it split the length by their shares.
    /// </summary>
    private static void FitAlong(LayoutNode node, SplitAxis axis, int size, bool force = false)
    {
        size = Math.Max(1, size);
        if (node.SizeAlong(axis) == size && !force) return;
        node.SetSize(axis, size);
        if (node.IsLeaf) return;

        if (node.Axis != axis)
        {
            foreach (var c in node.Children) FitAlong(c, axis, size);
            return;
        }

        var sizes = Distribute(size - (node.Children.Count - 1), node.Children.Select(c => c.Share).ToArray());
        for (int i = 0; i < node.Children.Count; i++)
            FitAlong(node.Children[i], axis, sizes[i]);
    }

    /// <summary>Splits <paramref name="total"/> cells by weight (largest remainder), at least 1 each.</summary>
    private static int[] Distribute(int total, double[] weights)
    {
        int n = weights.Length;
        var result = new int[n];
        double sum = weights.Sum();
        if (sum <= 0) { weights = Enumerable.Repeat(1.0, n).ToArray(); sum = n; }
        total = Math.Max(total, n);

        var remainders = new double[n];
        int used = 0;
        for (int i = 0; i < n; i++)
        {
            double exact = total * weights[i] / sum;
            result[i] = Math.Max(1, (int)Math.Floor(exact));
            remainders[i] = exact - Math.Floor(exact);
            used += result[i];
        }
        // Hand out (or claw back) the rounding difference, largest remainders first.
        foreach (var i in Enumerable.Range(0, n).OrderByDescending(i => remainders[i]))
        {
            if (used == total) break;
            if (used < total) { result[i]++; used++; }
        }
        for (int i = n - 1; used > total && i >= 0; i--)
        {
            int give = Math.Min(result[i] - 1, used - total);
            result[i] -= give;
            used -= give;
        }
        return result;
    }

    private static void RecomputeShares(LayoutNode split)
    {
        double total = split.Children.Sum(c => (double)c.SizeAlong(split.Axis));
        if (total <= 0) return;
        foreach (var c in split.Children) c.Share = c.SizeAlong(split.Axis) / total;
    }

    private static int MinAlong(SplitAxis axis) => axis == SplitAxis.LeftRight ? MinPaneWidth : MinPaneHeight;

    /// <summary>Smallest length <paramref name="node"/> can have along <paramref name="axis"/>.</summary>
    private static int MinSize(LayoutNode node, SplitAxis axis)
    {
        if (node.IsLeaf) return MinAlong(axis);
        return node.Axis == axis
            ? node.Children.Sum(c => MinSize(c, axis)) + node.Children.Count - 1
            : node.Children.Max(c => MinSize(c, axis));
    }

    /// <summary>Assigns X/Y top-down from the sizes.</summary>
    private static void Position(LayoutNode node, int x, int y)
    {
        node.X = x;
        node.Y = y;
        foreach (var c in node.Children)
        {
            Position(c, x, y);
            if (node.Axis == SplitAxis.LeftRight) x += c.Width + 1;
            else y += c.Height + 1;
        }
    }

    // -----------------------------------------------------------------------
    // Navigation
    // -----------------------------------------------------------------------

    /// <summary>
    /// The pane next to <paramref name="paneId"/> in <paramref name="direction"/>: the
    /// adjacent pane (across one separator) that overlaps it most, preferring
    /// <paramref name="preferred"/> (e.g. most recently used) among ties.
    /// </summary>
    public int? Neighbor(int paneId, PaneDirection direction, Func<int, int>? preferred = null)
    {
        var from = Find(paneId);
        if (from is null) return null;

        LayoutNode? best = null;
        int bestOverlap = 0;
        foreach (var l in Leaves())
        {
            if (l == from) continue;
            bool adjacent = direction switch
            {
                PaneDirection.Right => l.X == from.X + from.Width + 1,
                PaneDirection.Left  => l.X + l.Width + 1 == from.X,
                PaneDirection.Down  => l.Y == from.Y + from.Height + 1,
                _                   => l.Y + l.Height + 1 == from.Y,
            };
            if (!adjacent) continue;
            int overlap = direction is PaneDirection.Left or PaneDirection.Right
                ? Overlap(from.Y, from.Height, l.Y, l.Height)
                : Overlap(from.X, from.Width, l.X, l.Width);
            if (overlap <= 0) continue;
            if (best is null || (preferred is not null
                    ? preferred(l.PaneId) > preferred(best.PaneId)
                    : overlap > bestOverlap))
            {
                best = l;
                bestOverlap = overlap;
            }
        }
        return best?.PaneId;
    }

    private static int Overlap(int a, int alen, int b, int blen) =>
        Math.Min(a + alen, b + blen) - Math.Max(a, b);

    // -----------------------------------------------------------------------
    // tmux layout strings
    // -----------------------------------------------------------------------

    /// <summary>
    /// Formats the layout as tmux does (<c>#{window_layout}</c>):
    /// <c>csum,WxH,X,Y,id</c> for a pane, <c>{…}</c> / <c>[…]</c> for splits.
    /// </summary>
    public string ToTmuxString()
    {
        var body = new StringBuilder();
        Format(Root, body);
        var text = body.ToString();
        return $"{Checksum(text):x4},{text}";
    }

    private static void Format(LayoutNode n, StringBuilder sb)
    {
        sb.Append(CultureInfo.InvariantCulture, $"{n.Width}x{n.Height},{n.X},{n.Y}");
        if (n.IsLeaf)
        {
            sb.Append(CultureInfo.InvariantCulture, $",{n.PaneId}");
            return;
        }
        sb.Append(n.Axis == SplitAxis.LeftRight ? '{' : '[');
        for (int i = 0; i < n.Children.Count; i++)
        {
            if (i > 0) sb.Append(',');
            Format(n.Children[i], sb);
        }
        sb.Append(n.Axis == SplitAxis.LeftRight ? '}' : ']');
    }

    /// <summary>
    /// Parses a tmux layout string (as sent in <c>%layout-change</c>). Throws
    /// <see cref="FormatException"/> on malformed input or a checksum mismatch.
    /// </summary>
    public static PaneLayout ParseTmux(string layout)
    {
        int comma = layout.IndexOf(',');
        if (comma != 4 || !ushort.TryParse(layout.AsSpan(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var sum))
            throw new FormatException("Missing layout checksum.");
        var body = layout[(comma + 1)..];
        if (Checksum(body) != sum)
            throw new FormatException("Layout checksum mismatch.");

        int pos = 0;
        var root = ParseCell(body, ref pos, null);
        if (pos != body.Length) throw new FormatException($"Unexpected '{body[pos]}' at {pos}.");
        var result = new PaneLayout(root);
        SetSharesFromSizes(root);
        return result;
    }

    private static LayoutNode ParseCell(string s, ref int pos, LayoutNode? parent)
    {
        var node = new LayoutNode { Parent = parent };
        node.Width = ReadInt(s, ref pos); Expect(s, ref pos, 'x');
        node.Height = ReadInt(s, ref pos); Expect(s, ref pos, ',');
        node.X = ReadInt(s, ref pos); Expect(s, ref pos, ',');
        node.Y = ReadInt(s, ref pos);

        if (pos < s.Length && (s[pos] == '{' || s[pos] == '['))
        {
            char close = s[pos] == '{' ? '}' : ']';
            node.Axis = s[pos] == '{' ? SplitAxis.LeftRight : SplitAxis.TopBottom;
            pos++;
            while (true)
            {
                node.Children.Add(ParseCell(s, ref pos, node));
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                Expect(s, ref pos, close);
                break;
            }
        }
        else
        {
            Expect(s, ref pos, ',');
            node.PaneId = ReadInt(s, ref pos);
        }
        return node;
    }

    private static int ReadInt(string s, ref int pos)
    {
        int start = pos;
        while (pos < s.Length && char.IsAsciiDigit(s[pos])) pos++;
        if (pos == start) throw new FormatException($"Number expected at {start}.");
        return int.Parse(s.AsSpan(start, pos - start), CultureInfo.InvariantCulture);
    }

    private static void Expect(string s, ref int pos, char c)
    {
        if (pos >= s.Length || s[pos] != c) throw new FormatException($"'{c}' expected at {pos}.");
        pos++;
    }

    private static void SetSharesFromSizes(LayoutNode node)
    {
        if (node.IsLeaf) return;
        RecomputeShares(node);
        foreach (var c in node.Children) SetSharesFromSizes(c);
    }

    /// <summary>tmux's layout checksum (<c>layout_checksum</c> in layout-custom.c).</summary>
    public static ushort Checksum(string layout)
    {
        ushort csum = 0;
        foreach (var c in layout)
        {
            csum = (ushort)((csum >> 1) + ((csum & 1) << 15));
            csum += c;
        }
        return csum;
    }
}
