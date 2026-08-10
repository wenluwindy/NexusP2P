using NexusP2P.Core.Manifest;

namespace NexusP2P.Core.Tests.Manifest;

/// <summary>
/// 路径安全的对抗性测试。这是本项目唯一的高危安全面 ——
/// 一条被放过的 <c>../</c> 就等于任意文件写入。
/// </summary>
public sealed class SafePathTests
{
    [Theory]
    // 经典的目录穿越
    [InlineData("../secret.txt")]
    [InlineData("../../Windows/System32/evil.dll")]
    [InlineData("a/../../b")]
    [InlineData("a/b/../../../c")]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("a/..")]
    // 当前目录段
    [InlineData(".")]
    [InlineData("./a")]
    [InlineData("a/./b")]
    // 绝对路径
    [InlineData("/etc/passwd")]
    [InlineData("/a")]
    // 反斜杠：Windows 会把它当分隔符，只按 '/' 切分的校验看不见这种穿越
    [InlineData("..\\secret.txt")]
    [InlineData("a\\..\\..\\b")]
    [InlineData("C:\\Windows\\x")]
    [InlineData("a\\b")]
    // 盘符与 NTFS 备用数据流
    [InlineData("C:/Windows/x")]
    [InlineData("file.txt:hidden")]
    [InlineData("a/b:c")]
    // 空段
    [InlineData("a//b")]
    [InlineData("/")]
    [InlineData("a/")]
    [InlineData("")]
    // Windows 保留设备名
    [InlineData("NUL")]
    [InlineData("nul")]
    [InlineData("CON")]
    [InlineData("PRN.txt")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("LPT9.dat")]
    [InlineData("a/NUL/b")]
    [InlineData("dir/con.txt")]
    // 结尾的点与空格：Windows 会静默去掉，导致校验目标与落地目标不一致
    [InlineData("a.")]
    [InlineData("a ")]
    [InlineData("dir./file")]
    [InlineData("dir /file")]
    // 控制字符
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\u007fb")]
    // Windows 文件名非法字符
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    // 双向文本控制字符：能把 photo_txt.exe 显示成 photo_exe.txt
    [InlineData("photo_\u202Etxt.exe")]
    [InlineData("a\u200Eb")]
    [InlineData("a\u2066b")]
    public void 危险路径被拒绝(string path)
    {
        Assert.False(SafePath.IsSafe(path, out var error), $"路径 \"{path}\" 本应被拒绝");
        Assert.False(string.IsNullOrWhiteSpace(error), "拒绝时必须给出可读原因");
    }

    [Fact]
    public void 空路径被拒绝()
    {
        Assert.False(SafePath.IsSafe(null, out _));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("dir/a.txt")]
    [InlineData("a/b/c/d.bin")]
    [InlineData("我的文档/照片.jpg")]
    [InlineData("with space/file name.txt")]
    [InlineData("dots.in.name.tar.gz")]
    [InlineData(".hidden")]
    [InlineData("a/.hidden")]
    [InlineData("..hidden")]          // 两个点开头但不是 ".." 段
    [InlineData("CONSOLE.txt")]       // 以 CON 开头但不是保留名
    [InlineData("NULLABLE.cs")]
    [InlineData("COM10")]             // 只有 COM1~COM9 是保留名
    [InlineData("emoji_🎉.png")]
    public void 正常路径被接受(string path)
    {
        Assert.True(SafePath.IsSafe(path, out var error), $"路径 \"{path}\" 本应被接受，却报 {error}");
        Assert.Null(error);
    }

    [Fact]
    public void 超长路径被拒绝()
    {
        var tooLong = new string('a', SafePath.MaxPathLength + 1);

        Assert.False(SafePath.IsSafe(tooLong, out _));
    }

    [Fact]
    public void 超长路径段被拒绝()
    {
        var path = "dir/" + new string('a', SafePath.MaxSegmentLength + 1);

        Assert.False(SafePath.IsSafe(path, out _));
    }

    [Fact]
    public void 恰好到上限的路径段被接受()
    {
        var path = "dir/" + new string('a', SafePath.MaxSegmentLength);

        Assert.True(SafePath.IsSafe(path, out _));
    }

    // ---- ResolveWithin ----

    [Fact]
    public void 解析后的路径位于根目录内()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-test-root");

        var resolved = SafePath.ResolveWithin(root, "sub/dir/file.txt");

        Assert.StartsWith(Path.GetFullPath(root), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("file.txt", resolved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:\\Windows\\x")]
    public void 解析危险路径会抛_UnsafePathException(string path)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-test-root");

        var ex = Assert.Throws<UnsafePathException>(() => SafePath.ResolveWithin(root, path));
        Assert.Equal(path, ex.Path);
        Assert.False(string.IsNullOrWhiteSpace(ex.Reason));
    }

    [Fact]
    public void 根目录为空会被拒绝()
    {
        Assert.Throws<ArgumentException>(() => SafePath.ResolveWithin("", "a.txt"));
    }

    [Fact]
    public void 相邻的同前缀目录不会被误判为在根目录内()
    {
        // 若用朴素的字符串前缀比较，"C:\root-evil\x" 会被当成在 "C:\root" 之内。
        // 加上分隔符再比较可以避免这个经典错误。这里通过解析一条正常路径、
        // 确认结果带上了分隔符来间接验证。
        var root = Path.Combine(Path.GetTempPath(), "nexus-root");

        var resolved = SafePath.ResolveWithin(root, "a.txt");

        var expectedPrefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        Assert.StartsWith(expectedPrefix, resolved, StringComparison.OrdinalIgnoreCase);
    }

    // ---- TryToManifestPath ----

    [Fact]
    public void 本机反斜杠路径被转成正斜杠()
    {
        Assert.True(SafePath.TryToManifestPath(@"dir\sub\file.txt", out var manifestPath, out _));

        Assert.Equal("dir/sub/file.txt", manifestPath);
    }

    [Fact]
    public void 转换会去掉首尾斜杠()
    {
        Assert.True(SafePath.TryToManifestPath(@"\dir\file.txt\", out var manifestPath, out _));

        Assert.Equal("dir/file.txt", manifestPath);
    }

    [Fact]
    public void 转换后仍然会做安全校验()
    {
        // 发送方也不该造出穿越路径 —— 转换不是绕过校验的后门
        Assert.False(SafePath.TryToManifestPath(@"..\..\secret.txt", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void 空的本机路径被拒绝()
    {
        Assert.False(SafePath.TryToManifestPath(null, out _, out _));
        Assert.False(SafePath.TryToManifestPath("", out _, out _));
    }
}
