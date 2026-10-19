using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.Json;

namespace HealthHunter;

public sealed partial class MainForm : ScaledForm
{
    // ---------------------------------------------------------------- state
    private ProcessMemory _mem;
    private Scanner _scanner;
    private readonly List<TableEntry> _entries = new();
    private readonly BarDetector _bar = new();
    private readonly ValueLogger _logger = new();
    private List<PointerChain> _chains = new();
    private CancellationTokenSource _cts;
    private bool _busy;
    private double _lastBarPct = double.NaN;
    private double _lastLoggedBar = double.NaN;
    private const int HkFreeze = 1, HkRefill = 2, HkLog = 3;

    // ---------------------------------------------------------------- header / footer
    private readonly ComboBox _procCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360, Margin = new Padding(0, 6, 8, 0) };
    private readonly ModernButton _refreshBtn = new() { Text = "Refresh", AutoSize = true, Variant = ButtonVariant.Ghost };
    private readonly ModernButton _attachBtn = new() { Text = "Attach", AutoSize = true, Variant = ButtonVariant.Primary, MinimumSize = new Size(96, 32) };
    private readonly Pill _attachPill = new() { Text = "Not attached" };
    private readonly CheckBox _showAll = new() { Text = "Show background processes in the game list", AutoSize = true };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.TextDim, AutoEllipsis = true };
    private readonly NavHost _tabs = new();
    private SideNav _nav;
    private Panel _header, _footer;
    private BrandMark _brand;
    private FlowLayoutPanel _picker;

    // Manual scan page
    private readonly TextBlock _guide = new() { Font = new Font("Segoe UI", 10f) };
    private readonly ComboBox _kindCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, Margin = new Padding(0, 4, 14, 0) };
    private readonly ComboBox _scanCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, Margin = new Padding(0, 4, 14, 0) };
    private readonly TextBox _valueBox = new() { Width = 110, Margin = new Padding(0, 5, 14, 0) };
    private readonly Label _value2Lbl = new() { Text = "and", AutoSize = true, Padding = new Padding(0, 9, 0, 0), Visible = false, ForeColor = Theme.TextDim };
    private readonly TextBox _value2Box = new() { Width = 110, Visible = false, Margin = new Padding(4, 5, 14, 0) };
    private readonly ModernButton _undoBtn = new() { Text = "Undo", AutoSize = true, Enabled = false };
    private readonly CheckBox _fastScanChk = new() { Text = "Fast scan (aligned)", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 16, 0) };
    private readonly CheckBox _mappedChk = new() { Text = "Include mapped memory", AutoSize = true, Margin = new Padding(0, 6, 16, 0) };
    private readonly CheckBox _pauseChk = new() { Text = "Pause game while scanning", AutoSize = true, Margin = new Padding(0, 6, 16, 0) };
    private readonly TextBox _rangeFrom = new() { Width = 150, PlaceholderText = "from (hex / mod+off)", Margin = new Padding(0, 5, 6, 0) };
    private readonly TextBox _rangeTo = new() { Width = 150, PlaceholderText = "to (blank = end)", Margin = new Padding(0, 5, 14, 0) };
    private readonly NumericUpDown _tolBox = new() { DecimalPlaces = 2, Increment = 0.5m, Maximum = 100000, Width = 70, Margin = new Padding(0, 5, 14, 0) };
    private readonly ModernButton _firstBtn = new() { Text = "First scan", AutoSize = true, Variant = ButtonVariant.Primary };
    private readonly ModernButton _nextBtn = new() { Text = "Next scan", AutoSize = true, Enabled = false, Variant = ButtonVariant.Primary };
    private readonly ModernButton _resetBtn = new() { Text = "New scan", AutoSize = true };
    private readonly ModernButton _cancelBtn = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly ModernButton _advBtn = new() { Text = "Show advanced options", AutoSize = true, Variant = ButtonVariant.Ghost };
    private readonly CheckBox _plausible = new() { Text = "Plausible health only (0 – 10M)", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 16, 0) };
    private readonly SlimProgress _progress = new() { Dock = DockStyle.Top };
    private readonly Label _countLbl = new() { AutoSize = false, Dock = DockStyle.Top, Height = 26, ForeColor = Theme.TextDim, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly ListView _results = new() { View = View.Details, VirtualMode = true, FullRowSelect = true, HideSelection = false };
    private readonly TextBox _setBox = new() { Width = 80, Text = "999", Margin = new Padding(0, 6, 8, 0) };
    private readonly ModernButton _setBtn = new() { Text = "Set selected to", AutoSize = true };
    private readonly ModernButton _addBtn = new() { Text = "Add selected to cheat table", AutoSize = true, Variant = ButtonVariant.Primary };

    // Health bar page
    private readonly ModernButton _pickBtn = new() { Text = "Select bar on screen", AutoSize = true, Variant = ButtonVariant.Primary };
    private readonly ModernButton _calBtn = new() { Text = "Re-learn color (bar must be FULL)", AutoSize = true };
    private readonly NumericUpDown _barTol = new() { Minimum = 5, Maximum = 200, Value = 45, Width = 64, Margin = new Padding(0, 5, 14, 0) };
    private readonly PictureBox _barPreview = new() { Height = 70, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label _barPct = new() { AutoSize = true, Font = Theme.Big, Margin = new Padding(0, 0, 16, 0) };
    private readonly Label _barInfo = new() { AutoSize = true, ForeColor = Theme.TextDim, Padding = new Padding(0, 14, 0, 0) };
    private readonly ValueGraph _barGraph = new() { Height = 120, Kind = ValueKind.Float, Title = "Bar %" };
    private readonly CheckBox _autoNarrow = new() { Text = "Auto-narrow the manual scan whenever the bar moves", AutoSize = true };
    private readonly TextBlock _barHelp = new();

    // Cheat table page
    private readonly DataGridView _grid = new() { AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly ModernButton _ptrBtn = new() { Text = "Find pointer path…", AutoSize = true };
    private readonly ModernButton _removeBtn = new() { Text = "Remove", AutoSize = true };
    private readonly ModernButton _saveBtn = new() { Text = "Save profile", AutoSize = true, Variant = ButtonVariant.Primary };
    private readonly ModernButton _loadBtn = new() { Text = "Load profile…", AutoSize = true };
    private readonly ModernButton _logBtn = new() { Text = "Start logging", AutoSize = true };
    private readonly ModernButton _ctImportBtn = new() { Text = "Import .CT…", AutoSize = true };
    private readonly ModernButton _ctExportBtn = new() { Text = "Export .CT…", AutoSize = true };
    private readonly TextBlock _tableHelp = new();

    // Pointer page
    private readonly NumericUpDown _depth = new() { Minimum = 1, Maximum = 7, Value = 4, Width = 56, Margin = new Padding(0, 5, 14, 0) };
    private readonly TextBox _maxOff = new() { Text = "1000", Width = 70, Margin = new Padding(0, 5, 14, 0) };
    private readonly ModernButton _ptrScanBtn = new() { Text = "Scan for selected entry", AutoSize = true, Variant = ButtonVariant.Primary };
    private readonly ModernButton _validateBtn = new() { Text = "Keep only paths that still reach it", AutoSize = true };
    private readonly ModernButton _useChainBtn = new() { Text = "Use selected path", AutoSize = true };
    private readonly ModernButton _loadChainsBtn = new() { Text = "Load saved paths", AutoSize = true };
    private readonly ListBox _chainList = new() { Font = Theme.Mono, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly Label _ptrStatus = new() { AutoSize = false, Height = 28, ForeColor = Theme.TextDim, TextAlign = ContentAlignment.MiddleLeft };

    private NavPage _autoPage, _findPage, _barPage, _tablePage, _ptrPage;

    // Quick start page
    private StepCard _step1, _step2, _step3, _step4;
    private readonly TextBlock _qsAttach = new() { ForeColor = Theme.TextDim };
    private readonly Label _qsBarPct = new() { AutoSize = true, Font = Theme.Big, Text = "–", Margin = new Padding(0, 0, 14, 0) };
    private readonly Label _qsBarInfo = new() { AutoSize = true, ForeColor = Theme.TextDim, Padding = new Padding(0, 14, 0, 0) };
    private readonly ValueGraph _qsBarGraph = new() { Height = 70, Kind = ValueKind.Float, Title = "Bar %", Capacity = 150 };
    private readonly TextBlock _autoSteps = new() { ForeColor = Theme.TextDim };
    private readonly TextBlock _autoStatus = new() { Font = new Font("Segoe UI Semibold", 12f), ForeColor = Theme.Accent, Padding = new Padding(0, 4, 0, 8) };
    private readonly ModernButton _autoStartBtn = new() { Text = "Start auto-find", AutoSize = true, Variant = ButtonVariant.Primary, MinimumSize = new Size(150, 38) };
    private readonly ModernButton _autoStopBtn = new() { Text = "Stop", AutoSize = true, Enabled = false, MinimumSize = new Size(80, 38) };
    private readonly ModernButton _autoRebarBtn = new() { Text = "Select health bar", AutoSize = true, MinimumSize = new Size(0, 32) };
    private readonly CheckBox _autoVerify = new() { Text = "Confirm by changing each value once and watching the game (recommended)", AutoSize = true, Checked = true, Margin = new Padding(8, 12, 0, 0) };
    private readonly ComboBox _statCombo = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 150, Margin = new Padding(0, 5, 10, 0) };
    private readonly FlowLayoutPanel _togglePanel = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 0, 0, 6) };
    private readonly ComboBox _modeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Margin = new Padding(0, 5, 14, 0) };
    private readonly NumericUpDown _floorPct = new() { Minimum = 1, Maximum = 99, Value = 50, Width = 56, Margin = new Padding(0, 5, 14, 0) };
    private readonly ModernButton _refillBtn = new() { Text = "Refill now", AutoSize = true, Enabled = false };
    private readonly Label _freezeLbl = new() { AutoSize = true, ForeColor = Theme.TextDim, Padding = new Padding(8, 10, 0, 0) };
    private readonly Dictionary<string, BarCalibration> _bars = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _autoCts;
    /// <summary>The running auto-find, so Detach and closing can let it put back what it wrote before the handle closes.</summary>
    private Task _autoTask;
    private bool _autoRunning;
    private FreezeEngine _freezer;

    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 200 };

    public MainForm()
    {
        Text = "HealthHunter 6";
        Width = 1240; Height = 820;
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Body;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;

        BuildAssistantTab();
        BuildAutoTab();
        BuildFindTab();
        BuildBarTab();
        BuildTableTab();
        BuildPointerTab();
        BuildMemoryTab();
        BuildDiagTab();
        BuildSettingsTab();
        BuildShell();
        Theme.Apply(this);
        // Every size above is a 100 % value: from here on they follow the form's font (Windows scaling, monitor DPI).
        EnableDesignScaling();

        Shown += (_, _) =>
        {
            ActiveControl = _procCombo;
            _autoPage.Body.AutoScrollPosition = Point.Empty;
            _diagBox.Text = Diag.All();
            if (_settings.AssistantOn) StartAssistant();
        };
        _uiTimer.Tick += (_, _) => UiTick();
        _uiTimer.Start();
        LoadProcesses();
        UpdateGuide();
        UpdateButtons();
        Diag.Log($"HealthHunter {Application.ProductVersion} started ({(Environment.Is64BitProcess ? "64" : "32")}-bit, " +
                 $"{(IsElevated() ? "administrator" : "standard user")}, Windows {Environment.OSVersion.Version}).");
    }

    // ================================================================= layout

    private static NavPage Page(string nav, char glyph, string fallback, string heading, string blurb) =>
        new(nav, Theme.Glyph(glyph, fallback), heading, blurb);

    private void BuildShell()
    {
        // Header: brand on the left, game picker and Attach on the right.
        var header = _header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Sidebar, Padding = new Padding(20, 12, 20, 10) };
        _brand = new BrandMark { Dock = DockStyle.Left, Text = "HealthHunter", Version = "v6.1" };
        var picker = _picker = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        picker.Controls.AddRange(new Control[] { _attachPill, Ui.Caption("Game"), _procCombo, _refreshBtn, _attachBtn });
        header.Controls.Add(picker);
        header.Controls.Add(_brand);
        header.Paint += (_, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1); };
        // Fonts of the picker's controls can change after the form's (when their windows are created on a monitor with
        // another DPI), so re-check the bar heights whenever they lay out again.
        picker.Layout += (_, _) => FitBars();
        _brand.FontChanged += (_, _) => FitBars();

        var footer = _footer = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Theme.Sidebar, Padding = new Padding(16, 0, 16, 0) };
        footer.Controls.Add(_status);
        footer.Paint += (_, e) => { using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };

        _nav = new SideNav(_tabs) { Badge = NavBadge };
        Controls.Add(_tabs);
        Controls.Add(_nav);
        Controls.Add(footer);
        Controls.Add(header);

        _refreshBtn.Click += (_, _) => LoadProcesses();
        _showAll.CheckedChanged += (_, _) => LoadProcesses();
        _attachBtn.Click += (_, _) =>
        {
            if (_mem == null) Attach();
            else { _noAutoAttachPid = _mem.Pid; Detach("Detached."); } // don't auto-attach straight back
        };
    }

    protected override void OnUiScaleChanged(float k) => FitBars();

    /// <summary>Top bar and status bar: their 100 % height scaled, or taller if what they hold needs it.</summary>
    private bool _fittingBars;
    private void FitBars()
    {
        if (_header == null || _footer == null || _fittingBars) return;
        _fittingBars = true;
        try
        {
            int content = Math.Max(_brand.PreferredHeight, _picker.PreferredSize.Height);
            int h = Math.Max(Px(64), content + _header.Padding.Vertical);
            if (_header.Height != h) _header.Height = h;
            int f = Math.Max(Px(28), TextRenderer.MeasureText("Ag", _status.Font).Height + Px(8));
            if (_footer.Height != f) _footer.Height = f;
        }
        finally { _fittingBars = false; }
    }

    private string NavBadge(NavPage p)
    {
        if (p == _findPage && _scanner?.HasScan == true && !_scanner.InSnapshotMode) return _scanner.ResultCount > 9999 ? "10k+" : _scanner.ResultCount.ToString("N0");
        if (p == _tablePage && _entries.Count > 0) return _entries.Count.ToString();
        return null;
    }

    private void BuildFindTab()
    {
        var page = _findPage = Page("Manual scan", '\uE721', "?", "Manual scan",
            "Classic scanner. Use it when auto-find can't see a bar, or to find other numbers (ammo, money, stamina).");

        _kindCombo.Items.Add("Auto (Int32 + Float + Double)");
        foreach (var k in Enum.GetValues<ValueKind>()) _kindCombo.Items.Add(k);
        _kindCombo.SelectedIndex = 0;
        foreach (var t in Enum.GetValues<ScanType>()) _scanCombo.Items.Add(t.Label());
        _scanCombo.SelectedIndex = 0;
        _kindCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_scanner?.HasScan ?? true) _tolBox.Value = _kindCombo.SelectedIndex > 0 && SelectedKinds[0].IsFloating() ? 0.5m : 0;
        };

        // Left: what to do next.
        var guideCard = new Card { Title = "What to do next", Dock = DockStyle.Left, Width = 330 };
        guideCard.Controls.Add(_guide);

        // Right: scan controls, then results.
        var scanCard = new Card { Title = "Scan", FitContent = true };
        var row1 = Ui.Row(Ui.Caption("Value type"), _kindCombo, Ui.Caption("Scan"), _scanCombo);
        var row2 = Ui.Row(Ui.Caption("Value"), _valueBox, _value2Lbl, _value2Box, Ui.Caption("± tolerance"), _tolBox);
        var row3 = Ui.Row(_firstBtn, _nextBtn, _undoBtn, _resetBtn, _cancelBtn, _advBtn);
        var adv = Ui.Row(_fastScanChk, _mappedChk, _pauseChk, _plausible, Ui.Caption("Range"), _rangeFrom, _rangeTo);
        adv.Visible = false;
        Ui.Stack(scanCard, new Control[] { row1, row2, row3, adv, _progress, _countLbl });

        var resCard = new Card { Title = "Results" };
        var bottom = Ui.Row(_setBtn, _setBox, _addBtn);
        bottom.Padding = new Padding(0, 10, 0, 0);
        Ui.Stack(resCard, null, _results, new Control[] { bottom });

        var right = new Panel { Padding = new Padding(16, 0, 0, 0) };
        Ui.Stack(right, new Control[] { scanCard, Ui.Gap() }, resCard);
        Ui.Stack(page.Body, null, right);
        page.Body.Controls.Add(guideCard);
        _tabs.Add(page);

        _results.Columns.Add("Address", 300);
        _results.Columns.Add("Type", 80);
        _results.Columns.Add("Current", 140);
        _results.Columns.Add("Previous scan", 140);
        _results.RetrieveVirtualItem += Results_RetrieveVirtualItem;
        _results.DoubleClick += (_, _) => AddSelectedToTable();

        _advBtn.Click += (_, _) => { adv.Visible = !adv.Visible; _advBtn.Text = adv.Visible ? "Hide advanced options" : "Show advanced options"; };
        _firstBtn.Click += async (_, _) => await FirstScanAsync();
        _nextBtn.Click += async (_, _) => await NextScanAsync(SelectedScanType, ParseValueBox(), (double)_tolBox.Value);
        _resetBtn.Click += (_, _) => { _scanner?.Reset(); _results.VirtualListSize = 0; _scanCombo.SelectedIndex = 0; _countLbl.Text = ""; UpdateGuide(); UpdateButtons(); };
        _undoBtn.Click += (_, _) => UndoScan();
        _scanCombo.SelectedIndexChanged += (_, _) => _value2Lbl.Visible = _value2Box.Visible = SelectedScanType.NeedsSecondValue();
        _fastScanChk.Checked = _settings.FastScan;
        _mappedChk.Checked = _settings.IncludeMapped;
        _pauseChk.Checked = _settings.PauseWhileScanning;
        _fastScanChk.CheckedChanged += (_, _) => { _settings.FastScan = _fastScanChk.Checked; _settings.Save(); };
        _mappedChk.CheckedChanged += (_, _) => { _settings.IncludeMapped = _mappedChk.Checked; _settings.Save(); };
        _pauseChk.CheckedChanged += (_, _) => { _settings.PauseWhileScanning = _pauseChk.Checked; _settings.Save(); };
        _cancelBtn.Click += (_, _) => _cts?.Cancel();
        _plausible.CheckedChanged += (_, _) => { if (_scanner != null) _scanner.PlausibleHealthFilter = _plausible.Checked; };
        _setBtn.Click += (_, _) => SetSelectedValue();
        _addBtn.Click += (_, _) => AddSelectedToTable();
    }

    private void BuildBarTab()
    {
        var page = _barPage = Page("Health bar", '\uE95E', "♥", "Health bar",
            "HealthHunter reads the bar's pixels from the screen. Auto-find compares this reading with memory.");

        var liveCard = new Card { Title = "Live reading", FitContent = true };
        var row = Ui.Row(_pickBtn, _calBtn, Ui.Caption("Color tolerance"), _barTol);
        var row2 = Ui.Row(_barPct, _barInfo);
        _barPreview.Dock = DockStyle.Top;
        _barGraph.Dock = DockStyle.Top;
        _barGraph.BackColor = Theme.Surface2;
        Ui.Stack(liveCard, new Control[] { row, row2, _barPreview, Ui.Gap(10), _barGraph });

        var helpCard = new Card { Title = "Getting a good reading" };
        _barHelp.Text =
            "• Run the game in Borderless or Windowed mode and turn HDR off. Exclusive fullscreen and HDR capture as black or with wrong colors.\r\n" +
            "• Click 'Select bar on screen' with health FULL, then drag a tight box around the colored fill of the bar (not its frame).\r\n" +
            "• The % should read about 100 now. Take a hit: it should drop and the graph should step down cleanly. If it jumps around, raise " +
            "Color tolerance; if it never drops, lower it.\r\n" +
            "• Bars that drain right-to-left or top-down are detected on their own once the bar is partly empty.\r\n\r\n" +
            "Auto-narrow (for the manual scanner): tick it, then just play. Each time the bar settles at a new level, HealthHunter runs a " +
            "Decreased or Increased scan for you. Then check the short list on the Manual scan page.";
        Ui.Stack(helpCard, new Control[] { _autoNarrow, Ui.Gap(8), _barHelp });

        Ui.Stack(page.Body, new Control[] { liveCard, Ui.Gap() }, helpCard);
        page.Body.AutoScroll = true;
        _tabs.Add(page);

        _pickBtn.Click += (_, _) => { if (PickAndCalibrateBar()) SaveProfileQuiet(); };
        _calBtn.Click += (_, _) => CalibrateBar();
        _barTol.ValueChanged += (_, _) => { if (_bar.Cal != null) _bar.Cal.Tolerance = (int)_barTol.Value; };
        _autoNarrow.CheckedChanged += async (_, _) => await AutoNarrowToggled();
    }

    private void BuildTableTab()
    {
        var page = _tablePage = Page("Cheat table", '\uE8FD', "≡", "Cheat table",
            "Everything you've found. Tick Freeze, type in Value to write it, double-click Hotkeys to bind keys or a controller.");
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Freeze", HeaderText = "Freeze", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", FillWeight = 25 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Group", HeaderText = "Group", FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Max", HeaderText = "Max / refill", FillWeight = 15 });
        var modeCol = new DataGridViewComboBoxColumn { Name = "Mode", HeaderText = "Freeze mode", FillWeight = 22, FlatStyle = FlatStyle.Flat };
        foreach (var m in Enum.GetValues<FreezeMode>()) modeCol.Items.Add(TableEntry.ModeLabel(m));
        _grid.Columns.Add(modeCol);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mult", HeaderText = "Gain ×", FillWeight = 9 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Hotkeys", HeaderText = "Hotkeys", FillWeight = 28, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Type", FillWeight = 12, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Where", HeaderText = "Location", FillWeight = 60, ReadOnly = true });
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.CurrentCell is DataGridViewCheckBoxCell) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += Grid_CellValueChanged;
        _grid.CellDoubleClick += (_, ev) => { if (ev.RowIndex >= 0 && _grid.Columns[ev.ColumnIndex].Name == "Hotkeys") EditHotkeys(SelectedEntry); };
        _grid.SelectionChanged += (_, _) => ResetGraph();
        _grid.ContextMenuStrip = BuildGridMenu();

        _tableHelp.ForeColor = Theme.TextDim;
        _tableHelp.Text = "Global hotkeys: Ctrl+Alt+F freeze/unfreeze all  •  Ctrl+Alt+R refill all  •  Ctrl+Alt+L start/stop CSV logging. " +
                          "Right-click a row for group actions (freeze group, save/restore slots, view in memory). " +
                          "Profiles are saved per game in %AppData%\\HealthHunter\\profiles and load when you attach. Only entries with a " +
                          "pointer path are saved, because raw addresses change every launch.";
        var card = new Card();
        var buttons = Ui.Row(_saveBtn, _ptrBtn, _hotkeyBtn, _removeBtn, _loadBtn, _logBtn, _ctImportBtn, _ctExportBtn);
        buttons.Padding = new Padding(0, 0, 0, 10);
        _graph.BackColor = Theme.Surface2;
        Ui.Stack(card, new Control[] { buttons, _tableHelp, Ui.Gap(4) }, _grid, new Control[] { Ui.Gap(10), _graph });
        Ui.Stack(page.Body, null, card);
        _tabs.Add(page);

        _ptrBtn.Click += (_, _) => { _tabs.SelectedTab = _ptrPage; };
        _removeBtn.Click += (_, _) => { var e = SelectedEntry; if (e != null) { _entries.Remove(e); RebuildGrid(); RegisterHotkeys(); } };
        _hotkeyBtn.Click += (_, _) => EditHotkeys(SelectedEntry);
        _saveBtn.Click += (_, _) => SaveProfile();
        _loadBtn.Click += (_, _) => LoadProfileDialog();
        _logBtn.Click += (_, _) => ToggleLogging();
        _ctImportBtn.Click += (_, _) => ImportCt();
        _ctExportBtn.Click += (_, _) => ExportCt();
        _grid.DataError += (_, ev) => ev.ThrowException = false;
    }

    private void BuildPointerTab()
    {
        var page = _ptrPage = Page("Pointer paths", '\uE71B', "→", "Pointer paths",
            "Addresses change every launch. A pointer path starts inside the game's exe and follows pointers to the value, so it keeps working.");
        var stepsCard = new Card { Title = "How to get a path that survives restarts", FitContent = true };
        Ui.Stack(stepsCard, new Control[]
        {
            Ui.Para("1.  Select your entry on the Cheat table page, then click 'Scan for selected entry' below.\r\n" +
                    "2.  Restart the game, attach again, find the value again and add it to the table.\r\n" +
                    "3.  Select that new entry and click 'Keep only paths that still reach it'. Repeat once more for confidence.\r\n" +
                    "4.  Pick the top path, click 'Use selected path', then Save profile on the Cheat table page.", false)
        });

        var card = new Card { Title = "Paths" };
        var row = Ui.Row(Ui.Caption("Max levels"), _depth, Ui.Caption("Max offset (hex)"), _maxOff, _mainOnlyChk,
                         Ui.Caption("Last offset (hex)"), _lastOffBox);
        var row2 = Ui.Row(_ptrScanBtn, _validateBtn, _useChainBtn, _loadChainsBtn);
        _mainOnlyChk.Margin = new Padding(0, 6, 14, 0);
        _ptrStatus.Text = "Select an entry on the Cheat table page, then click Scan.";
        Ui.Stack(card, new Control[] { row, row2, Ui.Gap(6) }, _chainList, new Control[] { _ptrStatus });

        Ui.Stack(page.Body, new Control[] { stepsCard, Ui.Gap() }, card);
        _tabs.Add(page);

        _ptrScanBtn.Click += async (_, _) => await PointerScanAsync();
        _validateBtn.Click += (_, _) => ValidateChains();
        _useChainBtn.Click += (_, _) => UseSelectedChain();
        _loadChainsBtn.Click += (_, _) => LoadSavedChains();
    }

    // ================================================================= quick start (auto mode)

    private void BuildAutoTab()
    {
        var page = _autoPage = Page("Quick start", '\uE945', "»", "Quick start",
            "Four steps. Single-player and offline games only.");

        _statCombo.Items.AddRange(new object[] { "Health", "Stamina", "Mana / FP" });
        _statCombo.Text = "Health";
        foreach (var m in new[] { FreezeMode.LockAtMax, FreezeMode.Floor }) _modeCombo.Items.Add(TableEntry.ModeLabel(m));
        _modeCombo.SelectedIndex = 0;

        _step1 = new StepCard { Number = 1, Title = "Attach to your game", FitContent = true };
        _qsAttach.Text = "Start the game in Borderless or Windowed mode and load into gameplay. Pick it in the Game list at the top and click Attach.";
        Ui.Stack(_step1, new Control[] { _qsAttach });

        _step2 = new StepCard { Number = 2, Title = "Mark the bar", FitContent = true };
        _qsBarGraph.Dock = DockStyle.Top;
        _qsBarGraph.BackColor = Theme.Surface2;
        Ui.Stack(_step2, new Control[]
        {
            Ui.Row(Ui.Caption("Stat"), _statCombo, _autoRebarBtn),
            Ui.Para("With the bar FULL, drag a tight box around its colored fill. The live reading below should say about 100% and drop when you take a hit."),
            Ui.Row(_qsBarPct, _qsBarInfo),
            _qsBarGraph
        });

        _step3 = new StepCard { Number = 3, Title = "Find it", FitContent = true };
        _autoSteps.Text = "Click Start, go back to the game and make the bar move a few times: take hits, heal, sprint or cast. " +
                          "HealthHunter scans after each change and tracks whole numbers and decimals (Int32, Float, Double) at the same time. " +
                          "Big games can take 30–90 s for the first scan.";
        _autoStatus.Text = "Attach to a game to begin.";
        Ui.Stack(_step3, new Control[] { Ui.Row(_autoStartBtn, _autoStopBtn, _autoVerify), _autoStatus, _autoSteps });

        _step4 = new StepCard { Number = 4, Title = "Turn it on", FitContent = true };
        Ui.Stack(_step4, new Control[]
        {
            _togglePanel,
            Ui.Row(Ui.Caption("Mode for the selected stat"), _modeCombo, Ui.Caption("floor %"), _floorPct, _refillBtn, _freezeLbl),
            Ui.Para("Lock at max = infinite. Never below % = you still take damage but can't drop under the floor. The freeze runs about 1,000 " +
                    "times a second; a single hit bigger than the whole bar can still land between checks, so use 'Never below %' with a " +
                    "higher floor if that happens. Hotkeys: Ctrl+Alt+F toggles all, Ctrl+Alt+R refills all.")
        });

        page.Body.AutoScroll = true;
        page.Body.Padding = new Padding(0, 0, 8, 0);
        Ui.Stack(page.Body, new Control[] { _step1, Ui.Gap(12), _step2, Ui.Gap(12), _step3, Ui.Gap(12), _step4, Ui.Gap(12) });
        _tabs.Add(page);

        _autoStartBtn.Click += async (_, _) => await StartAutoFindAsync();
        _autoStopBtn.Click += (_, _) => _autoCts?.Cancel();
        _autoRebarBtn.Click += (_, _) => { if (PickAndCalibrateBar()) SaveProfileQuiet(); };
        _statCombo.TextChanged += (_, _) => OnStatChanged();
        _modeCombo.SelectedIndexChanged += (_, _) => ApplyModeToSelected();
        _floorPct.ValueChanged += (_, _) => ApplyModeToSelected();
        _refillBtn.Click += (_, _) => Refill(SelectedStatEntry);
    }

    private void UpdateSteps()
    {
        if (_step1 == null) return;
        bool att = _mem != null, bar = _bar.IsCalibrated, found = att && SelectedStatEntry?.Resolve(_mem) != null;
        _step1.State = att ? StepState.Done : StepState.Active;
        _step2.State = !att ? StepState.Pending : bar ? StepState.Done : StepState.Active;
        _step3.State = _autoRunning ? StepState.Active : found ? StepState.Done : att && bar ? StepState.Active : StepState.Pending;
        _step4.State = found ? (_entries.Any(e => e.Frozen) ? StepState.Done : StepState.Active) : StepState.Pending;
        _step2.Title = $"Mark the {CurrentStat.ToLowerInvariant()} bar";
        _autoRebarBtn.Text = bar ? $"Redraw {CurrentStat.ToLowerInvariant()} bar box" : $"Select {CurrentStat.ToLowerInvariant()} bar";
        _autoRebarBtn.Variant = bar ? ButtonVariant.Secondary : ButtonVariant.Primary;
        _attachPill.Text = att ? $"{_mem.ProcessName}  ·  {(_mem.Is64Bit ? "64" : "32")}-bit" : "Not attached";
        _attachPill.Dot = att ? Theme.Success : Theme.TextDim;
        _attachBtn.Text = att ? "Detach" : "Attach";
        _attachBtn.Variant = att ? ButtonVariant.Secondary : ButtonVariant.Primary;
        _nav?.Invalidate();
    }


    private void ImportCt()
    {
        using var dlg = new OpenFileDialog { Filter = "Cheat Engine tables|*.CT;*.ct" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var r = CheatTable.Import(dlg.FileName);
            _entries.AddRange(r.Entries);
            RebuildGrid();
            RegisterHotkeys();
            int ok = _mem == null ? 0 : r.Entries.Count(x => x.Resolve(_mem) != null);
            MessageBox.Show($"Imported {r.Entries.Count} pointer entr{(r.Entries.Count == 1 ? "y" : "ies")}" +
                            (_mem != null ? $" ({ok} resolve right now)" : "") + ".\r\n" +
                            (r.SkippedScripts > 0 ? $"Skipped {r.SkippedScripts} script entr{(r.SkippedScripts == 1 ? "y" : "ies")} (Auto Assembler scripts need Cheat Engine).\r\n" : "") +
                            (r.SkippedUnsupported > 0 ? $"Skipped {r.SkippedUnsupported} entr{(r.SkippedUnsupported == 1 ? "y" : "ies")} that use table symbols or unsupported types." : ""),
                            "Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show("Could not read that table: " + ex.Message); }
    }

    private void ExportCt()
    {
        if (!_entries.Any(x => x.Chain != null)) { MessageBox.Show("Nothing to export yet: entries need a pointer path (Pointer paths page) first."); return; }
        using var dlg = new SaveFileDialog { Filter = "Cheat Engine table|*.CT", FileName = (_mem?.ProcessName ?? "game") + ".CT" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        CheatTable.Export(dlg.FileName, _entries);
        _status.Text = "Exported " + dlg.FileName;
    }


    // ================================================================= auto mode

    private string CurrentStat
    {
        get { var t = _statCombo.Text.Trim(); return t.Length == 0 ? "Health" : t; }
    }

    private TableEntry SelectedStatEntry =>
        _entries.FirstOrDefault(e => string.Equals(e.Name, CurrentStat, StringComparison.OrdinalIgnoreCase));


    private bool _syncingMode;
    private void OnStatChanged()
    {
        _bar.Cal = _bars.TryGetValue(CurrentStat, out var c) ? c : null;
        var e = SelectedStatEntry;
        _syncingMode = true;
        if (e != null)
        {
            _modeCombo.SelectedIndex = e.Mode == FreezeMode.Floor ? 1 : 0;
            _floorPct.Value = (decimal)Math.Clamp(Math.Round(e.FloorPercent * 100), 1, 99);
        }
        _syncingMode = false;
        UpdateButtons();
    }

    private void ApplyModeToSelected()
    {
        if (_syncingMode) return;
        var e = SelectedStatEntry;
        if (e == null) return;
        e.Mode = _modeCombo.SelectedIndex == 1 ? FreezeMode.Floor : FreezeMode.LockAtMax;
        e.FloorPercent = (double)_floorPct.Value / 100.0;
        RebuildGrid();
        SaveProfileQuiet();
    }

    private void Refill(TableEntry e)
    {
        if (_mem == null || e == null) return;
        var a = e.Resolve(_mem);
        if (a == null) return;
        double max = e.CurrentMax(_mem, a.Value);
        _mem.WriteValue(a.Value, e.Kind, max);
        if (e.Mode is FreezeMode.FreezeCurrent or FreezeMode.AllowIncrease) e.FreezeValue = max;
    }

    /// <summary>One big on/off switch per found stat.</summary>
    private void RebuildToggles()
    {
        foreach (var panel in new[] { _togglePanel, _asTogglePanel }) RebuildToggles(panel, panel == _asTogglePanel ? 12 : 6);
    }

    private void RebuildToggles(FlowLayoutPanel _togglePanel, int take)
    {
        _togglePanel.SuspendLayout();
        foreach (Control c in _togglePanel.Controls.Cast<Control>().ToList()) c.Dispose();
        _togglePanel.Controls.Clear();
        foreach (var e in _entries.Take(take))
        {
            var t = new CheckBox
            {
                Appearance = Appearance.Button, Width = 280, Height = 78, Tag = e, FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI Semibold", 12.5f), Checked = e.Frozen,
                Margin = new Padding(0, 0, 12, 10), Cursor = Cursors.Hand
            };
            t.CheckedChanged += (_, _) =>
            {
                if (_syncingToggle) return;
                var en = (TableEntry)t.Tag;
                SetFrozen(en, t.Checked);
                StyleToggle(t);
                RebuildGrid();
            };
            StyleToggle(t);
            _togglePanel.Controls.Add(t);
        }
        _togglePanel.ResumeLayout();
    }

    private void SetFrozen(TableEntry e, bool on)
    {
        e.Frozen = on;
        if (on && _mem != null && e.Resolve(_mem) is long a && _mem.TryReadValue(a, e.Kind, out var cur))
            e.FreezeValue = e.Mode == FreezeMode.LockAtMax ? e.CurrentMax(_mem, a) : cur;
        _freezer?.Update(_entries);
    }

    private bool _syncingToggle;
    private void StyleToggle(CheckBox t)
    {
        var e = (TableEntry)t.Tag;
        string what = e.Mode switch
        {
            FreezeMode.LockAtMax => "Infinite " + e.Name.ToLowerInvariant(),
            FreezeMode.Floor => $"{e.Name} ≥ {e.FloorPercent * 100:0}%",
            FreezeMode.MultiplyGains => $"{e.Name} gains ×{e.Multiplier:0.##}",
            _ => "Freeze " + e.Name
        };
        string val = "";
        if (_mem != null && e.Resolve(_mem) is long a && _mem.TryReadValue(a, e.Kind, out var v))
            val = $"\n{e.Kind.Format(v)} / {e.Kind.Format(e.CurrentMax(_mem, a))}";
        else if (_mem != null) val = "\n(not in memory right now)";
        string text = $"{what}: {(t.Checked ? "ON" : "OFF")}{val}";
        if (t.Text != text) t.Text = text;
        t.BackColor = t.Checked ? Theme.Success : Theme.Surface2;
        t.ForeColor = t.Checked ? Color.White : Theme.Text;
        t.FlatAppearance.BorderColor = t.Checked ? Theme.Success : Theme.Border;
        t.FlatAppearance.CheckedBackColor = Theme.Success;
        t.FlatAppearance.MouseOverBackColor = t.Checked ? Theme.Blend(Theme.Success, Color.White, 0.1) : Theme.SurfaceHover;
    }

    private void UpdateAutoReadout()
    {
        foreach (CheckBox t in _togglePanel.Controls.Cast<Control>().Concat(_asTogglePanel.Controls.Cast<Control>()))
        {
            var e = (TableEntry)t.Tag;
            if (t.Checked != e.Frozen) { _syncingToggle = true; t.Checked = e.Frozen; _syncingToggle = false; }
            t.Enabled = _mem != null && e.Resolve(_mem) != null;
            StyleToggle(t);
        }
        _refillBtn.Enabled = _mem != null && SelectedStatEntry?.Resolve(_mem) != null;
        _freezeLbl.Text = _freezer != null && _entries.Any(e => e.Frozen)
            ? $"Freeze active, {Interlocked.Read(ref _freezer.Corrections):N0} correction(s) so far"
            : "";
    }

    private void AfterAttachAuto()
    {
        if (_mem == null) return;
        var working = _entries.Where(x => x.Resolve(_mem) is long ad && _mem.TryReadValue(ad, x.Kind, out var vv) && vv > 0 && vv <= x.CurrentMax(_mem, ad) * 1.5)
                              .Select(x => x.Name).ToList();
        var e = SelectedStatEntry;
        if (working.Count > 0 && (e == null || working.Contains(e.Name)))
        {
            _autoStatus.Text = $"Ready: {string.Join(", ", working)}. Flip a switch when you want it.";
            UpdateButtons();
            return;
        }
        string why = e == null ? $"No saved {CurrentStat.ToLowerInvariant()} for this game yet." : $"The saved {CurrentStat.ToLowerInvariant()} path didn't work this time.";
        if (e != null && _gameUpdated) why += " The game exe has changed since the profile was saved, so a game update probably moved it.";
        // v5: no pop-up; the Quick start steps show what to do next.
        _autoStatus.Text = why + (_bar.IsCalibrated ? " Get into gameplay with the bar showing and click Start auto-find."
                                                    : $" Mark the {CurrentStat.ToLowerInvariant()} bar in step 2 first.");
        _tabs.SelectedTab = _autoPage;
    }

    private bool PickAndCalibrateBar()
    {
        string stat = CurrentStat;
        MessageBox.Show($"Next, drag a box tightly around your {stat.ToLowerInvariant()} bar's colored fill.\r\n\r\nThe bar must be FULL right now.",
                        stat + " bar", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Hide();
        Thread.Sleep(300);
        Rectangle sel;
        using (var picker = new RegionPicker())
        {
            bool ok = picker.ShowDialog() == DialogResult.OK;
            sel = picker.Selected;
            Show();
            Activate();
            if (!ok) return false;
        }
        _bar.Cal = new BarCalibration { X = sel.X, Y = sel.Y, Width = sel.Width, Height = sel.Height, Tolerance = (int)_barTol.Value };
        _bar.Calibrate(_bar.Region);
        _bars[stat] = _bar.Cal;
        double check = _bar.Measure(out var bmp);
        bmp?.Dispose();
        if (double.IsNaN(check) || check < 0.85)
        {
            MessageBox.Show($"The box reads {check * 100:0}% full instead of about 100%. Redraw it tighter around just the colored fill, " +
                            "or raise Color tolerance on the Health bar page.", stat + " bar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    private async Task StartAutoFindAsync()
    {
        if (_mem == null || _busy) return;
        string stat = CurrentStat;
        if (!_bar.IsCalibrated && !PickAndCalibrateBar()) { _autoStatus.Text = $"{stat} bar not set. Click Start Auto-find to try again."; return; }

        _autoRunning = true;
        _busy = true;
        _results.VirtualListSize = 0;
        ResetScanOptionsForAuto();
        _autoCts = new CancellationTokenSource();
        UpdateButtons();
        var status = new Progress<string>(m => _autoStatus.Text = m);
        if (!long.TryParse(_maxOff.Text, NumberStyles.HexNumber, null, out long maxOff)) maxOff = 0x1000;
        var finder = new AutoFinder(_mem, _scanner, _bar, status, stat)
        {
            VerifyByWriting = _autoVerify.Checked,
            PointerDepth = (int)_depth.Value,
            PointerMaxOffset = maxOff
        };
        try
        {
            var ct = _autoCts.Token;
            var task = Task.Run(() => finder.Run(ct));
            _autoTask = task;
            var result = await task;
            if (result.Entry != null)
            {
                int at = _entries.FindIndex(x => string.Equals(x.Name, stat, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) _entries[at] = result.Entry; else _entries.Add(result.Entry);
                RebuildGrid();
                OnStatChanged();
                SaveProfileQuiet();
                System.Media.SystemSounds.Exclamation.Play();
            }
            if (result.Alternatives.Count > 0)
            {
                // Replace options left by an earlier give-up instead of adding a second "option A".
                _entries.RemoveAll(x => x.Chain == null && x.Name.StartsWith(stat + " option ", StringComparison.OrdinalIgnoreCase));
                _entries.AddRange(result.Alternatives);
                RebuildGrid();
            }
            _autoStatus.Text = result.Message;
        }
        catch (OperationCanceledException) { _autoStatus.Text = "Stopped. Click Start Auto-find to run again."; }
        catch (OutOfMemoryException) { _autoStatus.Text = "Ran out of memory during the pointer search. Lower Max levels on the Pointer paths page and run again."; }
        catch (Exception ex) { _autoStatus.Text = "Error: " + ex.Message; }
        finally
        {
            _autoRunning = false;
            _busy = false;
            _scanner?.Reset();
            if (_scanner != null) _scanner.KeepUndo = true;
            RefreshResultsList();
            UpdateGuide();
            UpdateButtons();
        }
    }

    private void SaveProfileQuiet()
    {
        if (_mem == null) return;
        try { ProfileStore.Save(CurrentProfile()); }
        catch { }
    }

    // ================================================================= process attach

    private sealed record ProcItem(int Pid, string Name, string Title)
    {
        public override string ToString() => string.IsNullOrEmpty(Title) ? $"{Name}  (pid {Pid})" : $"{Name} — {Title}  (pid {Pid})";
    }

    private void LoadProcesses()
    {
        var items = new List<ProcItem>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                string title = p.MainWindowTitle;
                if (!_showAll.Checked && string.IsNullOrWhiteSpace(title)) continue;
                items.Add(new ProcItem(p.Id, p.ProcessName, title));
            }
            catch { }
        }
        _procCombo.Items.Clear();
        foreach (var i in items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)) _procCombo.Items.Add(i);
        if (_procCombo.Items.Count > 0) _procCombo.SelectedIndex = 0;
    }

    private void Attach()
    {
        if (_procCombo.SelectedItem is not ProcItem item) return;
        Process p;
        try { p = Process.GetProcessById(item.Pid); }
        catch { MessageBox.Show("That process has exited. Click Refresh."); return; }
        Attach(p, quiet: false);
    }

    /// <summary>quiet = started by auto-attach: no pop-ups, failures just go to the status line.</summary>
    private bool Attach(Process p, bool quiet)
    {
        ProcessMemory mem;
        try { mem = ProcessMemory.Open(p); }
        catch (Exception ex)
        {
            if (quiet) { _status.Text = "Auto-attach: " + ex.Message; _noAutoAttachPid = p.Id; }
            else MessageBox.Show(ex.Message, "Attach failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var ac = mem.DetectAntiCheat();
        if (ac != null)
        {
            mem.Dispose();
            _noAutoAttachPid = p.Id;
            if (quiet) _status.Text = $"Not attaching to {p.ProcessName}: anti-cheat detected ({ac}).";
            else
                MessageBox.Show($"{p.ProcessName} looks protected by anti-cheat ({ac}).\r\n\r\nHealthHunter is for single-player and offline " +
                                "games only and won't attach here: editing memory in protected or online games gets accounts banned.",
                                "Not attaching", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        _mem = mem;
        _scanner = new Scanner(_mem) { PlausibleHealthFilter = _plausible.Checked };
        _freezer = new FreezeEngine(_mem);
        _entries.RemoveAll(e => e.Chain == null); // keep pointer-path entries across a game restart
        _results.VirtualListSize = 0;
        _attachBtn.Text = "Detach";
        _status.Text = $"Attached: {_mem.ProcessName} ({(_mem.Is64Bit ? "64" : "32")}-bit, {_mem.Modules.Count} modules)";
        Diag.Log($"Attached to {_mem.ProcessName} (pid {_mem.Pid}, {(_mem.Is64Bit ? "64" : "32")}-bit, {_mem.Modules.Count} modules).");
        _qsAttach.Text = $"Attached to {_mem.ProcessName}. Keep the game in Borderless or Windowed mode.";

        _exeFingerprint = _mem.ExeFingerprint();
        var prof = ProfileStore.Load(_mem.ProcessName);
        _autoBars = prof?.AutoBars ?? new();
        if (prof != null)
        {
            if (_entries.Count == 0) _entries.AddRange(prof.Entries);
            foreach (var kv in prof.Bars) _bars.TryAdd(kv.Key, kv.Value);
            foreach (var kv in prof.Slots) _slots.TryAdd(kv.Key, kv.Value);
            OnStatChanged();
            if (_bar.Cal != null) _barTol.Value = Math.Clamp(_bar.Cal.Tolerance, 5, 200);
            var ok = _entries.Count(e => e.Resolve(_mem) != null);
            _status.Text += $"  •  Profile loaded: {ok}/{_entries.Count} entries resolved";
            _gameUpdated = prof.ExeFingerprint != null && _exeFingerprint != null && prof.ExeFingerprint != _exeFingerprint;
            if (_gameUpdated) _status.Text += "  •  The game exe changed since this profile was saved (game update?): saved paths may need Auto-find again";
        }
        RebuildGrid();
        RegisterHotkeys();
        UpdateGuide();
        UpdateButtons();
        _tabs.SelectedTab = _autoPage;
        if (quiet) _autoStatus.Text = "Auto-attached. Switches turn on once the saved values are in memory (load into gameplay).";
        else BeginInvoke(new Action(AfterAttachAuto));
        return true;
    }

    /// <summary>Cancels auto-find and gives it a moment to undo its test writes while the game handle is still open.</summary>
    private void StopAutoFindAndWait()
    {
        _autoCts?.Cancel();
        try { _autoTask?.Wait(2000); } catch { /* cancelled or failed */ }
    }

    private void Detach(string why)
    {
        StopAutoFindAndWait();
        _assistant?.CancelGame();
        _assistant?.WaitForFinders(2000);
        _cts?.Cancel();
        _freezer?.Dispose(); _freezer = null;
        _autoNarrow.Checked = false;
        _scanner?.Dispose(); _scanner = null;
        _mem?.Dispose(); _mem = null;
        foreach (var e in _entries) e.Frozen = false;
        _gameUpdated = false;
        // Keep entries with pointer paths visible so the user can re-attach after a restart.
        _entries.RemoveAll(e => e.Chain == null);
        _results.VirtualListSize = 0;
        _attachBtn.Text = "Attach";
        _status.Text = why;
        Diag.Log(why);
        _qsAttach.Text = "Start the game in Borderless or Windowed mode and load into gameplay. Pick it in the Game list at the top and click Attach.";
        UnregisterHotkeys();
        RebuildGrid();
        UpdateGuide();
        UpdateButtons();
    }

    // ================================================================= scanning

    /// <summary>Index 0 is "Auto": scan Int32, Float and Double together.</summary>
    private ValueKind[] SelectedKinds => _kindCombo.SelectedIndex <= 0 ? ValueKindExt.HealthKinds : new[] { (ValueKind)_kindCombo.SelectedItem };
    private ScanType SelectedScanType => Enum.GetValues<ScanType>()[_scanCombo.SelectedIndex];

    private double ParseValueBox() => ParseNumber(_valueBox.Text);

    private static double ParseNumber(string text)
    {
        var t = (text ?? "").Trim();
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ||
            double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
        return double.NaN;
    }

    private async Task FirstScanAsync()
    {
        if (_mem == null) return;
        var type = SelectedScanType;
        if (!type.IsFirstScanType())
        {
            MessageBox.Show("For the first scan choose 'Exact value' (you can see the number), 'Unknown initial value' (only a bar), " +
                            "or Bigger than / Smaller than / Between.");
            return;
        }
        double v = ParseValueBox();
        if (type.NeedsValue() && double.IsNaN(v)) { MessageBox.Show("Type the health number you see in the game."); return; }
        if (!ApplyScanOptions(type)) return;
        var kinds = SelectedKinds;
        double tol = (double)_tolBox.Value;
        await RunBusy("Scanning…", (prog, ct) => _scanner.FirstScan(kinds, type, v, tol, prog, ct));
        if (type == ScanType.UnknownInitialValue) _scanCombo.SelectedIndex = (int)ScanType.Decreased;
    }

    private async Task NextScanAsync(ScanType type, double v, double tol)
    {
        if (_scanner == null || !_scanner.HasScan) return;
        if (type == ScanType.UnknownInitialValue) { MessageBox.Show("Pick a next-scan type such as Decreased."); return; }
        if (type.NeedsValue() && double.IsNaN(v)) { MessageBox.Show("Type the new health value."); return; }
        if (type.NeedsSecondValue())
        {
            var v2 = ParseNumber(_value2Box.Text);
            if (double.IsNaN(v2)) { MessageBox.Show("Type both ends of the range."); return; }
            _scanner.Value2 = v2;
        }
        await RunBusy("Narrowing…", (prog, ct) => _scanner.NextScan(type, v, tol, prog, ct));
    }

    private async Task RunBusy(string what, Action<IProgress<double>, CancellationToken> work)
    {
        if (_busy) return;
        _busy = true;
        _cts = new CancellationTokenSource();
        UpdateButtons();
        _countLbl.Text = what;
        var prog = new Progress<double>(p => _progress.Value = p);
        var sw = Stopwatch.StartNew();
        // Optional: freeze the game so values can't move mid-scan. Never used by Auto-find, which needs the game running.
        var mem = _mem;
        bool paused = _pauseChk.Checked && !_autoNarrow.Checked && mem != null && mem.Suspend();
        try
        {
            var ct = _cts.Token;
            await Task.Run(() => work(prog, ct));
            _countLbl.Text = $"Done in {sw.Elapsed.TotalSeconds:0.0}s";
            if (_scanner?.LastSummary is { Length: > 0 } sum) _status.Text = sum;
        }
        catch (OperationCanceledException)
        {
            if (_scanner?.HasScan == true) _countLbl.Text = "Cancelled: the previous results are kept.";
            else { _scanner?.Reset(); _countLbl.Text = "Cancelled — start a new scan."; }
        }
        catch (Exception ex) { _countLbl.Text = "Error: " + ex.Message; Diag.Log("Scan error: " + ex); }
        finally
        {
            if (paused) mem.Resume();
            _busy = false;
            _progress.Value = 0;
            RefreshResultsList();
            UpdateGuide();
            UpdateButtons();
        }
    }

    private void RefreshResultsList()
    {
        if (_scanner == null || !_scanner.HasScan || _scanner.InSnapshotMode) { _results.VirtualListSize = 0; }
        else _results.VirtualListSize = (int)Math.Min(_scanner.ResultCount, 20000);
        if (_scanner?.HasScan == true)
            _countLbl.Text += $"   •   {_scanner.ResultCount:N0} candidates after {_scanner.ScanCount} scan(s)" +
                              (_scanner.InSnapshotMode ? " (memory snapshot taken)" : "") +
                              (_scanner.MultiKind && !_scanner.InSnapshotMode ? $" — {_scanner.CountByKindText()}" : "") +
                              (_scanner.ResultCount > 20000 ? " — showing first 20,000" : "");
        _results.Invalidate();
    }

    private void Results_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
    {
        if (_autoRunning || _scanner == null || _mem == null || _scanner.InSnapshotMode || e.ItemIndex >= _scanner.ResultCount)
        {
            e.Item = new ListViewItem(new[] { "", "", "", "" });
            return;
        }
        long a = _scanner.AddressAt(e.ItemIndex);
        var k = _scanner.KindAt(e.ItemIndex);
        string cur = _mem.TryReadValue(a, k, out var v) ? k.Format(v) : "??";
        e.Item = new ListViewItem(new[] { FormatAddress(a), k.ToString(), cur, k.Format(_scanner.PreviousAt(e.ItemIndex)) });
    }

    private string FormatAddress(long a)
    {
        var m = _mem?.ModuleAt(a);
        return m != null ? $"{a:X}  ({m.Name}+{a - m.Base:X})" : a.ToString("X");
    }

    private IEnumerable<int> SelectedResultIndices() => _results.SelectedIndices.Cast<int>();

    private void SetSelectedValue()
    {
        if (_mem == null || _scanner == null) return;
        if (!double.TryParse(_setBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)) return;
        int n = 0;
        foreach (var i in SelectedResultIndices())
            if (_mem.WriteValue(_scanner.AddressAt(i), _scanner.KindAt(i), v)) n++;
        _countLbl.Text = $"Wrote {v} to {n} address(es). Check the game: did your health change and stay changed?";
    }

    private void AddSelectedToTable()
    {
        if (_mem == null || _scanner == null || _scanner.InSnapshotMode) return;
        var idx = SelectedResultIndices().ToList();
        if (idx.Count == 0) { MessageBox.Show("Select one or more results first."); return; }
        foreach (var i in idx)
        {
            long a = _scanner.AddressAt(i);
            var kind = _scanner.KindAt(i);
            _mem.TryReadValue(a, kind, out var cur);
            _entries.Add(new TableEntry
            {
                Name = idx.Count == 1 ? "Health" : $"Health? {a:X}",
                Kind = kind,
                RawAddress = a,
                MaxValue = cur > 0 ? cur : 100
            });
        }
        Diag.Log($"Added to table from manual scan: {string.Join(", ", idx.Take(10).Select(i => $"{_scanner.AddressAt(i):X} {_scanner.KindAt(i)}"))}");
        RebuildGrid();
        _tabs.SelectedTab = _tablePage;
    }

    // ================================================================= table / freeze / hotkeys

    private TableEntry SelectedEntry =>
        _grid.CurrentRow != null && _grid.CurrentRow.Index < _entries.Count ? _entries[_grid.CurrentRow.Index] : null;

    private bool _rebuilding;
    private void RebuildGrid()
    {
        _rebuilding = true;
        _grid.Rows.Clear();
        foreach (var e in _entries)
            _grid.Rows.Add(e.Frozen, e.Name, e.Group ?? "", "", e.Kind.Format(e.MaxValue), TableEntry.ModeLabel(e.Mode),
                           e.Multiplier.ToString("0.##"), HotkeySummary(e), e.Kind.ToString(),
                           e.Describe() + (e.MaxOffset is long mo ? $"   (max at {(mo < 0 ? "-" : "+")}{Math.Abs(mo):X})" : ""));
        _rebuilding = false;
        _entriesSnapshot = _entries.ToList();
        RebuildToggles();
    }

    private void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
    {
        if (_rebuilding || e.RowIndex < 0 || e.RowIndex >= _entries.Count) return;
        var entry = _entries[e.RowIndex];
        var cell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
        string col = _grid.Columns[e.ColumnIndex].Name;
        string text = cell.Value?.ToString() ?? "";
        switch (col)
        {
            case "Freeze":
                SetFrozen(entry, cell.Value is true);
                break;
            case "Mode":
                foreach (var fm in Enum.GetValues<FreezeMode>())
                    if (TableEntry.ModeLabel(fm) == text) entry.Mode = fm;
                if (entry.Frozen) SetFrozen(entry, true); // re-seed the held value for the new mode
                RebuildToggles();
                OnStatChanged();
                break;
            case "Name": entry.Name = text; break;
            case "Group": entry.Group = text.Trim(); break;
            case "Mult":
                if (double.TryParse(text, out var mult) && mult > 0 && mult <= 1000) entry.Multiplier = mult;
                RebuildToggles();
                break;
            case "Max":
                if (double.TryParse(text, out var m)) entry.MaxValue = m;
                break;
            case "Value":
                if (_mem != null && double.TryParse(text, out var v))
                {
                    var a = entry.Resolve(_mem);
                    if (a != null) _mem.WriteValue(a.Value, entry.Kind, v);
                    if (entry.Frozen) entry.FreezeValue = v;
                }
                break;
        }
    }

    private void RegisterHotkeys()
    {
        UnregisterHotkeys();
        if (_mem == null) return;
        uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        Native.RegisterHotKey(Handle, HkFreeze, mods, (uint)Keys.F);
        Native.RegisterHotKey(Handle, HkRefill, mods, (uint)Keys.R);
        Native.RegisterHotKey(Handle, HkLog, mods, (uint)Keys.L);
        RegisterEntryHotkeys();
    }

    private void UnregisterHotkeys()
    {
        Native.UnregisterHotKey(Handle, HkFreeze);
        Native.UnregisterHotKey(Handle, HkRefill);
        Native.UnregisterHotKey(Handle, HkLog);
        UnregisterEntryHotkeys();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && _mem != null)
        {
            switch ((int)m.WParam)
            {
                case HkFreeze:
                    bool any = _entries.Any(e => e.Frozen);
                    foreach (var e in _entries) SetFrozen(e, !any);
                    RebuildGrid();
                    Notify(any ? "All freezes OFF" : "All freezes ON", !any);
                    break;
                case HkRefill:
                    foreach (var e in _entries) Refill(e);
                    Notify("Refilled", true);
                    break;
                case HkLog:
                    ToggleLogging();
                    Notify(_logger.Active ? "Logging ON" : "Logging OFF", _logger.Active);
                    break;
                default:
                    OnEntryHotkey((int)m.WParam);
                    break;
            }
        }
        base.WndProc(ref m);
    }

    private void ToggleLogging()
    {
        if (_logger.Active) { _logger.Stop(); _logBtn.Text = "Start logging"; _status.Text = "Log saved: " + _logger.FilePath; return; }
        if (_mem == null) return;
        _logger.Start(_mem.ProcessName);
        foreach (var e in _entries) e.LastValue = double.NaN;
        _lastLoggedBar = double.NaN;
        _logBtn.Text = "Stop logging";
        _status.Text = "Logging to " + _logger.FilePath;
    }

    private void SaveProfile()
    {
        if (_mem == null) { MessageBox.Show("Attach to the game first."); return; }
        int raw = _entries.Count(e => e.Chain == null);
        var path = ProfileStore.Save(CurrentProfile());
        MessageBox.Show($"Saved to:\r\n{path}" + (raw > 0 ? $"\r\n\r\n{raw} entr{(raw == 1 ? "y has" : "ies have")} no pointer path yet and " +
                        "won't be saved — use the Pointer paths page so they work after a restart." : ""));
    }

    private void LoadProfileDialog()
    {
        using var dlg = new OpenFileDialog { InitialDirectory = ProfileStore.Folder, Filter = "Profiles|*.json" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var p = ProfileStore.LoadFile(dlg.FileName);
            _entries.AddRange(p.Entries);
            foreach (var kv in p.Bars) _bars[kv.Key] = kv.Value;
            foreach (var kv in p.Slots) _slots[kv.Key] = kv.Value;
            OnStatChanged();
            RebuildGrid();
            RegisterHotkeys();
        }
        catch (Exception ex) { MessageBox.Show("Could not load: " + ex.Message); }
    }

    // ================================================================= pointer paths

    private string ChainsFile => _mem == null ? null : Path.Combine(ProfileStore.Folder, _mem.ProcessName + ".paths.json");

    private async Task PointerScanAsync()
    {
        var e = SelectedEntry;
        if (_mem == null || e == null) { MessageBox.Show("Attach, then select an entry on the Cheat table page."); return; }
        var target = e.Resolve(_mem);
        if (target == null) { MessageBox.Show("That entry doesn't resolve to an address right now."); return; }
        if (!long.TryParse(_maxOff.Text, NumberStyles.HexNumber, null, out long maxOff)) maxOff = 0x1000;
        var ps = new PointerScanner { MaxDepth = (int)_depth.Value, MaxOffset = maxOff, MainModuleOnly = _mainOnlyChk.Checked };
        if (_lastOffBox.Text.Trim().Length > 0)
        {
            if (!long.TryParse(_lastOffBox.Text.Trim().Replace("0x", ""), NumberStyles.HexNumber, null, out long lo)) { MessageBox.Show("Last offset must be hex, e.g. 1C."); return; }
            ps.LastOffset = lo;
        }
        if (_busy) return;
        _busy = true; _cts = new CancellationTokenSource(); UpdateButtons();
        var status = new Progress<string>(s => _ptrStatus.Text = s);
        try
        {
            var ct = _cts.Token;
            var mem = _mem;
            long t = target.Value;
            _chains = await Task.Run(() => ps.Scan(mem, t, status, ct));
            File.WriteAllText(ChainsFile, JsonSerializer.Serialize(_chains));
            _ptrStatus.Text = $"{_chains.Count:N0} pointer paths found and saved. Now restart the game and narrow them down (see steps above).";
        }
        catch (OperationCanceledException) { _ptrStatus.Text = "Cancelled."; }
        catch (OutOfMemoryException) { _ptrStatus.Text = "Ran out of memory — lower Max levels or Max offset and try again."; }
        catch (Exception ex) { _ptrStatus.Text = "Error: " + ex.Message; }
        finally { _busy = false; UpdateButtons(); ShowChains(); }
    }

    private void ShowChains()
    {
        _chainList.BeginUpdate();
        _chainList.Items.Clear();
        foreach (var c in _chains.Take(5000))
        {
            string val = "";
            var e = SelectedEntry;
            if (_mem != null)
            {
                var a = c.Resolve(_mem);
                var kind = e?.Kind ?? ValueKind.Int32;
                val = a != null && _mem.TryReadValue(a.Value, kind, out var v) ? " = " + kind.Format(v) : " = ??";
            }
            _chainList.Items.Add(c + val);
        }
        _chainList.EndUpdate();
    }

    private void ValidateChains()
    {
        var e = SelectedEntry;
        if (_mem == null || e == null || _chains.Count == 0) { MessageBox.Show("Need: attached game, pointer paths, and a selected entry on the Cheat table page."); return; }
        var target = e.Resolve(_mem);
        if (target == null) return;
        int before = _chains.Count;
        _chains = PointerScanner.Validate(_mem, _chains, target.Value);
        File.WriteAllText(ChainsFile, JsonSerializer.Serialize(_chains));
        _ptrStatus.Text = $"{before:N0} → {_chains.Count:N0} paths still reach your health." +
                          (_chains.Count == 0 ? " None survived: rescan with more levels or a larger max offset." : "");
        ShowChains();
    }

    private void UseSelectedChain()
    {
        var e = SelectedEntry;
        int i = _chainList.SelectedIndex;
        if (e == null || i < 0 || i >= _chains.Count) { MessageBox.Show("Select a path here and an entry on the Cheat table page."); return; }
        e.Chain = _chains[i];
        e.RawAddress = 0;
        RebuildGrid();
        _ptrStatus.Text = $"'{e.Name}' now uses {e.Chain}. Save the profile on the Cheat table page.";
    }

    private void LoadSavedChains()
    {
        if (_mem == null || !File.Exists(ChainsFile)) { MessageBox.Show("No saved paths for this game yet."); return; }
        _chains = JsonSerializer.Deserialize<List<PointerChain>>(File.ReadAllText(ChainsFile)) ?? new();
        _ptrStatus.Text = $"Loaded {_chains.Count:N0} saved paths.";
        ShowChains();
    }

    // ================================================================= health bar

    private void CalibrateBar()
    {
        if (_bar.Cal == null) { MessageBox.Show("Select the bar on screen first."); return; }
        _bar.Calibrate(_bar.Region);
        _bar.Cal.Tolerance = (int)_barTol.Value;
        _lastBarPct = double.NaN;
        var c = Color.FromArgb(_bar.Cal.FillArgb);
        _status.Text = $"Color re-learned: R{c.R} G{c.G} B{c.B}. The live reading should say about 100% now.";
        SaveProfileQuiet();
    }

    private async Task AutoNarrowToggled()
    {
        if (!_autoNarrow.Checked) return;
        if (_mem == null || !_bar.IsCalibrated)
        {
            MessageBox.Show("Attach to the game and calibrate the bar first.");
            _autoNarrow.Checked = false;
            return;
        }
        _lastBarPct = double.NaN;
        if (_scanner != null && !_scanner.HasScan)
        {
            // Bar-only games: start from a snapshot of all memory.
            var kinds = SelectedKinds;
            await RunBusy("Taking memory snapshot…", (p, ct) => _scanner.FirstScan(kinds, ScanType.UnknownInitialValue, 0, 0, p, ct));
        }
    }

    private readonly Queue<double> _barRecent = new();

    private void BarTick()
    {
        if (!_bar.IsCalibrated)
        {
            _qsBarPct.Text = _barPct.Text = "–";
            _qsBarInfo.Text = _barInfo.Text = "No bar selected yet.";
            return;
        }
        bool visible = Visible && WindowState != FormWindowState.Minimized;
        bool barTabVisible = visible && _tabs.SelectedTab == _barPage;
        bool quickVisible = visible && _tabs.SelectedTab == _autoPage;
        if (!barTabVisible && !quickVisible && !_autoNarrow.Checked && !_logger.Active) return;

        double pct = _bar.Measure(out var bmp);
        if (barTabVisible) { var old = _barPreview.Image; _barPreview.Image = bmp; old?.Dispose(); }
        else bmp?.Dispose();
        if (double.IsNaN(pct)) return;

        var c = Color.FromArgb(_bar.Cal.FillArgb);
        string info = _bar.LastCaptureBlack
            ? "Screen capture is black: use Borderless/Windowed and turn HDR off."
            : $"color R{c.R} G{c.G} B{c.B}  ·  {_bar.LastMatchRatio * 100:0}% of pixels match";
        var statEntry = SelectedStatEntry;
        if (statEntry != null && _mem != null && statEntry.Resolve(_mem) is long sa)
            info += $"  ·  ≈ {statEntry.Kind.Format(pct * statEntry.CurrentMax(_mem, sa))} of {statEntry.Kind.Format(statEntry.CurrentMax(_mem, sa))}";
        _barPct.Text = _qsBarPct.Text = $"{pct * 100:0}%";
        _barInfo.Text = _qsBarInfo.Text = info;
        _barInfo.ForeColor = _qsBarInfo.ForeColor = _bar.LastCaptureBlack ? Theme.Warning : Theme.TextDim;
        _barGraph.Add(pct * 100);
        _qsBarGraph.Add(pct * 100);

        if (_logger.Active && (double.IsNaN(_lastLoggedBar) || Math.Abs(pct - _lastLoggedBar) >= 0.005))
        {
            _logger.Write("bar_percent", (pct * 100).ToString("0.0", CultureInfo.InvariantCulture));
            _lastLoggedBar = pct;
        }

        if (_autoNarrow.Checked && !_busy && _scanner?.HasScan == true)
        {
            // Wait for the bar to settle (damage animations, trailing "recent damage" segments) before scanning.
            _barRecent.Enqueue(pct);
            while (_barRecent.Count > 4) _barRecent.Dequeue();
            if (_barRecent.Count < 4 || _barRecent.Max() - _barRecent.Min() > 0.004) return;
            double settled = _barRecent.Average();
            if (double.IsNaN(_lastBarPct)) { _lastBarPct = settled; return; }
            double d = settled - _lastBarPct;
            if (Math.Abs(d) >= 0.02)
            {
                Diag.Log($"Auto-narrow: bar settled {_lastBarPct * 100:0.0}% → {settled * 100:0.0}%.");
                _lastBarPct = settled;
                _barRecent.Clear();
                var t = d < 0 ? ScanType.Decreased : ScanType.Increased;
                _ = NextScanAsync(t, 0, 0);
                if (_scanner.ResultCount is > 0 and <= 20 && !_scanner.InSnapshotMode)
                    _autoNarrow.Checked = false; // short list reached — hand over to the user
            }
        }
    }

    // ================================================================= periodic UI

    private void UiTick()
    {
        if (_mem != null && !_mem.IsAlive) { Detach("Game closed. Restart it, Refresh, and Attach again — pointer paths and the profile are kept."); return; }
        _tick++;
        if (_mem == null && _autoAttachChk.Checked && !_busy && _tick % 10 == 0) TryAutoAttach();
        if (_mem != null && _tick % 25 == 0 && RecheckAntiCheat()) return;

        try { BarTick(); } catch { /* capture can fail during mode switches */ }

        AssistantTick();
        if (_mem == null) return;
        _freezer?.Update(_entries); // pointer paths can move (e.g. after loading a save)
        UpdateAutoReadout();
        if (!_grid.IsCurrentCellInEditMode)
        {
            for (int i = 0; i < _entries.Count && i < _grid.Rows.Count; i++)
            {
                var e = _entries[i];
                var a = e.Resolve(_mem);
                string shown = "??";
                if (a != null && _mem.TryReadValue(a.Value, e.Kind, out var v))
                {
                    shown = e.Kind.Format(v);
                    if (_logger.Active && (double.IsNaN(e.LastValue) || v != e.LastValue))
                        _logger.Write(e.Name, shown);
                    e.LastValue = v;
                }
                _rebuilding = true;
                if ((string)_grid.Rows[i].Cells["Value"].Value != shown) _grid.Rows[i].Cells["Value"].Value = shown;
                if (_grid.Rows[i].Cells["Freeze"].Value is bool f && f != e.Frozen) _grid.Rows[i].Cells["Freeze"].Value = e.Frozen;
                _rebuilding = false;
            }
        }
        if (_tabs.SelectedTab == _findPage && _results.VirtualListSize > 0) _results.Invalidate();
        GraphTick();
        if (_tabs.SelectedTab == _memPage) RefreshMemoryView();
    }

    private void UpdateButtons()
    {
        bool att = _mem != null;
        _firstBtn.Enabled = att && !_busy;
        _nextBtn.Enabled = att && !_busy && _scanner?.HasScan == true;
        _undoBtn.Enabled = att && !_busy && _scanner?.CanUndo == true;
        _resetBtn.Enabled = att && !_busy;
        _cancelBtn.Enabled = _busy;
        _kindCombo.Enabled = !(_scanner?.HasScan ?? false);
        _ptrScanBtn.Enabled = att && !_busy;
        _attachBtn.Enabled = !_busy || _autoRunning;
        _autoStartBtn.Enabled = att && !_busy;
        _autoStopBtn.Enabled = _autoRunning;
        _autoRebarBtn.Enabled = !_autoRunning;
        _refillBtn.Enabled = att && SelectedStatEntry?.Resolve(_mem) != null;
        _selfCheckBtn.Enabled = !_busy || _autoRunning;
        UpdateSteps();
    }

    private void UpdateGuide()
    {
        if (_mem == null)
        {
            _guide.Text =
                "1   Attach\r\n\r\nStart your single-player game and load into gameplay. Pick it from the Game list at the top " +
                "(click Refresh if it isn't there) and click Attach.\r\n\r\nIf a profile was saved before, your entries and bar " +
                "settings load automatically.";
            return;
        }
        if (_scanner == null || !_scanner.HasScan)
        {
            _guide.Text =
                "2   First scan\r\n\r\nLeave Value type on Auto: it checks whole numbers and decimals (Int32, Float, Double) at once, " +
                "so you don't need to know how the game stores it.\r\n\r\n" +
                "Can you see the number?\r\n" +
                "•  Yes: Scan = Exact value, type the number, First scan.\r\n" +
                "•  No, only a bar: Scan = Unknown initial value, First scan. Or mark the bar on the Health bar page and tick Auto-narrow " +
                "so the scans run for you as you take hits.";
            return;
        }
        long n = _scanner.ResultCount;
        if (n == 0)
        {
            _guide.Text =
                "No candidates left\r\n\r\nCommon reasons:\r\n" +
                "•  The on-screen number is rounded or scaled. Use Unknown initial value with Decreased / Increased instead of exact numbers.\r\n" +
                "•  You ran Unchanged while health was regenerating.\r\n" +
                "•  You ran Decreased when it had actually gone up (or the other way round).\r\n\r\n" +
                "Click Undo to go back one step, or New scan to start over. The Diagnostics page lists every scan and how many results each type kept.";
            return;
        }
        if (_scanner.InSnapshotMode || n > 30)
        {
            _guide.Text =
                $"3   Narrow it down\r\n\r\n{n:N0} candidates. Go back to the game and change the value: take damage, or heal. Then:\r\n\r\n" +
                "•  Number visible: Scan = Exact value, type the new number, Next scan.\r\n" +
                "•  Bar only: Scan = Decreased or Increased, Next scan.\r\n\r\n" +
                "While it's steady, run Unchanged once or twice to throw out noise. 3 to 6 scans usually gets under 30.";
            return;
        }
        _guide.Text =
            $"4   Confirm ({n:N0} left)\r\n\r\nSelect a result, type a value next to 'Set selected to' and click it. Look at the game:\r\n\r\n" +
            "•  The value changed and stays changed: that's it. Click 'Add selected to cheat table'.\r\n" +
            "•  The display changed but snapped back, or nothing happened: it's a copy. Try the next one.\r\n\r\n" +
            "Then freeze or edit it on the Cheat table page, and find a pointer path so it works after a restart.";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        StopAutoFindAndWait();
        _assistant?.Stop();
        _assistant?.WaitForFinders(2500);
        _freezer?.Dispose();
        _logger.Dispose();
        UnregisterHotkeys();
        _padTimer.Stop();
        _overlay?.Dispose();
        _scanner?.Dispose();
        _mem?.Dispose();
        base.OnFormClosing(e);
    }
}
