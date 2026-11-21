using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Features;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>選択画面の1行（起動中のブック1件）。</summary>
public sealed class VbaWorkbookItemViewModel
{
    public VbaWorkbookItemViewModel(VbaWorkbookInfo info)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>ブックの情報。</summary>
    public VbaWorkbookInfo Info { get; }

    /// <summary>ブックのファイル名。</summary>
    public string Name => Info.Name;

    /// <summary>ブックのフルパス。</summary>
    public string FullName => Info.FullName;

    /// <summary>選べるか。選べないものは理由つきで無効にする（仕様 7.1 の 2）。</summary>
    public bool IsSelectable => Info.Availability == VbaWorkbookAvailability.Available;

    /// <summary>状態の短い見出し。選べるブックでも、読み取り専用は添える。</summary>
    public string StatusText => Info.Availability switch
    {
        VbaWorkbookAvailability.Available => Info.IsReadOnly ? "読み取り専用" : string.Empty,
        VbaWorkbookAvailability.NoMacroFormat => "マクロ不可",
        VbaWorkbookAvailability.Locked => "ロック中",
        VbaWorkbookAvailability.AccessNotTrusted => "アクセス未許可",
        VbaWorkbookAvailability.UrlLocation => "OneDrive／SharePoint",
        VbaWorkbookAvailability.ProtectedView => "保護ビュー",
        VbaWorkbookAvailability.Unresponsive => "応答なし",
        _ => string.Empty,
    };

    /// <summary>選べない理由、または補足（読み取り専用の注意）。</summary>
    public string ReasonText => Info.UnavailableReason
        ?? (Info.IsReadOnly ? "読み取り専用で開いています。反映はできますが、ブックの保存はできません。" : string.Empty);

    /// <summary>読み上げ用の名前。</summary>
    public string AutomationName
        => IsSelectable ? $"{Name}、{FullName}" : $"{Name}、{StatusText}のため選べません";
}

/// <summary>選択画面の確定結果。</summary>
/// <param name="WorkbookFullName">ブックのフルパス。</param>
/// <param name="ProjectName">プロジェクトの既定の表示名（ブック名、拡張子なし）。</param>
/// <param name="MirrorRoot">ミラーフォルダ（<c>Project.Root</c> になる）。</param>
/// <param name="ModuleCount">ミラーに書いたモジュールの数。既存のミラーを使ったときは -1。</param>
public sealed record VbaWorkbookPickerResult(string WorkbookFullName, string ProjectName, string MirrorRoot, int ModuleCount);

/// <summary>
/// 「Excel ブックから追加」の選択画面（<c>VbaWorkbookPickerWindow</c>）の ViewModel（仕様書_VBA連携 7.1）。
/// 起動中のブックを一覧にし、選べないものは理由つきで無効にする。ミラーフォルダの場所（既定はデータ保存先）を選び、
/// 同じ名前のミラーが既にあれば「既存のミラーを使う」「作り直す」を選ばせる。確定すると全コンポーネントを
/// Excel から読んでミラーと同期基準を作る。プロジェクトの登録そのものは呼び出し側
/// （<see cref="ProjectPaneViewModel"/>）が行う。
/// </summary>
public sealed class VbaWorkbookPickerViewModel : ObservableObject
{
    /// <summary>
    /// 一覧の下に常に添える注意（仕様 5.2）。OneDrive 同期フォルダ上など、対応していない場所のブックは
    /// ROT の表示名が URL にならず一覧に出ないことがあるため、「出ない」ときの手がかりを先に伝える。
    /// </summary>
    public const string ListNote =
        "一覧に出ないブックは、OneDrive 同期フォルダ上など対応していない場所にある可能性があります。" +
        "Excel で開いているブックのうち、保存済みで、マクロを保存できる形式（.xlsm／.xlsb／.xlam／.xls）のものが対象です。";

    private readonly VbaSyncService _service;
    private readonly IDialogService _dialogs;
    private VbaWorkbookItemViewModel? _selected;
    private string _mirrorFolder = string.Empty;
    private bool _isCustomFolder;
    private bool _existingMirrorFound;
    private bool _rebuildMirror;
    private bool _isBusy;
    private bool _hasLoaded;
    private GraftIssue? _error;

