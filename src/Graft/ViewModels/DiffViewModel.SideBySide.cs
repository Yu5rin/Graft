using System.Windows.Input;
using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="DiffViewModel"/> の分割ファイル（1ファイル400行上限のため。DiffViewModel.Jump.cs
/// と同じ理由）。差分の左右並列表示（機能改善）を担う: 表示方式の切り替え・設定への永続化
/// （<see cref="IsSideBySide"/> / <see cref="SideBySideChangeCommitted"/>）と、
/// 並列表示・統合表示それぞれの行組み立て（8.7）をまとめる。
///
/// 並列表示は、対応する行（削除→追加）を横に並べたペア行を作るだけで、
/// <see cref="ObservableCollection{T}"/>である<c>Lines</c>には表示中の行数ぶんの
/// <see cref="DiffLineViewModel"/>しか積まない。全文を先に実体化してから並べ替える、
/// といった処理は行わないため、8.13の折りたたみ（<c>ApplyExpansions</c>）による
/// 省略と合わせて、DiffView.axaml側のListBox仮想化がそのまま効く
/// （大きな差分でも並列表示への切り替えで実体化行数が増えない）。
/// </summary>
public sealed partial class DiffViewModel
{
    /// <summary>
    /// 並列表示（左右、既定）か統合表示（上下）か。既定値はSettings.Diff.SideBySide。
    /// DiffView.axamlのRadioButton（双方向バインド）からの変更はこのsetterを通るため、
    /// ユーザー操作による切り替えとみなして<see cref="SideBySideChangeCommitted"/>を発火する
    /// （FontSizeChangeCommittedと同じ考え方。設定側からの反映は<see cref="UpdateSettings"/>が
    /// 使う<see cref="ApplySideBySideFromSettings"/>を経由し、このsetterは通らないため
    /// 発火しない）。
    /// </summary>
    public bool IsSideBySide
    {
        get => _isSideBySide;
        set
        {
            if (!SetProperty(ref _isSideBySide, value)) return;
            RebuildRows();
            SideBySideChangeCommitted?.Invoke(this, value);
        }
    }

    /// <summary>
    /// 機能改善: diff表示ヘッダーでの並列／統合表示の切り替え確定通知。設定へ永続化するため
    /// ShellViewModel経由でSettingsViewModel.SetSideBySideLiveへ橋渡しする
    /// （DiffViewModel.FontSizeChangeCommittedのコメント参照）。
    /// </summary>
    public event EventHandler<bool>? SideBySideChangeCommitted;

    /// <summary>
    /// 設定側（SettingsViewModel経由の即時反映、または起動時の読み込み）からの反映専用。
    /// <see cref="IsSideBySide"/>のsetterと異なり<see cref="SideBySideChangeCommitted"/>は
    /// 発火しない（発火すると、他の画面での変更が巡り巡って自分自身の値を書き戻すだけの
    /// 無駄な保存が連鎖してしまうため）。
    /// </summary>
    private void ApplySideBySideFromSettings(bool value)
    {
        if (_isSideBySide == value) return;
        _isSideBySide = value;
        OnPropertyChanged(nameof(IsSideBySide));
        RebuildRows();
    }

    // ------------------------------------------------------------------
    // 8.7 並列／統合表示
    // ------------------------------------------------------------------

    private void RebuildRows()
    {
        Lines.Clear();
        var entries = IsFullyExpanded
            ? EnsureFullFlat().Select(l => new FlatEntry(l, -1)).ToList()
            : ApplyExpansions();

        var rows = IsSideBySide ? BuildSideBySideRows(entries) : BuildUnifiedRows(entries);
        var maxCells = 0;
        foreach (var row in rows)
        {
            Lines.Add(row);
            if (row.IsOmitted) continue;
            maxCells = Math.Max(maxCells, Math.Max(TextCells.Count(row.Left.Text), row.Right?.Text is { } rt ? TextCells.Count(rt) : 0));
        }
        MaxLineCells = maxCells;
        HorizontalOffset = 0;
    }

