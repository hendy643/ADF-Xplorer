using System.Text;
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
    public void FileSystemName_ReportsAmigaOfs()
    {
        var image = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);

        Assert.Equal("Amiga OFS", Mount(image).FileSystemName);
    }

    [Fact]
    public void TryMount_UsesMidpointRootWhenBootPointerIsZero()
    {
        var image = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        Array.Clear(image, 8, 4);
        ChecksumTestHelper.WriteBootChecksum(image);

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
    public void OpenRead_RootFile_SupportsSeekingAndStreaming()
    {
        var image = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out var fileContent, out _);
        var fs = Mount(image);

        using var stream = fs.OpenRead(TestImageBuilder.FileName);
        Assert.True(stream.CanSeek);
        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);

        stream.Seek(1, SeekOrigin.Begin);
        byte[] buf = new byte[4];
        int read = stream.Read(buf, 0, 4);
        Assert.Equal(4, read);
        Assert.Equal(fileContent[1..5], Encoding.Latin1.GetString(buf));
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

    [Fact]
    public void TryMount_WhenSectorCountExceeds4GiB_ReturnsNull()
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite))
            {
                long sectorCount = (long)OfsFileSystem.MaxSupportedSectors + 1;
                fs.SetLength(sectorCount * AdfImage.SectorSize);

                byte[] bootBlock = new byte[1024];
                bootBlock[0] = (byte)'D';
                bootBlock[1] = (byte)'O';
                bootBlock[2] = (byte)'S';
                bootBlock[3] = 0; // OFS
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bootBlock.AsSpan(8, 4), (int)(sectorCount / 2));
                ChecksumTestHelper.WriteBootChecksum(bootBlock);

                fs.Position = 0;
                fs.Write(bootBlock);
            }

            using var image = AdfImage.FromFile(tempPath);
            Assert.Equal(OfsFileSystem.MaxSupportedSectors + 1, image.SectorCount);
            Assert.Null(OfsFileSystem.TryMount(image));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void TryMount_WhenSectorCountIsExactly4GiB_MountsSuccessfully()
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            int sectorCount = OfsFileSystem.MaxSupportedSectors;
            int rootBlock = sectorCount / 2;

            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.SetLength((long)sectorCount * AdfImage.SectorSize);

                byte[] boot = new byte[1024];
                boot[0] = (byte)'D';
                boot[1] = (byte)'O';
                boot[2] = (byte)'S';
                boot[3] = 0; // OFS
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(boot.AsSpan(8, 4), rootBlock);
                ChecksumTestHelper.WriteBootChecksum(boot);

                byte[] root = new byte[512];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(root.AsSpan(0, 4), 2); // Header
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(root.AsSpan(12, 4), 72); // HashTableSize
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(root.AsSpan(312, 4), -1); // BitmapFlag
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(root.AsSpan(316, 4), rootBlock + 1); // BitmapPages
                root[432] = 7; // Name length
                System.Text.Encoding.Latin1.GetBytes("4GiBOfs").CopyTo(root.AsSpan(433, 7));
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(root.AsSpan(508, 4), 1); // SecType.Root
                ChecksumTestHelper.WriteNormalChecksum(root, 0);

                fs.Position = 0;
                fs.Write(boot);

                fs.Position = (long)rootBlock * AdfImage.SectorSize;
                fs.Write(root);
            }

            using var image = AdfImage.FromFile(tempPath);
            Assert.Equal(sectorCount, image.SectorCount);

            var mounted = OfsFileSystem.TryMount(image);
            Assert.NotNull(mounted);
            Assert.Equal("4GiBOfs", mounted!.VolumeLabel);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
