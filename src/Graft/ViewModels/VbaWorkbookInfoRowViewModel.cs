using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Graft.Core.Vba;

namespace Graft.ViewModels;

/// <summary>
/// コンテキスト収集の窓の「ブックの情報」の行（仕様書_ブックの情報と依頼の観点 5.3）。
/// ファイルのノードではなく専用の行にして、<c>Files</c> の選択集計・除外・差分方式の記憶とは混ぜない。
/// 3 状態（内容も出す／構成だけ／出さない）と、その下の<b>シートごとの見せ方の一覧</b>（v0.4）、取得結果の状況
/// （日時・警告）を持つ。
/// <para>
/// 状態の持ち主は <see cref="ContextCollectViewModel"/>（<c>VbaLink</c> への保存・Excel からの取得・確認ダイアログを
/// 担う）。この行は画面に結ぶ値と、操作のコマンドだけを持つ薄い入れ物で、値の変更は持ち主が
/// <c>internal</c> のメソッドで行う（行そのものを書き換えても何も保存されない、という事故を防ぐ）。
/// </para>
/// <para>
/// 【ブック全体の「値の見本」のオン・オフは持たない】 v0.4 で、値を渡すかどうかはシートごとの見せ方に一本化した
/// （画面の操作が二重にならないように。以前のオンは、全シート「見出し＋N 行」へ移行する）。
/// </para>
/// </summary>
public sealed class VbaWorkbookInfoRowViewModel : ObservableObject
{
    private VbaWorkbookInfoLevel _level = VbaWorkbookInfoLevel.Full;
    private bool _isVisible;
    private int _defaultHeaderRows = VbaSheetViewLimits.DefaultHeaderRows;
    private int _allMaxRows = VbaSheetViewLimits.DefaultAllMaxRows;
    private int _allMaxColumns = VbaSheetViewLimits.DefaultAllMaxColumns;
    private string _statusText = string.Empty;

    internal VbaWorkbookInfoRowViewModel(ICommand cycleCommand)
    {
        CycleCommand = cycleCommand;
        Sheets.CollectionChanged += (_, _) => RaiseSheetStateChanged();
    }

    /// <summary>行を出すか。VBA プロジェクトで、機能が有効で、この環境で使えるときだけ true。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        internal set
        {
            if (!SetProperty(ref _isVisible, value)) return;
            RaiseSheetStateChanged();
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
            RaiseSheetStateChanged();
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
        VbaWorkbookInfoLevel.Full => "内容も出す（シート・テーブル・名前・参照設定と、下で選んだシートの値）。クリックで「構成だけ」にします。",
        VbaWorkbookInfoLevel.StructureOnly => "構成だけ（シート・テーブル・名前付き範囲。値は渡しません）。クリックで「出さない」にします。",
        _ => "出さない（Excel にも問い合わせません）。クリックで「内容も出す」にします。",
    };

    /// <summary>読み上げ用の名前（色やアイコンだけに頼らない）。</summary>
    public string AutomationLabel => $"ブックの情報（{LevelLabel}）";

    /// <summary>
    /// 「見出し＋N 行」を選んだときの N の既定（設定 <c>Vba.WorkbookInfoSampleRows</c>）。
    /// </summary>
    public int DefaultHeaderRows
    {
        get => _defaultHeaderRows;
        internal set => SetProperty(ref _defaultHeaderRows, value);
    }

    /// <summary>「全部」「範囲を指定」の最大行数（設定）。案内文に出す。</summary>
    public int AllMaxRows
    {
        get => _allMaxRows;
        internal set
        {
            if (SetProperty(ref _allMaxRows, value)) OnPropertyChanged(nameof(LimitsText));
        }
    }

    /// <summary>「全部」「範囲を指定」の最大列数（設定）。案内文に出す。</summary>
    public int AllMaxColumns
    {
        get => _allMaxColumns;
        internal set
        {
            if (SetProperty(ref _allMaxColumns, value)) OnPropertyChanged(nameof(LimitsText));
        }
    }

    /// <summary>シートの一覧の下に出す、「全部」の上限の案内。</summary>
    public string LimitsText
        => string.Create(CultureInfo.InvariantCulture,
            $"「全部」「範囲を指定」は、1 シートあたり最大 {_allMaxRows} 行・{_allMaxColumns} 列まで渡します（超えた分は切って、依頼文にその旨を書きます。上限は設定の「VBA 連携」で変えられます）。");

    /// <summary>
    /// シートごとの見せ方の一覧（取得したブックのシート。消えたシートの記録は保存には残るが、ここには出さない）。
    /// </summary>
    public ObservableCollection<VbaSheetViewRowViewModel> Sheets { get; } = new();

    /// <summary>シートの一覧を出すか。「内容も出す」のときだけ（構成だけ・出さないでは、シートごとの見せ方は効かない）。</summary>
    public bool ShowSheetViews => _isVisible && _level == VbaWorkbookInfoLevel.Full && Sheets.Count > 0;

    /// <summary>シートの一覧がまだ無いとき（取得前・取得失敗）の案内を出すか。</summary>
    public bool ShowSheetViewsPlaceholder => _isVisible && _level == VbaWorkbookInfoLevel.Full && Sheets.Count == 0;

