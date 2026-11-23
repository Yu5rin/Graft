using System.IO;
using System.Text;
using System.Windows.Input;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ProjectPaneViewModel"/> の分割ファイル。リボン（customUI）の書き換えの手動の入口（仕様書_ブックの情報と依頼の観点 16.10・
/// 16.13 の UI。段階 F3）: 右クリックとコマンドパレットの「リボンをブックへ反映…」「リボンの XML を検査」と、途中で終わった書き換えの
/// 起動時の後始末の知らせ。
/// <para>
/// 【段階 F3 の範囲】 Excel で開いていないブックだけを書き換える。開いているブックは、サービスが「Excel でブックを閉じてから」の
/// 案内（E819）で止めるので、ここでは失敗の知らせに「もう一度試す」を添えるだけにする（閉じてから押し直せるように）。
/// 適用・取り消し・手動の「Excel へ反映」のあとに自動で確認を出す組み込み（16.10）は段階 F5。
/// </para>
/// </summary>
public sealed partial class ProjectPaneViewModel
{
    // 起動してから、途中で終わった書き換えの後始末を確かめたプロジェクト（1 回の起動につき 1 回だけ知らせる）。
    private readonly HashSet<string> _ribbonRecoveryChecked = new(StringComparer.Ordinal);

    /// <summary>「リボンをブックへ反映…」（16.13）。リボンが未反映のときだけ有効。</summary>
    public ICommand PushRibbonCommand { get; private set; } = null!;

    /// <summary>「リボンの XML を検査」（16.8 の手動の検査）。</summary>
    public ICommand InspectRibbonCommand { get; private set; } = null!;

    /// <summary>
    /// 確認ダイアログ（<c>VbaRibbonRewriteWindow</c>。16.9）を出して、「書き換える」を選んだかを返す（View が配線する）。
    /// 未設定なら「中止」として扱う（確認を出せないときに書き換えない）。
    /// </summary>
    public Func<VbaRibbonRewriteViewModel, Task<bool>>? ShowRibbonRewriteAsync { get; set; }

    /// <summary>確認ダイアログの ViewModel を作る（<c>MainViewModel</c> が設定と UI 機能を渡して配線する）。</summary>
    public Func<VbaRibbonRewritePlan, VbaRibbonRewriteViewModel?>? CreateRibbonRewriteViewModel { get; set; }

    /// <summary>フォルダを開く（E823 の「控えのフォルダを開く」）。既定はファイルマネージャ。テストで差し替える。</summary>
    public Action<string> RevealFolder { get; set; } = static path => PlatformServices.Current.FileManager.Reveal(path);

    private void InitializeVbaRibbonCommands()
    {
        PushRibbonCommand = new AsyncRelayCommand(PushRibbonSelectedAsync, CanPushRibbon, context: "リボンのブックへの反映");
        // 検査は読むだけなので、反映の最中でもよい（ブックにも Excel にも書かない）。
        InspectRibbonCommand = new AsyncRelayCommand(InspectRibbonSelectedAsync, CanInspectRibbon, context: "リボンの XML の検査");
    }

    /// <summary>
    /// 選択中の VBA プロジェクトでリボンを同期していて、未反映がある（またはまだ数えていない）とき。未反映が無いと分かっていれば
    /// 無効にする（16.13）。数える前に無効にすると、起動直後に押せない時間ができるので、分からないときは有効にして、押したときに
    /// 「反映するものはありません」と伝える。
    /// </summary>
    private bool CanPushRibbon()
    {
        if (!CanOperateOnSelectedVbaProject() || _vbaSync?.Ribbon.IsActive != true || SelectedItem is not { } item) return false;
        return !_vbaBadgeCache.TryGetValue(item.Project.Id, out var entry) || entry.RibbonUnpushed;
    }

    private bool CanInspectRibbon()
        => IsVbaFeatureAvailable && _vbaSync?.Ribbon.IsActive == true && SelectedItem is { IsDisconnected: false } item && item.Project.Vba is not null;

    private async Task PushRibbonSelectedAsync()
    {
        if (_vbaSync is null || SelectedItem is not { } item || item.Project.Vba is null) return;
        if (RejectWhileVbaBusy()) return;
        await PushRibbonAsync(item.Project).ConfigureAwait(true);
    }

