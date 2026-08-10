// 发送端状态机。对应 C# 的 SendSession。
//
// 流程：发清单 → 等对端位图 → **只发对端缺的分片** → 宣告本轮结束 → 再收位图。
// 「只发缺的」就是断点续传的发送侧 —— 不需要额外机制，位图本身就表达了
// 「从哪继续」。
//
// PushComplete 那条消息不能省：接收方拒收某个分片是静默的，发送方不知道
// 要重发，于是接收方等分片、发送方等完成通知，两边各等各的。

import { MessageType } from '../core/frame.js';
import { deriveManifestKey, PieceCipher, sealBlob } from '../core/crypto.js';
import { PieceLocator } from '../core/locator.js';
import { PieceBitfield, parseError, serializePiece, TransferErrorCode } from '../core/messages.js';

/** 最多来回几轮。每轮要么有进展要么直接报错，所以这只是防御性上限。 */
const MAX_ROUNDS = 8;

export class TransferFailedError extends Error {
    constructor(code, message) {
        super(message);
        this.name = 'TransferFailedError';
        this.code = code;
    }
}

export class SendSession {
    /**
     * @param manifest 已算好的清单
     * @param files 与 manifest.entries 同序的 File 列表（分片从这里读）
     * @param secret 本次传输的根密钥材料
     */
    constructor(manifest, files, secret) {
        this._manifest = manifest;
        this._secret = secret;
        this._locator = new PieceLocator(manifest);

        // 清单排过序，files 是调用方按原顺序给的 —— 必须按路径重建映射，
        // 否则会把 A 文件的字节当成 B 文件发出去（静默的数据错乱）
        this._filesByPath = new Map();
        for (const file of files) {
            const relative = file.webkitRelativePath !== undefined && file.webkitRelativePath.length > 0
                ? file.webkitRelativePath
                : file.name;
            this._filesByPath.set(relative.replace(/\\/g, '/').replace(/^\/+|\/+$/g, ''), file);
        }

        this.piecesSent = 0;
    }

    /**
     * 跑完一次发送。正常结束表示对端已确认全部收齐并校验通过。
     *
     * 失败时必须关掉通道，否则对端会一直等下去 —— 症状是「传输卡死且
     * 两边都不报错」，是最难查的一类故障。
     */
    async run(connection, { onProgress, signal } = {}) {
        try {
            await this._runCore(connection, onProgress, signal);
        } catch (error) {
            connection.channel.close(`${error.name}: ${error.message}`);
            throw error;
        }
    }

    async _runCore(connection, onProgress, signal) {
        // 1. 清单。用 manifestKey 密封 —— 文件名本身就是隐私。
        const manifestKey = await deriveManifestKey(this._secret);
        const sealed = await sealBlob(manifestKey, this._manifest.serialize());
        await connection.send(MessageType.Manifest, sealed, false);

        const cipher = await PieceCipher.create(this._secret, this._manifest.hash);

        for (let round = 1; round <= MAX_ROUNDS; round++) {
            throwIfAborted(signal);

            const message = await connection.receive(signal);

            switch (message.type) {
                case MessageType.Complete:
                    return;   // 对端已收齐并通过整体根校验

                case MessageType.Error: {
                    const error = parseError(message.payload);
                    throw new TransferFailedError(error.code, `对端报错：${error.message}`);
                }

                case MessageType.Bitfield:
                    break;

                default:
                    throw new TransferFailedError(
                        TransferErrorCode.ProtocolViolation,
                        `期望收到 Bitfield 或 Complete，实际收到 ${message.type}。`);
            }

            const remote = this._parseBitfield(message.payload);

            // 对端已经齐了，等它做完整体校验后发 Complete
            if (remote.isComplete) {
                continue;
            }

            await this._pushMissing(connection, cipher, remote, onProgress, signal);
            await connection.send(MessageType.PushComplete, new Uint8Array(0), false);
        }

        throw new TransferFailedError(
            TransferErrorCode.PieceVerificationFailed,
            `来回 ${MAX_ROUNDS} 轮仍未收齐，放弃。对端可能一直无法通过校验。`);
    }

    async _pushMissing(connection, cipher, remoteBitfield, onProgress, signal) {
        const alreadyDone = remoteBitfield.setCount;
        let sentThisRound = 0;
        let sentBytes = 0;

        for (const globalIndex of remoteBitfield.missingIndices()) {
            throwIfAborted(signal);

            if (!connection.channel.isOpen) {
                throw new TransferFailedError(TransferErrorCode.Unknown, '发送过程中通道关闭。');
            }

            const location = this._locator.locate(globalIndex);
            const plaintext = await this._readPiece(location);

            const ciphertext = await cipher.encrypt(
                location.fileIndex, location.localPieceIndex, plaintext);

            await connection.send(
                MessageType.Piece,
                serializePiece(location.fileIndex, location.localPieceIndex, ciphertext));

            this.piecesSent++;
            sentThisRound++;
            sentBytes += location.length;

            onProgress?.({
                completedBytes: sentBytes,
                totalBytes: this._manifest.totalLength,
                completedPieces: alreadyDone + sentThisRound,
                totalPieces: this._locator.totalPieces,
                bufferedAmount: connection.channel.bufferedAmount,
            });
        }
    }

    /** 从 File 里切出一个分片。Blob.slice 是惰性的，不会把整个文件读进内存。 */
    async _readPiece(location) {
        const entry = this._locator.entry(location.fileIndex);
        const file = this._filesByPath.get(entry.path);

        if (file === undefined) {
            throw new TransferFailedError(
                TransferErrorCode.Unknown,
                `找不到清单里的文件 "${entry.path}"。选中的文件可能已被移走。`);
        }

        const start = location.offsetInFile;
        const buffer = await file.slice(start, start + location.length).arrayBuffer();

        if (buffer.byteLength !== location.length) {
            throw new TransferFailedError(
                TransferErrorCode.Unknown,
                `读取 "${entry.path}" 时第 ${location.globalIndex} 个分片只读到 ` +
                `${buffer.byteLength} 字节，期望 ${location.length} 字节。文件可能在传输期间被改动了。`);
        }

        return new Uint8Array(buffer);
    }

    _parseBitfield(payload) {
        try {
            return PieceBitfield.deserialize(payload, this._locator.totalPieces);
        } catch (error) {
            throw new TransferFailedError(
                TransferErrorCode.ProtocolViolation, `对端的位图不合法：${error.message}`);
        }
    }
}

function throwIfAborted(signal) {
    if (signal?.aborted === true) {
        throw new TransferFailedError(TransferErrorCode.Cancelled, '已取消。');
    }
}
