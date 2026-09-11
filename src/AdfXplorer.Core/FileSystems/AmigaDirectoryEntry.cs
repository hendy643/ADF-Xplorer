namespace AdfXplorer.Core.FileSystems;

/// <summary>
/// A single file or directory entry as seen from an <see cref="IAmigaFileSystem"/>.
/// </summary>
public sealed record AmigaDirectoryEntry(
    string Name,
    bool IsDirectory,
    long Size,
    DateTime LastWriteTimeUtc,
    string Comment);
