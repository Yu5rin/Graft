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

    /// <summary>
    /// ファイル選択ダイアログを表示する。キャンセル時はnullを返す。
    /// <paramref name="extensions"/>（先頭ドット付き。例: ".txt"）を指定すると、その拡張子を
    /// 既定のフィルタとして提示する（「すべてのファイル」も併記する）。未指定または空なら
    /// フィルタ無しですべてのファイルを表示する。<see cref="PickFolderAsync"/>と同じ理由で
    /// 非同期シグネチャにしている。
    /// </summary>
    Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null);

    /// <summary>
    /// エクスプローラへの取り込み（「ファイルを追加」）用に、複数ファイルを選べるファイル選択
    /// ダイアログを表示する。キャンセル時、または1件も選ばれなかった場合はnullを返す。
    /// <see cref="PickFileAsync"/>と同じ意味の<paramref name="extensions"/>を受け取る。
    /// <para>
    /// 既定実装は<see cref="PickFileAsync"/>を1回呼ぶだけのフォールバック（単一選択）で、
    /// Avalonia標準の<see cref="Avalonia.Platform.Storage.IStorageProvider"/>を使わない
    /// テスト用フェイク・<see cref="Null.NullDialogService"/>はこのままで問題ない
    /// （<see cref="ShowActionMessageAsync"/>と同じ設計方針）。複数選択に対応する実装
    /// （<see cref="AvaloniaDialogService"/>）だけが明示的にオーバーライドする。
    /// </para>
    /// </summary>
    async Task<IReadOnlyList<string>?> PickFilesAsync(string title, IReadOnlyList<string>? extensions = null)
    {
        var single = await PickFileAsync(title, extensions).ConfigureAwait(true);
        return single is null ? null : new[] { single };
    }

    /// <summary>
    /// 「名前を付けて保存」ダイアログを表示する。キャンセル時はnullを返す。
    /// <paramref name="suggestedFileName"/>は既定のファイル名（拡張子込み）。
    /// <paramref name="extensions"/>は<see cref="PickFileAsync"/>と同じ意味（先頭ドット付き
    /// 拡張子でフィルタを提示。未指定または空ならフィルタ無し）。<see cref="PickFolderAsync"/>と
    /// 同じ理由で非同期シグネチャにしている。コンテキスト収集（10章）の「ファイルへ保存」で使う。
    /// </summary>
    Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null);

    /// <summary>OKボタンのみの通知ダイアログを表示する。</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>
    /// 不具合3: 単一のボタンだけを持つ通知ダイアログを表示する。<see cref="ShowMessageAsync"/>との
    /// 違いはボタンのラベルを差し替えられる点で、通知そのものがアクションのトリガーを兼ねる場面
    /// （例: データ保存先の移行完了後の「再起動」）で使う。
    /// </summary>
    /// <returns>
    /// 表示したボタンが押されたら true。タイトルバーの×等、ボタン以外の方法で閉じられた場合は
    /// false（ボタンに紐づくアクションを実行してはならないことを示す）。
    /// </returns>
    /// <remarks>
    /// 既定実装は後方互換のためのフォールバックで、<see cref="ShowMessageAsync"/>と同じ見た目
    /// （ボタンラベルは差し替わらない）で表示し、常にtrueを返す（<see cref="Null.NullDialogService"/>を
    /// 除くテスト用フェイク実装の大半はこの既定実装のままで問題ない想定）。ボタンラベルを
    /// 実際に差し替える必要がある実装（<see cref="AvaloniaDialogService"/>）は明示的にオーバーライドする。
    /// headless・未対応環境向けの<see cref="Null.NullDialogService"/>は、利用者不在のまま
    /// アクション（プロセス再起動等）を実行してしまわないよう、安全側（false）で明示的に
    /// オーバーライドする。
    /// </remarks>
    async Task<bool> ShowActionMessageAsync(string title, string message, string actionLabel)
    {
        await ShowMessageAsync(title, message).ConfigureAwait(true);
        return true;
    }
}
