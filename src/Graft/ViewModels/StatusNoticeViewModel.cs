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
///
/// 【「残る」種類（<see cref="ShowPersistent"/>）を足した理由】 リボンの反映の確認で「中止」したあと、6 秒で消える通知だけだと、
/// 気付かないうちに「リボン未反映のまま」閉じてしまう（利用者の報告）。数秒で消さず、操作（［今すぐ反映］）と
/// 「あとで」（消すだけ）を添えて、押されるまで残す種類を同じ部品に足した。ほかの通知（<see cref="Show"/>）が来たら
/// 置き換わるが、その場合も未反映の間はステータスバーの常時表示（ShellViewModel.VbaUnpushed.cs）が残る。
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
    private string? _persistentTag;
    private string _dismissLabel = string.Empty;

    public StatusNoticeViewModel(IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        // CreateTimerは反復タイマーのため、1回消したらStopして次の通知まで眠らせる。
        _timer = ui.CreateTimer(NoticeDuration, Dismiss);
        ActionCommand = new AsyncRelayCommand(RunActionAsync, () => _action is not null, context: "通知の操作");
        DismissCommand = new RelayCommand(Dismiss);
    }

    /// <summary>「あとで」など、操作せずに消すためのボタンの文言（<see cref="ShowPersistent"/> のときだけ。無ければ空）。</summary>
    public string DismissLabel
    {
        get => _dismissLabel;
        private set => SetProperty(ref _dismissLabel, value, () => OnPropertyChanged(nameof(HasDismiss)));
    }

    /// <summary>「あとで」のボタンを出すか。</summary>
    public bool HasDismiss => _dismissLabel.Length > 0;

    /// <summary>操作せずに消す（<see cref="Dismiss"/>）。</summary>
    public ICommand DismissCommand { get; }

    /// <summary>数秒で消えずに残る通知か。</summary>
    public bool IsPersistent => _persistentTag is not null;

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
        _persistentTag = null;
        DismissLabel = string.Empty;
        _action = action;
        Text = text;
        ActionLabel = action is null ? string.Empty : actionLabel ?? string.Empty;
        IsVisible = true;
        _timer.Restart();
    }

    /// <summary>
    /// 数秒で消えずに残る通知を出す（<see cref="Dismiss"/>・操作・別の通知への置き換えのどれかまで残る）。
    /// <paramref name="tag"/> は「この通知は何のためか」の識別子で、原因が解消したとき <see cref="DismissIfTag"/> で消すために使う。
    /// </summary>
    public void ShowPersistent(string tag, string text, string actionLabel, Func<Task> action, string dismissLabel)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentNullException.ThrowIfNull(action);
        _timer.Stop();
        _persistentTag = tag;
        _action = action;
        Text = text;
        ActionLabel = actionLabel;
        DismissLabel = dismissLabel;
        IsVisible = true;
        OnPropertyChanged(nameof(IsPersistent));
    }

    /// <summary>残る通知が、指定の識別子のものなら消す（原因が解消したとき）。</summary>
    public void DismissIfTag(string tag)
    {
        if (_persistentTag == tag) Dismiss();
    }

    /// <summary>通知を消す。</summary>
    public void Dismiss()
    {
        _timer.Stop();
        _action = null;
        _persistentTag = null;
        DismissLabel = string.Empty;
        IsVisible = false;
        OnPropertyChanged(nameof(IsPersistent));
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
