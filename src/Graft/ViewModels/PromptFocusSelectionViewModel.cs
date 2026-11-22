using System.Collections.ObjectModel;
using System.Windows.Input;
using Graft.Core.PromptFocus;

namespace Graft.ViewModels;

/// <summary>
/// 依頼の観点のチップ1つ。<c>ToggleButton</c> の <c>IsChecked</c> に結ぶ。
/// 色だけに頼らず「選択中／未選択」を読み上げられるよう、<see cref="AutomationName"/> に状態を含める
/// （仕様書_ブックの情報と依頼の観点 6.2、8.14）。
/// </summary>
public sealed class PromptFocusChipViewModel : ObservableObject
{
    private readonly Action<PromptFocusChipViewModel, bool> _onToggled;
    private bool _isSelected;

    public PromptFocusChipViewModel(PromptFocus focus, bool isSelected, Action<PromptFocusChipViewModel, bool> onToggled)
    {
        Focus = focus;
        _isSelected = isSelected;
        _onToggled = onToggled;
    }

    /// <summary>この チップが表す観点。指示文（Body）は画面に出さない。</summary>
    public PromptFocus Focus { get; }

    public string Id => Focus.Id;
    public string Name => Focus.Name;
    public string Tooltip => Focus.Tooltip;

    /// <summary>ツールチップが空の自作の観点でも、HelpTip（常に1つは必要）が空文字にならないようにする。</summary>
    public string TooltipOrName => string.IsNullOrWhiteSpace(Focus.Tooltip) ? Focus.Name : Focus.Tooltip;

    /// <summary>読み上げ用の名前。「チップ名（選択中／未選択）」。</summary>
    public string AutomationName => $"{Focus.Name}（{(_isSelected ? "選択中" : "未選択")}）";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            OnPropertyChanged(nameof(AutomationName));
            _onToggled(this, value);
        }
    }

    /// <summary>外から状態をそろえるとき（すべて解除・読み込み）。通知は呼び出し側がまとめて1回出す。</summary>
    internal void SetSelectedSilently(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(AutomationName));
    }
}

/// <summary>見出し1つ分（「きれいに」など）のチップの並び。</summary>
public sealed class PromptFocusGroupViewModel
{
    public PromptFocusGroupViewModel(string title, IReadOnlyList<PromptFocusChipViewModel> chips)
    {
        Title = title;
        Chips = chips;
    }

    public string Title { get; }
    public IReadOnlyList<PromptFocusChipViewModel> Chips { get; }
}

/// <summary>
/// 依頼の観点のチップの選択状態（仕様書_ブックの情報と依頼の観点 6.2）。
/// <see cref="ContextCollectViewModel.Focus"/> として1つだけ持ち、依頼文コピーのドロップダウンと
/// コンテキスト収集の窓の<b>両方</b>が同じインスタンスを見る。片方で選べばもう片方にも反映されるのは、
/// 状態の持ち主が1つだから（<c>Context</c> を両者が共有しているのと同じ作り）。
///
/// 【カタログを後から差し替えられる作りの理由】組み込みの17件はコードにあるので、画面は最初から
/// 全チップを並べられる。自作の観点は <c>focus.json</c> の非同期読み込みが要るので、後から
/// <see cref="SetCatalog"/> で差し替える。選択は Id の集合で持ち、チップが作り直されても保たれる。
/// 読み込み前の状態で、保存済みの「自作の観点の Id」を黙って捨てないために、一覧に無い Id の掃除は
/// カタログが確定した <see cref="SetCatalog"/> のときだけ行う（読み込み前に掃除すると、自作の観点を
/// 選んでいたプロジェクトで選択が消える）。
/// </summary>
public sealed class PromptFocusSelectionViewModel : ObservableObject
{
    private readonly bool _isVba;
    private readonly HashSet<string> _selected;
    private IReadOnlyList<PromptFocus> _catalog;
    private bool _catalogIsFinal;
    private bool _changedByUser;

    /// <param name="catalog">最初に並べる観点（普通は組み込みの17件）。</param>
    /// <param name="isVba">VBA プロジェクトか。false のとき VBA 専用のチップは出さず、依頼文にも入れない。</param>
    /// <param name="initialIds">前回の選択（<c>ProjectOverrides.PromptFocusIds</c>）。</param>
    public PromptFocusSelectionViewModel(IReadOnlyList<PromptFocus> catalog, bool isVba, IEnumerable<string> initialIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(initialIds);
        _catalog = catalog;
        _isVba = isVba;
        _selected = new HashSet<string>(initialIds, StringComparer.Ordinal);
        Groups = new ObservableCollection<PromptFocusGroupViewModel>();
        ClearAllCommand = new RelayCommand(ClearAll, () => SelectedCount > 0);
        RebuildGroups();
    }

    /// <summary>選択が変わるたびに1回発火する（トークン推定の再計算・記憶の保存の契機）。</summary>
    public event EventHandler? Changed;

