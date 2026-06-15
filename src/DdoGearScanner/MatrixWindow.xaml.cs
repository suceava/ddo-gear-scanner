using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DdoGearScanner.Model;
using DdoGearScanner.Vision;

namespace DdoGearScanner;

/// <summary>
/// The stacking "puzzle": one row per (stat, bonus type) so OVERLAP within a type is visible — the
/// slots contributing that type sit in the row, and any overridden (wasted) contribution is a struck
/// red pill while the counting ones are green. Rows are grouped into PRIORITY tiers (Strimtom A/B/C)
/// and badged. Columns are the full fixed slot set (head-to-toe). The header is frozen (its own grid)
/// and scrolls horizontally in sync with the body via a shared-size scope. Item-local weapon/armor
/// effects are listed separately. Built from <see cref="StackingAnalyzer"/>.
/// </summary>
public partial class MatrixWindow : Window
{
    private static readonly Brush CountFg = Frozen(0x8F, 0xCF, 0x8A);   // counts — natural green
    private static readonly Brush CountBg = Frozen(0x18, 0x2C, 0x1A);
    private static readonly Brush OverFg = Frozen(0xDA, 0x6E, 0x5E);    // overridden — muted red
    private static readonly Brush OverBg = Frozen(0x35, 0x20, 0x1C);
    private static readonly Brush TotalFg = Frozen(0xE6, 0xC6, 0x6A);   // gold
    private static readonly Brush TotalBg = Frozen(0x2E, 0x26, 0x16);
    private static readonly Brush HeaderFg = Frozen(0x9C, 0x8E, 0x70);  // muted parchment
    private static readonly Brush HeaderStrong = Frozen(0xCC, 0xC2, 0xA6);
    private static readonly Brush Amber = Frozen(0xE6, 0xC6, 0x6A);     // gold accent
    private static readonly Brush Faint = Frozen(0x3A, 0x30, 0x1E);
    private static readonly Brush RowBg = Frozen(0x1E, 0x18, 0x10);     // warm zebra
    private static readonly Brush ChipBg = Frozen(0x2A, 0x22, 0x14);
    private static readonly Brush Section = Frozen(0xC9, 0xA2, 0x4B);   // gold section heads
    private static readonly Brush TierGold = Frozen(0xE6, 0xC6, 0x6A);
    private static readonly Brush TierSilver = Frozen(0xC6, 0xCA, 0xD2);
    private static readonly Brush TierBronze = Frozen(0xC1, 0x83, 0x49);

    private const double StatColWidth = 260;

    private StackingMatrix _matrix;

    public MatrixWindow(StackingMatrix matrix)
    {
        InitializeComponent();
        WindowChrome.UseDarkTitleBar(this);
        AppSettings s = AppSettings.Instance;
        WindowChrome.ApplyBounds(this, s.MatrixLeft, s.MatrixTop, s.MatrixWidth, s.MatrixHeight, s.MatrixMaximized);
        WindowChrome.PersistBounds(this, (l, t, w, h, m) =>
        {
            s.MatrixLeft = l; s.MatrixTop = t; s.MatrixWidth = w; s.MatrixHeight = h; s.MatrixMaximized = m;
        });
        _matrix = matrix;
        Build();
    }

    /// <summary>Refresh with a freshly-analyzed loadout (used when the matrix is reopened/reused).</summary>
    public void Update(StackingMatrix matrix)
    {
        _matrix = matrix;
        Build();
    }

    private void ConflictsOnly_Changed(object sender, RoutedEventArgs e) => Build();

    private void BodyScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        => HeaderScroll?.ScrollToHorizontalOffset(e.HorizontalOffset);

