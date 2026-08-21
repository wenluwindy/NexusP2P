// 网页端入口：把 UI 与传输逻辑接起来。
//
// 这一层只负责「用户点了什么 → 调哪个流程 → 把状态显示出来」。
// 协议与加密全在 core/ 与 transfer/ 里，与界面无关。

import { formatCode, parseCode, parseShareLink, readShareLinkFromLocation }
    from './core/codes.js';
import { generateSecret } from './core/crypto.js';
import { TransferManifest } from './core/manifest.js';
import { TransferErrorCode } from './core/messages.js';
import { answer, offer } from './net/connector.js';
import { FanOutLinkState, offerMany } from './net/fanout-connector.js';
import { describeCandidateKind } from './net/peer.js';
import { ProtocolConnection } from './transfer/connection.js';
import { collectDroppedFiles } from './transfer/manifest-builder.js';
import { ReceiveSession } from './transfer/receive-session.js';
import { SendSession } from './transfer/send-session.js';
import {
    Bottleneck,
    RateTracker,
    describeBottleneck,
    detectBottleneck,
    estimateRemaining,
    formatDuration,
    formatSize,
    formatSpeed,
} from './transfer/progress.js';
import {
    chooseStrategy,
    describeStrategy,
    detectCapabilities,
    limitAdvice,
} from './storage/capabilities.js';
import { createWriter } from './storage/writers.js';
import * as ui from './ui/dom.js';

/** 信令服务器地址。同源部署时留空即可 —— 页面就是它托管的。 */
const SIGNALING_ORIGIN_KEY = 'nexusp2p.signalingOrigin';

const state = {
    files: [],
    manifest: null,
    secret: null,
    abort: null,
    link: null,
};

function signalingOrigin() {
    return localStorage.getItem(SIGNALING_ORIGIN_KEY) ?? '';
}

// ---------------- 发送 ----------------

/** 选中文件后先算清单。哈希在 Worker 里跑，主线程保持响应。 */
async function prepareFiles(files) {
    state.files = files;
    state.manifest = null;

    if (files.length === 0) {
        return;
    }

    ui.renderFileList(files);
    ui.setSendPhase('hashing');

    const tracker = new RateTracker();

    try {
        state.manifest = await computeManifest(files, progress => {
            tracker.record(progress.hashedBytes);
            ui.updateHashProgress(progress, tracker.bytesPerSecond());
        });

        ui.setSendPhase('ready');
        ui.setSendSummary(state.manifest);
    } catch (error) {
        ui.setSendPhase('idle');
        ui.notify(`计算校验和失败：${error.message}`, 'error');
    }
}

/**
 * 在 Worker 里算清单，拿回序列化字节后在主线程还原。
 *
 * Worker 不可用时退回主线程 —— 界面会卡，但功能不该因此消失。
 */
function computeManifest(files, onProgress) {
    return new Promise((resolve, reject) => {
        let worker;
        try {
            // 绝对路径：在 /r/<码> 这样的页面上，相对路径会解析到 /r/js/… 而 404
            worker = new Worker('/js/workers/hash.worker.js', { type: 'module' });
        } catch (error) {
            reject(new Error(`无法启动后台计算：${error.message}`));
            return;
        }

        worker.onmessage = async event => {
            const message = event.data;

            if (message.type === 'progress') {
                onProgress(message);
                return;
            }

            worker.terminate();

            if (message.type === 'error') {
                reject(new Error(message.message));
                return;
            }

            try {
                resolve(await TransferManifest.deserialize(message.serialized));
            } catch (error) {
                reject(error);
            }
        };

        worker.onerror = event => {
            worker.terminate();
            reject(new Error(event.message ?? '后台计算出错。'));
        };

        worker.postMessage({
            files,
            leafSize: 64 * 1024,
            pieceSize: 1024 * 1024,
        });
    });
}

