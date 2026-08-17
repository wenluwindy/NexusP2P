// 一对多发送的编排（V2）。对应 C# 的 FanOutSender + SendFanOut。
//
// 每个接收方一条独立链路 = 独立 RTCPeerConnection + 独立 SendSession（AD-11）。
// 链路之间唯一共享的是清单（Worker 里只算一次）与密钥。网页端**没有**
// CipherPieceCache —— AD-13 是优化不是正确性；浏览器把加密交给
// SubtleCrypto，多算几遍不值得为此引入缓存的复杂度。
//
// 每条链路的背压是它自己的 bufferedamountlow（AD-14），无轮询、互不影响。

import { DataChannel } from './peer.js';
import { FanOutSignalingClient } from './fanout-signaling.js';
import { ProtocolConnection } from '../transfer/connection.js';
import { SendSession } from '../transfer/send-session.js';

/** 一条链路的状态。 */
export const FanOutLinkState = {
    Running: 'running',
    Completed: 'completed',
    Failed: 'failed',
};

/**
 * 建房并持续接纳接收方，为每个进来的接收方开一条独立发送链路。
 *
 * @param signalingOrigin 信令服务器基址
 * @param manifest 已算好的清单（Worker 里只算一次 —— 这里绝不重算）
 * @param files 与清单对应的 File 列表
 * @param secret 本次传输的根密钥材料
 * @param maxReceivers 想要的席位数（服务器可能夹小；旧服务器回显 1 = 降级一对一）
 * @param onRoomCreated (room: FanOutRoomCreated) => void — 拿到码时立刻回调
 * @param onLinkUpdate (snapshot) => void — 任一链路的状态/进度变了
 *   快照形状：{ peerId, state, progress: {completedBytes,totalBytes}|null, error|null }
 * @param signal AbortController.signal — 停止接纳并取消所有在传链路
 * @returns 所有已开链路结束后 resolve，返回 Map<peerId, snapshot>
 */
export async function offerMany(
    signalingOrigin, manifest, files, secret, maxReceivers,
    { onRoomCreated, onLinkUpdate, signal } = {}) {
    const signaling = new FanOutSignalingClient(signalingOrigin);
    const links = new Map();      // peerId → snapshot
    const linkTasks = [];

    const publish = snapshot => {
        links.set(snapshot.peerId, snapshot);
        onLinkUpdate?.(snapshot);
    };

    try {
        const room = await signaling.createRoom(maxReceivers, signal);
        onRoomCreated?.(room);

        // 接纳循环：每来一个接收方开一条链路。abort 或信令关闭时退出。
        while (true) {
            let peerId;
            try {
                peerId = await signaling.waitForReceiver(signal);
            } catch (error) {
                if (error.name === 'AbortError') {
                    break;
                }

                throw error;
            }

            if (peerId === null) {
                break;   // 信令连接关闭
            }

            // 每条链路独立跑；失败进快照不炸掉编排（AD-11）
            linkTasks.push(runLink(signaling, peerId, room.iceServers,
                manifest, files, secret, publish, signal));
        }
    } finally {
        // 停止接纳后等所有已开链路自然结束，再关信令
        await Promise.allSettled(linkTasks);
        signaling.close();
    }

    return links;
}

/** 为一个接收方建 RTCPeerConnection + 数据通道并跑完一次发送。 */
async function runLink(signaling, peerId, iceServers, manifest, files, secret, publish, signal) {
    publish({ peerId, state: FanOutLinkState.Running, progress: null, error: null });

    let peer = null;

    try {
        peer = new RTCPeerConnection({
            iceServers: iceServers.length > 0 ? iceServers : [],
            iceCandidatePoolSize: 1,
        });

        peer.onicecandidate = event => {
            if (event.candidate !== null) {
                signaling.sendCandidate(peerId, event.candidate);
            }
        };

        // 两个处理器都挂好了才开闸（V1 教训：窗口期的信令不能丢）
        signaling.beginSignalDelivery(
            peerId,
            async description => {
                try {
                    await peer.setRemoteDescription(description);
                } catch {
                    // 描述应用失败会让建连超时，那条路径上有明确的错误信息
                }
            },
            async candidate => {
                try {
                    await peer.addIceCandidate(candidate);
                } catch {
                    // 候选来晚了会失败；ICE 能靠其他候选恢复
                }
            });

        // 发送方建通道并显式发 offer（与一对一的 offer() 同理）
        const raw = peer.createDataChannel('bulk', { ordered: true });
        await peer.setLocalDescription(await peer.createOffer());
        signaling.sendDescription(peerId, peer.localDescription);

        await DataChannel.waitForOpen(raw, signal);

        const connection = new ProtocolConnection(new DataChannel(raw));
        const session = new SendSession(manifest, files, secret);

        await session.run(connection, {
            signal,
            onProgress: progress => publish({
                peerId,
                state: FanOutLinkState.Running,
                progress,
                error: null,
            }),
        });

        publish({
            peerId,
            state: FanOutLinkState.Completed,
            progress: {
                completedBytes: manifest.totalLength,
                totalBytes: manifest.totalLength,
            },
            error: null,
        });
    } catch (error) {
        // 一条链路的失败不影响其他链路（AD-11）
        publish({ peerId, state: FanOutLinkState.Failed, progress: null, error });
    } finally {
        signaling.forgetPeer(peerId);
        peer?.close();
    }
}
