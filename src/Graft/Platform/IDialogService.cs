namespace Graft.Platform;

/// <summary>
/// 確認・入力・フォルダ選択・通知の共通ダイアログを抽象化したもの。
///
/// ViewModel はほぼ全クラスがこれをコンストラクタ注入されており、WPF専用の
/// <c>Graft.Views.DialogService</c> がViewModel層に残っていたことが、v2.0のWPF版とAvalonia版で
/// ViewModelをソース共有する最後の障害だった。<see cref="IUiServices"/> と同じ考え方で
/// ここへ抽象化する（仕様書v2.1 19章・20章 L3）。
///
/// 実装は WPF 版の <c>Graft.Views.DialogService</c>、Avalonia 版の
/// <c>Graft.Platform.AvaloniaDialogService</c>、テスト・未対応環境向けの
/// <see cref="Null.NullDialogService"/> の3つ。
/// </summary>
public interface IDialogService
{
    /// <summary>OK/キャンセルの確認ダイアログを表示する。</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>
    /// 3択の確認ダイアログを表示する。未保存ファイルを閉じるとき（v2.0 仕様書4.3）のように
    /// 「実行する／実行せず続ける／やめる」を選ばせる用途に使う。
    /// </summary>
    /// <returns>肯定ならtrue、否定ならfalse、キャンセルならnull。</returns>
    Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel);

    /// <summary>1行のテキスト入力ダイアログを表示する。キャンセル時はnullを返す。</summary>
    Task<string?> PromptAsync(string title, string message, string? initial = null);

    /// <summary>
    /// フォルダ選択ダイアログを表示する。キャンセル時はnullを返す。
    /// 非同期シグネチャなのは、Avalonia版が使う<c>IStorageProvider.OpenFolderPickerAsync</c>が
    /// 非同期APIしか持たず、UIスレッドを塞がずに待ち合わせる必要があるため
    /// （v2.0のWPF版は<see cref="Task.FromResult{TResult}(TResult)"/>で包むだけでよい）。
    /// </summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>OKボタンのみの通知ダイアログを表示する。</summary>
    Task ShowMessageAsync(string title, string message);
}
