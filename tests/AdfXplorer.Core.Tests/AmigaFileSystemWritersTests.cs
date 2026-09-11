using AdfXplorer.Core.FileSystems;

namespace AdfXplorer.Core.Tests;

public class AmigaFileSystemWritersTests
{
    [Fact]
    public void All_ContainsExactlyTheCompiledInWriters()
    {
        Assert.Equal(2, AmigaFileSystemWriters.All.Count);
        Assert.Contains(AmigaFileSystemWriters.All, w => w.DisplayName == "OFS (Original File System)");
        Assert.Contains(AmigaFileSystemWriters.All, w => w.DisplayName == "FFS (Fast File System)");
    }
}
