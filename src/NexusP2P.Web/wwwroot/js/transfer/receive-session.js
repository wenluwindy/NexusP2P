// 接收端状态机。对应 C# 的 ReceiveSession。
//
// 流程：**收密钥要约** → 收清单 → **校验路径安全** → 回发位图
//      → 逐分片解密校验后落盘 → 全齐后落地并通知对端。
//
// 接收方不再需要预先知道密钥（V3）。密钥由发送方在通道建立后作为首条消息
// 推来，所以用户只要输入九位文件码 —— 「密钥怎么念给对方」这个实际上无解的
// 问题就从产品里去掉了。代价是信令服务器若主动做中间人就能拿到密钥。
//
// 清单是不可信输入。任一路径非法就整体拒绝并报错，不做部分接受 ——
// 部分接受既让用户困惑，也给攻击者留了「混一条恶意路径进去」的空间。
// （路径校验在 TransferManifest.deserialize 里就做了。）
//
// 与 exe 的区别：浏览器没有持久的 .part 仓储，所以位图每次从空开始 ——
// **网页端不支持跨会话续传**，这与 AD-6 的能力诚实呈现一致。

import { MessageType } from '../core/frame.js';
import { bytesEqual } from '../core/bytes.js';
import { deriveManifestKey, openBlob, PieceCipher } from '../core/crypto.js';
import { hashPiece } from '../core/hashing.js';
import { PieceLocator } from '../core/locator.js';
import { TransferManifest } from '../core/manifest.js';
import {
    PieceBitfield, parseError, parseKeyOffer, parsePiece, TransferErrorCode,
} from '../core/messages.js';
import { TransferFailedError } from './send-session.js';

/** 允许连续拒收多少个分片后放弃。防止「对端一直发垃圾」变成无限循环。 */
const MAX_CONSECUTIVE_REJECTIONS = 16;

/** 最多来回几轮。必须与发送端一致或更宽松。 */
const MAX_ROUNDS = 8;

export class ReceiveSession {
    /**
     * @param openWriter 收到清单后调用，(manifest) => writer。
     *   延迟到这一刻才建 writer，因为要先知道文件数与总大小才能选策略、
     *   也才能把「当前策略能收多大」如实告诉用户。
     */
    constructor(openWriter) {
        this._openWriter = openWriter;
    }

    /** 与发送端同理：失败必须关通道，否则对端永远等下去。 */
    async run(connection, { onProgress, onManifest, signal } = {}) {
        try {
            return await this._runCore(connection, onProgress, onManifest, signal);
        } catch (error) {
            connection.channel.close(`${error.name}: ${error.message}`);
            throw error;
        }
    }

    async _runCore(connection, onProgress, onManifest, signal) {
        this._secret = await this._receiveKeyOffer(connection, signal);

        const manifest = await this._receiveManifest(connection, signal);
        onManifest?.(manifest);

        // 叶子尺寸来自清单（可协商的参数），校验分片时要用
        this._leafSize = manifest.parameters.leafSize;

        const locator = new PieceLocator(manifest);
        const bitfield = new PieceBitfield(locator.totalPieces);

        let writer;
        try {
            writer = await this._openWriter(manifest);
        } catch (error) {
            // 用户取消选目录、或没有可用策略。要告诉对端，否则它一直等。
            const reason = error.name === 'AbortError'
                ? '接收方取消了保存位置的选择。'
                : `接收方无法准备落盘位置：${error.message}`;

            await connection.sendErrorAndClose(TransferErrorCode.DestinationNotWritable, reason);
            throw new TransferFailedError(TransferErrorCode.DestinationNotWritable, reason);
        }

        try {
            for (let round = 1; round <= MAX_ROUNDS; round++) {
                throwIfAborted(signal);

                await connection.send(MessageType.Bitfield, bitfield.serialize(), false);

                if (bitfield.isComplete) {
                    break;
                }

                const before = bitfield.setCount;

                await this._receivePieces(
                    connection, writer, manifest, locator, bitfield, onProgress, signal);

                if (bitfield.setCount === before) {
                    // 一整轮下来一个分片都没补上。再来一轮也是一样的结果。
                    const reason = `第 ${round} 轮没有收到任何通过校验的分片，放弃。`;
                    await connection.sendErrorAndClose(
                        TransferErrorCode.PieceVerificationFailed, reason);
                    throw new TransferFailedError(TransferErrorCode.PieceVerificationFailed, reason);
                }

                if (round === MAX_ROUNDS && !bitfield.isComplete) {
                    const reason = `来回 ${MAX_ROUNDS} 轮仍未收齐，放弃。`;
                    await connection.sendErrorAndClose(
                        TransferErrorCode.PieceVerificationFailed, reason);
                    throw new TransferFailedError(TransferErrorCode.PieceVerificationFailed, reason);
                }
            }

            const result = await writer.finalize();

            await connection.send(MessageType.Complete, new Uint8Array(0), false);

            // 必须等排空：立刻关闭会让 Complete 还在缓冲里就被丢掉，
            // 发送端于是永远等不到完成通知。
            await connection.drain();

            return { manifest, ...result };
        } catch (error) {
            await writer.abort();
            throw error;
        }
    }

