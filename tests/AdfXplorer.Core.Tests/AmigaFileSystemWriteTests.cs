using System.Buffers.Binary;
using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems;
using AdfXplorer.Core.FileSystems.Ffs;
using AdfXplorer.Core.FileSystems.Ofs;

namespace AdfXplorer.Core.Tests;

/// <summary>
/// Read-write tests run against BOTH OFS and FFS (via <see cref="Formats"/>) - the write-side logic
/// (bitmap allocate/free, directory insert/remove/rename) lives in the shared
/// <c>AmigaHashDirectoryFileSystem</c> base and should behave identically for both formats; only the
/// data-block sizing differs; per-format sizes are chosen to exercise that (OFS's 488-byte payload vs
/// FFS's 512-byte raw block, and FFS's 72-slot-per-table extension-block threshold).
/// </summary>
public class AmigaFileSystemWriteTests
{
    public sealed record Format(string Name, Func<int, string, AdfImage> CreateBlank, Func<AdfImage, IAmigaFileSystem?> TryMount);

    public static TheoryData<Format> Formats { get; } = new()
    {
        new Format("OFS", OfsFileSystemWriter.CreateBlank, OfsFileSystem.TryMount),
        new Format("FFS", FfsFileSystemWriter.CreateBlank, FfsFileSystem.TryMount),
    };

    private const int SectorCount = 1760; // 880 KB

