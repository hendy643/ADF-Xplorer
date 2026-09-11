using System.Text;
using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems;

namespace AdfXplorer.Core.Tests;

public class RigidDiskBlockWriterTests
{
    // RDB dos_type values: ASCII "DOS"/"SFS" packed into the high 3 bytes, low byte selects the variant
    // (OFS=0, FFS=1). "SFS\0" is a foreign filesystem AdfXplorer doesn't implement, used to test that
    // such partitions are still correctly cataloged even though they can't be mounted.
    private const uint Dos0Ofs = 0x444F5300;
    private const uint Dos1Ffs = 0x444F5301;
    private const uint SfsZero = 0x53465300;

    [Fact]
    public void Create_SinglePartitionOfsNoDriver_IsReadableAndMountable()
    {
        var image = RigidDiskBlockWriter.Create([new HdfPartitionSpec("DH0", SizeMegabytes: 1, Dos0Ofs)]);

        var (partitions, repaired) = RigidDiskBlock.TryReadPartitions(image);

        Assert.NotNull(partitions);
        Assert.False(repaired);
        Assert.Single(partitions!);
        Assert.Equal("DH0", partitions![0].DriveName);

        var window = image.CreateWindow(partitions[0].StartBlock, partitions[0].BlockCount);
        var fs = AmigaFileSystemRegistry.CreateDefault().Mount(window);
        Assert.Equal("DH0", fs.VolumeLabel);
        Assert.Empty(fs.ListDirectory(""));
    }

    [Fact]
    public void Create_MultiPartition_FfsIsWritableAndForeignDosTypeIsListedButNotMountable()
    {
        var image = RigidDiskBlockWriter.Create(
        [
            new HdfPartitionSpec("Work", SizeMegabytes: 2, Dos1Ffs),
            new HdfPartitionSpec("SFSVol", SizeMegabytes: 2, SfsZero),
        ]);

        var (partitions, repaired) = RigidDiskBlock.TryReadPartitions(image);

        Assert.NotNull(partitions);
        Assert.False(repaired);
        Assert.Equal(2, partitions!.Count);
        Assert.Equal("Work", partitions[0].DriveName);
        Assert.Equal("SFSVol", partitions[1].DriveName);

        // FFS partition: fully usable - write a file through the writer interface, read it back.
        var ffsWindow = image.CreateWindow(partitions[0].StartBlock, partitions[0].BlockCount);
        var ffs = AmigaFileSystemRegistry.CreateDefault().Mount(ffsWindow);
        var writer = Assert.IsAssignableFrom<IAmigaFileSystemWriter>(ffs);
        writer.CreateFile("hello.txt");
        var content = Encoding.Latin1.GetBytes("Hello from a created HDF partition!");
        writer.WriteFile("hello.txt", 0, content);

        using (var stream = ffs.OpenRead("hello.txt"))
        using (var reader = new StreamReader(stream, Encoding.Latin1))
        {
            Assert.Equal("Hello from a created HDF partition!", reader.ReadToEnd());
        }

        // Foreign DosType partition: correctly reserved/listed but not claimed as mountable by us.
        var sfsWindow = image.CreateWindow(partitions[1].StartBlock, partitions[1].BlockCount);
        Assert.Throws<NotSupportedException>(() => AmigaFileSystemRegistry.CreateDefault().Mount(sfsWindow));
    }

    [Fact]
    public void Create_PartitionWithDriverImage_EmbedsAndRoundTripsDriverBytesViaFshdAndLsegChain()
    {
        // Deliberately not a multiple of 4, and larger than one LoadSegBlock's 492-byte payload, to
        // exercise both zero-padding and multi-block chaining.
        var driver = new byte[1200];
        for (int i = 0; i < driver.Length; i++)
        {
            driver[i] = (byte)(i % 251);
        }

        var image = RigidDiskBlockWriter.Create(
            [new HdfPartitionSpec("SFSVol", SizeMegabytes: 1, SfsZero, driver)]);

        // Walk rdb_FileSysHeaderList -> FSHD -> LoadSegBlock chain directly off the raw image.
        var rdsk = image.ReadBlock(0);
        int fshdBlock = ReadInt32(rdsk, 32); // rdb_FileSysHeaderList
        Assert.True(fshdBlock > 0);

        var fshd = image.ReadBlock(fshdBlock);
        Assert.Equal("FSHD", Signature(fshd));
        Assert.Equal(SfsZero, ReadUInt32(fshd, 32)); // dos_type
        uint patchFlags = ReadUInt32(fshd, 40);
        Assert.Equal(0x80u, patchFlags & 0x80u);
        int firstLseg = ReadInt32(fshd, 72); // dn_SegListBlk
        Assert.True(firstLseg > 0);

        var reconstructed = new List<byte>();
        int lsegBlock = firstLseg;
        while (lsegBlock > 0 && lsegBlock != -1)
        {
            var block = image.ReadBlock(lsegBlock);
            Assert.Equal("LSEG", Signature(block));
            int sizeLongs = ReadInt32(block, 4);
            int payloadBytes = sizeLongs * 4 - 20;
            reconstructed.AddRange(block.Slice(20, payloadBytes).ToArray());
            lsegBlock = ReadInt32(block, 16); // next
        }

        int paddedLength = ((driver.Length + 3) / 4) * 4;
        var expected = new byte[paddedLength];
        driver.CopyTo(expected, 0);

        Assert.Equal(expected, reconstructed.ToArray());
    }

    [Fact]
    public void Create_WithDriverImage_AllRdbFamilyBlocksHaveValidChecksums()
    {
        var driver = new byte[1000];
        for (int i = 0; i < driver.Length; i++)
        {
            driver[i] = (byte)i;
        }

        var image = RigidDiskBlockWriter.Create(
        [
            new HdfPartitionSpec("Work", SizeMegabytes: 1, Dos1Ffs),
            new HdfPartitionSpec("SFSVol", SizeMegabytes: 1, SfsZero, driver),
        ]);

        var reports = RigidDiskBlock.ScanChecksums(image).ToList();

        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.True(r.Valid, $"{r.BlockDescription}: stored=0x{r.Stored:X8} computed=0x{r.Computed:X8}"));

        // RDSK + 2 PART + 1 FSHD + at least 1 LSEG.
        Assert.True(reports.Count >= 5);
        Assert.Contains(reports, r => r.BlockDescription.StartsWith("FSHD"));
        Assert.Contains(reports, r => r.BlockDescription.StartsWith("LSEG"));
    }

    [Fact]
    public void Create_TotalSizeExceedsArrayLimit_ThrowsDescriptiveException()
    {
        var ex = Assert.Throws<ArgumentException>(() => RigidDiskBlockWriter.Create(
        [
            new HdfPartitionSpec("DH0", SizeMegabytes: 1024, Dos0Ofs + 5),
            new HdfPartitionSpec("DH1", SizeMegabytes: 1024, Dos0Ofs + 5),
        ]));

        Assert.Contains("exceeds", ex.Message);
    }

    private static string Signature(ReadOnlySpan<byte> block) =>
        Encoding.ASCII.GetString(block[..4]);

    private static int ReadInt32(ReadOnlySpan<byte> block, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(block.Slice(offset, 4));

    private static uint ReadUInt32(ReadOnlySpan<byte> block, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(block.Slice(offset, 4));
}
