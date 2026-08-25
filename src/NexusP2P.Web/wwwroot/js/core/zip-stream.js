// 顺流式 ZIP 打包器（store 模式，不压缩）。移植自 FilePizza 的
// zip-stream（BSD-3-Clause，其原型是 StreamSaver.js 的示例，MIT），
// 按 NexusP2P 的清单模型重写：pull 驱动、逐块背压、显式小端写入。
//
// 用途：OPFS / 内存策略收完多个文件后，把整棵目录树打包成**一个** zip
// 流式交给浏览器保存 —— 一次点击替代 N 个下载链接，且全程不把数据
// 再整份读进内存（每块读出、算 CRC、发出去，内存占用与文件大小无关）。
//
// 限制（与上游一致）：store-only；单文件与总大小、偏移量超过 4 GiB
// 需要 zip64，这里不支持 —— 调用方（writers.js）负责在超限时回退。

/** CRC-32（IEEE 802.3 多项式）查表实现。 */
class Crc32 {
    constructor() {
        this._crc = -1;
        this._table = Crc32.buildTable();
    }

    static buildTable() {
        const table = new Uint32Array(256);
        for (let i = 0; i < 256; i++) {
            let value = i;
            for (let bit = 0; bit < 8; bit++) {
                value = (value & 1) !== 0 ? (value >>> 1) ^ 0xEDB88320 : value >>> 1;
            }
            table[i] = value;
        }
        return table;
    }

    append(data) {
        let crc = this._crc | 0;
        const table = this._table;

        for (let i = 0; i < data.length; i++) {
            crc = (crc >>> 8) ^ table[(crc ^ data[i]) & 0xFF];
        }

        this._crc = crc;
    }

    get() {
        return ~this._crc >>> 0;
    }
}

/**
 * @typedef {Object} ZipEntry
 * @property {string} name 归档内路径，用 / 分隔，不得重复
 * @property {boolean} [directory] 目录项：无数据，只占一条记录
 * @property {number} [lastModified] 毫秒时间戳，缺省用打包时刻
 * @property {() => ReadableStream<Uint8Array>} [stream] 文件内容源
 */

/**
 * 把一批条目打包成一条 zip ReadableStream。
 *
 * pull 驱动：消费端（pipeTo）要一块才读一块，天然背压；
 * CRC 与大小在数据流完后补进 central directory（本地头里是 0 +
 * 数据描述符，zip 规范允许的流式写法）。
 *
 * @param {ZipEntry[]} entries 按归档内顺序排列
 * @returns {ReadableStream<Uint8Array>}
 */
