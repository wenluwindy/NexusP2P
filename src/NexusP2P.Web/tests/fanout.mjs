// 一对多发送的网页端自测（V2）。
//
// fanout-connector.js 里真正新的东西是「同一份清单 + 同一个密钥驱动 N 条
// 互不影响的发送链路」；RTCPeerConnection 没法在 Node 里跑，但链路语义
// 可以：N 对内存管道，每对上一条独立 SendSession，对面各一条 ReceiveSession。
// 另外 FanOutSignalingClient 的路由逻辑（按 from 攒信令、到达队列、离开事件）
// 纯粹是数据结构，不碰网络，直接喂消息验证。
//
// 跑：node src/NexusP2P.Web/tests/fanout.mjs

import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { ProtocolConnection } from '../wwwroot/js/transfer/connection.js';
import { ReceiveSession } from '../wwwroot/js/transfer/receive-session.js';
import { SendSession } from '../wwwroot/js/transfer/send-session.js';
import { buildManifest } from '../wwwroot/js/transfer/manifest-builder.js';
import { generateSecret } from '../wwwroot/js/core/crypto.js';
import { MerkleParameters } from '../wwwroot/js/core/manifest.js';
import { SAFE_MAX_MESSAGE_SIZE } from '../wwwroot/js/core/frame.js';
import { toBase64Url } from '../wwwroot/js/core/bytes.js';
import { FanOutSignalingClient } from '../wwwroot/js/net/fanout-signaling.js';

const HARNESS = join(
    import.meta.dirname, '..', '..', '..',
    'tests', 'NexusP2P.InteropHarness', 'bin', 'Debug', 'net9.0',
    'NexusP2P.InteropHarness.exe');

/** 把子进程的 stdin/stdout 包成 DataChannel（与 interop.mjs 相同的线上格式）。 */
class ProcessChannel {
    constructor(child) {
        this._child = child;
        this.maxMessageSize = SAFE_MAX_MESSAGE_SIZE;
        this.bufferedAmount = 0;
        this.isOpen = true;

        this._pending = Buffer.alloc(0);
        this._inbound = [];
        this._waiters = [];
        this._closedReason = null;

        child.stdout.on('data', chunk => this._onData(chunk));
        child.stdout.on('end', () => this._onClosed('对端关闭了 stdout'));
        child.on('exit', code => this._onClosed(`子进程退出，代码 ${code}`));
    }

    _onData(chunk) {
        this._pending = Buffer.concat([this._pending, chunk]);

        while (this._pending.length >= 4) {
            const length = this._pending.readInt32BE(0);

            if (this._pending.length < 4 + length) {
                break;
            }

            const message = new Uint8Array(this._pending.subarray(4, 4 + length));
            this._pending = this._pending.subarray(4 + length);

            const waiter = this._waiters.shift();
            if (waiter !== undefined) {
                waiter.resolve(message);
            } else {
                this._inbound.push(message);
            }
        }
    }

    send(bytes) {
        if (!this.isOpen) {
            throw new Error(`通道已关闭：${this._closedReason}`);
        }

        const header = Buffer.alloc(4);
        header.writeInt32BE(bytes.length, 0);
        this._child.stdin.write(Buffer.concat([header, Buffer.from(bytes)]));
    }

    receive() {
        if (this._inbound.length > 0) {
            return Promise.resolve(this._inbound.shift());
        }

        if (this._closedReason !== null) {
            return Promise.reject(new Error(this._closedReason));
        }

        return new Promise((resolve, reject) => this._waiters.push({ resolve, reject }));
    }

    async waitForDrain() {}

    close(reason) {
        this._onClosed(reason ?? '本地关闭');
        this._child.stdin.end();
    }

    _onClosed(reason) {
        if (this._closedReason !== null) {
            return;
        }

        this._closedReason = reason;
        this.isOpen = false;

        const waiters = this._waiters;
        this._waiters = [];
        for (const waiter of waiters) {
            waiter.reject(new Error(reason));
        }
    }
}

class LoopbackChannel {
    constructor(name, faultInjector = null) {
        this.name = name;
        this.peer = null;
        this.maxMessageSize = SAFE_MAX_MESSAGE_SIZE;
        this.bufferedAmount = 0;
        this.isOpen = true;

        this._inbound = [];
        this._waiters = [];
        this._closedReason = null;
        this._fault = faultInjector;
        this.framesSent = 0;
    }