    // ------------------------------------------------------------------
    // 横スクロール（監査 H1: 長い行が行番号ごとずれる不具合の対処）
    // ------------------------------------------------------------------
    //
    // 以前は、行（Grid）全体を1つの横スクロールへ入れていたため、行ごとに幅の決まり方が
    // ばらばらになり、長い行だけ右の列が左へずれて、右側の行番号まで本文と一緒に動いた
    // （実機で確認）。いまは、行番号と左端バーの列を固定し、本文だけを共通の横位置
    // （HorizontalOffset）で動かす。左右両方の本文が同じ量だけ動くので、並べて見比べやすい。
    // 本文の最大幅は、表示中の行の「桁数」（全角は2桁）の最大値（MaxLineCells）から見積もる。
    // 実際の幅の計算（1桁のピクセル幅を掛ける）はフォントを知っているViewが担う。

    private int _maxLineCells;
    private double _horizontalOffset;

    /// <summary>表示中の行のうち最も長い行の桁数（全角は2桁として数える。タブは1桁）。</summary>
    public int MaxLineCells { get => _maxLineCells; private set => SetProperty(ref _maxLineCells, value); }

    /// <summary>本文の横スクロール位置（px、0以上）。折り返し中は常に0。</summary>
    public double HorizontalOffset
    {
        get => _horizontalOffset;
        set => SetProperty(ref _horizontalOffset, Math.Max(0, value));
    }

    private List<DiffLineViewModel> BuildUnifiedRows(List<FlatEntry> entries)
    {
        var rows = new List<DiffLineViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            var command = entry.OmittedKey >= 0 ? MakeExpandCommand(entry.OmittedKey) : null;
            rows.Add(new DiffLineViewModel(MakeCell(entry.Line), right: null, command));
        }
        return rows;
    }

    // 連続する削除行の並びと、それに続く連続する追加行の並びを同数ぶんだけ突き合わせて
    // ペア行を作る（DiffBuilder.ApplyInlineSpansと同じ考え方）。変更なし・省略行は単独行として扱う。
    private List<DiffLineViewModel> BuildSideBySideRows(List<FlatEntry> entries)
    {
        var rows = new List<DiffLineViewModel>();
        var i = 0;
        while (i < entries.Count)
        {
            var kind = entries[i].Line.Kind;
            if (kind != DiffLineKind.Removed && kind != DiffLineKind.Added)
            {
                AddPairedRow(rows, entries[i], entries[i]);
                i++;
                continue;
            }

            i = AppendChangeBlock(rows, entries, i);
        }
        return rows;
    }

    private int AppendChangeBlock(List<DiffLineViewModel> rows, List<FlatEntry> entries, int start)
    {
        var i = start;
        var removedStart = i;
        while (i < entries.Count && entries[i].Line.Kind == DiffLineKind.Removed) i++;
        var removedCount = i - removedStart;

        var addedStart = i;
        while (i < entries.Count && entries[i].Line.Kind == DiffLineKind.Added) i++;
        var addedCount = i - addedStart;

        var max = Math.Max(removedCount, addedCount);
        for (var k = 0; k < max; k++)
        {
            FlatEntry? left = k < removedCount ? entries[removedStart + k] : null;
            FlatEntry? right = k < addedCount ? entries[addedStart + k] : null;
            AddPairedRow(rows, left, right);
        }
        return i;
    }

    private void AddPairedRow(List<DiffLineViewModel> rows, FlatEntry? left, FlatEntry? right)
    {
        var leftCell = left is { } l ? MakeCell(l.Line) : null;
        var rightCell = right is { } r ? MakeCell(r.Line) : null;
        if (leftCell is null && rightCell is null) return;

        var omittedKey = left is { OmittedKey: >= 0 } lo ? lo.OmittedKey
            : right is { OmittedKey: >= 0 } ro ? ro.OmittedKey : -1;
        var command = omittedKey >= 0 ? MakeExpandCommand(omittedKey) : null;

        rows.Add(new DiffLineViewModel(leftCell ?? DiffCellViewModel.Blank, rightCell ?? DiffCellViewModel.Blank, command));
    }

    private ICommand MakeExpandCommand(int omittedIndex) => new RelayCommand(() => ExpandOmitted(omittedIndex));
}