export function createZipStream(entries) {
    if (!Array.isArray(entries) || entries.length === 0) {
        throw new Error('zip 条目不能为空。');
    }

    let controller = null;
    let index = -1;
    let active = null;       // 当前文件：{ reader, crc, header, nameBuf, compressed, uncompressed, headerOffset }
    let offset = 0;          // 已输出的字节偏移，写 central directory 用
    let finished = false;

    const records = [];      // { header(26B 已补全), nameBuf, directory, headerOffset }
    const seen = new Set();
    const encoder = new TextEncoder();

    function enqueue(bytes) {
        controller.enqueue(bytes);
        offset += bytes.length;
    }

    /** 本地文件头（30B + 文件名）。flags 固定 0x0808：bit3 数据描述符 + bit11 UTF-8 名。 */
    function writeLocalHeader(nameBuf, directory, timestamp) {
        const header = new Uint8Array(30 + nameBuf.length);
        const view = new DataView(header.buffer);
        const date = dosDateTime(timestamp);

        view.setUint32(0, 0x04034B50, true);
        view.setUint16(4, 20, true);                 // version needed: 2.0
        view.setUint16(6, 0x0808, true);
        view.setUint16(8, 0, true);                  // method: store
        view.setUint16(10, date.time, true);
        view.setUint16(12, date.date, true);
        view.setUint16(26, nameBuf.length, true);    // 文件名长度
        header.set(nameBuf, 30);

        enqueue(header);
        return header.subarray(4, 30);               // 后半段 26B：central 要复用
    }

    /** 数据描述符（16B）：签名 + CRC + 压缩后大小 + 原始大小。 */
    function writeDataDescriptor(crc, compressed, uncompressed) {
        const footer = new Uint8Array(16);
        const view = new DataView(footer.buffer);

        view.setUint32(0, 0x08074B50, true);
        view.setUint32(4, crc, true);
        view.setUint32(8, compressed, true);
        view.setUint32(12, uncompressed, true);

        enqueue(footer);
    }

    /** central directory 尾块：每文件 46B + 名字，再加 EOCD。 */
    function writeCentralDirectory() {
        const total = records.reduce(
            (sum, record) => sum + 46 + record.nameBuf.length, 0);

        const block = new Uint8Array(total + 22);
        const view = new DataView(block.buffer);
        let at = 0;

        for (const record of records) {
            view.setUint32(at, 0x02014B50, true);
            view.setUint16(at + 4, 20, true);        // version made by
            block.set(record.header, at + 6);        // version needed .. extra len（含已补全的 CRC/大小）
            view.setUint16(at + 32, 0, true);        // comment len
            view.setUint16(at + 34, 0, true);        // disk start
            view.setUint16(at + 36, 0, true);        // internal attrs
            if (record.directory) {
                view.setUint8(at + 38, 0x10);        // MS-DOS 目录位
            }
            view.setUint32(at + 42, record.headerOffset, true);
            block.set(record.nameBuf, at + 46);
            at += 46 + record.nameBuf.length;
        }

        view.setUint32(at, 0x06054B50, true);
        view.setUint16(at + 8, records.length, true);
        view.setUint16(at + 10, records.length, true);
        view.setUint32(at + 12, total, true);
        view.setUint32(at + 16, offset, true);       // central 起始偏移（central 块自身未计入 offset）

        controller.enqueue(block);
    }

    /**
     * 推进一步（一块数据、一个头或一个尾）。返回 false 表示整个归档完成。
     * 一次 pull 只走一步 —— 消费端不拉，磁盘就不读，内存就不涨。
     */
    async function step() {
        if (finished) {
            return false;
        }

        if (active === null) {
            index++;

            if (index >= entries.length) {
                writeCentralDirectory();
                finished = true;
                return false;
            }

            const entry = entries[index];
            if (typeof entry?.name !== 'string' || entry.name.length === 0) {
                throw new Error('zip 条目缺少合法的 name。');
            }

            // 目录项必须以 / 结尾，这是 zip 规范表达「目录」的方式
            const name = entry.directory === true && !entry.name.endsWith('/')
                ? `${entry.name}/`
                : entry.name;

            if (seen.has(name)) {
                throw new Error(`zip 内出现重复路径：${name}。`);
            }
            seen.add(name);

            const nameBuf = encoder.encode(name);
            const headerOffset = offset;
            const header = writeLocalHeader(nameBuf, entry.directory === true, entry.lastModified);

            if (entry.directory === true) {
                writeDataDescriptor(0, 0, 0);
                records.push({ header, nameBuf, directory: true, headerOffset });
                return true;
            }

            if (typeof entry.stream !== 'function') {
                throw new Error(`zip 条目 ${entry.name} 既不是目录也没有内容源。`);
            }

            active = {
                reader: entry.stream().getReader(),
                crc: new Crc32(),
                header,
                nameBuf,
                headerOffset,
                compressed: 0,
                uncompressed: 0,
            };

            return true;
        }

        // 当前文件：读一块、算 CRC、发出去
        const { value, done } = await active.reader.read();

        if (done) {
            const crc = active.crc.get();
            writeDataDescriptor(crc, active.compressed, active.uncompressed);
            patchHeader(active.header, crc, active.compressed, active.uncompressed);
            records.push({
                header: active.header,
                nameBuf: active.nameBuf,
                directory: false,
                headerOffset: active.headerOffset,
            });
            active = null;
            return true;
        }

        active.crc.append(value);
        active.compressed += value.length;
        active.uncompressed += value.length;
        enqueue(value);

        if (offset > 0xFFFFFFFF) {
            throw new Error('归档超过 4 GiB，需要 zip64，此处不支持。');
        }

        return true;
    }

    return new ReadableStream({
        start(c) {
            controller = c;
        },

        async pull() {
            try {
                const more = await step();
                if (!more) {
                    controller.close();
                }
            } catch (error) {
                controller.error(error);
            }
        },
    });
}

/** 把 CRC 与大小补进 26B 头片段（本地头里仍为 0 —— 由数据描述符承载）。 */
function patchHeader(header, crc, compressed, uncompressed) {
    // 片段从本地头偏移 4 开始：片段内偏移 r 对应本地头 4 + r
    const view = new DataView(header.buffer, header.byteOffset, header.byteLength);
    view.setUint32(10, crc, true);         // 本地头偏移 14
    view.setUint32(14, compressed, true);  // 本地头偏移 18
    view.setUint32(18, uncompressed, true);
}

/** JS Date → DOS 时间/日期字段。 */
function dosDateTime(timestamp) {
    const date = Number.isFinite(timestamp) ? new Date(timestamp) : new Date();

    const year = Math.max(1980, date.getFullYear());
    const time = (date.getHours() << 11) | (date.getMinutes() << 5) | (date.getSeconds() >>> 1);
    const day = ((year - 1980) << 9) | ((date.getMonth() + 1) << 5) | date.getDate();

    return { time, date: day };
}
