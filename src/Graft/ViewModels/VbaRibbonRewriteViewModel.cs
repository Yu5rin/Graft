using System.Windows.Input;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// リボンの書き換えの確認ダイアログ（<c>VbaRibbonRewriteWindow</c>。仕様書_ブックの情報と依頼の観点 16.9）の ViewModel。
/// <para>
/// 【毎回確認する・既定は「中止」】 「次から確認しない」は置かない。Enter と Esc は「中止」（<see cref="Confirmed"/> は false のまま）。
/// ブックのファイルを書き換える操作で、取り違えたときの影響が大きいので、利用者が「書き換える」を押したときだけ進む。
/// 文面は <see cref="VbaRibbonRewriteText"/>（純ロジック）が作り、ここは並べるだけにする。
/// </para>
/// <para>
/// 【差分を見る】 汎用の <c>VbaChoiceRequest</c> ではなく専用の窓にしたのは、ブック側（いまの部品）とミラーの差分への入口と、警告の一覧を
/// 持つため（16.9）。差分は既存の <see cref="DiffViewModel"/>（控えの窓・衝突ダイアログと同じ作り）で、ボタンを押したときに作る。
/// </para>
/// </summary>
public sealed class VbaRibbonRewriteViewModel : ObservableObject
{
    private readonly int _contextLines;
    private bool _showAllWarnings;
    private bool _isDiffVisible;
    private VbaRibbonRewriteTarget? _diffTarget;

