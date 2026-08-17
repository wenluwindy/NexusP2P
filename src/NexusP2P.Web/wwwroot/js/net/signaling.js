// 信令 WebSocket 客户端。对应 C# 的 SignalingClient。
//
// 两个竞态在 C# 侧踩过并修好了（见 tasks/todo.md 的「阶段 3 查出的问题」），
// 这里必须同样处理，否则网页端会稳定地卡在「两端干等、谁都不报错」：
//
//   1. peer-joined 只在「对方进来那一刻」发一次。晚回来的一方等不到它，
//      靠的是进房应答里的 peerPresent。所以用一个已锁存的 Promise 而不是事件。
//   2. 建 RTCPeerConnection 与挂处理器之间有窗口，这期间到达的 offer 会丢。
//      所以在 beginSignalDelivery() 之前先把信令攒住，之后按原顺序补发。

/** 建房成功后拿到的东西。 */
export class RoomCreated {
    constructor(code, shareUrlBase, iceServers) {
        this.code = code;
        this.shareUrlBase = shareUrlBase;
        this.iceServers = iceServers;
    }
}

/** 信令交互失败。isRetryable 把「网络抖了一下」与「这个码没用」分开。 */
export class SignalingError extends Error {
    constructor(message, retryable = false) {
        super(message);
        this.name = 'SignalingError';
        this.isRetryable = retryable;
    }
}

export class SignalingClient {
    /** @param origin 信令服务器基址；留空表示用当前页面的同源地址。 */
    constructor(origin = '') {
        this._origin = origin.replace(/\/+$/, '');
        this._socket = null;
        this._pendingSignals = [];
        this._signalsFlowing = false;
        this._closed = false;

        this.onRemoteDescription = null;
        this.onRemoteCandidate = null;
        this.onPeerLeft = null;
        this.onError = null;

        // 「对端已进房」的锁存。构造时就建好，无论何时 await 都能拿到结果。
        this._peerPresent = createLatch();
    }

    /** 建房，返回文件码与分享链接基址。发送方用这个。 */
    async createRoom(signal) {
        const message = await this._connect('/signal/create', 'created', '建房', signal);

        return new RoomCreated(
            message.code ?? throwMissingCode(),
            message.shareUrlBase ?? '',
            readIceServers(message));
    }

    /** 用文件码进房。 */
    async joinRoom(code, asSender = false, signal) {
        const role = asSender ? 'sender' : 'receiver';
        const message = await this._connect(
            `/signal/join/${encodeURIComponent(code)}?role=${role}`, 'joined', '进房', signal);

        // 重连回到的房间里对端可能已经在了 —— 见文件头第 1 条
        if (message.peerPresent === true) {
            this._peerPresent.resolve();
        }

        return readIceServers(message);
    }

    /**
     * 等对端进房。已经进来过就立刻返回。
     *
     * 这一步常常等很久（几分钟到几小时都正常），所以取消必须能立刻打断它，
     * 而不是要等到对端真的出现或信令连接自己断开。
     */
    waitForPeer(signal) {
        if (signal === undefined) {
            return this._peerPresent.promise;
        }

        if (signal.aborted === true) {
            return Promise.reject(new DOMException('已取消。', 'AbortError'));
        }

        return new Promise((resolve, reject) => {
            const onAbort = () => reject(new DOMException('已取消。', 'AbortError'));
            signal.addEventListener('abort', onAbort, { once: true });

            this._peerPresent.promise
                .then(resolve, reject)
                .finally(() => signal.removeEventListener('abort', onAbort));
        });
    }

    /**
     * 声明描述与候选的处理器都已挂好，并把在此之前到达的信令按原顺序补发。
     *
     * 挂完处理器必须调这个 —— 见文件头第 2 条。重复调用无害。
     */
    beginSignalDelivery() {
        this._signalsFlowing = true;

        const pending = this._pendingSignals;
        this._pendingSignals = [];
        for (const payload of pending) {
            this._deliverSignal(payload);
        }
    }

    sendDescription(description) {
        this._sendSignal({ sdp: description.sdp, type: description.type });
    }

    sendCandidate(candidate) {
        this._sendSignal({ candidate: candidate.candidate, mid: candidate.sdpMid });
    }

    close() {
        this._closed = true;
        this._peerPresent.reject(new SignalingError('信令连接已关闭。', true));

        if (this._socket !== null && this._socket.readyState <= WebSocket.OPEN) {
            this._socket.close(1000, '结束');
        }

        this._socket = null;
    }