    /// <summary>値を AI に渡すシートの数（「内容も出す」のときの、見せ方が値を渡すもの）。</summary>
    public int ValueSheetCount => _level == VbaWorkbookInfoLevel.Full ? Sheets.Count(s => s.PassesValues) : 0;

    /// <summary>
    /// 値を渡すシートがある間、<b>常時</b>出す注意（仕様書5.2）。依頼文コピーのドロップダウンにも同じ文を 1 行出す。
    /// 渡すシートが無いときは空。
    /// </summary>
    public string ValueNoticeText => ValueSheetCount > 0
        ? $"シートの値: {ValueSheetCount} 枚のシートの値が AI へ渡ります（{string.Join("・", Sheets.Where(s => s.PassesValues).Take(3).Select(s => s.Name))}{(ValueSheetCount > 3 ? " ほか" : string.Empty)}）"
        : string.Empty;

    /// <summary>注意文を出すか。値が実際に渡るときだけ（構成だけ・出さない、または渡すシートが無いときは出さない）。</summary>
    public bool IsValueNoticeVisible => _isVisible && ValueSheetCount > 0;

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

    /// <summary>一覧の状態（見せ方・件数）が変わったとき、依存する表示を更新する。</summary>
    internal void RaiseSheetStateChanged()
    {
        OnPropertyChanged(nameof(ShowSheetViews));
        OnPropertyChanged(nameof(ShowSheetViewsPlaceholder));
        OnPropertyChanged(nameof(ValueSheetCount));
        OnPropertyChanged(nameof(ValueNoticeText));
        OnPropertyChanged(nameof(IsValueNoticeVisible));
    }
}

/// <summary>シートの見せ方の選択肢（ComboBox の項目）。</summary>
public sealed record VbaSheetModeOption(VbaSheetViewMode Mode, string Label)
{
    /// <summary>画面に並べる順の選択肢（同じインスタンスを共有する。選択の同一性を参照で比べるため）。</summary>
    public static IReadOnlyList<VbaSheetModeOption> All { get; } =
        VbaSheetViewModes.InDisplayOrder.Select(m => new VbaSheetModeOption(m, VbaSheetViewModes.Label(m))).ToArray();

    /// <summary>見せ方に対応する選択肢。</summary>
    public static VbaSheetModeOption Of(VbaSheetViewMode mode) => All.First(o => o.Mode == mode);

    /// <summary>ComboBox に出す文字。</summary>
    public override string ToString() => Label;
}

/// <summary>
/// シート 1 枚の見せ方の行（コンテキスト収集の窓の「ブックの情報」の下の一覧）。
/// 見せ方・行数・範囲・数式の変更は、持ち主の <see cref="ContextCollectViewModel"/> に頼んで、保存・取り直し・
/// 確認ダイアログまでを任せる（この行は値を持つだけ）。
/// </summary>
public sealed class VbaSheetViewRowViewModel : ObservableObject
{
    private readonly ContextCollectViewModel _owner;
    private VbaSheetModeOption _selectedMode = VbaSheetModeOption.Of(VbaSheetViewMode.StructureOnly);
    private string _rowsText = VbaSheetViewLimits.DefaultHeaderRows.ToString(CultureInfo.InvariantCulture);
    private string _rowsError = string.Empty;
    private string _rangeText = string.Empty;
    private string _rangeError = string.Empty;
    private bool _includeFormulas;
    private string _noteText = string.Empty;
    private string _usedRangeText = string.Empty;
    private bool _passesValues;

    // 持ち主からの反映中（画面の値を書き戻している間）は、変更を持ち主へ通知し返さない。
    private bool _applying;

    internal VbaSheetViewRowViewModel(ContextCollectViewModel owner, string name, string codeName)
    {
        _owner = owner;
        Name = name;
        CodeName = codeName;
    }

    /// <summary>シートの表示名。</summary>
    public string Name { get; internal set; }

    /// <summary>シートのコード名（見せ方の記憶の鍵。表示名を変えても外れない）。</summary>
    public string CodeName { get; }

    /// <summary>「表示名（コード名）」。</summary>
    public string Title => Name == CodeName ? Name : $"{Name}（{CodeName}）";

    /// <summary>使用範囲の文字（空のシートは「空」）。</summary>
    public string UsedRangeText
    {
        get => _usedRangeText;
        internal set => SetProperty(ref _usedRangeText, value);
    }

    /// <summary>選べる見せ方。</summary>
    public IReadOnlyList<VbaSheetModeOption> ModeOptions => VbaSheetModeOption.All;

