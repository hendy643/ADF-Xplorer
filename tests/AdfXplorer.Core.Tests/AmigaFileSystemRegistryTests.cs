using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems;

namespace AdfXplorer.Core.Tests;

public class AmigaFileSystemRegistryTests
{
    [Fact]
    public void Mount_RecognizesOfsImage()
    {
        var imageBytes = TestImageBuilder.BuildMinimalOfsImage("TestDisk", out _, out _);
        var image = new AdfImage(imageBytes);

        var fs = AmigaFileSystemRegistry.CreateDefault().Mount(image);

        Assert.Equal("TestDisk", fs.VolumeLabel);
    }

    [Fact]
    public void Mount_RecognizesFfsImage()
    {
        var imageBytes = FfsTestImageBuilder.Build(out _, out _);
        var image = new AdfImage(imageBytes);

        var fs = AmigaFileSystemRegistry.CreateDefault().Mount(image);

        Assert.Equal("FfsTestDisk", fs.VolumeLabel);
    }

    [Fact]
    public void Mount_ThrowsWithSignatureForUnrecognizedImage()
    {
        var garbage = new byte[1760 * AdfImage.SectorSize];
        garbage[0] = (byte)'X';
        garbage[1] = (byte)'Y';
        garbage[2] = (byte)'Z';
        garbage[3] = 9;
        var image = new AdfImage(garbage);

        var ex = Assert.Throws<NotSupportedException>(() => AmigaFileSystemRegistry.CreateDefault().Mount(image));
        // "XYZ\9": the 3-letter disk-type ASCII plus the boot-flags byte, the same format real DOS-type
        // errors are reported in (e.g. "DOS\3" for an unsupported FFS variant).
        Assert.Contains("XYZ\\9", ex.Message);
    }
}
