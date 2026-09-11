using System.Drawing;
using System.Windows.Forms;
using AdfXplorer.Core;
using AdfXplorer.Core.FileSystems.Ofs;

namespace AdfXplorer.WinFsp;

/// <summary>
/// Asks the user Repair/Ignore/Reject for one checksum mismatch. <see cref="Show"/> matches
/// <see cref="ChecksumConflictHandler"/>'s signature exactly, so it can be passed directly wherever the
/// console-based prompt used to be.
/// </summary>
public sealed class ChecksumConflictWindow : Form
{
    private readonly Label _descriptionText = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), MaximumSize = new Size(440, 0) };
    private readonly Label _valuesText = new() { AutoSize = true, Font = new Font("Consolas", 9f), MaximumSize = new Size(440, 0), Margin = new Padding(0, 8, 0, 0) };
    private readonly Label _disclosureText = new() { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 8, 0, 0) };
    private readonly Button _repairButton = new() { Text = "Repair", Width = 90 };
    private readonly Button _ignoreButton = new() { Text = "Ignore", Width = 90 };
    private readonly Button _rejectButton = new() { Text = "Reject", Width = 90 };

    /// <summary>The user's choice, defaulting to <see cref="ChecksumDecision.Ignore"/> if the window is closed without a button click.</summary>
    public ChecksumDecision Result { get; private set; } = ChecksumDecision.Ignore;

    /// <summary>
    /// Two distinct modes share one window: a live single-block repair prompt, or (when
    /// <paramref name="blockDescription"/> ends with <see cref="OfsFileSystem.LaterEntryPolicyQuestion"/>)
    /// a one-time policy question asked while a whole volume is being scanned, where per-entry repair
    /// isn't possible so the Repair option is hidden and the other two are relabeled accordingly.
    /// </summary>
    public ChecksumConflictWindow(string blockDescription, uint stored, uint computed)
    {
        bool isPolicyQuestion = blockDescription.EndsWith(
            OfsFileSystem.LaterEntryPolicyQuestion, StringComparison.Ordinal);

        if (isPolicyQuestion)
        {
            Text = "Checksum Policy";
            _descriptionText.Text = $"Policy for {blockDescription}";
            _valuesText.Text = "No live repair is possible for entries found while a volume is being browsed.";
            _repairButton.Visible = false;
            _ignoreButton.Text = "Ignore mismatches";
            _rejectButton.Text = "Skip mismatched entries";
        }
        else
        {
            Text = "Checksum Mismatch";
            _descriptionText.Text = $"Checksum mismatch in {blockDescription}:";
            _valuesText.Text = $"stored=0x{stored:X8}   computed=0x{computed:X8}";
            _disclosureText.Text =
                "Repair recomputes the checksum from the block's CURRENT bytes and writes it back - it " +
                "fixes a stale/wrong checksum field, but cannot fix data that was corrupted independently " +
                "of the checksum, so repair may or may not actually resolve the issue.";
        }

        _repairButton.Click += (_, _) => Finish(ChecksumDecision.Repair);
        _ignoreButton.Click += (_, _) => Finish(ChecksumDecision.Ignore);
        _rejectButton.Click += (_, _) => Finish(ChecksumDecision.Reject);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Right, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.AddRange([_rejectButton, _ignoreButton, _repairButton]);

        var content = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        content.Controls.AddRange([_descriptionText, _valuesText, _disclosureText]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(content, 0, 0);
        root.Controls.Add(buttons, 0, 1);

        Padding = new Padding(16);
        Controls.Add(root);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(480, 0);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
    }

    /// <summary>
    /// Called from any thread (including WinFsp's own, mid-mount) - marshals onto the UI thread and
    /// blocks the caller until the user answers.
    /// </summary>
    public static ChecksumDecision Show(string blockDescription, uint stored, uint computed) =>
        UiThread.Invoke(() =>
        {
            using var window = new ChecksumConflictWindow(blockDescription, stored, computed);
            window.ShowDialog(UiThread.Owner);
            return window.Result;
        });

    private void Finish(ChecksumDecision result)
    {
        Result = result;
        Close();
    }
}
