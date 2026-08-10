// Merkle 树哈希。必须与 C# 的 MerkleHasher 逐字节一致 ——
// 算错一位，接收端算出的分片根就与清单不符，每个分片都会被拒收，
// 表现是「一直重传直到放弃」而不是一个清晰的错误。
//
// 域分隔前缀（与 docs/formats/hashing.md 一致）：
//   0x00 叶子、0x01 内部节点、0x02 分片根、0x03 文件根
//
// 注意分片根与文件根的长度都写成 **be64**。C# 里 ComputePieceRoot 的
// 文档注释写的是 be32，但实现（BindLength）用的是 WriteInt64BigEndian —— 以实现为准。

import { ByteWriter } from './bytes.js';

export const HASH_SIZE = 32;

const LEAF_PREFIX = 0x00;
const NODE_PREFIX = 0x01;
const PIECE_ROOT_PREFIX = 0x02;
const FILE_ROOT_PREFIX = 0x03;

/** 清单哈希的域分隔前缀，与上面四个同属一个命名空间。 */
export const MANIFEST_HASH_PREFIX = 0x10;

async function sha256(data) {
    const digest = await crypto.subtle.digest('SHA-256', data);
    return new Uint8Array(digest);
}

/** 叶子哈希：SHA256(0x00 ‖ data)。data 可以为空（空文件的唯一叶子）。 */
export async function hashLeaf(data) {
    const buffer = new Uint8Array(1 + data.length);
    buffer[0] = LEAF_PREFIX;
    buffer.set(data, 1);
    return sha256(buffer);
}

/** 内部节点：SHA256(0x01 ‖ left ‖ right)。 */
export async function hashNode(left, right) {
    const buffer = new Uint8Array(1 + HASH_SIZE * 2);
    buffer[0] = NODE_PREFIX;
    buffer.set(left, 1);
    buffer.set(right, 1 + HASH_SIZE);
    return sha256(buffer);
}

/**
 * 把一层哈希折叠成根。
 *
 * 某层节点数为奇数时最后一个直接上提（不复制、不补位）——
 * 域分隔让这么做是安全的：上提的叶子哈希与内部节点哈希的输入前缀不同。
 */
export async function computeRoot(hashes) {
    if (hashes.length === 0) {
        throw new Error('至少需要一个哈希才能折叠出根。');
    }

    let level = hashes;
    while (level.length > 1) {
        const next = [];
        for (let i = 0; i < level.length; i += 2) {
            next.push(i + 1 < level.length
                ? await hashNode(level[i], level[i + 1])
                : level[i]);
        }

        level = next;
    }

    return level[0];
}

/** SHA256(域前缀 ‖ 长度_be64 ‖ 子树根)。长度绑定让根自描述。 */
async function bindLength(domain, length, subtreeRoot) {
    const writer = new ByteWriter(1 + 8 + HASH_SIZE);
    writer.u8(domain).i64(length).bytes(subtreeRoot);
    return sha256(writer.toUint8Array());
}

/** 分片根：SHA256(0x02 ‖ pieceLength_be64 ‖ merkleRoot(叶子哈希))。 */
export async function computePieceRoot(leafHashes, pieceLength) {
    return bindLength(PIECE_ROOT_PREFIX, pieceLength, await computeRoot(leafHashes));
}

/** 文件根：SHA256(0x03 ‖ fileLength_be64 ‖ merkleRoot(分片根))。 */
export async function computeFileRoot(pieceRoots, fileLength) {
    return bindLength(FILE_ROOT_PREFIX, fileLength, await computeRoot(pieceRoots));
}

/**
 * 算一个分片（明文）的分片根。接收端校验每个分片时用它。
 *
 * 与发送端流式计算走的是同一套原语，所以两边不会漂移。
 */
export async function hashPiece(plaintext, leafSize) {
    const leafHashes = [];

    if (plaintext.length === 0) {
        // 空内容也有恰好一个（空）叶子 —— 与 C# 的 FileHasher 一致
        leafHashes.push(await hashLeaf(plaintext));
    } else {
        for (let offset = 0; offset < plaintext.length; offset += leafSize) {
            const end = Math.min(offset + leafSize, plaintext.length);
            leafHashes.push(await hashLeaf(plaintext.subarray(offset, end)));
        }
    }

    return computePieceRoot(leafHashes, plaintext.length);
}

export async function sha256Bytes(data) {
    return sha256(data);
}
