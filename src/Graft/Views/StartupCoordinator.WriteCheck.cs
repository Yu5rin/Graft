using System.IO;
using Graft.Infra;
using Graft.Platform;

namespace Graft.Views;

/// <summary>
/// <see cref="StartupCoordinator"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 課題1（バグ）: 書き込み権限の無いフォルダ（Windowsの Program Files 配下等）から
/// 起動しても、以前は何の警告も出ないまま普通に起動していた。実機検証では次の2通りの
/// 症状を確認している。
///   (1) back/・logs/ がまだ存在しない場合: <see cref="AppPaths.EnsureCoreDirectoriesExist"/> が
///       UnauthorizedAccessExceptionを投げ、ダイアログすら出せずに丸ごとクラッシュする。
///   (2) back/・logs/ が既に存在する場合: クラッシュはしないが、<see cref="Logger"/> は
///       「ログ書き込みの失敗でアプリを落とさない」設計上、書き込み失敗を内部で握りつぶす。
///       結果、ログにすら1行も残らないまま設定・履歴・バックアップの保存だけが
///       静かに失われ続ける。
/// どちらの場合も「ログ経由の通知に頼らない」ことが前提（まさにログに書けない状況の
/// ため）なので、ここではLoggerを生成する前に、ダイアログのみで直接利用者へ伝える。
///
/// 起動を止めるか続行するかは実装判断に委ねられている。ここでは続行する側を選んだ。
///   - back/へ書けない場合でも、パッチ適用（ApplyEngine→BackupManager）自体は
///     IOException/UnauthorizedAccessExceptionをGraftResultとして返す作りになっており
///     （<see cref="Core.BackupManager.BeginAsync"/>参照）、実際に失敗した操作ごとに
///     通常のエラー表示で利用者へ伝わる。つまり「これから起きる個別の失敗」は
///     既存の経路で拾える。
///   - 一方、ここで起動そのものを止めてしまうと、閲覧・検索・比較など保存を伴わない
///     操作すら一切できなくなり、書き込めない場所を移動する作業をGraft自身で
///     確認しながら行う、といった実用上の逃げ道も塞いでしまう。
/// ただし「起動時に1回警告して終わり」では、以後ずっと黙って保存に失敗し続けることに
/// 変わりないため、ステータスバーへ常時警告を出し続ける
/// （<see cref="ViewModels.MainViewModel.MarkDataDirectoryReadOnly"/> /
/// <see cref="ViewModels.MainViewModel.IsDataDirectoryReadOnly"/>参照）ことで継続的に伝える。
/// </summary>
public sealed partial class StartupCoordinator
{
    /// <summary>
    /// データ保存先への書き込み確認・ディレクトリ作成・Logger生成をまとめて行う。
    /// <see cref="_isDataDirectoryWritable"/> を確定させ、生成したLoggerを返す
    /// （呼び出し元のStartAsync内で<c>_logger</c>へ代入させることで、null許容フロー解析上も
    /// 「この行より後は非null」と分かるようにするため、あえてvoidではなく戻り値にしている）。
    /// </summary>
    private async Task<Logger> InitializeDataDirectoryAsync(IDialogService dialogService)
    {
        _isDataDirectoryWritable = _appPaths.CanWriteToBaseDirectory();
        if (!_isDataDirectoryWritable)
        {
            await dialogService.ShowMessageAsync("Graft", BuildWriteProtectedWarningText()).ConfigureAwait(true);
        }

        try
        {
            _appPaths.EnsureCoreDirectoriesExist();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 上のCanWriteToBaseDirectory()で検出済みのはずだが、チェックと実際の作成の間に
            // 権限が変わる（起動中にメディアが取り外された等）レースも考えられるため二重に
            // 防御する。まだ警告していなければここで初めて警告する。
            if (_isDataDirectoryWritable)
            {
                _isDataDirectoryWritable = false;
                await dialogService.ShowMessageAsync("Graft", BuildWriteProtectedWarningText()).ConfigureAwait(true);
            }
        }

        var logger = new Logger(_appPaths);
        logger.Info("startup", _platform.DescribeEnvironment());
        if (!_isDataDirectoryWritable)
        {
            // 実際に書けるとは限らないが（Logger内部で握りつぶされる）、書ければ原因調査の
            // 手がかりとして残る。利用者への通知は既にダイアログで済んでいる。
            logger.Warn("startup", $"{_appPaths.BaseDirectory} へ書き込めません。設定・履歴・バックアップ・ログは保存されません。");
        }

        return logger;
    }

    /// <summary>
    /// 書き込み不可を伝える警告文。「なぜ問題なのか」「どうすればよいか」の両方を
    /// 明記する（対処方法が分からないまま不安にさせないため）。
    /// </summary>
    private string BuildWriteProtectedWarningText() =>
        "このフォルダへ書き込めません。" + Environment.NewLine +
        _appPaths.BaseDirectory + Environment.NewLine + Environment.NewLine +
        "Graftは実行ファイルと同じフォルダに設定・プロジェクトの履歴・バックアップ・ログを保存します。" +
        "このままでは設定を変更しても保存されず、パッチ適用前のバックアップも作成できません。" +
        "変更内容は次回起動時にすべて失われます。" + Environment.NewLine + Environment.NewLine +
        "対処: 書き込み権限のあるフォルダ（例: ドキュメントフォルダやデスクトップなど）へ、" +
        "Graftのフォルダ一式を移動してから起動し直してください。" +
        "（Windowsで「Program Files」配下に置いている場合、この状態になっている可能性があります。）" +
        Environment.NewLine + Environment.NewLine +
        "このまま起動は続行しますが、保存できない状態が続いていることはステータスバーに表示され続けます。";
}
