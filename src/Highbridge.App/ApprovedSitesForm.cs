using System.Globalization;
using Highbridge.Security;

namespace Highbridge.App;

/// <summary>Lists approved origins and lets the user revoke them.</summary>
internal sealed class ApprovedSitesForm : Form
{
    private readonly OriginRegistry _origins;
    private readonly ListView _list;
    private readonly Button _remove;

    public ApprovedSitesForm(OriginRegistry origins)
    {
        _origins = origins;
        Text = "highbridge - approved websites";
        Icon = AppIcon.Window;
        Size = new Size(620, 360);
        MinimumSize = new Size(480, 260);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
        };
        _list.Columns.Add("Website", 380);
        _list.Columns.Add("Approved", 180);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        _remove = new Button { Text = "Remove", AutoSize = true, Enabled = false };
        _remove.Click += (_, _) => RemoveSelected();
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(_remove);

        Controls.Add(_list);
        Controls.Add(buttons);
        CancelButton = close;
        Reload();
    }

    private void Reload()
    {
        _list.Items.Clear();
        foreach (string origin in _origins.PolicyOrigins.Order(StringComparer.Ordinal))
        {
            _list.Items.Add(new ListViewItem([origin, "by administrator"]) { Tag = null });
        }

        foreach (var paired in _origins.ListUserPairings().Where(p => !_origins.IsPolicyOrigin(p.Origin)).OrderBy(p => p.Origin, StringComparer.Ordinal))
        {
            string when = paired.PairedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            _list.Items.Add(new ListViewItem([paired.Origin, when]) { Tag = paired.Origin });
        }

        if (_list.Items.Count == 0)
        {
            _list.Items.Add(new ListViewItem(["No websites approved yet.", string.Empty]) { ForeColor = SystemColors.GrayText });
        }

        UpdateButtons();
    }

    private void UpdateButtons() =>
        _remove.Enabled = _list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is string;

    private void RemoveSelected()
    {
        if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is string origin)
        {
            _origins.Unpair(origin);
            Reload();
        }
    }
}
