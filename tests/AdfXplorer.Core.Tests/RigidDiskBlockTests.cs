using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems;

namespace AdfXplorer.Core.Tests;

public class RigidDiskBlockTests
{
    [Fact]
    public void TryReadPartitions_NonRdbImage_ReturnsNull()
    {
        var imageBytes = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        var image = new AdfImage(imageBytes);

        var (partitions, repaired) = RigidDiskBlock.TryReadPartitions(image);

        Assert.Null(partitions);
        Assert.False(repaired);
    }

    [Fact]
    public void TryReadPartitions_RdbImage_ReturnsBothPartitionsWithExpectedGeometry()
    {
        var imageBytes = RdbTestImageBuilder.Build(
            out int p0Start, out int p0Count, out int p1Start, out int p1Count, out _, out _);
        var image = new AdfImage(imageBytes);

        var (partitions, repaired) = RigidDiskBlock.TryReadPartitions(image);

        Assert.NotNull(partitions);
        Assert.False(repaired);
        Assert.Equal(2, partitions!.Count);

        Assert.Equal(RdbTestImageBuilder.Partition0DriveName, partitions[0].DriveName);
        Assert.Equal(p0Start, partitions[0].StartBlock);
        Assert.Equal(p0Count, partitions[0].BlockCount);

        Assert.Equal(RdbTestImageBuilder.Partition1DriveName, partitions[1].DriveName);
        Assert.Equal(p1Start, partitions[1].StartBlock);
        Assert.Equal(p1Count, partitions[1].BlockCount);
    }

    [Fact]
    public void Partitions_MountIndependentlyWithCorrectIsolatedContent()
    {
        var imageBytes = RdbTestImageBuilder.Build(
            out _, out _, out _, out _, out var p0Content, out var p1Content);
        var image = new AdfImage(imageBytes);
        var (partitions, _) = RigidDiskBlock.TryReadPartitions(image);
        var registry = AmigaFileSystemRegistry.CreateDefault();

        var window0 = image.CreateWindow(partitions![0].StartBlock, partitions[0].BlockCount);
        var fs0 = registry.Mount(window0);
        Assert.Equal("PartitionZero", fs0.VolumeLabel);
        using (var stream = fs0.OpenRead(RdbTestImageBuilder.Partition0FileName))
        using (var reader = new StreamReader(stream))
        {
            Assert.Equal(p0Content, reader.ReadToEnd());
        }

        var window1 = image.CreateWindow(partitions[1].StartBlock, partitions[1].BlockCount);
        var fs1 = registry.Mount(window1);
        Assert.Equal("PartitionOne", fs1.VolumeLabel);
        using (var stream = fs1.OpenRead(RdbTestImageBuilder.Partition1FileName))
        using (var reader = new StreamReader(stream))
        {
            Assert.Equal(p1Content, reader.ReadToEnd());
        }

        // Cross-checks: partition 0 shouldn't see partition 1's file and vice versa.
        Assert.False(fs0.TryGetEntry(RdbTestImageBuilder.Partition1FileName, out _));
        Assert.False(fs1.TryGetEntry(RdbTestImageBuilder.Partition0FileName, out _));
    }

    [Fact]
    public void FromFile_RdbWindowsShareStreamedReadsAndCachedWrites()
    {
        var bytes = RdbTestImageBuilder.Build(
            out int p0Start, out _, out _, out _, out _, out _);
        string path = Path.Combine(Path.GetTempPath(), $"rdb_streamed_{Guid.NewGuid():N}.hdf");
        File.WriteAllBytes(path, bytes);

        try
        {
            using (var image = AdfImage.FromFile(path))
            {
                var (partitions, _) = RigidDiskBlock.TryReadPartitions(image);
                Assert.NotNull(partitions);

                var partition = image.CreateWindow(partitions![0].StartBlock, partitions[0].BlockCount);
                Assert.Equal("DOS", System.Text.Encoding.ASCII.GetString(partition.ReadBlock(0), 0, 3));

                // The root block is shared with the top-level streamed image but belongs to the partition window.
                const int rootNameOffset = 433;
                partition.GetBlockForWrite(20)[rootNameOffset] = (byte)'X';
                image.SaveTo(path);

                Assert.Equal((byte)'X', image.ReadBlock(p0Start + 20)[rootNameOffset]);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
