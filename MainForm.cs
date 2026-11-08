using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Quickcamp;

public sealed class MainForm : Form
{
    private const int ArtistColumn = 1, TitleColumn = 2, TypeColumn = 3, StatusColumn = 4, DetailsColumn = 5;
    private const double BytesPerMB = 1024 * 1024;

    private enum Result { Done, Skipped, Failed, Cancelled }
    private sealed record DownloadJob(string Format, string OutDir, bool Unzip, LibraryScan Scan);

    private readonly BandcampClient _bc = new();
    private static readonly string ExeDir = AppContext.BaseDirectory;
    private static readonly string IniFile = Path.Combine(ExeDir, "Quickcamp.ini");
    private static readonly string DataDir = Path.Combine(ExeDir, "data");
    private string? _savedIdentity;

    private List<CollectionItem> _all = new();
    private List<CollectionItem> _visible = new();
    private readonly HashSet<CollectionItem> _checked = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<CollectionItem> _possibleDupes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<CollectionItem> _extraDupes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CollectionItem, string> _status = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CollectionItem, ListViewItem> _rows = new(ReferenceEqualityComparer.Instance);

    private bool _rebuilding;
    private int _sortColumn = ArtistColumn;
    private bool _sortDescending;
    private bool _gridMode;
    private bool _loggedIn;
    private CancellationTokenSource? _cts;

