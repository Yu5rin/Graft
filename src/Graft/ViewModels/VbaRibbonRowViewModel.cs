using System.Windows.Input;
using Graft.Core.Vba;

namespace Graft.ViewModels;

/// <summary>
/// コンテキスト収集の窓の「リボン（customUI）」の行（仕様書_ブックの情報と依頼の観点 15.5）。
/// 「ブックの情報」の行（<see cref="VbaWorkbookInfoRowViewModel"/>）の隣に置く専用の行で、<c>Files</c> の選択集計・除外・差分方式の
/// 記憶とは混ぜない。3 状態（内容も出す／構成だけ／出さない）と、取得の状況（日時・理由）、コールバックの照合の要約を持つ。
/// <para>
/// 状態の持ち主は <see cref="ContextCollectViewModel"/>（<c>VbaLink</c> への保存・ファイルの読み取りを担う）。この行は
/// 画面に結ぶ値と操作のコマンドだけを持つ薄い入れ物で、値の変更は持ち主が <c>internal</c> のメソッドで行う
/// （行そのものを書き換えても何も保存されない、という事故を防ぐ。ブックの情報の行と同じ作り）。
/// </para>
/// </summary>
public sealed class VbaRibbonRowViewModel : ObservableObject
{
    /// <summary>
    /// 常時出す注意。読んでいるのはディスク上のファイルなので、Excel で開いて編集中の未保存の変更は入らない
    /// （利用者が「今の画面と同じ」と思い込まないように。依頼文の冒頭にも同じことを書く）。
    /// </summary>
    public const string SavedContentNote = "読むのは最後に保存した内容です（Excel で編集中の、まだ保存していない変更は含まれません）。";

    private VbaWorkbookInfoLevel _level = VbaWorkbookInfoLevel.Full;
    private bool _isVisible;
    private string _statusText = string.Empty;
    private string _callbackText = string.Empty;
    private string _missingText = string.Empty;

    internal VbaRibbonRowViewModel(ICommand cycleCommand)
    {
        CycleCommand = cycleCommand;
    }

    /// <summary>行を出すか。VBA プロジェクトで、機能が有効で、この環境で使えるときだけ true。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        internal set
        {
            if (!SetProperty(ref _isVisible, value)) return;
            OnPropertyChanged(nameof(ShowNote));
        }
    }

    /// <summary>3 状態。クリックで 内容も出す → 構成だけ → 出さない と巡る。</summary>
    public VbaWorkbookInfoLevel Level
    {
        get => _level;
        internal set
        {
            if (!SetProperty(ref _level, value)) return;
            OnPropertyChanged(nameof(IsFull));
            OnPropertyChanged(nameof(IsStructureOnly));
            OnPropertyChanged(nameof(IsHidden));
            OnPropertyChanged(nameof(LevelLabel));
            OnPropertyChanged(nameof(ToggleToolTip));
            OnPropertyChanged(nameof(AutomationLabel));
            OnPropertyChanged(nameof(ShowNote));
        }
    }

    public bool IsFull => _level == VbaWorkbookInfoLevel.Full;
    public bool IsStructureOnly => _level == VbaWorkbookInfoLevel.StructureOnly;
    public bool IsHidden => _level == VbaWorkbookInfoLevel.Hidden;

    /// <summary>状態の名前（ファイルの 3 状態と同じ言い方）。</summary>
    public string LevelLabel => _level switch
    {
        VbaWorkbookInfoLevel.StructureOnly => "構成だけ",
        VbaWorkbookInfoLevel.Hidden => "出さない",
        _ => "内容も出す",
    };

    /// <summary>アイコンのツールチップ。次の状態を伝える。</summary>
    public string ToggleToolTip => _level switch
    {
        VbaWorkbookInfoLevel.Full => "内容も出す（タブ・グループ・コントロールの構成、コールバックと VBA の照合、XML の全文）。クリックで「構成だけ」にします。",
        VbaWorkbookInfoLevel.StructureOnly => "構成だけ（タブ・グループ・コントロールの id と label と種類、コールバックの一覧）。XML の全文は渡しません。クリックで「出さない」にします。",
        _ => "出さない（ブックのファイルも読みません）。クリックで「内容も出す」にします。",
    };

    /// <summary>読み上げ用の名前（色やアイコンだけに頼らない）。</summary>
    public string AutomationLabel => $"リボン（customUI）（{LevelLabel}）";

    /// <summary>「最後に保存した内容」の注意を出すか（行が出ていて、「出さない」でないとき）。</summary>
    public bool ShowNote => _isVisible && _level != VbaWorkbookInfoLevel.Hidden;

    /// <summary>注意の文（<see cref="SavedContentNote"/>）。</summary>
    public string NoteText => SavedContentNote;

    /// <summary>取得日時・ファイルの更新日時・読めなかった理由（1 行）。</summary>
    public string StatusText
    {
        get => _statusText;
        internal set
        {
            if (!SetProperty(ref _statusText, value)) return;
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _statusText.Length > 0;

    /// <summary>コールバックの照合の要約（「コールバック 5 件（VBA に見つからない 2 件）」）。無ければ空。</summary>
    public string CallbackText
    {
        get => _callbackText;
        private set
        {
            if (!SetProperty(ref _callbackText, value)) return;
            OnPropertyChanged(nameof(HasCallbackText));
        }
    }

    public bool HasCallbackText => _callbackText.Length > 0;

    /// <summary>見つからない・呼べないコールバックの名前（先頭の数件）。問題が無ければ空。</summary>
    public string MissingText
    {
        get => _missingText;
        private set
        {
            if (!SetProperty(ref _missingText, value)) return;
            OnPropertyChanged(nameof(HasMissingText));
        }
    }

    public bool HasMissingText => _missingText.Length > 0;

    /// <summary>状態を巡回する（ファイルの 3 状態の操作と同じ）。</summary>
    public ICommand CycleCommand { get; }

    /// <summary>取得結果のコールバックの照合を、画面の要約にする。<paramref name="checks"/> が null なら要約を消す。</summary>
    internal void ShowCallbacks(IReadOnlyList<VbaRibbonCallbackCheck>? checks)
    {
        if (checks is null || checks.Count == 0)
        {
            CallbackText = string.Empty;
            MissingText = string.Empty;
            return;
        }

        var problems = checks.Where(c => c.Status is VbaRibbonCallbackStatus.NotFound or VbaRibbonCallbackStatus.ModuleNotFound or VbaRibbonCallbackStatus.NotPublic
                                          or VbaRibbonCallbackStatus.FoundOutsideStandardModule).ToArray();
        var notChecked = checks.Any(c => c.Status == VbaRibbonCallbackStatus.Unchecked);
        CallbackText = notChecked
            ? $"コールバック {checks.Count} 件（VBA のミラーを読めなかったため、照合できていません）"
            : problems.Length == 0
                ? $"コールバック {checks.Count} 件（すべて VBA にあります）"
                : $"コールバック {checks.Count} 件（VBA に見つからない・呼べない可能性があるもの {problems.Length} 件）";
        MissingText = problems.Length == 0
            ? string.Empty
            : "VBA に見つからない・呼べない可能性があるもの: " + string.Join("、", problems.Take(5).Select(p => p.Name)) + (problems.Length > 5 ? $" ほか {problems.Length - 5} 件" : string.Empty);
    }
}