    private static (IAmigaFileSystemWriter Writer, IAmigaFileSystem Fs) Blank(Format format, string label = "TestDisk")
    {
        var image = format.CreateBlank(SectorCount, label);
        var fs = format.TryMount(image);
        Assert.NotNull(fs);
        var writer = Assert.IsAssignableFrom<IAmigaFileSystemWriter>(fs);
        return (writer, fs!);
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 256);
        }

        return bytes;
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void SupportsSafeWrite_IsTrueForBlankImage(Format format)
    {
        var (writer, _) = Blank(format);
        Assert.True(writer.SupportsSafeWrite);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void SupportsSafeWrite_IsFalseWhenMultipleBitmapBlocksAreInUse(Format format)
    {
        var image = format.CreateBlank(SectorCount, "TestDisk");
        int rootBlock = SectorCount / 2;
        // Simulate a real-world large-disk image using a second bitmap page - our write support doesn't
        // handle that, so it must refuse rather than risk corrupting a structure it doesn't implement.
        var root = image.GetBlockForWrite(rootBlock);
        BinaryPrimitives.WriteInt32BigEndian(root.Slice(316 + 4, 4), 999); // bm_pages[1]

        var fs = format.TryMount(image);
        var writer = Assert.IsAssignableFrom<IAmigaFileSystemWriter>(fs);
        Assert.False(writer.SupportsSafeWrite);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void CreateFile_WriteAndReadBack_ByteExact_AcrossMultipleBlocks(Format format)
    {
        var (writer, fs) = Blank(format);
        writer.CreateFile("BIG.BIN");

        var content = Pattern(2000); // spans several data blocks for both formats
        int written = writer.WriteFile("BIG.BIN", 0, content);
        Assert.Equal(content.Length, written);

        using var stream = fs.OpenRead("BIG.BIN");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Assert.Equal(content, ms.ToArray());

        Assert.True(fs.TryGetEntry("BIG.BIN", out var entry));
        Assert.Equal(content.Length, entry.Size);

        var aware = Assert.IsAssignableFrom<IChecksumAware>(fs);
        Assert.All(aware.ScanChecksums(), r => Assert.True(r.Valid, r.BlockDescription));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void CreateFile_LargeEnoughToForceExtensionOrManyBlocks_ReadsBackByteExact(Format format)
    {
        var (writer, fs) = Blank(format);
        writer.CreateFile("HUGE.BIN");

        // 40000 bytes forces an FFS extension block (>72*512=36864) and, for OFS, a ~82-block chain -
        // both are meaningfully larger than a single block/table for either format.
        var content = Pattern(40000);
        writer.WriteFile("HUGE.BIN", 0, content);

        using var stream = fs.OpenRead("HUGE.BIN");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Assert.Equal(content, ms.ToArray());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void CreateDirectory_CreateFileInside_ListAndReadBack(Format format)
    {
        var (writer, fs) = Blank(format);
        writer.CreateDirectory("SUBDIR");
        writer.CreateFile("SUBDIR/INNER.TXT");
        var content = Pattern(100);
        writer.WriteFile("SUBDIR/INNER.TXT", 0, content);

        var rootEntries = fs.ListDirectory("");
        Assert.Contains(rootEntries, e => e.Name == "SUBDIR" && e.IsDirectory);

        var subEntries = fs.ListDirectory("SUBDIR");
        var inner = Assert.Single(subEntries);
        Assert.Equal("INNER.TXT", inner.Name);

        using var stream = fs.OpenRead("SUBDIR/INNER.TXT");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Assert.Equal(content, ms.ToArray());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Delete_File_RemovesFromListingAndFreesBlocks(Format format)
    {
        var image = format.CreateBlank(SectorCount, "TestDisk");
        var fs = format.TryMount(image);
        var writer = Assert.IsAssignableFrom<IAmigaFileSystemWriter>(fs);

        writer.CreateFile("A.BIN");
        writer.WriteFile("A.BIN", 0, Pattern(5000));
        long freeBeforeCreate = writer.FreeBytes;

        writer.Delete("A.BIN");

        Assert.False(fs!.TryGetEntry("A.BIN", out _));
        Assert.DoesNotContain(fs.ListDirectory(""), e => e.Name == "A.BIN");
        Assert.True(writer.FreeBytes > freeBeforeCreate, "Deleting the file should free its data blocks.");

        // Functional proof the blocks are genuinely free again: recreate a file of the same size.
        writer.CreateFile("B.BIN");
        int written = writer.WriteFile("B.BIN", 0, Pattern(5000));
        Assert.Equal(5000, written);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Delete_NonEmptyDirectory_ThrowsUntilEmptied(Format format)
    {
        var (writer, fs) = Blank(format);
        writer.CreateDirectory("SUBDIR");
        writer.CreateFile("SUBDIR/FILE.TXT");

        Assert.Throws<IOException>(() => writer.Delete("SUBDIR"));

        writer.Delete("SUBDIR/FILE.TXT");
        writer.Delete("SUBDIR"); // now empty - should succeed

        Assert.DoesNotContain(fs.ListDirectory(""), e => e.Name == "SUBDIR");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Rename_SameDirectory_UpdatesName(Format format)
    {
        var (writer, fs) = Blank(format);
        writer.CreateFile("OLD.TXT");
        writer.WriteFile("OLD.TXT", 0, Pattern(50));

        writer.Rename("OLD.TXT", "NEW.TXT");

        Assert.False(fs.TryGetEntry("OLD.TXT", out _));
        Assert.True(fs.TryGetEntry("NEW.TXT", out var entry));
        Assert.Equal(50, entry.Size);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Rename_ToDifferentDirectory_MovesEntry(Format format)
    {
        var (writer, fs) = Blank(format);
        writer.CreateDirectory("DEST");
        writer.CreateFile("FILE.TXT");
        var content = Pattern(75);
        writer.WriteFile("FILE.TXT", 0, content);

        writer.Rename("FILE.TXT", "DEST/FILE.TXT");

        Assert.False(fs.TryGetEntry("FILE.TXT", out _));
        Assert.True(fs.TryGetEntry("DEST/FILE.TXT", out _));

        using var stream = fs.OpenRead("DEST/FILE.TXT");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Assert.Equal(content, ms.ToArray());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void SetFileSize_GrowThenTruncate_FreesBlocksAndKeepsRemainingContentCorrect(Format format)
    {
        var (writer, fs) = Blank(format);
        writer.CreateFile("FILE.BIN");
        var content = Pattern(3000);
        writer.WriteFile("FILE.BIN", 0, content);

        writer.SetFileSize("FILE.BIN", 6000); // grow: zero-fills the new region
        Assert.True(fs.TryGetEntry("FILE.BIN", out var grown));
        Assert.Equal(6000, grown.Size);

        using (var stream = fs.OpenRead("FILE.BIN"))
        using (var ms = new MemoryStream())
        {
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            Assert.Equal(content, bytes[..3000]);
            Assert.All(bytes[3000..], b => Assert.Equal(0, b));
        }

        long freeAfterGrow = writer.FreeBytes;
        writer.SetFileSize("FILE.BIN", 1000); // truncate: frees the tail blocks
        Assert.True(writer.FreeBytes > freeAfterGrow);

        Assert.True(fs.TryGetEntry("FILE.BIN", out var truncated));
        Assert.Equal(1000, truncated.Size);

        using var truncatedStream = fs.OpenRead("FILE.BIN");
        using var truncatedMs = new MemoryStream();
        truncatedStream.CopyTo(truncatedMs);
        Assert.Equal(content[..1000], truncatedMs.ToArray());
    }
}
