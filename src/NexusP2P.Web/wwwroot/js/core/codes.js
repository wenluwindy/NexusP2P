// 文件码与分享链接。对应 C# 的 TransferCode / ShareLinkFactory。
//
// V3 起链接里**不再带密钥片段**：密钥由发送方在数据通道里推给接收方，
// 所以链接就是「文件码的可点击形式」，没有别的秘密。

export const DIGIT_COUNT = 9;
export const ROOM_PATH_SEGMENT = 'r';

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

/**
 * 解析分享链接。**与基址无关** —— 接收方拿到的链接可能来自任何域名，
 * 所以只看路径，不校验主机。
 *
 * V3 起链接不带密钥片段，所以只需取出文件码。旧链接（带 `#密钥`）
 * 仍然能解析 —— 片段被忽略即可，用户不必知道链接格式变过。
 *
 * 返回 { code } 或 null。
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

    const segments = parsed.pathname.split('/').filter(s => s.length > 0);
    if (segments.length < 2 || segments[segments.length - 2] !== ROOM_PATH_SEGMENT) {
        return null;
    }

    const code = parseCode(segments[segments.length - 1]);
    return code === null ? null : { code };
}

/** 生成分享链接。V3 起不带密钥 —— 见文件头。 */
export function buildShareLink(origin, code) {
    const base = origin.replace(/\/+$/, '');
    return `${base}/${ROOM_PATH_SEGMENT}/${code}`;
}

/**
 * 从当前页面地址取出房间码（接收方点开分享链接时）。
 * 不是分享链接格式就返回 null。
 */
export function readShareLinkFromLocation() {
    return parseShareLink(window.location.href);
}
