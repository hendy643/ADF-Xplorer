using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace AdfXplorer.WinFspInstaller;

/// <summary>
/// Chained as an ExePackage in Bundle.wxs, exactly like the .NET Desktop Runtime - the only reason
/// this tiny wrapper exists at all is that WiX's MsiPackage element (unlike ExePackage) always needs
/// to read the real .msi bytes at *build* time to harvest its ProductCode, so a raw third-party MSI
/// can't be declared as a purely remote payload the way an .exe can. This tool sidesteps that: it's
/// our own first-party code (nothing to harvest, no local MSI ever needed), and it does the actual
/// WinFsp download and install itself, entirely at real install time on the end user's machine.
/// </summary>
public static class Program
{
    /// <summary>The exact pinned GitHub release "v2.1" asset - an immutable download once published.</summary>
    private const string DownloadUrl = "https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi";

    /// <summary>Published SHA-256 for winfsp-2.1.25156.msi (see the WinFsp v2.1 release notes).</summary>
    private const string ExpectedSha256 = "073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A";

    public static async Task<int> Main()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "winfsp-2.1.25156.msi");

        try
        {
            using var http = new HttpClient();
            byte[] bytes = await http.GetByteArrayAsync(DownloadUrl);

            string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                await Console.Error.WriteLineAsync(
                    $"WinFsp download hash mismatch: expected {ExpectedSha256}, got {actualHash}.");
                return 1;
            }

            await File.WriteAllBytesAsync(tempPath, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            await Console.Error.WriteLineAsync($"Couldn't download WinFsp: {ex.Message}");
            return 1;
        }

        // No silent switches: this lets WinFsp's own installer show its wizard (license, Cancel
        // button and all), so the user genuinely chooses whether to install it - AdfXplorer just
        // can't mount anything until they do (or already have, which Bundle.wxs's DetectCondition
        // already skips this whole package for).
        using var msiexec = Process.Start("msiexec.exe", $"/i \"{tempPath}\"");
        await msiexec.WaitForExitAsync();
        return msiexec.ExitCode;
    }
}
