using System.IO;
using Graft.Core;

namespace Graft.Features;

/// <summary>
/// 取り込み計画1件（ドロップまたはファイル選択ダイアログで渡された1件のトップレベル項目）。
/// 衝突の確認・別名の決定は<see cref="ExplorerViewModel"/>側でUIダイアログを介して行い済みの
/// 前提で、このクラスはコピーの実処理のみを担う（<see cref="FileImportService"/>のコメント参照）。
/// </summary>
public sealed record FileImportPlanItem
{
    /// <summary>コピー元の絶対パス（プロジェクト外の任意の場所）。</summary>
    public required string SourceFullPath { get; init; }

    /// <summary>コピー先の絶対パス（<see cref="PathGuard.ResolveImportTarget"/>で検証済み）。</summary>
    public required string DestinationFullPath { get; init; }

    /// <summary>コピー先のプロジェクトルートからの相対パス（'/'区切り、UI通知・ツリー選択に使う）。</summary>
    public required string DestinationRelativePath { get; init; }

    /// <summary>フォルダかどうか。フォルダの場合は再帰的にコピーする。</summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// 既に同名の項目が存在する場合に上書きしてよいか。事前にExplorerViewModelが利用者へ
    /// 確認済みの結果であり、このクラス自身は衝突の可否を判断しない（黙って上書きしないこと、
    /// という依頼の要件はUI層の確認ダイアログで担保する）。
    /// </summary>
    public bool Overwrite { get; init; }
}

/// <summary>1件の取り込み結果。</summary>
public sealed record FileImportItemOutcome
{
    public required FileImportPlanItem Item { get; init; }
    public bool IsSuccess { get; init; }

    /// <summary>取り込みの途中でキャンセルされ、この項目には手を付けていない（または着手前に中断した）ことを示す。</summary>
    public bool WasCancelled { get; init; }
    public GraftIssue? Issue { get; init; }
}

/// <summary>進捗（コピー済みファイル数／総ファイル数）。フォルダ内の個々のファイル単位で報告する。</summary>
public sealed record FileImportProgress(int CompletedFiles, int TotalFiles, string CurrentRelativePath);

/// <summary>
/// エクスプローラへの既存ファイル・フォルダの取り込み（利用者の指摘「ファイルをボタンや
/// ドラッグ＆ドロップでエクスプローラーに追加できない」への対応）を担う。WPFに依存しない
/// （<see cref="FileTreeService"/>と同じ方針。テストプロジェクトが直接コンパイルするため）。
///
/// <para>
/// 【常にコピー、移動ではない】 Windowsのエクスプローラは「同一ドライブ内のドラッグ＆ドロップは
/// 移動、異なるドライブ間はコピー」という慣習があるが、あえて従わず常にコピーする。取り込み元
/// （プロジェクト外の任意の場所）からファイルが消えてしまうと、誤操作の取り返しがつかない事故に
/// なりうるため（依頼の必須要件）。
/// </para>
/// <para>
/// 【安全機構の適用範囲】 プロジェクト外への書き込み・シンボリックリンク経由の脱出は
/// <see cref="PathGuard.ResolveImportTarget"/>で従来どおり防ぐ。一方、拡張子ホワイトリスト・
/// ファイルサイズ上限（<see cref="PathGuardOptions"/>）は取り込みには適用しない
/// （<see cref="PathGuard.ResolveImportTarget"/>のコメント参照。画像等の非テキスト資産を
/// 持ち込みたいという要望が主な動機であり、拡張子ホワイトリストの趣旨（AI/Graft自身による
/// テキスト書き込みの安全策）に合わないため）。ファイルサイズ上限を適用しない代わりに、
/// 大きなファイル・大量のファイルでもUIスレッドを塞がないこと（下記）・進捗表示・中断で
/// 対応する。
/// </para>
/// <para>
/// 【UIスレッドを塞がない】 実際のコピー（<see cref="ImportAsync"/>）は内部で<c>Task.Run</c>に
/// より丸ごとスレッドプールへ逃がす。1件あたりの失敗（権限不足・ディスク容量不足・コピー中に
/// ファイルが消えた等）は他の項目の処理を止めず、<see cref="FileImportItemOutcome"/>として
/// 個別に記録して呼び出し側（ExplorerViewModel）が部分的な成否を利用者へ伝えられるようにする。
/// </para>
/// <para>
/// 【中断】 <see cref="CancellationToken"/>を各ファイルのコピー前後でチェックする。中断された
/// 時点までにコピー済みのファイルは残す（部分的なロールバックは行わない。中断はあくまで
/// 「これ以上進めない」ためのものであり、依頼にも「大量のファイル・大きなファイルでUIが
/// 固まらないこと」「中止手段を検討すること」とあるのみで、原子性までは求められていないため、
/// 実装の複雑さとのバランスを取った）。未着手のトップレベル項目・進行中に中断されたトップ
/// レベル項目は<see cref="FileImportItemOutcome.WasCancelled"/>で示す。
/// </para>
/// </summary>
public sealed class FileImportService
{
    /// <summary>取り込み先の候補パスを検証する。プロジェクト外への脱出はここで拒否する（E201）。</summary>
    public static GraftResult<string> ResolveDestination(
        Project project, string targetRelativeDir, string name, PathGuardOptions guardOptions)
    {
        var guard = new PathGuard(project.Root, guardOptions);
        var relativePath = CombineRelative(targetRelativeDir, name);
        return guard.ResolveImportTarget(relativePath);
    }

