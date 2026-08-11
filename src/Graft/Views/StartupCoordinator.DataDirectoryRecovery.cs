using Graft.Infra;
using Graft.Platform;

namespace Graft.Views;

/// <summary>
/// <see cref="StartupCoordinator"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 機能3の追加: 孤立したユーザーフォルダの復帰確認（判定の背景・3条件・実行順序の注意は
/// <see cref="DataDirectoryRecovery"/>クラスドキュメント参照）。ここではダイアログ表示と
/// ポインタファイルの書き込みという副作用だけを担い、「尋ねるべきかどうか」の判定自体は
/// <see cref="DataDirectoryRecovery.ShouldPromptForRecovery"/>（副作用の無い純粋関数）に
/// 委ねる。
///
/// 【なぜstaticメソッドとして、コンストラクタより前に呼べる形にしているか】この確認は
/// <see cref="AppPaths"/>（ひいてはStartupCoordinator自身のコンストラクタ）を組み立てるより
/// 前に完了させ、その結果（datapath.txtの内容）を確定させておく必要がある
/// （<see cref="AppPaths.ResolveBaseDirectory"/>はdatapath.txtを読んで基準ディレクトリを
/// 決めるため。<see cref="ViewModels.SettingsViewModel"/>のデータ保存先まわりのコメントに
/// あるとおり、実行中のAppPathsの差し替えはできない前提）。呼び出し元（<c>App.axaml.cs</c>の
/// <c>OnFrameworkInitializationCompleted</c>）は、<c>new StartupCoordinator()</c>より前に
/// これを呼ぶ。
///
/// テストからbaseDirectoryを明示指定する経路（<see cref="StartupCoordinator(string?)"/>の
/// baseDirectory引数）は、この静的メソッドを一切呼ばない（テストはApp.axaml.csの
/// OnFrameworkInitializationCompletedを経由せず、StartupCoordinatorのコンストラクタを
/// 直接呼ぶため）。これにより「基準ディレクトリを明示指定した場合は復帰確認が一切動かない」
/// が自動的に保証される。
/// </summary>
public sealed partial class StartupCoordinator
{
    /// <summary>
    /// 孤立したユーザーフォルダの復帰確認を行う。3条件を満たさなければ何もせず
    /// <see cref="DataDirectoryRecoveryOutcome.NotApplicable"/>を返す（ダイアログは出さない）。
    /// </summary>
    /// <param name="dialogService">確認ダイアログの表示に使う。呼び出し元がまだLoggerも
    /// AppPathsも持たないごく初期の段階のため、コンストラクタ注入されたものではなく
    /// 呼び出し側が直接渡す。</param>
    /// <param name="exeDirectory">exeと同じ階層（datapath.txtの置き場所）。</param>
    /// <param name="userDataDirectory">既定のユーザーフォルダ（<see cref="AppPaths.
    /// DefaultUserDataDirectory"/>）。呼び出し側から渡させることで、このメソッド自体は
    /// テストから任意の一時ディレクトリを差し込める（実際の%APPDATA%に一切触れずに検証できる）。</param>
    public static async Task<DataDirectoryRecoveryOutcome> ResolveDataDirectoryRecoveryAsync(
        IDialogService dialogService, string exeDirectory, string userDataDirectory)
    {
        if (!DataDirectoryRecovery.ShouldPromptForRecovery(exeDirectory, userDataDirectory))
        {
            return DataDirectoryRecoveryOutcome.NotApplicable;
        }

        // 3択（はい／いいえ／キャンセル）。「はい」を既定ボタンにする（見つかったデータを
        // 使うことを促す）。既存の作法どおり「肯定→否定→キャンセル」の順で並ぶ
        // （AvaloniaDialogService.ConfirmThreeWayAsyncのコメント参照）。
        var response = await dialogService.ConfirmThreeWayAsync(
            "データの保存先",
            BuildRecoveryPromptText(userDataDirectory),
            "はい", "いいえ").ConfigureAwait(true);

        if (response == true)
        {
            var written = DataDirectoryPointer.TryWrite(exeDirectory, userDataDirectory);
            return new DataDirectoryRecoveryOutcome(
                written ? DataDirectoryRecoveryResult.Recovered : DataDirectoryRecoveryResult.RecoveredPointerWriteFailed,
                userDataDirectory);
        }

        if (response == false)
        {
            // 「いいえ」: 毎回聞かれると鬱陶しいため、以後は尋ねない状態にする。datapath.txtへ
            // exeフォルダ自身のパスを書けば「明示的にポータブル」となり、次回以降は
            // ShouldPromptForRecoveryの条件1（ポインタが無い）が不成立になり候補から外れる。
            var written = DataDirectoryPointer.TryWrite(exeDirectory, exeDirectory);
            return new DataDirectoryRecoveryOutcome(
                written ? DataDirectoryRecoveryResult.DeclinedAndMarkedPortable : DataDirectoryRecoveryResult.DeclinedPointerWriteFailed,
                userDataDirectory);
        }

        // キャンセル（「キャンセル」ボタン、またはタイトルバーの×）: ポインタには一切触れない。
        // 起動をそのまま続けるとexeフォルダにback/・logs/等が作られ、次回以降ずっとこの確認自体が
        // 出せなくなる（DataDirectoryRecoveryResult.Cancelledのコメント参照）ため、呼び出し元
        // （App.OnFrameworkInitializationCompleted）でGraft自体を終了させる。ここでは終了処理を
        // 一切行わず「終了すべき」という判定結果（DataDirectoryRecoveryOutcome.ShouldExitProcess）を
        // 返すだけに留める（実際の終了という副作用は呼び出し元の責務。判定と副作用を分けることで
        // この関数自体は単体テストしやすいままにする）。
        return new DataDirectoryRecoveryOutcome(DataDirectoryRecoveryResult.Cancelled, userDataDirectory);
    }

