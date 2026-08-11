using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 機能3の追加（孤立したユーザーフォルダの復帰確認）のシナリオテスト。
/// 3条件の判定そのもの（<see cref="DataDirectoryRecovery.ShouldPromptForRecovery"/>）は
/// Graft.Tests側（DataDirectoryRecoveryTests.cs）で検証済みのため、ここでは実際に確認
/// ダイアログを出し、応答に応じてdatapath.txtを書く一連の流れ
/// （<see cref="StartupCoordinator.ResolveDataDirectoryRecoveryAsync"/>）を検証する。
/// </summary>
public class DataDirectoryRecoveryScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-data-dir-recovery-scenario", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "3条件を満たさない場合はダイアログを一切出さず、NotApplicableを返す")]
    public async Task 条件を満たさなければダイアログを出さない()
    {
        var exeDir = Path.Combine(_root, "exe1");
        var userDir = Path.Combine(_root, "user1");
        Directory.CreateDirectory(exeDir);
        // userDirはあえて作らない（条件3不成立）。

        var dialogs = new RecordingDialogService();

        var outcome = await StartupCoordinator.ResolveDataDirectoryRecoveryAsync(dialogs, exeDir, userDir);

        outcome.Result.Should().Be(DataDirectoryRecoveryResult.NotApplicable);
        dialogs.ConfirmThreeWayCallCount.Should().Be(0, "対象外なら確認ダイアログを出してはならない");
        File.Exists(DataDirectoryPointer.PointerFilePath(exeDir)).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "「はい」を選ぶと、ユーザーフォルダを指すポインタが書かれ、その保存先が使われる")]
    public async Task はいを選ぶと復帰しポインタが書かれる()
    {
        var exeDir = Path.Combine(_root, "exe2");
        var userDir = Path.Combine(_root, "user2");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(userDir);
        await File.WriteAllTextAsync(Path.Combine(userDir, "settings.json"), "{}");

        var dialogs = new RecordingDialogService { ThreeWayResponse = true };

        var outcome = await StartupCoordinator.ResolveDataDirectoryRecoveryAsync(dialogs, exeDir, userDir);

        outcome.Result.Should().Be(DataDirectoryRecoveryResult.Recovered);
        dialogs.ConfirmThreeWayCallCount.Should().Be(1);
        dialogs.LastMessage.Should().Contain(userDir, "見つかった絶対パスを文言に添える必要がある");

        DataDirectoryPointer.TryRead(exeDir).Should().Be(userDir);
        AppPaths.ResolveBaseDirectory(exeDir).Should().Be(userDir,
            "このプロセスの以後の起動処理はこの保存先を使う必要がある");
    }

    [AvaloniaFact(DisplayName = "「いいえ」を選ぶと、ポインタにexeフォルダ自身が書かれ、以後は候補にならない")]
    public async Task いいえを選ぶとポータブルを明示し以後尋ねない()
    {
        var exeDir = Path.Combine(_root, "exe3");
        var userDir = Path.Combine(_root, "user3");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(userDir);
        await File.WriteAllTextAsync(Path.Combine(userDir, "settings.json"), "{}");

        var dialogs = new RecordingDialogService { ThreeWayResponse = false };

        var outcome = await StartupCoordinator.ResolveDataDirectoryRecoveryAsync(dialogs, exeDir, userDir);

        outcome.Result.Should().Be(DataDirectoryRecoveryResult.DeclinedAndMarkedPortable);
        DataDirectoryPointer.TryRead(exeDir).Should().Be(exeDir, "「明示的なポータブル」としてexeフォルダ自身を書く");

        // 以後の起動では条件1（ポインタが無い）が不成立になり、二度と尋ねられない。
        DataDirectoryRecovery.ShouldPromptForRecovery(exeDir, userDir).Should().BeFalse();
        AppPaths.ResolveBaseDirectory(exeDir).Should().Be(exeDir);
    }

    [AvaloniaFact(DisplayName = "キャンセル（決めなかった）場合はポインタに一切触れず、次回起動でまた尋ねられる")]
    public async Task キャンセルするとポインタは変更されず次回また尋ねられる()
    {
        var exeDir = Path.Combine(_root, "exe4");
        var userDir = Path.Combine(_root, "user4");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(userDir);
        await File.WriteAllTextAsync(Path.Combine(userDir, "settings.json"), "{}");

        var dialogs = new RecordingDialogService { ThreeWayResponse = null };

        var outcome = await StartupCoordinator.ResolveDataDirectoryRecoveryAsync(dialogs, exeDir, userDir);

        outcome.Result.Should().Be(DataDirectoryRecoveryResult.Postponed);
        File.Exists(DataDirectoryPointer.PointerFilePath(exeDir)).Should().BeFalse();
        DataDirectoryRecovery.ShouldPromptForRecovery(exeDir, userDir).Should().BeTrue("次回起動でまた尋ねられる必要がある");
    }

    [AvaloniaFact(DisplayName = "基準ディレクトリを明示指定すると、復帰確認の対象になりうる状況でも一切影響を受けない" +
        "（テストからのStartupCoordinator構築は常にこの経路。ResolveDataDirectoryRecoveryAsyncは" +
        "App.axaml.cs側からコンストラクタ呼び出しより前に明示的に呼ばれるときだけ動く設計であり、" +
        "StartupCoordinatorのコンストラクタ・AppPathsの解決自体はこれを一切参照しないことを固定する）")]
    public void 基準ディレクトリ明示指定では復帰確認が一切動かない()
    {
        var exeDir = Path.Combine(_root, "exe5");
        var elsewhereUserDir = Path.Combine(_root, "user5"); // 復帰候補になりうる状況を用意する。
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(elsewhereUserDir);
        File.WriteAllText(Path.Combine(elsewhereUserDir, "settings.json"), "{}");

        // 「本来ならexeDirを基準にすれば復帰候補になる状況」であることを確認しておく。
        DataDirectoryRecovery.ShouldPromptForRecovery(exeDir, elsewhereUserDir).Should().BeTrue(
            "この前提が崩れていると、このテスト自体が何も検証していないことになる");

        // AppPaths.ResolveBaseDirectory・StartupCoordinatorのコンストラクタは、
        // ResolveDataDirectoryRecoveryAsyncを一切呼ばない（App.axaml.cs側のみが呼ぶ設計）。
        // そのため、baseDirectoryを明示すればdatapath.txtの読み取りすら行わず、
        // 復帰確認の候補になりうる状況であっても一切影響を受けない。
        var coordinator = new StartupCoordinator(baseDirectory: exeDir);

        coordinator.AppPaths.BaseDirectory.Should().Be(exeDir);
        File.Exists(DataDirectoryPointer.PointerFilePath(exeDir)).Should().BeFalse(
            "baseDirectoryを明示指定した場合、ポインタファイルは一切書き込まれてはならない");
    }

    /// <summary>確認ダイアログの応答・呼び出し回数を記録する簡易実装。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public bool? ThreeWayResponse { get; set; } = true;
        public int ConfirmThreeWayCallCount { get; private set; }
        public string? LastMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
        {
            ConfirmThreeWayCallCount++;
            LastMessage = message;
            return Task.FromResult(ThreeWayResponse);
        }

        public Task<string?> PromptAsync(string title, string message, string? initial = null) => Task.FromResult(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
