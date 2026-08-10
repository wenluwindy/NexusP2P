// 从浏览器的 File 对象构建传输清单。对应 C# 侧 CLI 的 ManifestBuilder。
//
// 流式读取：只持有一个叶子缓冲区，内存占用与文件大小无关。
// 20 GB 文件在这里也不会把标签页撑爆 —— 但哈希本身要跑十几秒，
// 所以调用方应该把它放进 Worker（见 workers/hash.worker.js）。

import { computeFileRoot, computePieceRoot, hashLeaf } from '../core/hashing.js';
import { ManifestEntry, MerkleParameters, TransferManifest } from '../core/manifest.js';
import { toManifestPath } from '../core/safepath.js';

/**
 * 算一个文件的分片根与文件根。
 *
 * 用 Blob.stream() 而不是一次 arrayBuffer()：后者会把整个文件读进内存，
 * 在 20 GB 上直接失败。
 */
export async function hashFile(file, parameters, onProgress) {
    const reader = file.stream().getReader();
    const leafBuffer = new Uint8Array(parameters.leafSize);
    const pieceRoots = [];

    let leafHashes = [];
    let leafFill = 0;
    let pieceLength = 0;
    let totalLength = 0;

    // 把流切成恰好 leafSize 的块。stream 给的块大小是任意的，
    // 不重新切分会让叶子边界与 C# 侧不一致，算出的根就全错。
    const flushLeaf = async () => {
        leafHashes.push(await hashLeaf(leafBuffer.subarray(0, leafFill)));
        pieceLength += leafFill;
        leafFill = 0;

        if (leafHashes.length === parameters.leavesPerPiece) {
            pieceRoots.push(await computePieceRoot(leafHashes, pieceLength));
            leafHashes = [];
            pieceLength = 0;
        }
    };

    while (true) {
        const { done, value } = await reader.read();
        if (done) {
            break;
        }

        let consumed = 0;
        while (consumed < value.length) {
            const take = Math.min(parameters.leafSize - leafFill, value.length - consumed);
            leafBuffer.set(value.subarray(consumed, consumed + take), leafFill);
            leafFill += take;
            consumed += take;
            totalLength += take;

            if (leafFill === parameters.leafSize) {
                await flushLeaf();
            }
        }

        onProgress?.(totalLength);
    }

    // 收尾：不满一个叶子的剩余，以及不满一个分片的剩余叶子。
    // 完全空的文件必须产出恰好一个空叶子 —— 与 C# 的 FileHasher 一致，
    // 这让「分片数为 0」这种特例不存在。
    if (leafFill > 0 || totalLength === 0) {
        await flushLeaf();
    }

    if (leafHashes.length > 0) {
        pieceRoots.push(await computePieceRoot(leafHashes, pieceLength));
    }

    return {
        // computeFileRoot 会消费传入的数组，给它一份副本，
        // 免得把要返回给调用方的分片根列表毁掉
        root: await computeFileRoot([...pieceRoots], totalLength),
        pieceRoots,
        length: totalLength,
    };
}

/**
 * 从一批 File 构建清单。
 *
 * 顶层名字包含在路径里：拖一个 MyStuff 文件夹进来得到 MyStuff/a.txt，
 * 这样接收端能自然重建目录结构，而不是把一堆文件散落进下载目录。
 *
 * webkitRelativePath 在「选文件夹」时才有值；单选文件时退回 name。
 */
export async function buildManifest(files, options = {}) {
    const parameters = options.parameters ?? new MerkleParameters();
    const onProgress = options.onProgress;

    if (files.length === 0) {
        throw new Error('没有选中任何文件。');
    }

    const totalBytes = files.reduce((sum, file) => sum + file.size, 0);
    const entries = [];
    let hashedSoFar = 0;

    for (const file of files) {
        const relative = file.webkitRelativePath !== undefined && file.webkitRelativePath.length > 0
            ? file.webkitRelativePath
            : file.name;

        const resolved = toManifestPath(relative);
        if (resolved.error !== undefined) {
            throw new Error(`路径 "${relative}" 不能用于传输：${resolved.error}`);
        }

        const alreadyHashed = hashedSoFar;
        const result = await hashFile(file, parameters, read =>
            onProgress?.({ hashedBytes: alreadyHashed + read, totalBytes, path: resolved.path }));

        hashedSoFar += result.length;
        entries.push(new ManifestEntry(resolved.path, result.length, result.root, result.pieceRoots));
    }

    return TransferManifest.create(parameters, entries);
}

/**
 * 把 DataTransfer 里拖进来的东西摊平成 File 列表。
 *
 * 拖文件夹时必须走 webkitGetAsEntry 递归 —— DataTransfer.files 对文件夹
 * 只给一个空条目，直接用会得到「拖了文件夹却说没有文件」。
 */
export async function collectDroppedFiles(dataTransfer) {
    const items = [...dataTransfer.items].filter(item => item.kind === 'file');

    // webkitGetAsEntry 必须在同步阶段全部取出：DataTransferItemList 在
    // 第一个 await 之后就失效了
    const entries = items.map(item => item.webkitGetAsEntry?.() ?? null);

    if (entries.every(entry => entry === null)) {
        return [...dataTransfer.files];
    }

    const files = [];
    for (const entry of entries) {
        if (entry !== null) {
            await walkEntry(entry, '', files);
        }
    }

    return files;
}

async function walkEntry(entry, prefix, output) {
    if (entry.isFile) {
        const file = await new Promise((resolve, reject) => entry.file(resolve, reject));

        // FileSystemEntry 给的 File 没有 webkitRelativePath，自己补上，
        // 否则文件夹结构会在清单里丢失
        Object.defineProperty(file, 'webkitRelativePath', {
            value: prefix + file.name,
            configurable: true,
        });

        output.push(file);
        return;
    }

    if (!entry.isDirectory) {
        return;
    }

    const reader = entry.createReader();
    const children = [];

    // readEntries 一次最多给 100 个，必须读到空数组为止
    while (true) {
        const batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject));
        if (batch.length === 0) {
            break;
        }

        children.push(...batch);
    }

    for (const child of children) {
        await walkEntry(child, `${prefix}${entry.name}/`, output);
    }
}
