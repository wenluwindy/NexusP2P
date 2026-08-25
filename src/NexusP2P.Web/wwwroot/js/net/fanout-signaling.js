// 一对多发送方的信令客户端（V2，AD-12）。对应 C# 的 FanOutSignalingClient。
//
// 与 signaling.js 里 SignalingClient 的分工：那个类服务一对一（接收方永远用它）；
// 这个类只给一对多的发送方用 —— 建房时声明 maxReceivers，之后按 peerId
// 路由每个接收方的信令（收 from、发 to）。
//
// V1 那两个来之不易的教训在这里**逐 peer** 适用：
//   1. peer-joined 不能靠事件等 —— 用到达队列缓冲，订阅前进来的不丢；
//   2. 信令要按 peer 攒住 —— 每个 peer 的 RTCPeerConnection 要一点时间建好，
//      这期间到达的 answer/候选按 peerId 入队，beginSignalDelivery 时补发。

import { SignalingError } from './signaling.js';

/** 建房成功后拿到的东西（V2：多了生效的席位数）。passwordProtected=false 且带了密码 = 旧服务器。 */
export class FanOutRoomCreated {
    constructor(code, shareUrlBase, iceServers, maxReceivers, passwordProtected = false) {
        this.code = code;
        this.shareUrlBase = shareUrlBase;
        this.iceServers = iceServers;
        this.maxReceivers = maxReceivers;
        this.passwordProtected = passwordProtected;
    }
}

export class FanOutSignalingClient {
    /** @param origin 信令服务器基址；留空表示用当前页面的同源地址。 */
    constructor(origin = '') {
        this._origin = origin.replace(/\/+$/, '');
        this._socket = null;
        this._closed = false;

        // peerId → { flowing, pending: [], onDescription, onCandidate }
        this._peers = new Map();

        // 进房的接收方队列 + 等待者（Channel 语义：先到的排队，不丢）
        this._arrivals = [];
        this._arrivalWaiters = [];

        this.onReceiverLeft = null;
        this.onError = null;
    }

    /** 建房（声明接收方席位数），返回文件码、分享基址与生效席位。 */
    async createRoom(maxReceivers, signal, password) {
        if (!(Number.isInteger(maxReceivers)) || maxReceivers < 1) {
            throw new SignalingError(`maxReceivers 必须是不小于 1 的整数，实际为 ${maxReceivers}。`);
        }

        // 口令为空时不拼 —— 与从前逐字节一致
        const passwordQuery = typeof password === 'string' && password.length > 0
            ? `&password=${encodeURIComponent(password)}`
            : '';
        const url = this._buildUrl(`/signal/create?maxReceivers=${maxReceivers}${passwordQuery}`);
        throwIfAborted(signal);

        // 【与 SignalingClient._connect 同样的修复】：onmessage 必须在建 socket
        // 的同一个同步块里挂上，否则应答到得快时会在窗口期丢失。
        const queued = [];
        let deliver = data => queued.push(data);

        await withAbort(new Promise((resolve, reject) => {
            this._socket = new WebSocket(url);
            this._socket.binaryType = 'arraybuffer';
            this._socket.onmessage = event => deliver(event.data);
            this._socket.onopen = () => resolve();
            this._socket.onerror = () => reject(new SignalingError(
                `连接信令服务器失败：${url}。可能是地址不对、服务未启动，或尝试过于频繁。`, true));
        }), signal, () => this._socket?.close());

        const message = await withAbort(new Promise((resolve, reject) => {
            const handle = data => {
                let parsed;
                try {
                    parsed = JSON.parse(data);
                } catch {
                    reject(new SignalingError('建房时收到无法解析的消息。'));
                    return;
                }

                if (parsed.type === 'created') {
                    resolve(parsed);
                } else if (parsed.type === 'error') {
                    reject(new SignalingError(parsed.message ?? '建房失败。'));
                } else {
                    this._dispatch(parsed);
                }
            };

            deliver = handle;

            this._socket.onclose = () => reject(new SignalingError(
                '建房时连接被关闭，服务器没有给出应答。', true));

            while (queued.length > 0) {
                handle(queued.shift());
            }
        }), signal, () => this._socket?.close());

        this._startPump();

        return new FanOutRoomCreated(
            message.code ?? throwMissing('文件码'),
            message.shareUrlBase ?? '',
            readIceServers(message),
            // 旧服务器不回显 maxReceivers：视为 1，调用方据此降级为一对一（AD-15）
            typeof message.maxReceivers === 'number' ? message.maxReceivers : 1,
            // 旧服务器不回显 passwordProtected：视为未生效，调用方据此警告
            message.passwordProtected === true);
    }

    /**
     * 依次取出进房的接收方 peerId。没有新接收方时会等；
     * 连接关闭后返回 null。
     */
    waitForReceiver(signal) {
        if (this._arrivals.length > 0) {
            return Promise.resolve(this._arrivals.shift());
        }

        if (this._closed) {
            return Promise.resolve(null);
        }

        return withAbort(new Promise(resolve => {
            this._arrivalWaiters.push(resolve);
        }), signal, () => {});
    }

