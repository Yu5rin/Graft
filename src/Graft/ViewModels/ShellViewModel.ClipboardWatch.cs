using System.Windows.Input;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ShellViewModel"/> の分割ファイル（1ファイル400行上限のため）。
///
/// クリップボード監視（仕様書9章・10章）のステータスバー表示を担う。監視の開始・停止自体は
/// <c>StartupCoordinator</c> が <c>IPlatformServices.Clipboard</c> 越しに行う
/// （ViewModel層をPlatformの具体実装へ直接依存させない方針、附録A.5）。ここでは
/// その結果を<see cref="SetClipboardWatchActive"/>・<see cref="NotifyClipboardPatchDetected"/>
/// で受け取り、表示状態へ変換するだけに徹する。
///
/// 表示スロットを2つに分け、<see cref="ShellViewModel.StatusBarWarning.cs"/>の警告スロット
/// （複数の警告を1件へ集約する仕組み）とは独立させる。「監視が有効である」ことは警告ではなく
/// 平常状態の表示であり、同じ集約ロジックに混ぜると警告と紛れて見えてしまうため。
///   - <see cref="IsClipboardWatchActive"/>: 監視が有効な間ずっと出しっぱなしの控えめな状態表示。
///   - <see cref="HasClipboardPatchNotice"/>: パッチ形式のテキストを検知した際の一時的な通知。
///     クリックで<see cref="MainViewModel.PasteAndParseCommand"/>を実行して消える。
///     トレイ通知・ウィンドウ表示（既定/非アクティブ/アクティブ表示、
///     StartupCoordinator.OnClipboardPatchDetected）と役割が重なるが、タスクトレイが
///     使えない環境（D-Bus非対応・Wayland等）でも必ず気付けるようにするための保険であり、
///     クリックしない限りクリップボードを読み直したり適用したりはしない
///     （要件: 確認なしに適用しない）。
/// </summary>
public sealed partial class ShellViewModel
{
    private bool _isClipboardWatchActive;
    private bool _hasClipboardPatchNotice;

    /// <summary>クリップボード監視が現在有効かどうか。<see cref="SetClipboardWatchActive"/>で更新する。</summary>
    public bool IsClipboardWatchActive
    {
        get => _isClipboardWatchActive;
        private set => SetProperty(ref _isClipboardWatchActive, value);
    }

    /// <summary>監視中インジケータの表示文言。</summary>
    public string ClipboardWatchStatusText => "クリップボード監視中";

    /// <summary>パッチ検知の通知を表示すべきかどうか。</summary>
    public bool HasClipboardPatchNotice
    {
        get => _hasClipboardPatchNotice;
        private set => SetProperty(ref _hasClipboardPatchNotice, value);
    }

    /// <summary>パッチ検知の通知文言。</summary>
    public string ClipboardPatchNoticeText => "パッチ形式のテキストを検知しました。クリックで解析";

    /// <summary>ステータスバーの通知をクリックしたときの「解析」（コマンドバーの「解析」と同じ処理）。</summary>
    public ICommand AnalyzeClipboardPatchCommand { get; }

    /// <summary>
    /// クリップボード監視の開始・停止の都度、StartupCoordinatorが実際の有効状態
    /// （<c>IPlatformServices.Clipboard.IsEnabled</c>）を渡して呼ぶ。監視を止めた場合は
    /// 古いパッチ検知通知も意味を失うため合わせて消す。
    /// </summary>
    public void SetClipboardWatchActive(bool isActive)
    {
        IsClipboardWatchActive = isActive;
        if (!isActive) HasClipboardPatchNotice = false;
    }

    /// <summary>StartupCoordinatorがPatchDetectedイベントを受け取るたびに呼ぶ。</summary>
    public void NotifyClipboardPatchDetected() => HasClipboardPatchNotice = true;

    private void AnalyzeClipboardPatch()
    {
        HasClipboardPatchNotice = false;
        if (Graft.PasteAndParseCommand.CanExecute(null)) Graft.PasteAndParseCommand.Execute(null);
    }
}