    public VbaRibbonRewriteViewModel(VbaRibbonRewritePlan plan, Settings settings, IUiServices ui)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ui);
        _contextLines = settings.Diff.ContextLines;
        Diff = new DiffViewModel(settings, ui)
        {
            WordWrap = settings.Diff.WordWrap,
            ShowWhitespace = settings.Diff.ShowWhitespace,
        };
        _diffTarget = plan.Targets.FirstOrDefault();

        RewriteCommand = new RelayCommand(() =>
        {
            Confirmed = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });
        CancelCommand = new RelayCommand(() =>
        {
            Confirmed = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });
        ToggleDiffCommand = new RelayCommand(ToggleDiff);
        ShowAllWarningsCommand = new RelayCommand(() => ShowAllWarnings = true, () => HasMoreWarnings);
    }

    /// <summary>計画。</summary>
    public VbaRibbonRewritePlan Plan { get; }

    /// <summary>利用者が「書き換える」を選んだか（それ以外の閉じ方はすべて中止）。</summary>
    public bool Confirmed { get; private set; }

    /// <summary>窓を閉じてほしいとき。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>窓の題。</summary>
    public string TitleText => VbaRibbonRewriteText.Title;

    /// <summary>冒頭の文（何をするか・ブックは開いていないこと）。</summary>
    public string LeadText => VbaRibbonRewriteText.Lead(Plan);

    /// <summary>手順（番号つき。改行区切り）。</summary>
    public string StepsText => string.Join(Environment.NewLine, VbaRibbonRewriteText.Steps(Plan));

    /// <summary>
    /// 未保存の変更の注意（開いていて未保存のときだけ。それ以外は空）。確認の文面を変えるためだけの表示で、保存するかどうかは
    /// この値で決めない（必ず保存する。16.6）。
    /// </summary>
    public string UnsavedWarningText => VbaRibbonRewriteText.UnsavedWarning(Plan) ?? string.Empty;

    /// <summary>未保存の注意を出すか。</summary>
    public bool HasUnsavedWarning => Plan.HasUnsavedChanges;

    /// <summary>開いているブックの注意（マクロが動く・「元に戻す」の履歴が戻らない。開いていないときは空）。</summary>
    public string OpenNoticesText => string.Join(Environment.NewLine, VbaRibbonRewriteText.OpenNotices(Plan).Select(n => "・" + n));

    /// <summary>開いているブックの注意を出すか。</summary>
    public bool HasOpenNotices => Plan.IsOpen;

    /// <summary>実行のボタンの文言（開いていれば「保存して書き換える」、開いていなければ「書き換える」）。</summary>
    public string RewriteButtonText => VbaRibbonRewriteText.ConfirmButtonText(Plan);

    /// <summary>控えの場所の行。</summary>
    public string BackupText => "・" + VbaRibbonRewriteText.BackupLine(Plan);

    /// <summary>警告があるか。</summary>
    public bool HasWarnings => Plan.Warnings.Count > 0;

    /// <summary>警告の見出し（無ければ空）。</summary>
    public string WarningsHeadingText => VbaRibbonRewriteText.WarningsHeading(Plan) is { } heading ? "・" + heading : string.Empty;

    /// <summary>警告の一覧（既定は先頭 5 件と「ほか N 件」）。</summary>
    public IReadOnlyList<string> WarningLines => VbaRibbonRewriteText.WarningLines(Plan, _showAllWarnings);

    /// <summary>出し切れていない警告があるか（「すべて見る」を出す）。</summary>
    public bool HasMoreWarnings => !_showAllWarnings && Plan.Warnings.Count > VbaRibbonRewriteText.MaxWarningsShown;

    /// <summary>警告を全件出すか。</summary>
    public bool ShowAllWarnings
    {
        get => _showAllWarnings;
        set
        {
            if (!SetProperty(ref _showAllWarnings, value)) return;
            OnPropertyChanged(nameof(WarningLines));
            OnPropertyChanged(nameof(HasMoreWarnings));
            CommandRequery.Invalidate();
        }
    }

    /// <summary>差分（ブック側 → ミラー）。</summary>
    public DiffViewModel Diff { get; }

    /// <summary>差分を出しているか。</summary>
    public bool IsDiffVisible
    {
        get => _isDiffVisible;
        private set
        {
            if (!SetProperty(ref _isDiffVisible, value)) return;
            OnPropertyChanged(nameof(DiffButtonText));
            OnPropertyChanged(nameof(ShowDiffTargetPicker));
        }
    }

    /// <summary>差分のボタンの文言（出しているときは「差分を隠す」）。</summary>
    public string DiffButtonText => _isDiffVisible ? "差分を隠す" : VbaRibbonRewriteText.DiffButton;

    /// <summary>差分を見る形式（2 形式を書くときに切り替える）。</summary>
    public IReadOnlyList<VbaRibbonRewriteTarget> DiffTargets => Plan.Targets;

    /// <summary>2 形式を書くか（差分の切り替えを出す）。</summary>
    public bool HasSeveralTargets => Plan.Targets.Count > 1;

    /// <summary>差分の形式の切り替えを出すか（差分を出していて、2 形式を書くときだけ）。</summary>
    public bool ShowDiffTargetPicker => _isDiffVisible && HasSeveralTargets;

    /// <summary>差分を見ている形式。</summary>
    public VbaRibbonRewriteTarget? DiffTarget
    {
        get => _diffTarget;
        set
        {
            if (!SetProperty(ref _diffTarget, value)) return;
            if (_isDiffVisible) LoadDiff();
        }
    }

    /// <summary>差分の見出し。</summary>
    public string DiffCaptionText => _diffTarget is { IsNewPart: true }
        ? $"左: ブック（{VbaRibbonMirrorLayout.FileName(_diffTarget.Format)} はまだありません） / 右: Graft のファイル（{_diffTarget.MirrorPath}）"
        : $"左: ブックのいまのリボン / 右: Graft のファイル（{_diffTarget?.MirrorPath}）";

    /// <summary>「書き換える」。</summary>
    public ICommand RewriteCommand { get; }

    /// <summary>「中止」。</summary>
    public ICommand CancelCommand { get; }

    /// <summary>「差分を見る」／「差分を隠す」。</summary>
    public ICommand ToggleDiffCommand { get; }

    /// <summary>警告を全件出す。</summary>
    public ICommand ShowAllWarningsCommand { get; }

    /// <summary>ダイアログ全体の文面（読み上げ用）。</summary>
    public string AccessibleText => VbaRibbonRewriteText.FullText(Plan);

    private void ToggleDiff()
    {
        if (_isDiffVisible)
        {
            IsDiffVisible = false;
            return;
        }

        LoadDiff();
        IsDiffVisible = true;
    }

    private void LoadDiff()
    {
        OnPropertyChanged(nameof(DiffCaptionText));
        if (_diffTarget is not { } target)
        {
            Diff.Clear();
            return;
        }

        Diff.Load(VbaConflictDiff.BuildPlan(target.MirrorPath, target.BookXml ?? string.Empty, target.Xml, DiffCaptionText, _contextLines));
    }
}
