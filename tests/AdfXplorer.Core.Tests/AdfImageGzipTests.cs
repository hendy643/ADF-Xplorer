using System.IO.Compression;
using AdfXplorer.Core.DiskImage;

namespace AdfXplorer.Core.Tests;

public class AdfImageGzipTests
{
    [Fact]
    public void FromGzipFile_DecompressesToIdenticalBlocks()
    {
        var original = TestImageBuilder.BuildMinimalOfsImage("GzipTest", out _, out _);

        string path = Path.GetTempFileName();
        try
        {
            using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var gzip = new GZipStream(fileStream, CompressionLevel.Fastest))
            {
                gzip.Write(original);
            }

            using var gzipImage = AdfImage.FromGzipFile(path);
            using var rawImage = new AdfImage(original);

            Assert.Equal(rawImage.SectorCount, gzipImage.SectorCount);
            for (long block = 0; block < rawImage.SectorCount; block++)
            {
                Assert.Equal(rawImage.ReadBlock(block), gzipImage.ReadBlock(block));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