/** 开始发送：建房 → 显示码 → 等对方 → 传。 */
async function startSending() {
    if (state.manifest === null) {
        ui.notify('请先选择要发送的文件。', 'error');
        return;
    }

    // 一对多（V2）走独立流程；1 人时下面的一对一路径与 V1 一字不差
    const maxPeers = ui.readMaxPeers();
    if (maxPeers > 1) {
        await startSendingMany(maxPeers);
        return;
    }

    state.secret = generateSecret();
    state.abort = new AbortController();

    const tracker = new RateTracker();
    let link = null;
    let candidateKind = null;

    ui.setSendPhase('waiting');

    try {
        link = await offer(signalingOrigin(), {
            onRoomCreated: room => {
                state.link = room;
                ui.showShareCode(room);
            },
            onPeerArrived: () => ui.setSendStatus('对方已进入，正在建立连接…'),
            signal: state.abort.signal,
        });

        candidateKind = await link.getCandidateKind();
        ui.setSendPhase('transferring');
        ui.setConnectionType(describeCandidateKind(candidateKind));

        const connection = new ProtocolConnection(link.channel);
        const session = new SendSession(state.manifest, state.files, state.secret);

        await session.run(connection, {
            signal: state.abort.signal,
            onProgress: progress => {
                tracker.record(progress.completedBytes);
                const speed = tracker.bytesPerSecond();

                ui.updateSendProgress({
                    ...progress,
                    speed,
                    remaining: estimateRemaining(
                        progress.completedBytes, progress.totalBytes, speed),
                    bottleneck: describeBottleneck(
                        detectBottleneck({
                            phase: 'transferring',
                            candidateKind,
                            bufferedAmount: progress.bufferedAmount,
                            bytesPerSecond: speed,
                        }),
                        candidateKind),
                });
            },
        });

        ui.setSendPhase('done');
        ui.notify('传输完成，对方已确认收齐并通过校验。', 'success');
    } catch (error) {
        if (isCancellation(error)) {
            ui.setSendPhase('idle');
            ui.notify('已取消。', 'info');
        } else {
            ui.setSendPhase('failed');
            ui.setSendStatus(`发送失败：${error.message}`);
            ui.notify(`发送失败：${error.message}`, 'error');
        }
    } finally {
        link?.close();
        state.abort = null;
    }
}

/** 用户主动点了取消，而不是真的出错 —— 界面上要分开对待。 */
function isCancellation(error) {
    return error.name === 'AbortError' || error.code === TransferErrorCode.Cancelled;
}

/**
 * 一对多发送（V2）。清单在 Worker 里已经算好（state.manifest），
 * 这里绝不重算 —— 每个接收方进来只是多开一条链路。
 *
 * 没有「自动结束」：发送方不知道还会不会有人来，守到用户点取消为止。
 * 点取消 = 停止接纳新接收方并取消在传链路，然后按各链路结果收尾。
 */
async function startSendingMany(maxPeers) {
    state.secret = generateSecret();
    state.abort = new AbortController();

    const tracker = new RateTracker();
    const snapshots = new Map();   // peerId → 最新快照

    ui.setSendPhase('waiting');

    const refresh = () => {
        const list = [...snapshots.values()]
            .sort((a, b) => a.peerId < b.peerId ? -1 : a.peerId > b.peerId ? 1 : 0);
        ui.renderReceiverList(list);

        // 整体进度 = 各链路已传字节之和 / 字节数×人数
        const total = state.manifest.totalLength * Math.max(1, list.length);
        const done = list.reduce((sum, s) => sum + (s.progress?.completedBytes ?? 0), 0);
        tracker.record(done);
        const speed = tracker.bytesPerSecond();
        const active = list.filter(s => s.state === FanOutLinkState.Running).length;

        ui.setSendStatus(list.length === 0
            ? '等待对方接收…'
            : `正在传输（${active} 人接收中，整体 ${(total > 0 ? done / total * 100 : 0).toFixed(0)}%）`);
        ui.updateSendProgress({
            completedBytes: done,
            totalBytes: total,
            speed,
            remaining: estimateRemaining(done, total, speed),
            bottleneck: '',
        });
    };

    try {
        const links = await offerMany(
            signalingOrigin(), state.manifest, state.files, state.secret, maxPeers, {
                onRoomCreated: room => {
                    state.link = room;
                    ui.showShareCode(room);

                    // 旧服务器不认识 maxReceivers（回显 1）：降级为一对一提醒用户
                    if (room.maxReceivers < maxPeers) {
                        ui.notify(
                            `信令服务器只支持 ${room.maxReceivers} 人接收，已按 ` +
                            `${room.maxReceivers} 人继续。`, 'info');
                    }
                },
                onLinkUpdate: snapshot => {
                    snapshots.set(snapshot.peerId, snapshot);
                    ui.setSendPhase('transferring');
                    refresh();
                },
                signal: state.abort.signal,
            });

        const all = [...links.values()];
        const completed = all.filter(s => s.state === FanOutLinkState.Completed).length;

        if (all.length > 0 && completed === all.length) {
            ui.setSendPhase('done');
            ui.notify(`传输完成，${completed} 个接收方都已确认收齐并通过校验。`, 'success');
        } else if (all.length === 0 || state.abort.signal.aborted) {
            ui.setSendPhase('idle');
            ui.notify(completed > 0
                ? `已停止。${completed}/${all.length} 个接收方收齐。`
                : '已取消。', 'info');
        } else {
            ui.setSendPhase('failed');
            ui.setSendStatus(`发送结束：${completed}/${all.length} 个接收方收齐。`);
            ui.notify(`有接收方没有收完（${completed}/${all.length} 收齐）。`, 'error');
        }
    } catch (error) {
        if (isCancellation(error)) {
            ui.setSendPhase('idle');
            ui.notify('已取消。', 'info');
        } else {
            ui.setSendPhase('failed');
            ui.setSendStatus(`发送失败：${error.message}`);
            ui.notify(`发送失败：${error.message}`, 'error');
        }
    } finally {
        state.abort = null;
    }
}

