using AdfXplorer.Core.DiskImage;
using AdfXplorer.Core.FileSystems.Ofs;

namespace AdfXplorer.Core.FileSystems.Ffs;

/// <summary>
/// A readable and seekable stream over an FFS file's data blocks.
/// Keeps only the data-block sector indices in memory; file content is streamed
/// on demand from the underlying <see cref="AdfImage"/>.
/// </summary>
internal sealed class FfsFileDataStream : Stream
{
    private const int SlotsPerTable = 72;

    private readonly AdfImage _image;
    private readonly long _size;
    private readonly List<long> _dataBlocks = [];
    private long _position;

    public FfsFileDataStream(
        AdfImage image,
        int headerBlock,
        long size,
        Func<int, string, bool> isBlockAccepted)
    {
        _image = image;
        _size = size;

        int currentBlock = headerBlock;
        bool first = true;
        int maxBlocksNeeded = size == 0 ? 0 : (int)((size + AdfImage.SectorSize - 1) / AdfImage.SectorSize);

        while (currentBlock != 0 && _dataBlocks.Count < maxBlocksNeeded)
        {
            if (!first && !isBlockAccepted(currentBlock, "file extension block"))
            {
                break; // Reject policy: truncate at already indexed blocks.
            }

            var block = _image.ReadBlock(currentBlock);
            int highSeq = BlockReader.ReadInt32(block, OfsBlockOffsets.FileHeader_HighSeq);

            for (int i = 0; i < highSeq && _dataBlocks.Count < maxBlocksNeeded; i++)
            {
                int slot = SlotsPerTable - 1 - i;
                int dataBlockNum = BlockReader.ReadInt32(block, OfsBlockOffsets.HashTable + slot * 4);
                _dataBlocks.Add(dataBlockNum);
            }

            currentBlock = BlockReader.ReadInt32(block, OfsBlockOffsets.Extension);
            first = false;
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
            int logicalBlockIndex = (int)(_position / AdfImage.SectorSize);
            int blockOffset = (int)(_position % AdfImage.SectorSize);
            if (logicalBlockIndex >= _dataBlocks.Count)
            {
                break;
            }

            long blockNum = _dataBlocks[logicalBlockIndex];
            int availableInBlock = (int)Math.Min((long)AdfImage.SectorSize - blockOffset, _size - _position);
            int toCopy = Math.Min(availableInBlock, destination.Length - bytesRead);

            if (blockNum == 0)
            {
                destination.Slice(bytesRead, toCopy).Clear();
            }
            else
            {
                var blockData = _image.ReadBlock(blockNum);
                blockData.AsSpan(blockOffset, toCopy).CopyTo(destination.Slice(bytesRead, toCopy));
            }

            _position += toCopy;
            bytesRead += toCopy;
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
        throw new NotSupportedException("FfsFileDataStream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("FfsFileDataStream is read-only.");
}
