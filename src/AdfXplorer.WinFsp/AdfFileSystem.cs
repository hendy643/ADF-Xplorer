using System.IO;
using System.Security.AccessControl;
using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems;
using WinFsp.Native;

namespace AdfXplorer.WinFsp;

/// <summary>
/// Bridges an <see cref="IAmigaFileSystem"/> (optionally also <see cref="IAmigaFileSystemWriter"/>) onto
/// WinFsp's <see cref="IFileSystem"/> callback contract, using the WinFsp.Native binding
/// (https://github.com/hooyao/winfsp-native).
///
/// Every mutating callback is refused with <see cref="NtStatus.MediaWriteProtected"/> when the mounted
/// filesystem doesn't implement <see cref="IAmigaFileSystemWriter"/>, or when it does but
/// <see cref="IAmigaFileSystemWriter.SupportsSafeWrite"/> is false (a volume shape this project's write
/// support doesn't handle - see <see cref="IAmigaFileSystemWriter"/>'s doc comment) - enforced both by
/// <see cref="Init"/> declaring the volume read-only in that case and by explicit per-callback checks, so
/// a bug in one layer can't silently corrupt someone's disk image.
///
/// Every successful mutating callback persists immediately (<see cref="Persist"/>) - write-through on
/// every mutation rather than dirty-tracking, which is fine at floppy/small-hardfile scale.
/// <paramref name="topLevelImage"/>/<paramref name="sourcePath"/> passed to the constructor are
/// deliberately the *original, un-windowed* image and its file path even when <paramref name="fs"/> is
/// backed by an RDB partition's windowed sub-image - <c>AdfImage.CreateWindow</c> shares its parent's
/// backing array, so saving the top-level image always captures a partition's writes too, while saving a
/// window directly would truncate the rest of the file.
/// </summary>
public sealed unsafe class AdfFileSystem : IFileSystem
{
    private readonly IAmigaFileSystem _fs;
    private readonly IAmigaFileSystemWriter? _writer;
    private readonly AdfImage _topLevelImage;
    private readonly string _sourcePath;
    private readonly long _imageSizeBytes;
    private readonly byte[] _securityDescriptor;

    /// <summary>
    /// Wraps an already-opened Amiga filesystem for mounting.
    /// </summary>
    /// <param name="fs">The filesystem to expose (OFS/FFS reader, optionally also a writer).</param>
    /// <param name="topLevelImage">The un-windowed root image to persist on every mutation - see class remarks.</param>
    /// <param name="sourcePath">Path this image was loaded from and is saved back to.</param>
    /// <param name="volumeSizeBytes">Reported total volume size (the windowed size, not the whole-image size).</param>
    public AdfFileSystem(IAmigaFileSystem fs, AdfImage topLevelImage, string sourcePath, long volumeSizeBytes)
    {
        _fs = fs;
        _writer = fs as IAmigaFileSystemWriter;
        _topLevelImage = topLevelImage;
        _sourcePath = sourcePath;
        _imageSizeBytes = volumeSizeBytes;
        _securityDescriptor = BuildSecurityDescriptor();
    }

    /// <summary>
    /// True because <see cref="AdfImage"/> exposes synchronous block reads and writes; file-backed images
    /// stream those blocks directly from their source file without a dispatcher thread-pool hop.
    /// </summary>
    public bool SynchronousIo => true;

    /// <summary>
    /// Configures the mounted volume's fixed characteristics. Amiga disks are always 512-byte-sectored,
    /// so <see cref="FileSystemHost.SectorSize"/>/<see cref="FileSystemHost.SectorsPerAllocationUnit"/> are
    /// hardcoded rather than derived from the source format.
    /// </summary>
    public int Init(FileSystemHost host)
    {
        host.SectorSize = 512;
        host.SectorsPerAllocationUnit = 1;
        host.VolumeSerialNumber = 0x41444658; // "ADFX"
        host.ReadOnlyVolume = _writer is null || !_writer.SupportsSafeWrite;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.ReparsePoints = false;
        host.NamedStreams = false;
        host.ExtendedAttributes = false;
        host.FileSystemName = _fs.FileSystemName;
        return NtStatus.Success;
    }

    /// <summary>No post-mount setup is needed - the image is already fully loaded before <see cref="Init"/>.</summary>
    public int Mounted(FileSystemHost host) => NtStatus.Success;

