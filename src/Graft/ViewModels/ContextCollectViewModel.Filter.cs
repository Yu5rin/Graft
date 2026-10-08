using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// コンテキスト収集ViewModelのうち、ファイル名の絞り込み（一覧の上の入力欄）を担う部分。
/// 本体が大きいため分割ファイルにしている。照合そのものは純ロジックの
/// <see cref="FileNameFilter"/> に任せ、ここは「いつ・何に対して掛け、結果をどう見せるか」だけを持つ。
/// </summary>
public sealed partial class ContextCollectViewModel
{
    /// <summary>
    /// 入力が止まってから絞り込みを掛けるまでの間隔。既存の保存のデバウンス（300ms、
    /// <c>PersistDebounceMs</c>）やエクスプローラの絞り込み（300ms）より短くしているのは、
    /// こちらはディスクを読まずメモリ上の一覧を舐めるだけで安く、待たされる感じを減らしたいため。
    /// 一方でゼロにしない理由は、10万件規模では1回の絞り込みに数十ミリ秒かかり、
    /// 1文字ごとに一覧を差し替えると、打鍵の最中に何度も一覧が組み直されて入力が引っかかるため。
    /// </summary>
    private const int FilterDebounceMs = 200;

    private string _filterText = string.Empty;
    private IReadOnlyList<string> _filterTerms = Array.Empty<string>();
    private IReadOnlyList<ContextFileNodeViewModel> _displayedFiles;

    /// <summary>
    /// 直近の絞り込みで一覧に出した行。次の絞り込み・解除のときに
    /// <see cref="ContextFileNodeViewModel.ShowFullPath"/> を下ろすために覚えておく
    /// （全ノードを毎回舐め直さず、立てた行だけを戻せる）。
    /// </summary>
    private List<ContextFileNodeViewModel> _filterRows = new();

    private int _filterMatchCount;
    private int _filterTotalCount;

    /// <summary>
    /// 一覧（ListBox）が実際に表示する行。絞り込みが無いときは<see cref="Files"/>そのもの
    /// （同じインスタンスなので、再走査での作り直しも、そのまま一覧に反映される）。
    /// 絞り込み中は、一致した**ファイルだけ**を平らに並べた別の一覧になる。
    ///
    /// 【なぜ絞り込み中はフォルダ行と「すべて」行を出さないか】
    /// フォルダ行を押すと、<see cref="ApplyStateRecursive"/> は配下の**全ファイル**へ再帰的に
    /// 状態を適用する。絞り込み中にこの行を残すと、画面に見えていない（絞り込みで隠れた）
    /// ファイルまで切り替わってしまい、利用者の予想と食い違う。絞り込み中は
    /// 「見えているものだけを触る」状態にするため、フォルダ行ごと出さない。
    /// 「すべて」行も同じ理由（プロジェクト全体に効くため）で出さない。
    ///
    /// 行は<see cref="Files"/>と**同じノードのインスタンス**を使う。ツリーとの間で状態を
    /// コピーしないので、絞り込み中に切り替えた状態は、解除したときのツリーにそのまま現れ、
    /// 保存（<see cref="PersistFileStatesAsync"/>）も通常どおり働く。
    /// </summary>
    public IReadOnlyList<ContextFileNodeViewModel> DisplayedFiles
    {
        get => _displayedFiles;
        private set => SetProperty(ref _displayedFiles, value);
    }

    /// <summary>
    /// ファイル名の絞り込み欄の入力。空（または空白だけ）なら絞り込みなし。
    /// 変更のたびに一覧を組み直すのではなく、<see cref="FilterDebounceMs"/>だけ入力が止まるのを
    /// 待ってから掛ける。**空にしたときだけは待たず、すぐ**元のツリーへ戻す
    /// （戻すのは安く、「消したのに戻らない」間が見えると壊れたように感じるため）。
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set => SetProperty(ref _filterText, value ?? string.Empty, OnFilterTextChanged);
    }

    /// <summary>入力欄に文字があるか（「×」ボタンの表示に使う）。</summary>
    public bool HasFilterText => _filterText.Length > 0;

    /// <summary>絞り込みが**掛かっている**か（入力途中でまだ反映前の状態は含まない）。</summary>
    public bool IsFiltering => _filterTerms.Count > 0;

    /// <summary>絞り込みで一致したファイルの数。</summary>
    public int FilterMatchCount => _filterMatchCount;

    /// <summary>絞り込み中で、一致が1件も無いか。</summary>
    public bool FilterHasNoMatches => IsFiltering && _filterMatchCount == 0;

    /// <summary>
    /// 件数の表示文言。例:「12件（ファイル全体 340件中）」。一致が無いときはその旨。
    /// 絞り込み中でなければ空。
    /// </summary>
    public string FilterSummary
    {
        get
        {
            if (!IsFiltering) return string.Empty;
            return _filterMatchCount == 0
                ? "一致するファイルがありません"
                : $"{_filterMatchCount}件（ファイル全体 {_filterTotalCount}件中）";
        }
    }

    /// <summary>絞り込み欄を空にする（「×」ボタン・Escキー）。</summary>
    public RelayCommand ClearFilterCommand { get; }

    private void OnFilterTextChanged()
    {
        OnPropertyChanged(nameof(HasFilterText));
        ClearFilterCommand.RaiseCanExecuteChanged();

        if (string.IsNullOrWhiteSpace(_filterText))
        {
            _filterTimer.Stop();
            ApplyFilter();
            return;
        }

        _filterTimer.Restart();
    }

    private void OnFilterTick()
    {
        _filterTimer.Stop();
        ApplyFilter();
    }

    /// <summary>
    /// 現在の入力を<see cref="Files"/>へ掛けて、<see cref="DisplayedFiles"/>を差し替える。
    /// 再走査（<see cref="RefreshAsync"/>）の直後にも呼ぶ。
    ///
    /// 照合は一覧のノード（UIスレッドが持つ）を読むため、スレッドプールへ逃がさずここで行う。
    /// 1件あたりは短い序数比較の部分一致で、10万件でも数十ミリ秒に収まる見込み
    /// （入力のたびではなく、デバウンスで1回にまとめている）。
    /// </summary>
    private void ApplyFilter()
    {
        var terms = FileNameFilter.ParseTerms(_filterText);
        _filterTerms = terms;

        // 前回の絞り込みで立てた行を戻す。ツリーの行は名前＋インデントの表示に戻る。
        foreach (var row in _filterRows) row.ShowFullPath = false;

        if (terms.Count == 0)
        {
            _filterRows = new List<ContextFileNodeViewModel>();
            _filterMatchCount = 0;
            _filterTotalCount = 0;
            DisplayedFiles = Files;
        }
        else
        {
            var rows = new List<ContextFileNodeViewModel>();
            var total = 0;
            foreach (var node in Files)
            {
                if (node.IsDirectory) continue; // フォルダ行・「すべて」行は絞り込み中は出さない
                total++;
                if (FileNameFilter.Matches(node.RelativePath, terms)) rows.Add(node);
            }

            foreach (var row in rows) row.ShowFullPath = true;
            _filterRows = rows;
            _filterMatchCount = rows.Count;
            _filterTotalCount = total;
            DisplayedFiles = rows;
        }

        OnPropertyChanged(nameof(IsFiltering));
        OnPropertyChanged(nameof(FilterMatchCount));
        OnPropertyChanged(nameof(FilterHasNoMatches));
        OnPropertyChanged(nameof(FilterSummary));
    }
}