    /// <summary>選ばれている見せ方。利用者が変えると、持ち主が確認・保存・取り直しをする（断られたら元に戻る）。</summary>
    public VbaSheetModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (value is null || !SetProperty(ref _selectedMode, value)) return;
            RaiseShape();
            if (_applying) return;
            _ = SafeHandler.RunAsync("シートの見せ方の変更", () => _owner.SetSheetViewModeAsync(CodeName, value.Mode));
        }
    }

    /// <summary>「見出し＋N 行」の N（入力欄の文字）。</summary>
    public string RowsText
    {
        get => _rowsText;
        set
        {
            if (!SetProperty(ref _rowsText, value ?? string.Empty) || _applying) return;
            _ = SafeHandler.RunAsync("見出しの下の行数の変更", async () =>
            {
                RowsError = await _owner.SetSheetHeaderRowsAsync(CodeName, _rowsText).ConfigureAwait(true) ?? string.Empty;
            });
        }
    }

    /// <summary>N の入力の誤り（空なら誤りなし）。</summary>
    public string RowsError
    {
        get => _rowsError;
        private set
        {
            if (!SetProperty(ref _rowsError, value)) return;
            OnPropertyChanged(nameof(HasRowsError));
        }
    }

    public bool HasRowsError => _rowsError.Length > 0;

    /// <summary>範囲の入力欄の文字（例 <c>A1:F30</c>）。</summary>
    public string RangeText
    {
        get => _rangeText;
        set
        {
            if (!SetProperty(ref _rangeText, value ?? string.Empty) || _applying) return;
            _ = SafeHandler.RunAsync("シートの範囲の変更", async () =>
            {
                RangeError = await _owner.SetSheetRangeAsync(CodeName, _rangeText).ConfigureAwait(true) ?? string.Empty;
            });
        }
    }

    /// <summary>範囲の入力の誤り（空なら誤りなし）。誤りがあるうちは、そのシートの値は依頼文に入らない。</summary>
    public string RangeError
    {
        get => _rangeError;
        internal set
        {
            if (!SetProperty(ref _rangeError, value)) return;
            OnPropertyChanged(nameof(HasRangeError));
        }
    }

    public bool HasRangeError => _rangeError.Length > 0;

    /// <summary>数式も含めるか。</summary>
    public bool IncludeFormulas
    {
        get => _includeFormulas;
        set
        {
            if (!SetProperty(ref _includeFormulas, value) || _applying) return;
            _ = SafeHandler.RunAsync("数式を含める設定の切り替え", () => _owner.SetSheetIncludeFormulasAsync(CodeName, value));
        }
    }

    /// <summary>取得結果についての注記（切った行数・列数、範囲の外、など）。無ければ空。</summary>
    public string NoteText
    {
        get => _noteText;
        internal set
        {
            if (!SetProperty(ref _noteText, value)) return;
            OnPropertyChanged(nameof(HasNote));
        }
    }

    public bool HasNote => _noteText.Length > 0;

    /// <summary>このシートの値が AI に渡る見せ方か（範囲は有効な入力があるときだけ）。</summary>
    public bool PassesValues
    {
        get => _passesValues;
        private set => SetProperty(ref _passesValues, value);
    }

    /// <summary>N の入力欄を出すか。</summary>
    public bool ShowRows => _selectedMode.Mode == VbaSheetViewMode.HeaderRows;

    /// <summary>範囲の入力欄を出すか。</summary>
    public bool ShowRange => _selectedMode.Mode == VbaSheetViewMode.Range;

    /// <summary>数式のチェックを出すか（値が渡る見せ方のとき）。</summary>
    public bool ShowFormulas => VbaSheetViewModes.IsValueMode(_selectedMode.Mode);

    /// <summary>読み上げ用の名前。</summary>
    public string AutomationLabel => $"シート {Title} の見せ方（{_selectedMode.Label}）";

    /// <summary>
    /// 持ち主が決めた見せ方を画面の値へ反映する（保存済みの見せ方・断られたときの巻き戻し・取り直し後）。
    /// 持ち主へは通知し返さない。入力欄（範囲・行数）の文字は、<paramref name="keepDraft"/> なら（入力中なので）そのまま残す。
    /// </summary>
    internal void Apply(VbaSheetView view, bool keepDraft)
    {
        _applying = true;
        try
        {
            SelectedMode = VbaSheetModeOption.Of(view.Mode);
            IncludeFormulas = view.IncludeFormulas;
            PassesValues = view.PassesValues;
            // 入力途中の欄（範囲・行数）は、持ち主が「いま入力中」と言うときは触らない（打っている文字を消さない）。
            if (!keepDraft)
            {
                RowsText = VbaSheetViewLimits.ClampHeaderRows(view.HeaderRows).ToString(CultureInfo.InvariantCulture);
                RowsError = string.Empty;
                RangeText = view.Range ?? string.Empty;
                if (view.Mode != VbaSheetViewMode.Range || view.Range is not null) RangeError = string.Empty;
            }
        }
        finally
        {
            _applying = false;
        }

        RaiseShape();
    }

    /// <summary>範囲の入力が必要なのに空・不正な間の案内（入力欄の下に出す）。</summary>
    internal void ShowRangeRequired()
    {
        if (RangeError.Length == 0) RangeError = "範囲を入力してください（例 A1:F30）。入力するまで、このシートの値は渡しません。";
    }

    private void RaiseShape()
    {
        OnPropertyChanged(nameof(ShowRows));
        OnPropertyChanged(nameof(ShowRange));
        OnPropertyChanged(nameof(ShowFormulas));
        OnPropertyChanged(nameof(AutomationLabel));
    }
}
