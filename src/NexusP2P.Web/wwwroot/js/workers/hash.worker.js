// 清单计算的 Worker。
//
// 为什么必须放在 Worker 里：20 GB 的 SHA-256 要跑十几秒到几十秒。
// 在主线程上跑的话界面完全冻死 —— 连「正在计算校验和」这行字都刷不出来，
// 用户看到的是一个假死的页面。
//
// File 对象可以直接 postMessage 过来（结构化克隆支持它），底层数据不会被拷贝。

import { buildManifest } from '../transfer/manifest-builder.js';
import { MerkleParameters } from '../core/manifest.js';

self.onmessage = async event => {
    const { files, leafSize, pieceSize } = event.data;

    try {
        const parameters = new MerkleParameters(leafSize, pieceSize);

        const manifest = await buildManifest(files, {
            parameters,
            onProgress: progress => self.postMessage({ type: 'progress', ...progress }),
        });

        // 清单对象带方法，不能直接结构化克隆 —— 传回序列化后的字节，
        // 主线程用 TransferManifest.deserialize 还原。这样两边走的是
        // 同一条解析路径，不会出现「Worker 里算的清单和主线程里的不一致」。
        self.postMessage({
            type: 'done',
            serialized: manifest.serialize(),
            hash: manifest.hash,
            totalLength: manifest.totalLength,
            totalPieces: manifest.totalPieces,
            entryCount: manifest.entries.length,
        });
    } catch (error) {
        self.postMessage({ type: 'error', message: error.message });
    }
};
