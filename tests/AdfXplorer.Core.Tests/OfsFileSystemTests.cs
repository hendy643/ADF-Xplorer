using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems;
using AdfXplorer.Core.FileSystems.Ofs;

namespace AdfXplorer.Core.Tests;

public class OfsFileSystemTests
{
    private static IAmigaFileSystem Mount(byte[] imageBytes)
    {
        var image = new AdfImage(imageBytes);
        var fs = OfsFileSystem.TryMount(image);
        Assert.NotNull(fs);
        return fs!;
    }

    [Fact]
    public void VolumeLabel_MatchesBuiltImage()
    {
        var image = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        var fs = Mount(image);

        Assert.Equal("TestDisk", fs.VolumeLabel);
    }

    [Fact]
    public void ListDirectory_Root_ReturnsFileAndSubdirectory()
    {
        var image = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        var fs = Mount(image);

        var entries = fs.ListDirectory("");

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Name == TestImageBuilder.FileName && !e.IsDirectory);
        Assert.Contains(entries, e => e.Name == TestImageBuilder.SubDirName && e.IsDirectory);
    }

    [Fact]
    public void ListDirectory_Subdirectory_ReturnsNestedFile()
    {
        var image = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        var fs = Mount(image);

        var entries = fs.ListDirectory(TestImageBuilder.SubDirName);

        var entry = Assert.Single(entries);
        Assert.Equal(TestImageBuilder.NestedFileName, entry.Name);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void OpenRead_RootFile_ReturnsExactBytes()
    {
        var image = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out var fileContent, out _);
        var fs = Mount(image);

        using var stream = fs.OpenRead(TestImageBuilder.FileName);
        using var reader = new StreamReader(stream);

        Assert.Equal(fileContent, reader.ReadToEnd());
    }

    [Fact]
    public void OpenRead_NestedFile_ReturnsExactBytes()
    {
        var image = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out var nestedContent);
        var fs = Mount(image);

        using var stream = fs.OpenRead($"{TestImageBuilder.SubDirName}/{TestImageBuilder.NestedFileName}");
        using var reader = new StreamReader(stream);

        Assert.Equal(nestedContent, reader.ReadToEnd());
    }

    [Fact]
    public void TryGetEntry_UnknownPath_ReturnsFalse()
    {
        var image = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        var fs = Mount(image);

        Assert.False(fs.TryGetEntry("DOES-NOT-EXIST.TXT", out _));
    }

    [Fact]
    public void TryGetEntry_IsCaseInsensitive()
    {
        var image = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        var fs = Mount(image);

        Assert.True(fs.TryGetEntry("hello.txt", out var entry));
        Assert.Equal(TestImageBuilder.FileName, entry.Name);
    }

    [Fact]
    public void TryMount_RejectsNonOfsImage()
    {
        var garbage = new byte[1760 * AdfImage.SectorSize];
        garbage[0] = (byte)'X';
        var image = new AdfImage(garbage);

        Assert.Null(OfsFileSystem.TryMount(image));
    }

    [Theory]
    [InlineData(2)] // international mode
    [InlineData(4)] // international mode + directory cache
    public void TryMount_AcceptsOfsSubtypeBootFlags(byte flags)
    {
        var imageBytes = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        imageBytes[3] = flags; // OFS bit (0) stays clear; checksum validity is irrelevant to detection

        var fs = OfsFileSystem.TryMount(new AdfImage(imageBytes));

        Assert.NotNull(fs);
        Assert.Equal("TestDisk", fs!.VolumeLabel);
    }

    // Bit 0 of the boot-block flags byte is the OFS/FFS discriminator; odd values here are FFS variants
    // and must never be picked up by the OFS mounter even though the rest of the image is OFS-shaped.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void TryMount_RejectsFfsBootFlags(byte flags)
    {
        var imageBytes = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        imageBytes[3] = flags;

        Assert.Null(OfsFileSystem.TryMount(new AdfImage(imageBytes)));
    }
}
