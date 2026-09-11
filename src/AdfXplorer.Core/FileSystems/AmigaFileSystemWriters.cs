using AdfXplorer.Core.DiskImage;

namespace AdfXplorer.Core.FileSystems;

/// <summary>One writable Amiga filesystem format: a display name and a blank-volume factory.</summary>
public sealed record AmigaFileSystemWriter(string DisplayName, Func<int, string, AdfImage> Create);

/// <summary>
/// Every filesystem format this build can *create* a blank volume for - the "dynamically populated
/// based on what's compiled in" list the Create ADF dialog's filesystem dropdown reads from. Parallel
/// to (not merged with) <see cref="AmigaFileSystemRegistry"/>, which is about reading existing images -
/// adding a new writable format later is purely appending another entry here.
/// </summary>
public static class AmigaFileSystemWriters
{
    public static IReadOnlyList<AmigaFileSystemWriter> All { get; } =
    [
        new("OFS (Original File System)", Ofs.OfsFileSystemWriter.CreateBlank),
        new("FFS (Fast File System)", Ffs.FfsFileSystemWriter.CreateBlank),
    ];
}
