using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems;
using AdfXplorer.Core.FileSystems.Ffs;

namespace AdfXplorer.Core.Tests;

public class FfsFileSystemTests
{
    private static IAmigaFileSystem Mount(byte[] imageBytes)
    {
        var image = new AdfImage(imageBytes);
        var fs = FfsFileSystem.TryMount(image);
        Assert.NotNull(fs);
        return fs!;
    }

    [Fact]
    public void VolumeLabel_MatchesBuiltImage()
    {
        var image = FfsTestImageBuilder.Build(out _, out _);
        var fs = Mount(image);

        Assert.Equal("FfsTestDisk", fs.VolumeLabel);
    }

    [Fact]
    public void FileSystemName_ReportsAmigaFfs()
    {
        var image = FfsTestImageBuilder.Build(out _, out _);

        Assert.Equal("Amiga FFS", Mount(image).FileSystemName);
    }

    [Fact]
    public void TryMount_UsesMidpointRootWhenBootPointerIsZero()
    {
        var image = FfsTestImageBuilder.Build(out _, out _);
        Array.Clear(image, 8, 4);
        ChecksumTestHelper.WriteBootChecksum(image);

        var fs = Mount(image);

        Assert.Equal("FfsTestDisk", fs.VolumeLabel);
    }

    [Fact]
    public void ListDirectory_Root_ReturnsBothFiles()
    {
        var image = FfsTestImageBuilder.Build(out _, out _);
        var fs = Mount(image);

        var entries = fs.ListDirectory("");

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == FfsTestImageBuilder.SmallFileName && !e.IsDirectory);
        Assert.Contains(entries, e => e.Name == FfsTestImageBuilder.BigFileName && !e.IsDirectory);
    }

    [Fact]
    public void TryGetEntry_ReturnsCorrectSizeForBigFile()
    {
        var image = FfsTestImageBuilder.Build(out _, out var bigContent);
        var fs = Mount(image);

        Assert.True(fs.TryGetEntry(FfsTestImageBuilder.BigFileName, out var entry));
        Assert.Equal(bigContent.Length, entry.Size);
    }

    [Fact]
    public void OpenRead_SmallFile_ReturnsExactBytes()
    {
        var image = FfsTestImageBuilder.Build(out var smallContent, out _);
        var fs = Mount(image);

        using var stream = fs.OpenRead(FfsTestImageBuilder.SmallFileName);
        using var reader = new StreamReader(stream);

        Assert.Equal(smallContent, reader.ReadToEnd());
    }

    // Exercises the > 72-data-block case specifically: the header block's own data-block table only
    // holds 72 slots, so this file's remaining blocks live in an extension (T_LIST) block, and reading
    // must follow that link and keep the two tables' blocks in the right overall order.
    [Fact]
    public void OpenRead_BigFile_ReassemblesAllDataBlocksAcrossExtensionBlockInOrder()
    {
        var image = FfsTestImageBuilder.Build(out _, out var bigContent);
        var fs = Mount(image);

        using var stream = fs.OpenRead(FfsTestImageBuilder.BigFileName);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var actual = ms.ToArray();

        Assert.Equal(bigContent, actual);
    }

    [Fact]
    public void ScanChecksums_HappyPathImage_ReportsEverythingValid()
    {
        var image = FfsTestImageBuilder.Build(out _, out _);
        var fs = Mount(image);

        var aware = Assert.IsAssignableFrom<IChecksumAware>(fs);
        var reports = aware.ScanChecksums().ToList();

        // boot + root + 2 file headers + 1 extension block = 5 reports (no raw-data-block reports -
        // FFS data blocks carry no checksum).
        Assert.Equal(5, reports.Count);
        Assert.All(reports, r => Assert.True(r.Valid, $"{r.BlockDescription} should be valid"));
        Assert.Contains(reports, r => r.BlockDescription.StartsWith("FFS boot block"));
        Assert.Contains(reports, r => r.BlockDescription.StartsWith("FFS root block"));
        Assert.Contains(reports, r => r.BlockDescription.Contains("extension block 1"));
    }
}
