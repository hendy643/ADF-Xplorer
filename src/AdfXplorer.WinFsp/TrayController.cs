using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using AdfXplorer.Core;
using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems;
using WinFsp.Native;

namespace AdfXplorer.WinFsp;

/// <summary>
/// Everything AdfXplorer does hangs off this one persistent tray icon: mounting, unmounting, creating
/// new ADF/HDF images, and validate/repair - there is no other UI surface (no window, no right-click
/// integration on drives; see Package.wxs for the one piece of Explorer integration that does exist, the
/// .adf/.hdf file association). Each menu action runs on a background <see cref="Task"/> (so a big HDF's
/// checksum scan doesn't freeze the tray icon), and shows its dialogs by marshaling onto the UI thread
/// via <see cref="UiThread"/>; only <see cref="RebuildMenu"/> itself (which touches the
/// <see cref="ContextMenuStrip"/>) needs the UI thread directly.
/// </summary>
internal sealed class TrayController : IDisposable
{
    /// <summary>One live WinFsp mount, tracked so it can be listed/unmounted later and cleaned up on exit.</summary>
    private sealed record MountedVolume(FileSystemHost Host, string SourcePath, string MountPoint);

    private readonly Action _exit;
    private readonly NotifyIcon _notifyIcon;
    private readonly List<MountedVolume> _mounted = [];

