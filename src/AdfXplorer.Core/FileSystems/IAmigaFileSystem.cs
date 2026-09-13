namespace AdfXplorer.Core.FileSystems;

/// <summary>
/// A read-only, format-agnostic view over an Amiga disk filesystem mounted from an
/// <see cref="DiskImage.AdfImage"/>. Implementations (OFS, and later FFS/SFS/PFS3) register
/// themselves with <see cref="AmigaFileSystemRegistry"/>.
///
/// Paths use forward-slash-separated, case-insensitive segments, with the empty string (or "/")
/// denoting the root directory - callers are responsible for normalizing Windows-style paths
/// before calling in.
/// </summary>
public interface IAmigaFileSystem
{
    /// <summary>The filesystem name reported to host platforms, for example "Amiga OFS".</summary>
    string FileSystemName { get; }

    string VolumeLabel { get; }

    IReadOnlyList<AmigaDirectoryEntry> ListDirectory(string path);

    bool TryGetEntry(string path, out AmigaDirectoryEntry entry);

    Stream OpenRead(string path);
}
