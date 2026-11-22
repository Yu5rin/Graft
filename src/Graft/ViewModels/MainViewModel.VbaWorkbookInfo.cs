using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル。ブックの情報（機能 A。仕様書_ブックの情報と依頼の観点 5.4）を
/// Excel から取る窓口。自動 Pull（<c>CreateVbaContextPull</c>）と同じ作りで、
/// <see cref="ContextCollectViewModel"/> に渡す。
/// <para>
/// 【設定は呼び出しのたびに読む】 設定画面での変更（<c>Vba.CollectWorkbookInfo</c>・行数・上限）を、プロジェクトを
/// 選び直さなくても反映するため。<c>Vba.Enabled</c> が false のとき・VBA でないプロジェクト・Null のブリッジでは、
/// 行そのものを出さず、Excel も呼ばない。
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 指定のプロジェクトのブックの情報の窓口を作る。VBA プロジェクトでなければ null
    /// （通常のファイルプロジェクトの挙動は従来どおり。呼び出しも増えない）。
    /// </summary>
    private VbaWorkbookInfoSource? CreateVbaWorkbookInfoSource(Project project)
    {
        if (project.Vba is null) return null;
        return new VbaWorkbookInfoSource
        {
            IsAvailable = () => IsVbaActive(CurrentVbaProject(project)),
            SampleRows = () => _settings.Vba.WorkbookInfoSampleRows,
            FetchAsync = (sampleValues, includeFormulas, ct) => FetchVbaWorkbookInfoAsync(project, sampleValues, includeFormulas, ct),
            // 値の見本をオンにする確認は、ボタンの文言と既定を指定できる選択ダイアログ（VbaChoiceWindow）で出す。
            ChooseAsync = ChooseVbaOptionAsync,
        };
    }

    /// <summary>一覧を読み直した後の最新の Project（<see cref="VbaLink"/>）。無ければ渡されたもの。</summary>
    private Project CurrentVbaProject(Project project)
        => ProjectPane.Items.FirstOrDefault(i => i.Project.Id == project.Id)?.Project ?? project;

    private async Task<GraftResult<VbaWorkbookInfoSnapshot>?> FetchVbaWorkbookInfoAsync(
        Project project, bool sampleValues, bool includeFormulas, CancellationToken ct)
    {
        var current = CurrentVbaProject(project);
        if (!IsVbaActive(current) || !_settings.Vba.CollectWorkbookInfo) return null;

        var vba = _settings.Vba;
        // 値の見本の行数は、オプトインしていないとき 0（見本を読む COM 呼び出しを一切行わない）。
        var options = new VbaWorkbookInfoOptions(
            IncludeReferences: true,
            SampleRows: sampleValues ? vba.WorkbookInfoSampleRows : 0,
            IncludeFormulas: sampleValues && includeFormulas,
            MaxColumns: vba.WorkbookInfoMaxColumns,
            MaxCellChars: vba.WorkbookInfoMaxCellChars,
            MaxSheets: 100,
            // 呼び出し全体の時間切れ（E805）より手前で、読めた分を返すための予算（仕様書5.5）。
            Budget: TimeSpan.FromMilliseconds(vba.CallTimeoutMs * 2 / 3.0));

        var result = await _vbaBridge.ReadWorkbookInfoAsync(current.Vba!.WorkbookFullName, options, ct).ConfigureAwait(true);

        // ログには事実だけを残す。見本の値そのものはログに書かない（社外秘を含みうるため。仕様書14章）。
        if (result.IsSuccess)
        {
            var sampled = options.SampleRows > 0 ? result.Value.Sheets.Sum(s => s.SampleRows.Count) : 0;
            Logger?.Info(
                "vba-workbook-info",
                options.SampleRows > 0
                    ? $"ブックの情報を取得しました（シート {result.Value.Sheets.Count} 枚、値の見本 {sampled} 行）。"
                    : $"ブックの情報を取得しました（シート {result.Value.Sheets.Count} 枚。値の見本は読んでいません）。",
                targetPath: current.Root);
        }
        else
        {
            Logger?.Warn("vba-workbook-info", "ブックの情報を取得できませんでした。", targetPath: current.Root);
        }

        return result;
    }
}
