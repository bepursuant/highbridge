using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Highbridge.Configuration;
using Highbridge.Server;
using Highbridge.Support;

namespace Highbridge.App;

/// <summary>
/// One tab per device module. The list is the physical devices; the grid is that module's options,
/// saved as the defaults a page uses when it leaves a field out.
/// </summary>
internal sealed class DevicesForm : Form
{
    private readonly Func<Task<SupportSnapshot>> _load;
    private readonly Func<IDeviceModule, CancellationToken, Task<IReadOnlyList<DeviceListing>>> _list;
    private readonly SettingsStore _settings;
    private readonly string _logDirectory;
    private readonly IReadOnlyList<IDeviceModule> _modules;
    private readonly Label _status;
    private readonly Button _refresh;
    private readonly Button _copy;
    private readonly TabControl _tabs;
    private int _generation;
    private bool _loadingGrid;
    private SupportSnapshot? _latest;

    public DevicesForm(
        Func<Task<SupportSnapshot>> load,
        Func<IDeviceModule, CancellationToken, Task<IReadOnlyList<DeviceListing>>> list,
        SettingsStore settings,
        string logDirectory,
        IReadOnlyList<IDeviceModule> modules)
    {
        _load = load;
        _list = list;
        _settings = settings;
        _logDirectory = logDirectory;
        _modules = modules;
        Text = "highbridge - devices";
        Icon = AppIcon.Window;
        Size = new Size(860, 560);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        _status = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Text = "Looking for devices...",
        };

        _tabs = new TabControl { Dock = DockStyle.Fill };
        foreach (var module in modules)
        {
            _tabs.TabPages.Add(CreateTab(module));
        }

