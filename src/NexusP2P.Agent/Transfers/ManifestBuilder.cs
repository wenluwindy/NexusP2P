using System.IO.Enumeration;
using NexusP2P.Core.Hashing;
using NexusP2P.Core.Manifest;

namespace NexusP2P.Agent.Transfers;

/// <summary>
/// 从磁盘上的文件或文件夹构建传输清单。
///
/// <para>顶层名字包含在路径里：发送 <c>MyStuff</c> 得到 <c>MyStuff/a.txt</c>，
/// 这样接收端能自然重建目录结构，而不是把一堆文件散落进下载目录。</para>
/// </summary>
public static class ManifestBuilder
{
    /// <summary>
    /// 扫描并计算清单。<paramref name="progress"/> 报告已哈希的字节数 ——
    /// 20 GB 要跑十几秒，不能让界面看起来卡死。
    /// </summary>
    public static async Task<TransferManifest> BuildAsync(
        string path,
        MerkleParameters? parameters = null,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var merkle = parameters ?? MerkleParameters.Default;
        var full = Path.GetFullPath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (File.Exists(full))
        {
            var entry = await HashFileAsync(full, Path.GetFileName(full), merkle, progress, 0, cancellationToken)
                .ConfigureAwait(false);
            return TransferManifest.Create(merkle, [entry]);
        }

        if (!Directory.Exists(full))
        {
            throw new FileNotFoundException($"找不到文件或文件夹：{full}");
        }

        var parent = Path.GetDirectoryName(full)
                     ?? throw new ArgumentException($"无法取得 \"{full}\" 的上级目录。", nameof(path));

        var entries = new List<ManifestEntry>();
        var emptyDirectories = new List<string>();
        long hashedSoFar = 0;

        foreach (var file in EnumerateFiles(full))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(parent, file);
            if (!SafePath.TryToManifestPath(relative, out var manifestPath, out var error))
            {
                throw new InvalidOperationException($"路径 \"{relative}\" 不能用于传输：{error}");
            }

            var entry = await HashFileAsync(file, manifestPath, merkle, progress, hashedSoFar, cancellationToken)
                .ConfigureAwait(false);

            hashedSoFar += entry.Length;
            entries.Add(entry);
        }

        // 底下完全没有文件的目录要显式列出来，否则传过去就没了
        foreach (var directory in EnumerateDirectories(full))
        {
            if (EnumerateFiles(directory).Any())
            {
                continue;
            }

            var relative = Path.GetRelativePath(parent, directory);
            if (SafePath.TryToManifestPath(relative, out var manifestPath, out _))
            {
                emptyDirectories.Add(manifestPath);
            }
        }

        // 清单至少要有一个条目。全是空目录的文件夹产不出合法清单 ——
        // 明确报错，而不是让用户对着一个奇怪的失败发愣。
        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                emptyDirectories.Count == 0
                    ? $"文件夹 \"{full}\" 是空的，没有可传的内容。"
                    : $"文件夹 \"{full}\" 里只有空目录，没有文件可传。");
        }

        return TransferManifest.Create(merkle, entries, emptyDirectories);
    }

    /// <summary>
    /// 不跟随符号链接与重解析点：跟随会带来两个问题 ——
    /// 目录环导致无限递归，以及把用户根本没打算发的东西卷进来。
    /// </summary>
    private static FileSystemEnumerable<string> EnumerateFiles(string root) =>
        new FileSystemEnumerable<string>(
            root,
            (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
        };

    private static FileSystemEnumerable<string> EnumerateDirectories(string root) =>
        new FileSystemEnumerable<string>(
            root,
            (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.IsDirectory,
        };

    private static async Task<ManifestEntry> HashFileAsync(
        string diskPath,
        string manifestPath,
        MerkleParameters parameters,
        IProgress<long>? progress,
        long alreadyHashed,
        CancellationToken cancellationToken)
    {
        using var hasher = new FileHasher(parameters);
        await using var stream = new FileStream(
            diskPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, useAsync: true);

        var result = await hasher
            .ComputeAsync(
                stream,
                progress is null ? null : new Progress<long>(read => progress.Report(alreadyHashed + read)),
                cancellationToken)
            .ConfigureAwait(false);

        return ManifestEntry.FromHashResult(manifestPath, result);
    }
}