    /**
     * 收下密钥要约（V3 的第一步）。
     *
     * 它**必须是第一条消息**。顺序在协议里是硬性的而不是约定俗成：
     * 密钥晚于清单到达的话，清单在到手的那一刻就解不开，
     * 而「先缓存住等密钥来」会给攻击者一个免费的内存占用面。
     */
    async _receiveKeyOffer(connection, signal) {
        const message = await connection.receive(signal);

        if (message.type === MessageType.Error) {
            const error = parseError(message.payload);
            throw new TransferFailedError(error.code, `对端报错：${error.message}`);
        }

        if (message.type !== MessageType.KeyOffer) {
            // 旧版发送方（V1/V2）会直接发 Manifest。给一句能指向真正原因的话 ——
            // 「期望 KeyOffer 实际 Manifest」只有开发者看得懂。
            const reason = message.type === MessageType.Manifest
                ? '对方用的是旧版本，它仍然需要你手动输入密钥。请让对方升级到新版本。'
                : `期望首条消息是 KeyOffer，实际收到 ${message.type}。`;

            await connection.sendErrorAndClose(TransferErrorCode.ProtocolViolation, reason);
            throw new TransferFailedError(TransferErrorCode.ProtocolViolation, reason);
        }

        try {
            return parseKeyOffer(message.payload);
        } catch (error) {
            await connection.sendErrorAndClose(TransferErrorCode.ProtocolViolation, error.message);
            throw new TransferFailedError(TransferErrorCode.ProtocolViolation, error.message);
        }
    }

    async _receiveManifest(connection, signal) {
        const message = await connection.receive(signal);

        if (message.type === MessageType.Error) {
            const error = parseError(message.payload);
            throw new TransferFailedError(error.code, `对端报错：${error.message}`);
        }

        if (message.type !== MessageType.Manifest) {
            const reason = `期望首条消息是 Manifest，实际收到 ${message.type}。`;
            await connection.sendErrorAndClose(TransferErrorCode.ProtocolViolation, reason);
            throw new TransferFailedError(TransferErrorCode.ProtocolViolation, reason);
        }

        const manifestKey = await deriveManifestKey(this._secret);

        let plaintext;
        try {
            plaintext = await openBlob(manifestKey, message.payload);
        } catch {
            // V3 里密钥是对端刚推过来的，所以这已经不可能是「用户填错密钥」了。
            // 能走到这里说明对端的密钥与它自己密封清单用的不是同一把 ——
            // 要么实现有 bug，要么中间有人改过字节。
            const reason = '清单解密失败。对方的实现可能有问题，或数据在途中被篡改。';
            await connection.sendErrorAndClose(TransferErrorCode.InvalidManifest, reason);
            throw new TransferFailedError(TransferErrorCode.InvalidManifest, reason);
        }

        try {
            // deserialize 内部会校验每条路径的安全性，任一条非法就整体拒绝
            return await TransferManifest.deserialize(plaintext);
        } catch (error) {
            const reason = `清单不合法：${error.message}`;
            await connection.sendErrorAndClose(TransferErrorCode.InvalidManifest, reason);
            throw new TransferFailedError(TransferErrorCode.InvalidManifest, reason);
        }
    }

