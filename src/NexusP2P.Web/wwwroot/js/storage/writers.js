// 落盘实现。每种策略一个 writer，接口统一：
//
//   writePiece(fileIndex, offset, bytes)   按偏移随机写
//   finalize()                             收尾，返回落地结果说明
//
// 为什么必须支持随机写：分片在一轮里是按缺失顺序发的，续传时更是从中间
// 开始。假定「按顺序到达」会在第一次重传时就静默写错位置。

import { StorageStrategy, formatSize } from './capabilities.js';
import { isStreamSaveSupported, saveStreamedFile } from './stream-saver.js';
import { createZipStream } from '../core/zip-stream.js';

/** 目录直写：多文件按清单结构写进用户选的目录。 */
export class DirectoryWriter {
    constructor(directoryHandle, manifest) {
        this._root = directoryHandle;
        this._manifest = manifest;
        this._streams = new Map();
    }

    static async create(manifest) {
        const handle = await window.showDirectoryPicker({ mode: 'readwrite', id: 'nexusp2p-receive' });
        return new DirectoryWriter(handle, manifest);
    }

    async writePiece(fileIndex, offset, bytes) {
        const stream = await this._streamFor(fileIndex);
        await stream.write({ type: 'write', position: offset, data: bytes });
    }

    /** 按需打开写入流并创建中间目录。 */
    async _streamFor(fileIndex) {
        const existing = this._streams.get(fileIndex);
        if (existing !== undefined) {
            return existing;
        }

        const entry = this._manifest.entries[fileIndex];
        const segments = entry.path.split('/');
        let directory = this._root;

        for (const segment of segments.slice(0, -1)) {
            directory = await directory.getDirectoryHandle(segment, { create: true });
        }

        const fileHandle = await directory.getFileHandle(segments[segments.length - 1], { create: true });
        const stream = await fileHandle.createWritable({ keepExistingData: true });
        this._streams.set(fileIndex, stream);
        return stream;
    }

    async finalize() {
        for (const stream of this._streams.values()) {
            await stream.close();
        }

        this._streams.clear();

        // 空目录也要建出来，否则「传一个项目文件夹，结果空的 logs/ 没了」
        for (const directory of this._manifest.directories) {
            let current = this._root;
            for (const segment of directory.split('/')) {
                current = await current.getDirectoryHandle(segment, { create: true });
            }
        }

        return {
            strategy: StorageStrategy.Directory,
            message: `已写入所选文件夹（${this._manifest.entries.length} 个文件）。`,
            downloads: [],
        };
    }

    async abort() {
        for (const stream of this._streams.values()) {
            try {
                await stream.abort();
            } catch {
                // 流可能已经关了
            }
        }

        this._streams.clear();
    }
}

/** 单文件流式写入用户选的保存位置。 */
export class SaveFileWriter {
    constructor(stream, manifest) {
        this._stream = stream;
        this._manifest = manifest;
    }

    static async create(manifest) {
        const entry = manifest.entries[0];
        const suggested = entry.path.split('/').pop();

        const handle = await window.showSaveFilePicker({ suggestedName: suggested });
        return new SaveFileWriter(await handle.createWritable({ keepExistingData: true }), manifest);
    }

    async writePiece(fileIndex, offset, bytes) {
        await this._stream.write({ type: 'write', position: offset, data: bytes });
    }

    async finalize() {
        await this._stream.close();
        return {
            strategy: StorageStrategy.SaveFile,
            message: '已写入所选位置。',
            downloads: [],
        };
    }

    async abort() {
        try {
            await this._stream.abort();
        } catch {
            // 流可能已经关了
        }
    }
}

/**
 * OPFS：先写进浏览器的私有文件系统，完成后给出下载链接。
 *
 * 内存占用恒定（实测 ≤10 MiB），代价是完成后要再拷贝一次。
 * 用 createSyncAccessHandle 才能随机写，而它只在 Worker 里可用 ——
 * 所以这里退回 createWritable + keepExistingData。
 */
export class OpfsWriter {
    constructor(root, manifest) {
        this._root = root;
        this._manifest = manifest;
        this._streams = new Map();
        this._handles = new Map();
    }

    static async create(manifest) {
        const root = await navigator.storage.getDirectory();

        // 每次传输用一个独立子目录，避免上一次的残留混进来
        const session = await root.getDirectoryHandle(
            `nexusp2p-${Date.now().toString(36)}`, { create: true });

        return new OpfsWriter(session, manifest);
    }

    async writePiece(fileIndex, offset, bytes) {
        const stream = await this._streamFor(fileIndex);
        await stream.write({ type: 'write', position: offset, data: bytes });
    }

