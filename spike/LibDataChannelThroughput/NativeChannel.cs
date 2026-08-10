using System.Reflection;
using DataChannelDotnet;
using DataChannelDotnet.Bindings;

namespace LibDataChannelThroughput;

/// <summary>
/// 补上托管封装没暴露的背压 API。
///
/// <para><see cref="IRtcDataChannel"/> 只给了 Send / IsOpen / 事件，
/// <b>没有 BufferedAmount 也没有低水位事件</b>。但 libdatachannel 的 C API
/// 是有的（<c>rtcGetBufferedAmount</c> 等），只是封装层没往上抬。</para>
///
/// <para>spike 里用反射取出私有的 <c>_channelId</c> 直接调原始绑定。
/// <b>这不是可交付的做法</b> —— 若最终选定这个库，要么给上游提 PR 把这几个
/// 属性抬上来，要么在产品里直接用 <c>Bindings.Rtc</c> 而不用托管封装。
/// 这里只是为了先拿到数字。</para>
/// </summary>
internal sealed class NativeChannel
{
    private static readonly FieldInfo ChannelIdField =
        typeof(DataChannelDotnet.Impl.RtcDataChannel)
            .GetField("_channelId", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            "拿不到 RtcDataChannel._channelId —— 封装层结构变了，背压无从下手。");

    private readonly int _id;

    private NativeChannel(int id) => _id = id;

    public int Id => _id;

    public static NativeChannel From(IRtcDataChannel channel)
    {
        if (channel is not DataChannelDotnet.Impl.RtcDataChannel implementation)
        {
            throw new InvalidOperationException(
                $"预期是 Impl.RtcDataChannel，实际是 {channel.GetType().FullName}。");
        }

        var id = (int)ChannelIdField.GetValue(implementation)!;
        return new NativeChannel(id);
    }

    /// <summary>已入队但尚未发出的字节数。</summary>
    public long BufferedAmount
    {
        get
        {
            var value = Rtc.rtcGetBufferedAmount(_id);
            return value < 0 ? 0 : value;   // 负数是错误码（通道已关等）
        }
    }

    public void SetBufferedAmountLowThreshold(int bytes) =>
        Rtc.rtcSetBufferedAmountLowThreshold(_id, bytes);
}