    /// <summary>
    /// 復帰確認の文言。見つかった絶対パスを添える。キャンセルするとGraftそのものが終了する
    /// （DataDirectoryRecoveryResult.Cancelledのコメント参照）ことが伝わるよう、末尾に一文だけ添える
    /// （ダイアログが冗長にならないよう最小限に留め、「キャンセル」ボタン自体のラベルは
    /// 「この起動をやめる」の意味として読めるためそのまま変えない）。
    /// </summary>
    private static string BuildRecoveryPromptText(string userDataDirectory) =>
        "以前ユーザーフォルダに保存したデータが見つかりました。こちらを使いますか？" +
        Environment.NewLine + Environment.NewLine + userDataDirectory +
        Environment.NewLine + Environment.NewLine + "（キャンセルするとGraftを終了します）";

    /// <summary>
    /// <see cref="ResolveDataDirectoryRecoveryAsync"/>の結果をLoggerへ記録する。呼び出し元
    /// （<see cref="StartAsync"/>）はLogger生成後に呼ぶ（確認自体はLogger生成より前に完了して
    /// いるため、確定した結果をここでまとめて記録するだけ）。
    /// </summary>
    private void LogDataDirectoryRecoveryOutcome(DataDirectoryRecoveryOutcome outcome)
    {
        switch (outcome.Result)
        {
            case DataDirectoryRecoveryResult.NotApplicable:
                // 大半の起動はこれ（対象外）。ログを毎回増やさないため記録しない。
                return;
            case DataDirectoryRecoveryResult.Recovered:
                _logger?.Info("startup",
                    $"孤立したユーザーフォルダのデータを復帰しました: {outcome.UserDataDirectory}");
                return;
            case DataDirectoryRecoveryResult.RecoveredPointerWriteFailed:
                _logger?.Warn("startup",
                    $"ユーザーフォルダのデータ復帰を選びましたが、切り替え用ファイル（{DataDirectoryPointer.FileName}）を" +
                    $"書き込めませんでした。ポータブル（exeフォルダ）のまま起動します: {outcome.UserDataDirectory}");
                return;
            case DataDirectoryRecoveryResult.DeclinedAndMarkedPortable:
                _logger?.Info("startup",
                    $"ユーザーフォルダのデータ復帰を見送り、ポータブルとして明示しました（以後は尋ねません）: {outcome.UserDataDirectory}");
                return;
            case DataDirectoryRecoveryResult.DeclinedPointerWriteFailed:
                _logger?.Warn("startup",
                    $"ユーザーフォルダのデータ復帰を見送りましたが、ポータブルを明示する切り替え用ファイル（" +
                    $"{DataDirectoryPointer.FileName}）を書き込めませんでした。次回起動時にまた確認します: {outcome.UserDataDirectory}");
                return;
            case DataDirectoryRecoveryResult.Cancelled:
                // 実際にはここへ到達しないはずである: Cancelledの場合、呼び出し元
                // （App.OnFrameworkInitializationCompleted）はStartupCoordinatorのコンストラクタを
                // 呼ぶより前にGraftを終了させるため、Logger自体が生成されずStartAsync（延いては
                // このメソッド）へ到達しない。到達順序を変える将来の変更に備えた保険として、
                // 万一到達した場合のログだけは残しておく。
                _logger?.Warn("startup",
                    $"データ復帰の確認をキャンセルしました（本来は終了しているはずの経路です）: {outcome.UserDataDirectory}");
                return;
        }
    }
}
