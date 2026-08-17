// 所有 DOM 操作集中在这里。app.js 只调这些函数，不直接碰元素 ——
// 这样界面改版不会牵动传输逻辑。
//
// 全部文本用 textContent 写入，不用 innerHTML：文件名来自对端，
// 是不可信输入，拼进 HTML 就是一个 XSS。

import { formatCode } from '../core/codes.js';
import { buildShareLink } from '../core/codes.js';
import { formatDuration, formatSize, formatSpeed } from '../transfer/progress.js';

const $ = id => document.getElementById(id);

let handlers = {};

export function bind(options) {
    handlers = options;

    bindTabs();
    bindUpload();
    bindSendActions();
    bindReceiveActions();
    bindSettings(options.signalingOrigin);
}

function bindTabs() {
    for (const button of document.querySelectorAll('.tab-btn')) {
        button.addEventListener('click', () => activateTab(button.dataset.tab));
    }
}

export function activateTab(name) {
    for (const button of document.querySelectorAll('.tab-btn')) {
        const selected = button.dataset.tab === name;
        button.classList.toggle('active', selected);
        button.setAttribute('aria-selected', String(selected));
    }

    for (const panel of document.querySelectorAll('.tab-content')) {
        panel.classList.toggle('active', panel.id === `${name}-tab`);
    }
}

function bindUpload() {
    const area = $('uploadArea');
    const fileInput = $('fileInput');
    const folderInput = $('folderInput');

    $('pickFilesBtn').addEventListener('click', () => fileInput.click());
    $('pickFolderBtn').addEventListener('click', () => folderInput.click());

    fileInput.addEventListener('change', () => handlers.onFilesSelected([...fileInput.files]));
    folderInput.addEventListener('change', () => handlers.onFilesSelected([...folderInput.files]));

    // dragover 必须 preventDefault，否则浏览器会直接打开文件
    for (const type of ['dragenter', 'dragover']) {
        area.addEventListener(type, event => {
            event.preventDefault();
            area.classList.add('drag-over');
        });
    }

    for (const type of ['dragleave', 'drop']) {
        area.addEventListener(type, () => area.classList.remove('drag-over'));
    }

    area.addEventListener('drop', event => {
        event.preventDefault();
        handlers.onDropped(event.dataTransfer);
    });
}

function bindSendActions() {
    // 包一层：处理器里抛出的同步异常与 reject 都会被浏览器静默吞掉，
    // 用户看到的就是「点了没反应」。宁可弹一条错误也不要毫无反馈。
    $('startSendBtn').addEventListener('click', () => guard(() => handlers.onStartSend()));
    $('cancelSendBtn').addEventListener('click', () => guard(() => handlers.onCancel()));

    $('copyCodeBtn').addEventListener('click', () => copyText($('shareCode').dataset.raw, '文件码'));
    $('copyLinkBtn').addEventListener('click', () => copyText($('shareLink').textContent, '分享链接'));
    $('copyKeyBtn').addEventListener('click', () => copyText($('shareKey').textContent, '密钥'));
}

/** 跑一个界面动作，出了意外就说出来 —— 不留「点了没反应」这种状态。 */
function guard(action) {
    try {
        const result = action();
        if (result instanceof Promise) {
            result.catch(reportUnexpected);
        }
    } catch (error) {
        reportUnexpected(error);
    }
}

function reportUnexpected(error) {
    console.error(error);
    notify(`界面出错：${error?.message ?? error}。请刷新页面（Ctrl+F5）后重试。`, 'error');
}

function bindReceiveActions() {
    $('startReceiveBtn').addEventListener('click', () => guard(() =>
        handlers.onStartReceive($('receiveInput').value.trim(), $('receiveKey').value)));

    $('cancelReceiveBtn').addEventListener('click', () => guard(() => handlers.onCancel()));
}

function bindSettings(origin) {
    $('signalingUrl').value = origin ?? '';
    $('saveSettingsBtn').addEventListener('click', () =>
        handlers.onSaveSignaling($('signalingUrl').value.trim()));
}

