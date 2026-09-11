namespace AdfXplorer.Core;

/// <summary>
/// What to do about a block whose stored checksum doesn't match its computed one.
/// </summary>
public enum ChecksumDecision
{
    /// <summary>Recompute the checksum from the block's current bytes and write it back.</summary>
    Repair,

    /// <summary>Use the block anyway, despite the mismatch.</summary>
    Ignore,

    /// <summary>Treat the block as absent/untrustworthy.</summary>
    Reject,
}

/// <summary>
/// Called when a checksum mismatch is found, to decide what to do about it.
/// <paramref name="blockDescription"/> is built by the caller (e.g. "RDSK block 5", "OFS root block",
/// "partition 'DH1': OFS root block") - the handler doesn't need to know which structure it's looking at.
/// </summary>
public delegate ChecksumDecision ChecksumConflictHandler(string blockDescription, uint stored, uint computed);
