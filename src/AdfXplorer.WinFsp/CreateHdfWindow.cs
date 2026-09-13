using System.Globalization;
using System.IO;
using System.Windows.Forms;
using AdfXplorer.Core.DiskImage;

namespace AdfXplorer.WinFsp;

/// <summary>Asks for one or more partitions (drive name, size, DOS type, optional driver) when creating
/// a new RDB-partitioned .hdf. DOS type isn't limited to OFS/FFS - see <see cref="RigidDiskBlockWriter"/>.
/// </summary>
public sealed class CreateHdfWindow : Form
{
    /// <summary>A dropdown shortcut for a raw RDB DOS-type 4CC; the user can still type any 8-hex-digit value instead.</summary>
    private sealed record DosTypePreset(string Label, uint Value);

    /// <summary>
    /// The DOS-type 4CCs are the ASCII bytes "DOS\0"/"DOS\1" packed big-endian into a uint (per the RDB
    /// spec); SFS and PFS3 aren't natively understood by this project's filesystem readers but are still
    /// offered since a partition's DOS type only needs to match the driver embedded alongside it.
    /// </summary>
    private static readonly DosTypePreset[] DosTypePresets =
    [
        new("OFS (DOS\\0)", 0x444F5300),
        new("FFS (DOS\\1)", 0x444F5301),
        new("SFS/0 (0x53465300)", 0x53465300),
        new("PFS3 (0x50465303)", 0x50465303),
    ];

    /// <summary>The live controls for one partition's editable row, plus any driver file the user attached to it.</summary>
    private sealed class PartitionRow
    {
        public required FlowLayoutPanel RootPanel;
        public required TextBox DriveNameBox;
        public required TextBox SizeBox;
        public required ComboBox DosTypeBox;
        public required Label DriverLabel;
        public string? DriverPath;
    }

    private readonly List<PartitionRow> _rows = [];
    private readonly FlowLayoutPanel _partitionsPanel = new()
    {
        FlowDirection = FlowDirection.TopDown,
        Dock = DockStyle.Fill,
        WrapContents = false,
        AutoScroll = true,
    };

    /// <summary>The validated partition specs to hand to the RDB writer, in the order the user laid them out.</summary>
    public IReadOnlyList<HdfPartitionSpec> Partitions { get; private set; } = [];

    /// <summary>Starts with one partition row already present - an .hdf with zero partitions makes no sense.</summary>
    public CreateHdfWindow()
    {
        var addButton = new Button { Text = "Add partition", Width = 130, Margin = new Padding(0, 8, 0, 8) };
        addButton.Click += (_, _) => AddPartition();

        var okButton = new Button { Text = "OK", Width = 90, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        okButton.Click += Ok_Click;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Right };
        buttons.Controls.AddRange([cancelButton, okButton]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = "Partitions (drive name, size in MB, DOS type, optional filesystem driver):", AutoSize = true, Margin = new Padding(0, 0, 0, 8) }, 0, 0);
        root.Controls.Add(_partitionsPanel, 0, 1);
        var footer = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        footer.Controls.Add(addButton, 0, 0);
        footer.Controls.Add(buttons, 1, 0);
        root.Controls.Add(footer, 0, 2);

        Text = "Create HDF Disk Image";
        Padding = new Padding(16);
        Controls.Add(root);
        Width = 680;
        Height = 420;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AcceptButton = okButton;
        CancelButton = cancelButton;

