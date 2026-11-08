using System.Windows.Forms.VisualStyles;

namespace QuickCamp;

public sealed class AlbumGrid : Control
{
    public event EventHandler? CheckedChanged;
    public event EventHandler? SelectAllClicked;

    private readonly HashSet<CollectionItem> _checked;
    private readonly Func<CollectionItem, string> _statusOf;
    private readonly string _artDir;
    private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right };

    private IReadOnlyList<CollectionItem> _items = Array.Empty<CollectionItem>();
    private ArtCache? _art;
    private int _hover = -1;
    private Font? _boldFont, _noteFont;

    private Font BoldFont => _boldFont ??= new Font(Font, FontStyle.Bold);
    private Font NoteFont => _noteFont ??= new Font(Font.FontFamily, Font.Size * 3);

    public AlbumGrid(HashSet<CollectionItem> checkedItems, Func<CollectionItem, string> statusOf, string artDir)
    {
        _checked = checkedItems;
        _statusOf = statusOf;
        _artDir = artDir;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = SystemColors.Window;
        TabStop = true;

        Controls.Add(_scroll);
        _scroll.ValueChanged += (_, _) => Invalidate();
    }

    public void SetItems(IReadOnlyList<CollectionItem> items)
    {
        _items = items;
        _hover = -1;
        UpdateScrollBar();
        Invalidate();
    }
    private int Tile => LogicalToDeviceUnits(150);
    private int Gap => LogicalToDeviceUnits(14);
    private int HeaderHeight => LogicalToDeviceUnits(32);
    private int TextHeight => Font.Height * 3 + LogicalToDeviceUnits(6);
    private int CellWidth => Tile + Gap;
    private int CellHeight => Tile + TextHeight + Gap;
    private int UsableWidth => Math.Max(0, ClientSize.Width - _scroll.Width - Gap);
    private int Columns => Math.Max(1, UsableWidth / CellWidth);
    private int LeftPad => Gap + Math.Max(0, (UsableWidth - Columns * CellWidth) / 2);
    private int ViewHeight => Math.Max(1, ClientSize.Height - HeaderHeight);

    private Rectangle TileRect(int index)
    {
        int row = index / Columns, col = index % Columns;
        int x = LeftPad + col * CellWidth;
        int y = HeaderHeight + Gap + row * CellHeight - _scroll.Value;
        return new Rectangle(x, y, Tile, Tile + TextHeight);
    }

    private int HitTest(Point p)
    {
        if (p.Y < HeaderHeight) return -1;
        for (int i = FirstVisible(); i <= LastVisible(); i++)
            if (TileRect(i).Contains(p)) return i;
        return -1;
    }

    private int FirstVisible() => Math.Min(_items.Count, Math.Max(0, (_scroll.Value - Gap) / CellHeight) * Columns);

    private int LastVisible() => Math.Min(_items.Count - 1, ((_scroll.Value + ViewHeight) / CellHeight + 1) * Columns - 1);

    private void UpdateScrollBar()
    {
        int rows = (_items.Count + Columns - 1) / Columns;
        int content = rows * CellHeight + Gap;

        _scroll.Minimum = 0;
        _scroll.Maximum = Math.Max(0, content - 1);
        _scroll.LargeChange = ViewHeight;
        _scroll.SmallChange = Math.Max(1, CellHeight / 3);
        _scroll.Enabled = content > ViewHeight;

        int maxValue = Math.Max(0, content - ViewHeight);
        if (_scroll.Value > maxValue) _scroll.Value = maxValue;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollBar();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _boldFont?.Dispose(); _boldFont = null;
        _noteFont?.Dispose(); _noteFont = null;
        UpdateScrollBar();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        _art?.Dispose();
        _art = null;
        UpdateScrollBar();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (_art is null)
        {
            _art = new ArtCache(_artDir, Tile);
            _art.Loaded += Invalidate;
        }

        int first = FirstVisible(), last = LastVisible();
        _art.SetVisible(Enumerable.Range(first, Math.Max(0, last - first + 1))
            .Select(i => _items[i].ArtUrl).OfType<string>());

        for (int i = first; i <= last; i++)
            DrawTile(g, i);

        DrawHeader(g);
    }

    private void DrawTile(Graphics g, int index)
    {
        var item = _items[index];
        var r = TileRect(index);
        var artRect = new Rectangle(r.X, r.Y, Tile, Tile);
        bool isChecked = _checked.Contains(item);

        var img = _art!.Get(item.ArtUrl);
        if (img is not null)
        {
            g.DrawImage(img, artRect);
        }
        else
        {
            using var bg = new SolidBrush(SystemColors.ControlLight);
            g.FillRectangle(bg, artRect);
            TextRenderer.DrawText(g, "♪", NoteFont, artRect, SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        if (item.DownloadPage is null)
        {
            using var wash = new SolidBrush(Color.FromArgb(150, SystemColors.Window));
            g.FillRectangle(wash, artRect);
        }

        if (isChecked)
        {
            using var pen = new Pen(SystemColors.Highlight, LogicalToDeviceUnits(3));
            g.DrawRectangle(pen, Rectangle.Inflate(artRect, -1, -1));
        }
        else if (index == _hover)
        {
            using var pen = new Pen(SystemColors.ControlDark, LogicalToDeviceUnits(1));
            g.DrawRectangle(pen, artRect.X, artRect.Y, artRect.Width - 1, artRect.Height - 1);
        }

        var boxSize = CheckBoxRenderer.GetGlyphSize(g, CheckBoxState.UncheckedNormal);
        var boxPt = new Point(artRect.X + LogicalToDeviceUnits(6), artRect.Y + LogicalToDeviceUnits(6));
        using (var backing = new SolidBrush(Color.FromArgb(220, Color.White)))
            g.FillRectangle(backing, boxPt.X - 2, boxPt.Y - 2, boxSize.Width + 4, boxSize.Height + 4);
        CheckBoxRenderer.DrawCheckBox(g, boxPt, isChecked ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal);

        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        int y = artRect.Bottom + LogicalToDeviceUnits(4);
        TextRenderer.DrawText(g, item.Artist, BoldFont, new Rectangle(r.X, y, Tile, Font.Height), ForeColor, flags);
        TextRenderer.DrawText(g, item.Title, Font, new Rectangle(r.X, y + Font.Height, Tile, Font.Height), ForeColor, flags);
        TextRenderer.DrawText(g, _statusOf(item), Font, new Rectangle(r.X, y + Font.Height * 2, Tile, Font.Height), SystemColors.GrayText, flags);
    }

    private void DrawHeader(Graphics g)
    {
        var header = new Rectangle(0, 0, ClientSize.Width - _scroll.Width, HeaderHeight);
        using (var bg = new SolidBrush(SystemColors.Control))
            g.FillRectangle(bg, header);
        g.DrawLine(SystemPens.ControlDark, 0, header.Bottom - 1, header.Right, header.Bottom - 1);

        bool all = _items.Count > 0 && _items.All(_checked.Contains);
        bool some = !all && _items.Any(_checked.Contains);
        var state = all ? CheckBoxState.CheckedNormal : some ? CheckBoxState.MixedNormal : CheckBoxState.UncheckedNormal;

        var boxSize = CheckBoxRenderer.GetGlyphSize(g, state);
        var boxPt = new Point(LeftPad, (HeaderHeight - boxSize.Height) / 2);
        CheckBoxRenderer.DrawCheckBox(g, boxPt, state);

        var textRect = new Rectangle(boxPt.X + boxSize.Width + LogicalToDeviceUnits(6), 0, header.Width, HeaderHeight);
        TextRenderer.DrawText(g, $"Select all shown ({_items.Count})", Font, textRect, ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;

        if (e.Y < HeaderHeight)
        {
            SelectAllClicked?.Invoke(this, EventArgs.Empty);
            return;
        }

        int hit = HitTest(e.Location);
        if (hit < 0) return;

        var item = _items[hit];
        if (!_checked.Remove(item)) _checked.Add(item);
        Invalidate();
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        if (hit == _hover) return;
        _hover = hit;
        Cursor = hit >= 0 || e.Y < HeaderHeight ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_scroll.Enabled) return;
        int maxValue = Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1);
        int step = CellHeight / 2 * Math.Sign(e.Delta);
        _scroll.Value = Math.Clamp(_scroll.Value - step, 0, maxValue);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _art?.Dispose();
            _boldFont?.Dispose();
            _noteFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
