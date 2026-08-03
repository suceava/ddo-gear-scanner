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
    private static readonly Brush HeaderFg = Frozen(0x9C, 0x8E, 0x70);  // muted parchment
    private static readonly Brush HeaderStrong = Frozen(0xCC, 0xC2, 0xA6);
    private static readonly Brush Amber = Frozen(0xE6, 0xC6, 0x6A);     // gold accent
    private static readonly Brush Faint = Frozen(0x3A, 0x30, 0x1E);
    private static readonly Brush ChipBg = Frozen(0x2A, 0x22, 0x14);
    private static readonly Brush Section = Frozen(0xC9, 0xA2, 0x4B);   // gold section heads
    private static readonly Brush TierGold = Frozen(0xE6, 0xC6, 0x6A);
    private static readonly Brush TierSilver = Frozen(0xC6, 0xCA, 0xD2);
    private static readonly Brush TierBronze = Frozen(0xC1, 0x83, 0x49);

    private const double StatColWidth = 210;
    private const int SlotCol0 = 3;   // columns: 0 Stat | 1 Type | 2 Eff | 3.. slots

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

        // Columns mirror the web matrix: Stat | Type | Eff | one per slot (head-to-toe, empty = gap).
        var slots = SlotInfo.DisplayOrder.ToList();
        int cols = slots.Count + SlotCol0;
        AddColumns(HeaderGrid, slots.Count);
        AddColumns(MatrixGrid, slots.Count);

        // ---- frozen header ----
        int hr = AddRow(HeaderGrid);
        PlaceHeader(HeaderGrid, "Stat", hr, 0, left: true);
        PlaceHeader(HeaderGrid, "Type", hr, 1, left: true);
        PlaceHeader(HeaderGrid, "Eff", hr, 2, right: true);
        for (int i = 0; i < slots.Count; i++) PlaceHeader(HeaderGrid, SlotInfo.Label(slots[i]), hr, i + SlotCol0);
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

        // Priority band → stat (shown once, spanning its bonus-type rows, like the web matrix) → one row
        // per bonus type. Overrides are shown by the red "(N)" cell pill, not a per-row marker.
        foreach (var tier in rows.GroupBy(r => r.Priority))
        {
            // ---- prominent priority band (spans all columns) ----
            int br = AddRow(MatrixGrid);
            Span(MatrixGrid, new Border { Background = TierBandBg(tier.Key) }, br, 0, cols);
            var band = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 8, 7), VerticalAlignment = VerticalAlignment.Center };
            band.Children.Add(new Border { Background = TierColor(tier.Key), Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 9, 1) });
            band.Children.Add(new TextBlock { Text = TierLabel(tier.Key), Foreground = TierColor(tier.Key), FontWeight = FontWeights.Bold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            band.Children.Add(new TextBlock { Text = $"  ({tier.Count()})", Foreground = HeaderFg, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            Span(MatrixGrid, band, br, 0, cols);

            foreach (var statGrp in tier.GroupBy(r => r.Stat))
            {
                var typeRows = statGrp.ToList();
                int first = MatrixGrid.RowDefinitions.Count;   // this group's first body row

                for (int j = 0; j < typeRows.Count; j++)
                {
                    MatrixRow tr = typeRows[j];
                    int r = AddRow(MatrixGrid);
                    // Thin separator above each stat group (mirrors the web row border between groups).
                    if (j == 0)
                        Span(MatrixGrid, new Border { BorderBrush = Faint, BorderThickness = new Thickness(0, 1, 0, 0) }, r, 0, cols);
                    PlaceText(MatrixGrid, tr.BonusType, HeaderFg, r, 1, left: true, size: 11.5);
                    PlaceText(MatrixGrid, Fmt(tr.Effective, tr.IsPercent), Amber, r, 2, right: true);
                    PlaceCells(tr, slots, r);
                }

                // ---- stat name: once, vertically centered, spanning its bonus-type rows ----
                var statTb = new TextBlock
                {
                    Text = statGrp.Key,
                    Foreground = HeaderStrong,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(10, 5, 6, 5),
                    ToolTip = statGrp.Key,
                };
                Grid.SetRowSpan(statTb, typeRows.Count);
                Place(MatrixGrid, statTb, first, 0);
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
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StatColWidth) });               // 0 Stat
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "mtype" }); // 1 Type
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "meff" });  // 2 Eff
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

    /// <summary>Render one stat/type row's per-slot pills across the fixed slot columns. Counting cells
    /// show "+N" green; overridden cells show "(N)" muted red (no strike) — matching the web matrix.</summary>
    private void PlaceCells(MatrixRow row, List<EquipSlot> slots, int r)
    {
        for (int s = 0; s < slots.Count; s++)
        {
            EquipSlot slot = slots[s];
            var cells = row.Cells.Where(c => c.Slot == slot).ToList();
            if (cells.Count == 0) { PlaceText(MatrixGrid, "·", Faint, r, s + SlotCol0); continue; }
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 4, 6, 4) };
            foreach (MatrixCell c in cells)
            {
                string txt = c.Counts ? Fmt(c.Value, c.IsPercent) : $"({Fmt(c.Value, c.IsPercent).TrimStart('+')})";
                Border pill = Pill(txt, c.Counts ? CountFg : OverFg, c.Counts ? CountBg : OverBg);
                pill.ToolTip = c.Counts ? row.BonusType : $"overridden — a higher {row.BonusType} bonus wins";
                pill.Margin = new Thickness(0, 1, 0, 1);
                sp.Children.Add(pill);
            }
            Place(MatrixGrid, sp, r, s + SlotCol0);
        }
    }

    private static string TierLabel(char? rank) => rank switch
    {
        'A' => "CORE (A)",
        'B' => "STRONG (B)",
        'C' => "SITUATIONAL (C)",
        _ => "UNRANKED",
    };

    private static Brush TierColor(char? rank) => rank switch
    {
        'A' => TierGold,
        'B' => TierSilver,
        'C' => TierBronze,
        _ => HeaderFg,
    };

    // Tinted full-width band behind each priority header — warm gold / cool silver / bronze.
    private static Brush TierBandBg(char? rank) => rank switch
    {
        'A' => Frozen(0x32, 0x28, 0x12),
        'B' => Frozen(0x24, 0x27, 0x2C),
        'C' => Frozen(0x30, 0x22, 0x14),
        _ => Frozen(0x20, 0x1B, 0x12),
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

    private static void PlaceHeader(Grid g, string text, int row, int col, bool left = false, bool right = false)
        => Place(g, new TextBlock
        {
            Text = text,
            Foreground = HeaderStrong,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            HorizontalAlignment = left ? HorizontalAlignment.Left : right ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(left ? 10 : 8, 9, 8, 9),
        }, row, col);

    private static void PlaceText(Grid g, string text, Brush fg, int row, int col, bool bold = false, bool left = false, double size = 12.5, double topPad = 0, bool right = false)
        => Place(g, new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontSize = size,
            HorizontalAlignment = left ? HorizontalAlignment.Left : right ? HorizontalAlignment.Right : HorizontalAlignment.Center,
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
