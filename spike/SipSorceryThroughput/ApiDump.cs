using System.Reflection;
using System.Text;

namespace SipSorceryThroughput;

/// <summary>
/// 临时工具：把 SIPSorcery 关键类型的成员（含非公开静态常量）打印出来。
/// 用法：--api [类型全名 ...]，不给类型则打印默认那批。
/// </summary>
internal static class ApiDump
{
    private static readonly string[] Default =
    [
        "SIPSorcery.Net.RTCDataChannel",
        "SIPSorcery.Net.RTCPeerConnection",
        "SIPSorcery.Net.RTCConfiguration",
        "SIPSorcery.Net.RTCDataChannelInit",
        "SIPSorcery.Net.RTCSessionDescriptionInit",
        "SIPSorcery.Net.RTCIceCandidateInit",
    ];

    public static void Run(string[] requested)
    {
        var names = requested.Length > 0 ? requested : Default;

        foreach (var typeName in names)
        {
            var type = Type.GetType($"{typeName}, SIPSorcery");
            if (type is null)
            {
                Console.WriteLine($"### {typeName}  ==> NOT FOUND");
                Console.WriteLine();
                continue;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"### {type.FullName}");

            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                                         | BindingFlags.Instance | BindingFlags.Static;

            // 常量和静态字段最有价值：节拍周期、初始窗口这类调参旋钮通常藏在这里。
            foreach (var f in type.GetFields(All).Where(f => f.IsStatic).OrderBy(f => f.Name))
            {
                var value = "";
                try
                {
                    if (f.IsLiteral || f.IsInitOnly) value = $" = {f.GetValue(null)}";
                }
                catch
                {
                    // 需要实例或初始化失败，忽略
                }

                sb.AppendLine($"  static {(f.IsLiteral ? "const" : "     ")} {Simple(f.FieldType),-22} {f.Name}{value}");
            }

            foreach (var f in type.GetFields(All).Where(f => !f.IsStatic).OrderBy(f => f.Name))
            {
                sb.AppendLine($"  field  {(f.IsPublic ? "pub " : "priv")} {Simple(f.FieldType),-22} {f.Name}");
            }

            foreach (var p in type.GetProperties(All).OrderBy(p => p.Name))
            {
                sb.AppendLine($"  prop        {Simple(p.PropertyType),-22} {p.Name}");
            }

            foreach (var m in type.GetMethods(All)
                         .Where(m => !m.IsSpecialName && m.DeclaringType == type)
                         .OrderBy(m => m.Name))
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => $"{Simple(p.ParameterType)} {p.Name}"));
                sb.AppendLine($"  meth   {(m.IsPublic ? "pub " : "priv")} {Simple(m.ReturnType),-22} {m.Name}({ps})");
            }

            Console.WriteLine(sb.ToString());
        }
    }

    private static string Simple(Type t)
    {
        if (t.IsByRef) return Simple(t.GetElementType()!) + "&";
        if (!t.IsGenericType) return t.Name;
        var args = string.Join(",", t.GetGenericArguments().Select(Simple));
        return $"{t.Name[..t.Name.IndexOf('`')]}<{args}>";
    }
}