    /// <summary>画面に並べる見出しとチップ。空の見出し（VBA でないときの VBA など）は含めない。</summary>
    public ObservableCollection<PromptFocusGroupViewModel> Groups { get; }

    /// <summary>VBA プロジェクトか。</summary>
    public bool IsVba => _isVba;

    /// <summary>利用者がチップを操作したか。読み込みの完了で、利用者の操作を上書きしないための目印。</summary>
    public bool ChangedByUser => _changedByUser;

    /// <summary>「すべて解除」。</summary>
    public ICommand ClearAllCommand { get; }

    /// <summary>画面に出ていて選ばれている観点の数。</summary>
    public int SelectedCount => Groups.Sum(g => g.Chips.Count(c => c.IsSelected));

    /// <summary>パネルの見出し。選んでいるときは数を添える（「依頼の観点 3」）。</summary>
    public string HeaderText => SelectedCount > 0 ? $"依頼の観点 {SelectedCount}" : "依頼の観点";

    /// <summary>選ばれている観点の Id（画面の並び順）。一覧に無いもの・VBA でないときの VBA 専用は含めない。</summary>
    public IReadOnlyList<string> SelectedIds
        => PromptFocusComposer.ResolveSelected(_catalog, _selected, _isVba).Select(f => f.Id).ToArray();

    /// <summary>
    /// 記憶に保存する Id。カタログが確定するまでは、まだ読み込めていない自作の観点の Id を落とさないよう、
    /// 手元の選択をそのまま返す（確定後は <see cref="SelectedIds"/> と同じ）。
    /// </summary>
    public IReadOnlyList<string> IdsToPersist
        => _catalogIsFinal ? SelectedIds : _selected.OrderBy(i => i, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// 選ばれた観点を依頼文用の1つの文にまとめる。何も選ばれていなければ空の文字列
    /// （＝依頼文は従来と同じ）。
    /// </summary>
    /// <param name="hasWorkbookInfo">ブックの情報が依頼文に入っているか（Q1では常に false。ブックの情報は次の段階）。</param>
    public string ComposeText(bool hasWorkbookInfo = false)
    {
        var selected = PromptFocusComposer.ResolveSelected(_catalog, _selected, _isVba);
        return PromptFocusComposer.Compose(selected, _isVba, hasWorkbookInfo);
    }

    /// <summary>
    /// カタログ（組み込み＋自作）を差し替える。<paramref name="isFinal"/> が true のとき、一覧に無い Id
    /// （削除された自作の観点）と、VBA でないプロジェクトの VBA 専用の Id を黙って捨てる（仕様書4.1）。
    /// </summary>
    /// <returns>選択が変わった（捨てた）ときは true。</returns>
    public bool SetCatalog(IReadOnlyList<PromptFocus> catalog, bool isFinal)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        var before = _selected.Count;
        if (isFinal)
        {
            _catalogIsFinal = true;
            var valid = new HashSet<string>(
                catalog.Where(f => _isVba || !f.VbaOnly).Select(f => f.Id), StringComparer.Ordinal);
            _selected.IntersectWith(valid);
        }
        RebuildGroups();
        RaiseSelectionProperties();
        return _selected.Count != before;
    }

    /// <summary>
    /// 保存済みの選択で置き換える（プロジェクトを開いた直後の復元）。利用者がまだ触っていないときだけ呼ぶ。
    /// </summary>
    public void ReplaceSelection(IEnumerable<string> ids)
    {
        _selected.Clear();
        foreach (var id in ids) _selected.Add(id);
        RebuildGroups();
        RaiseSelectionProperties();
    }

    private void ClearAll()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        foreach (var chip in Groups.SelectMany(g => g.Chips)) chip.SetSelectedSilently(false);
        _changedByUser = true;
        RaiseSelectionProperties();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnChipToggled(PromptFocusChipViewModel chip, bool isSelected)
    {
        if (isSelected) _selected.Add(chip.Id);
        else _selected.Remove(chip.Id);
        _changedByUser = true;
        RaiseSelectionProperties();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseSelectionProperties()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HeaderText));
        ((RelayCommand)ClearAllCommand).RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 見出しごとにチップを並べ直す。並びは依頼文に連結する順（<see cref="PromptFocusComposer.OrderForDisplay"/>）と
    /// 同じものを使うので、「画面の並び＝優先順位」が構造的に保たれる。
    /// </summary>
    private void RebuildGroups()
    {
        Groups.Clear();
        var visible = PromptFocusComposer.OrderForDisplay(_catalog).Where(f => _isVba || !f.VbaOnly);
        foreach (var group in visible.GroupBy(f => f.Group))
        {
            var chips = group
                .Select(f => new PromptFocusChipViewModel(f, _selected.Contains(f.Id), OnChipToggled))
                .ToArray();
            Groups.Add(new PromptFocusGroupViewModel(group.Key, chips));
        }
    }
}
