using System.Collections.ObjectModel;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// 修正1: 履歴差分タブ内の1ファイル分の表示。ファイルパス・操作種別の見出しと、
/// そのファイル専用の<see cref="DiffViewModel"/>を持つ（既存のDiffView/DiffViewModelを
/// そのまま1ファイルぶんずつ再利用するだけに留める。複数ファイルの並べ方は
/// <see cref="HistoryDiffViewModel"/>のコメント参照）。
/// </summary>
public sealed class HistoryDiffFileViewModel
{
    public HistoryDiffFileViewModel(BlockPlan plan, Settings settings, IUiServices ui)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Diff = new DiffViewModel(settings, ui);
        Diff.Load(plan);
    }

    /// <summary>この行のもとになったドライラン結果相当のBlockPlan（History.BuildDiffPlansAsync参照）。</summary>
    public BlockPlan Plan { get; }

    /// <summary>このファイル専用のdiff表示。</summary>
    public DiffViewModel Diff { get; }

    /// <summary>見出しの1行目: ファイルパス。</summary>
    public string PathText => Plan.Path;

    /// <summary>見出しの2行目: 操作種別（BlockItemViewModel.OperationFallbackTextと同じ表記）。</summary>
    public string OperationText => Plan.Operation switch
    {
        EntryOperation.Create => "新規作成",
        EntryOperation.Delete => "削除",
        EntryOperation.Rename => "移動・改名",
        EntryOperation.Mkdir => "フォルダ作成",
        _ => "変更",
    };

    /// <summary>読み上げ用の見出し全体（8.14）。</summary>
    public string AutomationName => $"{PathText}、{OperationText}の差分";
}

/// <summary>
/// 修正1: 履歴のリビジョン選択に連動して開く「履歴差分タブ」（EditorTabKind.HistoryDiff）の
/// 表示内容。選択したリビジョンが変更した全ファイルの差分を、ファイルごとの見出し区切り付きで
/// 縦に並べて表示する（HistoryDiffView.axaml参照）。
///
/// 【複数ファイルの表示方法についての判断】
/// 既存のDiffViewModelは「1回のLoadはブロック1件（＝1ファイル）分の差分を表示する」設計で、
/// 複数ファイルを1つのDiffViewModelで同時に表示する仕組みは持たない。選択肢は主に2つあった。
/// (a) ファイル一覧＋単一のDiffViewを置き、選択中の1ファイルだけを都度Load()し直して切り替える
///     （従来のブロック一覧→Diff.Loadと同じ構造）。
/// (b) ファイルごとに独立したDiffViewModel/DiffViewを作り、区切り付きで縦に並べる。
/// (a)は既存のDiffViewModelを1個しか使わずメモリ効率は良いが、「全ファイルの差分を確認できる」
/// という要件に対して毎回1ファイルしか見えない（結局ファイル切り替えが要る）ため要件を弱くしか
/// 満たさない。(b)はDiffViewModel/DiffViewをそのまま複製するだけで実装でき、スクロールするだけで
/// 全ファイルの差分を見比べられる。1リビジョンで変更するファイル数は通常少数のため、(b)の
/// メモリ・表示コストは許容範囲と判断した。加えて(b)の各DiffViewはHistoryDiffView.axaml側で
/// 高さを固定し、ListBoxの仮想化がファイル単位では機能する（1ファイル内の行が多くても
/// そのファイルのビューポート内だけが実体化される）ようにしている。
/// </summary>
public sealed class HistoryDiffViewModel : ObservableObject
{
    private readonly IUiServices _ui;
    private Settings _settings;

    private string _revisionLabel = string.Empty;
    private string _bannerText = string.Empty;

    public HistoryDiffViewModel(Settings settings, IUiServices ui)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <summary>選択中リビジョンが変更した全ファイル（表示順は元のmanifest.entriesの順序のまま）。</summary>
    public ObservableCollection<HistoryDiffFileViewModel> Files { get; } = new();

    /// <summary>タブ見出しに使う「r3」等のラベル。EditorTabViewModel.Titleが参照する。</summary>
    public string RevisionLabel { get => _revisionLabel; private set => SetProperty(ref _revisionLabel, value); }

    /// <summary>
    /// 「これは過去の適用の記録であり、現在のファイル内容ではない」ことを明示するバナー文言
    /// （タブ本体の先頭に表示する。HistoryDiffView.axaml参照）。
    /// </summary>
    public string BannerText { get => _bannerText; private set => SetProperty(ref _bannerText, value); }

    /// <summary>タブを開いておくべき内容があるかどうか（ShellViewModelがタブの開閉判定に使う）。</summary>
    public bool HasFiles => Files.Count > 0;

    /// <summary>
    /// 4.8: 各ファイルのdiff表示からのジャンプ要求をまとめて中継する。ShellViewModelはこの
    /// インスタンス自体が使い回されるため、コンストラクタ時に一度だけ購読すればよい
    /// （個々のDiffViewModelはLoad/Clearのたびに作り直されるため、直接購読すると
    /// 都度つなぎ直しが要る）。
    /// </summary>
    public event EventHandler<(string RelativePath, int Line)>? JumpRequested;

    /// <summary>
    /// 履歴のリビジョン選択（MainViewModel.OnRevisionSelected）から呼ぶ。既存のFilesを破棄し、
    /// 渡されたplans（HistoryPaneViewModel.BuildDiffPlansAsyncの結果）で作り直す。
    /// </summary>
    public void Load(RevisionRowViewModel row, IReadOnlyList<BlockPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(plans);
        ClearFiles();

        RevisionLabel = row.RevisionLabel;
        BannerText =
            $"{row.RevisionLabel}（{row.AppliedAtText} 適用、{row.SummaryText}）が行った変更の記録です。現在のファイルの内容ではありません。";

        foreach (var plan in plans)
        {
            var file = new HistoryDiffFileViewModel(plan, _settings, _ui);
            file.Diff.JumpRequested += OnFileJumpRequested;
            Files.Add(file);
        }
        OnPropertyChanged(nameof(HasFiles));
    }

    /// <summary>選択解除時に呼ぶ。表示内容を空へ戻す。</summary>
    public void Clear()
    {
        ClearFiles();
        RevisionLabel = string.Empty;
        BannerText = string.Empty;
        OnPropertyChanged(nameof(HasFiles));
    }

    private void ClearFiles()
    {
        foreach (var file in Files)
        {
            file.Diff.JumpRequested -= OnFileJumpRequested;
        }
        Files.Clear();
    }

    /// <summary>
    /// 課題1と同じ方針: 折り返し・空白表示等の表示だけの設定は、適用処理の実行中かどうかに
    /// 関わらずその場で反映する（MainViewModel.UpdateSettings参照）。
    /// </summary>
    public void UpdateSettings(Settings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        foreach (var file in Files)
        {
            file.Diff.UpdateSettings(settings);
        }
    }

    private void OnFileJumpRequested(object? sender, (string RelativePath, int Line) e) => JumpRequested?.Invoke(this, e);
}
