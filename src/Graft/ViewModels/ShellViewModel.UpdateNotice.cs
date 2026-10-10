using System.Windows.Input;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ShellViewModel"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 「確認なしで自動更新する」（<see cref="SettingsViewModel.UpdateAutoInstall"/>）の結果を、
/// ステータスバーに1行で知らせる表示を担う（機能追加 1.0.25）。通知の内容と出すタイミングは
/// <see cref="SettingsViewModel.CurrentAutoUpdateNotice"/>が決め、ここはそれを受けて表示状態へ
/// 変換するだけに徹する（<see cref="SetClipboardWatchActive"/>などクリップボード監視の表示と同じ流儀。
/// ViewModel同士を直接つながず、StartupCoordinatorがイベントで橋渡しする）。
///
/// 表示スロットは警告（<c>ShellViewModel.StatusBarWarning.cs</c>）とは別にする。更新の完了は
/// 警告ではなく「お知らせ」であり、書き込み不可のような深刻な警告と同じ集約へ混ぜると、
/// どちらの重みも伝わりにくくなるため。
/// </summary>
public sealed partial class ShellViewModel
{
    private AutoUpdateNotice? _updateNotice;

    /// <summary>自動更新の通知を表示すべきか。</summary>
    public bool HasUpdateNotice => _updateNotice is not null;

    /// <summary>ステータスバーに出す1行（通知が無ければ空文字列）。</summary>
    public string UpdateNoticeText => _updateNotice?.Text ?? string.Empty;

    /// <summary>ツールチップに出す詳しい説明（通知が無ければ空文字列）。</summary>
    public string UpdateNoticeDetail => _updateNotice?.Detail ?? string.Empty;

    /// <summary>「今すぐ再起動」を出すか。入れ替えが済んだ通知のときだけ（失敗の通知には出さない）。</summary>
    public bool CanRestartForUpdate => _updateNotice?.Kind == AutoUpdateNoticeKind.Installed;

    /// <summary>通知の「今すぐ再起動」。実際の再起動要求はStartupCoordinatorが受けて行う。</summary>
    public ICommand RestartForUpdateCommand { get; private set; } = null!;

    /// <summary>通知の「×」。</summary>
    public ICommand DismissUpdateNoticeCommand { get; private set; } = null!;

    /// <summary>「今すぐ再起動」が押された。未保存の確認を含む再起動の流れは<see cref="SettingsViewModel.RestartForInstalledUpdateAsync"/>。</summary>
    public event EventHandler? UpdateRestartRequested;

    /// <summary>「×」が押された。通知の状態を持つ<see cref="SettingsViewModel"/>側で消してもらう。</summary>
    public event EventHandler? UpdateNoticeDismissRequested;

    private void InitializeUpdateNotice()
    {
        // ボタン自体がIsVisibleで通知の有無・種別に連動して出し入れされるため、コマンド側に
        // CanExecuteは持たせない（表示されていれば押せる、で足りる）。
        RestartForUpdateCommand = new RelayCommand(() => UpdateRestartRequested?.Invoke(this, EventArgs.Empty));
        DismissUpdateNoticeCommand = new RelayCommand(() => UpdateNoticeDismissRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// 通知を表示する。nullなら消す。<see cref="SettingsViewModel.AutoUpdateNoticeChanged"/>から
    /// StartupCoordinator経由で呼ばれる。
    /// </summary>
    public void SetUpdateNotice(AutoUpdateNotice? notice)
    {
        _updateNotice = notice;
        OnPropertyChanged(nameof(HasUpdateNotice));
        OnPropertyChanged(nameof(UpdateNoticeText));
        OnPropertyChanged(nameof(UpdateNoticeDetail));
        OnPropertyChanged(nameof(CanRestartForUpdate));
    }
}
