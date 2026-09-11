using AdfXplorer.Core.DiskImage;

namespace AdfXplorer.Core.FileSystems.Ofs;

/// <summary>Creates a blank, correctly-checksummed OFS volume. See <see cref="AmigaBlankVolumeWriter"/>
/// for the shared root/bitmap-block logic (identical between OFS and FFS).</summary>
public static class OfsFileSystemWriter
{
    /// <summary>
    /// Builds a blank OFS volume of <paramref name="sectorCount"/> 512-byte sectors (e.g. 1760 for an
    /// 880 KB DD floppy, 3520 for a 1.76 MB HD floppy) with no files and the given volume label
    /// (truncated to Amiga's 30-character limit).
    /// </summary>
    public static AdfImage CreateBlank(int sectorCount, string volumeLabel) =>
        AmigaBlankVolumeWriter.CreateBlank(sectorCount, volumeLabel, bootFlags: 0);
}
