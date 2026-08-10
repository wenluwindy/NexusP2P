// 跨实现一致性向量。
//
// 这个脚本把 JS 侧算出的哈希、清单字节、密文打印成 JSON；
// C# 侧有一个对应的测试读同样的输入算同样的东西并比对。
//
// 为什么值得单独做这件事：清单哈希、分片根、nonce 派生这三样只要有一位不同，
// 表现就是「网页发给 exe 时每个分片都被拒收」，而错误信息只会说
// 「连续 16 个分片校验失败」—— 完全指不到真正的原因。
//
// 跑：node src/NexusP2P.Web/tests/vectors.mjs

import { toHex } from '../wwwroot/js/core/bytes.js';
import { hashLeaf, hashNode, computePieceRoot, computeFileRoot, hashPiece }
    from '../wwwroot/js/core/hashing.js';
import { ManifestEntry, MerkleParameters, TransferManifest } from '../wwwroot/js/core/manifest.js';
import { PieceCipher, deriveContentKey, deriveManifestKey, derivePieceNonce }
    from '../wwwroot/js/core/crypto.js';
import { PieceBitfield, serializePiece } from '../wwwroot/js/core/messages.js';
import { writeFrame } from '../wwwroot/js/core/frame.js';

/** 固定密钥材料，让向量可复现。 */
const SECRET = new Uint8Array(32);
for (let i = 0; i < 32; i++) {
    SECRET[i] = i;
}

/** 固定内容：0,1,2,… 循环，长度刻意跨越叶子与分片边界。 */
function pattern(length) {
    const data = new Uint8Array(length);
    for (let i = 0; i < length; i++) {
        data[i] = i % 251;
    }

    return data;
}

const out = {};

// ---- 哈希原语 ----
out.leafEmpty = toHex(await hashLeaf(new Uint8Array(0)));
out.leafAbc = toHex(await hashLeaf(new TextEncoder().encode('abc')));

const leafA = await hashLeaf(new Uint8Array([1]));
const leafB = await hashLeaf(new Uint8Array([2]));
out.nodeAB = toHex(await hashNode(leafA, leafB));

// 三个叶子：奇数节点直接上提的路径
out.rootOfThree = toHex(await computePieceRoot([leafA, leafB, await hashLeaf(new Uint8Array([3]))], 3));

// ---- 小参数下的分片根与文件根（1 KiB 叶子 / 4 KiB 分片）----
const small = new MerkleParameters(1024, 4096);

// 长度 10000 = 2 个分片（4096 + 4096 + 1808）
const content = pattern(10000);
const pieceRoots = [];
for (let offset = 0; offset < content.length; offset += small.pieceSize) {
    const piece = content.subarray(offset, Math.min(offset + small.pieceSize, content.length));
    pieceRoots.push(await hashPiece(piece, small.leafSize));
}

out.pieceRoots = pieceRoots.map(toHex);
out.fileRoot = toHex(await computeFileRoot([...pieceRoots], content.length));

// 空文件：恰好一个空叶子、一个分片
const emptyPieceRoot = await hashPiece(new Uint8Array(0), small.leafSize);
out.emptyPieceRoot = toHex(emptyPieceRoot);
out.emptyFileRoot = toHex(await computeFileRoot([emptyPieceRoot], 0));

// ---- 清单 ----
const manifest = await TransferManifest.create(
    small,
    [
        new ManifestEntry('docs/b.txt', content.length,
            await computeFileRoot([...pieceRoots], content.length), pieceRoots),
        new ManifestEntry('a.bin', 0, await computeFileRoot([emptyPieceRoot], 0), [emptyPieceRoot]),
    ],
    ['docs/empty']);

out.manifestSerialized = toHex(manifest.serialize());
out.manifestHash = toHex(manifest.hash);
out.manifestTotalLength = manifest.totalLength;
out.manifestTotalPieces = manifest.totalPieces;
out.manifestOrder = manifest.entries.map(e => e.path);

// ---- 密钥派生 ----
// deriveKey 出来的 CryptoKey 不可导出，所以用 deriveBits 以完全相同的参数
// 重算一次。参数一致 ⇒ 结果就是 PieceCipher 实际用的那把密钥。
out.contentKey = toHex(await deriveKeyBits('content'));
out.manifestKey = toHex(await deriveKeyBits('manifest'));

// ---- nonce 派生 ----
out.nonce_0_0 = toHex(derivePieceNonce(0, 0));
out.nonce_1_0 = toHex(derivePieceNonce(1, 0));
out.nonce_0_1 = toHex(derivePieceNonce(0, 1));
out.nonce_big = toHex(derivePieceNonce(0x01020304, 21542142465));

// ---- 分片加密（确定性：nonce 由位置派生，所以密文可复现）----
const cipher = await PieceCipher.create(SECRET, manifest.hash);
out.ciphertext_0_0 = toHex(await cipher.encrypt(0, 0, pattern(64)));
out.ciphertext_2_5 = toHex(await cipher.encrypt(2, 5, pattern(64)));

// ---- 线上格式 ----
out.pieceMessage = toHex(serializePiece(3, 7, new Uint8Array([0xaa, 0xbb])));
out.frame = toHex(writeFrame(0x03, 5, 2, new Uint8Array([9, 9, 9])));

const bitfield = new PieceBitfield(11);
bitfield.set(0);
bitfield.set(7);
bitfield.set(10);
out.bitfield = toHex(bitfield.serialize());

async function deriveKeyBits(purpose) {
    const material = await crypto.subtle.importKey('raw', SECRET, 'HKDF', false, ['deriveBits']);

    return new Uint8Array(await crypto.subtle.deriveBits(
        {
            name: 'HKDF',
            hash: 'SHA-256',
            salt: new Uint8Array(0),
            info: new TextEncoder().encode('NexusP2P/v1/' + purpose),
        },
        material,
        256));
}

console.log(JSON.stringify(out, null, 2));