    public VbaWorkbookPickerViewModel(VbaSyncService service, IDialogService dialogs)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy, context: "Excel ブック一覧の更新");
        BrowseMirrorFolderCommand = new AsyncRelayCommand(BrowseMirrorFolderAsync, () => !IsBusy, context: "ミラーフォルダの選択");
    }

    /// <summary>起動中のブック。</summary>
    public ObservableCollection<VbaWorkbookItemViewModel> Workbooks { get; } = new();

    /// <summary>選択中のブック。</summary>
    public VbaWorkbookItemViewModel? SelectedWorkbook
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(CanConfirm));

            // 利用者が場所を自分で選んでいない間は、選んだブックに合わせた既定の場所へ追随させる。
            if (!_isCustomFolder && value is not null)
            {
                SetMirrorFolderCore(_service.DefaultMirrorRoot(value.FullName));
            }
        }
    }

    /// <summary>ミラーフォルダ。既定は <c>&lt;データ保存先&gt;/vba-mirrors/&lt;ブック名&gt;-&lt;短いハッシュ&gt;</c>。</summary>
    public string MirrorFolder
    {
        get => _mirrorFolder;
        set
        {
            _isCustomFolder = true;
            SetMirrorFolderCore(value);
        }
    }

    /// <summary>同じ場所に、使えるミラー（ファイル、または同期状態）が既にあるか。</summary>
    public bool ExistingMirrorFound
    {
        get => _existingMirrorFound;
        private set => SetProperty(ref _existingMirrorFound, value);
    }

    /// <summary>
    /// 「作り直す」を選んでいるか（既存のミラーをごみ箱へ送って、Excel から作り直す）。false は
    /// 「既存のミラーを使う（三方比較で続ける）」。既存のミラーが無いときは意味を持たない。
    /// </summary>
    public bool RebuildMirror
    {
        get => _rebuildMirror;
        set
        {
            if (SetProperty(ref _rebuildMirror, value)) OnPropertyChanged(nameof(ReuseExistingMirror));
        }
    }

    /// <summary>「既存のミラーを使う」を選んでいるか（<see cref="RebuildMirror"/> の逆。ラジオボタン用）。</summary>
    public bool ReuseExistingMirror
    {
        get => !_rebuildMirror;
        set => RebuildMirror = !value;
    }

    /// <summary>一覧の読み込みや確定の処理中。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanConfirm));
        }
    }

    /// <summary>一覧を一度読み込み終えたか（空の一覧の案内を、読み込み前に出さないため）。</summary>
    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => SetProperty(ref _hasLoaded, value);
    }

    /// <summary>直近の失敗（E802 の案内など）。画面にその場で出す。</summary>
    public GraftIssue? Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(ErrorText));
            OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>失敗の表示文（見出し・詳細・対処）。</summary>
    public string ErrorText => _error is null ? string.Empty : $"{_error.ToDisplayText()}\n対処: {_error.Remedy}";

    /// <summary>失敗があるか。</summary>
    public bool HasError => _error is not null;

    /// <summary>一覧の下に添える注意（<see cref="ListNote"/>）。</summary>
    public string Note => ListNote;

    /// <summary>追加できるか（選べるブックを選んでいて、処理中でなく、ミラーの場所が入っている）。</summary>
    public bool CanConfirm => !IsBusy && SelectedWorkbook is { IsSelectable: true } && !string.IsNullOrWhiteSpace(MirrorFolder);

    /// <summary>一覧の更新。</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>ミラーフォルダの場所を選ぶ。</summary>
    public ICommand BrowseMirrorFolderCommand { get; }

    /// <summary>確定の結果。<see cref="ConfirmAsync"/> が true を返したときだけ入る。</summary>
    public VbaWorkbookPickerResult? Result { get; private set; }

    /// <summary>起動中のブックを読み込む。失敗（E802 など）は <see cref="Error"/> に出す。</summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            var listed = await _service.ListWorkbooksAsync().ConfigureAwait(true);
            var previous = SelectedWorkbook?.FullName;
            Workbooks.Clear();
            if (!listed.IsSuccess)
            {
                Error = listed.Errors.FirstOrDefault() ?? listed.Issues.FirstOrDefault();
                return;
            }

            foreach (var info in listed.Value) Workbooks.Add(new VbaWorkbookItemViewModel(info));
            SelectedWorkbook = Workbooks.FirstOrDefault(w => w.FullName == previous)
                               ?? Workbooks.FirstOrDefault(w => w.IsSelectable);
        }
        finally
        {
            HasLoaded = true;
            IsBusy = false;
        }
    }

    /// <summary>
    /// 確定。既存のミラーを使うときを除いて、全コンポーネントを Excel から読んでミラーと同期基準を作る。
    /// 失敗したら画面に残して false（利用者が原因を直して、もう一度押せる）。
    /// </summary>
    public async Task<bool> ConfirmAsync()
    {
        if (!CanConfirm || SelectedWorkbook is not { } workbook) return false;

        IsBusy = true;
        Error = null;
        try
        {
            string root;
            try
            {
                root = Path.GetFullPath(MirrorFolder.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Error = GraftIssue.Of(ErrorCode.E201, $"ミラーフォルダのパスが正しくありません: {ex.Message}", path: MirrorFolder);
                return false;
            }

            var name = Path.GetFileNameWithoutExtension(workbook.Name);
            if (ExistingMirrorFound && !RebuildMirror)
            {
                // 既存のミラーを使う。同じ Id の同期状態が残っていれば、三方比較で続ける。
                Result = new VbaWorkbookPickerResult(workbook.FullName, name, root, -1);
                return true;
            }

            var created = await _service.CreateMirrorAsync(workbook.FullName, root, rebuild: ExistingMirrorFound).ConfigureAwait(true);
            if (!created.IsSuccess)
            {
                Error = created.Errors.FirstOrDefault() ?? created.Issues.FirstOrDefault();
                return false;
            }

            Result = new VbaWorkbookPickerResult(workbook.FullName, name, created.Value.MirrorRoot, created.Value.ModuleCount);
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BrowseMirrorFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("ミラーフォルダの場所を選択").ConfigureAwait(true);
        if (!string.IsNullOrEmpty(folder)) MirrorFolder = folder;
    }

    private void SetMirrorFolderCore(string value)
    {
        if (SetProperty(ref _mirrorFolder, value, nameof(MirrorFolder)))
        {
            OnPropertyChanged(nameof(CanConfirm));
        }

        // 場所が変わるたびに、既存のミラーの有無を調べ直す（ファイルの一覧を見るだけで軽い）。
        ExistingMirrorFound = !string.IsNullOrWhiteSpace(value) && SafeMirrorExists(value);
        if (!ExistingMirrorFound) RebuildMirror = false;
    }

    private bool SafeMirrorExists(string value)
    {
        try
        {
            return _service.MirrorExists(value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException
                                       or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
