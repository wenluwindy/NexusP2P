// 密钥派生与分片加解密。对应 C# 的 KeyDerivation / PieceCipher / BlobCipher。
//
// 两处必须与 C# 完全一致，否则解密会失败（表现为「文件码不匹配」）：
//   1. HKDF 的 info = "NexusP2P/v1/" + 用途，salt 为空
//   2. 分片 nonce = 文件序号(be32) ‖ 分片序号(be64)，恰好 12 字节
//
// nonce 由位置派生而非随机，是这套方案里最关键的安全不变式：
// AES-GCM 下同一密钥重用 nonce 会泄露明文异或值。位置唯一 ⇒ nonce 唯一。
// 文件序号必须参与派生 —— 只用分片序号的话，两个文件的第 0 片会撞 nonce。

export const SECRET_SIZE = 32;
export const NONCE_SIZE = 12;
export const TAG_SIZE = 16;

const LABEL_PREFIX = 'NexusP2P/v1/';
const CONTENT_PURPOSE = 'content';
const MANIFEST_PURPOSE = 'manifest';

/** 生成一次传输的根密钥材料。 */
export function generateSecret() {
    return crypto.getRandomValues(new Uint8Array(SECRET_SIZE));
}

/**
 * HKDF-SHA256 派生 AES-256 密钥。
 *
 * salt 传空：与 C# 侧 `salt: default` 一致。RFC 5869 规定空 salt 等价于
 * 一串 hashLen 个零字节，WebCrypto 与 .NET 在这一点上行为相同。
 */
async function deriveKey(secret, purpose) {
    const material = await crypto.subtle.importKey('raw', secret, 'HKDF', false, ['deriveKey']);

    return crypto.subtle.deriveKey(
        {
            name: 'HKDF',
            hash: 'SHA-256',
            salt: new Uint8Array(0),
            info: new TextEncoder().encode(LABEL_PREFIX + purpose),
        },
        material,
        { name: 'AES-GCM', length: 256 },
        false,
        ['encrypt', 'decrypt']);
}

export function deriveContentKey(secret) {
    return deriveKey(secret, CONTENT_PURPOSE);
}

export function deriveManifestKey(secret) {
    return deriveKey(secret, MANIFEST_PURPOSE);
}

/** nonce = 文件序号(be32) ‖ 分片序号(be64)。见文件头关于唯一性的说明。 */
export function derivePieceNonce(fileIndex, pieceIndex) {
    const nonce = new Uint8Array(NONCE_SIZE);
    const view = new DataView(nonce.buffer);
    view.setUint32(0, fileIndex, false);
    view.setBigUint64(4, BigInt(pieceIndex), false);
    return nonce;
}

/**
 * 分片密码。持有派生后的内容密钥与 AAD（清单哈希）。
 *
 * AAD 绑定清单哈希：即使密钥泄露，也无法把一次传输的密文重放进另一次。
 */
export class PieceCipher {
    static async create(secret, manifestHash) {
        return new PieceCipher(await deriveContentKey(secret), manifestHash);
    }

    constructor(key, manifestHash) {
        this._key = key;
        this._aad = manifestHash;
    }

    /** 返回 明文 ‖ 标签（WebCrypto 的输出格式与 .NET 的 密文+标签 布局相同）。 */
    async encrypt(fileIndex, pieceIndex, plaintext) {
        const result = await crypto.subtle.encrypt(
            {
                name: 'AES-GCM',
                iv: derivePieceNonce(fileIndex, pieceIndex),
                additionalData: this._aad,
                tagLength: TAG_SIZE * 8,
            },
            this._key,
            plaintext);

        return new Uint8Array(result);
    }

    /**
     * 解密。认证失败抛错 —— 调用方应把它当成「这个分片坏了，等重传」，
     * 而不是致命错误。单个分片坏掉是可恢复的。
     */
    async decrypt(fileIndex, pieceIndex, ciphertext) {
        const result = await crypto.subtle.decrypt(
            {
                name: 'AES-GCM',
                iv: derivePieceNonce(fileIndex, pieceIndex),
                additionalData: this._aad,
                tagLength: TAG_SIZE * 8,
            },
            this._key,
            ciphertext);

        return new Uint8Array(result);
    }
}

/**
 * 独立数据块（清单）的加解密。格式：nonce(12) ‖ 密文 ‖ 标签(16)。
 *
 * 这里用随机 nonce 而分片用位置派生：分片有天然唯一的位置可用，清单没有。
 * 重连时会再发一次清单，固定 nonce 在那种情形下就是重用。
 */
export async function sealBlob(key, plaintext) {
    const nonce = crypto.getRandomValues(new Uint8Array(NONCE_SIZE));
    const sealed = await crypto.subtle.encrypt(
        { name: 'AES-GCM', iv: nonce, tagLength: TAG_SIZE * 8 }, key, plaintext);

    const result = new Uint8Array(NONCE_SIZE + sealed.byteLength);
    result.set(nonce, 0);
    result.set(new Uint8Array(sealed), NONCE_SIZE);
    return result;
}

export async function openBlob(key, sealed) {
    if (sealed.length < NONCE_SIZE + TAG_SIZE) {
        throw new Error(
            `密封数据只有 ${sealed.length} 字节，不足 nonce 与标签的 ${NONCE_SIZE + TAG_SIZE} 字节。`);
    }

    const plaintext = await crypto.subtle.decrypt(
        { name: 'AES-GCM', iv: sealed.subarray(0, NONCE_SIZE), tagLength: TAG_SIZE * 8 },
        key,
        sealed.subarray(NONCE_SIZE));

    return new Uint8Array(plaintext);
}
