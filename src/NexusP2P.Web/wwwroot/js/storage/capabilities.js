// 落盘能力探测。实现 AD-6：**不做浏览器判断，只做能力探测**。
//
// 这个文件里没有任何一处读 UA 字符串 —— 按型号分流会把新浏览器
// 无理由地挡在门外，而能力探测天然向前兼容。
//
// 四种策略的实测数据（docs/spikes/browser-storage.md，Task 0.2）：
//
//   目录直写  showDirectoryPicker  内存恒定    无实际上限（受磁盘）
//   OPFS      navigator.storage    ≤10 MiB     5 GiB 实测通过
//   内存 Blob 永远可用             与文件 1:1  **5 GiB 要占 5132 MiB 堆**
//   流式另存  Service Worker       恒定        收完后的「拷贝出去」一步
//
// Blob 的内存占用与文件大小 1:1，所以它只能是最后兜底，而且必须在界面上
// 明说上限。一台 8 GB 内存的笔记本收 5 GiB 文件，标签页会先死。

import { isStreamSaveSupported } from './stream-saver.js';

export const StorageStrategy = {
    /** 用户选一个目录，多文件直接按结构写进去。文件夹传输的首选。 */
    Directory: 'directory',

    /** 用户选一个保存位置，流式写入。单文件的首选。 */
    SaveFile: 'saveFile',

    /** 源私有文件系统，写完再引导用户另存。内存占用恒定但要二次拷贝。 */
    Opfs: 'opfs',

    /** 全部攒在内存里最后给个下载链接。只适合小文件。 */
    Blob: 'blob',
};

/** 内存 Blob 策略的建议上限。超过这个值几乎必然让标签页被杀。 */
export const BLOB_SAFE_LIMIT = 1024 * 1024 * 1024;

export function detectCapabilities() {
    return {
        directory: typeof window.showDirectoryPicker === 'function',
        saveFile: typeof window.showSaveFilePicker === 'function',
        opfs: typeof navigator.storage?.getDirectory === 'function',
        streamSave: isStreamSaveSupported(),
        blob: true,
    };
}

/**
 * 按能力挑一个策略。
 *
 * 文件夹传输优先目录直写：其他策略都只能产出单个文件，多文件要么逐个
 * 弹保存框（十几个文件就是十几次点击），要么打包成 zip（我们不做打包 ——
 * 那会引入一个压缩库并让接收端多一步解压）。
 */
export function chooseStrategy(capabilities, { fileCount, totalBytes }) {
    if (fileCount > 1) {
        if (capabilities.directory) {
            return StorageStrategy.Directory;
        }

        if (capabilities.opfs) {
            return StorageStrategy.Opfs;
        }

        return StorageStrategy.Blob;
    }

    if (capabilities.saveFile) {
        return StorageStrategy.SaveFile;
    }

    if (capabilities.opfs) {
        return StorageStrategy.Opfs;
    }

    return StorageStrategy.Blob;
}

/**
 * 当前策略的大小上限说明。
 *
 * **开始前就要告知**，而不是传到第 40 分钟才失败。Task 0.2 发现
 * `estimate().quota` 不可信（无痕模式下报 6 GiB，2 GiB 就抛
 * QuotaExceededError），所以这里给的是基于实测的保守判断，不读 quota。
 */
export function describeStrategy(strategy, totalBytes) {
    switch (strategy) {
        case StorageStrategy.Directory:
            return {
                label: '直接写入所选文件夹',
                detail: '内存占用恒定，大小只受磁盘剩余空间限制。',
                withinLimit: true,
            };

        case StorageStrategy.SaveFile:
            return {
                label: '流式写入所选文件',
                detail: '内存占用恒定，大小只受磁盘剩余空间限制。',
                withinLimit: true,
            };

        case StorageStrategy.Opfs:
            return {
                label: '先写入浏览器存储，完成后另存',
                detail: '内存占用恒定，但完成后需要再拷贝一次到你选的位置。' +
                    '浏览器配额不可靠，超过 5 GiB 有失败风险。' +
                    '（支持流式另存时，完成后会自动打包成单个 ZIP 保存。）',
                withinLimit: totalBytes <= 5 * 1024 * 1024 * 1024,
            };

        default:
            return {
                label: '全部暂存在内存中',
                detail: `内存占用与文件大小 1:1（${formatSize(totalBytes)} 的内容大约要占 ` +
                    `${formatSize(totalBytes)} 内存）。这是最后的兜底方案。` +
                    '（支持流式另存时，完成后会自动保存，不需要逐个点链接。）',
                withinLimit: totalBytes <= BLOB_SAFE_LIMIT,
            };
    }
}

/** 超限时的建议。大文件本来就属于 exe，这与产品定位是吻合的。 */
export function limitAdvice(strategy, totalBytes) {
    if (strategy === StorageStrategy.Blob) {
        return `这次要收 ${formatSize(totalBytes)}，而当前浏览器只能把内容暂存在内存里 —— ` +
            '很可能在收完之前标签页就被系统结束了。建议改用桌面版程序接收，' +
            '它直接写磁盘，没有这个限制。';
    }

    if (strategy === StorageStrategy.Opfs) {
        return `这次要收 ${formatSize(totalBytes)}，超过了浏览器存储的可靠范围。` +
            '建议改用桌面版程序接收。';
    }

    return null;
}

export function formatSize(bytes) {
    if (bytes >= 1024 ** 3) {
        return `${(bytes / 1024 ** 3).toFixed(2)} GiB`;
    }

    if (bytes >= 1024 ** 2) {
        return `${(bytes / 1024 ** 2).toFixed(1)} MiB`;
    }

    if (bytes >= 1024) {
        return `${Math.round(bytes / 1024)} KiB`;
    }

    return `${bytes} B`;
}
