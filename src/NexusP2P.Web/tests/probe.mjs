// 临时脚本：确认分段控件的选中态在深浅色下都只有一个、且底色区分得开。
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { join } from 'node:path';

const require = createRequire(
    join(import.meta.dirname, '..', '..', '..', 'spike', 'BrowserStorage', 'package.json'));
const { chromium } = require('playwright');

const SIGNALING = join(
    import.meta.dirname, '..', '..', '..',
    'src', 'NexusP2P.Signaling', 'bin', 'Debug', 'net9.0', 'nexusp2p-signaling.exe');

const PORT = 5189;
const ORIGIN = `http://127.0.0.1:${PORT}`;

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

try {
    for (const scheme of ['light', 'dark']) {
        const page = await browser.newPage({ colorScheme: scheme });
        await page.goto(ORIGIN, { waitUntil: 'networkidle' });
        await page.locator('.tab-btn[data-tab="settings"]').click();
        await page.waitForTimeout(400);   // 等 0.2s 的过渡跑完，否则读到的是插值中的旧色

        const report = await page.evaluate(() => ({
            active: [...document.querySelectorAll('.tab-btn.active')].map(b => b.dataset.tab),
            aria: [...document.querySelectorAll('.tab-btn')]
                .map(b => `${b.dataset.tab}=${b.getAttribute('aria-selected')}`),
            bg: [...document.querySelectorAll('.tab-btn')].map(b =>
                `${b.dataset.tab}:${getComputedStyle(b).backgroundColor}`),
            track: getComputedStyle(document.querySelector('.tabs')).backgroundColor,
            panel: [...document.querySelectorAll('.tab-content')]
                .filter(p => getComputedStyle(p).display !== 'none').map(p => p.id),
            barAria: document.querySelector('#sendProgressBar')
                .parentElement.getAttribute('aria-valuenow'),
        }));

        console.log(`[${scheme}]`, JSON.stringify(report, null, 2));
        await page.close();
    }
} finally {
    await browser.close();
    server.kill();
}
