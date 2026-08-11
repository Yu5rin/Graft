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
///
/// 11件目の不具合修正: 以前は<see cref="HasClipboardPatchNotice"/>を消す経路が
/// 「クリックして解析する」「監視そのものを止める」の2つしか無く、パッチ検知の直後に
/// 通常のテキストをコピーしても通知が出たままになる不具合があった
/// （<c>IClipboardMonitor.PatchDetected</c>は「パッチ形式と判定したときだけ」発火する
/// 設計で、非パッチへの変化を知らせる経路が無かったのが真因）。
/// <see cref="ClearClipboardPatchNotice"/>を追加し、<c>IClipboardMonitor.NonPatchTextChanged</c>
/// （StartupCoordinator経由）から呼んでもらうことで解消する。
///
/// 機能追加: クリップボード監視で検知したら自動で解析する設定（既定オン）。判断自体は
/// <see cref="HandleClipboardPatchDetected"/>に集約し、StartupCoordinatorはその戻り値
/// （実際に自動解析したか）だけを見てウィンドウの前面化を判断する。
///
/// 細かいユーザビリティ改善2: ステータスバーの「クリップボード監視中」表示をクリックすると
/// 一時停止／再開できるようにした（パスワードをコピーする間だけ止めたい、という用途。設定画面
/// まで行かせないのが目的）。<see cref="IsClipboardWatchPaused"/>は表示専用の一時的な状態であり、
/// <c>Settings.ClipboardWatch.Enabled</c>そのものは一切変更しない。アプリを再起動すれば設定
/// どおりに戻る（＝一時停止したまま再起動しても、次回起動時は通常どおり監視が始まる）。
/// 実際に<c>IClipboardMonitor.Start/Stop</c>を呼ぶのはStartupCoordinator側の責務（ViewModel層を
/// Platformの具体実装へ直接依存させない方針、附録A.5）で、ここではクリックを
/// <see cref="ClipboardWatchPauseToggleRequested"/>イベントとして中継し、実際の開始・停止結果を
/// <see cref="SetClipboardWatchPaused"/>で受け取って表示へ反映するだけに徹する
/// （<see cref="SetClipboardWatchActive"/>・<see cref="NotifyClipboardPatchDetected"/>と同じ流儀）。
/// </summary>
public sealed partial class ShellViewModel
{
    private bool _isClipboardWatchActive;
    private bool _hasClipboardPatchNotice;
    private bool _isClipboardWatchPaused;

    /// <summary>クリップボード監視が現在有効かどうか。<see cref="SetClipboardWatchActive"/>で更新する。</summary>
    public bool IsClipboardWatchActive
    {
        get => _isClipboardWatchActive;
        private set => SetProperty(ref _isClipboardWatchActive, value);
    }

    /// <summary>
    /// 細かいユーザビリティ改善2: 一時停止中かどうか（表示専用。設定は変えない）。
    /// <see cref="SetClipboardWatchActive"/>（設定・トレイ経由でのオン/オフ）が呼ばれると、
    /// 新しい明示的な状態が来たとみなして必ずfalseへリセットする。
    /// </summary>
    public bool IsClipboardWatchPaused
    {
        get => _isClipboardWatchPaused;
        private set
        {
            if (SetProperty(ref _isClipboardWatchPaused, value)) OnPropertyChanged(nameof(ClipboardWatchStatusText));
        }
    }

    /// <summary>監視中インジケータの表示文言。一時停止中は文言を変えてひと目で分かるようにする。</summary>
    public string ClipboardWatchStatusText => IsClipboardWatchPaused
        ? "クリップボード監視: 一時停止中（クリックで再開）"
        : "クリップボード監視中（クリックで一時停止）";

