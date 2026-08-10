// 真浏览器里加载一次界面，确认 DOM 层能跑起来。
//
// loopback / interop 两个测试跑的是协议层（在 Node 里）。
// 但 ui/dom.js 与 app.js 碰的是真实 DOM、Worker、能力探测 ——
// 那些在 Node 里根本不存在，所以必须真的开一次浏览器。
//
// 这个测试要抓的是最蠢也最容易发生的一类问题：
// 某个 getElementById 拼错了、某个模块 import 路径错了、Worker 起不来。
// 全都表现为「页面打开一片空白」，而 Node 侧的测试全是绿的。
//
// 跑：node src/NexusP2P.Web/tests/browser.mjs
//   （需要 spike/BrowserStorage 里已装好的 playwright）

import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { join } from 'node:path';

const require = createRequire(
    join(import.meta.dirname, '..', '..', '..', 'spike', 'BrowserStorage', 'package.json'));
const { chromium } = require('playwright');

const SIGNALING = join(
    import.meta.dirname, '..', '..', '..',
    'src', 'NexusP2P.Signaling', 'bin', 'Debug', 'net9.0', 'nexusp2p-signaling.exe');

const PORT = 5177;
const ORIGIN = `http://127.0.0.1:${PORT}`;

let failures = 0;

function check(condition, description) {
    console.log(`  ${condition ? '✓' : '✗'} ${description}`);
    if (!condition) {
        failures++;
    }
}

/** 起信令服务器（它同时托管前端）。 */
async function startServer() {
    const child = spawn(SIGNALING, [], {
        env: {
            ...process.env,
            Signaling__PublicOrigin: ORIGIN,
            ASPNETCORE_URLS: ORIGIN,
            ASPNETCORE_ENVIRONMENT: 'Production',
        },
        stdio: ['ignore', 'pipe', 'pipe'],
    });

    // 等到真的能连上，而不是睡一个固定的秒数
    for (let attempt = 0; attempt < 40; attempt++) {
        await new Promise(resolve => setTimeout(resolve, 250));

        try {
            const response = await fetch(`${ORIGIN}/health`);
            if (response.ok) {
                return child;
            }
        } catch {
            // 还没起来
        }
    }

    child.kill();
    throw new Error('信令服务器在 10 秒内没有起来。');
}

const server = await startServer();
const browser = await chromium.launch();

try {
    const page = await browser.newPage();

    // 任何一条控制台错误或未处理异常都要记下来 —— 那正是「页面一片空白」的成因
    const consoleErrors = [];
    const pageErrors = [];
    page.on('console', message => {
        if (message.type() === 'error') {
            consoleErrors.push(message.text());
        }
    });
    page.on('pageerror', error => pageErrors.push(error.message));

    console.log('首页加载');
    await page.goto(ORIGIN, { waitUntil: 'networkidle' });

    check(pageErrors.length === 0,
        pageErrors.length === 0 ? '没有未处理的 JS 异常' : `有 JS 异常：${pageErrors.join(' | ')}`);
    check(consoleErrors.length === 0,
        consoleErrors.length === 0 ? '控制台没有错误' : `控制台报错：${consoleErrors.join(' | ')}`);

    check(await page.title() === 'NexusP2P — 高速文件传输', '标题正确');

    // 能力探测应该已经把三行状态渲染出来了 ——
    // 它跑在 app.js 的 init() 里，渲染出来就证明整条启动链路走通了
    const capabilityRows = await page.locator('#capabilities .info-item').count();
    check(capabilityRows === 3, `能力探测渲染出 3 行（实际 ${capabilityRows}）`);

    console.log('标签页切换');
    await page.locator('.tab-btn[data-tab="receive"]').click();
    check(await page.locator('#receive-tab').evaluate(el => el.classList.contains('active')),
        '点「接收」切到了接收页');

    await page.locator('.tab-btn[data-tab="settings"]').click();
    check(await page.locator('#settings-tab').evaluate(el => el.classList.contains('active')),
        '点「设置」切到了设置页');

    console.log('分享链接路由：/r/<码>#<密钥> 应该自动填好并切到接收页');
    const code = '130226582';
    const key = 'A'.repeat(43);
    await page.goto(`${ORIGIN}/r/${code}#${key}`, { waitUntil: 'networkidle' });

    check(await page.locator('#receive-tab').evaluate(el => el.classList.contains('active')),
        '自动切到了接收页');
    check(await page.locator('#receiveInput').inputValue() === '130-226-582',
        `文件码已填好并分组显示（实际「${await page.locator('#receiveInput').inputValue()}」）`);
    check(await page.locator('#receiveKey').inputValue() === key, '密钥已从 fragment 填好');

    console.log('文件选择 → 清单计算（这一步会真的起 Worker 算 SHA-256）');
    await page.goto(ORIGIN, { waitUntil: 'networkidle' });

    // 造一个 300 KiB 的文件喂给 file input，验证 Worker 真的算完并显示摘要
    await page.locator('#fileInput').setInputFiles({
        name: 'probe.bin',
        mimeType: 'application/octet-stream',
        buffer: Buffer.alloc(300 * 1024, 7),
    });

    await page.waitForSelector('#sendSummary:not(.hidden)', { timeout: 20_000 });
    const summary = await page.locator('#sendSummary').textContent();

    check(summary.includes('1 个文件'), `摘要显示文件数（「${summary}」）`);
    check(summary.includes('个分片'), '摘要显示分片数（说明 Worker 真的算完了清单）');
    check(await page.locator('#startSendBtn').isVisible(), '「生成文件码」按钮出现了');

    check(pageErrors.length === 0 && consoleErrors.length === 0,
        '整个流程下来仍然没有 JS 错误');
} finally {
    await browser.close();
    server.kill();
}

console.log();
if (failures === 0) {
    console.log('浏览器端界面全部通过。');
} else {
    console.log(`${failures} 项失败。`);
    process.exitCode = 1;
}
