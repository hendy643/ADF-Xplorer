using AdfXplorer.Core;
using AdfXplorer.Core.DiskImage;

namespace AdfXplorer.Core.Tests;

public class RigidDiskBlockChecksumTests
{
    [Fact]
    public void ScanChecksums_HappyPathImage_ReportsAllBlocksValid()
    {
        var imageBytes = RdbTestImageBuilder.Build(out _, out _, out _, out _, out _, out _);
        var image = new AdfImage(imageBytes);

        var reports = RigidDiskBlock.ScanChecksums(image).ToList();

        Assert.Equal(3, reports.Count); // RDSK + 2 PART blocks
        Assert.All(reports, r => Assert.True(r.Valid, $"{r.BlockDescription} should be valid"));
    }

    [Fact]
    public void TryReadPartitions_CorruptedRdsk_DefaultsToIgnore()
    {
        var imageBytes = RdbTestImageBuilder.Build(out _, out _, out _, out _, out _, out _);
        Corrupt(imageBytes, RdbTestImageBuilder.RdskBlockNumber, 8);
        var image = new AdfImage(imageBytes);

        var (partitions, repaired) = RigidDiskBlock.TryReadPartitions(image);

        Assert.NotNull(partitions);
        Assert.Equal(2, partitions!.Count);
        Assert.False(repaired);
    }

    // Unlike a rejected PART block (which just drops that one partition), a rejected RDSK block means
    // the partition table itself can't be trusted, so the whole image falls back to "not RDB-partitioned"
    // rather than reporting a partial/corrupt partition list.
    [Fact]
    public void TryReadPartitions_RejectRdsk_FallsBackToNoRdb()
    {
        var imageBytes = RdbTestImageBuilder.Build(out _, out _, out _, out _, out _, out _);
        Corrupt(imageBytes, RdbTestImageBuilder.RdskBlockNumber, 8);
        var image = new AdfImage(imageBytes);

        var (partitions, repaired) = RigidDiskBlock.TryReadPartitions(
            image, (_, _, _) => ChecksumDecision.Reject);

        Assert.Null(partitions);
        Assert.False(repaired);
    }

    [Fact]
    public void TryReadPartitions_RejectOnePartition_OmitsOnlyThatPartition()
    {
        var imageBytes = RdbTestImageBuilder.Build(out _, out _, out _, out _, out _, out _);
        Corrupt(imageBytes, RdbTestImageBuilder.Partition0BlockNumber, 8);
        var image = new AdfImage(imageBytes);

        var (partitions, repaired) = RigidDiskBlock.TryReadPartitions(
            image, (desc, _, _) => desc.Contains("PART") ? ChecksumDecision.Reject : ChecksumDecision.Ignore);

        Assert.NotNull(partitions);
        Assert.False(repaired);
        var partition = Assert.Single(partitions!);
        Assert.Equal(RdbTestImageBuilder.Partition1DriveName, partition.DriveName);
    }

    [Fact]
    public void TryReadPartitions_RepairRdsk_MarksRepairedAndPersistsInMemory()
    {
        var imageBytes = RdbTestImageBuilder.Build(out _, out _, out _, out _, out _, out _);
        Corrupt(imageBytes, RdbTestImageBuilder.RdskBlockNumber, 8);
        var image = new AdfImage(imageBytes);

        var (partitions, repaired) = RigidDiskBlock.TryReadPartitions(
            image, (_, _, _) => ChecksumDecision.Repair);

        Assert.NotNull(partitions);
        Assert.True(repaired);

        // A second pass with a handler that must never be invoked confirms the in-memory block now
        // validates cleanly.
        var (secondPass, secondRepaired) = RigidDiskBlock.TryReadPartitions(
            image, (desc, stored, computed) => throw new InvalidOperationException(
                $"Unexpected mismatch after repair: {desc} stored=0x{stored:X8} computed=0x{computed:X8}"));

        Assert.NotNull(secondPass);
        Assert.False(secondRepaired);
    }

    private static void Corrupt(byte[] image, int blockNumber, int checksumFieldOffset)
    {
        int offset = blockNumber * AdfImage.SectorSize + checksumFieldOffset;
        image[offset] ^= 0xFF;
    }
}
