using NexusP2P.Core.Hashing;
using NexusP2P.Core.Manifest;

namespace NexusP2P.Transfer.Storage;

/// <summary>清单里一个分片的完整坐标。</summary>
public readonly record struct PieceLocation(
    int GlobalIndex,
    int FileIndex,
    long LocalPieceIndex,
    long OffsetInFile,
    int Length,
    Hash256 ExpectedRoot);

/// <summary>
/// 全局分片下标与「文件内分片」之间的换算。
///
/// <para><b>为什么需要两套下标</b>：协议层的 Bitfield 消息是对整次传输的
/// 一张位图，所以需要一个连续的<b>全局</b>下标空间；而落盘时每个文件必须
/// 独立写入、独立完成，用的是<b>文件内</b>下标。这个类是两者唯一的换算入口 ——
/// 换算逻辑散落到多处是这类代码最容易出错的地方，一旦偏移算错就是静默的数据损坏。</para>
///
/// <para>全局下标按清单里文件的顺序拼接。清单是排好序的，所以这个映射是确定的，
/// 两端算出来必然一致。</para>
/// </summary>
public sealed class PieceLocator
{
    private readonly TransferManifest _manifest;

    /// <summary>第 i 个文件的首个分片的全局下标。长度是文件数 + 1，末项等于总分片数。</summary>
    private readonly long[] _fileStartIndex;

    public PieceLocator(TransferManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        _manifest = manifest;
        _fileStartIndex = new long[manifest.Entries.Length + 1];

        long running = 0;
        for (var i = 0; i < manifest.Entries.Length; i++)
        {
            _fileStartIndex[i] = running;
            running += manifest.Entries[i].PieceCount;
        }

        _fileStartIndex[^1] = running;

        if (running > int.MaxValue)
        {
            throw new ArgumentException(
                $"总分片数 {running} 超过 int 范围，位图无法表示。", nameof(manifest));
        }

        TotalPieces = (int)running;
    }

    public int TotalPieces { get; }

    public int FileCount => _manifest.Entries.Length;

    /// <summary>由全局下标解出完整坐标。</summary>
    public PieceLocation Locate(int globalIndex)
    {
        if (globalIndex < 0 || globalIndex >= TotalPieces)
        {
            throw new ArgumentOutOfRangeException(nameof(globalIndex), globalIndex,
                $"全局分片下标应在 0~{TotalPieces - 1} 之间。");
        }

        var fileIndex = FindFileIndex(globalIndex);
        var entry = _manifest.Entries[fileIndex];
        var localIndex = globalIndex - _fileStartIndex[fileIndex];

        return new PieceLocation(
            globalIndex,
            fileIndex,
            localIndex,
            _manifest.Parameters.PieceOffset(localIndex),
            _manifest.Parameters.PieceLength(entry.Length, localIndex),
            entry.PieceRoots[(int)localIndex]);
    }

    /// <summary>由文件内坐标算出全局下标。</summary>
    public int GlobalIndex(int fileIndex, long localPieceIndex)
    {
        if (fileIndex < 0 || fileIndex >= FileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex), fileIndex,
                $"文件序号应在 0~{FileCount - 1} 之间。");
        }

        var pieceCount = _manifest.Entries[fileIndex].PieceCount;
        if (localPieceIndex < 0 || localPieceIndex >= pieceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(localPieceIndex), localPieceIndex,
                $"文件 {fileIndex} 只有 {pieceCount} 个分片。");
        }

        return (int)(_fileStartIndex[fileIndex] + localPieceIndex);
    }

    /// <summary>某个文件的全局下标区间 [起, 止)。</summary>
    public (int Start, int End) FileRange(int fileIndex)
    {
        if (fileIndex < 0 || fileIndex >= FileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex), fileIndex,
                $"文件序号应在 0~{FileCount - 1} 之间。");
        }

        return ((int)_fileStartIndex[fileIndex], (int)_fileStartIndex[fileIndex + 1]);
    }

    public ManifestEntry Entry(int fileIndex) => _manifest.Entries[fileIndex];

    private int FindFileIndex(int globalIndex)
    {
        // 二分：文件数可达 10 万，线性扫会让每个分片的写入都带上一次遍历
        var low = 0;
        var high = FileCount - 1;

        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (_fileStartIndex[mid] <= globalIndex)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }
}