    send(bytes) {
        if (!this.isOpen) {
            throw new Error(`${this.name} 已关闭。`);
        }

        this.framesSent++;
        let frame = bytes.slice();

        if (this._fault !== null) {
            frame = this._fault(this, frame, this.framesSent);
            if (frame === null) {
                return;
            }
        }

        this.peer._deliver(frame);
    }

    _deliver(frame) {
        const waiter = this._waiters.shift();
        if (waiter !== undefined) {
            waiter.resolve(frame);
            return;
        }

        this._inbound.push(frame);
    }

    receive() {
        if (this._inbound.length > 0) {
            return Promise.resolve(this._inbound.shift());
        }

        if (this._closedReason !== null) {
            return Promise.reject(new Error(this._closedReason));
        }

        return new Promise((resolve, reject) => this._waiters.push({ resolve, reject }));
    }

    async waitForDrain() {}

    close(reason) {
        if (this._closedReason !== null) {
            return;
        }

        this._closedReason = reason ?? '已关闭';
        this.isOpen = false;

        const waiters = this._waiters;
        this._waiters = [];
        for (const waiter of waiters) {
            waiter.reject(new Error(this._closedReason));
        }

        this.peer?._onPeerClosed(this._closedReason);
    }

    _onPeerClosed(reason) {
        if (this._closedReason !== null) {
            return;
        }

        this._closedReason = `对端关闭：${reason}`;
        this.isOpen = false;

        const waiters = this._waiters;
        this._waiters = [];
        for (const waiter of waiters) {
            waiter.reject(new Error(this._closedReason));
        }
    }

    static pair(senderFault = null) {
        const a = new LoopbackChannel('sender', senderFault);
        const b = new LoopbackChannel('receiver');
        a.peer = b;
        b.peer = a;
        return [a, b];
    }
}

class MemoryWriter {
    constructor(manifest) {
        this.manifest = manifest;
        this.buffers = manifest.entries.map(entry => new Uint8Array(entry.length));
    }

    async writePiece(fileIndex, offset, bytes) {
        this.buffers[fileIndex].set(bytes, offset);
    }

    async finalize() {
        return { strategy: 'memory', message: '完成', downloads: [] };
    }

    async abort() {}
}

function fakeFile(name, length, seed = 0) {
    const data = new Uint8Array(length);
    for (let i = 0; i < length; i++) {
        data[i] = (i * 7 + seed * 31) % 251;
    }

    const file = new File([data], name);
    Object.defineProperty(file, 'webkitRelativePath', { value: name, configurable: true });
    return { file, data };
}

let failures = 0;

function check(condition, description) {
    console.log(`  ${condition ? '✓' : '✗'} ${description}`);
    if (!condition) {
        failures++;
    }
}

function bytesEqual(a, b) {
    if (a.length !== b.length) {
        return false;
    }

    for (let i = 0; i < a.length; i++) {
        if (a[i] !== b[i]) {
            return false;
        }
    }

    return true;
}

console.log('同一份清单驱动 3 条独立链路，三个接收方都逐字节收齐');
{
    const source = fakeFile('shared.bin', 20000, 42);

    // 清单只算一次 —— 这正是扇出路径的约定（Worker 里算一次，N 条链路复用）
    const manifest = await buildManifest([source.file], {
        parameters: new MerkleParameters(1024, 4096),
    });
    const secret = generateSecret();

    const writers = [];
    const links = [];

    for (let i = 0; i < 3; i++) {
        const [senderChannel, receiverChannel] = LoopbackChannel.pair();

        const receive = new ReceiveSession(secret, m => {
            const writer = new MemoryWriter(m);
            writers.push(writer);
            return writer;
        }).run(new ProtocolConnection(receiverChannel));

        // 每个接收方一条**独立的** SendSession（AD-11）
        const send = new SendSession(manifest, [source.file], secret)
            .run(new ProtocolConnection(senderChannel));

        links.push(Promise.all([send, receive]));
    }

    await Promise.all(links);

    check(writers.length === 3, '三个接收方都收到了清单');
    check(
        writers.every(w => bytesEqual(w.buffers[0], source.data)),
        '三份落地内容都与源逐字节一致');
}