        AddPartition();
    }

    /// <summary>
    /// Builds one partition row's controls entirely in code rather than a designer, since the row count
    /// is dynamic (add/remove) and every row needs the same wiring.
    /// </summary>
    private void AddPartition()
    {
        int index = _rows.Count + 1;

        var driveNameBox = new TextBox { Width = 80, Text = $"DH{index - 1}", Margin = new Padding(0, 4, 8, 4) };
        var sizeBox = new TextBox { Width = 60, Text = "100", Margin = new Padding(0, 4, 8, 4) };
        var dosTypeBox = new ComboBox
        {
            Width = 190,
            Margin = new Padding(0, 4, 8, 4),
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
        };
        dosTypeBox.Items.AddRange([.. DosTypePresets.Select(p => p.Label)]);
        dosTypeBox.Text = DosTypePresets[0].Label;
        var driverLabel = new Label { Text = "(no driver)", Width = 140, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 4, 8, 4) };
        var driverButton = new Button { Text = "Driver...", Width = 70, Margin = new Padding(0, 0, 8, 0) };
        var removeButton = new Button { Text = "✕", Width = 24 };

        var row = new PartitionRow
        {
            RootPanel = null!,
            DriveNameBox = driveNameBox,
            SizeBox = sizeBox,
            DosTypeBox = dosTypeBox,
            DriverLabel = driverLabel,
        };

        driverButton.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "Select filesystem driver binary" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                row.DriverPath = dialog.FileName;
                driverLabel.Text = Path.GetFileName(dialog.FileName);
            }
        };

        var rowPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
        };
        rowPanel.Controls.AddRange([driveNameBox, sizeBox, dosTypeBox, driverLabel, driverButton, removeButton]);
        row.RootPanel = rowPanel;

        removeButton.Click += (_, _) =>
        {
            if (_rows.Count <= 1)
            {
                return; // always keep at least one partition row
            }

            _partitionsPanel.Controls.Remove(rowPanel);
            _rows.Remove(row);
        };

        _rows.Add(row);
        _partitionsPanel.Controls.Add(rowPanel);
    }

    /// <summary>Validates every row before letting the OK button's own <see cref="DialogResult"/> close the form.</summary>
    private void Ok_Click(object? sender, EventArgs e)
    {
        var specs = new List<HdfPartitionSpec>();

        foreach (var row in _rows)
        {
            string driveName = row.DriveNameBox.Text.Trim();
            if (driveName.Length == 0)
            {
                ShowError("Every partition needs a drive name.");
                return;
            }

            if (!int.TryParse(row.SizeBox.Text.Trim(), out int sizeMb) || sizeMb <= 0)
            {
                ShowError($"'{driveName}': size must be a positive whole number of MB.");
                return;
            }

            string dosTypeText = row.DosTypeBox.Text.Trim();
            if (!TryParseDosType(dosTypeText, out uint dosType))
            {
                ShowError($"'{driveName}': DOS type '{dosTypeText}' isn't understood - pick a preset or enter an 8-digit hex value like 0x53465300.");
                return;
            }

            if (IsOfsOrFfsDosType(dosType) && sizeMb > 4096)
            {
                ShowError($"'{driveName}': OFS and FFS partitions cannot exceed 4096 MB (4 GiB).");
                return;
            }

            byte[]? driverImage = null;
            if (row.DriverPath is not null)
            {
                try
                {
                    driverImage = File.ReadAllBytes(row.DriverPath);
                }
                catch (IOException ex)
                {
                    ShowError($"'{driveName}': couldn't read driver file '{row.DriverPath}': {ex.Message}");
                    return;
                }
            }

            specs.Add(new HdfPartitionSpec(driveName, sizeMb, dosType, driverImage));
        }

        Partitions = specs;
    }

    /// <summary>Accepts either a preset label or a raw 8-hex-digit (optionally "0x"-prefixed) DOS-type value.</summary>
    private static bool TryParseDosType(string text, out uint dosType)
    {
        foreach (var preset in DosTypePresets)
        {
            if (string.Equals(preset.Label, text, StringComparison.OrdinalIgnoreCase))
            {
                dosType = preset.Value;
                return true;
            }
        }

        string hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out dosType))
        {
            return true;
        }

        dosType = 0;
        return false;
    }

    private static bool IsOfsOrFfsDosType(uint dosType) =>
        dosType is >= 0x444F5300 and <= 0x444F5305;

    /// <summary>Shows the error and cancels the OK button's pending close.</summary>
    private void ShowError(string message)
    {
        MessageBox.Show(this, message, "AdfXplorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        DialogResult = DialogResult.None;
    }
}
