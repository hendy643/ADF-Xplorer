using AdfXplorer.Core.DiskImage;

namespace AdfXplorer.Core.Tests;

public class AdfImageTests
{
    [Fact]
    public void FromFile_LargeDiskImage_OpensWithoutArtificialSectorCap()
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            // 5 GiB image (10,485,760 sectors)
            long sectorCount = 10_485_760L;
            using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.SetLength(sectorCount * AdfImage.SectorSize);
            }

            using var image = AdfImage.FromFile(tempPath);

            Assert.Equal(sectorCount, image.SectorCount);
            Assert.Equal(sectorCount * AdfImage.SectorSize, image.SizeInBytes);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void CreateEmpty_HugeTerabyteCapacity_AllocatesIndicesOnlyWithoutMemorySpike()
    {
        // 1 TiB image (2,147,483,648 sectors) - exceeds int.MaxValue
        long sectorCount = 2_147_483_648L;
        using var image = AdfImage.CreateEmpty(sectorCount);

        Assert.Equal(sectorCount, image.SectorCount);
        Assert.Equal(sectorCount * AdfImage.SectorSize, image.SizeInBytes);

        // Accessing high block numbers
        long highBlock = sectorCount - 1;
        var block = image.GetBlockForWrite(highBlock);
        block[0] = 0x55;
        block[511] = 0xAA;

        var readBack = image.ReadBlock(highBlock);
        Assert.Equal(0x55, readBack[0]);
        Assert.Equal(0xAA, readBack[511]);

        // Unmodified block is zeroed
        var zeroBlock = image.ReadBlock(highBlock - 1);
        Assert.All(zeroBlock, b => Assert.Equal(0, b));
    }

    [Fact]
    public void FromFile_NonMultipleOfSectorSize_ThrowsArgumentException()
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempPath, new byte[513]);
            Assert.Throws<ArgumentException>(() => AdfImage.FromFile(tempPath));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void CreateWindow_SubWindow_ProvidesAccurateBlockAccess()
    {
        var buffer = new byte[4 * AdfImage.SectorSize];
        buffer[2 * AdfImage.SectorSize] = 0x42;
        var rootImage = new AdfImage(buffer);

        var window = rootImage.CreateWindow(startBlock: 2, blockCount: 2);

        Assert.Equal(2, window.SectorCount);
        Assert.Equal(2 * AdfImage.SectorSize, window.SizeInBytes);
        var block0 = window.ReadBlock(0);
        Assert.Equal(0x42, block0[0]);
    }

    [Fact]
    public void CreateEmpty_SparseImage_SavesToFileWithCorrectLengthAndBlocks()
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            const int sectorCount = 8_388_608; // 4 GiB
            using var image = AdfImage.CreateEmpty(sectorCount);
            Assert.Equal(sectorCount, image.SectorCount);
            Assert.Equal((long)sectorCount * AdfImage.SectorSize, image.SizeInBytes);

            // Read untouched block returns zeroes
            var emptyBlock = image.ReadBlock(100);
            Assert.All(emptyBlock, b => Assert.Equal(0, b));

            // Write to block
            var block = image.GetBlockForWrite(100);
            block[0] = 0xAA;
            block[511] = 0xBB;

            image.SaveTo(tempPath);

            var fileInfo = new FileInfo(tempPath);
            Assert.Equal((long)sectorCount * AdfImage.SectorSize, fileInfo.Length);

            using var reloaded = AdfImage.FromFile(tempPath);
            var reloadedBlock = reloaded.ReadBlock(100);
            Assert.Equal(0xAA, reloadedBlock[0]);
            Assert.Equal(0xBB, reloadedBlock[511]);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void RollbackTransaction_AfterPartialWrite_RestoresPreExistingBlockContent()
    {
        var image = AdfImage.CreateEmpty(4);

        // Block 1 already holds real data before the transaction starts.
        var preexisting = image.GetBlockForWrite(1);
        preexisting[0] = 0x11;
        preexisting[10] = 0x22;

        image.BeginTransaction();
        var touched = image.GetBlockForWrite(1);
        touched[0] = 0xFF; // overwrite part of the pre-existing block, like WriteToDataBlock would
        touched[10] = 0xFF;
        image.GetBlockForWrite(2)[0] = 0xEE; // a brand-new block allocated mid-operation

        image.RollbackTransaction();

        var restored = image.ReadBlock(1);
        Assert.Equal(0x11, restored[0]);
        Assert.Equal(0x22, restored[10]);

        var newBlock = image.ReadBlock(2);
        Assert.All(newBlock, b => Assert.Equal(0, b));
    }

    [Fact]
    public void CommitTransaction_KeepsChangesAndStopsJournaling()
    {
        var image = AdfImage.CreateEmpty(2);

        image.BeginTransaction();
        image.GetBlockForWrite(0)[0] = 0x42;
        image.CommitTransaction();

        // A write after commit must not be rolled back by a later, unrelated rollback call.
        image.RollbackTransaction();

        Assert.Equal(0x42, image.ReadBlock(0)[0]);
    }
}