// ---------------- 发送侧 ----------------

export function renderFileList(files) {
    const list = $('fileList');
    list.textContent = '';

    const total = files.reduce((sum, file) => sum + file.size, 0);

    for (const file of files.slice(0, 200)) {
        const row = document.createElement('div');
        row.className = 'file-item';

        const name = document.createElement('span');
        name.className = 'file-name';
        name.textContent = file.webkitRelativePath || file.name;

        const size = document.createElement('span');
        size.className = 'file-size';
        size.textContent = formatSize(file.size);

        row.append(name, size);
        list.append(row);
    }

    if (files.length > 200) {
        const more = document.createElement('div');
        more.className = 'file-item';
        more.textContent = `…以及另外 ${files.length - 200} 个文件`;
        list.append(more);
    }

    const summary = document.createElement('div');
    summary.className = 'file-item file-total';
    summary.textContent = `共 ${files.length} 个文件，${formatSize(total)}`;
    list.append(summary);

    list.classList.remove('hidden');
}

export function setSendSummary(manifest) {
    $('sendSummary').textContent =
        `共 ${manifest.entries.length} 个文件，${formatSize(manifest.totalLength)}，` +
        `${manifest.totalPieces} 个分片。`;
}

export function updateHashProgress(progress, speed) {
    const percent = progress.totalBytes > 0
        ? (progress.hashedBytes / progress.totalBytes) * 100
        : 0;

    setBar('hashBar', percent);
    $('hashStatus').textContent =
        `正在计算校验和 ${formatSize(progress.hashedBytes)} / ${formatSize(progress.totalBytes)}` +
        `（${formatSpeed(speed)}）`;
}

/** 发送侧的阶段机：idle → hashing → ready → waiting → transferring → done/failed */
export function setSendPhase(phase) {
    show('hashPanel', phase === 'hashing');
    show('sendSummary', phase === 'ready' || phase === 'waiting' || phase === 'transferring');
    show('startSendBtn', phase === 'ready');
    show('maxPeersGroup', phase === 'ready');
    show('sendPanel', phase === 'waiting' || phase === 'transferring' || phase === 'done');
    show('sendProgress', phase === 'transferring' || phase === 'done' || phase === 'failed');
    show('cancelSendBtn', phase === 'waiting' || phase === 'transferring');

    if (phase === 'idle' || phase === 'ready') {
        $('receiverList').textContent = '';
        show('receiverList', false);
    }

    if (phase === 'waiting') {
        setSendStatus('等待对方接收…');
    } else if (phase === 'done') {
        setSendStatus('传输完成。');
        setBar('sendProgressBar', 100);
    }
}

/**
 * 读「允许接收人数」。默认 1 = 一对一（V1 行为）。
 *
 * 只保证下界（至少 1 个人）—— 上界不在这里定：服务器会把请求夹到它
 * 自己配置的席位上限，并在 `created` 里回显生效值，界面按回显说明。
 *
 * 找不到输入框时返回 1 而不是抛异常：这个值只是可选的扇出上限，
 * 不该因为它让整个「生成文件码」流程崩掉（曾经因为浏览器缓存了
 * 旧版本的这个文件、元素 id 对不上，点按钮直接静默失效）。
 */
export function readMaxPeers() {
    const input = document.getElementById('maxPeersInput');
    if (input === null) {
        return 1;
    }

    const value = Number.parseInt(input.value, 10);
    if (!Number.isInteger(value)) {
        return 1;
    }

    return Math.max(1, value);
}

/**
 * 接收方列表（V2）：每人一行。一对一（快照为空）时不渲染 ——
 * 一对一的界面与 V1 完全一致。
 *
 * @param snapshots [{ peerId, state, progress, error }]
 */
