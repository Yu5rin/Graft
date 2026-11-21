using System.Collections.ObjectModel;
using System.Windows.Input;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// コマンドパレットに載せる1件の操作の定義。ShellViewModelが構築時に一度だけ組み立てる
/// 静的な一覧で、いずれも既存のICommand（ツールバー・サイドバー・ショートカットで既に
/// 到達できる操作）をそのまま参照する。新しいコマンドはここでは作らない（仕様どおり）。
/// </summary>
/// <param name="Title">一覧・検索対象になる表示名（既存のAutomationProperties.Name等、
/// 他画面で使っている表記に揃える）。</param>
/// <param name="Command">実行する既存のICommand。</param>
/// <param name="Parameter">Command.Execute/CanExecuteに渡すパラメータ（無ければnull）。</param>
/// <param name="Gesture">ショートカット表記。<see cref="ShortcutCatalog"/>から逆引きした値を
/// ShellViewModelが渡す（無い操作はnull）。</param>
/// <param name="Category">分類（ファイル／編集／表示／AIに渡す／AIから受け取る／履歴／設定とヘルプ）。
/// 各行に添えて表示し、候補を分類順に並べる。<see cref="PaletteCategories"/>の定数を使う。</param>
public sealed record PaletteCommandDescriptor(
    string Title, ICommand Command, object? Parameter, string? Gesture, string Category, Func<bool>? IsAvailable = null);

/// <summary>
/// コマンドパレットの分類名と、その並び順。ツールバー・メニューの区分（AIに渡す→AIから受け取る→履歴→
/// 設定とヘルプ）と、エディタの一般的な区分（ファイル・編集・表示）を合わせた7つ。
/// </summary>
public static class PaletteCategories
{
    public const string File = "ファイル";
    public const string Edit = "編集";
    public const string View = "表示";
    public const string Handoff = "AIに渡す";
    public const string Receive = "AIから受け取る";
    public const string History = "履歴";
    public const string Help = "設定とヘルプ";

    /// <summary>候補の分類の並び順。</summary>
    public static IReadOnlyList<string> Order { get; } = new[] { File, Edit, View, Handoff, Receive, History, Help };
}

/// <summary>コマンドパレットの候補一覧の1件（表示用）。</summary>
public sealed class CommandPaletteItem
{
    public CommandPaletteItem(PaletteCommandDescriptor descriptor)
    {
        Title = descriptor.Title;
        Category = descriptor.Category;
        GestureText = descriptor.Gesture ?? string.Empty;
        HasGesture = descriptor.Gesture is not null;
        Command = descriptor.Command;
        Parameter = descriptor.Parameter;
        IsEnabled = descriptor.Command.CanExecute(descriptor.Parameter);
    }

    /// <summary>操作名。</summary>
    public string Title { get; }

    /// <summary>分類名。各行の左に添えて表示する。</summary>
    public string Category { get; }

    /// <summary>ショートカット表記（無ければ空文字）。</summary>
    public string GestureText { get; }

    /// <summary>ショートカットが割り当てられている操作かどうか（Viewの表示切り替え用）。</summary>
    public bool HasGesture { get; }

    /// <summary>実行対象のコマンド。</summary>
    public ICommand Command { get; }

    /// <summary>Command.Execute/CanExecuteへ渡すパラメータ。</summary>
    public object? Parameter { get; }

    /// <summary>
    /// 今この瞬間に実行できるかどうか（構築時の<see cref="ICommand.CanExecute"/>の結果を
    /// スナップショットしたもの）。一覧を作り直す（Open・Query変更のたび）ごとに
    /// 再評価されるため、パレットを開いている間の状態変化を都度追いたい場合は
    /// クエリを打ち直せば最新化される（QuickOpenViewModelの一覧再構築と同じ考え方）。
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>読み上げ用の文言（8.14と同じ考え方）。実行できない項目はその旨を添える。</summary>
    public string AutomationName => (HasGesture, IsEnabled) switch
    {
        (true, true) => $"{Category}、{Title}（{GestureText}）",
        (false, true) => $"{Category}、{Title}",
        (true, false) => $"{Category}、{Title}（{GestureText}）、実行できません",
        (false, false) => $"{Category}、{Title}、実行できません",
    };
}