    /**
     * 为一个接收方挂上信令处理器并开闸。两个处理器都挂好了才调 ——
     * 开闸前到达的信令按原顺序补发（与 V1 的 beginSignalDelivery 同理）。
     */
    beginSignalDelivery(peerId, onDescription, onCandidate) {
        const sink = this._sink(peerId);
        sink.onDescription = onDescription;
        sink.onCandidate = onCandidate;
        sink.flowing = true;

        const pending = sink.pending;
        sink.pending = [];
        for (const payload of pending) {
            deliverSignal(sink, payload);
        }
    }

    /** 链路拆除后忘掉这个 peer 的路由状态。 */
    forgetPeer(peerId) {
        this._peers.delete(peerId);
    }

    /** 把本地描述送给指定接收方（带 to，AD-12）。 */
    sendDescription(peerId, description) {
        this._sendSignal(peerId, { sdp: description.sdp, type: description.type });
    }

    /** 把本地候选送给指定接收方。 */
    sendCandidate(peerId, candidate) {
        this._sendSignal(peerId, { candidate: candidate.candidate, mid: candidate.sdpMid });
    }

    close() {
        this._closed = true;

        // 等新接收方的调用方要醒过来并拿到「不会再有了」
        const waiters = this._arrivalWaiters;
        this._arrivalWaiters = [];
        for (const waiter of waiters) {
            waiter(null);
        }

        if (this._socket !== null && this._socket.readyState <= WebSocket.OPEN) {
            this._socket.close(1000, '结束');
        }

        this._socket = null;
    }

    _startPump() {
        this._socket.onmessage = event => {
            try {
                this._dispatch(JSON.parse(event.data));
            } catch {
                // 畸形消息忽略即可
            }
        };

        this._socket.onclose = () => {
            if (!this._closed) {
                this.close();
            }
        };

        this._socket.onerror = () => {};
    }

    _dispatch(message) {
        switch (message.type) {
            case 'peer-joined': {
                const peerId = message.peerId;
                if (typeof peerId !== 'string' || peerId.length === 0) {
                    break;
                }

                const waiter = this._arrivalWaiters.shift();
                if (waiter !== undefined) {
                    waiter(peerId);
                } else {
                    this._arrivals.push(peerId);
                }

                break;
            }

            case 'peer-left':
                if (typeof message.peerId === 'string' && message.peerId.length > 0) {
                    this.onReceiverLeft?.(message.peerId);
                }

                break;

            case 'error':
                this.onError?.(message.message ?? '服务器报告了一个错误。');
                break;

            case 'signal': {
                const from = message.from;
                if (typeof from !== 'string' || from.length === 0 ||
                    message.payload === null || typeof message.payload !== 'object') {
                    break;
                }

                const sink = this._sink(from);
                if (!sink.flowing) {
                    sink.pending.push(message.payload);
                } else {
                    deliverSignal(sink, message.payload);
                }

                break;
            }
        }
    }

    _sink(peerId) {
        let sink = this._peers.get(peerId);
        if (sink === undefined) {
            sink = { flowing: false, pending: [], onDescription: null, onCandidate: null };
            this._peers.set(peerId, sink);
        }

        return sink;
    }

    _sendSignal(peerId, payload) {
        if (this._socket === null || this._socket.readyState !== WebSocket.OPEN) {
            return;
        }

        this._socket.send(JSON.stringify({ type: 'signal', payload, to: peerId }));
    }

    _buildUrl(path) {
        const base = this._origin.length > 0 ? this._origin : window.location.origin;
        const url = new URL(path, base.endsWith('/') ? base : base + '/');
        url.protocol = url.protocol === 'https:' ? 'wss:' : url.protocol === 'http:' ? 'ws:' : url.protocol;
        return url.toString();
    }
}

/** 服务器不解析 payload，所以这里要自己分辨是描述还是候选。 */
function deliverSignal(sink, payload) {
    if (typeof payload.sdp === 'string' && typeof payload.type === 'string') {
        sink.onDescription?.({ sdp: payload.sdp, type: payload.type });
        return;
    }

    if (typeof payload.candidate === 'string') {
        sink.onCandidate?.({ candidate: payload.candidate, sdpMid: payload.mid ?? null });
    }
}

function readIceServers(message) {
    if (!Array.isArray(message.iceServers)) {
        return [];
    }

    return message.iceServers.filter(entry => entry !== null && Array.isArray(entry.urls));
}

function throwMissing(what) {
    throw new SignalingError(`服务器没有返回${what}。`);
}

function throwIfAborted(signal) {
    if (signal?.aborted === true) {
        throw new DOMException('已取消。', 'AbortError');
    }
}

function withAbort(promise, signal, cleanup) {
    if (signal === undefined) {
        return promise;
    }

    if (signal.aborted === true) {
        cleanup();
        return Promise.reject(new DOMException('已取消。', 'AbortError'));
    }

    return new Promise((resolve, reject) => {
        const onAbort = () => {
            cleanup();
            reject(new DOMException('已取消。', 'AbortError'));
        };

        signal.addEventListener('abort', onAbort, { once: true });

        promise
            .then(resolve, reject)
            .finally(() => signal.removeEventListener('abort', onAbort));
    });
}
