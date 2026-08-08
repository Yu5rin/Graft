using System.Collections.ObjectModel;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// 課題1（設定 <see cref="Settings.ShowPreview"/>）: 適用前プレビューダイアログ
/// （<see cref="Views.ApplyPreviewWindow"/>）用の軽量ViewModel。
///
/// v1.5仕様書6.8「プレビューとインライン編集」の「設定showPreview: falseの場合はプレビューを
/// スキップする」を踏まえ、trueの場合に書き込み前の最終確認として差分を見せる役割を担う。
/// 既存の<see cref="Views.DiffView"/>/<see cref="DiffViewModel"/>をそのまま流用する。
///
/// メイン画面（MainViewModel.Blocks/SelectedBlock/Diff）とは独立した一覧・DiffViewModelを
/// 持たせている。理由は、このダイアログでの選択操作が背後の接ぎ木パネルの選択状態や
/// エディタ側のdiffタブに影響しないようにするため。表示対象も「実際に書き込まれるもの」
/// （チェック済みかつ適用可能なブロック）だけに絞り、要確認・失敗のブロックは
/// 接ぎ木パネル側で既に常時表示されているためここでは扱わない。
/// </summary>
public sealed class ApplyPreviewViewModel : ObservableObject
{
    private BlockItemViewModel? _selectedItem;

    public ApplyPreviewViewModel(IReadOnlyList<BlockPlan> plansToApply, Settings settings, IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(plansToApply);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ui);

        Items = new ObservableCollection<BlockItemViewModel>(plansToApply.Select(p => new BlockItemViewModel(p)));
        Diff = new DiffViewModel(settings, ui)
        {
            WordWrap = settings.Diff.WordWrap,
            ShowWhitespace = settings.Diff.ShowWhitespace,
        };
        SummaryText = Items.Count == 1
            ? "1件を適用します。内容を確認してください。"
            : $"{Items.Count}件を適用します。内容を確認してください。";

        SelectedItem = Items.Count > 0 ? Items[0] : null;
    }

    /// <summary>実際に書き込まれる対象の一覧（チェック済みかつ適用可能なブロックのみ）。</summary>
    public ObservableCollection<BlockItemViewModel> Items { get; }

    /// <summary><see cref="SelectedItem"/>の差分を表示する。</summary>
    public DiffViewModel Diff { get; }

    /// <summary>見出しの件数サマリ（「N件を適用します」相当）。</summary>
    public string SummaryText { get; }

    public BlockItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            if (value is null) Diff.Clear(); else Diff.Load(value.Plan);
        }
    }
}