    /// <summary>
    /// リボンをブックへ反映する（16.5 の段階 F3 の経路）: 準備 → 確認（毎回）→ 書き換え → 結果の知らせ。失敗のうち、利用者が直して
    /// すぐ押し直せるもの（使用中・Excel で開いている）は「もう一度試す」を出し、選ばれたら準備からやり直す（確認も、もう一度出す）。
    /// </summary>
    internal async Task PushRibbonAsync(Project project)
    {
        if (_vbaSync is null) return;
        var ribbon = _vbaSync.Ribbon;
        while (true)
        {
            // 前回の書き換えが途中で終わっていれば、先に片付けて知らせる（16.11「次のリボンの操作で」）。
            if (await ribbon.RecoverInterruptedAsync(project).ConfigureAwait(true) is { } recovery)
            {
                await _dialogs.ShowMessageAsync(RecoveryTitle, recovery.Message).ConfigureAwait(true);
            }

            var prepared = await ribbon.PrepareRewriteAsync(project).ConfigureAwait(true);
            if (!prepared.IsSuccess)
            {
                if (!await ShowRibbonFailureAsync("リボンをブックへ反映できません", project, prepared.Issues).ConfigureAwait(true)) return;
                continue;
            }

            var preparation = prepared.Value;
            switch (preparation.Kind)
            {
                case VbaRibbonRewritePreparationKind.Declined:
                    Notify?.Invoke("リボンの反映を中止しました（未保存の変更を保存しなかったため）。リボンは未反映のままです。");
                    return;
                case VbaRibbonRewritePreparationKind.NothingToDo:
                    await ShowWarningsAsync("リボンの反映の注意", preparation.Notes).ConfigureAwait(true);
                    Notify?.Invoke("ブックへ反映するリボンの変更はありません。");
                    return;
            }

            var plan = preparation.Plan!;
            var viewModel = CreateRibbonRewriteViewModel?.Invoke(plan);
            var confirmed = viewModel is not null && ShowRibbonRewriteAsync is not null
                            && await ShowRibbonRewriteAsync(viewModel).ConfigureAwait(true);
            if (!confirmed)
            {
                Notify?.Invoke("リボンの反映を中止しました。リボンは未反映のままです。");
                return;
            }

            var result = await ribbon.RewriteAsync(project, plan).ConfigureAwait(true);
            await RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                var outcome = result.Value;
                Notify?.Invoke(VbaRibbonRewriteText.SucceededNotice(outcome));
                await ShowWarningsAsync("リボンの反映の注意", preparation.Notes.Concat(result.Issues)).ConfigureAwait(true);
                // リボンを新しく足したとき（そのブックへの初めての反映）だけ、Excel が読み込めたかを確かめる方法を案内する（16.8）。
                // エラーコードの付く問題ではないので、知らせの窓で出す。
                if (outcome.AddedNew) await _dialogs.ShowMessageAsync("リボンを新しく足しました", VbaRibbonRewriteText.FirstAddHint).ConfigureAwait(true);
                return;
            }

            if (!await ShowRibbonFailureAsync("リボンをブックへ反映できませんでした", project, result.Issues).ConfigureAwait(true)) return;
        }
    }

    private const string RecoveryTitle = "前回のリボンの書き換えについて";

    /// <summary>
    /// 失敗の知らせ（16.10「書き換えが失敗したとき」）。E823 は「控えのフォルダを開く」、一時的な理由（使用中・Excel で開いている）は
    /// 「もう一度試す」を添える。既定（閉じる・Esc）は「ファイルの変更は残す（リボンは未反映）」＝何もしない。戻り値は「もう一度試す」を選んだか。
    /// 適用の取り消しは提案しない（16.16-12）。
    /// </summary>
    private async Task<bool> ShowRibbonFailureAsync(string title, Project project, IReadOnlyList<GraftIssue> issues)
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, issues.Select(FormatIssue));
        if (issues.Any(i => i.Code == ErrorCode.E823))
        {
            if (await _dialogs.ShowActionMessageAsync(title, text, "控えのフォルダを開く").ConfigureAwait(true) && _vbaSync is not null)
            {
                RevealBackupFolder(project);
            }

            return false;
        }

        if (issues.Any(IsRetryable))
        {
            return await _dialogs.ShowActionMessageAsync(title, text, "もう一度試す").ConfigureAwait(true);
        }

        await _dialogs.ShowMessageAsync(title, text).ConfigureAwait(true);
        return false;
    }

    /// <summary>利用者が直してすぐ押し直せる失敗か（ほかのプログラムが使用中、Excel で開いている）。</summary>
    private static bool IsRetryable(GraftIssue issue)
        => (issue.Code == ErrorCode.E821 && (issue.Detail ?? string.Empty).Contains("ほかのプログラムが使っている", StringComparison.Ordinal))
           || (issue.Code == ErrorCode.E819 && (issue.Detail ?? string.Empty).Contains("閉じて", StringComparison.Ordinal));

    private void RevealBackupFolder(Project project)
    {
        if (VbaWorkbookBackupDirectoryOf?.Invoke(project) is not { } directory) return;
        try
        {
            Directory.CreateDirectory(directory);
            RevealFolder(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify?.Invoke($"控えのフォルダを開けませんでした: {ExceptionMessages.Describe(ex)}");
        }
    }

    /// <summary>ブックの丸ごとの控えのフォルダ（<c>MainViewModel</c> が <c>AppPaths.GetVbaWorkbookBackupDirectory</c> を渡す）。</summary>
    public Func<Project, string>? VbaWorkbookBackupDirectoryOf { get; set; }

    private async Task InspectRibbonSelectedAsync()
    {
        if (_vbaSync is null || SelectedItem is not { } item || item.Project.Vba is null) return;
        var results = await _vbaSync.Ribbon.InspectMirrorAsync(item.Project).ConfigureAwait(true);
        if (results.Count == 0)
        {
            Notify?.Invoke("検査するリボンのファイル（CustomUI/customUI14.xml・CustomUI/customUI.xml）がありません。");
            return;
        }

        await _dialogs.ShowMessageAsync("リボンの XML の検査", FormatInspection(results)).ConfigureAwait(true);
    }

    /// <summary>
    /// 「リボンの XML を検査」の結果の文（ファイルごとに件数の見出しと、行・列つきの一覧）。エラーは「［エラー］」、警告は「［警告］」を
    /// 頭に付ける（重さが一目で分かるように。エラーがあるうちはブックへ書けないことも添える）。
    /// </summary>
    internal static string FormatInspection(IReadOnlyList<VbaRibbonInspection> results)
    {
        var text = new StringBuilder();
        foreach (var result in results)
        {
            if (text.Length > 0) text.AppendLine().AppendLine();
            if (result.Validation is not { } validation)
            {
                text.Append(result.MirrorPath).Append(": 読めません（").Append((result.Problem ?? "理由は不明です").TrimEnd('。')).Append("）。");
                continue;
            }

            var errors = validation.Errors.Count();
            var warnings = validation.Warnings.Count();
            if (errors == 0 && warnings == 0)
            {
                text.Append(result.MirrorPath).Append(": 問題はありません。");
                continue;
            }

            text.Append(result.MirrorPath).Append(": エラー ").Append(errors).Append(" 件・警告 ").Append(warnings).Append(" 件");
            text.Append(errors > 0 ? "（エラーがあるうちは、ブックへ書きません）" : "（警告だけなら、ブックへ書けます）");
            foreach (var finding in validation.Findings)
            {
                text.AppendLine().Append(finding.Severity == Severity.Error ? "  ［エラー］" : "  ［警告］").Append(finding.ToDisplayText());
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// 途中で終わった書き換えの後始末（16.11「次の起動」）。バッジの調べ直しのついでに、起動してから初めて見るプロジェクトだけ確かめる。
    /// 知らせ（ダイアログ）は、呼び出し側がバッジの錠を放してから出す（ダイアログを出しているあいだ、バッジの調べ直しを止めないため）。
    /// </summary>
    private async Task<List<VbaRibbonRecovery>> RecoverInterruptedRibbonRewritesAsync(VbaSyncService service, IReadOnlyList<Project> targets)
    {
        var found = new List<VbaRibbonRecovery>();
        foreach (var project in targets)
        {
            if (!_ribbonRecoveryChecked.Add(project.Id) || !service.Ribbon.HasInterruptedRewrite(project)) continue;
            if (await service.Ribbon.RecoverInterruptedAsync(project).ConfigureAwait(true) is { } recovery) found.Add(recovery);
        }

        return found;
    }

    private async Task ShowRibbonRecoveriesAsync(IReadOnlyList<VbaRibbonRecovery> recoveries)
    {
        foreach (var recovery in recoveries)
        {
            await _dialogs.ShowMessageAsync(RecoveryTitle, recovery.Message).ConfigureAwait(true);
        }
    }
}
