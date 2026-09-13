using System.Buffers.Binary;
using AdfXplorer.Core.FileSystems;
using AdfXplorer.Core.FileSystems.Ofs;

namespace AdfXplorer.Core.Tests;

public class OfsFileSystemWriterTests
{
    [Theory]
    [InlineData(1760)] // 880 KB DD floppy
    [InlineData(3520)] // 1.76 MB HD floppy
    public void CreateBlank_MountsAsEmptyVolumeWithAllChecksumsValid(int sectorCount)
    {
        var image = OfsFileSystemWriter.CreateBlank(sectorCount, "BlankDisk");

        var fs = OfsFileSystem.TryMount(image);
        Assert.NotNull(fs);
        Assert.Equal("BlankDisk", fs!.VolumeLabel);
        Assert.Empty(fs.ListDirectory(""));

        var aware = Assert.IsAssignableFrom<IChecksumAware>(fs);
        var reports = aware.ScanChecksums().ToList();

        // Boot block + root block (the bitmap block isn't reachable via ScanChecksums' directory-tree
        // walk - nothing in OFS's own structures references it as a "directory entry" - so it's
        // validated directly, independently, below).
        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.True(r.Valid, $"{r.BlockDescription} should be valid"));
    }

    // Independently re-implements the "normal" checksum algorithm (same rationale as
    // ChecksumTestHelper: two independent implementations agreeing is a better check than reusing
    // AdfXplorer.Core's internal implementation, which isn't visible to this assembly anyway) to verify
    // the bitmap block, which ScanChecksums doesn't cover.
    [Theory]
    [InlineData(1760)]
    [InlineData(3520)]
    public void CreateBlank_BitmapBlock_HasCorrectChecksumAndBitPolarity(int sectorCount)
    {
        const int SectorSize = 512;
        var image = OfsFileSystemWriter.CreateBlank(sectorCount, "BlankDisk");
        int rootBlock = sectorCount / 2;
        int bitmapBlock = rootBlock + 1;

        var block = image.ReadBlock(bitmapBlock);
        uint stored = BinaryPrimitives.ReadUInt32BigEndian(block[..4]);
        uint computed = ComputeNormalChecksum(block);
        Assert.Equal(computed, stored);

        Assert.False(IsFree(block, rootBlock));
        Assert.False(IsFree(block, bitmapBlock));
        Assert.True(IsFree(block, 2)); // first usable block after the boot blocks
        Assert.True(IsFree(block, sectorCount - 1)); // last block on the volume

        static uint ComputeNormalChecksum(ReadOnlySpan<byte> block)
        {
            uint sum = 0;
            for (int i = 0; i < SectorSize / 4; i++)
            {
                int byteOffset = i * 4;
                if (byteOffset == 0)
                {
                    continue;
                }

                sum += BinaryPrimitives.ReadUInt32BigEndian(block.Slice(byteOffset, 4));
            }

            return unchecked((uint)-(int)sum);
        }
    }

    /// <summary>Decodes the OFS bitmap block's bit-per-block allocation map. Block 0/1 (boot block) are
    /// never represented (hence <c>blockNumber - 2</c>), and a set bit means free, not allocated.</summary>
    private static bool IsFree(ReadOnlySpan<byte> bitmapBlock, int blockNumber)
    {
        int sectOfMap = blockNumber - 2;
        int mapIndex = (sectOfMap / 32) % 127;
        int bitPos = sectOfMap % 32;
        uint word = BinaryPrimitives.ReadUInt32BigEndian(bitmapBlock.Slice(4 + mapIndex * 4, 4));
        return (word & (1u << bitPos)) != 0;
    }

    [Fact]
    public void CreateBlank_WhenSectorCountIs4GiB_CreatesAndMountsSuccessfully()
    {
        var image = OfsFileSystemWriter.CreateBlank(8_388_608, "BigOfsDisk");
        var fs = OfsFileSystem.TryMount(image);
        Assert.NotNull(fs);
        Assert.Equal("BigOfsDisk", fs!.VolumeLabel);
    }

    [Fact]
    public void CreateBlank_WhenSectorCountExceeds4GiB_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OfsFileSystemWriter.CreateBlank(8_388_609, "OversizedDisk"));
    }

    [Fact]
    public void CreateBlank_WhenSectorCountLessThan4_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OfsFileSystemWriter.CreateBlank(3, "TooSmallDisk"));
    }
}
