using System.Buffers.Binary;
using System.Text;
using AdfXplorer.Core;
using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems;
using AdfXplorer.Core.FileSystems.Ofs;

namespace AdfXplorer.Core.Tests;

public class OfsChecksumTests
{
    /// <summary>Callback for a second <c>ValidateChecksums</c> pass after a repair: the only prompt that
    /// should still fire is <see cref="OfsFileSystem.LaterEntryPolicyQuestion"/> (a policy question, not
    /// an actual checksum mismatch) - anything else means the repair didn't stick.</summary>
    private static ChecksumDecision AssertNoRealMismatch(string desc, uint stored, uint computed) =>
        desc == OfsFileSystem.LaterEntryPolicyQuestion
            ? ChecksumDecision.Ignore
            : throw new InvalidOperationException($"Unexpected mismatch after repair: {desc}");

    private static IChecksumAware Mount(byte[] imageBytes)
    {
        var image = new AdfImage(imageBytes);
        var fs = OfsFileSystem.TryMount(image);
        Assert.NotNull(fs);
        return Assert.IsAssignableFrom<IChecksumAware>(fs);
    }

    [Fact]
    public void ValidateChecksums_HealthyImage_NeverAsksLaterEntryPolicy()
    {
        var imageBytes = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        var aware = Mount(imageBytes);

        bool ok = aware.ValidateChecksums(
            (desc, _, _) => throw new InvalidOperationException($"Unexpected prompt: {desc}"), out bool repaired);

        Assert.True(ok);
        Assert.False(repaired);
    }

    [Fact]
    public void ValidateChecksums_CreatedViaRealWriter_SavedAndReloaded_NoChecksumMismatch()
    {
        var created = OfsFileSystemWriter.CreateBlank(1760, "TestDisk");
        string path = Path.Combine(Path.GetTempPath(), $"checksum_repro_{Guid.NewGuid():N}.adf");
        created.SaveTo(path);
        try
        {
            var image = AdfImage.FromFile(path);
            var fs = AmigaFileSystemRegistry.CreateDefault().Mount(image);
            var aware = Assert.IsAssignableFrom<IChecksumAware>(fs);

            bool ok = aware.ValidateChecksums(
                (desc, stored, computed) => throw new InvalidOperationException(
                    $"Unexpected mismatch: {desc} stored=0x{stored:X8} computed=0x{computed:X8}"),
                out bool repaired);

            Assert.True(ok);
            Assert.False(repaired);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScanChecksums_HappyPathImage_ReportsEverythingValid()
    {
        var imageBytes = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        var aware = Mount(imageBytes);

        var reports = aware.ScanChecksums().ToList();

        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.True(r.Valid, $"{r.BlockDescription} should be valid"));
    }

    [Fact]
    public void ValidateChecksums_CorruptedRoot_DefaultIgnore_StillMounts()
    {
        var imageBytes = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        Corrupt(imageBytes, TestImageBuilder.RootBlockNumber, 20);
        var aware = Mount(imageBytes);

        bool ok = aware.ValidateChecksums(null, out bool repaired);

        Assert.True(ok);
        Assert.False(repaired);
    }

    [Fact]
    public void ValidateChecksums_RejectRoot_Fails()
    {
        var imageBytes = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        Corrupt(imageBytes, TestImageBuilder.RootBlockNumber, 20);
        var aware = Mount(imageBytes);

        bool ok = aware.ValidateChecksums((_, _, _) => ChecksumDecision.Reject, out bool repaired);

        Assert.False(ok);
        Assert.False(repaired);
    }

    [Fact]
    public void ValidateChecksums_RepairRoot_MarksRepaired_AndPersistsInMemory()
    {
        var imageBytes = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        Corrupt(imageBytes, TestImageBuilder.RootBlockNumber, 20);
        var aware = Mount(imageBytes);

        bool ok = aware.ValidateChecksums((_, _, _) => ChecksumDecision.Repair, out bool repaired);
        Assert.True(ok);
        Assert.True(repaired);

        // Repair must fix the checksum in the in-memory image, not just report success - re-validating
        // the same (unsaved) image should now find nothing wrong.
        bool secondOk = aware.ValidateChecksums(AssertNoRealMismatch, out bool secondRepaired);
        Assert.True(secondOk);
        Assert.False(secondRepaired);
    }

    [Fact]
    public void CorruptedFileHeader_DefaultIgnorePolicy_StillListedAndReadable()
    {
        var imageBytes = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out var content, out _);
        Corrupt(imageBytes, TestImageBuilder.FileHeaderBlockNumber, 20);
        var fs = (IAmigaFileSystem)Mount(imageBytes);

        var entries = fs.ListDirectory("");
        Assert.Contains(entries, e => e.Name == TestImageBuilder.FileName);
        Assert.True(fs.TryGetEntry(TestImageBuilder.FileName, out _));

        using var stream = fs.OpenRead(TestImageBuilder.FileName);
        using var reader = new StreamReader(stream);
        Assert.Equal(content, reader.ReadToEnd());
    }

