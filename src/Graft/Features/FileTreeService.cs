using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Graft.Core;
using Graft.Infra;

namespace Graft.Features;

/// <summary>エクスプローラの1ノード（ファイルまたはディレクトリ）。仕様書4.2。</summary>
public sealed record FileTreeEntry
{
    /// <summary>ファイル名またはフォルダ名のみ。</summary>
    public required string Name { get; init; }

    /// <summary>プロジェクトルートからの相対パス（区切りは "/"）。</summary>
    public required string RelativePath { get; init; }

    /// <summary>絶対パス。</summary>
    public required string FullPath { get; init; }

    /// <summary>ディレクトリかどうか。</summary>
    public bool IsDirectory { get; init; }

    /// <summary>除外規則により除外されているか。</summary>
    public bool IsExcluded { get; init; }

    /// <summary>除外理由（UI表示用）。</summary>
    public string? ExcludeReason { get; init; }
}

/// <summary>
/// エクスプローラ（仕様書4.2）のツリー列挙・除外規則適用・ファイル操作を担う。WPFに依存しない
/// （テストプロジェクトが net8.0 として直接コンパイルするため）。除外判定は
/// <see cref="GitignoreFilter"/> をそのまま使い、<see cref="Graft.Features.ContextCollector"/> と
/// 同じ規則（既定パターン＋.gitignore＋<see cref="ProjectOverrides.Excludes"/>）を適用することで
/// 判定ロジックの二重実装を避ける。ContextCollector.ScanAsync がツリー全体を一括走査するのに対し、
/// 本クラスは大きなフォルダで固まらないよう1階層ずつ遅延列挙する（仕様書4.2の遅延読み込み）。
/// </summary>
public sealed class FileTreeService
{
    /// <summary>コンテキスト収集と共通の除外規則からフィルタを構築する（仕様書4.2・10.2）。</summary>
    public static async Task<GitignoreFilter> BuildFilterAsync(Project project, CancellationToken ct = default)
    {
        var defaultFilter = GitignoreFilter.FromPatterns(ContextCollector.DefaultExcludePatterns, "既定除外");
        var gitignoreFilter = await GitignoreFilter.LoadAsync(project.Root, ct).ConfigureAwait(false);
        var overrideFilter = GitignoreFilter.FromPatterns(project.Overrides.Excludes, "プロジェクト設定");
        return defaultFilter.Merge(gitignoreFilter).Merge(overrideFilter);
    }

    /// <summary>
    /// 指定ディレクトリ（プロジェクトルートからの相対パス。ルート自身は空文字列）直下の子要素を
    /// 列挙する。フォルダ優先・名前順（仕様書4.2）。子孫は列挙しない（遅延読み込み）。
    /// </summary>
    public Task<GraftResult<IReadOnlyList<FileTreeEntry>>> ListChildrenAsync(
        Project project, string relativeDir, GitignoreFilter filter, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dirFullPath = string.IsNullOrEmpty(relativeDir)
            ? project.Root
            : Path.Combine(project.Root, relativeDir.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(dirFullPath))
        {
            return Task.FromResult(GraftResult<IReadOnlyList<FileTreeEntry>>.Fail(
                ErrorCode.E201, "フォルダが見つかりません", path: relativeDir));
        }

        try
        {
            var dirs = Directory.EnumerateDirectories(dirFullPath)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(d => BuildEntry(project.Root, d, isDirectory: true, filter));
            var files = Directory.EnumerateFiles(dirFullPath)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(f => BuildEntry(project.Root, f, isDirectory: false, filter));
            IReadOnlyList<FileTreeEntry> entries = dirs.Concat(files).ToList();
            return Task.FromResult(GraftResult<IReadOnlyList<FileTreeEntry>>.Ok(entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(GraftResult<IReadOnlyList<FileTreeEntry>>.Fail(
                ErrorCode.E204, ExceptionMessages.Describe(ex), path: relativeDir));
        }
    }

    /// <summary>新規ファイルを作成する（空の内容）。プロジェクトの既定エンコーディングを反映する。</summary>
    public async Task<GraftResult<string>> CreateFileAsync(
        Project project, string relativeDir, string fileName, PathGuardOptions guardOptions, CancellationToken ct = default)
    {
        var guard = new PathGuard(project.Root, guardOptions);
        var relativePath = CombineRelative(relativeDir, fileName);
        var resolved = guard.Resolve(relativePath);
        if (!resolved.IsSuccess) return GraftResult<string>.Fail(resolved.Issues);
        if (File.Exists(resolved.Value))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "同名のファイルが既に存在します", path: relativePath);
        }

        var write = await FileTextIO.WriteAsync(resolved.Value, string.Empty, BuildNewFileShape(project), ct)
            .ConfigureAwait(false);
        return write.IsSuccess ? GraftResult<string>.Ok(relativePath, write.Issues) : GraftResult<string>.Fail(write.Issues);
    }

    /// <summary>
    /// 新規フォルダを作成する。フォルダには拡張子ホワイトリストを適用しない
    /// （<see cref="PathGuard.ResolveDirectory"/>、仕様書14章）。
    /// </summary>
    public Task<GraftResult<string>> CreateFolderAsync(
        Project project, string relativeDir, string folderName, PathGuardOptions guardOptions)
    {
        var guard = new PathGuard(project.Root, guardOptions);
        var relativePath = CombineRelative(relativeDir, folderName);
        var resolved = guard.ResolveDirectory(relativePath);
        if (!resolved.IsSuccess) return Task.FromResult(GraftResult<string>.Fail(resolved.Issues));
        if (Directory.Exists(resolved.Value) || File.Exists(resolved.Value))
        {
            return Task.FromResult(GraftResult<string>.Fail(
                ErrorCode.E201, "同名のフォルダまたはファイルが既に存在します", path: relativePath));
        }

        return Task.FromResult(TryRun(() =>
        {
            Directory.CreateDirectory(LongPath.Extended(resolved.Value));
            return relativePath;
        }, relativePath));
    }

