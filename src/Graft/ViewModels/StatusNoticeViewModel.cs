using System.Windows.Input;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// ステータスバーに数秒だけ出す汎用の通知（文言＋押せる操作が最大1つ）。
///
/// 成功を知らせるだけの内容（コピーしました・キューへ追加しました等）を、OKを押すまで閉じない
/// モーダルで出すと、作業のたびに1クリック余計にかかる。適用直後の「元に戻す」通知
/// （MainViewModel.ApplyUndoNotice.cs）と同じ作法（<see cref="IUiTimer"/>で数秒後に自動で消す）を、
/// 文言だけ差し替えて使えるように共通化したもの。失敗や、利用者の判断が要る内容は対象外で、
/// これまでどおりダイアログで出す。
///
/// 表示は「文言（通常の文字色）」と「操作（リンクの見た目）」を分けて持つ。以前の通知は
/// Button 全体をリンク色にしていたため、押せない説明文まで押せそうに見えた
/// （StatusBarView.axaml 参照）。
/// </summary>
public sealed class StatusNoticeViewModel : ObservableObject
{
    // 適用直後の通知（MainViewModel.ApplyUndoNoticeDuration）・削除取り消し通知と同じ長さ。
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(6);

    private readonly IUiTimer _timer;
    private bool _isVisible;
    private string _text = string.Empty;
    private string _actionLabel = string.Empty;
    private Func<Task>? _action;

    public StatusNoticeViewModel(IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        // CreateTimerは反復タイマーのため、1回消したらStopして次の通知まで眠らせる。
        _timer = ui.CreateTimer(NoticeDuration, Dismiss);
        ActionCommand = new AsyncRelayCommand(RunActionAsync, () => _action is not null, context: "通知の操作");
    }

    /// <summary>通知を出しているかどうか。</summary>
    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }

    /// <summary>通知の文言（押せない部分）。</summary>
    public string Text { get => _text; private set => SetProperty(ref _text, value); }

    /// <summary>リンクとして見せる操作の文言（例:「元に戻す」）。操作が無い通知では空。</summary>
    public string ActionLabel
    {
        get => _actionLabel;
        private set => SetProperty(ref _actionLabel, value, () => OnPropertyChanged(nameof(HasAction)));
    }

    /// <summary>押せる操作があるかどうか。</summary>
    public bool HasAction => _actionLabel.Length > 0;

    /// <summary>操作の実行。実行すると通知は消える。</summary>
    public ICommand ActionCommand { get; }

    /// <summary>
    /// 通知を出す（すでに出ていれば置き換えて数え直す）。<paramref name="action"/> を渡したときだけ
    /// <paramref name="actionLabel"/> がリンクとして出る。
    /// </summary>
    public void Show(string text, string? actionLabel = null, Func<Task>? action = null)
    {
        _action = action;
        Text = text;
        ActionLabel = action is null ? string.Empty : actionLabel ?? string.Empty;
        IsVisible = true;
        _timer.Restart();
    }

    /// <summary>通知を消す。</summary>
    public void Dismiss()
    {
        _timer.Stop();
        _action = null;
        IsVisible = false;
    }

    private async Task RunActionAsync()
    {
        var action = _action;
        // 実行の前に消す。操作の中身（解析結果の復元など）が次の通知を出す場合に、
        // 後から消してしまわないため。
        Dismiss();
        if (action is not null) await action().ConfigureAwait(true);
    }
}
