// 传输清单的规范序列化与解析。对应 C# 的 TransferManifest。
//
// 排序规则用 UTF-16 码元序（JS 的 < 运算符），与 C# 侧刻意选用的
// string.CompareOrdinal 一致 —— C# 的注释里明确写了这是为了让网页端
// 能算出同一个清单哈希。
//
// 清单哈希是这次传输的身份，也是分片加密的 AAD。它错了什么都对不上。

import { ByteReader, ByteWriter } from './bytes.js';
import { HASH_SIZE, MANIFEST_HASH_PREFIX, sha256Bytes } from './hashing.js';
import { isSafePath } from './safepath.js';

const MAGIC = new Uint8Array([0x4e, 0x58, 0x50, 0x32, 0x50, 0x4d, 0x41, 0x4e]);   // "NXP2PMAN"
const FORMAT_VERSION = 1;

export const MAX_ENTRIES = 100_000;
export const MAX_TOTAL_PIECES = 4_000_000;
export const MAX_PATH_LENGTH = 1024;

export const DEFAULT_LEAF_SIZE = 64 * 1024;
export const DEFAULT_PIECE_SIZE = 1024 * 1024;

/** 分片与叶子的尺寸参数。 */
export class MerkleParameters {
    constructor(leafSize = DEFAULT_LEAF_SIZE, pieceSize = DEFAULT_PIECE_SIZE) {
        if (leafSize < 1024 || (leafSize & (leafSize - 1)) !== 0) {
            throw new Error(`叶子块大小 ${leafSize} 必须是不小于 1024 的 2 的幂。`);
        }

        if (pieceSize < leafSize || pieceSize % leafSize !== 0) {
            throw new Error(`分片大小 ${pieceSize} 必须是叶子块大小 ${leafSize} 的整数倍。`);
        }

        this.leafSize = leafSize;
        this.pieceSize = pieceSize;
    }

    get leavesPerPiece() {
        return this.pieceSize / this.leafSize;
    }

    /** 空内容也算一个分片 —— 让「分片数为 0」这种特例不存在。 */
    pieceCount(length) {
        return length === 0 ? 1 : Math.ceil(length / this.pieceSize);
    }

    pieceLength(contentLength, pieceIndex) {
        return Math.min(contentLength - pieceIndex * this.pieceSize, this.pieceSize);
    }

    pieceOffset(pieceIndex) {
        return pieceIndex * this.pieceSize;
    }
}

/** 清单里的一个文件。 */
export class ManifestEntry {
    constructor(path, length, root, pieceRoots) {
        const problem = isSafePath(path);
        if (problem !== null) {
            throw new Error(`拒绝不安全的路径 "${path}"：${problem}`);
        }

        if (pieceRoots.length === 0) {
            throw new Error('分片根列表不能为空。');
        }

        this.path = path;
        this.length = length;
        this.root = root;
        this.pieceRoots = pieceRoots;
    }

    get pieceCount() {
        return this.pieceRoots.length;
    }
}

/** 一次传输的全部内容描述。 */
export class TransferManifest {
    /**
     * 私有构造。走 create() 或 deserialize()，两者都会算好哈希 ——
     * 哈希是异步的（WebCrypto），所以不能在构造函数里做。
     */
    constructor(parameters, entries, directories, hash) {
        this.parameters = parameters;
        this.entries = entries;
        this.directories = directories;
        this.hash = hash;
        this.totalLength = entries.reduce((sum, e) => sum + e.length, 0);
        this.totalPieces = entries.reduce((sum, e) => sum + e.pieceCount, 0);
    }