    async _streamFor(fileIndex) {
        const existing = this._streams.get(fileIndex);
        if (existing !== undefined) {
            return existing;
        }

        // OPFS 里不建目录结构：路径里的 / 换成 __，避免为了目录多绕一层。
        // 反正最终要靠下载链接落地，文件名是下载时才决定的。
        const entry = this._manifest.entries[fileIndex];
        const handle = await this._root.getFileHandle(
            entry.path.replace(/\//g, '__'), { create: true });

        const stream = await handle.createWritable({ keepExistingData: true });
        this._streams.set(fileIndex, stream);
        this._handles.set(fileIndex, handle);
        return stream;
    }

    async finalize() {
        for (const stream of this._streams.values()) {
            await stream.close();
        }

        this._streams.clear();

        const downloads = [];
        const blobs = [];

        for (const [fileIndex, handle] of this._handles) {
            const file = await handle.getFile();
            const path = this._manifest.entries[fileIndex].path;
            blobs.push({ path, blob: file });
            downloads.push({ path, url: URL.createObjectURL(file), size: file.size });
        }

        return {
            strategy: StorageStrategy.Opfs,
            message: startStreamOut(this._manifest, blobs) ??
                '已收完并存在浏览器存储里，点下面的链接保存到磁盘。',
            downloads,
        };
    }

    async abort() {
        for (const stream of this._streams.values()) {
            try {
                await stream.abort();
            } catch {
                // 流可能已经关了
            }
        }

        this._streams.clear();
    }
}

/**
 * 内存兜底：每个文件一个预分配的 Uint8Array。
 *
 * 内存占用与文件大小 1:1（实测 5 GiB → 5132 MiB 堆）。预分配是刻意的：
 * 要失败就在开始时立刻失败，而不是传到一半才 OOM。
 */
export class BlobWriter {
    constructor(manifest) {
        this._manifest = manifest;
        this._buffers = manifest.entries.map(entry => new Uint8Array(entry.length));
    }

    static async create(manifest) {
        return new BlobWriter(manifest);
    }

    async writePiece(fileIndex, offset, bytes) {
        this._buffers[fileIndex].set(bytes, offset);
    }

    async finalize() {
        const blobs = this._manifest.entries.map((entry, index) => ({
            path: entry.path,
            blob: new Blob([this._buffers[index]]),
        }));

        const downloads = blobs.map(({ path, blob }) => ({
            path,
            url: URL.createObjectURL(blob),
            size: blob.size,
        }));

        return {
            strategy: StorageStrategy.Blob,
            message: startStreamOut(this._manifest, blobs) ??
                '已收完，点下面的链接保存到磁盘。',
            downloads,
        };
    }

    async abort() {
        // 让 GC 回收 —— 这是唯一能做的清理
        this._buffers = [];
    }
}

/** 按策略建 writer。用户取消选择目录/文件时抛 AbortError。 */
export async function createWriter(strategy, manifest) {
    switch (strategy) {
        case StorageStrategy.Directory:
            return DirectoryWriter.create(manifest);
        case StorageStrategy.SaveFile:
            return SaveFileWriter.create(manifest);
        case StorageStrategy.Opfs:
            return OpfsWriter.create(manifest);
        default:
            return BlobWriter.create(manifest);
    }
}

/** store 模式 zip 的单字段长度上限（u32）。超过需要 zip64，这里不支持。 */
const ZIP_ENTRY_LIMIT = 0xFFFFFFFF;

const NAME_ENCODER = new TextEncoder();

/**
 * 收完后的「一键流式保存」（OPFS / 内存策略专用增强）：
 *
 *   单文件 → 直接流式另存到下载文件夹；
 *   多文件 → 打包成单个 store zip 流式保存，一次点击替代 N 个链接。
 *
 * 数据经 Service Worker 直落磁盘（stream-saver.js），内存占用与文件
 * 大小无关 —— 这是 FilePizza 的做法，移植过来补齐「没有 File System
 * Access API 的浏览器」的短板。
 *
 * **尽力而为，不阻塞主流程**：传输此时已经完成并校验通过，返回的
 * message 会让用户知道可以等自动保存、也可以直接点兜底链接；异步
 * 失败只记 console 警告 —— 链接还在，数据不会丢。
 *
 * @param {TransferManifest} manifest 清单（空目录列表也进 zip，保结构）
 * @param {Array<{path: string, blob: Blob}>} blobs 收到的内容
 * @returns {string|null} 提示文案；不可用时返回 null（走原有文案）
 */
function startStreamOut(manifest, blobs) {
    if (blobs.length === 0 || !isStreamSaveSupported()) {
        return null;
    }

    for (const { blob } of blobs) {
        if (blob.size > ZIP_ENTRY_LIMIT) {
            return null;   // 单文件超限：zip64 之外，直接另存也超 4 GiB 边界，退回链接
        }
    }

    if (blobs.length === 1) {
        const { path, blob } = blobs[0];
        const filename = path.split('/').pop();
        quietStart(saveStreamedFile(filename, { size: blob.size }, () => blob.stream()));
        return `已收完，正在把「${filename}」流式保存到下载文件夹；` +
            '若浏览器没有自动开始，请点下面的链接。';
    }

    // 多文件 → 单个 zip。store 模式下归档大小可精确算出，
    // 写进 Content-Length 让下载进度条有总量。
    const entries = [];
    let exactSize = 22;   // EOCD

    for (const directory of manifest.directories) {
        const name = `${directory}/`;
        const nameLength = NAME_ENCODER.encode(name).length;
        entries.push({ name, directory: true });
        exactSize += (30 + nameLength) + 16 + (46 + nameLength);
    }

    for (const { path, blob } of blobs) {
        const nameLength = NAME_ENCODER.encode(path).length;
        entries.push({
            name: path,
            stream: () => blob.stream(),
            lastModified: blob.lastModified,
        });
        exactSize += (30 + nameLength) + blob.size + 16 + (46 + nameLength);
    }

    if (exactSize > ZIP_ENTRY_LIMIT) {
        return null;   // 总量超限（zip64 之外），退回逐个链接
    }

    const filename = `nexusp2p-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '')}.zip`;
    quietStart(saveStreamedFile(filename, { size: exactSize }, () => createZipStream(entries)));

    return `已收完，正在把 ${blobs.length} 个文件打包成单个 ZIP 流式保存；` +
        '若浏览器没有自动开始，请点下面的链接逐个保存。';
}

/** 后台跑一个保存任务，失败不炸主流程但要留下痕迹。 */
function quietStart(task) {
    task.catch(error => {
        console.warn(`[writers] 流式保存失败，请改用页面上的下载链接：${error?.message ?? error}`);
    });
}

export { formatSize };
