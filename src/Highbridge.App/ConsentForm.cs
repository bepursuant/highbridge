using Highbridge.Consent;

namespace Highbridge.App;

/// <summary>One-time website approval dialog. Closing it any other way counts as "no".</summary>
internal sealed class ConsentForm : Form
{
    private ConsentForm(string heading, string origin, string details, string approveText, string denyText)
    {
        Text = "highbridge";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = new Font("Segoe UI", 9.5f);
        Icon = AppIcon.Window;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(460, 0),
        };

        layout.Controls.Add(new Label
        {
            Text = heading,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Regular),
            Margin = new Padding(0, 0, 0, 8),
        });
        layout.Controls.Add(new Label
        {
            Text = origin,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        });
        layout.Controls.Add(new Label
        {
            Text = details,
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Margin = new Padding(0, 0, 0, 12),
        });

        var approve = new Button { Text = approveText, AutoSize = true, MinimumSize = new Size(96, 30) };
        var deny = new Button { Text = denyText, AutoSize = true, MinimumSize = new Size(96, 30) };
        approve.Click += (_, _) =>
        {
            Approved = true;
            Close();
        };
        deny.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(deny);
        buttons.Controls.Add(approve);
        layout.Controls.Add(buttons);

        Controls.Add(layout);

        // Deliberately no AcceptButton: pressing Enter must not approve access.
        CancelButton = deny;
        Shown += (_, _) => deny.Focus();
    }

    public bool Approved { get; private set; }

    public static ConsentForm ForPairing(PairingRequest request) => new(
        "Allow this website to use your scanners and card reader?",
        request.Origin,
        "The site will be able to list your scanners and scan documents, and to read cards, whenever it asks, without asking you again. "
            + "A card read sends the last four digits of the card number, the expiry, and the cardholder name. "
            + "Scans it asks to stamp will include your Windows user name and computer name. "
            + "Only allow sites you trust. You can remove the site later from \"Approved websites\" in the highbridge tray menu.",
        "Allow",
        "Don't allow");
}