    /**
     * 连上并等到握手应答。
     *
     * **握手应答不保证是第一条消息**：两端几乎同时进房时，对端的 peer-joined
     * 可能先一步到达。把「不是应答」一律当成错误会让重连稳定失败在这里。
     */
    async _connect(path, expectedType, what, signal) {
        const url = this._buildUrl(path);
        throwIfAborted(signal);

        // 【必须在建 socket 的同一个同步块里就挂上 onmessage】
        //
        // 服务器接受连接后**立刻**发 created/joined，不等客户端先说话。
        // 而「等 open 决议」与「挂 onmessage」之间隔着若干 await 与
        // .finally 跳板 —— 应答在这个窗口里到达时，会被投给一个还没有
        // 处理器的 socket 然后永久消失，握手 Promise 再也不会决议。
        //
        // 症状极具误导性：WebSocket 是 101 成功的，浏览器 Network 面板里
        // 那条 created 帧清清楚楚（帧的记录与 JS 有没有处理器无关），
        // 控制台一个错都没有，界面却永远停在「等待生成…」。
        //
        // 所以这里先把消息攒进队列，真正的处理器挂好后再按原顺序补发 ——
        // 与文件头第 2 条对信令用的是同一套办法，握手也不能例外。
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

        // 握手完成前到达的消息不能丢，照常走 _dispatch
        const handshake = await withAbort(new Promise((resolve, reject) => {
            const handle = data => {
                let message;
                try {
                    message = JSON.parse(data);
                } catch {
                    reject(new SignalingError(`${what}时收到无法解析的消息。`));
                    return;
                }

                if (message.type === expectedType) {
                    resolve(message);
                    return;
                }

                if (message.type === 'error') {
                    reject(new SignalingError(message.message ?? `${what}失败。`));
                    return;
                }

                if (typeof message.type !== 'string') {
                    reject(new SignalingError(`${what}时收到没有类型的消息。`));
                    return;
                }

                this._dispatch(message);
            };

            // 之后到达的直接走 handle
            deliver = handle;

            this._socket.onclose = () => reject(new SignalingError(
                `${what}时连接被关闭，服务器没有给出应答。`, true));

            // 补发窗口期攒下的消息。重复 resolve/reject 是无害的空操作，
            // 所以即使应答早就在队列里也能正确决议。
            while (queued.length > 0) {
                handle(queued.shift());
            }
        }), signal, () => this._socket?.close());

        this._startPump();
        return handshake;
    }

    _startPump() {
        this._socket.onmessage = event => {
            try {
                this._dispatch(JSON.parse(event.data));
            } catch {
                // 畸形消息忽略即可，不值得为它断开一条正常的信令连接
            }
        };

        this._socket.onclose = () => {
            // 传输建立后信令就没用了，断开是常态。但若还在等对端，要让它醒过来。
            this._peerPresent.reject(new SignalingError('等待对端时信令连接断开。', true));
        };

        this._socket.onerror = () => {};
    }

    _dispatch(message) {
        switch (message.type) {
            case 'peer-joined':
                this._peerPresent.resolve();
                break;

            case 'peer-left':
                this.onPeerLeft?.();
                break;

            case 'error': {
                const text = message.message ?? '服务器报告了一个错误。';
                // 等对端的调用方也要醒过来，否则会一直等到超时
                this._peerPresent.reject(new SignalingError(text));
                this.onError?.(text);
                break;
            }

            case 'signal':
                this._dispatchSignal(message.payload);
                break;
        }
    }

    _dispatchSignal(payload) {
        if (payload === null || typeof payload !== 'object') {
            return;
        }

        if (!this._signalsFlowing) {
            this._pendingSignals.push(payload);
            return;
        }

        this._deliverSignal(payload);
    }

    /** 服务器不解析 payload，所以这里要自己分辨是描述还是候选。 */
    _deliverSignal(payload) {
        if (typeof payload.sdp === 'string' && typeof payload.type === 'string') {
            this.onRemoteDescription?.({ sdp: payload.sdp, type: payload.type });
            return;
        }

        if (typeof payload.candidate === 'string') {
            this.onRemoteCandidate?.({
                candidate: payload.candidate,
                sdpMid: payload.mid ?? null,
            });
        }
    }

    _sendSignal(payload) {
        if (this._socket === null || this._socket.readyState !== WebSocket.OPEN) {
            return;
        }

        // WebSocket 本身保序，所以不需要 C# 侧那个出站队列：
        // 那个队列是为了给来自不同原生回调线程的消息定序，浏览器里没有这个问题。
        this._socket.send(JSON.stringify({ type: 'signal', payload }));
    }

    _buildUrl(path) {
        const base = this._origin.length > 0 ? this._origin : window.location.origin;
        const url = new URL(path, base.endsWith('/') ? base : base + '/');
        url.protocol = url.protocol === 'https:' ? 'wss:' : url.protocol === 'http:' ? 'ws:' : url.protocol;
        return url.toString();
    }
}

/** 可以在外部 resolve/reject 的 Promise。锁存语义：resolve 过就永远是已完成。 */
function createLatch() {
    let resolve;
    let reject;
    const promise = new Promise((res, rej) => {
        resolve = res;
        reject = rej;
    });

    // 未处理的 rejection 会在控制台刷警告 —— 这个 Promise 常常没人 await
    promise.catch(() => {});

    return { promise, resolve, reject };
}

function readIceServers(message) {
    if (!Array.isArray(message.iceServers)) {
        return [];
    }

    // 直接交给 RTCPeerConnection：字段名（urls/username/credential）
    // 在服务端就是按 RTCIceServer 对齐的
    return message.iceServers.filter(entry => entry !== null && Array.isArray(entry.urls));
}

function throwMissingCode() {
    throw new SignalingError('服务器没有返回文件码。');
}

function throwIfAborted(signal) {
    if (signal?.aborted === true) {
        throw new DOMException('已取消。', 'AbortError');
    }
}

/**
 * 给一个 Promise 接上取消信号：信号触发时立刻 reject，并跑一次清理
 * （这里通常是关掉正在建立的 socket）。
 *
 * 不能只是 Promise.race 完事 —— 被落选的那个 Promise 仍然在跑，
 * 它的 onopen/onmessage 迟早会触发，往一个已经没人管的 socket 上
 * 挂处理器，是内存泄漏和幽灵状态的来源。
 */
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

/** 路径拼接时的房间段，与 core/codes.js 保持一致。 */
export const SIGNALING_CREATE_PATH = '/signal/create';
