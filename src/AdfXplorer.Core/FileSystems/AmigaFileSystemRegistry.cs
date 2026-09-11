using AdfXplorer.Core.DiskImage;

namespace AdfXplorer.Core.FileSystems;

/// <summary>
/// Returns a mounted <see cref="IAmigaFileSystem"/> for the given image, or <c>null</c> if this
/// factory does not recognize the image's on-disk format.
/// </summary>
public delegate IAmigaFileSystem? AmigaFileSystemFactory(AdfImage image);

/// <summary>
/// Pluggable registry of Amiga on-disk filesystem formats. Adding support for a new format (FFS
/// variants, SFS, PFS3, ...) is purely a matter of implementing <see cref="IAmigaFileSystem"/> and
/// registering a detector here - no changes are needed anywhere else in the codebase.
/// </summary>
public sealed class AmigaFileSystemRegistry
{
    private readonly List<AmigaFileSystemFactory> _factories = [];

    /// <summary>
    /// A registry pre-populated with every filesystem format this build supports out of the box.
    /// </summary>
    public static AmigaFileSystemRegistry CreateDefault()
    {
        var registry = new AmigaFileSystemRegistry();
        registry.Register(Ofs.OfsFileSystem.TryMount);
        registry.Register(Ffs.FfsFileSystem.TryMount);
        return registry;
    }

    public void Register(AmigaFileSystemFactory factory) => _factories.Add(factory);

    public IAmigaFileSystem Mount(AdfImage image)
    {
        foreach (var factory in _factories)
        {
            var fs = factory(image);
            if (fs is not null)
            {
                return fs;
            }
        }

        throw new NotSupportedException(
            $"No registered Amiga file system recognizes this image (boot block signature: {DescribeSignature(image)}).");
    }

    // The boot block's first 4 bytes are the AmigaDOS disk-type ID: "DOS" followed by a version
    // byte (0=OFS, 1=FFS, plus INTL/DIRCACHE bit flags) - decode it for a useful error message.
    private static string DescribeSignature(AdfImage image)
    {
        if (image.SectorCount == 0)
        {
            return "<empty image>";
        }

        var boot = image.ReadBlock(0);
        if (boot.Length < 4)
        {
            return "<unreadable>";
        }

        Span<char> chars = stackalloc char[3];
        for (int i = 0; i < 3; i++)
        {
            chars[i] = (char)boot[i];
        }

        return $"{new string(chars)}\\{boot[3]}";
    }
}
