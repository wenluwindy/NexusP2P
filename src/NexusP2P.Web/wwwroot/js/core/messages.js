// 消息载荷的编解码。对应 C# 的 Messages.cs 与 PieceBitfield。

/** 位置头的字节数：文件序号(4) + 分片序号(8)。 */
export const PIECE_HEADER_SIZE = 12;

export const TransferErrorCode = {
    Unknown: 0,
    InvalidManifest: 1,
    PieceVerificationFailed: 2,
    InsufficientDiskSpace: 3,
    DestinationNotWritable: 4,
    ProtocolViolation: 5,
    Cancelled: 6,
};

const MAX_ERROR_MESSAGE_BYTES = 4096;

/**
 * 序列化一个分片：位置头 ‖ 密文。
 *
 * 位置必须随密文一起传：加密的 nonce 就是由 (文件序号, 分片序号) 派生的，
 * 接收方少了位置就解不开 —— 这同时也让「把密文挪到别的位置」这种攻击失效。
 */
export function serializePiece(fileIndex, pieceIndex, ciphertext) {
    const result = new Uint8Array(PIECE_HEADER_SIZE + ciphertext.length);
    const view = new DataView(result.buffer);
    view.setInt32(0, fileIndex, false);
    view.setBigInt64(4, BigInt(pieceIndex), false);
    result.set(ciphertext, PIECE_HEADER_SIZE);
    return result;
}

/** 解析分片载荷。不可信输入，越界与负数都要拒。 */
export function parsePiece(payload) {
    if (payload.length < PIECE_HEADER_SIZE) {
        throw new Error(`Piece 消息只有 ${payload.length} 字节，不足位置头 ${PIECE_HEADER_SIZE} 字节。`);
    }

    const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
    const fileIndex = view.getInt32(0, false);
    const pieceIndex = Number(view.getBigInt64(4, false));

    if (fileIndex < 0) {
        throw new Error(`Piece 消息里的文件序号为负数：${fileIndex}。`);
    }

    if (pieceIndex < 0) {
        throw new Error(`Piece 消息里的分片序号为负数：${pieceIndex}。`);
    }

    return { fileIndex, pieceIndex, ciphertext: payload.subarray(PIECE_HEADER_SIZE) };
}

/** 序列化错误通知：错误码(be16) ‖ UTF-8 文本。 */
export function serializeError(code, message) {
    let text = new TextEncoder().encode(message);
    if (text.length > MAX_ERROR_MESSAGE_BYTES) {
        text = text.subarray(0, MAX_ERROR_MESSAGE_BYTES);
    }

    const result = new Uint8Array(2 + text.length);
    new DataView(result.buffer).setUint16(0, code, false);
    result.set(text, 2);
    return result;
}

export function parseError(payload) {
    if (payload.length < 2) {
        throw new Error(`Error 消息只有 ${payload.length} 字节，不足错误码。`);
    }

    const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
    const rawCode = view.getUint16(0, false);
    const textBytes = payload.subarray(2);

    if (textBytes.length > MAX_ERROR_MESSAGE_BYTES) {
        throw new Error(`Error 消息文本 ${textBytes.length} 字节超过上限 ${MAX_ERROR_MESSAGE_BYTES}。`);
    }

    // 未知错误码不算协议违规 —— 对端可能是更新的版本
    const known = Object.values(TransferErrorCode).includes(rawCode);

    return {
        code: known ? rawCode : TransferErrorCode.Unknown,
        message: new TextDecoder().decode(textBytes),
    };
}

/**
 * 「哪些分片已经有了」的位图。断点续传的核心数据结构。
 *
 * 线上格式：分片数(be32) + 位图字节。位序与 C# 一致：
 * 第 i 位在字节 i>>3 的第 (i&7) 位（最低位优先）。
 */
export class PieceBitfield {
    constructor(count) {
        if (count <= 0) {
            throw new Error(`分片数必须为正，实际 ${count}。`);
        }

        this.count = count;
        this.setCount = 0;
        this._bits = new Uint8Array(Math.ceil(count / 8));
    }

    get isComplete() {
        return this.setCount === this.count;
    }

    has(index) {
        this._validate(index);
        return (this._bits[index >> 3] & (1 << (index & 7))) !== 0;
    }

    set(index) {
        this._validate(index);
        const mask = 1 << (index & 7);
        if ((this._bits[index >> 3] & mask) === 0) {
            this._bits[index >> 3] |= mask;
            this.setCount++;
        }
    }

    clear(index) {
        this._validate(index);
        const mask = 1 << (index & 7);
        if ((this._bits[index >> 3] & mask) !== 0) {
            this._bits[index >> 3] &= ~mask & 0xff;
            this.setCount--;
        }
    }

    /** 还缺哪些分片，按下标升序。 */
    *missingIndices() {
        for (let i = 0; i < this.count; i++) {
            if (!this.has(i)) {
                yield i;
            }
        }
    }

    serialize() {
        const result = new Uint8Array(4 + this._bits.length);
        new DataView(result.buffer).setInt32(0, this.count, false);
        result.set(this._bits, 4);
        return result;
    }

    /** data 是不可信输入，所有字段都校验。 */
    static deserialize(data, expectedCount) {
        if (data.length < 4) {
            throw new Error(`位图数据只有 ${data.length} 字节，不足头部。`);
        }

        const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
        const count = view.getInt32(0, false);
        if (count !== expectedCount) {
            throw new Error(`位图声明 ${count} 个分片，但清单里是 ${expectedCount} 个。`);
        }

        const expectedBytes = Math.ceil(count / 8);
        if (data.length !== 4 + expectedBytes) {
            throw new Error(`位图应为 ${4 + expectedBytes} 字节，实际 ${data.length} 字节。`);
        }

        const bits = data.subarray(4);

        // 最后一个字节里超出 count 的高位必须是 0。放过它会让同一个位图
        // 有多种字节表示。
        const remainder = count & 7;
        if (remainder !== 0) {
            const validMask = (1 << remainder) - 1;
            if ((bits[bits.length - 1] & ~validMask & 0xff) !== 0) {
                throw new Error('位图最后一个字节里有超出分片数的位被置起。');
            }
        }

        const result = new PieceBitfield(count);
        result._bits.set(bits);
        for (let i = 0; i < count; i++) {
            if (result.has(i)) {
                result.setCount++;
            }
        }

        return result;
    }

    _validate(index) {
        if (!Number.isInteger(index) || index < 0 || index >= this.count) {
            throw new RangeError(`分片下标应在 0~${this.count - 1} 之间，实际 ${index}。`);
        }
    }
}
