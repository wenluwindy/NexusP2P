// 把信令与 WebRTC 接起来，产出一条可用的数据通道。
// 对应 C# 的 PeerConnector。

import { CandidatePairKind, DataChannel, PeerConnectionClosedError } from './peer.js';
import { SignalingClient } from './signaling.js';

const CONNECT_TIMEOUT_MS = 60_000;

/** 一条建好的对等连接，以及建立它时得到的信息。 */
export class PeerLink {
    constructor(signaling, peer, channel, code, shareUrlBase) {
        this._signaling = signaling;
        this._peer = peer;
        this.channel = channel;
        this.code = code;
        this.shareUrlBase = shareUrlBase;
    }

    /**
     * 当前走的是直连还是中继。「瓶颈说明」要用。
     *
     * 浏览器里只能通过 getStats 拿到，而且是异步的 —— 与 C# 侧那个同步属性不同。
     */
    async getCandidateKind() {
        try {
            const stats = await this._peer.getStats();
            let selected = null;

            for (const report of stats.values()) {
                if (report.type === 'candidate-pair' && report.state === 'succeeded' &&
                    (report.selected === true || report.nominated === true)) {
                    selected = report;
                    break;
                }
            }

            if (selected === null) {
                return CandidatePairKind.Unknown;
            }

            const local = stats.get(selected.localCandidateId);
            const remote = stats.get(selected.remoteCandidateId);
            return classifyPair(local?.candidateType, remote?.candidateType);
        } catch {
            return CandidatePairKind.Unknown;
        }
    }

    close() {
        this.channel.close('传输结束');
        this._peer.close();
        this._signaling.close();
    }
}

/** 只要有一端是 relay，整条路径就是中继。 */
function classifyPair(local, remote) {
    if (local === 'relay' || remote === 'relay') {
        return CandidatePairKind.Relay;
    }

    if (local === 'srflx' || remote === 'srflx' || local === 'prflx' || remote === 'prflx') {
        return CandidatePairKind.ServerReflexive;
    }

    if (local === 'host' && remote === 'host') {
        return CandidatePairKind.Host;
    }

    return CandidatePairKind.Unknown;
}

/**
 * 建房并等对方进来，返回连好的通道。**发送方用这个。**
 *
 * @param onRoomCreated 房间建好、拿到文件码时立刻回调 —— UI 要马上把码显示出来，
 *   而不是等对方进来之后。
 * @param onPeerArrived 对方进房、开始打洞时回调。这两个阶段的等待性质完全不同：
 *   「还没人来」可以等几小时，「正在打洞」超过十几秒就说明多半连不上了。
 * @param password 可选进房口令。不传/为空 = 不设口令，行为与从前完全一致。
 */
export async function offer(signalingOrigin, { onRoomCreated, onPeerArrived, signal, password } = {}) {
    const signaling = new SignalingClient(signalingOrigin);
    let peer = null;

    try {
        const room = await signaling.createRoom(signal, { password });
        onRoomCreated?.(room);

        peer = createPeer(room.iceServers);
        wireSignaling(signaling, peer);

        // 等对方进房再开始协商：过早生成 offer，信令服务器没有对端可转发，
        // 那条 offer 就白丢了。这一等常常是几分钟到几小时，取消必须能打断它。
        await signaling.waitForPeer(signal);
        onPeerArrived?.();

        // 发送方建通道并显式触发协商。显式发 offer 而不是靠
        // onnegotiationneeded —— 那个事件在 setLocalDescription 之前就触发，
        // 那时 localDescription 还是 null。
        const raw = peer.createDataChannel('bulk', { ordered: true });
        await peer.setLocalDescription(await peer.createOffer());
        signaling.sendDescription(peer.localDescription);

        await DataChannel.waitForOpen(raw, signal);
        return new PeerLink(signaling, peer, new DataChannel(raw), room.code, room.shareUrlBase);
    } catch (error) {
        peer?.close();
        signaling.close();
        throw error;
    }
}

