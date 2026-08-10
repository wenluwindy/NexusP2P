// 二进制读写原语。
//
// 全部用大端序 —— 与 C# 侧的 BinaryPrimitives.Write*BigEndian 对齐。
// 这不是风格选择：清单哈希是对规范字节形式算的，端序错一位，
// 两端算出的清单哈希就不同，接收端会直接拒收。

/** 只前进的写入器，按需扩容。 */
export class ByteWriter {
    constructor(capacity = 1024) {
        this._buffer = new Uint8Array(capacity);
        this._length = 0;
    }

    get length() {
        return this._length;
    }

    _ensure(count) {
        if (this._length + count <= this._buffer.length) {
            return;
        }

        let capacity = this._buffer.length * 2;
        while (capacity < this._length + count) {
            capacity *= 2;
        }

        const grown = new Uint8Array(capacity);
        grown.set(this._buffer.subarray(0, this._length));
        this._buffer = grown;
    }

    u8(value) {
        this._ensure(1);
        this._buffer[this._length++] = value & 0xff;
        return this;
    }

    u16(value) {
        this._ensure(2);
        this._buffer[this._length++] = (value >>> 8) & 0xff;
        this._buffer[this._length++] = value & 0xff;
        return this;
    }

    i32(value) {
        this._ensure(4);
        this._buffer[this._length++] = (value >>> 24) & 0xff;
        this._buffer[this._length++] = (value >>> 16) & 0xff;
        this._buffer[this._length++] = (value >>> 8) & 0xff;
        this._buffer[this._length++] = value & 0xff;
        return this;
    }

    /**
     * 64 位大端。用 BigInt 而不是拆成两个 32 位数 ——
     * 文件长度会超过 2^53，用 Number 做位运算必然静默截断。
     */
    i64(value) {
        this._ensure(8);
        let remaining = BigInt(value);
        for (let i = 7; i >= 0; i--) {
            this._buffer[this._length + i] = Number(remaining & 0xffn);
            remaining >>= 8n;
        }

        this._length += 8;
        return this;
    }

    bytes(source) {
        this._ensure(source.length);
        this._buffer.set(source, this._length);
        this._length += source.length;
        return this;
    }

    /** UTF-8 编码后写入，返回写入的字节数（调用方常需要先写长度）。 */
    utf8(text) {
        return this.bytes(new TextEncoder().encode(text));
    }

    toUint8Array() {
        return this._buffer.slice(0, this._length);
    }
}

/**
 * 只前进的读取器。
 *
 * 越界一律抛错 —— 这里读的全是对端给的不可信数据，
 * 静默返回零字节会让畸形输入伪装成合法内容。
 */
export class ByteReader {
    constructor(data) {
        this._data = data;
        this._position = 0;
    }

    get remaining() {
        return this._data.length - this._position;
    }

    get isAtEnd() {
        return this._position >= this._data.length;
    }

    _require(count) {
        if (count < 0 || this._position + count > this._data.length) {
            throw new RangeError(
                `数据被截断：位置 ${this._position} 处需要 ${count} 字节，只剩 ${this.remaining} 字节。`);
        }
    }

    bytes(count) {
        this._require(count);
        const slice = this._data.subarray(this._position, this._position + count);
        this._position += count;
        return slice;
    }

    u8() {
        return this.bytes(1)[0];
    }

    u16() {
        const b = this.bytes(2);
        return (b[0] << 8) | b[1];
    }

    i32() {
        const b = this.bytes(4);
        // >>> 0 之后再转有符号：i32 的负值在协议里是非法的，
        // 但必须能读出来才能报错，不能在这里就被无符号化掩盖
        return ((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]) | 0;
    }

    /** 返回 Number。文件长度最大 4 TiB 级，远在 2^53 之内，安全。 */
    i64() {
        const b = this.bytes(8);
        let value = 0n;
        for (let i = 0; i < 8; i++) {
            value = (value << 8n) | BigInt(b[i]);
        }

        if (value > BigInt(Number.MAX_SAFE_INTEGER)) {
            throw new RangeError(`64 位长度 ${value} 超出可安全表示的范围。`);
        }

        return Number(value);
    }

    utf8(byteCount) {
        return new TextDecoder('utf-8', { fatal: true }).decode(this.bytes(byteCount));
    }
}

/** base64url（无填充）编码。密钥要放进 URL fragment，不能带 + / =。 */
export function toBase64Url(bytes) {
    let binary = '';
    for (const byte of bytes) {
        binary += String.fromCharCode(byte);
    }

    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/** base64url 解码。非法输入返回 null 而不是抛错 —— 调用方拿到的是用户粘贴的文本。 */
export function fromBase64Url(text) {
    if (typeof text !== 'string' || !/^[A-Za-z0-9\-_]+$/.test(text)) {
        return null;
    }

    const padded = text.replace(/-/g, '+').replace(/_/g, '/');

    try {
        const binary = atob(padded);
        const result = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) {
            result[i] = binary.charCodeAt(i);
        }

        return result;
    } catch {
        return null;
    }
}

/** 定长比较。用于哈希比对，所以不做提前退出。 */
export function bytesEqual(left, right) {
    if (left.length !== right.length) {
        return false;
    }

    let diff = 0;
    for (let i = 0; i < left.length; i++) {
        diff |= left[i] ^ right[i];
    }

    return diff === 0;
}

export function toHex(bytes) {
    return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
}
