// 临时脚本：把界面各状态截图出来，肉眼确认改版结果。不属于测试套件。
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { join } from 'node:path';

const require = createRequire(
    join(import.meta.dirname, '..', '..', '..', 'spike', 'BrowserStorage', 'package.json'));
const { chromium } = require('playwright');

const SIGNALING = join(
    import.meta.dirname, '..', '..', '..',
    'src', 'NexusP2P.Signaling', 'bin', 'Debug', 'net9.0', 'nexusp2p-signaling.exe');

const PORT = 5188;
const ORIGIN = `http://127.0.0.1:${PORT}`;
const OUT = join(import.meta.dirname, '..', '..', '..', 'shots');

const server = spawn(SIGNALING, [], {
    env: {
        ...process.env,
        Signaling__PublicOrigin: ORIGIN,
        ASPNETCORE_URLS: ORIGIN,
        ASPNETCORE_ENVIRONMENT: 'Production',
    },
    stdio: 'ignore',
});

for (let i = 0; i < 40; i++) {
    await new Promise(r => setTimeout(r, 250));
    try {
        if ((await fetch(`${ORIGIN}/health`)).ok) break;
    } catch { /* 还没起来 */ }
}

const browser = await chromium.launch();

async function shots(scheme) {
    const page = await browser.newPage({
        viewport: { width: 1000, height: 1100 },
        deviceScaleFactor: 2,
        colorScheme: scheme,
    });

    await page.goto(ORIGIN, { waitUntil: 'networkidle' });
    await page.screenshot({ path: join(OUT, `${scheme}-send.png`) });

    await page.locator('#fileInput').setInputFiles({
        name: 'presentation.key',
        mimeType: 'application/octet-stream',
        buffer: Buffer.alloc(2_400_000, 3),
    });
    await page.waitForSelector('#sendSummary:not(.hidden)', { timeout: 20_000 });
    await page.locator('#startSendBtn').click();
    await page.waitForFunction(
        () => (document.getElementById('shareLink')?.textContent ?? '').includes('#'),
        null, { timeout: 15_000 });
    await page.screenshot({ path: join(OUT, `${scheme}-share.png`), fullPage: true });

    await page.locator('.tab-btn[data-tab="settings"]').click();
    await page.waitForTimeout(400);   // 等分段控件的过渡跑完再拍
    await page.screenshot({ path: join(OUT, `${scheme}-settings.png`) });

    await page.locator('.tab-btn[data-tab="receive"]').click();
    await page.waitForTimeout(400);
    await page.screenshot({ path: join(OUT, `${scheme}-receive.png`) });
    await page.close();
}

try {
    await shots('light');
    await shots('dark');

    const mobile = await browser.newPage({
        viewport: { width: 390, height: 844 },
        deviceScaleFactor: 2,
    });
    await mobile.goto(ORIGIN, { waitUntil: 'networkidle' });
    await mobile.screenshot({ path: join(OUT, 'mobile-send.png') });

    // 分享链接入口：会自动填好并弹一次通知，正好用来看 toast 的样子
    await mobile.goto(`${ORIGIN}/r/130226582#${'A'.repeat(43)}`, { waitUntil: 'networkidle' });
    await mobile.waitForTimeout(500);
    await mobile.screenshot({ path: join(OUT, 'mobile-toast.png') });
    await mobile.close();

    console.log('截图完成');
} finally {
    await browser.close();
    server.kill();
}
