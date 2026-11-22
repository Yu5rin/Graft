using System.Windows.Input;
using Graft.Core.Vba;

namespace Graft.ViewModels;

/// <summary>
/// コンテキスト収集の窓の「ブックの情報」の行（仕様書_ブックの情報と依頼の観点 5.3）。
/// ファイルのノードではなく専用の行にして、<c>Files</c> の選択集計・除外・差分方式の記憶とは混ぜない。
/// 3 状態（内容も出す／構成だけ／出さない）、値の見本のオプトイン、取得結果の状況（日時・警告）を持つ。
/// <para>
/// 状態の持ち主は <see cref="ContextCollectViewModel"/>（<c>VbaLink</c> への保存・Excel からの取得・確認ダイアログを
/// 担う）。この行は画面に結ぶ値と、操作のコマンドだけを持つ薄い入れ物で、値の変更は持ち主が
/// <c>internal</c> のメソッドで行う（行そのものを書き換えても何も保存されない、という事故を防ぐ）。
/// </para>
/// </summary>
public sealed class VbaWorkbookInfoRowViewModel : ObservableObject
{
    private VbaWorkbookInfoLevel _level = VbaWorkbookInfoLevel.Full;
    private bool _sampleValues;
    private bool _includeFormulas;
    private bool _isVisible;
    private int _sampleRows = 5;
    private string _statusText = string.Empty;

    internal VbaWorkbookInfoRowViewModel(ICommand cycleCommand, ICommand toggleSampleCommand)
    {
        CycleCommand = cycleCommand;
        ToggleSampleValuesCommand = toggleSampleCommand;
    }

    /// <summary>行を出すか。VBA プロジェクトで、機能が有効で、この環境で使えるときだけ true。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        internal set
        {
            if (!SetProperty(ref _isVisible, value)) return;
            OnPropertyChanged(nameof(IsSampleNoticeVisible));
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
            OnPropertyChanged(nameof(ShowSampleControls));
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
        VbaWorkbookInfoLevel.Full => "内容も出す（シート・テーブル・名前・見出し・参照設定）。クリックで「構成だけ」にします。",
        VbaWorkbookInfoLevel.StructureOnly => "構成だけ（シート・テーブル・名前付き範囲）。クリックで「出さない」にします。",
        _ => "出さない（Excel にも問い合わせません）。クリックで「内容も出す」にします。",
    };

    /// <summary>読み上げ用の名前（色やアイコンだけに頼らない）。</summary>
    public string AutomationLabel => $"ブックの情報（{LevelLabel}）";

    /// <summary>値の見本をオンにしているか（既定オフ）。</summary>
    public bool SampleValues
    {
        get => _sampleValues;
        internal set
        {
            if (!SetProperty(ref _sampleValues, value)) return;
            OnPropertyChanged(nameof(SampleToggleText));
            OnPropertyChanged(nameof(SampleNoticeText));
            OnPropertyChanged(nameof(IsSampleNoticeVisible));
        }
    }

    /// <summary>値の見本に数式も含めるか（既定オフ）。</summary>
    public bool IncludeFormulas
    {
        get => _includeFormulas;
        internal set => SetProperty(ref _includeFormulas, value);
    }

    /// <summary>注意文に出す行数（<c>Vba.WorkbookInfoSampleRows</c>）。</summary>
    public int SampleRows
    {
        get => _sampleRows;
        internal set
        {
            if (!SetProperty(ref _sampleRows, value)) return;
            OnPropertyChanged(nameof(SampleNoticeText));
        }
    }

    /// <summary>値の見本のボタンの文言。オフのときは「オンにする」操作だと分かる言い方にする。</summary>
    public string SampleToggleText => _sampleValues ? "値の見本: オン（クリックでオフ）" : "値の見本: オフ（クリックでオンにする）";

    /// <summary>
    /// 値の見本がオンの間、<b>常時</b>出す注意（仕様書5.2）。依頼文コピーのドロップダウンにも同じ文を 1 行出す。
    /// オフのときは空。
    /// </summary>
    public string SampleNoticeText => _sampleValues
        ? $"値の見本: オン（各シートの先頭 {_sampleRows} 行が AI へ渡ります）"
        : string.Empty;

    /// <summary>注意文を出すか（オンのときだけ。見出し・行の非表示と関係なく、オンなら出す）。</summary>
    public bool IsSampleNoticeVisible => _sampleValues && _isVisible;

    /// <summary>値の見本・数式の操作を出すか。「構成だけ」「出さない」では見本は出ないので隠す。</summary>
    public bool ShowSampleControls => _level == VbaWorkbookInfoLevel.Full;

    /// <summary>取得日時・取得できなかった理由・省略の説明（1 行）。</summary>
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

    /// <summary>状態を巡回する（ファイルの 3 状態の操作と同じ）。</summary>
    public ICommand CycleCommand { get; }

    /// <summary>値の見本のオン・オフ。オンにするときは確認ダイアログを出す。</summary>
    public ICommand ToggleSampleValuesCommand { get; }
}
