using System.Windows.Input;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ShellViewModel"/> の分割ファイル（1ファイル400行上限のため）。Excel（ブック）へ未反映のリボン・モジュールの
/// 見落とし対策のうち、画面全体を知る Shell が担う部分:
/// <list type="bullet">
/// <item>ステータスバーの常時表示（「リボン未反映（ブック名）」「Excel へ未反映（ブック名）」。押すと反映を始める）</item>
/// <item>エクスプローラーの印（未反映のファイル・フォルダ）の更新</item>
/// <item>Graft を閉じるときの確認（<see cref="ConfirmVbaUnpushedBeforeExitAsync"/>）</item>
/// </list>
/// <para>
/// 【表示スロットを警告・通知と分ける理由】 既存の警告スロット（<c>ShellViewModel.StatusBarWarning.cs</c>）は、深刻な警告を
/// 1 つの文字列に要約する「押せない」表示で、複数成立すると「ほかN件」に畳む。ここは「押すと反映する」操作つきで、消えずに残る
/// 必要がある（数秒で消える通知（<c>StatusNoticeViewModel</c>）だと、見落としの対策にならない）ので、クリップボード監視の通知
/// （<c>ShellViewModel.ClipboardWatch.cs</c>）と同じ、独立した押せるスロットにした。
/// </para>
/// <para>
/// 【更新の契機】 プロジェクトペインのバッジの数え直し（<c>RefreshVbaBadgesAsync</c>。ウィンドウの前面化・適用・反映・取込・取り消しのあと）の
/// 末尾の通知（<c>VbaUnpushedChanged</c>）だけ。専用のファイル監視・定期処理は足さない。判定はミラーと同期基準を読むだけで、Excel に触れない。
/// </para>
/// </summary>
public sealed partial class ShellViewModel
{
    private IReadOnlyList<VbaUnpushedInfo> _vbaUnpushed = Array.Empty<VbaUnpushedInfo>();

    /// <summary>未反映があるプロジェクトが 1 つ以上あるか（ステータスバーの常時表示を出す）。</summary>
    public bool HasVbaUnpushed => _vbaUnpushed.Count > 0;

    /// <summary>
    /// ステータスバーの文言。先頭のプロジェクトがモジュールを含むなら「Excel へ未反映（ブック名）」、リボンだけなら「リボン未反映（ブック名）」。
    /// 複数あるときは「ほか N 件」を添える。
    /// </summary>
    public string VbaUnpushedText
    {
        get
        {
            if (_vbaUnpushed.Count == 0) return string.Empty;
            var first = _vbaUnpushed[0];
            var head = first.HasModules ? $"Excel へ未反映（{first.WorkbookName}）" : $"リボン未反映（{first.WorkbookName}）";
            return _vbaUnpushed.Count > 1 ? $"{head} ほか {_vbaUnpushed.Count - 1} 件" : head;
        }
    }

