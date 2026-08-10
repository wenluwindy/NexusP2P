// 网页端发送/接收状态机的端到端自测。
//
// 用一对内存管道把 SendSession 与 ReceiveSession 直接对接 ——
// 这与 C# 侧「先在内存管道上跑通协议，再接 WebRTC」（AD-1）是同一个思路：
// 协议正确性与网络无关，而内存管道能精确注入乱序、拒收、断连。
//
// 跑：node src/NexusP2P.Web/tests/loopback.mjs

import { ProtocolConnection } from '../wwwroot/js/transfer/connection.js';
import { ReceiveSession } from '../wwwroot/js/transfer/receive-session.js';
import { SendSession } from '../wwwroot/js/transfer/send-session.js';
import { buildManifest } from '../wwwroot/js/transfer/manifest-builder.js';
import { generateSecret } from '../wwwroot/js/core/crypto.js';
import { MerkleParameters } from '../wwwroot/js/core/manifest.js';
import { SAFE_MAX_MESSAGE_SIZE } from '../wwwroot/js/core/frame.js';

/**
 * 一条内存数据通道，接口与 net/peer.js 的 DataChannel 一致。
 *
 * faultInjector 可以丢帧或改帧，用来验证「坏分片会被拒收并重传」
 * 这条最关键的恢复路径。
 */
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

        // 复制一份：真实通道给的是一份私有副本，
        // 复用同一个缓冲区会让 bug 被掩盖
        let frame = bytes.slice();

        if (this._fault !== null) {
            frame = this._fault(frame, this.framesSent);
            if (frame === null) {
                return;   // 丢掉这一帧
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

    async waitForDrain() {
        // 内存管道是同步投递的，永远没有积压
    }

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

        // 对端也要知道，否则它会一直等
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

/** 收集写入的分片，最后拼成完整内容以便逐字节比对。 */
class MemoryWriter {
    constructor(manifest) {
        this.manifest = manifest;
        this.buffers = manifest.entries.map(entry => new Uint8Array(entry.length));
        this.writes = 0;
    }

    async writePiece(fileIndex, offset, bytes) {
        this.buffers[fileIndex].set(bytes, offset);
        this.writes++;
    }

    async finalize() {
        return { strategy: 'memory', message: '完成', downloads: [] };
    }

    async abort() {}
}

/** 造一个内容确定的假 File。Node 18+ 有全局 File/Blob。 */
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
    if (condition) {
        console.log(`  ✓ ${description}`);
    } else {
        console.log(`  ✗ ${description}`);
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

/** 跑一次完整传输。返回接收端写下的内容与统计。 */
async function transfer(files, { senderFault = null, parameters } = {}) {
    const merkle = parameters ?? new MerkleParameters(1024, 4096);
    const manifest = await buildManifest(files.map(f => f.file), { parameters: merkle });
    const secret = generateSecret();

    const [senderChannel, receiverChannel] = LoopbackChannel.pair(senderFault);

    let writer = null;
    const receive = new ReceiveSession(secret, m => {
        writer = new MemoryWriter(m);
        return writer;
    }).run(new ProtocolConnection(receiverChannel));

    const send = new SendSession(manifest, files.map(f => f.file), secret)
        .run(new ProtocolConnection(senderChannel));

    const [, result] = await Promise.all([send, receive]);
    return { manifest, writer, result, senderChannel };
}

console.log('单文件，跨越分片与叶子边界');
{
    // 10000 字节 / 4096 = 3 个分片，末片不满
    const files = [fakeFile('a.bin', 10000)];
    const { manifest, writer } = await transfer(files);

    check(manifest.totalPieces === 3, `分片数为 3（实际 ${manifest.totalPieces}）`);
    check(bytesEqual(writer.buffers[0], files[0].data), '收到的内容与源逐字节一致');
}

console.log('多文件 + 空文件 + 子目录');
{
    const files = [
        fakeFile('docs/readme.txt', 5000, 1),
        fakeFile('empty.bin', 0, 2),
        fakeFile('data/nested/blob.bin', 9000, 3),
    ];

    const { manifest, writer } = await transfer(files);

    check(manifest.entries.length === 3, '清单有 3 个条目');
    check(
        manifest.entries.map(e => e.path).join(',') === 'data/nested/blob.bin,docs/readme.txt,empty.bin',
        '条目按规范顺序排列');

    // 清单排过序，所以要按路径找回对应的源数据
    let allMatch = true;
    for (let i = 0; i < manifest.entries.length; i++) {
        const source = files.find(f => f.file.name === manifest.entries[i].path
            || f.file.webkitRelativePath === manifest.entries[i].path);
        if (!bytesEqual(writer.buffers[i], source.data)) {
            allMatch = false;
        }
    }

    check(allMatch, '三个文件的内容都逐字节一致（含空文件）');
}

console.log('坏分片会被拒收并在下一轮重传');
{
    const files = [fakeFile('a.bin', 10000, 9)];

    // 发送端的帧序列是：1=清单，2/3/4=三个分片，5=PushComplete。
    // 篡改第 3 帧（第二个分片）的最后一个字节 —— 那是认证标签所在的位置，
    // 改动之后解密必然失败，这个分片应该被拒收然后在下一轮重传。
    let tampered = 0;
    const fault = (frame, index) => {
        if (index === 3 && tampered === 0) {
            tampered++;
            frame[frame.length - 1] ^= 0xff;
        }

        return frame;
    };

    const clean = await transfer([fakeFile('a.bin', 10000, 9)]);
    const { manifest, writer, senderChannel } = await transfer(files, { senderFault: fault });

    check(tampered === 1, '确实篡改了一个分片帧');
    check(bytesEqual(writer.buffers[0], files[0].data), '篡改后仍然收到完整正确的内容（已重传）');

    // 被拒收的分片不会落盘，重传的那一份只落盘一次 —— 所以写入次数恰好等于分片数
    check(
        writer.writes === manifest.totalPieces,
        `写入次数恰好等于分片数（${writer.writes} == ${manifest.totalPieces}），坏分片没有落盘`);

    // 重传的直接证据：比无故障时多发了帧
    check(
        senderChannel.framesSent > clean.senderChannel.framesSent,
        `发生了重传（有故障 ${senderChannel.framesSent} 帧 vs 无故障 ${clean.senderChannel.framesSent} 帧）`);
}

console.log('默认参数（64 KiB 叶子 / 1 MiB 分片）下的较大文件');
{
    // 2.5 MiB → 3 个 1 MiB 分片，且单个分片远超 64 KiB 的单帧上限，
    // 所以这条同时验证了分片/重组
    const files = [fakeFile('big.bin', 2_621_440, 5)];
    const { manifest, writer, senderChannel } = await transfer(files, {
        parameters: new MerkleParameters(64 * 1024, 1024 * 1024),
    });

    check(manifest.totalPieces === 3, `分片数为 3（实际 ${manifest.totalPieces}）`);
    check(senderChannel.framesSent > 3, `一条逻辑消息被切成了多帧（共发 ${senderChannel.framesSent} 帧）`);
    check(bytesEqual(writer.buffers[0], files[0].data), '2.5 MiB 内容逐字节一致');
}

console.log('密钥不对时明确报错，而不是收到垃圾');
{
    const files = [fakeFile('a.bin', 5000, 7)];
    const merkle = new MerkleParameters(1024, 4096);
    const manifest = await buildManifest([files[0].file], { parameters: merkle });

    const [senderChannel, receiverChannel] = LoopbackChannel.pair();

    const receive = new ReceiveSession(generateSecret(), m => new MemoryWriter(m))
        .run(new ProtocolConnection(receiverChannel));

    const send = new SendSession(manifest, [files[0].file], generateSecret())
        .run(new ProtocolConnection(senderChannel));

    const outcomes = await Promise.allSettled([send, receive]);
    const receiveError = outcomes[1].status === 'rejected' ? outcomes[1].reason.message : '';

    check(outcomes[1].status === 'rejected', '接收端拒绝了这次传输');
    check(receiveError.includes('清单解密失败'), `错误信息指向密钥不匹配：「${receiveError.slice(0, 30)}…」`);
}

console.log();
if (failures === 0) {
    console.log('全部通过。');
} else {
    console.log(`${failures} 项失败。`);
    process.exitCode = 1;
}
