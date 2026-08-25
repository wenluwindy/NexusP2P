// 全链路真实测试：**真浏览器发送 → 真 WebRTC → CLI 接收**。
//
// 这是唯一一条把所有东西串起来的测试：真的信令服务器、真的 WebSocket、
// 真的 ICE 打洞、真的 DTLS/SCTP 数据通道、真的浏览器 Web Crypto、
// 真的落盘。前面几个测试各自只覆盖一层：
//
//   vectors.mjs   字节格式一致（不过网络）
//   loopback.mjs  网页端协议自洽（内存管道）
//   interop.mjs   跨实现消息序列兼容（stdio 管道）
//   browser.mjs   界面能加载与操作（不真的传）
//   这一个        以上全部，加上真实 WebRTC
//
// 跑：node src/NexusP2P.Web/tests/e2e.mjs

import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const root = join(import.meta.dirname, '..', '..', '..');
const require = createRequire(join(root, 'spike', 'BrowserStorage', 'package.json'));
const { chromium } = require('playwright');

const SIGNALING = join(root, 'src', 'NexusP2P.Signaling', 'bin', 'Debug', 'net9.0',
    'nexusp2p-signaling.exe');
const CLI = join(root, 'src', 'NexusP2P.Cli', 'bin', 'Debug', 'net9.0', 'nexusp2p.exe');

const PORT = 5179;
const ORIGIN = `http://127.0.0.1:${PORT}`;

let failures = 0;

function check(condition, description) {
    console.log(`  ${condition ? '✓' : '✗'} ${description}`);
    if (!condition) {
        failures++;
    }
}

async function startSignaling() {
    const child = spawn(SIGNALING, [], {
        env: {
            ...process.env,
            Signaling__PublicOrigin: ORIGIN,
            ASPNETCORE_URLS: ORIGIN,
            ASPNETCORE_ENVIRONMENT: 'Production',
        },
        stdio: ['ignore', 'pipe', 'pipe'],
    });

    for (let attempt = 0; attempt < 40; attempt++) {
        await new Promise(resolve => setTimeout(resolve, 250));
        try {
            if ((await fetch(`${ORIGIN}/health`)).ok) {
                return child;
            }
        } catch {
            // 还没起来
        }
    }

    child.kill();
    throw new Error('信令服务器在 10 秒内没有起来。');
}

const signaling = await startSignaling();
const browser = await chromium.launch();
const destination = await mkdtemp(join(tmpdir(), 'nexusp2p-e2e-'));

// 内容大到需要多个分片、又不至于让测试跑很久
const SIZE = 3 * 1024 * 1024 + 12345;
const payload = Buffer.alloc(SIZE);
for (let i = 0; i < SIZE; i++) {
    payload[i] = (i * 17 + 3) % 251;
}

let cli = null;

try {
    const page = await browser.newPage();

    const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(error.message));

    console.log('浏览器打开界面并选文件');
    await page.goto(ORIGIN, { waitUntil: 'networkidle' });

    await page.locator('#fileInput').setInputFiles({
        name: 'e2e-payload.bin',
        mimeType: 'application/octet-stream',
        buffer: payload,
    });

    await page.waitForSelector('#sendSummary:not(.hidden)', { timeout: 30_000 });
    check(true, `清单已算好（${(SIZE / 1024 / 1024).toFixed(2)} MiB）`);

    console.log('点「生成文件码」，拿到分享链接');
    await page.locator('#startSendBtn').click();

    // 等分享链接真的填好（建房成功才有）。V2.1.0 起链接只带文件码，
    // 不再有 # 密钥片段 —— 见 core/codes.js 的 buildShareLink。
    await page.waitForFunction(
        () => {
            const text = document.getElementById('shareLink')?.textContent ?? '';
            return !text.includes('#') && text.includes('/r/');
        },
        { timeout: 30_000 });

    const shareUrl = (await page.locator('#shareLink').textContent()).trim();
    check(shareUrl.startsWith(ORIGIN), `拿到分享链接（${shareUrl.slice(0, 46)}…）`);

    console.log('CLI 用这个链接接收（真实 WebRTC）');
    cli = spawn(CLI, ['receive', shareUrl, '--dest', destination, '--signaling', ORIGIN], {
        stdio: ['ignore', 'pipe', 'pipe'],
    });

    let cliOutput = '';
    cli.stdout.on('data', chunk => {
        cliOutput += chunk.toString('utf8');
    });
    cli.stderr.on('data', chunk => {
        cliOutput += chunk.toString('utf8');
    });

    const exitCode = await new Promise(resolve => {
        const timer = setTimeout(() => {
            cli.kill();
            resolve('timeout');
        }, 120_000);

        cli.on('exit', code => {
            clearTimeout(timer);
            resolve(code);
        });
    });

    check(exitCode === 0, `CLI 正常退出（${exitCode}）`);

    if (exitCode !== 0) {
        console.log('    CLI 输出：');
        console.log(cliOutput.split('\n').map(line => `      ${line}`).join('\n'));
    }

    // 连接类型：本机回环应该是 host 候选直连，不该走中继
    check(
        cliOutput.includes('同局域网直连') || cliOutput.includes('公网直连'),
        cliOutput.includes('中继')
            ? '走了中继（本机回环下不该如此）'
            : '走的是直连（host 候选）');

    const landed = await readFile(join(destination, 'e2e-payload.bin'));
    check(landed.length === SIZE, `落盘大小正确（${landed.length} 字节）`);
    check(Buffer.compare(landed, payload) === 0, '内容与浏览器发出的逐字节一致');

    console.log('浏览器侧的界面状态');
    await page.waitForFunction(
        () => (document.getElementById('sendStatus')?.textContent ?? '').includes('完成'),
        { timeout: 30_000 });

    check(true, '界面显示传输完成');

    const percent = await page.locator('#sendPercent').textContent();
    check(percent.startsWith('100'), `进度到 100%（实际 ${percent}）`);

    check(pageErrors.length === 0,
        pageErrors.length === 0 ? '全程没有 JS 异常' : `有 JS 异常：${pageErrors.join(' | ')}`);
} finally {
    cli?.kill();
    await browser.close();
    signaling.kill();
    await rm(destination, { recursive: true, force: true });
}

console.log();
if (failures === 0) {
    console.log('全链路（浏览器 → 真实 WebRTC → CLI 落盘）通过。');
} else {
    console.log(`${failures} 项失败。`);
    process.exitCode = 1;
}