export function renderReceiverList(snapshots) {
    const list = $('receiverList');
    list.textContent = '';

    if (snapshots.length === 0) {
        show('receiverList', false);
        return;
    }

    for (const snapshot of snapshots) {
        const row = document.createElement('div');
        row.className = 'file-item';

        const name = document.createElement('span');
        name.className = 'file-name';

        const detail = document.createElement('span');
        detail.className = 'file-size';

        if (snapshot.state === 'completed') {
            name.textContent = `接收方 ${snapshot.peerId} — 已收齐并通过校验`;
            detail.textContent = '';
        } else if (snapshot.state === 'failed') {
            name.textContent = `接收方 ${snapshot.peerId} — 失败`;
            detail.textContent = snapshot.error?.message ?? '';
        } else if (snapshot.progress !== null) {
            const percent = snapshot.progress.totalBytes > 0
                ? (snapshot.progress.completedBytes / snapshot.progress.totalBytes) * 100
                : 0;
            name.textContent = `接收方 ${snapshot.peerId}`;
            detail.textContent =
                `${percent.toFixed(1)}%  ` +
                `${formatSize(snapshot.progress.completedBytes)} / ` +
                formatSize(snapshot.progress.totalBytes);
        } else {
            name.textContent = `接收方 ${snapshot.peerId} — 正在建立连接…`;
            detail.textContent = '';
        }

        row.append(name, detail);
        list.append(row);
    }

    show('receiverList', true);
}

export function setSendStatus(text) {
    $('sendStatus').textContent = text;
}

export function showShareCode(room, secret) {
    const code = $('shareCode');
    code.textContent = formatCode(room.code);
    code.dataset.raw = room.code;

    const base = room.shareUrlBase.length > 0
        ? room.shareUrlBase.replace(/\/r$/, '')
        : window.location.origin;

    $('shareLink').textContent = buildShareLink(base, room.code, secret);
    $('shareKey').textContent = toBase64UrlText(secret);
}