    /// <summary>No teardown needed - the last <see cref="Persist"/> already wrote everything to disk.</summary>
    public void Unmounted(FileSystemHost host)
    {
    }

    /// <summary>Nothing to release - there's no dispatcher-owned state outside the managed objects here.</summary>
    public void DispatcherStopped(bool normally)
    {
    }

    /// <summary>Reports the windowed volume size (not the whole backing image) and remaining free space.</summary>
    public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
    {
        totalSize = (ulong)_imageSizeBytes;
        freeSize = (ulong)(_writer?.FreeBytes ?? 0);
        volumeLabel = _fs.VolumeLabel;
        return NtStatus.Success;
    }

    public int SetVolumeLabel(string volumeLabel, out ulong totalSize, out ulong freeSize)
    {
        totalSize = (ulong)_imageSizeBytes;
        freeSize = (ulong)(_writer?.FreeBytes ?? 0);
        return NtStatus.MediaWriteProtected; // volume-label rename isn't modeled by IAmigaFileSystemWriter
    }

    /// <summary>
    /// Answers Explorer's "properties"/permission checks by name, without requiring an open handle first.
    /// The root path (empty after normalization) is synthesized as a directory since it has no
    /// corresponding <see cref="AmigaDirectoryEntry"/> of its own.
    /// </summary>
    public int GetFileSecurityByName(string fileName, out uint fileAttributes, ref byte[]? securityDescriptor)
    {
        securityDescriptor = _securityDescriptor;

        var path = NormalizePath(fileName);
        if (path.Length == 0)
        {
            fileAttributes = (uint)FileAttributes.Directory;
            return NtStatus.Success;
        }

        if (!_fs.TryGetEntry(path, out var entry))
        {
            fileAttributes = 0;
            return NtStatus.ObjectNameNotFound;
        }

        fileAttributes = AttributesFor(entry);
        return NtStatus.Success;
    }

    /// <summary>
    /// Creates a new file or directory. WinFsp requires the new object's <see cref="FileOperationInfo.Context"/>
    /// be populated here so subsequent callbacks on the same handle (read/write/close) know which path and
    /// kind they're operating on without re-resolving it.
    /// </summary>
    public ValueTask<CreateResult> CreateFile(
        string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[]? securityDescriptor, ulong allocationSize, FileOperationInfo info, CancellationToken ct)
    {
        if (_writer is null)
        {
            return ValueTask.FromResult(new CreateResult(NtStatus.MediaWriteProtected, default, null));
        }

        var path = NormalizePath(fileName);
        bool isDirectory = (createOptions & (uint)CreateOptions.FileDirectoryFile) != 0;

        try
        {
            var entry = isDirectory ? _writer.CreateDirectory(path) : _writer.CreateFile(path);
            info.Context = new FileContext(path, entry.IsDirectory);
            Persist();
            return ValueTask.FromResult(new CreateResult(NtStatus.Success, BuildFileInfo(entry), null));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(new CreateResult(NtStatus.ObjectNameCollision, default, null));
        }
    }

    /// <summary>
    /// Opens an existing file, directory, or the volume root (path normalizes to empty and is synthesized
    /// since the root has no <see cref="AmigaDirectoryEntry"/> of its own).
    /// </summary>
    public ValueTask<CreateResult> OpenFile(
        string fileName, uint createOptions, uint grantedAccess, FileOperationInfo info, CancellationToken ct)
    {
        var path = NormalizePath(fileName);
        AmigaDirectoryEntry entry;
        if (path.Length == 0)
        {
            entry = new AmigaDirectoryEntry(_fs.VolumeLabel, IsDirectory: true, Size: 0, DateTime.UnixEpoch, "");
        }
        else if (!_fs.TryGetEntry(path, out entry!))
        {
            return ValueTask.FromResult(new CreateResult(NtStatus.ObjectNameNotFound, default, null));
        }

        info.Context = new FileContext(path, entry.IsDirectory);
        return ValueTask.FromResult(new CreateResult(NtStatus.Success, BuildFileInfo(entry), null));
    }

    /// <summary>Truncates an existing file to zero length (Windows "overwrite" semantics, e.g. from `&gt;`-style redirection).</summary>
    public ValueTask<FsResult> OverwriteFile(
        uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, FileOperationInfo info,
        CancellationToken ct)
    {
        if (_writer is null)
        {
            return ValueTask.FromResult(FsResult.Error(NtStatus.MediaWriteProtected));
        }

        var ctx = (FileContext)info.Context!;
        try
        {
            _writer.SetFileSize(ctx.Path, 0);
            Persist();
            _fs.TryGetEntry(ctx.Path, out var entry);
            return ValueTask.FromResult(FsResult.Success(BuildFileInfo(entry)));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(FsResult.Error(NtStatus.UnexpectedIoError));
        }
    }

