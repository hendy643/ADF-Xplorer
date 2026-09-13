using AdfXplorer.Core.DiskImage;

namespace AdfXplorer.Core.FileSystems.Ofs;

/// <summary>
/// A readable and seekable stream over an OFS file's data blocks.
/// Keeps only the data-block sector indices in memory; file content is streamed
/// on demand from the underlying <see cref="AdfImage"/>.
/// </summary>
internal sealed class OfsFileDataStream : Stream
{
    private readonly AdfImage _image;
    private readonly long _size;
    private readonly List<long> _dataBlocks = [];
    private long _position;

    public OfsFileDataStream(
        AdfImage image,
        int headerBlock,
        long size,
        Func<int, string, bool> isBlockAccepted)
    {
        _image = image;
        _size = size;

        var header = _image.ReadBlock(headerBlock);
        int nextData = BlockReader.ReadInt32(header, OfsBlockOffsets.FileHeader_FirstData);
        long indexedBytes = 0;

        while (nextData != 0 && indexedBytes < size)
        {
            if (!isBlockAccepted(nextData, "file data block"))
            {
                break; // Reject policy: truncate at already indexed blocks.
            }

            _dataBlocks.Add(nextData);
            var data = _image.ReadBlock(nextData);
            int dataSize = (int)BlockReader.ReadUInt32(data, OfsBlockOffsets.Data_DataSize);
            indexedBytes += Math.Min(dataSize, (int)(size - indexedBytes));
            nextData = BlockReader.ReadInt32(data, OfsBlockOffsets.Data_NextData);
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _size;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        if (_position >= _size || destination.IsEmpty)
        {
            return 0;
        }

        int bytesRead = 0;
        while (_position < _size && bytesRead < destination.Length)
        {
            int logicalBlockIndex = (int)(_position / OfsBlockOffsets.Data_PayloadMaxSize);
            int blockOffset = (int)(_position % OfsBlockOffsets.Data_PayloadMaxSize);
            if (logicalBlockIndex >= _dataBlocks.Count)
            {
                break;
            }

            long blockNum = _dataBlocks[logicalBlockIndex];
            int availableInBlock = (int)Math.Min((long)OfsBlockOffsets.Data_PayloadMaxSize - blockOffset, _size - _position);
            int toCopy = Math.Min(availableInBlock, destination.Length - bytesRead);

            if (blockNum == 0)
            {
                destination.Slice(bytesRead, toCopy).Clear();
                _position += toCopy;
                bytesRead += toCopy;
            }
            else
            {
                var blockData = _image.ReadBlock(blockNum);
                int dataSize = (int)BlockReader.ReadUInt32(blockData, OfsBlockOffsets.Data_DataSize);
                int payloadAvailable = Math.Max(0, dataSize - blockOffset);
                int actualToCopy = Math.Min(toCopy, payloadAvailable);
                if (actualToCopy > 0)
                {
                    blockData.AsSpan(OfsBlockOffsets.Data_Payload + blockOffset, actualToCopy)
                        .CopyTo(destination.Slice(bytesRead, actualToCopy));
                    _position += actualToCopy;
                    bytesRead += actualToCopy;
                }

                if (actualToCopy < toCopy)
                {
                    // Block had less valid data than expected; cannot read further from this block
                    break;
                }
            }
        }

        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _size + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0)
        {
            throw new IOException("Cannot seek before beginning of stream.");
        }

        _position = target;
        return _position;
    }

    public override void SetLength(long value) =>
        throw new NotSupportedException("OfsFileDataStream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("OfsFileDataStream is read-only.");
}
