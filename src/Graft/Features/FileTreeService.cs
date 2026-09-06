using System.IO;
using System.Text;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;

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
    private readonly ITrashService? _trash;

    /// <param name="trash">
    /// ごみ箱への削除（<see cref="DeleteAsync"/>）。省略時（テスト等）はごみ箱を使わず常に
    /// 通常削除する（10件目の不具合修正。従来はWindows専用の<c>Core.RecycleBin</c>を
    /// <c>OperatingSystem.IsWindows()</c>で直呼びしており、Linuxでは<c>LinuxTrashService</c>が
    /// 一度も呼ばれず「ごみ箱への削除」が常に完全削除にフォールバックしていた。呼び出し元
    /// （ExplorerViewModel）は実行中のOSに応じた実装を <c>PlatformServices.Current.Trash</c>
    /// から渡す）。
    /// </param>
    public FileTreeService(ITrashService? trash = null)
    {
        _trash = trash;
    }

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
        var nameIssue = ValidateEntryName(fileName);
        if (nameIssue is not null) return GraftResult<string>.Fail(nameIssue);

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
        var nameIssue = ValidateEntryName(folderName);
        if (nameIssue is not null) return Task.FromResult(GraftResult<string>.Fail(nameIssue));

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

    /// <summary>
    /// ファイルまたはフォルダをリネームする（同じ親フォルダ内での名前変更のみ）。
    ///
    /// 異常系点検「低」4件目の対応: <paramref name="newName"/>は「新しい親フォルダを含む
    /// パス」ではなく、常に「今の親フォルダ内でのファイル名/フォルダ名1つ分」として扱う。
    /// 以前はこのdocコメントが実装と食い違っており、<paramref name="newName"/>に
    /// <c>"dir/moved.txt"</c>のようなパス区切りを含む文字列を渡すと、<see cref="PathGuard.Resolve"/>
    /// 自体は「妥当な相対パス」として素通しするため、実際に別フォルダ（dir/）へ移動できて
    /// しまっていた（実測で確認済み）。<see cref="ValidateEntryName"/>で
    /// <paramref name="newName"/>自体にパス区切りを含められないよう検証することで、
    /// このメソッドが「同じ親フォルダ内での名前変更のみ」であることをdocコメントどおり
    /// 実装でも保証する。
    /// </summary>
    public Task<GraftResult<string>> RenameAsync(
        Project project, string oldRelativePath, string newName, bool isDirectory, PathGuardOptions guardOptions)
    {
        var nameIssue = ValidateEntryName(newName);
        if (nameIssue is not null) return Task.FromResult(GraftResult<string>.Fail(nameIssue));

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

    /// <summary>ファイルまたはフォルダを削除する。常にごみ箱経由（未対応環境は通常削除、仕様書14章）。</summary>
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
            // 10件目の不具合修正: 従来はWindows専用のRecycleBinを直呼びしており、Linuxでは
            // 常に通常削除（完全削除）にフォールバックしていた。ITrashService経由に揃え、
            // ごみ箱へ送れない・未対応（_trashがnullまたはSendが失敗）の場合のみ通常削除する。
            if (_trash is null || !_trash.Send(fullPath))
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

    /// <summary>
    /// 異常系点検「低」4件目の対応: 実行時のOSが返す不許可文字一覧
    /// （<see cref="Path.GetInvalidFileNameChars"/>）から ':' を除いたもの。Windows実機では
    /// コロン・アスタリスク等多数を返すが、Linux上ではNUL文字と'/'（本メソッドでは別途
    /// <see cref="PathSeparators"/>で検証済み）程度しか返らない（実機・開発機の違いにより
    /// 検証範囲が変わる点はクラスコメント末尾の実測メモ参照）。
    ///
    /// 【':' を除く理由（PathGuardとのメッセージ重複解消）】 v1.0.15セキュリティ対応
    /// （<see cref="PathGuard"/>の穴3対応・<see cref="Core.ErrorCode.E212"/>）で、代替データ
    /// ストリーム記法（<c>evil.exe:payload.txt</c>）による拡張子ホワイトリスト回避を防ぐため、
    /// パスのセグメントに ':' を含む場合を専用のE212（「Windowsの予約文字で、代替データ
    /// ストリームの指定と解釈されます」という具体的な理由付き）で拒否する経路が追加された。
    /// もしここでも ':' を「使用できない文字」として一緒くたに拒否してしまうと、Windows実機
    /// では常にこちら（E214、一般的な文言）が先に発火してPathGuard側のE212（より具体的な
    /// 理由）へ絶対に到達しなくなり、Linux開発機とWindows実機とで表示される理由が食い違う
    /// （実機だけ情報量の少ないメッセージになる）事故になる。':' の判定はPathGuard側の
    /// 専用チェックに委ね、ここでは残りの文字（Windowsでは<c>&lt; &gt; " | ? *</c>等）だけを見る。
    /// staticフィールドとして1回だけ取得する（実行中にOSが変わることはないため）。
    /// </summary>
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars().Where(c => c != ':').ToArray();

    private static readonly char[] PathSeparators = { '/', '\\' };

    /// <summary>
    /// 新規作成（<see cref="CreateFileAsync"/>・<see cref="CreateFolderAsync"/>）・名前の変更
    /// （<see cref="RenameAsync"/>）で渡される「ファイル名/フォルダ名1つ分」（ちょうど1階層の
    /// 名前で、パスであってはならない）を検証する。
    ///
    /// 【なぜここでも検証するのか】<see cref="PathGuard.Resolve(string)"/>は「相対パス全体」の
    /// 検証（ルート外参照・上位ディレクトリ参照(..)・絶対パス・拡張子ホワイトリスト等）を
    /// 担うが、name自体にパス区切り（"/" "\"）が混じっていても、それ自体は「妥当な相対パス」
    /// として素通ししてしまう。実測で確認した2つの不具合はどちらもこれが原因だった:
    /// <list type="bullet">
    /// <item>名前の変更で<c>newName="dir/moved.txt"</c>を渡すと、<see cref="RenameAsync"/>の
    /// docコメント（「同じ親フォルダ内での名前変更のみ」）に反してdir/へ実際に移動できた。</item>
    /// <item>新規ファイル作成で<c>fileName="sub/child.txt"</c>（subが存在しない）を渡すと、
    /// <see cref="PathGuard"/>までは通過し、<see cref="FileTextIO.WriteAsync"/>の一時ファイル
    /// 書き込みが「フォルダが見つかりません」で失敗する際、そのエラーメッセージに内部の
    /// 一時ファイル名（<c>*.graft-tmp-&lt;guid&gt;</c>）がそのまま利用者に見えていた。</item>
    /// </list>
    /// ここで1階層の名前として弾くことで、両方を「そもそも1階層の名前としては使えません」
    /// という、原因を正しく言い当てた日本語メッセージへ統一する。
    /// </summary>
    private static GraftIssue? ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return GraftIssue.Of(ErrorCode.E214, "名前を入力してください。");
        }

        if (name.IndexOfAny(PathSeparators) >= 0)
        {
            return GraftIssue.Of(ErrorCode.E214,
                "名前に \"/\" や \"\\\" を含めることはできません（サブフォルダの指定はできません）。", path: name);
        }

        // 実測: Linux上では"a\nb.txt"・"a\tb.txt"のような制御文字混じりの名前がそのまま
        // 作成でき、ツリー表示が改行で崩れる不具合を確認した。改行・タブ・NUL等の制御文字は
        // どのプラットフォームでも「見た目のファイル名」として意味を成さないため一律に拒否する
        // （char.IsControlはNUL・タブ・改行等のC0/C1制御文字をすべて対象にする）。
        if (name.Any(char.IsControl))
        {
            return GraftIssue.Of(ErrorCode.E214, "名前に改行やタブなどの制御文字を含めることはできません。", path: name);
        }

        if (name.IndexOfAny(InvalidNameChars) >= 0)
        {
            return GraftIssue.Of(ErrorCode.E214, "名前に使用できない文字が含まれています。", path: name);
        }

        // 実測: "a.txt "（末尾に半角空白）を新規作成すると、実際の問題（末尾の空白）とは
        // 無関係に「拡張子 '.txt ' は許可されていません」という誤解を招くメッセージに
        // なっていた（Path.GetExtensionが空白込みの".txt "を拡張子として切り出すため）。
        // 拡張子ホワイトリストの判定（PathGuard.Resolve）より前にここで検出し、実際の問題
        // （末尾の空白）を言い当てる。
        if (name.Length != name.TrimEnd().Length)
        {
            return GraftIssue.Of(ErrorCode.E214, "名前の末尾に空白を含めることはできません。", path: name);
        }

        // Windowsのエクスプローラ自体が末尾ピリオドの名前を拒否する（NTFSの制約に由来）。
        // Linux上では作成できてしまうため、クロスプラットフォームで「一方の環境でだけ
        // 開けない名前」を作ってしまわないよう、ここで事前に弾く。
        if (name.EndsWith('.'))
        {
            return GraftIssue.Of(ErrorCode.E214, "名前の末尾にピリオド(.)を含めることはできません。", path: name);
        }

        // 実測: 300文字の名前で「予期しないエラーが発生しました…（詳細: The path '…' is too
        // long...）」という英語の生の例外メッセージがそのまま利用者に表示されていた。
        // 255は主要なファイルシステム（NTFS・大半のLinuxファイルシステム）に共通する
        // 1コンポーネントあたりの上限に合わせた値で、OS例外に到達する前に日本語で理由を伝える。
        if (name.Length > 255)
        {
            return GraftIssue.Of(ErrorCode.E214, $"名前が長すぎます（{name.Length}文字）。255文字以内にしてください。", path: name);
        }

        return null;
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