// ---------------- 接收 ----------------

/**
 * 从输入框解析出房间码。
 *
 * V3 起**不再需要密钥** —— 它由发送方在数据通道里推来。
 * 分享链接（含带 `#密钥` 的旧链接）与九位码都接受。
 */
function resolveReceiveTarget(input) {
    const fromLink = parseShareLink(input);
    if (fromLink !== null) {
        return fromLink;
    }

    const code = parseCode(input);
    if (code === null) {
        return { error: '这不是合法的分享链接，也不是九位文件码。' };
    }

    return { code };
}

async function startReceiving(input) {
    const target = resolveReceiveTarget(input);
    if (target.error !== undefined) {
        ui.notify(target.error, 'error');
        return;
    }

    state.abort = new AbortController();
    const tracker = new RateTracker();
    let link = null;
    let candidateKind = null;

    ui.setReceivePhase('connecting');
    ui.setReceiveStatus(`正在连接（文件码 ${formatCode(target.code)}）…`);

    try {
        link = await answer(signalingOrigin(), target.code, state.abort.signal);

        candidateKind = await link.getCandidateKind();
        ui.setConnectionType(describeCandidateKind(candidateKind), 'receive');
        ui.setReceiveStatus('已连接，正在接收清单…');

        const connection = new ProtocolConnection(link.channel);

        const session = new ReceiveSession(manifest => openWriterFor(manifest));

        const result = await session.run(connection, {
            signal: state.abort.signal,
            onManifest: manifest => {
                ui.setReceivePhase('transferring');
                ui.setReceiveSummary(manifest);
            },
            onProgress: progress => {
                tracker.record(progress.completedBytes);
                const speed = tracker.bytesPerSecond();

                ui.updateReceiveProgress({
                    ...progress,
                    speed,
                    remaining: estimateRemaining(
                        progress.completedBytes, progress.totalBytes, speed),
                    bottleneck: describeBottleneck(
                        detectBottleneck({
                            phase: 'transferring',
                            candidateKind,
                            bytesPerSecond: speed,
                        }),
                        candidateKind),
                });
            },
        });

        ui.setReceivePhase('done');
        ui.showDownloads(result);
        ui.notify('接收完成，全部内容已通过校验。', 'success');
    } catch (error) {
        if (isCancellation(error)) {
            ui.setReceivePhase('idle');
            ui.notify('已取消。', 'info');
        } else {
            ui.setReceivePhase('failed');
            ui.setReceiveStatus(`接收失败：${error.message}`);
            ui.notify(`接收失败：${error.message}`, 'error');
        }
    } finally {
        link?.close();
        state.abort = null;
    }
}

/**
 * 收到清单后才选落盘策略 —— 这时才知道文件数与总大小。
 *
 * 超限时如实告知并引导用桌面版，但**不阻止**用户继续（AD-6：
 * 能力差异要诚实呈现，而不是把人挡在门外）。
 */
async function openWriterFor(manifest) {
    const capabilities = detectCapabilities();
    const strategy = chooseStrategy(capabilities, {
        fileCount: manifest.entries.length,
        totalBytes: manifest.totalLength,
    });

    const description = describeStrategy(strategy, manifest.totalLength);
    ui.setStorageStrategy(description, strategy);

    if (!description.withinLimit) {
        const advice = limitAdvice(strategy, manifest.totalLength);
        ui.notify(advice, 'error');

        if (!window.confirm(`${advice}\n\n仍然继续吗？`)) {
            const error = new Error('用户选择不继续。');
            error.name = 'AbortError';
            throw error;
        }
    }

    return createWriter(strategy, manifest);
}

// ---------------- 启动 ----------------

function init() {
    ui.bind({
        onFilesSelected: prepareFiles,
        onDropped: async dataTransfer => prepareFiles(await collectDroppedFiles(dataTransfer)),
        onStartSend: startSending,
        onStartReceive: startReceiving,
        onCancel: () => state.abort?.abort(),
        signalingOrigin: signalingOrigin(),
        onSaveSignaling: origin => {
            if (origin.length === 0) {
                localStorage.removeItem(SIGNALING_ORIGIN_KEY);
            } else {
                localStorage.setItem(SIGNALING_ORIGIN_KEY, origin.replace(/\/+$/, ''));
            }

            ui.notify('已保存。', 'success');
        },
    });

    ui.showCapabilities(detectCapabilities());

    // 点开分享链接进来的：直接把码和密钥填好，切到接收页
    const fromLink = readShareLinkFromLocation();
    if (fromLink !== null) {
        ui.prefillReceive(fromLink);
    }
}

init();