    private readonly TextBox _search = new() { Width = 260, PlaceholderText = "Type to filter by artist or title" };
    private readonly ComboBox _format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly TextBox _outDir = new() { Width = 300 };
    private readonly Button _browse = new() { Text = "Browse...", AutoSize = true };
    private readonly CheckBox _unzip = new() { Text = "Unzip albums", AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
    private readonly CheckBox _hideDownloaded = new() { Text = "Hide downloaded", AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
    private readonly ComboBox _parallel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 75 };
    private readonly CheckBox _hideDupes = new() { Text = "Hide duplicates", AutoSize = true, Checked = true, Margin = new Padding(8, 6, 3, 3) };
    private readonly CheckBox _showDetails = new() { Text = "Purchase details", AutoSize = true, Margin = new Padding(8, 6, 3, 3) };
    private readonly Button _viewToggle = new() { Text = "Grid view", AutoSize = true };

    private readonly CheckListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
    };
    private readonly AlbumGrid _grid;
    private readonly Button _bigLogin = new()
    {
        Text = "Log in to Bandcamp",
        AutoSize = true,
        Padding = new Padding(30, 14, 30, 14),
        Anchor = AnchorStyles.None,
    };
    private readonly TableLayoutPanel _loginPanel = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Visible = false };

    private readonly Button _download = new() { Text = "Download checked", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Button _reload = new() { Text = "Reload collection", AutoSize = true, Visible = false };
    private readonly Button _logout = new() { Text = "Log out", AutoSize = true, Enabled = false };

    private readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _countText = new();
    private readonly ToolStripProgressBar _progress = new() { Width = 200 };

    private readonly System.Windows.Forms.Timer _filterTimer = new() { Interval = 150 };

    public MainForm()
    {
        Text = "Quickcamp";
        Icon = Assets.AppIcon;
        StartPosition = FormStartPosition.Manual;
        SizeToScreen();
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;

        _grid = new AlbumGrid(_checked, GridStatus, Path.Combine(DataDir, "artcache")) { Dock = DockStyle.Fill, Visible = false };

        _format.Items.AddRange(Formats.All);

        for (int i = 1; i <= 10; i++) _parallel.Items.Add(i);
        _parallel.SelectedItem = 3;
        _list.Columns.Add("", LogicalToDeviceUnits(26));
        _list.Columns.Add("Artist", LogicalToDeviceUnits(260));
        _list.Columns.Add("Title", LogicalToDeviceUnits(400));
        _list.Columns.Add("Type", LogicalToDeviceUnits(80));
        _list.Columns.Add("Status", LogicalToDeviceUnits(220));

        var row1 = MakeRow(_logout, MakeLabel("Save to:"), _outDir, _browse, MakeLabel("Format:"), _format, _unzip,
                           MakeLabel("Simultaneous Downloads:"), _parallel);
        var row2 = MakeRow(MakeLabel("Search:"), _search, _hideDownloaded, _hideDupes, _showDetails, _viewToggle);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2, Padding = new Padding(6, 4, 6, 0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.Controls.Add(row1, 0, 0);
        top.Controls.Add(row2, 0, 1);
        var logo = new LogoBox { Dock = DockStyle.Fill, Margin = new Padding(12, 2, 4, 4) };
        top.Controls.Add(logo, 1, 0);
        top.SetRowSpan(logo, 2);

        Load += (_, _) => FitMinimumSizeTo(top, row1, row2, logo);
        DpiChanged += (_, _) => BeginInvoke(() => FitMinimumSizeTo(top, row1, row2, logo));

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        bottom.Controls.AddRange(new Control[] { _download, _cancel, _reload });

        var strip = new StatusStrip();
        strip.Items.AddRange(new ToolStripItem[] { _statusText, _countText, _progress });

        var center = new Panel { Dock = DockStyle.Fill };
        center.Controls.Add(_list);
        center.Controls.Add(_grid);
        _bigLogin.Font = new Font(Font.FontFamily, Font.Size * 1.8f, FontStyle.Bold);
        _loginPanel.Controls.Add(_bigLogin, 0, 0);
        center.Controls.Add(_loginPanel);

        Controls.Add(center);
        Controls.Add(top);
        Controls.Add(bottom);
        Controls.Add(strip);

        Task.Run(() => { try { Directory.Delete(Path.Combine(DataDir, "WebView2"), recursive: true); } catch { } });

        LoadSettings();
        ApplyDetailsColumn();
        ApplyView();
        WireEvents();
    }

    private static FlowLayoutPanel MakeRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
        row.Controls.AddRange(controls);
        return row;
    }

    private void SizeToScreen()
    {
        var screen = Screen.PrimaryScreen!.WorkingArea;
        int width = Math.Max(1600, LogicalToDeviceUnits(1070));
        int height = Math.Max(1050, LogicalToDeviceUnits(700));
        Size = new Size(Math.Min(width, screen.Width - 40), Math.Min(height, screen.Height - 40));
        Location = new Point(screen.Left + (screen.Width - Width) / 2, screen.Top + (screen.Height - Height) / 2);
    }
    private void FitMinimumSizeTo(TableLayoutPanel top, FlowLayoutPanel row1, FlowLayoutPanel row2, Control logo)
    {
        int rowsWidth = Math.Max(row1.PreferredSize.Width, row2.PreferredSize.Width);
        int rowsHeight = row1.PreferredSize.Height + row1.Margin.Vertical + row2.PreferredSize.Height + row2.Margin.Vertical;
        int logoHeight = Math.Max(0, rowsHeight - logo.Margin.Vertical);
        int logoWidth = (int)Math.Ceiling(logoHeight * (double)Assets.Logo.Width / Assets.Logo.Height);
        int clientWidth = rowsWidth + logoWidth + logo.Margin.Horizontal + top.Padding.Horizontal + top.Margin.Horizontal;
        int borderWidth = Width - ClientSize.Width;

        MinimumSize = new Size(clientWidth + borderWidth, LogicalToDeviceUnits(450));
    }

    private static Label MakeLabel(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(8, 7, 0, 0) };

    private void WireEvents()
    {
        _search.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFilter(); };
        _hideDownloaded.CheckedChanged += (_, _) => ApplyFilter();
        _hideDupes.CheckedChanged += (_, _) => ApplyFilter();
        _showDetails.CheckedChanged += (_, _) =>
        {
            ApplyDetailsColumn();
            ApplyFilter();
        };

        _list.ItemChecked += (_, e) =>
        {
            if (_rebuilding || e.Item.Tag is not CollectionItem item) return;
            if (e.Item.Checked) _checked.Add(item); else _checked.Remove(item);
            UpdateCount();
        };
        _list.HeaderCheckBoxClicked += (_, _) => ToggleAllShown();
        _list.ColumnClick += (_, e) =>
        {
            if (e.Column == 0) return;
            _sortDescending = e.Column == _sortColumn && !_sortDescending;
            _sortColumn = e.Column;
            ApplyFilter();
        };
        _list.ColumnWidthChanging += (_, e) =>
        {
            if (e.ColumnIndex == 0) { e.Cancel = true; e.NewWidth = _list.Columns[0].Width; }
        };

        _grid.CheckedChanged += (_, _) => UpdateCount();
        _grid.SelectAllClicked += (_, _) => ToggleAllShown();

        _viewToggle.Click += (_, _) =>
        {
            _gridMode = !_gridMode;
            ApplyView();
            ApplyFilter();
        };

        _browse.Click += async (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _outDir.Text, UseDescriptionForTitle = true, Description = "Save downloads to" };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _outDir.Text = dlg.SelectedPath;
                await RefreshOnDiskStatusAsync();
            }
        };
        _outDir.Leave += async (_, _) => await RefreshOnDiskStatusAsync();

        _download.Click += async (_, _) => await DownloadCheckedAsync();
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            SetStatus("Stopping... (partial downloads are kept and will resume next time)");
            _cts?.Cancel();
        };
        _reload.Click += async (_, _) => await LoginAndLoadAsync(allowLoginWindow: true);
        _bigLogin.Click += async (_, _) => await LoginAndLoadAsync(allowLoginWindow: true);
        _logout.Click += (_, _) => LogOut();

        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && _search.Focused) { _search.Clear(); e.Handled = true; }
        };

        Shown += async (_, _) => await LoginAndLoadAsync(allowLoginWindow: false);
        FormClosing += (_, _) => { _cts?.Cancel(); SaveSettings(); };
    }
    private async Task LoginAndLoadAsync(bool allowLoginWindow)
    {
        SetBusy(true, canCancel: false);
        try
        {
            Fan? fan = null;

            if (!string.IsNullOrEmpty(_savedIdentity))
            {
                SetStatus("Checking saved login...");
                fan = await TryIdentityAsync(_savedIdentity);
            }

            if (fan is null && allowLoginWindow)
                fan = await LoginThroughBrowserAsync();

            if (fan is null)
            {
                SetLoggedIn(false);
                if (!allowLoginWindow)
                    SetStatus(string.IsNullOrEmpty(_savedIdentity) ? "Not logged in." : "Your saved login has expired. Log in again.");
                return;
            }

            SetLoggedIn(true);
            Text = $"Quickcamp - {fan.Username}";

            SetStatus("Reading collection...");
            _all = await _bc.GetCollectionAsync(fan.Id, n => SetStatus($"Reading collection... {n} items"));
            _checked.Clear();
            FindPossibleDuplicates();
            await RefreshOnDiskStatusAsync();  
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't load collection.");
            MessageBox.Show(this, ex.Message, "Quickcamp", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }
    private async Task<Fan?> TryIdentityAsync(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return null;
        _bc.SetIdentity(identity.Trim());
        var fan = await _bc.GetFanAsync();
        if (fan is not null)
        {
            _savedIdentity = identity.Trim();
            SaveSettings();
        }
        return fan;
    }
    private async Task<Fan?> LoginThroughBrowserAsync()
    {
        SetStatus("Log in to Bandcamp in the login window...");
        Fan? fan = null;
        using var login = new LoginWindow(Path.Combine(DataDir, "WebView2"),
            async id => (fan = await TryIdentityAsync(id)) is not null);
        login.ShowDialog(this);

        if (fan is null) SetStatus("Login cancelled. Click Log in to try again.");
        return fan;
    }

    private void LogOut()
    {
        _savedIdentity = null;
        SaveSettings();

        _all = new List<CollectionItem>();
        _checked.Clear();
        _possibleDupes.Clear();
        _extraDupes.Clear();
        _status.Clear();
        ApplyFilter();

        SetLoggedIn(false);
        SetStatus("Logged out.");
    }

    private void SetLoggedIn(bool loggedIn)
    {
        _loggedIn = loggedIn;
        _logout.Enabled = loggedIn;
        _reload.Visible = loggedIn;
        if (!loggedIn) Text = "Quickcamp";
        ApplyView();
    }
    private void ApplyDetailsColumn()
    {
        bool hasColumn = _list.Columns.Count > DetailsColumn;
        if (_showDetails.Checked && !hasColumn)
        {
            _list.Columns.Add("Purchase details", LogicalToDeviceUnits(320));
        }
        else if (!_showDetails.Checked && hasColumn)
        {
            _list.Columns.RemoveAt(DetailsColumn);
            if (_sortColumn == DetailsColumn) _sortColumn = ArtistColumn;
        }
        _list.ApplyHeaderCheckBox(); 
        }

    private void ApplyView()
    {
        _loginPanel.Visible = !_loggedIn;
        _list.Visible = _loggedIn && !_gridMode;
        _grid.Visible = _loggedIn && _gridMode;
        _viewToggle.Text = _gridMode ? "List view" : "Grid view";
        _showDetails.Enabled = !_gridMode;
    }

    private void ApplyFilter()
    {
        var terms = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _all.Where(i => terms.All(t => i.Display.Contains(t, StringComparison.OrdinalIgnoreCase)));
        if (_hideDupes.Checked)
            matches = matches.Where(i => !_extraDupes.Contains(i));
        if (_hideDownloaded.Checked)
            matches = matches.Where(i => StatusOf(i) != "On disk");

        Func<CollectionItem, string> sortKey = _sortColumn switch
        {
            TitleColumn => i => i.Title,
            TypeColumn => i => i.TypeLabel,
            StatusColumn => StatusOf,
            DetailsColumn => i => i.PurchaseDetails,
            _ => i => i.Artist,
        };
        var cmp = StringComparer.OrdinalIgnoreCase;
        var sorted = _sortDescending ? matches.OrderByDescending(sortKey, cmp) : matches.OrderBy(sortKey, cmp);
        _visible = sorted.ThenBy(i => i.Artist, cmp).ThenBy(i => i.Title, cmp).ToList();

        _grid.SetItems(_visible);
        if (_gridMode)
            _rows.Clear();
        else
            RebuildListRows();

        UpdateCount();
    }

    private void RebuildListRows()
    {
        _rebuilding = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        _rows.Clear();

        var rows = new ListViewItem[_visible.Count];
        for (int i = 0; i < _visible.Count; i++)
        {
            var item = _visible[i];
            var cells = _showDetails.Checked
                ? new[] { "", item.Artist, item.Title, item.TypeLabel, StatusOf(item), item.PurchaseDetails }
                : new[] { "", item.Artist, item.Title, item.TypeLabel, StatusOf(item) };
            var row = new ListViewItem(cells)
            {
                Tag = item,
                Checked = _checked.Contains(item),
            };
            if (item.DownloadPage is null) row.ForeColor = SystemColors.GrayText;
            if (!_hideDupes.Checked && _possibleDupes.Contains(item))
                row.BackColor = Color.FromArgb(255, 244, 204);
            rows[i] = row;
            _rows[item] = row;
        }
        _list.Items.AddRange(rows);

        _list.EndUpdate();
        _rebuilding = false;
    }

    private void ToggleAllShown()
    {
        bool check = !AllShownChecked();
        foreach (var item in _visible)
        {
            if (check) _checked.Add(item); else _checked.Remove(item);
        }

        _rebuilding = true;
        _list.BeginUpdate();
        foreach (ListViewItem row in _list.Items)
            row.Checked = row.Tag is CollectionItem item && _checked.Contains(item);
        _list.EndUpdate();
        _rebuilding = false;

        _grid.Invalidate();
        UpdateCount();
    }

    private bool AllShownChecked() => _visible.Count > 0 && _visible.All(_checked.Contains);

    private async Task RefreshOnDiskStatusAsync()
    {
        if (_cts is not null) return;

        var outDir = _outDir.Text.Trim();
        SetStatus("Checking save folder...");
        var scan = await Task.Run(() => LibraryScanner.Scan(outDir));

        int onDisk = 0, partial = 0;
        foreach (var item in _all)
        {
            string status;
            long partBytes;
            if (item.DownloadPage is null) status = "No download";
            else if (scan.IsComplete(item)) { status = "On disk"; onDisk++; }
            else if ((partBytes = scan.PartialBytes(item)) > 0) { status = $"Partial ({partBytes / BytesPerMB:0} MB), will resume"; partial++; }
            else status = "";
            _status[item] = status;
        }
        ApplyFilter();
        SetStatus($"{_all.Count} items: {onDisk} already downloaded" + (partial > 0 ? $", {partial} partly downloaded." : "."));
    }

    private string StatusOf(CollectionItem item) => _status.TryGetValue(item, out var s) ? s : "";

    private string GridStatus(CollectionItem item)
    {
        var status = StatusOf(item);
        return status == "" && !_hideDupes.Checked && _possibleDupes.Contains(item) ? "Possible duplicate" : status;
    }
    private void FindPossibleDuplicates()
    {
        _possibleDupes.Clear();
        _extraDupes.Clear();
        var groups = _all
            .GroupBy(LibraryScanner.Key)
            .Where(g => g.Key != "" && g.Select(i => i.ReleaseId).Distinct().Count() > 1);
        foreach (var group in groups)
        {
            var keep = group.FirstOrDefault(i => i.DownloadPage is not null) ?? group.First();
            foreach (var item in group)
            {
                _possibleDupes.Add(item);
                if (!ReferenceEquals(item, keep)) _extraDupes.Add(item);
            }
        }
    }

    private void SetItemStatus(CollectionItem item, string text)
    {
        _status[item] = text;
        if (_rows.TryGetValue(item, out var row)) row.SubItems[StatusColumn].Text = text;
        if (_gridMode) _grid.Invalidate();
    }

    private void UpdateCount()
    {
        _countText.Text = $"Showing {_visible.Count} of {_all.Count}   |   {_checked.Count} checked" +
                          (_extraDupes.Count == 0 ? ""
                              : _hideDupes.Checked ? $"   |   {_extraDupes.Count} duplicates hidden"
                              : $"   |   {_possibleDupes.Count} possible duplicates");
        _list.HeaderChecked = AllShownChecked();
        if (_gridMode) _grid.Invalidate();
    }

    private void SetStatus(string text) => _statusText.Text = text;

    private void OnUi(Action action)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action); else action();
        }
        catch (ObjectDisposedException) {}
        catch (InvalidOperationException) {}
    }

    private async Task DownloadCheckedAsync()
    {
        var shown = new HashSet<CollectionItem>(_visible, ReferenceEqualityComparer.Instance);
        var picked = _visible.Where(_checked.Contains)
            .Concat(_all.Where(i => _checked.Contains(i) && !shown.Contains(i)))
            .ToList();
        if (picked.Count == 0)
        {
            MessageBox.Show(this, "Check some items first.", "Quickcamp");
            return;
        }

        var format = (string)_format.SelectedItem!;
        string outDir;
        try
        {
            outDir = Path.GetFullPath(_outDir.Text.Trim());
            Directory.CreateDirectory(outDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Bad output folder: {ex.Message}", "Quickcamp");
            return;
        }
        SaveSettings();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusy(true, canCancel: true);

        SetStatus("Checking what's already downloaded...");
        var scan = await Task.Run(() => LibraryScanner.Scan(outDir));
        var job = new DownloadJob(format, outDir, _unzip.Checked, scan);
        int atOnce = (int)_parallel.SelectedItem!;

        int ok = 0, skipped = 0, failed = 0, finished = 0;
        _progress.Maximum = picked.Count;
        _progress.Value = 0;
        SetStatus($"Downloading {picked.Count} item(s), {atOnce} at a time...");

        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = atOnce, CancellationToken = ct };
            await Parallel.ForEachAsync(picked, options, async (item, token) =>
            {
                var result = await DownloadOneAsync(item, job, token);
                switch (result)
                {
                    case Result.Done: Interlocked.Increment(ref ok); break;
                    case Result.Skipped: Interlocked.Increment(ref skipped); break;
                    case Result.Failed: Interlocked.Increment(ref failed); break;
                }
                int n = Interlocked.Increment(ref finished);
                OnUi(() =>
                {
                    _progress.Value = Math.Min(n, _progress.Maximum);
                    if (!ct.IsCancellationRequested) SetStatus($"{n} of {picked.Count} finished ({atOnce} at a time)...");
                });
            });
        }
        catch (OperationCanceledException)
        {}
        finally
        {
            bool cancelled = _cts.IsCancellationRequested;
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            _progress.Maximum = 100;
            _progress.Value = 0;
            SetStatus($"{(cancelled ? "Stopped" : "Done")}. {ok} downloaded, {skipped} skipped, {failed} failed." +
                      (cancelled ? " Partial downloads will resume next time." : ""));
        }
    }
    private async Task<Result> DownloadOneAsync(CollectionItem item, DownloadJob job, CancellationToken ct)
    {
        void Status(string text) => OnUi(() => SetItemStatus(item, text));

        if (item.DownloadPage is null) { Status("No download"); return Result.Skipped; }
        if (job.Scan.IsComplete(item)) { Status("On disk"); return Result.Skipped; }

        try
        {
            Status(job.Scan.PartialBytes(item) > 0 ? "Resuming..." : "Preparing...");
            var links = await _bc.GetDownloadLinksAsync(item.DownloadPage, ct);
            if (!links.TryGetValue(job.Format, out var url))
            {
                Status($"No {job.Format} available");
                return Result.Skipped;
            }
            url = await _bc.ResolveDownloadUrlAsync(url, ct);
            var baseName = SafeName(item.Display);
            var throttle = Stopwatch.StartNew();
            var saved = await _bc.DownloadFileAsync(url, job.OutDir, baseName,
                Formats.FallbackExtension(job.Format, item.Type),
                (done, total) =>
                {
                    if (throttle.ElapsedMilliseconds < 250) return;
                    throttle.Restart();
                    var mb = done / BytesPerMB;
                    Status(total is > 0
                        ? $"{100 * done / total.Value}%  ({mb:0} of {total.Value / BytesPerMB:0} MB)"
                        : $"{mb:0.0} MB");
                },
                ct);

            if (job.Unzip && saved.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Status("Unzipping...");
                Unzip(saved, Path.Combine(job.OutDir, baseName));
            }
            Status("Done");
            return Result.Done;
        }
        catch (OperationCanceledException)
        {
            Status("Stopped (will resume)");
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            Status($"Failed: {ex.Message}");
            return Result.Failed;
        }
    }
    private static void Unzip(string zipPath, string folder)
    {
        if (Directory.Exists(folder))
        {
            ZipFile.ExtractToDirectory(zipPath, folder, overwriteFiles: true);
        }
        else
        {
            var temp = folder + ".extracting";
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            ZipFile.ExtractToDirectory(zipPath, temp);
            Directory.Move(temp, folder);
        }
        File.Delete(zipPath);
    }
    private void SetBusy(bool busy, bool canCancel = false)
    {
        _download.Enabled = !busy && _loggedIn;
        _reload.Enabled = !busy;
        _bigLogin.Enabled = !busy;
        _logout.Enabled = !busy && _loggedIn;
        _format.Enabled = !busy;
        _outDir.Enabled = !busy;
        _browse.Enabled = !busy;
        _unzip.Enabled = !busy;
        _parallel.Enabled = !busy;
        _cancel.Enabled = busy && canCancel;
        _progress.Style = busy && !canCancel ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        UseWaitCursor = busy && !canCancel;
        if (!busy) _progress.Value = 0;
    }

    private void LoadSettings()
    {
        var settings = new Dictionary<string, string>();
        try
        {
            foreach (var line in File.ReadAllLines(IniFile))
            {
                var eq = line.IndexOf('=');
                if (eq > 0) settings[line[..eq]] = line[(eq + 1)..];
            }
        }
        catch {}

        var format = settings.GetValueOrDefault("format", "flac");
        _format.SelectedItem = Formats.All.Contains(format) ? format : "flac";
        _outDir.Text = settings.GetValueOrDefault("outDir",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Bandcamp"));
        _unzip.Checked = settings.GetValueOrDefault("unzip") == "true";
        _hideDownloaded.Checked = settings.GetValueOrDefault("hideDownloaded") == "true";
        _showDetails.Checked = settings.GetValueOrDefault("purchaseDetails") == "true";
        _hideDupes.Checked = settings.GetValueOrDefault("hideDuplicates", "true") == "true";
        _gridMode = settings.GetValueOrDefault("view") == "grid";
        if (int.TryParse(settings.GetValueOrDefault("parallel"), out var n))
            _parallel.SelectedItem = Math.Clamp(n, 1, 10);
        _savedIdentity = settings.GetValueOrDefault("identity");
    }
    private void SaveSettings()
    {
        try
        {
            File.WriteAllLines(IniFile, new[]
            {
                $"format={_format.SelectedItem}",
                $"outDir={_outDir.Text.Trim()}",
                $"unzip={(_unzip.Checked ? "true" : "false")}",
                $"hideDownloaded={(_hideDownloaded.Checked ? "true" : "false")}",
                $"purchaseDetails={(_showDetails.Checked ? "true" : "false")}",
                $"hideDuplicates={(_hideDupes.Checked ? "true" : "false")}",
                $"view={(_gridMode ? "grid" : "list")}",
                $"parallel={_parallel.SelectedItem}",
                $"identity={_savedIdentity}",
            });
        }
        catch {}
    }
    private static string SafeName(string name)
    {
        const string invalid = "<>:\"/\\|?*";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(c < 32 || invalid.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length > 150 ? s[..150].TrimEnd() : s;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bc.Dispose();
            _filterTimer.Dispose();
        }
        base.Dispose(disposing);
    }
    private sealed class LogoBox : Control
    {
        public LogoBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var img = Assets.Logo;
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            float scale = Math.Min((float)ClientSize.Width / img.Width, (float)ClientSize.Height / img.Height);
            int w = (int)(img.Width * scale), h = (int)(img.Height * scale);
            var target = new Rectangle(ClientSize.Width - w, (ClientSize.Height - h) / 2, w, h);
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(img, target);
        }
    }
}
