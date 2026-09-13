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
    public void OpenRead_BigFile_SupportsSeekingAndPartialStreamingReads()
    {
        var image = FfsTestImageBuilder.Build(out _, out var bigContent);
        var fs = Mount(image);

        using var stream = fs.OpenRead(FfsTestImageBuilder.BigFileName);
        Assert.True(stream.CanSeek);
        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.Equal(bigContent.Length, stream.Length);

        // Read a 100-byte slice from offset 1000
        stream.Seek(1000, SeekOrigin.Begin);
        Assert.Equal(1000, stream.Position);

        var slice = new byte[100];
        int bytesRead = stream.Read(slice, 0, 100);
        Assert.Equal(100, bytesRead);
        Assert.Equal(bigContent.AsSpan(1000, 100).ToArray(), slice);
        Assert.Equal(1100, stream.Position);

        // Seek from current position
        stream.Seek(-50, SeekOrigin.Current);
        Assert.Equal(1050, stream.Position);
        byte[] slice2 = new byte[20];
        stream.ReadExactly(slice2);
        Assert.Equal(bigContent.AsSpan(1050, 20).ToArray(), slice2);

        // Seek from end
        stream.Seek(-30, SeekOrigin.End);
        Assert.Equal(bigContent.Length - 30, stream.Position);
        byte[] slice3 = new byte[30];
        stream.ReadExactly(slice3);
        Assert.Equal(bigContent.AsSpan(bigContent.Length - 30, 30).ToArray(), slice3);
        Assert.Equal(0, stream.Read(slice3, 0, 10)); // at EOF
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

    [Fact]
    public void TryMount_WhenSectorCountExceeds4GiB_ReturnsNull()
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite))
            {
                long sectorCount = (long)FfsFileSystem.MaxSupportedSectors + 1;
                fs.SetLength(sectorCount * AdfImage.SectorSize);

                byte[] bootBlock = new byte[1024];
                bootBlock[0] = (byte)'D';
                bootBlock[1] = (byte)'O';
                bootBlock[2] = (byte)'S';
                bootBlock[3] = 1; // FFS
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bootBlock.AsSpan(8, 4), (int)(sectorCount / 2));
                ChecksumTestHelper.WriteBootChecksum(bootBlock);

                fs.Position = 0;
                fs.Write(bootBlock);
            }

            using var image = AdfImage.FromFile(tempPath);
            Assert.Equal(FfsFileSystem.MaxSupportedSectors + 1, image.SectorCount);
            Assert.Null(FfsFileSystem.TryMount(image));
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
            int sectorCount = FfsFileSystem.MaxSupportedSectors;
            int rootBlock = sectorCount / 2;

            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.SetLength((long)sectorCount * AdfImage.SectorSize);

                byte[] boot = new byte[1024];
                boot[0] = (byte)'D';
                boot[1] = (byte)'O';
                boot[2] = (byte)'S';
                boot[3] = 1; // FFS
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(boot.AsSpan(8, 4), rootBlock);
                ChecksumTestHelper.WriteBootChecksum(boot);

                byte[] root = new byte[512];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(root.AsSpan(0, 4), 2); // Header
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(root.AsSpan(12, 4), 72); // HashTableSize
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(root.AsSpan(312, 4), -1); // BitmapFlag
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(root.AsSpan(316, 4), rootBlock + 1); // BitmapPages
                root[432] = 7; // Name length
                System.Text.Encoding.Latin1.GetBytes("4GiBFfs").CopyTo(root.AsSpan(433, 7));
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(root.AsSpan(508, 4), 1); // SecType.Root
                ChecksumTestHelper.WriteNormalChecksum(root, 0);

                fs.Position = 0;
                fs.Write(boot);

                fs.Position = (long)rootBlock * AdfImage.SectorSize;
                fs.Write(root);
            }

            using var image = AdfImage.FromFile(tempPath);
            Assert.Equal(sectorCount, image.SectorCount);

            var mounted = FfsFileSystem.TryMount(image);
            Assert.NotNull(mounted);
            Assert.Equal("4GiBFfs", mounted!.VolumeLabel);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
