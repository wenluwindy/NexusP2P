// 流式落盘的 Service Worker（移植自 StreamSaver.js，MIT License，
// https://github.com/jimmywarting/StreamSaver.js，按 NexusP2P 的同源自部署
// 形态裁剪：页面、worker 与下载 URL 同源，消息由页面直发 worker，
// 不再经过中间人页转发）。
//
// 原理：页面把一个 MessageChannel 的另一端转移过来，worker 用它造一条
// ReadableStream 存着；随后页面用 <a> 导航到一个本作用域内的随机 URL，
// fetch 在这里被拦下，把那条流作为响应体交出去 —— 数据由浏览器直接
// 写进下载文件，全程不进 JS 堆，内存占用恒定。
//
// 它解决的是 File System Access API 之外的世界：Firefox / 移动浏览器
// 没有 showSaveFilePicker，原本只能整份攒在内存里再给下载链接。

/* global self, ReadableStream, Response, Headers, URLSearchParams */

self.addEventListener('install', () => {
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(self.clients.claim());
});

/** downloadUrl → { stream, headers }。取用一次即删：同一 URL 只允许下载一次。 */
const downloads = new Map();

// 页面发来的消息有两种：
//   'ping' —— 保活心跳，传输大文件时 worker 可能被浏览器回收
//   { url, headers } —— 登记一次下载，_ports[0] 是数据入口
self.onmessage = event => {
    if (event.data === 'ping') {
        return;
    }

    const data = event.data;
    const port = event.ports[0];

    const stream = new ReadableStream({
        start(controller) {
            port.onmessage = ({ data: chunk }) => {
                if (chunk === 'end') {
                    controller.close();
                    return;
                }

                if (chunk === 'abort') {
                    controller.error(new Error('下载已被发起方中止。'));
                    return;
                }

                controller.enqueue(chunk);
            };

            // 告诉页面 URL 已登记，可以触发导航了
            port.postMessage({ download: data.url });
        },

        cancel() {
            // 用户在下载管理器里取消了保存
            port.postMessage({ cancelled: true });
        },
    });

    downloads.set(data.url, { stream, headers: data.headers });
};

self.onfetch = event => {
    const url = event.request.url;

    // Firefox 上从隐藏 iframe 发起的保活探测（见 stream.html）
    if (url.endsWith('/ping')) {
        event.respondWith(new Response('pong'));
        return;
    }

    const entry = downloads.get(url);
    if (entry === undefined) {
        return;
    }

    downloads.delete(url);

    // 响应头白名单：只从页面声明的 headers 里取 Content-Length 与
    // Content-Disposition 两项，其余一概不透传 —— 不给任何人凭下载
    // 请求定制任意响应头的机会。
    const headers = new Headers({
        'Content-Type': 'application/octet-stream; charset=utf-8',
        'Content-Security-Policy': "default-src 'none'",
        'X-Content-Security-Policy': "default-src 'none'",
        'X-WebKit-CSP': "default-src 'none'",
    });

    const declared = new Headers(entry.headers ?? {});

    if (declared.has('Content-Length')) {
        headers.set('Content-Length', declared.get('Content-Length'));
    }

    if (declared.has('Content-Disposition')) {
        headers.set('Content-Disposition', declared.get('Content-Disposition'));
    }

    event.respondWith(new Response(entry.stream, { headers }));
};