console.log('一条链路断掉不影响其他链路（AD-11）');
{
    const source = fakeFile('mixed.bin', 16000, 7);
    const manifest = await buildManifest([source.file], {
        parameters: new MerkleParameters(1024, 4096),
    });
    const secret = generateSecret();

    // 第 2 条链路在发第 3 帧时把通道咔嚓掉
    const outcomes = [];
    const writers = [];

    const tasks = [0, 1, 2].map(i => {
        const fault = i === 1
            ? (channel, frame, index) => {
                if (index === 3) {
                    channel.close('模拟链路断开');
                    return null;
                }

                return frame;
            }
            : null;

        const [senderChannel, receiverChannel] = LoopbackChannel.pair(fault);

        const receive = new ReceiveSession(secret, m => {
            const writer = new MemoryWriter(m);
            writers[i] = writer;
            return writer;
        }).run(new ProtocolConnection(receiverChannel));

        const send = new SendSession(manifest, [source.file], secret)
            .run(new ProtocolConnection(senderChannel));

        return Promise.allSettled([send, receive]).then(result => {
            outcomes[i] = result;
        });
    });

    await Promise.all(tasks);

    check(
        outcomes[1].some(o => o.status === 'rejected'),
        '断开的那条链路以失败结束');
    check(
        outcomes[0].every(o => o.status === 'fulfilled') &&
        outcomes[2].every(o => o.status === 'fulfilled'),
        '另外两条链路正常完成');
    check(
        bytesEqual(writers[0].buffers[0], source.data) &&
        bytesEqual(writers[2].buffers[0], source.data),
        '正常链路的内容逐字节一致');
}

console.log('FanOutSignalingClient：开闸前的信令按 peer 攒住并按序补发');
{
    const client = new FanOutSignalingClient('http://example.invalid');

    // 直接喂消息（不连网络）：r1 的 offer 应答与两个候选在开闸前到达
    client._dispatch({ type: 'signal', from: 'r1', payload: { sdp: 'sdp-1', type: 'answer' } });
    client._dispatch({ type: 'signal', from: 'r1', payload: { candidate: 'cand-a', mid: '0' } });
    client._dispatch({ type: 'signal', from: 'r2', payload: { candidate: 'cand-x', mid: '0' } });
    client._dispatch({ type: 'signal', from: 'r1', payload: { candidate: 'cand-b', mid: '0' } });

    const seen = [];
    client.beginSignalDelivery('r1',
        description => seen.push(`desc:${description.sdp}`),
        candidate => seen.push(`cand:${candidate.candidate}`));

    check(
        seen.join(',') === 'desc:sdp-1,cand:cand-a,cand:cand-b',
        `r1 的三条信令按原顺序补发（实际 ${seen.join(',')}）`);

    // r2 的信令不能串进 r1（AD-12：按 from 路由）
    const seenR2 = [];
    client.beginSignalDelivery('r2',
        () => seenR2.push('desc'),
        candidate => seenR2.push(`cand:${candidate.candidate}`));

    check(seenR2.join(',') === 'cand:cand-x', 'r2 只收到自己的信令');

    // 开闸后的信令直接投递
    client._dispatch({ type: 'signal', from: 'r1', payload: { candidate: 'cand-c', mid: '0' } });
    check(seen[seen.length - 1] === 'cand:cand-c', '开闸后到达的信令即时投递');
}

console.log('FanOutSignalingClient：到达队列不丢、按序取出，peer-left 触发回调');
{
    const client = new FanOutSignalingClient('http://example.invalid');

    // 没人等的时候到达的两个接收方要排队
    client._dispatch({ type: 'peer-joined', peerId: 'aa11' });
    client._dispatch({ type: 'peer-joined', peerId: 'bb22' });

    check(await client.waitForReceiver() === 'aa11', '第一个取出的是先到的 aa11');
    check(await client.waitForReceiver() === 'bb22', '第二个取出的是 bb22');

    // 有人在等的时候直接唤醒
    const waiting = client.waitForReceiver();
    client._dispatch({ type: 'peer-joined', peerId: 'cc33' });
    check(await waiting === 'cc33', '等待中的调用被新到达的接收方唤醒');

    const left = [];
    client.onReceiverLeft = peerId => left.push(peerId);
    client._dispatch({ type: 'peer-left', peerId: 'aa11' });
    check(left.join(',') === 'aa11', 'peer-left 带着 peerId 到达回调');

    // close 后等待者拿到 null（「不会再有了」）
    const last = client.waitForReceiver();
    client.close();
    check(await last === null, 'close 之后等待者醒来并拿到 null');
    check(await client.waitForReceiver() === null, 'close 之后再等直接返回 null');
}