    /** 从条目集合建清单。会排序、查重、并校验分片数与长度自洽。 */
    static async create(parameters, entries, emptyDirectories = []) {
        if (entries.length === 0) {
            throw new Error('清单至少要有一个条目。');
        }

        if (entries.length > MAX_ENTRIES) {
            throw new Error(`条目数 ${entries.length} 超过上限 ${MAX_ENTRIES}。`);
        }

        // 大小写不敏感查重：Windows 上 a.txt 与 A.TXT 是同一个文件，
        // 放过去会让后一个静默覆盖前一个
        const seen = new Set();
        for (const entry of entries) {
            const key = entry.path.toLowerCase();
            if (seen.has(key)) {
                throw new Error(`清单里有重复路径（忽略大小写）："${entry.path}"。`);
            }

            seen.add(key);

            const expected = parameters.pieceCount(entry.length);
            if (entry.pieceCount !== expected) {
                throw new Error(
                    `条目 "${entry.path}" 长度 ${entry.length} 应有 ${expected} 个分片，` +
                    `实际给了 ${entry.pieceCount} 个。`);
            }
        }

        const totalPieces = entries.reduce((sum, e) => sum + e.pieceCount, 0);
        if (totalPieces > MAX_TOTAL_PIECES) {
            throw new Error(`总分片数 ${totalPieces} 超过上限 ${MAX_TOTAL_PIECES}。`);
        }

        const directories = normalizeDirectories(emptyDirectories, seen);

        // 排序是规范化的一部分，决定清单哈希的稳定性
        const sorted = [...entries].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0));

        const hash = await computeManifestHash(parameters, sorted, directories);
        return new TransferManifest(parameters, sorted, directories, hash);
    }

    /** 规范二进制形式。同一份内容永远产出同一串字节。 */
    serialize() {
        const writer = new ByteWriter(4096);
        writeCanonical(writer, this.parameters, this.entries, this.directories);
        return writer.toUint8Array();
    }

    /**
     * 解析清单。data 是**不可信输入** —— 所有边界都在分配之前校验，
     * 所有路径都过 isSafePath。任一条不合法就整体拒绝，不做部分接受。
     */
    static async deserialize(data) {
        const reader = new ByteReader(data);

        const magic = reader.bytes(MAGIC.length);
        for (let i = 0; i < MAGIC.length; i++) {
            if (magic[i] !== MAGIC[i]) {
                throw new Error('魔数不匹配，这不是一份清单。');
            }
        }

        const version = reader.u8();
        if (version !== FORMAT_VERSION) {
            throw new Error(`清单版本 ${version} 不受支持（本实现只认 ${FORMAT_VERSION}）。`);
        }

        const parameters = new MerkleParameters(reader.i32(), reader.i32());

        const entryCount = reader.i32();
        if (entryCount <= 0 || entryCount > MAX_ENTRIES) {
            throw new Error(`条目数 ${entryCount} 不在 1~${MAX_ENTRIES} 之间。`);
        }

        const entries = [];
        const seen = new Set();
        let totalPieces = 0;
        let previousPath = '';

        for (let i = 0; i < entryCount; i++) {
            const pathLength = reader.u16();
            if (pathLength === 0 || pathLength > MAX_PATH_LENGTH) {
                throw new Error(`第 ${i} 条的路径字节数 ${pathLength} 不合法。`);
            }

            const path = reader.utf8(pathLength);
            const problem = isSafePath(path);
            if (problem !== null) {
                throw new Error(`第 ${i} 条的路径不安全：${problem}`);
            }

            const key = path.toLowerCase();
            if (seen.has(key)) {
                throw new Error(`路径重复（忽略大小写）："${path}"。`);
            }

            seen.add(key);

            // 规范形式要求已排序。未排序说明对端实现不规范，或有人想造出
            // 同内容不同哈希的清单。
            if (i > 0 && previousPath >= path) {
                throw new Error(`清单未按规范顺序排列："${path}" 出现在 "${previousPath}" 之后。`);
            }

            previousPath = path;

            const length = reader.i64();
            if (length < 0) {
                throw new Error(`路径 "${path}" 的长度为负数 ${length}。`);
            }

            const root = reader.bytes(HASH_SIZE).slice();

            // 分片数从长度推导，不从流里读 —— 这样对端没法在这里撒谎诱导巨额分配
            const pieceCount = parameters.pieceCount(length);
            totalPieces += pieceCount;
            if (totalPieces > MAX_TOTAL_PIECES) {
                throw new Error(`总分片数超过上限 ${MAX_TOTAL_PIECES}。`);
            }

            const pieceRoots = [];
            for (let p = 0; p < pieceCount; p++) {
                pieceRoots.push(reader.bytes(HASH_SIZE).slice());
            }

            entries.push(new ManifestEntry(path, length, root, pieceRoots));
        }

        const directoryCount = reader.i32();
        if (directoryCount < 0 || directoryCount > MAX_ENTRIES) {
            throw new Error(`空目录数 ${directoryCount} 不在 0~${MAX_ENTRIES} 之间。`);
        }

        const directories = [];
        let previousDirectory = '';
        for (let i = 0; i < directoryCount; i++) {
            const length = reader.u16();
            if (length === 0 || length > MAX_PATH_LENGTH) {
                throw new Error(`第 ${i} 个空目录的路径字节数 ${length} 不合法。`);
            }

            const directory = reader.utf8(length);
            const problem = isSafePath(directory);
            if (problem !== null) {
                throw new Error(`第 ${i} 个空目录的路径不安全：${problem}`);
            }

            if (i > 0 && previousDirectory >= directory) {
                throw new Error(
                    `空目录未按规范顺序排列："${directory}" 出现在 "${previousDirectory}" 之后。`);
            }

            previousDirectory = directory;
            directories.push(directory);
        }

        if (!reader.isAtEnd) {
            throw new Error(`清单末尾有 ${reader.remaining} 字节多余数据。`);
        }

        // 走 create 而不是直接构造：让「从字节还原」与「从条目新建」
        // 经过完全相同的校验，避免两条路径的检查漂移
        return TransferManifest.create(parameters, entries, directories);
    }
}