    async _receivePieces(connection, writer, manifest, locator, bitfield, onProgress, signal) {
        const cipher = await PieceCipher.create(this._secret, manifest.hash);
        let consecutiveRejections = 0;
        let completedBytes = countCompletedBytes(locator, bitfield);

        while (true) {
            throwIfAborted(signal);

            const message = await connection.receive(signal);

            switch (message.type) {
                case MessageType.Piece:
                    break;

                case MessageType.PushComplete:
                    return;   // 本轮结束。缺的部分交给外层循环重发位图去要。

                case MessageType.Error: {
                    const error = parseError(message.payload);
                    throw new TransferFailedError(error.code, `对端报错：${error.message}`);
                }

                default: {
                    const reason = `接收分片期间收到意外的 ${message.type} 消息。`;
                    await connection.sendErrorAndClose(TransferErrorCode.ProtocolViolation, reason);
                    throw new TransferFailedError(TransferErrorCode.ProtocolViolation, reason);
                }
            }

            const accepted = await this._tryAcceptPiece(
                connection, writer, cipher, locator, bitfield, message.payload);

            if (accepted.ok) {
                consecutiveRejections = 0;
                completedBytes += accepted.bytesAdded;

                onProgress?.({
                    completedBytes,
                    totalBytes: manifest.totalLength,
                    completedPieces: bitfield.setCount,
                    totalPieces: bitfield.count,
                });

                // 收齐了就不必等 PushComplete —— 对端也知道该发 Complete 了。
                // 这让常见路径少一次往返。
                if (bitfield.isComplete) {
                    return;
                }
            } else if (++consecutiveRejections >= MAX_CONSECUTIVE_REJECTIONS) {
                const reason = `连续 ${consecutiveRejections} 个分片校验失败，放弃。`;
                await connection.sendErrorAndClose(TransferErrorCode.PieceVerificationFailed, reason);
                throw new TransferFailedError(TransferErrorCode.PieceVerificationFailed, reason);
            }
        }
    }

    /**
     * 解密、校验、落盘一个分片。返回是否被接受。
     *
     * 解密失败或校验失败**不是致命错误** —— 单个分片坏掉是可以重传的。
     * 但要计数，避免对端一直发垃圾把我们卡在循环里。
     */
    async _tryAcceptPiece(connection, writer, cipher, locator, bitfield, payload) {
        let piece;
        try {
            piece = parsePiece(payload);
        } catch (error) {
            await connection.sendErrorAndClose(TransferErrorCode.ProtocolViolation, error.message);
            throw new TransferFailedError(TransferErrorCode.ProtocolViolation, error.message);
        }

        let globalIndex;
        try {
            globalIndex = locator.globalIndex(piece.fileIndex, piece.pieceIndex);
        } catch {
            // 位置越界是协议违规而不是数据损坏 —— 正常的对端不会算错位置
            const reason = `分片位置越界：文件 ${piece.fileIndex}，分片 ${piece.pieceIndex}。`;
            await connection.sendErrorAndClose(TransferErrorCode.ProtocolViolation, reason);
            throw new TransferFailedError(TransferErrorCode.ProtocolViolation, reason);
        }

        // 已经有的分片直接忽略。重连后对端可能重发几个，这不是错误。
        if (bitfield.has(globalIndex)) {
            return { ok: true, bytesAdded: 0 };
        }

        const location = locator.locate(globalIndex);

        // 密文长度 = 明文 + 16 字节标签。不符说明这个分片本身就不对。
        if (piece.ciphertext.length !== location.length + 16) {
            return { ok: false, bytesAdded: 0 };
        }

        let plaintext;
        try {
            plaintext = await cipher.decrypt(piece.fileIndex, piece.pieceIndex, piece.ciphertext);
        } catch {
            return { ok: false, bytesAdded: 0 };   // 认证失败，等重传
        }

        // 分片 Merkle 校验在浏览器侧同样执行 —— 加密的认证标签证明「没被改过」，
        // 而 Merkle 根证明「这确实是清单里承诺的那份内容」。两者不能互相替代。
        const actualRoot = await hashPiece(plaintext, this._leafSize);

        if (!bytesEqual(actualRoot, location.expectedRoot)) {
            return { ok: false, bytesAdded: 0 };
        }

        await writer.writePiece(piece.fileIndex, location.offsetInFile, plaintext);
        bitfield.set(globalIndex);

        return { ok: true, bytesAdded: location.length };
    }
}

function countCompletedBytes(locator, bitfield) {
    let total = 0;
    for (let i = 0; i < bitfield.count; i++) {
        if (bitfield.has(i)) {
            total += locator.locate(i).length;
        }
    }

    return total;
}

function throwIfAborted(signal) {
    if (signal?.aborted === true) {
        throw new TransferFailedError(TransferErrorCode.Cancelled, '已取消。');
    }
}