    /// <summary>
    /// Creates and shows the tray icon, using the exe's own embedded icon resource (set via
    /// <c>ApplicationIcon</c> in the csproj) rather than a separate loose .ico file next to it - the
    /// installer only ships the exe itself, so a side-by-side file would be missing after a real
    /// install even though it's present when running straight from the build/publish output.
    /// </summary>
    public TrayController(Action exit)
    {
        _exit = exit;

        _notifyIcon = new NotifyIcon
        {
            Text = "AdfXplorer",
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Visible = true,
        };

        RebuildMenu();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    // --- external requests (double-clicked file) ---

    /// <summary>
    /// Entry point for a mount request forwarded from a second process launch (see
    /// <see cref="SingleInstance"/>) or the initial launch's own argv - always a double-clicked
    /// .adf/.hdf, since that's the file association's whole command line (see Package.wxs).
    /// </summary>
    public void HandleExternalRequest(string[] args)
    {
        if (args is ["mount", var path])
        {
            _ = Task.Run(() => Mount(path));
        }
    }

    // --- menu ---

    /// <summary>
    /// Rebuilds the whole tray menu from scratch (rather than patching it) since the list of mounted
    /// volumes changes rarely and a full rebuild is far simpler than tracking incremental menu edits.
    /// Must run on the UI thread - callers that aren't already on it go through <see cref="UiThread"/>.
    /// </summary>
    private void RebuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Mount...", null, (_, _) => _ = Task.Run(MountViaPicker));
        menu.Items.Add("New ADF disk image...", null, (_, _) => _ = Task.Run(CreateAdfViaPicker));
        menu.Items.Add("New HDF disk image...", null, (_, _) => _ = Task.Run(CreateHdfViaPicker));
        menu.Items.Add("Validate...", null, (_, _) => _ = Task.Run(() => ValidateViaPicker(interactive: false)));
        menu.Items.Add("Repair...", null, (_, _) => _ = Task.Run(() => ValidateViaPicker(interactive: true)));

        menu.Items.Add(new ToolStripSeparator());

        if (_mounted.Count == 0)
        {
            menu.Items.Add("(nothing mounted)").Enabled = false;
        }
        else
        {
            foreach (var volume in _mounted)
            {
                menu.Items.Add(
                    $"Unmount {volume.MountPoint} ({Path.GetFileName(volume.SourcePath)})",
                    null,
                    (_, _) => _ = Task.Run(() => UnmountVolume(volume)));
            }
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        var old = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    /// <summary>Unmounts everything before shutting down so Windows doesn't end up with dangling drive letters after the process exits.</summary>
    private void Exit()
    {
        foreach (var volume in _mounted.ToList())
        {
            TryUnmount(volume);
        }

        _exit();
    }

    private void ShowError(string message) =>
        UiThread.Invoke(() => MessageBox.Show(UiThread.Owner, message, "AdfXplorer", MessageBoxButtons.OK, MessageBoxIcon.Warning));

    // --- mount / unmount ---

    private void MountViaPicker()
    {
        string? path = PickOpenPath("Mount disk image");
        if (path is not null)
        {
            Mount(path);
        }
    }

    /// <summary>
    /// Mounts either a single-filesystem image or, if an RDB partition table is present, every partition
    /// in it - one drive letter each. Saves the image once at the end if any checksum repair happened
    /// along the way, regardless of which path was taken.
    /// </summary>
    private void Mount(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            ShowError($"File not found: {sourcePath}");
            return;
        }

        var image = AdfImage.FromFile(sourcePath);
        var (partitions, repaired) = RigidDiskBlock.TryReadPartitions(image, ChecksumConflictWindow.Show);

        bool anyRepaired = repaired;
        if (partitions is null)
        {
            anyRepaired |= MountSingle(sourcePath, image);
        }
        else if (partitions.Count == 0)
        {
            ShowError("An RDB partition table was found but it lists no partitions.");
        }
        else
        {
            anyRepaired |= MountAllPartitions(sourcePath, image, partitions);
        }

        if (anyRepaired)
        {
            image.SaveTo(sourcePath);
        }
    }

    private bool MountSingle(string sourcePath, AdfImage image)
    {
        string? mountPoint = PickFreeDriveLetter([]);
        if (mountPoint is null)
        {
            ShowError("No free drive letters available.");
            return false;
        }

        string label = $"'{sourcePath}'";
        var (fs, repaired) = MountFileSystemWithChecksums(image, label);
        if (fs is not null)
        {
            MountOne(sourcePath, image, fs, image.SizeInBytes, mountPoint, label);
        }

        return repaired;
    }

    private bool MountAllPartitions(string sourcePath, AdfImage image, IReadOnlyList<RdbPartition> partitions)
    {
        var claimedLetters = new HashSet<char>();
        bool repaired = false;
        bool mountedAny = false;

        foreach (var part in partitions)
        {
            var window = image.CreateWindow(part.StartBlock, part.BlockCount);
            string label = $"partition '{part.DriveName}' of '{sourcePath}'";

            var (fs, partRepaired) = MountFileSystemWithChecksums(window, label);
            repaired |= partRepaired;
            if (fs is null)
            {
                continue;
            }

            var driveLetter = PickFreeDriveLetter(claimedLetters);
            if (driveLetter is null)
            {
                ShowError($"No free drive letters left for partition '{part.DriveName}'.");
                continue;
            }

            claimedLetters.Add(driveLetter[0]);

            if (MountOne(sourcePath, image, fs, window.SizeInBytes, driveLetter, label))
            {
                mountedAny = true;
            }
        }

        if (!mountedAny)
        {
            ShowError("No partitions could be mounted.");
        }

        return repaired;
    }

    /// <summary>
    /// Mounts via the registry (unchanged, still throws/reports <see cref="NotSupportedException"/> on
    /// no match), then - if the result implements <see cref="IChecksumAware"/> - validates its
    /// foundational blocks, showing <see cref="ChecksumConflictWindow"/> for any mismatch, with
    /// descriptions prefixed by <paramref name="label"/>.
    /// </summary>
    private (IAmigaFileSystem? Fs, bool Repaired) MountFileSystemWithChecksums(AdfImage image, string label)
    {
        IAmigaFileSystem fs;
        try
        {
            fs = AmigaFileSystemRegistry.CreateDefault().Mount(image);
        }
        catch (NotSupportedException ex)
        {
            ShowError($"{label}: {ex.Message}");
            return (null, false);
        }

        bool repaired = false;
        if (fs is IChecksumAware aware)
        {
            ChecksumDecision Prompt(string desc, uint stored, uint computed) =>
                ChecksumConflictWindow.Show($"{label}: {desc}", stored, computed);

            if (!aware.ValidateChecksums(Prompt, out repaired))
            {
                ShowError($"Rejected a required block for {label}; not mounting.");
                return (null, repaired);
            }
        }

        return (fs, repaired);
    }

    /// <summary>
    /// Does the actual WinFsp mount for one already-resolved filesystem, tracks it for later
    /// unmount/listing, and opens it in Explorer as a convenience (best-effort - failure here doesn't
    /// undo an otherwise-successful mount).
    /// </summary>
    private bool MountOne(
        string sourcePath, AdfImage topLevelImage, IAmigaFileSystem fs, long sizeBytes, string mountPoint,
        string label)
    {
        var adfFs = new AdfFileSystem(fs, topLevelImage, sourcePath, sizeBytes);
        var host = new FileSystemHost(adfFs);
        var status = host.Mount(mountPoint, true);
        if (status != NtStatus.Success)
        {
            ShowError(
                $"Failed to mount {label} (status 0x{status:X8}). Is the WinFsp service installed and " +
                "running? See https://winfsp.dev.");
            return false;
        }

        _mounted.Add(new MountedVolume(host, Path.GetFullPath(sourcePath), mountPoint));
        UiThread.Invoke(RebuildMenu);

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", mountPoint) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // best-effort auto-open; the mount itself succeeded regardless.
        }

        return true;
    }

