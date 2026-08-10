// 对**发布产物**跑一遍全链路，而不是对开发构建。
//
// 为什么要单独跑一次：自包含单文件发布与 dotnet build 出来的东西不一样 ——
// 原生库（libdatachannel）要在运行时解压到临时目录，静态资源要真的被复制进包。
// 这两件事在开发构建里都不成立，所以开发构建全绿也证明不了包能用。
//
// 跑：node src/NexusP2P.Web/tests/released.mjs

import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const root = join(import.meta.dirname, '..', '..', '..');
const require = createRequire(join(root, 'spike', 'BrowserStorage', 'package.json'));
const { chromium } = require('playwright');

// 关键区别就是这两行：指向 dist 里的发布产物
const DIST = join(root, 'dist', 'nexusp2p-win-x64');
const SIGNALING = join(DIST, 'nexusp2p-signaling.exe');
const CLI = join(DIST, 'nexusp2p.exe');

const PORT = 5189;
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
        cwd: DIST,
        env: {
            ...process.env,
            Signaling__PublicOrigin: ORIGIN,
            ASPNETCORE_URLS: ORIGIN,
        },
        stdio: ['ignore', 'pipe', 'pipe'],
    });

    for (let attempt = 0; attempt < 60; attempt++) {
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
    throw new Error('发布版信令服务器在 15 秒内没有起来。');
}

const signaling = await startSignaling();
const browser = await chromium.launch();
const destination = await mkdtemp(join(tmpdir(), 'nexusp2p-released-'));

const SIZE = 4 * 1024 * 1024 + 4321;
const payload = Buffer.alloc(SIZE);
for (let i = 0; i < SIZE; i++) {
    payload[i] = (i * 23 + 11) % 251;
}

let cli = null;

try {
    const page = await browser.newPage();
    const pageErrors = [];
    page.on('pageerror', error => pageErrors.push(error.message));

    console.log('发布版信令服务器托管的网页界面');
    await page.goto(ORIGIN, { waitUntil: 'networkidle' });
    check(await page.title() === 'NexusP2P — 高速文件传输', '首页从发布产物加载成功');

    await page.locator('#fileInput').setInputFiles({
        name: 'released.bin',
        mimeType: 'application/octet-stream',
        buffer: payload,
    });

    await page.waitForSelector('#sendSummary:not(.hidden)', { timeout: 30_000 });
    check(true, `清单已算好（${(SIZE / 1024 / 1024).toFixed(2)} MiB）`);

    await page.locator('#startSendBtn').click();
    await page.waitForFunction(
        () => (document.getElementById('shareLink')?.textContent ?? '').includes('#'),
        { timeout: 30_000 });

    const shareUrl = (await page.locator('#shareLink').textContent()).trim();
    check(shareUrl.includes('/r/'), '拿到分享链接');

    console.log('发布版 CLI 接收（真实 WebRTC，原生库需运行时解压）');
    cli = spawn(CLI, ['receive', shareUrl, '--dest', destination, '--signaling', ORIGIN], {
        cwd: DIST,
        stdio: ['ignore', 'pipe', 'pipe'],
    });

    let output = '';
    cli.stdout.on('data', c => { output += c.toString('utf8'); });
    cli.stderr.on('data', c => { output += c.toString('utf8'); });

    const exitCode = await new Promise(resolve => {
        const timer = setTimeout(() => { cli.kill(); resolve('timeout'); }, 150_000);
        cli.on('exit', code => { clearTimeout(timer); resolve(code); });
    });

    check(exitCode === 0, `发布版 CLI 正常退出（${exitCode}）`);
    if (exitCode !== 0) {
        console.log(output.split('\n').map(l => `      ${l}`).join('\n'));
    }

    const landed = await readFile(join(destination, 'released.bin'));
    check(landed.length === SIZE, `落盘大小正确（${landed.length} 字节）`);
    check(Buffer.compare(landed, payload) === 0, '内容逐字节一致');

    await page.waitForFunction(
        () => (document.getElementById('sendStatus')?.textContent ?? '').includes('完成'),
        { timeout: 30_000 });
    check(true, '浏览器侧显示传输完成');

    check(pageErrors.length === 0,
        pageErrors.length === 0 ? '没有 JS 异常' : `有 JS 异常：${pageErrors.join(' | ')}`);
} finally {
    cli?.kill();
    await browser.close();
    signaling.kill();
    await rm(destination, { recursive: true, force: true });
}

console.log();
if (failures === 0) {
    console.log('发布产物全链路通过。');
} else {
    console.log(`${failures} 项失败。`);
    process.exitCode = 1;
}