        _refresh = new Button { Text = "Refresh", AutoSize = true };
        _refresh.Click += (_, _) => RefreshDevices();
        _copy = new Button { Text = "Copy summary", AutoSize = true, Enabled = false };
        _copy.Click += (_, _) => CopySummary();
        var logs = new Button { Text = "Open log folder", AutoSize = true };
        logs.Click += (_, _) => OpenLogFolder();
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(logs);
        buttons.Controls.Add(_copy);
        buttons.Controls.Add(_refresh);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(_tabs, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);
        CancelButton = close;
        Shown += (_, _) => RefreshDevices();
    }

    public void RefreshDevices()
    {
        int generation = ++_generation;
        _refresh.Enabled = false;
        _status.Text = "Looking for devices...";
        _ = LoadAsync(generation);
    }

    private TabPage CreateTab(IDeviceModule module)
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
            HideSelection = false,
        };
        list.Columns.Add("Name", 220);
        list.Columns.Add("Detail", 160);
        var grid = new PropertyGrid
        {
            Dock = DockStyle.Fill,
            HelpVisible = true,
            ToolbarVisible = false,
            PropertySort = PropertySort.CategorizedAlphabetical,
        };
        grid.PropertyValueChanged += (_, _) => SaveGrid(module, list, grid);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 400,
        };
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(grid);

        var page = new TabPage(module.Title) { Tag = module };
        page.Controls.Add(split);
        list.SelectedIndexChanged += (_, _) => BindGrid(module, list, grid);
        return page;
    }

    private async Task LoadAsync(int generation)
    {
        SupportSnapshot? snapshot = null;
        string? snapshotError = null;
        try
        {
            snapshot = await _load().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            snapshotError = ex.Message;
        }

        if (IsDisposed || generation != _generation)
        {
            return;
        }

        var listings = new Dictionary<IDeviceModule, IReadOnlyList<DeviceListing>>();
        var errors = new List<string>();
        if (snapshotError is not null)
        {
            errors.Add(snapshotError);
        }

        foreach (var module in _modules)
        {
            try
            {
                listings[module] = await _list(module, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                listings[module] = [];
                errors.Add(ex.Message);
            }

            if (IsDisposed || generation != _generation)
            {
                return;
            }
        }

        _latest = snapshot;
        _copy.Enabled = snapshot is not null;
        ShowListings(listings);
        _status.Text = StatusText(snapshot, listings, errors);
        _refresh.Enabled = true;
    }

    private void ShowListings(Dictionary<IDeviceModule, IReadOnlyList<DeviceListing>> listings)
    {
        foreach (TabPage page in _tabs.TabPages)
        {
            var module = (IDeviceModule)page.Tag!;
            var list = FindList(page);
            var devices = listings.GetValueOrDefault(module) ?? [];
            string? selected = list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as string : null;
            list.BeginUpdate();
            list.Items.Clear();
            if (devices.Count == 0)
            {
                var empty = new ListViewItem($"No {module.Title.ToLowerInvariant()}s found.") { ForeColor = SystemColors.GrayText };
                empty.SubItems.Add(string.Empty);
                list.Items.Add(empty);
            }
            else
            {
                foreach (var device in devices)
                {
                    var item = new ListViewItem(device.Name) { Tag = device.Id };
                    item.SubItems.Add(device.Detail ?? string.Empty);
                    list.Items.Add(item);
                    if (device.Id == selected)
                    {
                        item.Selected = true;
                    }
                }
            }

            list.EndUpdate();
            if (list.SelectedItems.Count == 0 && list.Items.Count > 0 && list.Items[0].Tag is string)
            {
                list.Items[0].Selected = true;
            }
        }
    }

    private void BindGrid(IDeviceModule module, ListView list, PropertyGrid grid)
    {
        if (list.SelectedItems.Count != 1 || list.SelectedItems[0].Tag is not string id)
        {
            grid.SelectedObject = null;
            return;
        }

        object options;
        try
        {
            var stored = _settings.DeviceOptions(module.Key, id);
            options = stored?.Deserialize(module.OptionsType, HighbridgeJson.Options) ?? module.CreateOptions();
        }
        catch (JsonException)
        {
            options = module.CreateOptions();
        }

        module.PrepareOptions(options, new DeviceListing(id, list.SelectedItems[0].Text, null));
        _loadingGrid = true;
        grid.SelectedObject = options;
        grid.Tag = id;
        _loadingGrid = false;
    }

    private void SaveGrid(IDeviceModule module, ListView list, PropertyGrid grid)
    {
        if (_loadingGrid || grid.SelectedObject is null || grid.Tag is not string id)
        {
            return;
        }

        var node = JsonSerializer.SerializeToNode(grid.SelectedObject, module.OptionsType, HighbridgeJson.Options) as JsonObject;
        if (node is null)
        {
            return;
        }

        _settings.SaveDeviceOptions(module.Key, id, node);
        _ = list;
    }

    private static string StatusText(
        SupportSnapshot? snapshot,
        Dictionary<IDeviceModule, IReadOnlyList<DeviceListing>> listings,
        IReadOnlyList<string> errors)
    {
        string counts = string.Join(", ", listings.Select(pair => $"{pair.Value.Count} {pair.Key.Title.ToLowerInvariant()}(s)"));
        if (snapshot is null)
        {
            string failed = errors.Count == 0 ? "Could not list devices." : string.Join(" ", errors);
            return failed + (counts.Length == 0 ? string.Empty : Environment.NewLine + counts);
        }

        string when = snapshot.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        string policy = snapshot.CardReaderEnabled ? "card reading on" : "card reading off by policy";
        string summary = $"highbridge {snapshot.Version}  ·  127.0.0.1:{snapshot.Port}  ·  {snapshot.ScannerBackend}, {snapshot.ReaderBackend}  ·  {policy}";
        string line = $"Listed at {when}. {counts}. Select a device to edit the defaults a page inherits.";
        return errors.Count == 0 ? summary + Environment.NewLine + line : summary + Environment.NewLine + line + " " + string.Join(" ", errors);
    }

    private void CopySummary()
    {
        if (_latest is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(SupportReport.Format(_latest));
            _status.Text = "Support summary copied. It lists device names only.";
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            _status.Text = "Could not copy the summary.";
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_logDirectory) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _status.Text = "Could not open the log folder.";
        }
    }

    private static ListView FindList(TabPage page) =>
        page.Controls.OfType<SplitContainer>().First().Panel1.Controls.OfType<ListView>().First();
}
