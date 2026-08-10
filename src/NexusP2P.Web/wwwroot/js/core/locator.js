// 全局分片下标与「文件内分片」之间的换算。对应 C# 的 PieceLocator。
//
// 为什么需要两套下标：协议层的 Bitfield 是对整次传输的一张位图，需要连续的
// **全局**下标空间；而落盘时每个文件独立写入，用的是**文件内**下标。
// 这个类是两者唯一的换算入口 —— 换算逻辑散落多处是最容易出错的地方，
// 偏移算错就是静默的数据损坏。

export class PieceLocator {
    constructor(manifest) {
        this._manifest = manifest;
        this._fileStartIndex = new Array(manifest.entries.length + 1);

        let running = 0;
        for (let i = 0; i < manifest.entries.length; i++) {
            this._fileStartIndex[i] = running;
            running += manifest.entries[i].pieceCount;
        }

        this._fileStartIndex[manifest.entries.length] = running;
        this.totalPieces = running;
    }

    get fileCount() {
        return this._manifest.entries.length;
    }

    /** 由全局下标解出完整坐标。 */
    locate(globalIndex) {
        if (globalIndex < 0 || globalIndex >= this.totalPieces) {
            throw new RangeError(`全局分片下标应在 0~${this.totalPieces - 1} 之间，实际 ${globalIndex}。`);
        }

        const fileIndex = this._findFileIndex(globalIndex);
        const entry = this._manifest.entries[fileIndex];
        const localIndex = globalIndex - this._fileStartIndex[fileIndex];
        const parameters = this._manifest.parameters;

        return {
            globalIndex,
            fileIndex,
            localPieceIndex: localIndex,
            offsetInFile: parameters.pieceOffset(localIndex),
            length: parameters.pieceLength(entry.length, localIndex),
            expectedRoot: entry.pieceRoots[localIndex],
        };
    }

    /** 由文件内坐标算出全局下标。 */
    globalIndex(fileIndex, localPieceIndex) {
        if (fileIndex < 0 || fileIndex >= this.fileCount) {
            throw new RangeError(`文件序号应在 0~${this.fileCount - 1} 之间，实际 ${fileIndex}。`);
        }

        const pieceCount = this._manifest.entries[fileIndex].pieceCount;
        if (localPieceIndex < 0 || localPieceIndex >= pieceCount) {
            throw new RangeError(`文件 ${fileIndex} 只有 ${pieceCount} 个分片，实际给了 ${localPieceIndex}。`);
        }

        return this._fileStartIndex[fileIndex] + localPieceIndex;
    }

    /** 某个文件的全局下标区间 [起, 止)。 */
    fileRange(fileIndex) {
        if (fileIndex < 0 || fileIndex >= this.fileCount) {
            throw new RangeError(`文件序号应在 0~${this.fileCount - 1} 之间，实际 ${fileIndex}。`);
        }

        return [this._fileStartIndex[fileIndex], this._fileStartIndex[fileIndex + 1]];
    }

    entry(fileIndex) {
        return this._manifest.entries[fileIndex];
    }

    /** 二分：文件数可达 10 万，线性扫会让每个分片的处理都带上一次遍历。 */
    _findFileIndex(globalIndex) {
        let low = 0;
        let high = this.fileCount - 1;

        while (low < high) {
            const mid = low + Math.floor((high - low + 1) / 2);
            if (this._fileStartIndex[mid] <= globalIndex) {
                low = mid;
            } else {
                high = mid - 1;
            }
        }

        return low;
    }
}