    /// <summary>ツールチップ（各プロジェクトの未反映の中身と、押すとどうなるか）。</summary>
    public string VbaUnpushedTooltip
    {
        get
        {
            if (_vbaUnpushed.Count == 0) return string.Empty;
            var lines = _vbaUnpushed.Select(u => $"{u.Project.DisplayName}（{u.WorkbookName}）: {u.Contents}").ToList();
            lines.Add(_vbaUnpushed.Count > 1
                ? "クリックすると、先頭のプロジェクトを選んで反映を始めます（終わると、次のものが先頭になります）。"
                : "クリックすると、このプロジェクトを選んで反映を始めます（リボンは確認の窓が出ます）。");
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>押すと、先頭の未反映のプロジェクトを選んで、リボンなら「リボンをブックへ反映…」、モジュールなら「Excel へ反映」を実行する。</summary>
    public ICommand PushVbaUnpushedCommand { get; private set; } = null!;

    private void InitializeVbaUnpushed()
    {
        PushVbaUnpushedCommand = new AsyncRelayCommand(PushFirstVbaUnpushedAsync, () => HasVbaUnpushed, context: "Excel への未反映の反映");
        Graft.ProjectPane.VbaUnpushedChanged += (_, _) => RefreshVbaUnpushed();
    }

    /// <summary>
    /// 保持している数え直しの結果から、ステータスバーとエクスプローラーの未反映の印を更新する（Excel にもファイルにも触れない）。
    /// プロジェクトの切り替え直後にも呼ぶ（新しいツリーには、前の印が残っていないので）。
    /// </summary>
    internal void RefreshVbaUnpushed()
    {
        _vbaUnpushed = Graft.ProjectPane.GetVbaUnpushed();
        OnPropertyChanged(nameof(HasVbaUnpushed));
        OnPropertyChanged(nameof(VbaUnpushedText));
        OnPropertyChanged(nameof(VbaUnpushedTooltip));
        CommandRequery.Invalidate();

        // エクスプローラーの印は、いま開いているプロジェクトのぶんだけ。
        if (Explorer.Project is { } current)
        {
            var info = _vbaUnpushed.FirstOrDefault(u => u.Project.Id == current.Id);
            Explorer.SetVbaUnpushed(
                current.Id, info?.ModulePaths ?? Array.Empty<string>(), info?.RibbonPaths ?? Array.Empty<string>());
        }
    }

    private async Task PushFirstVbaUnpushedAsync()
    {
        var first = _vbaUnpushed.FirstOrDefault();
        if (first is null) return;
        await Graft.ProjectPane.PushUnpushedAsync(first).ConfigureAwait(true);
    }

    /// <summary>
    /// Graft を閉じるときの確認の材料: 未反映のあるプロジェクト。保持している数え直しの結果ではなく、ここで数え直してから返す
    /// （閉じる直前に外部のツールでミラーを編集した場合も拾うため）。判定は Excel に触れずファイルを読むだけで、閉じる処理を重くしない。
    /// </summary>
    public async Task<IReadOnlyList<VbaUnpushedInfo>> GetVbaUnpushedForCloseAsync()
    {
        await Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);
        return Graft.ProjectPane.GetVbaUnpushed();
    }

    /// <summary>閉じるときの確認の題名。</summary>
    public const string CloseConfirmTitle = "Excel へ未反映の変更があります";

    /// <summary>「反映してから閉じる」の識別子。</summary>
    public const string ClosePushId = "push";

    /// <summary>「このまま閉じる」の識別子。</summary>
    public const string CloseAnywayId = "close";

    /// <summary>「キャンセル」の識別子（既定。Enter・Esc・× もこれ）。</summary>
    public const string CloseCancelId = "cancel";

    /// <summary>
    /// Graft を閉じる前の確認。未反映（リボンまたはモジュール）がある VBA プロジェクトがあれば、プロジェクト名と中身を挙げて
    /// 「反映してから閉じる／このまま閉じる／キャンセル」を出す。既定（Enter・Esc・×）はキャンセル（未反映のまま閉じて失うより、
    /// 閉じないほうが安全なため）。「反映してから閉じる」は、プロジェクトごとにモジュール → リボンの順に既存の反映を行い
    /// （リボンの確認の窓は出る）、すべて成功したときだけ閉じる。失敗・中止があれば閉じずに戻る。
    /// </summary>
    /// <returns>閉じてよいか。</returns>
    public async Task<bool> ConfirmVbaUnpushedBeforeExitAsync()
    {
        if (!MayHaveVbaUnpushed) return true;
        var unpushed = await GetVbaUnpushedForCloseAsync().ConfigureAwait(true);
        if (unpushed.Count == 0) return true;

        var lines = unpushed.Select(u => $"・{u.Project.DisplayName}（{u.WorkbookName}）: {u.Contents}");
        var message =
            "次のプロジェクトに、Excel（ブック）へまだ反映していない変更があります。" + Environment.NewLine + Environment.NewLine +
            string.Join(Environment.NewLine, lines) + Environment.NewLine + Environment.NewLine +
            "「反映してから閉じる」を選ぶと、モジュールは「Excel へ反映」、リボンは「リボンをブックへ反映…」（確認の窓が出ます）の順に反映し、" +
            "すべて成功したら閉じます。失敗や中止があったときは閉じません。「このまま閉じる」を選ぶと、未反映のまま閉じます" +
            "（ファイルは残るので、次の起動でも未反映の印が出ます）。";
        var request = new Graft.Core.Vba.VbaChoiceRequest(
            CloseConfirmTitle, message,
            new[]
            {
                new Graft.Core.Vba.VbaChoiceOption(ClosePushId, "反映してから閉じる"),
                new Graft.Core.Vba.VbaChoiceOption(CloseAnywayId, "このまま閉じる"),
                new Graft.Core.Vba.VbaChoiceOption(CloseCancelId, "キャンセル", IsDefault: true),
            });
        var choose = Graft.ProjectPane.ChooseVba;
        var choice = choose is null ? request.Default.Id : await choose(request).ConfigureAwait(true);
        switch (choice)
        {
            case CloseAnywayId:
                return true;
            case ClosePushId:
                foreach (var info in unpushed)
                {
                    if (!await Graft.ProjectPane.PushForCloseAsync(info).ConfigureAwait(true)) return false;
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>VBA の未反映を数える必要があるか（VBA 連携が使えて、VBA プロジェクトが 1 つ以上ある）。閉じる処理の入口の軽い判定。</summary>
    public bool MayHaveVbaUnpushed => Graft.ProjectPane.HasVbaProjects;
}
