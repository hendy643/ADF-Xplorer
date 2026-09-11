namespace AdfXplorer.Core.FileSystems;

/// <summary>
/// One block's checksum status, as reported by <see cref="IChecksumAware.ScanChecksums"/>.
/// <paramref name="BlockNumber"/> and <paramref name="ChecksumFieldOffset"/> are exactly the arguments
/// a repair needs for <c>AdfImage.PatchChecksumField</c> - carried here so a caller driving
/// <see cref="IChecksumAware.RepairChecksum"/> never needs to re-derive them from
/// <paramref name="BlockDescription"/>.
/// </summary>
public sealed record ChecksumReport(
    string BlockDescription, int BlockNumber, int ChecksumFieldOffset, bool Valid, uint Stored, uint Computed);

/// <summary>
/// Opt-in capability for an <see cref="IAmigaFileSystem"/> that supports checksum-protected on-disk
/// blocks. Not part of <see cref="IAmigaFileSystem"/> itself - a format that doesn't checksum its blocks
/// (or hasn't had this added yet) simply doesn't implement it.
/// </summary>
public interface IChecksumAware
{
    /// <summary>
    /// Re-validates this already-mounted filesystem's foundational blocks (whatever "foundational"
    /// means for this format - e.g. OFS's boot+root blocks) against <paramref name="onChecksumMismatch"/>
    /// (or a default <see cref="ChecksumDecision.Ignore"/> policy if <c>null</c>), applying any
    /// <see cref="ChecksumDecision.Repair"/> decisions to the underlying image, and - for formats where
    /// later per-entry reads can also hit checksum-protected blocks - recording a standing policy for
    /// those (no live prompting is possible once a volume is mounted and being browsed from WinFsp
    /// dispatcher threads).
    /// </summary>
    /// <param name="repaired">Whether any block was repaired.</param>
    /// <returns>
    /// <c>false</c> if the user rejected a block this filesystem cannot function without; the caller
    /// should treat the mount as failed.
    /// </returns>
    bool ValidateChecksums(ChecksumConflictHandler? onChecksumMismatch, out bool repaired);

    /// <summary>
    /// Walks every block reachable from this filesystem's root (recursively - boot, root, every
    /// directory, every file header, every data block in every file), reporting each one's checksum
    /// status. Read-only; never modifies the image on its own - repairs are applied separately via
    /// <see cref="RepairChecksum"/>, driven by the caller's decision for each reported mismatch.
    /// </summary>
    IEnumerable<ChecksumReport> ScanChecksums();

    /// <summary>
    /// Recomputes and patches (in-memory only, via <c>AdfImage.PatchChecksumField</c>) the checksum for
    /// the block described by a specific <see cref="ChecksumReport"/> previously returned from
    /// <see cref="ScanChecksums"/>. The caller is responsible for persisting the change afterward.
    /// </summary>
    void RepairChecksum(ChecksumReport report);
}