/** 校验并排序空目录列表。同一个路径不能既是文件又是目录。 */
function normalizeDirectories(directories, takenPaths) {
    const result = [];
    const seen = new Set();

    for (const directory of directories) {
        const problem = isSafePath(directory);
        if (problem !== null) {
            throw new Error(`拒绝不安全的路径 "${directory}"：${problem}`);
        }

        const key = directory.toLowerCase();
        if (takenPaths.has(key)) {
            throw new Error(`路径 "${directory}" 同时被当作文件和目录。`);
        }

        if (seen.has(key)) {
            throw new Error(`空目录列表里有重复路径（忽略大小写）："${directory}"。`);
        }

        seen.add(key);
        result.push(directory);
    }

    if (result.length > MAX_ENTRIES) {
        throw new Error(`空目录数 ${result.length} 超过上限 ${MAX_ENTRIES}。`);
    }

    result.sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
    return result;
}

function writeCanonical(writer, parameters, entries, directories) {
    writer.bytes(MAGIC);
    writer.u8(FORMAT_VERSION);
    writer.i32(parameters.leafSize);
    writer.i32(parameters.pieceSize);
    writer.i32(entries.length);

    const encoder = new TextEncoder();

    for (const entry of entries) {
        const pathBytes = encoder.encode(entry.path);
        writer.u16(pathBytes.length);
        writer.bytes(pathBytes);
        writer.i64(entry.length);
        writer.bytes(entry.root);

        for (const pieceRoot of entry.pieceRoots) {
            writer.bytes(pieceRoot);
        }
    }

    writer.i32(directories.length);
    for (const directory of directories) {
        const pathBytes = encoder.encode(directory);
        writer.u16(pathBytes.length);
        writer.bytes(pathBytes);
    }
}

async function computeManifestHash(parameters, entries, directories) {
    const writer = new ByteWriter(4096);
    writer.u8(MANIFEST_HASH_PREFIX);
    writeCanonical(writer, parameters, entries, directories);
    return sha256Bytes(writer.toUint8Array());
}

/**
 * 落盘时需要创建的全部目录：文件路径隐含的 + 显式列出的空目录，
 * 按深度排序好让父目录先创建。
 */
export function getAllDirectories(manifest) {
    const all = new Set();

    const addWithAncestors = directory => {
        let current = directory;
        while (current.length > 0) {
            if (all.has(current)) {
                return;   // 这一条及其祖先都已加过
            }

            all.add(current);
            const slash = current.lastIndexOf('/');
            current = slash <= 0 ? '' : current.slice(0, slash);
        }
    };

    for (const entry of manifest.entries) {
        const lastSlash = entry.path.lastIndexOf('/');
        if (lastSlash > 0) {
            addWithAncestors(entry.path.slice(0, lastSlash));
        }
    }

    for (const directory of manifest.directories) {
        addWithAncestors(directory);
    }

    const depth = path => (path.match(/\//g) || []).length;
    return [...all].sort((a, b) => depth(a) - depth(b) || (a < b ? -1 : a > b ? 1 : 0));
}
