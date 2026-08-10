// 生成 app.ico。一次性脚本 —— 跑一次产出图标后就不需要再跑了，
// 留在仓库里是为了让「图标怎么来的」可复现，而不是留一个二进制黑盒。
//
// 画的是一个圆角方块 + 两个箭头示意双向传输，配色与网页端一致。
// 只做 32×32 与 16×16 两个尺寸，够 Windows 用（任务栏、标题栏、资源管理器）。

import { writeFileSync } from 'node:fs';

function drawSize(size) {
    const pixels = Buffer.alloc(size * size * 4);
    const center = (size - 1) / 2;
    const radius = size * 0.46;

    for (let y = 0; y < size; y++) {
        for (let x = 0; x < size; x++) {
            // BMP 是自下而上存储的
            const offset = ((size - 1 - y) * size + x) * 4;

            // 圆角方块：切比雪夫距离配一个角落的欧氏修正
            const dx = Math.abs(x - center);
            const dy = Math.abs(y - center);
            const corner = Math.max(0, dx - radius * 0.62) ** 2 + Math.max(0, dy - radius * 0.62) ** 2;

            if (Math.max(dx, dy) > radius || corner > (radius * 0.38) ** 2) {
                continue;   // 透明
            }

            // 渐变：左上蓝 → 右下紫，与网页端 header 一致
            const t = (x + y) / (2 * size);
            const r = Math.round(0x66 + (0x76 - 0x66) * t);
            const g = Math.round(0x7e + (0x4b - 0x7e) * t);
            const b = Math.round(0xea + (0xa2 - 0xea) * t);

            // 中间画一条白色斜向双箭头的示意：两道白杠
            const onBar = isArrow(x, y, size);

            pixels[offset] = onBar ? 0xff : b;
            pixels[offset + 1] = onBar ? 0xff : g;
            pixels[offset + 2] = onBar ? 0xff : r;
            pixels[offset + 3] = 0xff;
        }
    }

    return pixels;
}

/** 两道横杠 + 箭头头部，示意双向传输。 */
function isArrow(x, y, size) {
    const s = size / 32;   // 以 32×32 为基准缩放
    const upperY = Math.round(12 * s);
    const lowerY = Math.round(19 * s);
    const thickness = Math.max(1, Math.round(2 * s));

    const inUpper = y >= upperY && y < upperY + thickness;
    const inLower = y >= lowerY && y < lowerY + thickness;

    const left = Math.round(9 * s);
    const right = Math.round(23 * s);

    if (inUpper && x >= left && x <= right) {
        return true;
    }

    if (inLower && x >= left && x <= right) {
        return true;
    }

    // 上排箭头指右、下排箭头指左
    const headSize = Math.max(1, Math.round(3 * s));
    for (let i = 1; i <= headSize; i++) {
        if (Math.abs(x - (right - i)) < thickness && Math.abs(y - upperY) <= i) {
            return true;
        }

        if (Math.abs(x - (left + i)) < thickness && Math.abs(y - lowerY) <= i) {
            return true;
        }
    }

    return false;
}

/** 一个尺寸的 ICO 目录项 + BITMAPINFOHEADER + 像素 + AND 掩码。 */
function buildImage(size) {
    const pixels = drawSize(size);

    // AND 掩码：32 位带 alpha 时不被使用，但格式要求存在，且每行按 4 字节对齐
    const maskRowBytes = Math.ceil(size / 32) * 4;
    const mask = Buffer.alloc(maskRowBytes * size);

    const header = Buffer.alloc(40);
    header.writeUInt32LE(40, 0);              // biSize
    header.writeInt32LE(size, 4);             // biWidth
    header.writeInt32LE(size * 2, 8);         // biHeight：XOR + AND 两部分，所以是两倍
    header.writeUInt16LE(1, 12);              // biPlanes
    header.writeUInt16LE(32, 14);             // biBitCount
    header.writeUInt32LE(0, 16);              // BI_RGB
    header.writeUInt32LE(pixels.length + mask.length, 20);

    return Buffer.concat([header, pixels, mask]);
}

const sizes = [32, 16];
const images = sizes.map(buildImage);

const fileHeader = Buffer.alloc(6);
fileHeader.writeUInt16LE(0, 0);               // 保留
fileHeader.writeUInt16LE(1, 2);               // 类型 1 = 图标
fileHeader.writeUInt16LE(sizes.length, 4);

let offset = 6 + sizes.length * 16;
const entries = sizes.map((size, index) => {
    const entry = Buffer.alloc(16);
    entry.writeUInt8(size === 256 ? 0 : size, 0);
    entry.writeUInt8(size === 256 ? 0 : size, 1);
    entry.writeUInt8(0, 2);                   // 调色板颜色数（0 = 不用调色板）
    entry.writeUInt8(0, 3);                   // 保留
    entry.writeUInt16LE(1, 4);                // 色彩平面
    entry.writeUInt16LE(32, 6);               // 每像素位数
    entry.writeUInt32LE(images[index].length, 8);
    entry.writeUInt32LE(offset, 12);
    offset += images[index].length;
    return entry;
});

writeFileSync(
    new URL('app.ico', import.meta.url),
    Buffer.concat([fileHeader, ...entries, ...images]));

console.log(`已生成 app.ico（${sizes.join('、')} 像素）`);
