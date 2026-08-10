// 逻辑消息的收发。对应 C# 的 ProtocolConnection + FrameWriter。

import {
    HEADER_SIZE,
    MAX_LOGICAL_MESSAGE_SIZE,
    MessageAssembler,
    MessageType,
    maxFragmentPayload,
    writeFrame,
} from '../core/frame.js';
import { serializeError } from '../core/messages.js';

/** 投递分片时的缓冲高水位。与 C# 侧一致的 4 MiB。 */
const HIGH_WATER_MARK = 4 * 1024 * 1024;

/** 发错误通知最多花多久。错误通知是给对端的好意，不值得为它把自己挂住。 */
const ERROR_NOTIFY_TIMEOUT_MS = 5000;

export class ProtocolConnection {
    constructor(channel) {
        this._channel = channel;
        this._assembler = new MessageAssembler();
    }

    get channel() {
        return this._channel;
    }

    /**
     * 收下一条完整的逻辑消息。
     *
     * 帧可能只是消息的一部分，所以要循环读到重组器给出完整消息为止。
     *
     * @param signal 取消信号，原样转给底层通道 —— 见 DataChannel.receive。
     */
    async receive(signal) {
        while (true) {
            const frame = await this._channel.receive(signal);
            const message = this._assembler.feed(frame);
            if (message !== null) {
                return message;
            }
        }
    }

    /**
     * 投递一条逻辑消息。超过单条上限时自动切帧，帧与帧之间连续 ——
     * 这正是 MessageAssembler 依赖的不变式。
     *
     * @param applyBackpressure 大消息（分片）要遵守背压；小的控制消息不必。
     */
    async send(type, payload, applyBackpressure = true) {
        if (payload.length > MAX_LOGICAL_MESSAGE_SIZE) {
            throw new Error(
                `逻辑消息 ${payload.length} 字节超过上限 ${MAX_LOGICAL_MESSAGE_SIZE} 字节。`);
        }

        const maxFragment = maxFragmentPayload(this._channel.maxMessageSize);
        let offset = 0;

        // 空载荷也要发一帧（Complete 消息就是空的），所以用 do-while
        do {
            if (applyBackpressure && this._channel.bufferedAmount > HIGH_WATER_MARK) {
                await this._channel.waitForDrain(HIGH_WATER_MARK / 2);
            }

            const take = Math.min(maxFragment, payload.length - offset);
            this._channel.send(writeFrame(type, payload.length, offset, payload.subarray(offset, offset + take)));
            offset += take;
        } while (offset < payload.length);
    }

    /** 发一条错误通知然后关闭。发送失败时忽略 —— 通道可能已经断了。 */
    async sendErrorAndClose(code, message) {
        try {
            await withTimeout(
                this.send(MessageType.Error, serializeError(code, message), false),
                ERROR_NOTIFY_TIMEOUT_MS);
        } catch {
            // 通道已经不可用，没别的办法可想
        }

        this._channel.close(message);
    }

    /**
     * 等缓冲彻底排空。
     *
     * 接收端发 Complete 之后必须等这一步：立刻关闭通道会让 Complete
     * 还在缓冲里就被丢掉，发送端于是永远等不到完成通知。
     */
    async drain() {
        try {
            await withTimeout(this._channel.waitForDrain(0), ERROR_NOTIFY_TIMEOUT_MS);
        } catch {
            // 排空超时不该让一次成功的传输报错
        }
    }
}

function withTimeout(promise, timeoutMs) {
    return Promise.race([
        promise,
        new Promise((_, reject) => setTimeout(() => reject(new Error('操作超时。')), timeoutMs)),
    ]);
}

/** 帧头大小的再导出，便于计算单帧净载荷。 */
export { HEADER_SIZE };