    private void UnmountVolume(MountedVolume volume)
    {
        TryUnmount(volume);
        UiThread.Invoke(RebuildMenu);
    }

    private void TryUnmount(MountedVolume volume)
    {
        try
        {
            volume.Host.Unmount();
        }
        catch (Exception)
        {
            // best-effort - it may already be gone (e.g. the underlying drive was force-removed).
        }

        _mounted.Remove(volume);
    }

    /// <summary>
    /// Picks the highest free drive letter Z-D (skipping A/B/C, conventionally floppy/system drives) so
    /// concurrently mounting several partitions in one call doesn't race against Windows re-enumerating
    /// drives between picks - <paramref name="alsoExclude"/> carries letters already claimed earlier in
    /// that same batch, which <see cref="DriveInfo.GetDrives"/> wouldn't see yet.
    /// </summary>
    private static string? PickFreeDriveLetter(HashSet<char> alsoExclude)
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'D'; c--)
        {
            if (!used.Contains(c) && !alsoExclude.Contains(c))
            {
                return $"{c}:";
            }
        }

        return null;
    }

    // --- create new ADF/HDF ---

    private void CreateAdfViaPicker()
    {
        string? targetPath = PickSavePath("New ADF disk image.adf", "ADF disk image", "adf");
        if (targetPath is null)
        {
            return;
        }

        string defaultLabel = Path.GetFileNameWithoutExtension(targetPath);
        if (defaultLabel.Length > 30)
        {
            defaultLabel = defaultLabel[..30];
        }

        // The whole dialog interaction (construction, ShowDialog, and reading its result properties back
        // out) has to happen on the UI thread as one unit - Form/Control objects aren't safe to touch
        // from a background thread even after they're closed.
        var (result, sectorCount, writer, volumeLabel) = UiThread.Invoke(() =>
        {
            using var dialog = new CreateAdfWindow(defaultLabel);
            var result = dialog.ShowDialog(UiThread.Owner);
            return (result, dialog.SectorCount, dialog.Writer, dialog.VolumeLabel);
        });
        if (result != DialogResult.OK)
        {
            return;
        }

        try
        {
            var image = writer.Create(sectorCount, volumeLabel);
            image.SaveTo(targetPath);
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't create '{targetPath}': {ex.Message}");
        }
    }

    private void CreateHdfViaPicker()
    {
        string? targetPath = PickSavePath("New HDF disk image.hdf", "HDF disk image", "hdf");
        if (targetPath is null)
        {
            return;
        }

        var (result, partitions) = UiThread.Invoke(() =>
        {
            using var dialog = new CreateHdfWindow();
            var result = dialog.ShowDialog(UiThread.Owner);
            return (result, dialog.Partitions);
        });
        if (result != DialogResult.OK)
        {
            return;
        }

        try
        {
            var image = RigidDiskBlockWriter.Create(partitions);
            image.SaveTo(targetPath);
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't create '{targetPath}': {ex.Message}");
        }
    }

    private static string? PickSavePath(string suggestedName, string typeLabel, string extension) =>
        UiThread.Invoke(() =>
        {
            using var dialog = new SaveFileDialog();
            dialog.Title = $"New {typeLabel}";
            dialog.FileName = suggestedName;
            dialog.DefaultExt = extension;
            dialog.AddExtension = true;
            dialog.Filter = $"{typeLabel}|*.{extension}";
            return dialog.ShowDialog(UiThread.Owner) == DialogResult.OK ? dialog.FileName : null;
        });

    private static string? PickOpenPath(string title) =>
        UiThread.Invoke(() =>
        {
            using var dialog = new OpenFileDialog();
            dialog.Title = title;
            dialog.Filter = "ADF/HDF disk image|*.adf;*.hdf";
            dialog.CheckFileExists = true;
            return dialog.ShowDialog(UiThread.Owner) == DialogResult.OK ? dialog.FileName : null;
        });

    // --- validate / repair ---

    private void ValidateViaPicker(bool interactive)
    {
        string? path = PickOpenPath(interactive ? "Repair disk image" : "Validate disk image");
        if (path is not null)
        {
            RunChecksumScan(path, interactive);
        }
    }

    /// <summary>
    /// Shared driver for both validate (report-only) and repair (interactive repair/ignore/reject) -
    /// deep-scans every checksum-protected block (RDB + every partition's/the whole image's filesystem
    /// tree), unlike <see cref="Mount"/> which only checks foundational blocks. Each mismatch is resolved
    /// (during repair) via <see cref="ChecksumConflictWindow"/> as it's found, then the full result set
    /// is shown in one <see cref="ChecksumScanWindow"/> at the end.
    /// </summary>
    private void RunChecksumScan(string sourcePath, bool interactive)
    {
        if (!File.Exists(sourcePath))
        {
            ShowError($"File not found: {sourcePath}");
            return;
        }

        var image = AdfImage.FromFile(sourcePath);
        var rows = new List<ChecksumRow>();
        int checkedCount = 0;
        int problemCount = 0;
        int repairedCount = 0;

        void Report(string containerLabel, ChecksumReport report, Action repair)
        {
            checkedCount++;
            string label = $"{containerLabel}: {report.BlockDescription}";
            string stored = $"0x{report.Stored:X8}";
            string computed = $"0x{report.Computed:X8}";

            if (report.Valid)
            {
                rows.Add(new ChecksumRow(label, "OK", stored, computed));
                return;
            }

            problemCount++;

            if (!interactive)
            {
                rows.Add(new ChecksumRow(label, "INVALID", stored, computed));
                return;
            }

            if (ChecksumConflictWindow.Show(label, report.Stored, report.Computed) == ChecksumDecision.Repair)
            {
                repair();
                repairedCount++;
                rows.Add(new ChecksumRow(label, "REPAIRED", stored, computed));
            }
            else
            {
                rows.Add(new ChecksumRow(label, "INVALID", stored, computed));
            }
        }

        foreach (var report in RigidDiskBlock.ScanChecksums(image))
        {
            Report($"'{sourcePath}'", report, () => RigidDiskBlock.RepairChecksum(image, report));
        }

        var (partitions, _) = RigidDiskBlock.TryReadPartitions(image);
        if (partitions is { Count: > 0 })
        {
            foreach (var part in partitions)
            {
                var window = image.CreateWindow(part.StartBlock, part.BlockCount);
                string label = $"partition '{part.DriveName}' of '{sourcePath}'";
                string? notRecognized = ScanFileSystem(window, label, Report);
                if (notRecognized is not null)
                {
                    rows.Add(new ChecksumRow(label, "N/A", notRecognized, ""));
                }
            }
        }
        else
        {
            string label = $"'{sourcePath}'";
            string? notRecognized = ScanFileSystem(image, label, Report);
            if (notRecognized is not null)
            {
                rows.Add(new ChecksumRow(label, "N/A", notRecognized, ""));
            }
        }

        if (repairedCount > 0)
        {
            image.SaveTo(sourcePath);
        }

        string summary =
            $"{checkedCount} block(s) checked, {problemCount} problem(s) found" +
            (interactive ? $", {repairedCount} repaired." : ".");
        UiThread.Invoke(() => new ChecksumScanWindow(rows, summary).ShowDialog(UiThread.Owner));
    }

    /// <summary>Returns a "why not" message if this container's filesystem couldn't be scanned, else null.</summary>
    private static string? ScanFileSystem(AdfImage image, string label, Action<string, ChecksumReport, Action> report)
    {
        IAmigaFileSystem fs;
        try
        {
            fs = AmigaFileSystemRegistry.CreateDefault().Mount(image);
        }
        catch (NotSupportedException ex)
        {
            return ex.Message;
        }

        if (fs is not IChecksumAware aware)
        {
            return "checksums not supported for this filesystem format";
        }

        foreach (var r in aware.ScanChecksums())
        {
            report(label, r, () => aware.RepairChecksum(r));
        }

        return null;
    }
}