/// <summary>
/// コマンドパレット（Ctrl+Shift+P、全操作のあいまい検索・実行）のViewModel。
/// クイックオープン（<see cref="QuickOpenViewModel"/>）と同じ操作感・実装作法に揃える
/// （開閉・上下キー・Enter・Escapeの扱い、あいまい一致に既存の<see cref="FuzzyMatcher"/>を
/// 流用する点、マウスクリックでも確定できる点）。ファイル列挙のような非同期処理が無いため、
/// 対象コマンド一覧は構築時に固定で受け取る（QuickOpenの<c>_allFiles</c>相当）。
/// </summary>
public sealed class CommandPaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<PaletteCommandDescriptor> _allCommands;

    private bool _isOpen;
    private string _query = string.Empty;
    private CommandPaletteItem? _selectedResult;

    public CommandPaletteViewModel(IReadOnlyList<PaletteCommandDescriptor> commands)
    {
        _allCommands = commands ?? throw new ArgumentNullException(nameof(commands));
        CloseCommand = new RelayCommand(Close);
    }

    /// <summary>絞り込み結果（スコア順。クエリが空のときは全件を表示名の昇順で表示する）。</summary>
    public ObservableCollection<CommandPaletteItem> Results { get; } = new();

    /// <summary>オーバーレイが開いているかどうか。</summary>
    public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }

    /// <summary>検索ボックスの入力文字列。</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value)) UpdateResults();
        }
    }

    /// <summary>候補一覧での選択中の項目。</summary>
    public CommandPaletteItem? SelectedResult { get => _selectedResult; set => SetProperty(ref _selectedResult, value); }

    public ICommand CloseCommand { get; }

    /// <summary>開いた直後、検索ボックスへフォーカスするようView側へ知らせる。</summary>
    public event EventHandler? Opened;

    /// <summary>Ctrl+Shift+P。既に開いていれば閉じ（トグル）、閉じていれば開く。</summary>
    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
            return;
        }
        Open();
    }

    /// <summary>開く。クイックオープンと異なりファイル一覧の非同期読み込みが無いため同期で開く。</summary>
    public void Open()
    {
        IsOpen = true;
        _query = string.Empty;
        OnPropertyChanged(nameof(Query));
        UpdateResults();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Esc、または再度のCtrl+Shift+P。</summary>
    public void Close()
    {
        IsOpen = false;
        _query = string.Empty;
        OnPropertyChanged(nameof(Query));
        Results.Clear();
        SelectedResult = null;
    }

    /// <summary>上下キーでの選択移動。候補が無ければ何もしない。</summary>
    public void MoveSelection(int direction)
    {
        if (Results.Count == 0) return;

        var currentIndex = SelectedResult is null ? -1 : Results.IndexOf(SelectedResult);
        var nextIndex = ((currentIndex + direction) % Results.Count + Results.Count) % Results.Count;
        SelectedResult = Results[nextIndex];
    }

    /// <summary>
    /// Enter、またはマウスクリック。選択中の項目を実行する。実行できない状態
    /// （CanExecuteがfalse）の項目は選べないことが分かるよう表示するだけで、確定しても
    /// 何も起きない（QuickOpenと異なりファイルを開くだけの安全な操作ではなく、
    /// パッチの適用のような取り消しにくい操作も並ぶため、無効状態の誤操作は防ぐ）。
    /// </summary>
    public void ConfirmSelection()
    {
        if (SelectedResult is not { IsEnabled: true } item) return;

        Close();
        item.Command.Execute(item.Parameter);
    }

    private FuzzyMatcher.FuzzyMatch MatchTitleOrCategory(PaletteCommandDescriptor c)
    {
        var byTitle = FuzzyMatcher.TryMatch(_query, c.Title);
        return byTitle.IsMatch ? byTitle : FuzzyMatcher.TryMatch(_query, $"{c.Category} {c.Title}");
    }

    private static int CategoryRank(string category)
    {
        for (var i = 0; i < PaletteCategories.Order.Count; i++)
        {
            if (PaletteCategories.Order[i] == category) return i;
        }
        return PaletteCategories.Order.Count;
    }

    private void UpdateResults()
    {
        Results.Clear();

        // 環境によって存在しない機能（この環境では使えない Excel VBA 連携など）は、無効な行として見せるのではなく
        // 一覧に出さない。判定は開くたび・入力のたびに行うので、設定や環境が途中で変わっても追随する。
        var available = _allCommands.Where(c => c.IsAvailable?.Invoke() ?? true).ToList();

        IEnumerable<PaletteCommandDescriptor> ordered;
        if (_query.Length == 0)
        {
            // 分類ごとに並べる（分類の順は PaletteCategories.Order）。同じ分類の中は、定義した順
            // （よく使う順に並べてある）のまま。以前は操作名の五十音順で、関連する操作が散らばっていた。
            ordered = available.OrderBy(c => CategoryRank(c.Category));
        }
        else
        {
            // 操作名に一致しなければ、分類名つき（「履歴 取り消す」のような入力）でも探す。
            ordered = available
                .Select(c => (Descriptor: c, Match: MatchTitleOrCategory(c)))
                .Where(x => x.Match.IsMatch)
                .OrderBy(x => x.Match.Tier)
                .ThenBy(x => x.Match.RelativePathLength)
                .ThenBy(x => x.Descriptor.Title, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Descriptor);
        }

        foreach (var descriptor in ordered)
        {
            Results.Add(new CommandPaletteItem(descriptor));
        }

        // 実行できる項目があれば、それを初期選択にする（無効な項目が先頭に来て
        // 何もできないEnterを誘発しないようにするため）。
        SelectedResult = Results.FirstOrDefault(r => r.IsEnabled) ?? Results.FirstOrDefault();
    }
}
