using System.Windows.Forms;
using AdfXplorer.Core.FileSystems;

namespace AdfXplorer.WinFsp;

/// <summary>A selectable floppy capacity; <see cref="ToString"/> drives the combo box's display text.</summary>
public sealed record AdfSizeOption(string Label, int SectorCount)
{
    public override string ToString() => Label;
}

/// <summary>Asks for size, filesystem, and volume label when creating a new blank .adf.</summary>
public sealed class CreateAdfWindow : Form
{
    /// <summary>Only the two real Amiga floppy geometries - a blank .adf can't be any other size.</summary>
    private static readonly AdfSizeOption[] SizeOptions =
    [
        new("880 KB (DD floppy)", 1760),
        new("1.76 MB (HD floppy)", 3520),
    ];

    private readonly ComboBox _sizeCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly ComboBox _fileSystemCombo = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 320,
        DisplayMember = nameof(AmigaFileSystemWriter.DisplayName),
    };
    private readonly TextBox _volumeLabelBox = new() { MaxLength = 30, Width = 320 };

    /// <summary>Chosen floppy size, in 512-byte sectors.</summary>
    public int SectorCount { get; private set; }
    /// <summary>Chosen filesystem writer (OFS/FFS) to format the new volume with.</summary>
    public AmigaFileSystemWriter Writer { get; private set; } = null!;
    /// <summary>Chosen volume label, defaulting to "Empty" (AmigaDOS's own convention for an unlabeled disk) if left blank.</summary>
    public string VolumeLabel { get; private set; } = "";

    /// <summary>Seeds the size/filesystem combos and pre-fills the label with a caller-supplied default.</summary>
    public CreateAdfWindow(string defaultVolumeLabel)
    {
        _sizeCombo.Items.AddRange(SizeOptions);
        _sizeCombo.SelectedIndex = 0;

        _fileSystemCombo.Items.AddRange([.. AmigaFileSystemWriters.All]);
        _fileSystemCombo.SelectedIndex = 0;

        _volumeLabelBox.Text = defaultVolumeLabel;

        var okButton = new Button { Text = "OK", Width = 90, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        okButton.Click += Ok_Click;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Right };
        buttons.Controls.AddRange([cancelButton, okButton]);

        var content = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        content.Controls.AddRange(
        [
            new Label { Text = "Size:", AutoSize = true },
            _sizeCombo,
            new Label { Text = "Filesystem:", AutoSize = true, Margin = new Padding(0, 8, 0, 0) },
            _fileSystemCombo,
            new Label { Text = "Volume label:", AutoSize = true, Margin = new Padding(0, 8, 0, 0) },
            _volumeLabelBox,
        ]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(content, 0, 0);
        root.Controls.Add(buttons, 0, 1);

        Text = "Create ADF Disk Image";
        Padding = new Padding(16);
        Controls.Add(root);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    /// <summary>
    /// Validates the combo selections before letting the OK button's own <see cref="DialogResult"/>
    /// close the form - setting it back to <see cref="DialogResult.None"/> cancels that close.
    /// </summary>
    private void Ok_Click(object? sender, EventArgs e)
    {
        if (_sizeCombo.SelectedItem is not AdfSizeOption size || _fileSystemCombo.SelectedItem is not AmigaFileSystemWriter writer)
        {
            DialogResult = DialogResult.None;
            return;
        }

        SectorCount = size.SectorCount;
        Writer = writer;
        VolumeLabel = string.IsNullOrWhiteSpace(_volumeLabelBox.Text) ? "Empty" : _volumeLabelBox.Text.Trim();
    }
}
