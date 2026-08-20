using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// 依頼4対応（診断用ログ）。「ネットワークドライブ上のプロジェクトですべてのブロックが
/// E101になる」という実機報告の原因切り分けのため、ドライラン完了時に対象ファイルごとの
/// 「解決した絶対パス」「存在するか」「読み取れたサイズ/行数」を1件1行でログへ残す。
/// ドライランはUIから明示的に起動される操作のためログの頻度は自然に抑えられており、
/// 適用（ApplyAsync）やUI再描画のたびには呼ばない（過剰なログにしないため。RunDryRunAsync
/// からのみ呼ぶ）。
/// <see cref="DryRunPlanner"/>・<see cref="ApplyEngine"/>自体はUIに依存しないため
/// <c>Graft.Infra.Logger</c>を引き回さず、呼び出し元であるMainViewModel側で記録する
/// （MainViewModel.Apply.csのLogger運用に倣う）。
/// </summary>
public sealed partial class MainViewModel
{
    private void LogDryRunFileProbes(DryRunResult dryRun, ApplyContext context)
    {
        foreach (var probe in dryRun.FileProbes)
        {
            var result = probe.Exists
                ? $"存在=あり 絶対パス={probe.FullPath}{FormatSizeOrLineCount(probe)}"
                : $"存在=なし 絶対パス={probe.FullPath}";
            Logger?.Info("dry-run-file-probe", result, targetPath: probe.Path, revision: context.Revision);
        }
    }

    private static string FormatSizeOrLineCount(DryRunFileProbe probe)
    {
        if (probe.LineCount is int lineCount) return $" 行数={lineCount}";
        if (probe.SizeBytes is long sizeBytes) return $" サイズ={sizeBytes}バイト";
        // 読み取り自体に失敗した場合（例: E204）は行数・サイズのどちらも取れない。
        return " 読み取り不可";
    }
}
