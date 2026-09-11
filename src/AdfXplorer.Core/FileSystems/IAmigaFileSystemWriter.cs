namespace AdfXplorer.Core.FileSystems;

/// <summary>
/// Opt-in write capability for an <see cref="IAmigaFileSystem"/>, parallel to
/// <see cref="IChecksumAware"/> - a format opts in by implementing this; nothing else in the codebase
/// is forced to support writing.
/// </summary>
public interface IAmigaFileSystemWriter
{
    /// <summary>
    /// False if this volume's bitmap needs more than this project's write support handles (a second
    /// bitmap block, or a bitmap-extension chain) - the caller must not attempt any write in that case
    /// and should mount the volume read-only instead.
    /// </summary>
    bool SupportsSafeWrite { get; }

    /// <summary>Free space, computed from the bitmap.</summary>
    long FreeBytes { get; }

    AmigaDirectoryEntry CreateFile(string path);

    AmigaDirectoryEntry CreateDirectory(string path);

    /// <summary>Writes <paramref name="data"/> at <paramref name="offset"/>, growing the file (and
    /// allocating blocks) as needed. Returns the number of bytes written.</summary>
    int WriteFile(string path, long offset, ReadOnlySpan<byte> data);

    /// <summary>Truncates (freeing tail blocks) or grows (zero-filling) the file to exactly
    /// <paramref name="size"/> bytes.</summary>
    void SetFileSize(string path, long size);

    /// <summary>Deletes a file or an empty directory. Throws <see cref="IOException"/> for a non-empty
    /// directory.</summary>
    void Delete(string path);

    /// <summary>Renames and/or moves an entry (same-directory rename or cross-directory move).</summary>
    void Rename(string oldPath, string newPath);

    void SetLastWriteTime(string path, DateTime utc);
}