console.log('降级（AD-15）：旧服务器不回显 maxReceivers 时按 1 处理');
{
    // 不连网络，只验证解析规则本身
    const { FanOutRoomCreated } = await import('../wwwroot/js/net/fanout-signaling.js');

    const modern = new FanOutRoomCreated('123456789', 'http://x/r', [], 3);
    check(modern.maxReceivers === 3, '新服务器回显的席位数原样保留');

    // createRoom 里的规则：typeof message.maxReceivers === 'number' ? ... : 1
    const echoed = { code: '1', shareUrlBase: '' };
    const fallback = typeof echoed.maxReceivers === 'number' ? echoed.maxReceivers : 1;
    check(fallback === 1, '缺字段时降级为 1（一对一）');
}

console.log('跨实现扇出：网页端一份清单同时发给 2 个 C# 接收进程');
{
    const source = fakeFile('fanout.bin', 2_500_000, 21);
    const manifest = await buildManifest([source.file], {
        parameters: new MerkleParameters(64 * 1024, 1024 * 1024),
    });
    const secret = generateSecret();

    // 两个真实的 C# 接收进程 —— 与真实扇出唯一的差别是通道走 stdio 而不是
    // WebRTC；两条链路各自独立 SendSession，与 fanout-connector.js 一致
    const results = await Promise.all([0, 1].map(async i => {
        const destination = await mkdtemp(join(tmpdir(), `nexusp2p-fanout-${i}-`));
        const child = spawn(HARNESS, ['receive', toBase64Url(secret), destination], {
            stdio: ['pipe', 'pipe', 'pipe'],
        });

        let diagnostics = '';
        child.stderr.on('data', chunk => {
            diagnostics += chunk.toString('utf8');
        });

        // 等 close 而不是 exit：close 保证 stderr 已经全部冲刷进 diagnostics，
        // 两个子进程并行时 exit 与最后一段 stderr 的先后没有保证
        const exited = new Promise(resolve => child.on('close', code => resolve(code)));
        const channel = new ProcessChannel(child);

        try {
            await new SendSession(manifest, [source.file], secret)
                .run(new ProtocolConnection(channel));
        } finally {
            child.stdin.end();
        }

        // 先等退出再读 diagnostics —— 对象字面量的属性是从左到右求值的，
        // 把 await 放在属性位置会先读走一个还是空的 diagnostics
        const exitCode = await exited;
        return { destination, diagnostics, exitCode };
    }));

    check(results.every(r => r.exitCode === 0),
        `两个 C# 接收进程都正常退出（${results.map(r => r.exitCode).join(', ')}）`);

    // 清单哈希（= 分片加密的 AAD）两端两份都必须一致
    const localHash = Array.from(manifest.hash, b => b.toString(16).padStart(2, '0')).join('');
    check(
        results.every(r => /hash=([0-9a-f]{64})/.exec(r.diagnostics)?.[1] === localHash),
        `两个接收方都算出同一个清单哈希（${localHash.slice(0, 16)}…）`);

    let bothMatch = true;
    for (const { destination } of results) {
        const landed = await readFile(join(destination, 'fanout.bin'));
        if (Buffer.compare(landed, Buffer.from(source.data)) !== 0) {
            bothMatch = false;
        }

        await rm(destination, { recursive: true, force: true });
    }

    check(bothMatch, '两份落盘内容都与源逐字节一致（2.5 MB）');
}

console.log();
if (failures === 0) {
    console.log('一对多网页端自测全部通过。');
} else {
    console.log(`${failures} 项失败。`);
    process.exitCode = 1;
}