    /// <summary>ファイルまたはフォルダをリネームする（同じ親フォルダ内での名前変更のみ、仕様書4.2）。</summary>
    public Task<GraftResult<string>> RenameAsync(
        Project project, string oldRelativePath, string newName, bool isDirectory, PathGuardOptions guardOptions)
    {
        var guard = new PathGuard(project.Root, guardOptions);
        var oldResolved = isDirectory ? guard.ResolveDirectory(oldRelativePath) : guard.Resolve(oldRelativePath);
        if (!oldResolved.IsSuccess) return Task.FromResult(GraftResult<string>.Fail(oldResolved.Issues));

        var newRelativePath = CombineRelative(GetParentRelative(oldRelativePath), newName);
        var newResolved = isDirectory ? guard.ResolveDirectory(newRelativePath) : guard.Resolve(newRelativePath);
        if (!newResolved.IsSuccess) return Task.FromResult(GraftResult<string>.Fail(newResolved.Issues));
        if (Directory.Exists(newResolved.Value) || File.Exists(newResolved.Value))
        {
            return Task.FromResult(GraftResult<string>.Fail(ErrorCode.E201, "同名の項目が既に存在します", path: newRelativePath));
        }

        return Task.FromResult(TryRun(() =>
        {
            var from = LongPath.Extended(oldResolved.Value);
            var to = LongPath.Extended(newResolved.Value);
            if (isDirectory) Directory.Move(from, to); else File.Move(from, to);
            return newRelativePath;
        }, oldRelativePath));
    }

    /// <summary>ファイルまたはフォルダを削除する。常にごみ箱経由（非Windowsは通常削除、仕様書14章）。</summary>
    public Task<GraftResult<bool>> DeleteAsync(
        Project project, string relativePath, bool isDirectory, PathGuardOptions guardOptions)
    {
        var guard = new PathGuard(project.Root, guardOptions);
        var resolved = isDirectory ? guard.ResolveDirectory(relativePath) : guard.Resolve(relativePath);
        if (!resolved.IsSuccess) return Task.FromResult(GraftResult<bool>.Fail(resolved.Issues));

        var fullPath = resolved.Value;
        var exists = isDirectory ? Directory.Exists(fullPath) : File.Exists(fullPath);
        if (!exists) return Task.FromResult(GraftResult<bool>.Ok(true));

        try
        {
            if (!OperatingSystem.IsWindows() || !RecycleBin.Send(fullPath))
            {
                if (isDirectory) Directory.Delete(LongPath.Extended(fullPath), recursive: true);
                else File.Delete(LongPath.Extended(fullPath));
            }
            return Task.FromResult(GraftResult<bool>.Ok(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(GraftResult<bool>.Fail(ErrorCode.E204, ExceptionMessages.Describe(ex), path: relativePath));
        }
    }

    /// <summary>設定からPathGuardの検証オプションを組み立てる。</summary>
    public static PathGuardOptions BuildGuardOptions(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var safety = settings.Safety;
        return new PathGuardOptions
        {
            AllowedExtensions = safety.AllowedExtensions,
            MaxFileSizeMB = safety.MaxFileSizeMB,
            MaxFilesPerRevision = safety.MaxFilesPerRevision,
        };
    }

    /// <summary>Windowsのエクスプローラで対象を選択表示する。Windows以外では何もしない（仕様書4.2）。</summary>
    public static void RevealInFileExplorer(string fullPath)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // エクスプローラの起動失敗は致命的ではないため無視する。
        }
    }

    private static GraftResult<string> TryRun(Func<string> action, string errorPath)
    {
        try
        {
            return GraftResult<string>.Ok(action());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<string>.Fail(ErrorCode.E204, ExceptionMessages.Describe(ex), path: errorPath);
        }
    }

    private static FileTreeEntry BuildEntry(string root, string fullPath, bool isDirectory, GitignoreFilter filter)
    {
        var rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var (ignored, label) = filter.Evaluate(rel, isDirectory);
        return new FileTreeEntry
        {
            Name = Path.GetFileName(fullPath),
            RelativePath = rel,
            FullPath = fullPath,
            IsDirectory = isDirectory,
            IsExcluded = ignored,
            ExcludeReason = ignored ? ReasonOf(label) : null,
        };
    }

    private static string? ReasonOf(string? label) => label switch
    {
        "既定除外" => "既定の除外パターンに一致",
        ".gitignore" => ".gitignoreに一致",
        "プロジェクト設定" => "プロジェクト設定の除外パターンに一致",
        _ => "除外パターンに一致",
    };

    private static TextShape BuildNewFileShape(Project project)
    {
        var encoding = string.Equals(project.Overrides.NewFileEncoding, "shift_jis", StringComparison.OrdinalIgnoreCase)
            ? GetShiftJisEncoding()
            : new UTF8Encoding(false);
        return new TextShape { Encoding = encoding, HasBom = false, NewLine = "\r\n", EndsWithNewLine = true };
    }

    private static Encoding GetShiftJisEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    private static string CombineRelative(string relativeDir, string name)
        => string.IsNullOrEmpty(relativeDir) ? name : $"{relativeDir.TrimEnd('/')}/{name}";

    private static string GetParentRelative(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimEnd('/');
        var idx = normalized.LastIndexOf('/');
        return idx < 0 ? string.Empty : normalized[..idx];
    }
}
