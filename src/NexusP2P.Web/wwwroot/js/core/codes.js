// 文件码与分享链接。对应 C# 的 TransferCode / ShareLinkFactory。
//
// 密钥必须位于 # 之后。URL fragment 按规范永不随请求发送到服务器，
// 这是「服务器即使中继流量也无法解密」的全部依据。

import { fromBase64Url, toBase64Url } from './bytes.js';
import { SECRET_SIZE } from './crypto.js';

export const DIGIT_COUNT = 9;
export const ROOM_PATH_SEGMENT = 'r';

/** base64url 编码后的密钥字符数（32 字节无填充）。 */
const SECRET_ENCODED_LENGTH = 43;

/**
 * 宽容解析文件码：忽略连字符、空格、以及它们的全角形式，
 * 并把全角数字折算成半角。用户是从聊天记录里复制或听着念的，
 * 不该因为多一个空格就失败。
 *
 * 返回 9 位数字串（含前导零），失败返回 null。
 */
export function parseCode(text) {
    if (typeof text !== 'string' || text.length === 0) {
        return null;
    }

    const skipped = new Set(['-', ' ', '\t', '_', '.', '－', '　', '‐', '–', '—']);
    let digits = '';

    for (const char of text) {
        if (skipped.has(char)) {
            continue;
        }

        const code = char.codePointAt(0);
        let digit = -1;

        if (code >= 0x30 && code <= 0x39) {
            digit = code - 0x30;
        } else if (code >= 0xff10 && code <= 0xff19) {
            digit = code - 0xff10;   // 全角０-９
        }

        if (digit < 0 || digits.length >= DIGIT_COUNT) {
            return null;
        }

        digits += String(digit);
    }

    return digits.length === DIGIT_COUNT ? digits : null;
}

/** 分组显示 111-111-111，便于口头传达。 */
export function formatCode(digits) {
    return digits.replace(/(\d{3})(?=\d)/g, '$1-');
}

/** 解析 32 字节密钥。失败返回 null —— 输入是用户粘贴的文本。 */
export function parseSecret(text) {
    if (typeof text !== 'string' || text.length !== SECRET_ENCODED_LENGTH) {
        return null;
    }

    const bytes = fromBase64Url(text);
    return bytes !== null && bytes.length === SECRET_SIZE ? bytes : null;
}

/**
 * 解析分享链接。**与基址无关** —— 接收方拿到的链接可能来自任何域名，
 * 所以只看路径与片段，不校验主机。
 *
 * 返回 { code, secret } 或 null。
 */
export function parseShareLink(url) {
    if (typeof url !== 'string' || url.trim().length === 0) {
        return null;
    }

    let parsed;
    try {
        parsed = new URL(url.trim());
    } catch {
        return null;
    }

    // 片段以 '#' 开头；空片段说明密钥没带上
    if (parsed.hash.length <= 1) {
        return null;
    }

    const secret = parseSecret(parsed.hash.slice(1));
    if (secret === null) {
        return null;
    }

    const segments = parsed.pathname.split('/').filter(s => s.length > 0);
    if (segments.length < 2 || segments[segments.length - 2] !== ROOM_PATH_SEGMENT) {
        return null;
    }

    const code = parseCode(segments[segments.length - 1]);
    return code === null ? null : { code, secret };
}

/** 生成分享链接。密钥放在 fragment 里 —— 见文件头。 */
export function buildShareLink(origin, code, secret) {
    const base = origin.replace(/\/+$/, '');
    return `${base}/${ROOM_PATH_SEGMENT}/${code}#${toBase64Url(secret)}`;
}

/**
 * 从当前页面地址取出房间码与密钥（接收方点开分享链接时）。
 * 不是分享链接格式就返回 null。
 */
export function readShareLinkFromLocation() {
    return parseShareLink(window.location.href);
}
