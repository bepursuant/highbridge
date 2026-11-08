using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Highbridge.Cards;
using Highbridge.Support;

namespace Highbridge.App;

/// <summary>Popout that lists the scanners and card readers this PC can see.</summary>
internal sealed class DevicesForm : Form
{
    private readonly Func<Task<SupportSnapshot>> _load;
    private readonly string _logDirectory;
    private readonly Label _status;
    private readonly ListView _scanners;
    private readonly ListView _readers;
    private readonly Button _refresh;
    private readonly Button _copy;
    private int _generation;
    private SupportSnapshot? _latest;

    public DevicesForm(Func<Task<SupportSnapshot>> load, string logDirectory)
    {
        _load = load;
        _logDirectory = logDirectory;
        Text = "highbridge - devices";
        Icon = AppIcon.Window;
        Size = new Size(760, 520);
        MinimumSize = new Size(560, 380);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        _status = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Text = "Looking for scanners and card readers...",
        };

        _scanners = CreateList("Name", "Manufacturer", "Default", "Id");
        _readers = CreateList("Name", "Slot", "Id");

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

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(Heading("Scanners"), 0, 1);
        layout.Controls.Add(_scanners, 0, 2);
        layout.Controls.Add(Heading("Card readers"), 0, 3);
        layout.Controls.Add(_readers, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
        CancelButton = close;
        Shown += (_, _) => RefreshDevices();
    }

    public void RefreshDevices()
    {
        int generation = ++_generation;
        _refresh.Enabled = false;
        _status.Text = "Looking for scanners and card readers...";
        _ = LoadAsync(generation);
    }

    private async Task LoadAsync(int generation)
    {
        SupportSnapshot snapshot;
        try
        {
            snapshot = await _load().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (IsDisposed || generation != _generation)
            {
                return;
            }

            _status.Text = "Could not list devices. " + ex.Message;
            _refresh.Enabled = true;
            return;
        }

        if (IsDisposed || generation != _generation)
        {
            return;
        }

        Show(snapshot);
        _refresh.Enabled = true;
    }

    private void Show(SupportSnapshot snapshot)
    {
        _latest = snapshot;
        _copy.Enabled = true;
        if (snapshot.ScannerError is not null)
        {
            Fill(_scanners, [[snapshot.ScannerError, string.Empty, string.Empty, string.Empty]]);
        }
        else if (snapshot.Scanners.Count == 0)
        {
            Fill(_scanners, [["No scanners found.", string.Empty, string.Empty, string.Empty]]);
        }
        else
        {
            Fill(_scanners, Rows(snapshot));
        }

        if (snapshot.ReaderError is not null)
        {
            Fill(_readers, [[snapshot.ReaderError, string.Empty, string.Empty]]);
        }
        else if (snapshot.Readers.Count == 0)
        {
            Fill(_readers, [["No card readers found.", string.Empty, string.Empty]]);
        }
        else
        {
            Fill(_readers, snapshot.Readers
                .OrderBy(reader => CardReaderSlot.Describe(reader.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(reader => reader.Name, StringComparer.OrdinalIgnoreCase)
                .Select(reader => new[] { reader.Name, CardReaderSlot.Describe(reader.Name), reader.Id })
                .ToList());
        }

        string when = snapshot.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        string policy = snapshot.CardReaderEnabled ? "card reading on" : "card reading off by policy";
        string summary = $"highbridge {snapshot.Version}  ·  127.0.0.1:{snapshot.Port}  ·  {snapshot.ScannerBackend}, {snapshot.ReaderBackend}  ·  {policy}";
        string counts = $"Listed at {when}. {snapshot.Scanners.Count} scanner(s), {snapshot.Readers.Count} card reader(s).";
        string errors = string.Join(" ", new[] { snapshot.ScannerError, snapshot.ReaderError }.Where(error => error is not null));
        _status.Text = string.IsNullOrEmpty(errors) ? summary + Environment.NewLine + counts : summary + Environment.NewLine + counts + " " + errors;
    }

    private static List<string[]> Rows(SupportSnapshot snapshot) =>
        snapshot.Scanners
            .OrderByDescending(scanner => scanner.IsDefault)
            .ThenBy(scanner => scanner.Name, StringComparer.OrdinalIgnoreCase)
            .Select(scanner => new[] { scanner.Name, scanner.Manufacturer ?? string.Empty, scanner.IsDefault ? "Yes" : string.Empty, scanner.Id })
            .ToList();

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

    private static ListView CreateList(params string[] columns)
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
        };
        int width = columns.Length == 0 ? 120 : Math.Max(80, 680 / columns.Length);
        foreach (string column in columns)
        {
            list.Columns.Add(column, width);
        }

        return list;
    }

    private static void Fill(ListView list, IReadOnlyList<string[]> rows)
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (string[] row in rows)
        {
            var item = new ListViewItem(row[0]);
            for (int i = 1; i < row.Length; i++)
            {
                item.SubItems.Add(row[i]);
            }

            if (row[0].StartsWith("No ", StringComparison.Ordinal))
            {
                item.ForeColor = SystemColors.GrayText;
            }

            list.Items.Add(item);
        }

        list.EndUpdate();
    }

    private static Label Heading(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft };
}