    /// <summary>指定した絶対パスに、既にファイルまたはフォルダが存在するかどうか。</summary>
    public static bool DestinationExists(string fullPath)
    {
        var ioPath = LongPath.Extended(fullPath);
        return File.Exists(ioPath) || Directory.Exists(ioPath);
    }

    /// <summary>
    /// 計画済みの取り込み項目を実際にコピーする。<see cref="Task.Run(Func{Task})"/>相当で
    /// スレッドプールへ逃がすため、呼び出し元のUIスレッドを塞がない。
    /// </summary>
    public Task<IReadOnlyList<FileImportItemOutcome>> ImportAsync(
        IReadOnlyList<FileImportPlanItem> items,
        IProgress<FileImportProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Task.Run(() => ImportCore(items, progress, ct), CancellationToken.None);
    }

    private static IReadOnlyList<FileImportItemOutcome> ImportCore(
        IReadOnlyList<FileImportPlanItem> items, IProgress<FileImportProgress>? progress, CancellationToken ct)
    {
        var totalFiles = Math.Max(1, CountFiles(items));
        var completed = 0;
        var outcomes = new List<FileImportItemOutcome>(items.Count);

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested)
            {
                outcomes.Add(new FileImportItemOutcome { Item = item, IsSuccess = false, WasCancelled = true });
                continue;
            }

            try
            {
                if (item.IsDirectory)
                {
                    CopyDirectory(item.SourceFullPath, item.DestinationFullPath, item.Overwrite, ref completed, totalFiles, progress, ct);
                }
                else
                {
                    CopyFile(item.SourceFullPath, item.DestinationFullPath, item.Overwrite);
                    completed++;
                    progress?.Report(new FileImportProgress(completed, totalFiles, item.DestinationRelativePath));
                }
                outcomes.Add(new FileImportItemOutcome { Item = item, IsSuccess = true });
            }
            catch (OperationCanceledException)
            {
                outcomes.Add(new FileImportItemOutcome { Item = item, IsSuccess = false, WasCancelled = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var issue = GraftIssue.Of(ErrorCode.E402, ExceptionMessages.Describe(ex), path: item.DestinationRelativePath);
                outcomes.Add(new FileImportItemOutcome { Item = item, IsSuccess = false, Issue = issue });
            }
        }

        return outcomes;
    }

    /// <summary>進捗表示の分母（対象に含まれる全ファイル数。フォルダは中身を再帰的に数える）。</summary>
    private static int CountFiles(IReadOnlyList<FileImportPlanItem> items)
    {
        var count = 0;
        foreach (var item in items)
        {
            count += item.IsDirectory
                ? SafeCountFiles(item.SourceFullPath)
                : 1;
        }
        return count;
    }

    private static int SafeCountFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 数え上げの失敗は進捗表示の精度に影響するだけなので、実コピー側の例外処理に委ねる。
            return 0;
        }
    }

    private static void CopyFile(string sourceFullPath, string destFullPath, bool overwrite)
    {
        var destDir = Path.GetDirectoryName(destFullPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(LongPath.Extended(destDir));
        File.Copy(LongPath.Extended(sourceFullPath), LongPath.Extended(destFullPath), overwrite);
    }

    private static void CopyDirectory(
        string sourceDir, string destDir, bool overwrite, ref int completed, int totalFiles,
        IProgress<FileImportProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(LongPath.Extended(destDir));

        // 空フォルダも含めて先にディレクトリ構造を作る（DeleteUndoStore.CopyDirectoryRecursivelyと同じ方針）。
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(LongPath.Extended(Path.Combine(destDir, relative)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, relative);
            File.Copy(LongPath.Extended(file), LongPath.Extended(target), overwrite);
            completed++;
            progress?.Report(new FileImportProgress(completed, totalFiles, Path.GetRelativePath(sourceDir, file).Replace('\\', '/')));
        }
    }

    private static string CombineRelative(string relativeDir, string name)
        => string.IsNullOrEmpty(relativeDir) ? name : $"{relativeDir.TrimEnd('/')}/{name}";
}