    private void Build()
    {
        Clear(HeaderGrid);
        Clear(MatrixGrid);

        bool conflictsOnly = ConflictsOnly.IsChecked == true;
        var rows = (conflictsOnly ? _matrix.Rows.Where(r => r.HasOverride) : _matrix.Rows).ToList();

        int conflicts = _matrix.Rows.Count(r => r.HasOverride);
        int wasted = _matrix.Rows.SelectMany(r => r.Cells).Count(c => c.Overridden);
        Summary.Text = $"{_matrix.Slots.Count}/{SlotInfo.DisplayOrder.Length} slots filled · {_matrix.Rows.Count} stat/type rows · " +
                       $"{conflicts} with overlap · {wasted} wasted mod{(wasted == 1 ? "" : "s")}";

        // Fixed columns: every slot, head-to-toe (empty slots become visible gaps).
        var slots = SlotInfo.DisplayOrder.ToList();
        int cols = slots.Count + 1;
        AddColumns(HeaderGrid, slots.Count);
        AddColumns(MatrixGrid, slots.Count);

        // ---- frozen header ----
        int hr = AddRow(HeaderGrid);
        PlaceHeader(HeaderGrid, "Stat", hr, 0, left: true);
        for (int i = 0; i < slots.Count; i++) PlaceHeader(HeaderGrid, SlotInfo.Label(slots[i]), hr, i + 1);
        int hl = AddRow(HeaderGrid);
        HeaderGrid.RowDefinitions[hl].Height = new GridLength(2);
        Span(HeaderGrid, new Border { Background = Section }, hl, 0, cols);

        // ---- body ----
        if (rows.Count == 0)
        {
            int er = AddRow(MatrixGrid);
            PlaceText(MatrixGrid, conflictsOnly ? "No overlaps — every bonus counts." : "No character-wide stats captured yet.",
                HeaderFg, er, 0, left: true);
        }

        char? lastTier = null;
        bool firstRow = true;
        int zebra = 0;
        foreach (MatrixRow row in rows)
        {
            if (firstRow || lastTier != row.Priority)
            {
                firstRow = false;
                lastTier = row.Priority;
                int sr = AddRow(MatrixGrid);
                PlaceText(MatrixGrid, TierLabel(row.Priority), TierColor(row.Priority), sr, 0, bold: true, left: true, size: 11, topPad: 14);
                zebra = 0;
            }

            int r = AddRow(MatrixGrid);
            if (zebra++ % 2 == 1) Span(MatrixGrid, new Border { Background = RowBg }, r, 0, cols);
            if (row.HasOverride)
                Span(MatrixGrid, new Border { Background = Amber, Width = 3, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) }, r, 0, 1);

            var label = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 5, 6, 5), VerticalAlignment = VerticalAlignment.Center };
            if (row.Priority is char rank)
                label.Children.Add(new Border
                {
                    Background = TierColor(rank), CornerRadius = new CornerRadius(4), Width = 16, Height = 16,
                    Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = rank.ToString(), Foreground = Frozen(0x14, 0x16, 0x1B), FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
            label.Children.Add(new TextBlock
            {
                Text = row.Stat,
                Foreground = row.HasOverride ? Amber : HeaderStrong,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            label.Children.Add(new Border
            {
                Background = ChipBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 1),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = row.BonusType, Foreground = HeaderFg, FontSize = 10.5 },
            });
            // Show a total only when it genuinely adds up (a self-stacking type with 2+ live sources).
            if (row.Cells.Count(c => c.Counts) > 1)
                label.Children.Add(new Border
                {
                    Background = TotalBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 1),
                    Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "= " + Fmt(row.Effective, row.IsPercent), Foreground = TotalFg, FontSize = 11, FontWeight = FontWeights.Bold },
                });
            Place(MatrixGrid, label, r, 0);

            for (int s = 0; s < slots.Count; s++)
            {
                EquipSlot slot = slots[s];
                var cells = row.Cells.Where(c => c.Slot == slot).ToList();
                if (cells.Count == 0)
                {
                    PlaceText(MatrixGrid, "·", Faint, r, s + 1);
                    continue;
                }
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 4, 6, 4) };
                foreach (MatrixCell c in cells)
                {
                    Border pill = Pill(Fmt(c.Value, c.IsPercent), c.Counts ? CountFg : OverFg, c.Counts ? CountBg : OverBg, strike: c.Overridden);
                    pill.ToolTip = c.Counts ? row.BonusType : $"overridden — a higher {row.BonusType} bonus wins";
                    pill.Margin = new Thickness(0, 1, 0, 1);
                    sp.Children.Add(pill);
                }
                Place(MatrixGrid, sp, r, s + 1);
            }
        }

        BuildItemLocal(conflictsOnly);
    }

    private void BuildItemLocal(bool conflictsOnly)
    {
        NamedList.Children.Clear();
        if (conflictsOnly || _matrix.ItemLocal.Count == 0) { NamedHeader.Visibility = Visibility.Collapsed; return; }

        NamedHeader.Visibility = Visibility.Visible;
        foreach (var slotGrp in _matrix.ItemLocal.GroupBy(e => e.Slot))
        {
            NamedList.Children.Add(new TextBlock
            {
                Text = SlotInfo.Label(slotGrp.Key),
                Foreground = Amber, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 10, 0, 3),
            });
            foreach (ItemLocalEffect e in slotGrp)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 1, 0, 1) };
                if (e.Value != 0)
                    line.Children.Add(new Border
                    {
                        Background = ChipBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0, 7, 0),
                        Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = Fmt(e.Value, e.IsPercent), Foreground = CountFg, FontSize = 11 },
                    });
                line.Children.Add(new TextBlock { Text = e.Stat, VerticalAlignment = VerticalAlignment.Center, ToolTip = e.Description });
                NamedList.Children.Add(line);
            }
        }
    }

    private static void AddColumns(Grid g, int slotCount)
    {
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StatColWidth) });
        for (int i = 0; i < slotCount; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = $"mcol{i}" });
    }

    private static Border Pill(string text, Brush fg, Brush bg, bool bold = false, bool strike = false) => new()
    {
        Background = bg,
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(9, 2, 9, 2),
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold,
            TextDecorations = strike ? TextDecorations.Strikethrough : null,
            HorizontalAlignment = HorizontalAlignment.Center,
        },
    };

    private static string TierLabel(char? rank) => rank switch
    {
        'A' => "PRIORITY A  ·  core",
        'B' => "PRIORITY B  ·  strong",
        'C' => "PRIORITY C  ·  situational",
        _ => "UNRANKED  ·  not in the priority list",
    };

    private static Brush TierColor(char? rank) => rank switch
    {
        'A' => TierGold,
        'B' => TierSilver,
        'C' => TierBronze,
        _ => HeaderFg,
    };

    private static string Fmt(double v, bool pct)
        => (v > 0 ? "+" : "") + v.ToString("0.##", CultureInfo.InvariantCulture) + (pct ? "%" : "");

    private static void Clear(Grid g)
    {
        g.Children.Clear();
        g.ColumnDefinitions.Clear();
        g.RowDefinitions.Clear();
    }

    private static int AddRow(Grid g)
    {
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return g.RowDefinitions.Count - 1;
    }

    private static void PlaceHeader(Grid g, string text, int row, int col, bool left = false)
        => Place(g, new TextBlock
        {
            Text = text,
            Foreground = HeaderStrong,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(left ? 10 : 8, 9, 8, 9),
        }, row, col);

    private static void PlaceText(Grid g, string text, Brush fg, int row, int col, bool bold = false, bool left = false, double size = 12.5, double topPad = 0)
        => Place(g, new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontSize = size,
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(left ? 4 : 8, 4 + topPad, 8, 6),
        }, row, col);

    private static void Place(Grid g, UIElement el, int row, int col)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        g.Children.Add(el);
    }

    private static void Span(Grid g, UIElement el, int row, int col, int span)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        Grid.SetColumnSpan(el, span);
        Grid.SetZIndex(el, -1);
        g.Children.Add(el);
    }

    private static SolidColorBrush Frozen(byte r, byte gr, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, gr, b));
        br.Freeze();
        return br;
    }
}
