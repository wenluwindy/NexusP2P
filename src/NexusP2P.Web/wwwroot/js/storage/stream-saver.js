// StreamSaver 式流式落盘的客户端（移植自 StreamSaver.js，MIT License，
// https://github.com/jimmywarting/StreamSaver.js，按同源自部署裁剪）。
//
// 用法（高层封装见本目录 writers.js）：
//
//   const stop = await saveStreamedFile('a.zip', { size: 12345 }, source);
//   // source 是 () => ReadableStream<Uint8Array>
//
// 数据经 MessageChannel 直送 sw.js，浏览器把它写进下载文件 ——
// 内存占用与文件大小无关，这是它替代「整份攒在内存里再给链接」的意义。
//
// 注意：与传输会话不同，这里**不需要随机写** —— 它只在收完之后的
// 「拷贝出去」一步用，顺序流式即可。

/** 桥接 iframe 就绪消息的等待上限。超时就当没有它也能继续。 */
const BRIDGE_TIMEOUT_MS = 4000;

/** 打包/拷贝期间的保活间隔。浏览器在 ~30 秒空闲后可能回收 worker。 */
const KEEPALIVE_INTERVAL_MS = 2000;

/** 复用同一次注册与同一个保活 iframe。 */
let readyPromise = null;

/**
 * 当前环境能否流式保存：需要安全上下文（https 或 localhost）、
 * Service Worker 与 ReadableStream。能力探测而不是浏览器判断 ——
 * 与 storage/capabilities.js 同一条原则。
 */
export function isStreamSaveSupported() {
    return typeof window === 'object' && window !== null &&
        window.isSecureContext === true &&
        'serviceWorker' in navigator &&
        typeof window.ReadableStream === 'function' &&
        typeof window.MessageChannel === 'function';
}

/**
 * 把 source() 产出的流送进浏览器下载（Content-Disposition: attachment）。
 *
 * @param {string} filename 建议保存名（进入 Content-Disposition，UTF-8）
 * @param {{ size?: number }} options 总字节数。提供时写入 Content-Length，
 *   下载进度条因此有总量可显示；打包流等未知长度可省略
 * @param {() => ReadableStream<Uint8Array>} source 数据源
 * @returns {Promise<void>} 数据全部送达 worker 即 resolve；
 *   磁盘写失败由下载管理器体现，这里感知不到
 */
export async function saveStreamedFile(filename, options, source) {
    const registration = await ensureReady();
    const worker = registration.active;
    if (worker === null) {
        throw new Error('Service Worker 尚未激活。');
    }

    const url = makeDownloadUrl(registration.scope, filename);

    const channel = new MessageChannel();
    const port = channel.port1;

    // 登记 URL 后 worker 立刻回 { download: url }，随后才值得开始推数据
    const downloadStarted = new Promise((resolve, reject) => {
        port.onmessage = event => {
            if (event.data !== undefined && event.data.download !== undefined) {
                triggerDownload(event.data.download);
                resolve();
            }
        };

        setTimeout(
            () => reject(new Error('Service Worker 没有响应流式下载请求。')),
            10_000);
    });

    worker.postMessage(
        { url, headers: buildHeaders(filename, options.size) },
        [channel.port2]);

    const keepAlive = startKeepAlive(worker);

    try {
        const writer = new WritableStream({
            write(chunk) {
                // 转移 buffer 避免整块复制；来源都是一次性缓冲（File/Blob 读出、
                // zip 流新建），没有共享同一 ArrayBuffer 的多次写入
                const buffer = chunk.buffer;
                if (buffer !== undefined && buffer.byteLength === chunk.byteLength &&
                    chunk instanceof Uint8Array) {
                    port.postMessage(chunk, [buffer]);
                } else {
                    port.postMessage(chunk);
                }
            },

            close() {
                port.postMessage('end');
                port.close();
            },

            abort() {
                port.postMessage('abort');
                port.close();
            },
        });

        await source().pipeTo(writer);
        await downloadStarted;
    } finally {
        keepAlive.stop();
    }
}

/** 注册 worker 并挂好保活 iframe。失败不缓存，下次重试。 */
function ensureReady() {
    if (readyPromise === null) {
        readyPromise = prepare().catch(error => {
            readyPromise = null;
            throw error;
        });
    }

    return readyPromise;
}

async function prepare() {
    const registration = await navigator.serviceWorker.register('/sw.js', { scope: '/' });
    await navigator.serviceWorker.ready;

    if (registration.active === null) {
        throw new Error('Service Worker 未能激活。');
    }

    await createBridge();
    return registration;
}

/**
 * 挂一个隐藏 iframe 常驻页面：持住注册、按需应答 /ping（Firefox 上
 * worker 比 Chrome 更容易被回收）。iframe 起不来不致命 —— 有它更稳，
 * 没有它主路径仍然可用，超时后照常继续。
 */
function createBridge() {
    return new Promise(resolve => {
        let settled = false;
        const finish = () => {
            if (!settled) {
                settled = true;
                window.removeEventListener('message', onMessage);
                resolve();
            }
        };

        const onMessage = event => {
            if (event.data === 'nexusp2p-stream-ready') {
                finish();
            }
        };

        window.addEventListener('message', onMessage);
        setTimeout(finish, BRIDGE_TIMEOUT_MS);

        const frame = document.createElement('iframe');
        frame.hidden = true;
        frame.src = '/stream.html';
        frame.addEventListener('error', finish, { once: true });
        document.body.append(frame);
    });
}

/** worker 作用域内的一次性 URL。随机段保证不会与真实路由相撞。 */
function makeDownloadUrl(scope, filename) {
    const base = scope.endsWith('/') ? scope : scope + '/';
    return new URL(`${base}download/${crypto.randomUUID()}/${encodeURIComponent(filename)}`,
        window.location.href).toString();
}

/** 只声明白名单头：sw.js 侧同样只认这两个，两头一致。 */
function buildHeaders(filename, size) {
    const headers = {
        'Content-Disposition': `attachment; filename*=UTF-8''${rfc5987Encode(filename)}`,
    };

    if (Number.isFinite(size) && size >= 0) {
        headers['Content-Length'] = String(size);
    }

    return headers;
}

/**
 * RFC 5987/6266 编码：非 ASCII 文件名放进 Content-Disposition 的标准写法，
 * 否则中文文件名在部分下载器里会变成乱码。
 */
function rfc5987Encode(text) {
    return encodeURIComponent(text)
        .replace(/['()]/g, escape)
        .replace(/\*/g, '%2A');
}

function triggerDownload(url) {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.rel = 'noopener';
    document.body.append(anchor);
    anchor.click();
    anchor.remove();
}

/** 心跳：pump 期间维持 worker 存活。 */
function startKeepAlive(worker) {
    const timer = setInterval(() => worker.postMessage('ping'), KEEPALIVE_INTERVAL_MS);
    return { stop: () => clearInterval(timer) };
}