/** 用文件码进房并等对方建通道过来。**接收方用这个。** password 为发送方设置的口令（可选）。 */
export async function answer(signalingOrigin, code, signal, password) {
    const signaling = new SignalingClient(signalingOrigin);
    let peer = null;

    try {
        const iceServers = await signaling.joinRoom(code, false, signal, password);

        peer = createPeer(iceServers);

        // ondatachannel 必须在挂信令处理器**之前**就绪：对端一看到通道打开
        // 就立刻发清单，晚一步订阅就会丢掉第一条消息。
        const incoming = waitForIncomingChannel(peer, signal);

        wireSignaling(signaling, peer);

        const raw = await incoming;
        await DataChannel.waitForOpen(raw, signal);
        return new PeerLink(signaling, peer, new DataChannel(raw), code, null);
    } catch (error) {
        peer?.close();
        signaling.close();
        throw error;
    }
}

function createPeer(iceServers) {
    return new RTCPeerConnection({
        iceServers: iceServers.length > 0 ? iceServers : [],
        // V2: 增加候选池大小以改善 NAT 穿透成功率，特别是在多接收方场景
        iceCandidatePoolSize: 4,
        // 积极的 ICE 传输策略：尽快收集所有候选
        iceTransportPolicy: 'all',
        // 启用 ICE 重启功能
        bundlePolicy: 'max-bundle',
        rtcpMuxPolicy: 'require',
    });
}

function waitForIncomingChannel(peer, signal) {
    return new Promise((resolve, reject) => {
        const timer = setTimeout(
            () => reject(new PeerConnectionClosedError(
                `等待对端建立数据通道超过 ${CONNECT_TIMEOUT_MS / 1000} 秒。可能是 ICE 打洞失败。`)),
            CONNECT_TIMEOUT_MS);

        peer.ondatachannel = event => {
            clearTimeout(timer);
            resolve(event.channel);
        };

        peer.addEventListener('connectionstatechange', () => {
            if (peer.connectionState === 'failed' || peer.connectionState === 'closed') {
                clearTimeout(timer);
                reject(new PeerConnectionClosedError(`连接进入 ${peer.connectionState} 状态。`));
            }
        });

        signal?.addEventListener('abort', () => {
            clearTimeout(timer);
            reject(new DOMException('已取消。', 'AbortError'));
        }, { once: true });
    });
}

/**
 * 挂上信令与 WebRTC 的双向桥。
 *
 * 最后一步 beginSignalDelivery() 不能省 —— 它把「挂处理器之前」窗口期
 * 攒下的信令按原顺序补发出去。C# 侧踩过这个坑：接收端建 WebRTC 对象要
 * 几百毫秒，这期间到达的 offer 会投给一个空处理器然后永久消失。
 */
function wireSignaling(signaling, peer) {
    peer.onicecandidate = event => {
        if (event.candidate !== null) {
            signaling.sendCandidate(event.candidate);
        }
    };

    // V2: 监控 ICE 收集状态，帮助诊断连接问题
    peer.onicegatheringstatechange = () => {
        if (peer.iceGatheringState === 'complete') {
            console.log('[connector] ICE 候选收集完成');
        }
    };

    peer.oniceconnectionstatechange = () => {
        console.log(`[connector] ICE 连接状态: ${peer.iceConnectionState}`);
        if (peer.iceConnectionState === 'failed') {
            console.error('[connector] ICE 连接失败，可能是 NAT 穿透失败');
        }
    };

    peer.onconnectionstatechange = () => {
        console.log(`[connector] 连接状态: ${peer.connectionState}`);
    };

    signaling.onRemoteDescription = async description => {
        try {
            await peer.setRemoteDescription(description);

            // 收到 offer 后要生成 answer
            if (description.type === 'offer') {
                await peer.setLocalDescription(await peer.createAnswer());
                signaling.sendDescription(peer.localDescription);
            }
        } catch {
            // 描述应用失败会让建连超时，那条路径上有明确的错误信息
        }
    };

    signaling.onRemoteCandidate = async candidate => {
        try {
            await peer.addIceCandidate(candidate);
        } catch {
            // 候选来晚了（描述还没设好）会失败。ICE 本身能靠其他候选恢复，
            // 不值得为此中断整条连接。
        }
    };

    // 刻意不挂 onnegotiationneeded：offer 由 offer() 显式生成并发出，
    // answer 在收到 offer 时生成。本协议一次连接只协商一次，
    // 让自动协商插一脚只会造成两端同时发 offer 的竞态。

    signaling.beginSignalDelivery();
}
