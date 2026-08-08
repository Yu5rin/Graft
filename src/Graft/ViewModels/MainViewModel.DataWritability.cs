namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> のうち、課題1（データ保存先に書き込めない場合の状態）を扱う部分
/// （1ファイル400行の上限のための分割）。
/// </summary>
public sealed partial class MainViewModel
{
    private bool _isDataDirectoryReadOnly;

    /// <summary>
    /// 実行ファイルと同じ階層（データ保存先）へ書き込めない状態かどうか。
    /// trueの間は設定・プロジェクトの履歴・バックアップ・ログのいずれも保存できない。
    ///
    /// StartupCoordinatorが起動時の書き込み確認結果を<see cref="MarkDataDirectoryReadOnly"/>
    /// で伝える。起動時ダイアログは1回しか出ないため、これだけでは「その後ずっと黙って
    /// 失敗し続ける」ことになってしまう。ステータスバー（StatusBarView）に常時表示する
    /// ことで、保存されない状態が続いている間は警告を出し続ける（課題1対応）。
    /// </summary>
    public bool IsDataDirectoryReadOnly
    {
        get => _isDataDirectoryReadOnly;
        private set => SetProperty(ref _isDataDirectoryReadOnly, value);
    }

    /// <summary>StartupCoordinatorから、データ保存先が書き込み不可だったことを伝える。</summary>
    public void MarkDataDirectoryReadOnly() => IsDataDirectoryReadOnly = true;
}
