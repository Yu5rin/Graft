using System.Collections.ObjectModel;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// エクスプローラツリーの1ノード（仕様書4.2）。ファイルまたはディレクトリを表す。
/// 子ノードの実列挙は遅延させ、初回展開時に <see cref="ExplorerViewModel"/> が
/// <see cref="ExpandRequested"/> を購読して行う（大きなフォルダで固まらないため）。
/// </summary>
public sealed class FileNodeViewModel : ObservableObject
{
    private FileTreeEntry _entry;
    private bool _isExpanded;
    private bool _isSelected;

    public FileNodeViewModel(FileTreeEntry entry, FileNodeViewModel? parent)
    {
        _entry = entry;
        Parent = parent;
        if (IsDirectory && !IsPlaceholder) Children.Add(CreatePlaceholder());
    }

    private FileNodeViewModel(bool isPlaceholder)
    {
        _entry = new FileTreeEntry { Name = "読み込み中...", RelativePath = string.Empty, FullPath = string.Empty };
        IsPlaceholder = isPlaceholder;
    }

    /// <summary>この項目より上の階層のノード。ルート直下の項目は null。</summary>
    public FileNodeViewModel? Parent { get; }

    /// <summary>子ノード。ディレクトリは初期状態でプレースホルダ1件のみを持つ。</summary>
    public ObservableCollection<FileNodeViewModel> Children { get; } = new();

    /// <summary>実際の子要素を読み込み済みかどうか（監視イベントによる再列挙の要否判定に使う）。</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>ツリー表示専用の「読み込み中...」ダミー項目かどうか。</summary>
    public bool IsPlaceholder { get; }

    public string Name => _entry.Name;
    public string RelativePath => _entry.RelativePath;
    public string FullPath => _entry.FullPath;
    public bool IsDirectory => _entry.IsDirectory;
    public bool IsExcluded => _entry.IsExcluded;
    public string? ExcludeReason => _entry.ExcludeReason;

    /// <summary>拡張子から見た「コードファイルらしさ」（アイコンの出し分けに使う、仕様書9.3）。</summary>
    public bool LooksLikeCode => !IsDirectory && CodeExtensions.Contains(System.IO.Path.GetExtension(Name));

    /// <summary>フォルダアイコンを表示するか（IsDirectoryは項目の生成後に変わらないため一度だけ評価すればよい）。</summary>
    public bool ShowFolderIcon => IsDirectory && !IsPlaceholder;

    /// <summary>file-codeアイコンを表示するか。</summary>
    public bool ShowCodeIcon => LooksLikeCode && !IsPlaceholder;

    /// <summary>汎用fileアイコンを表示するか。</summary>
    public bool ShowPlainFileIcon => !IsDirectory && !LooksLikeCode && !IsPlaceholder;

    /// <summary>展開状態。true への変化で初回のみ <see cref="ExpandRequested"/> を発火する。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value && IsDirectory && !IsLoaded)
            {
                IsLoaded = true;
                ExpandRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>選択状態。エクスプローラの操作対象（右クリック・F2・Delete等）の基準になる。</summary>
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>
    /// 細かいユーザビリティ改善4: このフォルダを最後に実列挙したときの、絞り込みに関わらない
    /// 全子ノード（ディスク順）。<see cref="ExplorerViewModel"/>が
    /// (1) 同一パスのノードインスタンスを使い回す（絞り込みで一時的にChildrenから除外されても
    /// 展開状態等を失わないようにする）ため、および (2) ディスクを再走査せずに絞り込み条件だけを
    /// 再適用する（<c>ApplyFilterToLevel</c>参照）ために保持する。まだ一度も実列挙していない
    /// （プレースホルダのみの）フォルダはnull。
    /// </summary>
    internal List<FileNodeViewModel>? AllChildrenCache { get; set; }

    /// <summary>種別（フォルダ／ファイル）と除外状態を含む読み上げ用テキスト（仕様書9.4）。</summary>
    public string AutomationName
    {
        get
        {
            var kind = IsDirectory ? "フォルダ" : "ファイル";
            return IsExcluded ? $"{Name}、{kind}、除外" : $"{Name}、{kind}";
        }
    }

    /// <summary>初めて展開されたとき（子の実列挙が必要になったとき）に発火する。</summary>
    public event EventHandler? ExpandRequested;

    /// <summary>この項目の内容（除外状態等）を更新する。子の並び替え等の副作用は起こさない。</summary>
    public void UpdateEntry(FileTreeEntry entry)
    {
        _entry = entry;
        OnPropertyChanged(nameof(IsExcluded));
        OnPropertyChanged(nameof(ExcludeReason));
        OnPropertyChanged(nameof(AutomationName));
    }

    /// <summary>子要素を実体の列挙結果で置き換える。</summary>
    public void ReplaceChildren(IEnumerable<FileNodeViewModel> children)
    {
        Children.Clear();
        foreach (var child in children) Children.Add(child);
    }

    /// <summary>
    /// 不具合2対応: 呼び出し元（<see cref="ExplorerViewModel"/>）が既に実列挙（子要素の反映）を
    /// 済ませたうえで、この項目を展開済み表示にする。<see cref="IsExpanded"/>のsetterと異なり
    /// <see cref="ExpandRequested"/>は発火させない（二重に列挙してしまうのを防ぐため）。
    /// 折りたたまれたフォルダの直下に新規ファイル・フォルダを作成した直後、ツリー上に
    /// 見えるようにする（自動展開）ために使う。
    /// </summary>
    public void MarkExpanded()
    {
        IsLoaded = true;
        if (!_isExpanded) SetProperty(ref _isExpanded, true, nameof(IsExpanded));
    }

    /// <summary>次に展開されたとき改めて実列挙させる（監視イベント・更新ボタンで使う）。</summary>
    public void ResetLoadState()
    {
        IsLoaded = false;
        if (IsExpanded)
        {
            IsLoaded = true;
            ExpandRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (IsDirectory && Children.Count == 0)
        {
            Children.Add(CreatePlaceholder());
        }
    }

    private static FileNodeViewModel CreatePlaceholder() => new(isPlaceholder: true);

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".js", ".jsx", ".ts", ".tsx", ".cs", ".java", ".go", ".rs", ".c", ".cpp", ".h",
        ".html", ".css", ".json", ".yaml", ".yml", ".sql", ".xml", ".sh", ".ps1",
    };
}