    /// <summary>
    /// 細かいユーザビリティ改善2: ステータスバーの監視中インジケータをクリックしたときの
    /// 一時停止／再開コマンド。監視自体が無効（<see cref="IsClipboardWatchActive"/>がfalse）の
    /// 間はインジケータ自体が非表示のため実質呼ばれないが、念のため内部でもガードする。
    /// </summary>
    public ICommand ToggleClipboardWatchPauseCommand { get; }

    /// <summary>
    /// 一時停止／再開が要求されたことをStartupCoordinatorへ伝える。引数は「一時停止にしたいか」
    /// （true=一時停止・false=再開）。実際の<c>IClipboardMonitor.Stop/Start</c>呼び出しと、
    /// その結果を<see cref="SetClipboardWatchPaused"/>で折り返す処理はStartupCoordinator側で行う。
    /// </summary>
    public event EventHandler<bool>? ClipboardWatchPauseToggleRequested;

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
        // 設定・トレイ経由の明示的なオン/オフは、それ以前のクリックによる一時停止状態を上書きする。
        IsClipboardWatchPaused = false;
    }

    /// <summary>
    /// 細かいユーザビリティ改善2: StartupCoordinatorが実際にIClipboardMonitor.Stop/Startを
    /// 呼んだ結果を受けて、一時停止表示を更新する。<see cref="SetClipboardWatchActive"/>とは
    /// 独立させており、こちらは<c>Settings.ClipboardWatch.Enabled</c>にも<see
    /// cref="IsClipboardWatchActive"/>にも影響しない。
    /// </summary>
    public void SetClipboardWatchPaused(bool paused) => IsClipboardWatchPaused = paused;

    /// <summary>ステータスバーのインジケータをクリックしたときの入口。</summary>
    private void ToggleClipboardWatchPause()
    {
        if (!IsClipboardWatchActive) return; // 監視自体が無効な間は一時停止の概念が無い。
        ClipboardWatchPauseToggleRequested?.Invoke(this, !IsClipboardWatchPaused);
    }

    /// <summary>StartupCoordinatorがPatchDetectedイベントを受け取るたびに呼ぶ。</summary>
    public void NotifyClipboardPatchDetected() => HasClipboardPatchNotice = true;

    /// <summary>
    /// 11件目の不具合修正: StartupCoordinatorがIClipboardMonitor.NonPatchTextChanged
    /// （パッチ形式ではない内容へ変わった）を受け取るたびに呼ぶ。出しっぱなしになっていた
    /// パッチ検知通知を消す。
    /// </summary>
    public void ClearClipboardPatchNotice() => HasClipboardPatchNotice = false;

    /// <summary>
    /// 機能追加: StartupCoordinatorがPatchDetectedイベントを受け取るたびに呼ぶ、
    /// 「自動解析するかどうか」の判断込みの入口。<paramref name="autoParseEnabled"/>が
    /// オン、かつ<see cref="MainViewModel.HasUnprocessedResult"/>がfalse（未処理の解析結果も
    /// キューも無い）の場合のみ、その場で解析（<see cref="AnalyzeClipboardPatch"/>と同じ処理）
    /// まで行いtrueを返す。それ以外は要件どおり通知に留め（従来のNotifyClipboardPatchDetected）
    /// falseを返す。呼び出し元はtrueが返った場合のみウィンドウを前面化すればよい
    /// （解析結果を接ぎ木パネルで見せる必要があるため。反応時の挙動設定には関わらず必ず
    /// 前面化する。falseの場合の前面化可否は反応時の挙動設定に従う）。
    /// </summary>
    public bool HandleClipboardPatchDetected(bool autoParseEnabled)
    {
        if (autoParseEnabled && !Graft.HasUnprocessedResult)
        {
            AnalyzeClipboardPatch();
            return true;
        }

        NotifyClipboardPatchDetected();
        return false;
    }

    private void AnalyzeClipboardPatch()
    {
        ClearClipboardPatchNotice();
        if (Graft.PasteAndParseCommand.CanExecute(null)) Graft.PasteAndParseCommand.Execute(null);
    }
}
