// RTCPeerConnection 封装。对应 C# 的 WebRtcPeerConnection + WebRtcDataChannel。
//
// 角色分工与 C# 侧一致，不能弄反：
//   发送方 = offerer，等对端进房后 createDataChannel 并生成 offer
//   接收方 = answerer，等 ondatachannel
//
// 通道默认就是有序可靠的（ordered: true、无 maxRetransmits），这正是协议层
// 「一条逻辑消息的帧连续到达」这个不变式的依据 —— 千万不要为了「快一点」
// 去开 unordered。

import { SAFE_MAX_MESSAGE_SIZE } from '../core/frame.js';

/** 建连超时。ICE 打不通时不会有任何回调，没有超时就是永久挂起。 */
const CONNECT_TIMEOUT_MS = 30_000;

export const CandidatePairKind = {
    Unknown: 'unknown',
    Host: 'host',
    ServerReflexive: 'srflx',
    Relay: 'relay',
};

/** 把候选类型说成人话。这就是「速度瓶颈说明」的一部分。 */
export function describeCandidateKind(kind) {
    switch (kind) {
        case CandidatePairKind.Host:
            return '同局域网直连';
        case CandidatePairKind.ServerReflexive:
            return '打洞成功，公网直连';
        case CandidatePairKind.Relay:
            return '经服务器中继，速度受服务器上行带宽限制';
        default:
            return '连接类型未知';
    }
}

export class PeerConnectionClosedError extends Error {
    constructor(message) {
        super(message);
        this.name = 'PeerConnectionClosedError';
    }
}

/** 数据通道的一层薄封装：把事件式接口包成可 await 的收发。 */
export class DataChannel {
    constructor(channel) {
        this._channel = channel;
        this._channel.binaryType = 'arraybuffer';

        this._inbound = [];
        this._waiters = [];
        this._closedReason = null;

        // 低水位阈值：背压靠 bufferedamountlow 事件，不轮询。
        // 轮询在 spike 里被证明会吃掉大半吞吐。
        this._channel.bufferedAmountLowThreshold = 0;
        this._drainWaiters = [];

        this._channel.onmessage = event => this._onMessage(event.data);
        this._channel.onbufferedamountlow = () => this._pulseDrain();
        this._channel.onclose = () => this._onClosed('通道被对端关闭。');
        this._channel.onerror = event => this._onClosed(
            `通道错误：${event.error?.message ?? '未知原因'}`);
    }

    get maxMessageSize() {
        return SAFE_MAX_MESSAGE_SIZE;
    }

    get bufferedAmount() {
        return this._channel.bufferedAmount;
    }

    get isOpen() {
        return this._channel.readyState === 'open';
    }

    /** 等通道打开。必须有超时 —— 见文件头。 */
    static waitForOpen(channel, signal) {
        if (channel.readyState === 'open') {
            return Promise.resolve();
        }

        return new Promise((resolve, reject) => {
            const timer = setTimeout(
                () => reject(new PeerConnectionClosedError(
                    `等待数据通道打开超过 ${CONNECT_TIMEOUT_MS / 1000} 秒。可能是 ICE 打洞失败。`)),
                CONNECT_TIMEOUT_MS);

            channel.addEventListener('open', () => {
                clearTimeout(timer);
                resolve();
            }, { once: true });

            channel.addEventListener('close', () => {
                clearTimeout(timer);
                reject(new PeerConnectionClosedError('等待打开期间通道关闭。'));
            }, { once: true });

            signal?.addEventListener('abort', () => {
                clearTimeout(timer);
                reject(new DOMException('已取消。', 'AbortError'));
            }, { once: true });
        });
    }

    send(bytes) {
        if (!this.isOpen) {
            throw new PeerConnectionClosedError(`通道当前状态为 ${this._channel.readyState}。`);
        }

        if (bytes.length > this.maxMessageSize) {
            throw new Error(`消息 ${bytes.length} 字节超过上限 ${this.maxMessageSize} 字节。`);
        }

        this._channel.send(bytes);
    }

    /**
     * 收下一条消息（原始帧字节）。通道关闭且队列已空时抛错。
     *
     * @param signal 取消信号。已经排队等待时被触发也能立刻唤醒 ——
     *   否则「点取消」在网络空闲期间会看起来什么都没发生。
     */
    receive(signal) {
        if (this._inbound.length > 0) {
            return Promise.resolve(this._inbound.shift());
        }

        if (this._closedReason !== null) {
            return Promise.reject(new PeerConnectionClosedError(this._closedReason));
        }

        if (signal?.aborted === true) {
            return Promise.reject(new DOMException('已取消。', 'AbortError'));
        }

        return new Promise((resolve, reject) => {
            const waiter = { resolve, reject };
            this._waiters.push(waiter);

            signal?.addEventListener('abort', () => {
                const index = this._waiters.indexOf(waiter);
                if (index !== -1) {
                    this._waiters.splice(index, 1);
                    reject(new DOMException('已取消。', 'AbortError'));
                }
            }, { once: true });
        });
    }

    /** 等缓冲降到阈值以下。 */
    async waitForDrain(threshold) {
        this._channel.bufferedAmountLowThreshold = threshold;

        while (this._channel.bufferedAmount > threshold) {
            if (!this.isOpen) {
                throw new PeerConnectionClosedError('等待排空期间通道关闭。');
            }

            // 兜一个超时：万一低水位事件因为某种原因没来，退化为低频轮询
            // 而不是永久挂死。C# 侧踩过这个坑（catch 写在循环外导致背压失效）。
            await new Promise(resolve => {
                const timer = setTimeout(resolve, 200);
                this._drainWaiters.push(() => {
                    clearTimeout(timer);
                    resolve();
                });
            });
        }
    }

    close(reason) {
        this._onClosed(reason ?? '本地关闭。');

        if (this._channel.readyState === 'open' || this._channel.readyState === 'connecting') {
            this._channel.close();
        }
    }

    _onMessage(data) {
        const bytes = new Uint8Array(data);

        // 有人在等就直接交给它，否则入队。
        // 队列是必须的：对 answerer 来说对端一看到通道打开就立刻发清单，
        // 而上层要等 await 才来取 —— 丢掉的话两端会一起干等。
        const waiter = this._waiters.shift();
        if (waiter !== undefined) {
            waiter.resolve(bytes);
            return;
        }

        this._inbound.push(bytes);
    }

    _onClosed(reason) {
        if (this._closedReason !== null) {
            return;
        }

        this._closedReason = reason;
        this._pulseDrain();

        const waiters = this._waiters;
        this._waiters = [];
        for (const waiter of waiters) {
            waiter.reject(new PeerConnectionClosedError(reason));
        }
    }

    _pulseDrain() {
        const waiters = this._drainWaiters;
        this._drainWaiters = [];
        for (const wake of waiters) {
            wake();
        }
    }
}
