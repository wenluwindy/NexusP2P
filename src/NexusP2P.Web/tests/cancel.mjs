// 取消按钮的回归测试。
//
// 背景：AbortSignal 曾经只在两步之间的空隙被检查（throwIfAborted），
// 而实际的网络等待——WebSocket 握手、等对端进房、等 DataChannel 打开、
// 等下一条消息——都不知道 signal 存在。结果是点「取消」在等待阶段
// 什么都不会发生，界面卡死到用户以为软件坏了。
//
// 这里直接测两层：
//   1. DataChannel.receive(signal) —— 排队等待时 abort 能立刻唤醒
//   2. SendSession/ReceiveSession 在「等对端消息」阶段 abort 能让 run() 立刻退出
//
// 跑：node src/NexusP2P.Web/tests/cancel.mjs

import { DataChannel } from '../wwwroot/js/net/peer.js';
import { ProtocolConnection } from '../wwwroot/js/transfer/connection.js';
import { ReceiveSession } from '../wwwroot/js/transfer/receive-session.js';
import { SendSession } from '../wwwroot/js/transfer/send-session.js';
import { buildManifest } from '../wwwroot/js/transfer/manifest-builder.js';
import { generateSecret } from '../wwwroot/js/core/crypto.js';
import { SAFE_MAX_MESSAGE_SIZE } from '../wwwroot/js/core/frame.js';

let failures = 0;

function check(condition, description) {
    if (condition) {
        console.log(`  ✓ ${description}`);
    } else {
        console.log(`  ✗ ${description}`);
        failures++;
    }
}

/** 一个永远不会自己产出消息的假 RTCDataChannel —— 逼真实现「一直在等」的场景。 */
function makeSilentRawChannel() {
    return {
        binaryType: 'arraybuffer',
        readyState: 'open',
        bufferedAmount: 0,
        bufferedAmountLowThreshold: 0,
        onmessage: null,
        onbufferedamountlow: null,
        onclose: null,
        onerror: null,
        close() {},
    };
}

/** 只挂对端、从不主动发消息的 LoopbackChannel 变体，配合真实取消信号使用。 */
class HangingChannel {
    constructor() {
        this.maxMessageSize = SAFE_MAX_MESSAGE_SIZE;
        this.bufferedAmount = 0;
        this.isOpen = true;
        this._waiters = [];
    }

    send() {
        // 故意什么都不做：模拟对端永远不回应
    }

    receive(signal) {
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

    async waitForDrain() {}

    close() {
        this.isOpen = false;
    }
}

function fakeFile(name, length) {
    const data = new Uint8Array(length);
    for (let i = 0; i < length; i++) {
        data[i] = i % 251;
    }

    return new File([data], name);
}

console.log('DataChannel.receive(signal)：排队等待时 abort 能立刻唤醒');
{
    const channel = new DataChannel(makeSilentRawChannel());
    const controller = new AbortController();

    const pending = channel.receive(controller.signal);
    controller.abort();

    const outcome = await pending.then(() => ({ rejected: false }), error => ({ rejected: true, error }));

    check(outcome.rejected, 'receive() 被 reject 而不是继续挂着');
    check(outcome.error?.name === 'AbortError', `拒绝原因是 AbortError（实际 ${outcome.error?.name}）`);
}

console.log('DataChannel.receive(signal)：调用前 signal 已经是 aborted 状态');
{
    const channel = new DataChannel(makeSilentRawChannel());
    const controller = new AbortController();
    controller.abort();

    const outcome = await channel.receive(controller.signal)
        .then(() => ({ rejected: false }), error => ({ rejected: true, error }));

    check(outcome.rejected, '已经取消的 signal 立刻让 receive() 失败');
    check(outcome.error?.name === 'AbortError', `拒绝原因是 AbortError（实际 ${outcome.error?.name}）`);
}

console.log('SendSession：等待对端 Bitfield 时取消，run() 立刻退出');
{
    const files = [fakeFile('a.bin', 5000)];
    const manifest = await buildManifest(files, {});
    const secret = generateSecret();

    const channel = new HangingChannel();
    const connection = new ProtocolConnection(channel);
    const controller = new AbortController();

    const run = new SendSession(manifest, files, secret).run(connection, { signal: controller.signal });

    // 给它一点时间真正卡在「等消息」那一步，而不是还没跑到那里
    await new Promise(resolve => setTimeout(resolve, 20));
    controller.abort();

    const outcome = await Promise.race([
        run.then(() => ({ settled: 'resolved' }), error => ({ settled: 'rejected', error })),
        new Promise(resolve => setTimeout(() => resolve({ settled: 'timeout' }), 2000)),
    ]);

    check(outcome.settled === 'rejected', `run() 在取消后退出而不是继续挂着（实际 ${outcome.settled}）`);

    // 取消可能以两种形状之一出现：ProtocolConnection.receive 直接拒绝时是原始
    // AbortError；throwIfAborted 在步骤之间捕获时是 TransferFailedError(Cancelled)。
    // app.js 的 isCancellation() 两种都认，这里同样两种都接受 —— 关键是
    // 「确实被识别为取消」，不是具体走哪条内部路径。
    const isRecognizedCancellation =
        outcome.error?.name === 'AbortError' ||
        (outcome.error?.name === 'TransferFailedError' && outcome.error?.code === 6);
    check(isRecognizedCancellation, `失败原因可辨认为取消（实际 ${outcome.error?.name}, code=${outcome.error?.code}）`);
}

console.log('ReceiveSession：等待清单时取消，run() 立刻退出');
{
    const channel = new HangingChannel();
    const connection = new ProtocolConnection(channel);
    const controller = new AbortController();

    const run = new ReceiveSession(() => {
        throw new Error('不应该走到这里 —— 清单还没收到');
    }).run(connection, { signal: controller.signal });

    await new Promise(resolve => setTimeout(resolve, 20));
    controller.abort();

    const outcome = await Promise.race([
        run.then(() => ({ settled: 'resolved' }), error => ({ settled: 'rejected', error })),
        new Promise(resolve => setTimeout(() => resolve({ settled: 'timeout' }), 2000)),
    ]);

    check(outcome.settled === 'rejected', `run() 在取消后退出而不是继续挂着（实际 ${outcome.settled}）`);
    check(outcome.error?.name === 'AbortError' || outcome.error?.name === 'TransferFailedError',
        `失败原因可辨认为取消（实际 ${outcome.error?.name}）`);
}

console.log();
if (failures === 0) {
    console.log('全部通过。');
} else {
    console.log(`${failures} 项失败。`);
    process.exitCode = 1;
}