    /// <summary>Reads a byte range from a file, re-opening the underlying stream fresh each call - see remarks below.</summary>
    public ValueTask<ReadResult> ReadFile(
        string fileName, Memory<byte> buffer, ulong offset, FileOperationInfo info, CancellationToken ct)
    {
        var ctx = (FileContext)info.Context!;
        if (ctx.IsDirectory)
        {
            return ValueTask.FromResult(ReadResult.Error(NtStatus.AccessDenied));
        }

        // Re-opened fresh on every call (rather than cached at Open time) so a read always reflects the
        // latest writes made through this or another handle - correctness over the small re-read cost,
        // which is negligible at floppy/small-hardfile scale.
        using var stream = _fs.OpenRead(ctx.Path);
        if (offset >= (ulong)stream.Length)
        {
            return ValueTask.FromResult(ReadResult.EndOfFile());
        }

        stream.Position = (long)offset;
        int read = stream.Read(buffer.Span);
        return ValueTask.FromResult(ReadResult.Success((uint)read));
    }

    /// <summary>Writes a byte range, optionally appending at the current end-of-file (see <paramref name="writeToEndOfFile"/>).</summary>
    public ValueTask<WriteResult> WriteFile(
        string fileName, ReadOnlyMemory<byte> buffer, ulong offset, bool writeToEndOfFile, bool constrainedIo,
        FileOperationInfo info, CancellationToken ct)
    {
        if (_writer is null)
        {
            return ValueTask.FromResult(WriteResult.Error(NtStatus.MediaWriteProtected));
        }

        var ctx = (FileContext)info.Context!;
        try
        {
            long writeOffset = (long)offset;
            if (writeToEndOfFile && _fs.TryGetEntry(ctx.Path, out var current))
            {
                writeOffset = current.Size;
            }

            // constrainedIo (don't extend past current EOF, used for memory-mapped writes) isn't
            // enforced separately - an accepted simplification; ordinary file writes are unaffected.
            int written = _writer.WriteFile(ctx.Path, writeOffset, buffer.Span);
            Persist();

            _fs.TryGetEntry(ctx.Path, out var updated);
            return ValueTask.FromResult(WriteResult.Success((uint)written, BuildFileInfo(updated)));
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return ValueTask.FromResult(WriteResult.Error(NtStatus.UnexpectedIoError));
        }
    }

    /// <summary>
    /// A no-op beyond re-<see cref="Persist"/>ing - every mutating callback already persists immediately,
    /// so there's no buffered write state to flush.
    /// </summary>
    public ValueTask<FsResult> FlushFileBuffers(string? fileName, FileOperationInfo info, CancellationToken ct)
    {
        Persist();
        return ValueTask.FromResult(FsResult.Success(default));
    }

    /// <summary>Reports metadata for an already-open handle (root path is synthesized - see <see cref="OpenFile"/>).</summary>
    public ValueTask<FsResult> GetFileInformation(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        var ctx = (FileContext)info.Context!;
        AmigaDirectoryEntry entry;
        if (ctx.Path.Length == 0)
        {
            entry = new AmigaDirectoryEntry(_fs.VolumeLabel, IsDirectory: true, Size: 0, DateTime.UnixEpoch, "");
        }
        else if (!_fs.TryGetEntry(ctx.Path, out entry!))
        {
            return ValueTask.FromResult(FsResult.Error(NtStatus.ObjectNameNotFound));
        }

        return ValueTask.FromResult(FsResult.Success(BuildFileInfo(entry)));
    }

    /// <summary>
    /// Only <paramref name="lastWriteTime"/> maps onto anything AmigaDOS tracks; attributes and the other
    /// timestamps are accepted but ignored (see the inline comment below on protection bits).
    /// </summary>
    public ValueTask<FsResult> SetFileAttributes(
        string fileName, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime,
        ulong changeTime, FileOperationInfo info, CancellationToken ct)
    {
        if (_writer is null)
        {
            return ValueTask.FromResult(FsResult.Error(NtStatus.MediaWriteProtected));
        }

        var ctx = (FileContext)info.Context!;
        if (lastWriteTime != 0)
        {
            _writer.SetLastWriteTime(ctx.Path, DateTime.FromFileTimeUtc((long)lastWriteTime));
            Persist();
        }

        // Amiga protection-bit mapping (read-only/archive/etc.) is not modeled - accepted as a no-op.
        _fs.TryGetEntry(ctx.Path, out var entry);
        return ValueTask.FromResult(FsResult.Success(BuildFileInfo(entry)));
    }

