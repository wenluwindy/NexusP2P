using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NexusP2P.Core.Codes;

/// <summary>
/// 九位十进制文件码，展示成 <c>111-111-111</c>。
///
/// <para>分三组是为了<b>能口头念给别人</b> —— 这是它选十进制而不是
/// base32 之类更紧凑编码的唯一理由。10 亿种组合对「只有我和朋友用」
/// 完全够，而且房间只在传输期间存在。</para>
///
/// <para>码只承载房间号，<b>不承载密钥</b>。密钥由发送方在数据通道建立后
/// 推给接收方（见 <c>MessageType.KeyOffer</c>），所以 V3 起<b>文件码是接收
/// 一次传输的唯一凭证</b> —— 念给谁，谁就能收。</para>
/// </summary>
public readonly struct TransferCode : IEquatable<TransferCode>
{
    public const int DigitCount = 9;
    public const int GroupSize = 3;

    /// <summary>码的取值上界（不含）：10^9。</summary>
    public const int ExclusiveUpper = 1_000_000_000;

    private readonly int _value;

    private TransferCode(int value) => _value = value;

    /// <summary>纯数字形式，恒为 9 位，含前导零。</summary>
    public string Digits => _value.ToString("D9", CultureInfo.InvariantCulture);

    public int Value => _value;

    /// <summary>
    /// 生成一个新码。用 <see cref="RandomNumberGenerator"/> 而不是
    /// <see cref="Random"/> —— 码是唯一的访问凭证，可预测就等于形同虚设。
    /// </summary>
    public static TransferCode Generate() =>
        new(RandomNumberGenerator.GetInt32(0, ExclusiveUpper));

    public static TransferCode Parse(string text) =>
        TryParse(text, out var code)
            ? code
            : throw new FormatException($"不是合法的九位文件码：\"{text}\"。");

    /// <summary>
    /// 宽容解析：忽略连字符、空格、以及它们的全角形式，
    /// 并把全角数字折算成半角。用户是从聊天记录里复制或听着念的，
    /// 不该因为多一个空格就失败。
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out TransferCode code)
    {
        code = default;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var value = 0;
        var digits = 0;

        foreach (var c in text)
        {
            switch (c)
            {
                case '-' or ' ' or '\t' or '_' or '.':
                case '－' or '　' or '‐' or '–' or '—':
                    continue;
            }

            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= '０' and <= '９' => c - '０',
                _ => -1,
            };

            if (digit < 0)
            {
                return false;
            }

            if (++digits > DigitCount)
            {
                return false;
            }

            value = (value * 10) + digit;
        }

        if (digits != DigitCount)
        {
            return false;
        }

        code = new TransferCode(value);
        return true;
    }

    /// <summary>分组形式 <c>111-111-111</c>，便于口头传达。</summary>
    public override string ToString()
    {
        var digits = Digits;
        var builder = new StringBuilder(DigitCount + (DigitCount / GroupSize) - 1);

        for (var i = 0; i < DigitCount; i += GroupSize)
        {
            if (i > 0)
            {
                builder.Append('-');
            }

            builder.Append(digits, i, GroupSize);
        }

        return builder.ToString();
    }

    public bool Equals(TransferCode other) => _value == other._value;

    public override bool Equals(object? obj) => obj is TransferCode other && Equals(other);

    public override int GetHashCode() => _value;

    public static bool operator ==(TransferCode left, TransferCode right) => left.Equals(right);

    public static bool operator !=(TransferCode left, TransferCode right) => !left.Equals(right);
}
