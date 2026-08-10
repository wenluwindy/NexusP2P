// 清单路径校验。对应 C# 的 SafePath，规则必须一致。
//
// 这是本项目唯一的高危安全面：接收方按发送方给的清单落盘，而清单里的
// 路径是完全不可信的输入。网页端落盘时目录是用户选的，一条
// ../../ 就能写到选定目录之外。
//
// 返回 null 表示安全；返回字符串表示拒绝原因（传输被拒时用户要知道为什么）。

const MAX_SEGMENT_LENGTH = 255;
const MAX_PATH_LENGTH = 1024;

/** Windows 设备名。作为任何一段的主名（第一个点之前）出现都要拒绝。 */
const RESERVED_DEVICE_NAMES = new Set([
    'con', 'prn', 'aux', 'nul',
    'com1', 'com2', 'com3', 'com4', 'com5', 'com6', 'com7', 'com8', 'com9',
    'lpt1', 'lpt2', 'lpt3', 'lpt4', 'lpt5', 'lpt6', 'lpt7', 'lpt8', 'lpt9',
]);

/** Windows 文件名非法字符。冒号单独处理（盘符与 NTFS 备用数据流）。 */
const INVALID_NAME_CHARS = /[<>"|?*]/;

/**
 * 双向文本控制字符。它们能让 photo_txt.exe 显示成 photo_exe.txt，
 * 是已知的文件名欺骗手法。检查成本极低，直接拒。
 */
const BIDI_CONTROL_CHARS =
    /[‎‏‪‫‬‭‮⁦⁧⁨⁩]/;

/** 校验一条来自清单的路径。安全返回 null，否则返回原因。 */
export function isSafePath(path) {
    if (typeof path !== 'string' || path.length === 0) {
        return '路径为空。';
    }

    if (path.length > MAX_PATH_LENGTH) {
        return `路径长度 ${path.length} 超过上限 ${MAX_PATH_LENGTH}。`;
    }

    if (path.includes('\\')) {
        return "路径含反斜杠；清单里只允许用 '/' 作分隔符。";
    }

    if (path.includes(':')) {
        return '路径含冒号（可能是盘符或 NTFS 备用数据流）。';
    }

    if (path[0] === '/') {
        return '路径是绝对路径。';
    }

    for (const char of path) {
        const code = char.codePointAt(0);
        // C0/C1 控制字符。char.isControl 的等价判断
        if (code < 0x20 || (code >= 0x7f && code <= 0x9f)) {
            return `路径含控制字符 U+${code.toString(16).toUpperCase().padStart(4, '0')}。`;
        }
    }

    if (BIDI_CONTROL_CHARS.test(path)) {
        return '路径含双向文本控制字符，可能用于伪装文件名。';
    }

    if (INVALID_NAME_CHARS.test(path)) {
        return '路径含 Windows 文件名非法字符（< > " | ? *）。';
    }

    for (const segment of path.split('/')) {
        const problem = checkSegment(segment);
        if (problem !== null) {
            return problem;
        }
    }

    return null;
}

function checkSegment(segment) {
    if (segment.length === 0) {
        return '路径含空段（首尾斜杠或连续斜杠）。';
    }

    if (segment.length > MAX_SEGMENT_LENGTH) {
        return `路径段长度 ${segment.length} 超过上限 ${MAX_SEGMENT_LENGTH}。`;
    }

    if (segment === '.' || segment === '..') {
        return `路径含 "${segment}" 段。`;
    }

    // Windows 会静默丢掉结尾的点与空格："a. " 会变成 "a"，
    // 于是校验过的路径与实际落地的路径不是同一个 —— 直接拒
    const last = segment[segment.length - 1];
    if (last === '.' || last === ' ') {
        return `路径段 "${segment}" 以点或空格结尾；Windows 会静默去掉它。`;
    }

    const dotIndex = segment.indexOf('.');
    const stem = dotIndex < 0 ? segment : segment.slice(0, dotIndex);
    if (RESERVED_DEVICE_NAMES.has(stem.toLowerCase())) {
        return `路径段 "${segment}" 使用了 Windows 保留设备名 "${stem}"。`;
    }

    return null;
}

/**
 * 把浏览器给的相对路径（File.webkitRelativePath 或 File.name）
 * 转成清单里的规范形式。转换后仍会走一遍校验 —— 发送方也不该造出非法路径。
 *
 * 返回 { path } 或 { error }。
 */
export function toManifestPath(localRelativePath) {
    if (typeof localRelativePath !== 'string' || localRelativePath.length === 0) {
        return { error: '路径为空。' };
    }

    const candidate = localRelativePath.replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
    const problem = isSafePath(candidate);
    return problem === null ? { path: candidate } : { error: problem };
}
