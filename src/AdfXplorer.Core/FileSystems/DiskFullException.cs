namespace AdfXplorer.Core.FileSystems;

/// <summary>
/// Thrown when an Amiga filesystem operation fails because the volume has insufficient free blocks.
/// </summary>
public class DiskFullException : IOException
{
    public DiskFullException() : base("The disk is full.")
    {
    }

    public DiskFullException(string? message) : base(message)
    {
    }

    public DiskFullException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