    /// <summary>Resizes a file's logical length; a pure allocation-size hint is a no-op (see remarks inline).</summary>
    public ValueTask<FsResult> SetFileSize(
        string fileName, ulong newSize, bool setAllocationSize, FileOperationInfo info, CancellationToken ct)
    {
        if (_writer is null)
        {
            return ValueTask.FromResult(FsResult.Error(NtStatus.MediaWriteProtected));
        }

        var ctx = (FileContext)info.Context!;

        if (setAllocationSize)
        {
            // A pure allocation-size hint (no change to the logical EOF) isn't meaningful for our
            // block-per-file-size model; accept as a no-op.
            _fs.TryGetEntry(ctx.Path, out var unchanged);
            return ValueTask.FromResult(FsResult.Success(BuildFileInfo(unchanged)));
        }

        try
        {
            _writer.SetFileSize(ctx.Path, (long)newSize);
            Persist();
            _fs.TryGetEntry(ctx.Path, out var updated);
            return ValueTask.FromResult(FsResult.Success(BuildFileInfo(updated)));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(FsResult.Error(NtStatus.UnexpectedIoError));
        }
    }

    /// <summary>
    /// WinFsp's pre-delete veto point - separate from <see cref="Cleanup"/>, which has no status-return
    /// channel, so rejecting a non-empty directory (or a read-only mount) must happen here.
    /// </summary>
    public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        if (_writer is null)
        {
            return ValueTask.FromResult(NtStatus.MediaWriteProtected);
        }

        var ctx = (FileContext)info.Context!;
        if (ctx.IsDirectory && _fs.ListDirectory(ctx.Path).Count > 0)
        {
            return ValueTask.FromResult(NtStatus.DirectoryNotEmpty);
        }