function toBase64UrlText(secret) {
    let binary = '';
    for (const byte of secret) {
        binary += String.fromCharCode(byte);
    }

    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function updateSendProgress(progress) {
    renderProgress('send', progress);
}

// ---------------- 接收侧 ----------------

export function setReceivePhase(phase) {
    show('receiveProgress', phase !== 'idle');
    show('cancelReceiveBtn', phase === 'connecting' || phase === 'transferring');
    show('startReceiveBtn', phase === 'idle' || phase === 'done' || phase === 'failed');

    if (phase === 'done') {
        setBar('receiveProgressBar', 100);
        setReceiveStatus('接收完成。');
    }
}

export function setReceiveStatus(text) {
    $('receiveStatus').textContent = text;
}

export function setReceiveSummary(manifest) {
    const info = $('receiveFileInfo');
    info.textContent = '';

    const heading = document.createElement('div');
    heading.textContent =
        `共 ${manifest.entries.length} 个文件，${formatSize(manifest.totalLength)}：`;
    info.append(heading);

    for (const entry of manifest.entries.slice(0, 50)) {
        const row = document.createElement('div');
        row.className = 'file-item';

        const name = document.createElement('span');
        name.className = 'file-name';
        name.textContent = entry.path;

        const size = document.createElement('span');
        size.className = 'file-size';
        size.textContent = formatSize(entry.length);

        row.append(name, size);
        info.append(row);
    }

    if (manifest.entries.length > 50) {
        const more = document.createElement('div');
        more.textContent = `…以及另外 ${manifest.entries.length - 50} 个文件`;
        info.append(more);
    }
}

export function updateReceiveProgress(progress) {
    renderProgress('receive', progress);
}

export function setStorageStrategy(description, strategy) {
    const panel = $('storageStrategy');
    panel.textContent = '';
    panel.classList.remove('hidden');

    const label = document.createElement('div');
    label.className = 'strategy-label';
    label.textContent = `保存方式：${description.label}`;

    const detail = document.createElement('div');
    detail.className = 'strategy-detail';
    detail.textContent = description.detail;

    panel.append(label, detail);
    panel.classList.toggle('strategy-warning', !description.withinLimit);
}

/** 非流式策略收完后给下载链接。 */
export function showDownloads(result) {
    const panel = $('downloads');
    panel.textContent = '';

    const message = document.createElement('div');
    message.textContent = result.message;
    panel.append(message);

    for (const download of result.downloads) {
        const link = document.createElement('a');
        link.href = download.url;
        link.download = download.path.split('/').pop();
        link.className = 'download-link';
        link.textContent = `${download.path}（${formatSize(download.size)}）`;
        panel.append(link);
    }

    show('downloads', true);
}

export function prefillReceive({ code, secret }) {
    $('receiveInput').value = formatCode(code);
    $('receiveKey').value = toBase64UrlText(secret);
    activateTab('receive');
    notify('已从分享链接读取文件码和密钥，点「开始接收」即可。', 'success');
}

// ---------------- 公共 ----------------

/**
 * 写进度条宽度，同时把百分比同步到外层的 role="progressbar" 上 ——
 * 宽度是给眼睛看的，aria-valuenow 是给读屏软件念的，两者必须一起动。
 */
function setBar(id, percent) {
    const clamped = Math.min(Math.max(percent, 0), 100);
    const fill = $(id);

    fill.style.width = `${clamped.toFixed(1)}%`;
    fill.parentElement?.setAttribute('aria-valuenow', clamped.toFixed(0));
}

function renderProgress(prefix, progress) {
    const percent = progress.totalBytes > 0
        ? (progress.completedBytes / progress.totalBytes) * 100
        : 0;

    setBar(`${prefix}ProgressBar`, percent);
    $(`${prefix}Percent`).textContent = `${percent.toFixed(1)}%`;
    $(`${prefix}Speed`).textContent = formatSpeed(progress.speed);
    $(`${prefix}Eta`).textContent = `剩余 ${formatDuration(progress.remaining)}`;
    $(`${prefix}Transferred`).textContent =
        `${formatSize(progress.completedBytes)} / ${formatSize(progress.totalBytes)}`;
    $(`${prefix}Bottleneck`).textContent = progress.bottleneck;
}

export function setConnectionType(text, side = 'send') {
    $(`${side}ConnectionType`).textContent = text;
}

/** 能力探测结果如实展示（AD-6）。 */
export function showCapabilities(capabilities) {
    const rows = [
        ['直接写入文件夹', capabilities.directory],
        ['流式写入单个文件', capabilities.saveFile],
        ['浏览器存储（OPFS）', capabilities.opfs],
    ];

    const panel = $('capabilities');
    panel.textContent = '';

    for (const [label, available] of rows) {
        const row = document.createElement('div');
        row.className = 'info-item';

        const name = document.createElement('span');
        name.textContent = label;

        const badge = document.createElement('span');
        badge.className = available ? 'status-badge connected' : 'status-badge';
        badge.textContent = available ? '支持' : '不支持';

        row.append(name, badge);
        panel.append(row);
    }

    const note = document.createElement('p');
    note.textContent = '网页端不支持跨会话续传（关掉标签页后进度不保留）。' +
        '大文件建议用桌面版程序。';
    panel.append(note);
}

let notifyTimer = null;

export function notify(message, kind = 'info') {
    const element = $('notification');
    element.textContent = message;
    element.className = `notification ${kind}`;

    clearTimeout(notifyTimer);
    notifyTimer = setTimeout(() => element.classList.add('hidden'), 6000);
}

async function copyText(text, what) {
    if (typeof text !== 'string' || text.length === 0) {
        return;
    }

    try {
        await navigator.clipboard.writeText(text);
        notify(`${what}已复制。`, 'success');
    } catch {
        // 非 HTTPS 或未授权时剪贴板不可用。选中让用户自己复制，
        // 而不是留下一个「点了没反应」的按钮。
        notify(`无法自动复制，请手动选中${what}。`, 'error');
    }
}

function show(id, visible) {
    $(id).classList.toggle('hidden', !visible);
}