    [Fact]
    public void CorruptedFileHeader_RejectPolicy_OmittedFromListingAndLookup()
    {
        var imageBytes = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        Corrupt(imageBytes, TestImageBuilder.FileHeaderBlockNumber, 20);
        var aware = Mount(imageBytes);
        Assert.True(aware.ValidateChecksums((_, _, _) => ChecksumDecision.Reject, out _));
        var fs = (IAmigaFileSystem)aware;

        var entries = fs.ListDirectory("");
        Assert.DoesNotContain(entries, e => e.Name == TestImageBuilder.FileName);
        Assert.False(fs.TryGetEntry(TestImageBuilder.FileName, out _));

        // The sibling directory is unaffected.
        Assert.Contains(entries, e => e.Name == TestImageBuilder.SubDirName);
    }

    // A rejected data block can't just be skipped mid-file (there'd be no way to know how many bytes it
    // was supposed to contribute) - the only safe behavior is to stop the stream at the last good block.
    [Fact]
    public void CorruptedDataBlock_RejectPolicy_TruncatesStreamAtLastGoodBlock()
    {
        var imageBytes = BuildTwoDataBlockImage(out string firstChunk, out int secondBlock);
        Corrupt(imageBytes, secondBlock, 20);
        var aware = Mount(imageBytes);
        Assert.True(aware.ValidateChecksums((_, _, _) => ChecksumDecision.Reject, out _));
        var fs = (IAmigaFileSystem)aware;

        using var stream = fs.OpenRead("BIG.TXT");
        using var reader = new StreamReader(stream);
        Assert.Equal(firstChunk, reader.ReadToEnd());
    }

    private static void Corrupt(byte[] image, int blockNumber, int checksumFieldOffset)
    {
        int offset = blockNumber * AdfImage.SectorSize + checksumFieldOffset;
        image[offset] ^= 0xFF;
    }

    /// <summary>Minimal OFS image with a single file split across two data blocks, used to test
    /// mid-chain truncation on a rejected data block.</summary>
    private static byte[] BuildTwoDataBlockImage(out string firstChunk, out int secondDataBlock)
    {
        const int SectorSize = 512;
        const int TotalSectors = 1760;
        const int RootBlock = 880;
        const int FileHeaderBlock = 881;
        const int DataBlock1 = 882;
        const int DataBlock2 = 883;

        firstChunk = "AAAA";
        const string secondChunk = "BBBB";
        secondDataBlock = DataBlock2;

        var image = new byte[TotalSectors * SectorSize];
        image[0] = (byte)'D';
        image[1] = (byte)'O';
        image[2] = (byte)'S';
        image[3] = 0;
        WriteInt32(image, 8, RootBlock);

        int rootOff = RootBlock * SectorSize;
        WriteInt32(image, rootOff + 0, 2);
        WriteInt32(image, rootOff + 12, 72);
        WriteInt32(image, rootOff + 312, -1);
        WriteInt32(image, rootOff + 24, FileHeaderBlock);
        WriteBcplString(image, rootOff + 432, "TwoBlockDisk", 30);
        WriteInt32(image, rootOff + 508, 1);

        int fileOff = FileHeaderBlock * SectorSize;
        WriteInt32(image, fileOff + 0, 2);
        WriteInt32(image, fileOff + 16, DataBlock1);
        WriteInt32(image, fileOff + 324, firstChunk.Length + secondChunk.Length);
        WriteBcplString(image, fileOff + 432, "BIG.TXT", 30);
        WriteInt32(image, fileOff + 500, RootBlock);
        WriteInt32(image, fileOff + 508, -3);

        WriteDataBlock(image, DataBlock1, FileHeaderBlock, firstChunk, nextData: DataBlock2);
        WriteDataBlock(image, DataBlock2, FileHeaderBlock, secondChunk, nextData: 0);

        ChecksumTestHelper.WriteBootChecksum(image);
        ChecksumTestHelper.WriteNormalChecksum(image, RootBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, FileHeaderBlock);
        ChecksumTestHelper.WriteNormalChecksum(image, DataBlock1);
        ChecksumTestHelper.WriteNormalChecksum(image, DataBlock2);

        return image;
    }

    private static void WriteDataBlock(byte[] image, int block, int headerKey, string content, int nextData)
    {
        int off = block * 512;
        var bytes = Encoding.Latin1.GetBytes(content);
        WriteInt32(image, off + 0, 8);
        WriteInt32(image, off + 4, headerKey);
        WriteInt32(image, off + 8, 1);
        WriteInt32(image, off + 12, bytes.Length);
        WriteInt32(image, off + 16, nextData);
        bytes.CopyTo(image, off + 24);
    }

    private static void WriteInt32(byte[] image, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(offset, 4), value);

    private static void WriteBcplString(byte[] image, int offset, string value, int maxLen)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        int len = Math.Min(bytes.Length, maxLen);
        image[offset] = (byte)len;
        Array.Copy(bytes, 0, image, offset + 1, len);
    }
}