        return ValueTask.FromResult(NtStatus.Success);
    }

    /// <summary>
    /// Renames/moves a file or directory. When replacing an existing non-directory destination, the old
    /// destination is deleted first since the underlying writer's <c>Rename</c> doesn't overwrite.
    /// </summary>
    public ValueTask<int> MoveFile(
        string fileName, string newFileName, bool replaceIfExists, FileOperationInfo info, CancellationToken ct)
    {
        if (_writer is null)
        {
            return ValueTask.FromResult(NtStatus.MediaWriteProtected);
        }

        var ctx = (FileContext)info.Context!;
        var newPath = NormalizePath(newFileName);

        bool destExists = _fs.TryGetEntry(newPath, out var existing);
        if (destExists && !replaceIfExists)
        {
            return ValueTask.FromResult(NtStatus.ObjectNameCollision);
        }

        try
        {
            if (destExists && !existing.IsDirectory)
            {
                _writer.Delete(newPath);
            }

            _writer.Rename(ctx.Path, newPath);
            ctx.Path = newPath;
            Persist();
            return ValueTask.FromResult(NtStatus.Success);
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return ValueTask.FromResult(NtStatus.UnexpectedIoError);
        }
    }

    /// <summary>
    /// Performs the actual delete once WinFsp confirms the handle is closing with
    /// <see cref="CleanupFlags.Delete"/> set (the real accept/reject decision already happened in
    /// <see cref="CanDelete"/>).
    /// </summary>
    public void Cleanup(string? fileName, FileOperationInfo info, CleanupFlags flags)
    {
        if (_writer is null || !flags.HasFlag(CleanupFlags.Delete))
        {
            return;
        }

        var ctx = (FileContext)info.Context!;
        try
        {
            _writer.Delete(ctx.Path);
            Persist();
        }
        catch (Exception ex)
        {
            // Cleanup has no status-return channel back to the caller - CanDelete is where real
            // rejection happens; this is a best-effort log only.
            Console.Error.WriteLine($"Delete failed for '{ctx.Path}': {ex.Message}");
        }
    }

    /// <summary>No handle-owned resources to release - <see cref="FileContext"/> is just plain state.</summary>
    public void Close(FileOperationInfo info)
    {
    }

    /// <summary>
    /// Lists a directory's entries, synthesizing "." and ".." (AmigaDOS directories don't store them) and
    /// resuming from <paramref name="marker"/> when the caller's buffer couldn't hold a previous batch.
    /// </summary>
    public ValueTask<ReadDirectoryResult> ReadDirectory(
        string fileName, string? pattern, string? marker, IntPtr buffer, uint length, FileOperationInfo info,
        CancellationToken ct)
    {
        var path = NormalizePath(fileName);

        var names = new List<(string Name, AmigaDirectoryEntry Entry)>
        {
            (".", new AmigaDirectoryEntry(".", true, 0, DateTime.UnixEpoch, "")),
            ("..", new AmigaDirectoryEntry("..", true, 0, DateTime.UnixEpoch, "")),
        };
        foreach (var e in _fs.ListDirectory(path))
        {
            names.Add((e.Name, e));
        }

        IEnumerable<(string Name, AmigaDirectoryEntry Entry)> toEmit = names;
        if (!string.IsNullOrEmpty(marker))
        {
            toEmit = names
                .SkipWhile(e => !string.Equals(e.Name, marker, StringComparison.OrdinalIgnoreCase))
                .Skip(1);
        }

        uint bytesTransferred = 0;
        foreach (var (name, entry) in toEmit)
        {
            var dirInfo = new FspDirInfo();
            dirInfo.SetFileName(name);
            dirInfo.FileInfo = BuildFileInfo(entry);
            if (!WinFspFileSystem.AddDirInfo(&dirInfo, buffer, length, &bytesTransferred))
            {
                break; // buffer full; WinFsp will call again with a marker to resume
            }
        }

        return ValueTask.FromResult(ReadDirectoryResult.Success(bytesTransferred));
    }

    /// <summary>Every file/directory shares the same fixed descriptor built at construction time.</summary>
    public int GetFileSecurity(string fileName, out byte[]? securityDescriptor, FileOperationInfo info)
    {
        securityDescriptor = _securityDescriptor;
        return NtStatus.Success;
    }

    /// <summary>ACLs aren't persisted anywhere in the Amiga formats, so changing them is always refused.</summary>
    public int SetFileSecurity(
        string fileName, uint securityInformation, byte[] modificationDescriptor, FileOperationInfo info) =>
        NtStatus.MediaWriteProtected;

    /// <summary>Amiga formats have no reparse-point concept (symlinks/junctions aren't modeled).</summary>
    public int GetReparsePoint(string fileName, out byte[]? reparseData, FileOperationInfo info)
    {
        reparseData = null;
        return NtStatus.NotImplemented;
    }

    /// <inheritdoc cref="GetReparsePoint"/>
    public int SetReparsePoint(string fileName, byte[] reparseData, FileOperationInfo info) =>
        NtStatus.MediaWriteProtected;

    /// <inheritdoc cref="GetReparsePoint"/>
    public int DeleteReparsePoint(string fileName, byte[] reparseData, FileOperationInfo info) =>
        NtStatus.MediaWriteProtected;

    /// <summary>Always empty - <see cref="FileSystemHost.NamedStreams"/> is false in <see cref="Init"/>.</summary>
    public int GetStreamInfo(
        string fileName, IntPtr buffer, uint length, out uint bytesTransferred, FileOperationInfo info)
    {
        bytesTransferred = 0;
        return NtStatus.Success; // no named streams
    }

    /// <summary>Extended attributes aren't modeled - <see cref="FileSystemHost.ExtendedAttributes"/> is false in <see cref="Init"/>.</summary>
    public int GetEa(string fileName, IntPtr ea, uint eaLength, out uint bytesTransferred, FileOperationInfo info)
    {
        bytesTransferred = 0;
        return NtStatus.NotImplemented;
    }

    /// <inheritdoc cref="GetEa"/>
    public int SetEa(string fileName, IntPtr ea, uint eaLength, ref FspFileInfo fileInfo, FileOperationInfo info) =>
        NtStatus.MediaWriteProtected;

    /// <summary>No custom IOCTLs are supported by this filesystem.</summary>
    public int DeviceControl(
        string fileName, uint controlCode, ReadOnlySpan<byte> input, Span<byte> output, out uint bytesTransferred,
        FileOperationInfo info)
    {
        bytesTransferred = 0;
        return NtStatus.InvalidDeviceRequest;
    }

    /// <summary>
    /// Looks up a single named child of a directory directly, letting WinFsp/Explorer avoid a full
    /// <see cref="ReadDirectory"/> enumeration just to stat one entry.
    /// </summary>
    public int GetDirInfoByName(string dirName, string entryName, ref FspDirInfo dirInfo, FileOperationInfo info)
    {
        var dirPath = NormalizePath(dirName);
        var fullPath = dirPath.Length == 0 ? entryName : $"{dirPath}/{entryName}";
        if (!_fs.TryGetEntry(fullPath, out var entry))
        {
            return NtStatus.ObjectNameNotFound;
        }

        dirInfo.SetFileName(entryName);
        dirInfo.FileInfo = BuildFileInfo(entry);
        return NtStatus.Success;
    }

    /// <summary>
    /// WinFsp's last-resort catch: any exception escaping a callback is translated to an NTSTATUS here
    /// instead of crashing the dispatcher thread.
    /// </summary>
    public int ExceptionHandler(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => NtStatus.ObjectNameNotFound,
        UnauthorizedAccessException => NtStatus.AccessDenied,
        _ => NtStatus.InternalError,
    };

    /// <summary>Write-through save of the whole top-level image - see the class remarks on why not just the window.</summary>
    private void Persist() => _topLevelImage.SaveTo(_sourcePath);

    /// <summary>Converts a WinFsp-style `\`-rooted Windows path into the `/`-separated relative path <see cref="IAmigaFileSystem"/> expects.</summary>
    private static string NormalizePath(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath) || windowsPath == "\\")
        {
            return string.Empty;
        }

        return windowsPath.TrimStart('\\').Replace('\\', '/');
    }

    /// <summary>Files are marked read-only when the mount has no writer, so Explorer's own UI reflects the write-protection.</summary>
    private uint AttributesFor(AmigaDirectoryEntry entry)
    {
        if (entry.IsDirectory)
        {
            return (uint)FileAttributes.Directory;
        }

        return (uint)(_writer is null ? FileAttributes.Normal | FileAttributes.ReadOnly : FileAttributes.Normal);
    }

    /// <summary>
    /// AmigaDOS only tracks one timestamp per entry, so all four Windows timestamp fields are filled with
    /// the same <see cref="AmigaDirectoryEntry.LastWriteTimeUtc"/> value.
    /// </summary>
    private FspFileInfo BuildFileInfo(AmigaDirectoryEntry entry)
    {
        ulong fileTime = (ulong)entry.LastWriteTimeUtc.ToFileTimeUtc();
        return new FspFileInfo
        {
            FileAttributes = AttributesFor(entry),
            FileSize = (ulong)entry.Size,
            AllocationSize = RoundUpToSector((ulong)entry.Size),
            CreationTime = fileTime,
            LastAccessTime = fileTime,
            LastWriteTime = fileTime,
            ChangeTime = fileTime,
            IndexNumber = 0,
            HardLinks = 0,
            EaSize = 0,
            ReparseTag = 0,
        };
    }

    /// <summary>Windows reports <see cref="FspFileInfo.AllocationSize"/> in whole sectors, never a partial one.</summary>
    private static ulong RoundUpToSector(ulong size) => (size + 511) / 512 * 512;

    /// <summary>
    /// ACL-level access is wide open ("Everyone: full control"); write protection (for a read-only-mounted
    /// volume) is enforced by <see cref="FileSystemHost.ReadOnlyVolume"/> plus the explicit
    /// <see cref="NtStatus.MediaWriteProtected"/> refusals above, not by this descriptor.
    /// </summary>
    private static byte[] BuildSecurityDescriptor()
    {
        var sd = new RawSecurityDescriptor("O:BAG:BAD:(A;;FA;;;WD)");
        var buffer = new byte[sd.BinaryLength];
        sd.GetBinaryForm(buffer, 0);
        return buffer;
    }

    /// <summary>
    /// Per-handle state WinFsp round-trips via <see cref="FileOperationInfo.Context"/>. <see cref="Path"/>
    /// is mutable because <see cref="MoveFile"/> must update it in place so later callbacks on the same
    /// still-open handle resolve against the new location.
    /// </summary>
    private sealed class FileContext(string path, bool isDirectory)
    {
        public string Path { get; set; } = path;
        public bool IsDirectory { get; } = isDirectory;
    }
}
