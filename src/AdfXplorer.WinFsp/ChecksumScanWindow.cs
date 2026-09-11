using System.Windows.Forms;

namespace AdfXplorer.WinFsp;

/// <summary>One reported block's outcome, pre-formatted as display strings so the list view needs no converters.</summary>
public sealed record ChecksumRow(string Description, string Status, string Stored, string Computed);

/// <summary>Shows the final results of a validate/repair scan.</summary>
public sealed class ChecksumScanWindow : Form
{
    /// <summary>Populates the grid and summary line once, after the scan has already fully run.</summary>
    public ChecksumScanWindow(IReadOnlyList<ChecksumRow> rows, string summary)
    {
        var resultsList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
        };
        resultsList.Columns.Add("Block", 380);
        resultsList.Columns.Add("Status", 90);
        resultsList.Columns.Add("Stored", 100);
        resultsList.Columns.Add("Computed", 100);
        foreach (var row in rows)
        {
            resultsList.Items.Add(new ListViewItem([row.Description, row.Status, row.Stored, row.Computed]));
        }

        var closeButton = new Button { Text = "Close", Width = 90, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Right };
        buttons.Controls.Add(closeButton);

        var summaryText = new Label { Text = summary, AutoSize = true, Anchor = AnchorStyles.Left };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(resultsList, 0, 0);
        root.Controls.Add(summaryText, 0, 1);
        root.Controls.Add(buttons, 0, 2);

        Text = "Checksum Scan Results";
        Padding = new Padding(12);
        Controls.Add(root);
        Width = 700;
        Height = 480;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        CancelButton = closeButton;
    }
}
