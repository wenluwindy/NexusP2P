// 速度统计与瓶颈判定。对应 C# 的 RateTracker / TransferSnapshot。
//
// 瓶颈说明是刻意的产品决定：用户看到 3 MB/s 时第一反应是「是不是坏了」，
// 应该直接告诉他为什么。所有判定都来自**实际指标**（候选对类型、
// bufferedAmount 趋势、速度），不是猜测。

import { CandidatePairKind } from '../net/peer.js';
import { formatSize } from '../storage/capabilities.js';

/** 滑动窗口测速。窗口太短会剧烈跳动，太长则对变化反应迟钝。 */
const WINDOW_MS = 3000;

export class RateTracker {
    constructor() {
        this._samples = [];
    }

    record(completedBytes, now = performance.now()) {
        this._samples.push({ at: now, bytes: completedBytes });

        const cutoff = now - WINDOW_MS;
        while (this._samples.length > 2 && this._samples[0].at < cutoff) {
            this._samples.shift();
        }
    }

    /** 字节/秒。样本不足时返回 0 而不是一个假的瞬时值。 */
    bytesPerSecond(now = performance.now()) {
        if (this._samples.length < 2) {
            return 0;
        }

        const first = this._samples[0];
        const last = this._samples[this._samples.length - 1];
        const elapsed = (last.at - first.at) / 1000;

        if (elapsed <= 0) {
            return 0;
        }

        return Math.max(0, (last.bytes - first.bytes) / elapsed);
    }
}

export const Bottleneck = {
    Hashing: 'hashing',
    Relay: 'relay',
    PeerBackpressure: 'peerBackpressure',
    LocalUplink: 'localUplink',
    None: 'none',
    Unknown: 'unknown',
};

/**
 * 判定当前瓶颈。
 *
 * 输入全是实测量，判定逻辑是纯函数 —— 这样它可以被直接测试
 * （给定指标组合 → 期望结论），而不需要跑一次真实传输。
 *
 * @param phase 'hashing' | 'transferring'
 * @param candidateKind 候选对类型
 * @param bufferedAmount 当前发送缓冲字节数
 * @param highWaterMark 背压阈值
 * @param bytesPerSecond 当前速度
 */
export function detectBottleneck({
    phase,
    candidateKind,
    bufferedAmount = 0,
    highWaterMark = 4 * 1024 * 1024,
    bytesPerSecond = 0,
}) {
    if (phase === 'hashing') {
        return Bottleneck.Hashing;
    }

    // 走中继时速度受服务器上行限制。这个判定优先于其他 ——
    // 它是唯一一个「换个网络环境就能解决」的原因，用户最需要知道。
    if (candidateKind === CandidatePairKind.Relay) {
        return Bottleneck.Relay;
    }

    // 缓冲长期贴着高水位：对端消费不过来（下行满、或磁盘写不过来）。
    // 发送侧只能观察到「我的缓冲排不空」这一个现象，所以说成「对方处理不过来」
    // 而不是编一个更具体的原因。
    if (bufferedAmount >= highWaterMark * 0.75) {
        return Bottleneck.PeerBackpressure;
    }

    // 缓冲基本是空的，说明我们塞得进去多少就走掉多少 —— 瓶颈在本机上行。
    if (bufferedAmount < highWaterMark * 0.05 && bytesPerSecond > 0) {
        return Bottleneck.LocalUplink;
    }

    if (bytesPerSecond > 0) {
        return Bottleneck.None;
    }

    return Bottleneck.Unknown;
}

/** 把判定说成人话。 */
export function describeBottleneck(bottleneck, candidateKind) {
    switch (bottleneck) {
        case Bottleneck.Hashing:
            return '正在计算校验和（这一步只用本机 CPU，还没开始传）';

        case Bottleneck.Relay:
            return '走中继中 —— 速度受中继服务器上行带宽限制，不是你的网络问题';

        case Bottleneck.PeerBackpressure:
            return '对方处理不过来（下行带宽或磁盘写入已满），正在等它消费';

        case Bottleneck.LocalUplink:
            return candidateKind === CandidatePairKind.Host
                ? '本机上行已满（局域网直连，这基本就是网卡或磁盘的上限了）'
                : '本机上行已满 —— 这是当前链路能达到的速度';

        case Bottleneck.None:
            return '传输中';

        default:
            return '正在建立连接';
    }
}

/** 剩余时间估算。速度为 0 时返回 null 而不是 Infinity。 */
export function estimateRemaining(completedBytes, totalBytes, bytesPerSecond) {
    if (bytesPerSecond <= 0 || completedBytes >= totalBytes) {
        return null;
    }

    return (totalBytes - completedBytes) / bytesPerSecond;
}

export function formatDuration(seconds) {
    if (seconds === null || !Number.isFinite(seconds)) {
        return '--';
    }

    if (seconds < 60) {
        return `${Math.ceil(seconds)} 秒`;
    }

    if (seconds < 3600) {
        return `${Math.floor(seconds / 60)} 分 ${Math.round(seconds % 60)} 秒`;
    }

    return `${Math.floor(seconds / 3600)} 小时 ${Math.round((seconds % 3600) / 60)} 分`;
}

export function formatSpeed(bytesPerSecond) {
    return `${formatSize(bytesPerSecond)}/s`;
}

export { formatSize };
