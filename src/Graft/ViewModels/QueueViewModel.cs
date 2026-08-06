using System.Collections.ObjectModel;
using System.Windows.Input;
using Graft.Features;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// パッチキューの1ブロック行（仕様書4.10）。<see cref="QueueWindow"/> の一覧表示に使う。
/// </summary>
public sealed class QueuedBlockRowViewModel
{
    public QueuedBlockRowViewModel(QueuedBlock block)
    {
        Block = block ?? throw new ArgumentNullException(nameof(block));
    }

    /// <summary>元のキュー項目。</summary>
    public QueuedBlock Block { get; }

    public string PathText => Block.Block.Path;

    public string DescriptionText
        => string.IsNullOrWhiteSpace(Block.Block.Description) ? "（説明なし）" : Block.Block.Description!;

    public string AddedAtText => Block.AddedAt.LocalDateTime.ToString("HH:mm:ss");

    /// <summary>色のみに依存しないための読み上げ用テキスト（8.14）。</summary>
    public string AutomationName => $"{PathText}、{DescriptionText}、{AddedAtText}追加";
}

/// <summary>
/// パッチキュー管理ウィンドウ（<see cref="QueueWindow"/>）のViewModel。仕様書4.10。
/// キュー内のブロックを一覧表示し、個別削除・全削除・結合適用（<see cref="MergeRequested"/>）を提供する。
/// 実際の結合・ドライラン・適用は <see cref="MainViewModel"/> 側（既存の適用フローを再利用）が行う。
/// </summary>
public sealed class QueueViewModel : ObservableObject
{
    private readonly PatchQueue _queue;
    private readonly IDialogService _dialogs;

    public QueueViewModel(PatchQueue queue, IDialogService dialogs)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        RemoveCommand = new RelayCommand<QueuedBlockRowViewModel>(RemoveItem);
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => Items.Count > 0);
        MergeCommand = new RelayCommand(() => MergeRequested?.Invoke(this, EventArgs.Empty), () => Items.Count > 0);

        Refresh();
    }

    /// <summary>キュー内のブロック一覧。追加順。</summary>
    public ObservableCollection<QueuedBlockRowViewModel> Items { get; } = new();

    /// <summary>キューが空かどうか（空状態表示に使う、仕様書8.8）。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>選択中の行を削除する。</summary>
    public ICommand RemoveCommand { get; }

    /// <summary>キュー全体を空にする。</summary>
    public ICommand ClearCommand { get; }

    /// <summary>キューを1つのパッチへ結合し、通常の解析・適用フローへ乗せることを要求する。</summary>
    public ICommand MergeCommand { get; }

    /// <summary>「結合して適用」が要求されたことの通知。MainViewModelが購読し実際の処理を行う。</summary>
    public event EventHandler? MergeRequested;

    /// <summary>PatchQueueの現在の内容を読み直して一覧へ反映する。</summary>
    public void Refresh()
    {
        Items.Clear();
        foreach (var block in _queue.Items)
        {
            Items.Add(new QueuedBlockRowViewModel(block));
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RemoveItem(QueuedBlockRowViewModel? row)
    {
        if (row is null) return;
        _queue.Remove(row.Block.Id);
        Refresh();
    }

    private async Task ClearAsync()
    {
        var confirmed = await _dialogs
            .ConfirmAsync("キューを空にする", "パッチキューの内容をすべて削除します。よろしいですか？")
            .ConfigureAwait(true);
        if (!confirmed) return;

        _queue.Clear();
        Refresh();
    }
}
