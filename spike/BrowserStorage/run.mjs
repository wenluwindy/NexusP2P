// Task 0.2 的自动化部分：在多个浏览器里跑 OPFS 与 Blob 的尺寸梯度。
//
// showSaveFilePicker 不在这里 —— 它必须由真实的用户手势触发并弹出系统对话框，
// 自动化跑不了。请在浏览器里打开页面手动点那个按钮。
//
// 用法：
//   node spike/BrowserStorage/run.mjs [--sizes 1,2,5] [--persistent]
//
// --persistent 用真实的持久配置目录跑。**这一项会明显改变结论**：
// Playwright 默认的临时配置在 Chromium 系上拿到的存储配额远小于
// navigator.storage.estimate() 报的数字，不加这个开关测出来的上限
// 是工具的上限，不是浏览器的上限。

import { chromium, firefox } from 'playwright';
import { createServer } from 'node:http';
import { readFile, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const GIB = 1024 * 1024 * 1024;

const sizeArg = process.argv.indexOf('--sizes');
const sizes = (sizeArg >= 0 ? process.argv[sizeArg + 1] : '1,2,5')
  .split(',').map(g => Math.round(parseFloat(g) * GIB));

// OPFS 要求安全上下文。http://localhost 算安全，file:// 不算。
const server = createServer(async (_req, res) => {
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
  res.end(await readFile(join(here, 'index.html')));
});

await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;

const persistent = process.argv.includes('--persistent');

const targets = [
  { name: 'Chromium', type: chromium, options: {} },
  { name: 'Edge', type: chromium, options: { channel: 'msedge' } },
  { name: 'Chrome', type: chromium, options: { channel: 'chrome' } },
  { name: 'Firefox', type: firefox, options: {} },
];

const report = [];

for (const target of targets) {
  let browser = null;
  let context;
  let profileDir = null;

  try {
    if (persistent) {
      profileDir = await mkdtemp(join(tmpdir(), 'nexusp2p-profile-'));
      context = await target.type.launchPersistentContext(profileDir, target.options);
    } else {
      browser = await target.type.launch(target.options);
      context = await browser.newContext();
    }
  } catch (e) {
    console.log(`跳过 ${target.name}：${e.message.split('\n')[0]}`);
    report.push({ browser: target.name, skipped: e.message.split('\n')[0] });
    continue;
  }

  const version = browser ? browser.version() : '(持久配置)';
  const page = await context.newPage();
  page.on('console', m => process.stdout.write(`  [${target.name}] ${m.text()}\n`));

  await page.goto(origin);
  await page.waitForFunction(() => window.spike !== undefined);

  // 等探测真正跑完再读，别读一张填了一半的表
  const capabilities = await page.evaluate(async () => {
    await window.spike.ready;
    return window.spike.capabilities;
  });
  console.log(`\n=== ${target.name} ${version}${persistent ? ' [持久配置]' : ''} ===`);
  console.log(JSON.stringify(capabilities, null, 2));

  const rows = [];
  for (const strategy of ['opfs', 'blob']) {
    for (const bytes of sizes) {
      // 每档单独设超时：5 GiB 的 Blob 失败方式常常是「越来越慢」而不是抛异常
      const result = await page.evaluate(
        ([s, b]) => window.spike.runOne(s, b), [strategy, bytes]);

      console.log(`  ${JSON.stringify(result)}`);
      rows.push(result);

      if (!result.supported || !result.ok) break;
    }
  }

  report.push({ browser: target.name, version, persistent, capabilities, rows });

  await context.close();
  if (browser) await browser.close();
  if (profileDir) await rm(profileDir, { recursive: true, force: true });
}

server.close();
console.log('\n===== 汇总 =====');
console.log(JSON.stringify(report, null, 2));
