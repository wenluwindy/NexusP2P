// 真正的跨实现互通测试：**网页端发送 → C# 接收端落盘**。
//
// 向量测试证明了哈希与密文逐字节一致，回环测试证明了网页端自己能跑通。
// 但「网页发、exe 收」这条路要同时对上两件事：字节格式**和**消息序列。
// 后者只能靠真的跑一次 —— C# 侧已经栽过两次「两端一起干等、谁都不报错」，
// 那类问题在两端各自的单测里全是绿的。
//
// 跑：node src/NexusP2P.Web/tests/interop.mjs

import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { toBase64Url } from '../wwwroot/js/core/bytes.js';
import { generateSecret } from '../wwwroot/js/core/crypto.js';
import { MerkleParameters } from '../wwwroot/js/core/manifest.js';
import { ProtocolConnection } from '../wwwroot/js/transfer/connection.js';
import { buildManifest } from '../wwwroot/js/transfer/manifest-builder.js';
import { ReceiveSession } from '../wwwroot/js/transfer/receive-session.js';
import { SendSession } from '../wwwroot/js/transfer/send-session.js';
import { SAFE_MAX_MESSAGE_SIZE } from '../wwwroot/js/core/frame.js';

/** 收集写入的分片，最后逐字节比对。 */
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

const HARNESS = join(
    import.meta.dirname, '..', '..', '..',
    'tests', 'NexusP2P.InteropHarness', 'bin', 'Debug', 'net9.0',
    'NexusP2P.InteropHarness.exe');

