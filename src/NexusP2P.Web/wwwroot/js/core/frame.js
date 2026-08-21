// 帧编解码与逻辑消息重组。对应 C# 的 ProtocolFrame / MessageAssembler。
//
// 为什么需要分片机制：默认分片是 1 MiB，而 DataChannel 单条消息的
// 跨浏览器安全上限只有 256 KiB（我们保守用 64 KiB）。所以一条逻辑消息
// 必须拆成多个帧传。
//
// 关键不变式：一条逻辑消息的各个帧在链路上是**连续的**，不与其他逻辑
// 消息交错。由「通道有序可靠」+「发送方不交错投递」共同保证，使得接收侧
// 只需要一个重组槽位而不是一张表。这里会强制校验 —— 一旦对端实现打破它，
// 立刻报错而不是悄悄拼出错误的数据。

export const MessageType = {
    Manifest: 0x01,
    Bitfield: 0x02,
    Piece: 0x03,
    Complete: 0x04,
    Error: 0x05,
    PushComplete: 0x06,

    // V3：发送方 → 接收方，本次传输的 32 字节密钥材料，通道建立后的首条消息。
    //
    // 这条消息把「文件码 + 密钥」缩成了「只要文件码」。威胁模型因此变化：
    // V1/V2 里密钥在 URL fragment 中，信令服务器从密码学上无法解密任何字节；
    // V3 里服务器若**主动**在 SDP 交换阶段做中间人就能拿到密钥。
    // 被动记录流量的服务器仍然什么都拿不到 —— 载荷在 DTLS 里。
    KeyOffer: 0x07,
};

const KNOWN_TYPES = new Set(Object.values(MessageType));

/** 帧头字节数：类型(1) + 总长(4) + 偏移(4) + 本片长度(4)。 */
export const HEADER_SIZE = 13;

/** 单条逻辑消息的长度上限（8 MiB）。上界必须在分配之前校验。 */
export const MAX_LOGICAL_MESSAGE_SIZE = 8 * 1024 * 1024;

/** 与 C# 的 WebRtcDataChannel.SafeMaxMessageSize 一致。 */
export const SAFE_MAX_MESSAGE_SIZE = 64 * 1024;

export function maxFragmentPayload(maxMessageSize) {
    if (maxMessageSize <= HEADER_SIZE) {
        throw new Error(`单条消息上限必须大于帧头 ${HEADER_SIZE} 字节。`);
    }

    return maxMessageSize - HEADER_SIZE;
}

/** 写一个帧，返回完整帧字节。 */
export function writeFrame(type, totalLength, offset, fragment) {
    const frame = new Uint8Array(HEADER_SIZE + fragment.length);
    const view = new DataView(frame.buffer);

    frame[0] = type;
    view.setInt32(1, totalLength, false);
    view.setInt32(5, offset, false);
    view.setInt32(9, fragment.length, false);
    frame.set(fragment, HEADER_SIZE);

    return frame;
}

/**
 * 解析帧头并切出载荷。frame **是不可信输入** —— 所有字段都校验过才返回。
 * 返回 { header, payload } 或 { error }。
 */
export function parseFrame(frame) {
    if (frame.length < HEADER_SIZE) {
        return { error: `帧只有 ${frame.length} 字节，不足帧头 ${HEADER_SIZE} 字节。` };
    }

    const type = frame[0];
    if (!KNOWN_TYPES.has(type)) {
        return { error: `未知的消息类型 0x${type.toString(16).padStart(2, '0').toUpperCase()}。` };
    }

    const view = new DataView(frame.buffer, frame.byteOffset, frame.byteLength);
    const totalLength = view.getInt32(1, false);
    const offset = view.getInt32(5, false);
    const fragmentLength = view.getInt32(9, false);

    if (totalLength < 0 || totalLength > MAX_LOGICAL_MESSAGE_SIZE) {
        return { error: `声明的总长 ${totalLength} 不在 0~${MAX_LOGICAL_MESSAGE_SIZE} 之间。` };
    }

    if (offset < 0 || fragmentLength < 0) {
        return { error: `偏移 ${offset} 或本片长度 ${fragmentLength} 为负数。` };
    }

    if (offset > totalLength || fragmentLength > totalLength - offset) {
        return { error: `偏移 ${offset} 加本片 ${fragmentLength} 超过总长 ${totalLength}。` };
    }

    if (frame.length !== HEADER_SIZE + fragmentLength) {
        return { error: `帧实际 ${frame.length} 字节，但帧头声明载荷 ${fragmentLength} 字节。` };
    }

    return {
        header: { type, totalLength, offset, fragmentLength },
        payload: frame.subarray(HEADER_SIZE),
    };
}

/**
 * 把帧拼回逻辑消息。
 *
 * 只有一个重组槽位 —— 依赖「一条逻辑消息的帧连续到达」这个不变式。
 * 每次 feed 返回完整消息或 null。违反不变式一律抛错。
 */
export class MessageAssembler {
    constructor() {
        this._buffer = null;
        this._type = 0;
        this._received = 0;
        this._totalLength = 0;
    }

    /** 喂一个帧。返回 { type, payload } 或 null（还没拼完）。 */
    feed(frame) {
        const parsed = parseFrame(frame);
        if (parsed.error !== undefined) {
            throw new Error(`收到畸形帧：${parsed.error}`);
        }

        const { header, payload } = parsed;

        if (this._buffer === null) {
            if (header.offset !== 0) {
                throw new Error(
                    `新消息的首帧偏移应为 0，实际为 ${header.offset}（前一条消息可能没收完）。`);
            }

            this._buffer = new Uint8Array(header.totalLength);
            this._type = header.type;
            this._totalLength = header.totalLength;
            this._received = 0;
        } else {
            // 交错投递会走到这里。悄悄接受等于拼出错误的数据。
            if (header.type !== this._type) {
                throw new Error(
                    `消息交错：正在重组 0x${this._type.toString(16)}，` +
                    `却收到 0x${header.type.toString(16)} 的帧。`);
            }

            if (header.totalLength !== this._totalLength) {
                throw new Error(
                    `同一条消息的总长不一致：先前 ${this._totalLength}，现在 ${header.totalLength}。`);
            }

            if (header.offset !== this._received) {
                throw new Error(
                    `帧不连续：期望偏移 ${this._received}，实际 ${header.offset}。`);
            }
        }

        this._buffer.set(payload, header.offset);
        this._received += header.fragmentLength;

        if (this._received < this._totalLength) {
            return null;
        }

        const message = { type: this._type, payload: this._buffer };
        this._buffer = null;
        this._received = 0;
        this._totalLength = 0;
        return message;
    }
}