/**
 * 把子进程的 stdin/stdout 包成 DataChannel。
 *
 * 线上格式与 C# 侧的 StdioDataChannel 对称：每条消息前面 4 字节大端长度。
 */
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

    /** 按长度前缀切出完整消息。一次 data 事件可能带来半条或好几条。 */
    _onData(chunk) {
        this._pending = Buffer.concat([this._pending, chunk]);

        while (this._pending.length >= 4) {
            const length = this._pending.readInt32BE(0);

            if (this._pending.length < 4 + length) {
                break;   // 还没收够
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

    async waitForDrain() {
        // stdin 的背压交给 Node 的流机制，这里不额外等
    }

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

function fakeFile(name, length, seed = 0) {
    const data = new Uint8Array(length);
    for (let i = 0; i < length; i++) {
        data[i] = (i * 13 + seed * 97) % 251;
    }

    const file = new File([data], name.split('/').pop());
    Object.defineProperty(file, 'webkitRelativePath', { value: name, configurable: true });
    return { file, data, path: name };
}

let failures = 0;

function check(condition, description) {
    console.log(`  ${condition ? '✓' : '✗'} ${description}`);
    if (!condition) {
        failures++;
    }
}

/** 网页端发一批文件给 C# 接收端，返回落盘结果。 */
async function sendToCSharp(files, parameters) {
    const destination = await mkdtemp(join(tmpdir(), 'nexusp2p-interop-'));
    const secret = generateSecret();

    const manifest = await buildManifest(files.map(f => f.file), { parameters });

    const child = spawn(HARNESS, ['receive', toBase64Url(secret), destination], {
        stdio: ['pipe', 'pipe', 'pipe'],
    });

    let diagnostics = '';
    child.stderr.on('data', chunk => {
        diagnostics += chunk.toString('utf8');
    });

    const exited = new Promise(resolve => child.on('exit', code => resolve(code)));

    const channel = new ProcessChannel(child);

    try {
        await new SendSession(manifest, files.map(f => f.file), secret)
            .run(new ProtocolConnection(channel));
    } finally {
        child.stdin.end();
    }

    const exitCode = await exited;
    return { destination, manifest, diagnostics, exitCode, secret };
}

console.log('网页端发送 → C# 接收端（单文件，跨分片边界）');
{
    const files = [fakeFile('report.bin', 2_500_000, 3)];
    const { destination, manifest, diagnostics, exitCode } =
        await sendToCSharp(files, new MerkleParameters(64 * 1024, 1024 * 1024));

    check(exitCode === 0, `C# 接收端正常退出（代码 ${exitCode}）`);
    check(diagnostics.includes('OK files=1'), 'C# 侧报告收到 1 个文件');

    // 清单哈希两端必须算成同一个值 —— 它是分片加密的 AAD，
    // 不一致的话每个分片都会认证失败
    const reportedHash = /hash=([0-9a-f]{64})/.exec(diagnostics)?.[1];
    const localHash = Array.from(manifest.hash, b => b.toString(16).padStart(2, '0')).join('');
    check(reportedHash === localHash, `两端算出同一个清单哈希（${localHash.slice(0, 16)}…）`);

    const landed = await readFile(join(destination, 'report.bin'));
    check(
        Buffer.compare(landed, Buffer.from(files[0].data)) === 0,
        `落盘内容与源逐字节一致（${landed.length} 字节）`);

    await rm(destination, { recursive: true, force: true });
}

console.log('网页端发送 → C# 接收端（多文件 + 子目录 + 空文件）');
{
    const files = [
        fakeFile('bundle/docs/notes.txt', 120_000, 11),
        fakeFile('bundle/empty.dat', 0, 12),
        fakeFile('bundle/data/payload.bin', 700_000, 13),
    ];

    const { destination, diagnostics, exitCode } =
        await sendToCSharp(files, new MerkleParameters(64 * 1024, 1024 * 1024));

    check(exitCode === 0, `C# 接收端正常退出（代码 ${exitCode}）`);
    check(diagnostics.includes('OK files=3'), 'C# 侧报告收到 3 个文件');

    let allMatch = true;
    for (const entry of files) {
        const landed = await readFile(join(destination, ...entry.path.split('/')));
        if (Buffer.compare(landed, Buffer.from(entry.data)) !== 0) {
            allMatch = false;
            console.log(`    路径 ${entry.path} 内容不一致`);
        }
    }

    check(allMatch, '三个文件（含空文件）都按目录结构正确落盘');

    await rm(destination, { recursive: true, force: true });
}

console.log('C# 发送端 → 网页端接收（这是更常见的方向：exe 发、浏览器收）');
{
    // 在磁盘上造一个真的文件夹给 C# 侧发
    const source = await mkdtemp(join(tmpdir(), 'nexusp2p-source-'));
    const payload = new Uint8Array(1_600_000);
    for (let i = 0; i < payload.length; i++) {
        payload[i] = (i * 29 + 5) % 251;
    }

    const nested = join(source, 'inner');
    await mkdir(nested, { recursive: true });
    await writeFile(join(source, 'top.bin'), payload);
    await writeFile(join(nested, 'deep.txt'), Buffer.from('嵌套目录里的中文内容', 'utf8'));

    const secret = generateSecret();
    const child = spawn(HARNESS, ['send', toBase64Url(secret), source], {
        stdio: ['pipe', 'pipe', 'pipe'],
    });

    let diagnostics = '';
    child.stderr.on('data', chunk => {
        diagnostics += chunk.toString('utf8');
    });

    const exited = new Promise(resolve => child.on('exit', code => resolve(code)));
    const channel = new ProcessChannel(child);

    let writer = null;
    let received = null;

    try {
        received = await new ReceiveSession(manifest => {
            writer = new MemoryWriter(manifest);
            return writer;
        }).run(new ProtocolConnection(channel));
    } finally {
        child.stdin.end();
    }

    const exitCode = await exited;

    check(exitCode === 0, `C# 发送端正常退出（代码 ${exitCode}）`);

    // 清单哈希：C# 侧算出来的必须与网页端解析后算出来的一致
    const reportedHash = /hash=([0-9a-f]{64})/.exec(diagnostics)?.[1];
    const localHash = Array.from(received.manifest.hash, b => b.toString(16).padStart(2, '0')).join('');
    check(reportedHash === localHash, `两端算出同一个清单哈希（${localHash.slice(0, 16)}…）`);

    check(received.manifest.entries.length === 2, '网页端解析出 2 个条目');

    // 顶层文件夹名要包含在路径里，这样接收端能重建目录结构
    const paths = received.manifest.entries.map(e => e.path);
    const folder = source.split(/[\\/]/).pop();
    check(
        paths.every(p => p.startsWith(`${folder}/`)),
        `路径带上了顶层文件夹名（${paths[0]}）`);

    const bigIndex = paths.findIndex(p => p.endsWith('top.bin'));
    check(
        Buffer.compare(Buffer.from(writer.buffers[bigIndex]), Buffer.from(payload)) === 0,
        `1.6 MB 文件逐字节一致`);

    const textIndex = paths.findIndex(p => p.endsWith('deep.txt'));
    check(
        Buffer.from(writer.buffers[textIndex]).toString('utf8') === '嵌套目录里的中文内容',
        '嵌套目录里的 UTF-8 中文内容正确');

    await rm(source, { recursive: true, force: true });
}

console.log();
if (failures === 0) {
    console.log('跨实现互通全部通过（双向）。');
} else {
    console.log(`${failures} 项失败。`);
    process.exitCode = 1;
}
