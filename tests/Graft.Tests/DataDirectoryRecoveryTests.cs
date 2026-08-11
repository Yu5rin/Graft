using System.IO;
using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 機能3の追加（孤立したユーザーフォルダの復帰確認）の回帰テスト。
/// <see cref="DataDirectoryRecovery.ShouldPromptForRecovery"/>（副作用の無い純粋関数）の
/// 3条件判定だけを検証する。実際の確認ダイアログ表示・ポインタ書き込み
/// （<c>Views.StartupCoordinator.ResolveDataDirectoryRecoveryAsync</c>）はAvalonia依存のため
/// Graft.UiTests側（DataDirectoryRecoveryScenarioTests.cs）で検証する。
/// </summary>
public class DataDirectoryRecoveryTests
{
    [Fact(DisplayName = "3条件（ポインタ無し・exeフォルダに既知データ無し・ユーザーフォルダに既知データ有り）が" +
        "すべて成り立つときだけ復帰候補と判定される")]
    public void 全条件成立で復帰候補になる()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.CreateDirectory("user-data");
        ws.WriteText("user-data/settings.json", "{}");

        DataDirectoryRecovery.ShouldPromptForRecovery(exeDir, userDir).Should().BeTrue();
    }

    [Fact(DisplayName = "回帰: exeフォルダにdatapath.txtがあれば（条件1不成立）復帰候補にならない")]
    public void ポインタがあれば候補にならない()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.CreateDirectory("user-data");
        ws.WriteText("user-data/settings.json", "{}");
        DataDirectoryPointer.TryWrite(exeDir, exeDir); // 明示的にポータブル。

        DataDirectoryRecovery.ShouldPromptForRecovery(exeDir, userDir).Should().BeFalse();
    }

    [Fact(DisplayName = "最重要の回帰: exeフォルダに既知データが1つでもあれば（条件2不成立）復帰候補にならない" +
        "（ポータブル持ち運びの保護。他人の%APPDATA%へ乗っ取られないための安全弁）")]
    public void exeフォルダにデータが1つでもあれば候補にならない()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.CreateDirectory("user-data");
        ws.WriteText("exe/settings.json", "{}"); // exeフォルダ自身に既にデータがある（真にポータブル運用中）。
        ws.WriteText("user-data/settings.json", "{}");

        DataDirectoryRecovery.ShouldPromptForRecovery(exeDir, userDir).Should().BeFalse();
    }

    [Fact(DisplayName = "回帰: exeフォルダにlogs/だけがあっても（起動直後にEnsureCoreDirectoriesExistが作る想定）候補にならない。" +
        "一度でも普通に起動していれば復帰確認の対象にならない、という仕様どおりの挙動を固定する" +
        "（判定をEnsureCoreDirectoriesExistより後に呼ぶ実装ミスをすると、常にこれが真になり機能が丸ごと死ぬため、" +
        "その回帰を検出する目的も兼ねる）")]
    public void exeフォルダにlogsだけあっても候補にならない()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.CreateDirectory("user-data");
        ws.CreateDirectory("exe/logs"); // AppPaths.EnsureCoreDirectoriesExistが起動直後に作るのと同じ状態。
        ws.WriteText("user-data/settings.json", "{}");

        DataDirectoryRecovery.ShouldPromptForRecovery(exeDir, userDir).Should().BeFalse();
    }

    [Fact(DisplayName = "配布物本体のファイル（Graft.exeやランタイムのDLL等）はexeフォルダの既知データ判定に含まれない")]
    public void 配布物本体のファイルは既知データ扱いにならない()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.CreateDirectory("user-data");
        // 配布物に必ず含まれるであろうファイル名を置いても、判定対象（KnownFilePaths/
        // KnownDirectoryPaths）に含まれないため無視される。
        ws.WriteText("exe/Graft.exe", "dummy");
        ws.WriteText("exe/libSkiaSharp.dll", "dummy");
        ws.WriteText("exe/libHarfBuzzSharp.dll", "dummy");
        ws.WriteText("exe/av_libglesv2.dll", "dummy");
        ws.WriteText("user-data/settings.json", "{}");

        DataDirectoryMigrator.HasKnownContents(exeDir).Should().BeFalse(
            "配布物本体のファイルはGraftが作るデータではないため、既知データ判定に含めてはならない");
        DataDirectoryRecovery.ShouldPromptForRecovery(exeDir, userDir).Should().BeTrue();
    }

    [Fact(DisplayName = "ユーザーフォルダが存在しなければ（条件3不成立）復帰候補にならない")]
    public void ユーザーフォルダが存在しなければ候補にならない()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.Combine("user-data-not-created");

        DataDirectoryRecovery.ShouldPromptForRecovery(exeDir, userDir).Should().BeFalse();
    }

    [Fact(DisplayName = "ユーザーフォルダが存在しても中身が空（既知データが1つも無い）なら候補にならない")]
    public void ユーザーフォルダが空なら候補にならない()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.CreateDirectory("user-data-empty");

        DataDirectoryRecovery.ShouldPromptForRecovery(exeDir, userDir).Should().BeFalse();
    }

    [Fact(DisplayName = "AppPaths.DefaultUserDataDirectoryは%APPDATA%\\Graft相当のパスを組み立てる")]
    public void 既定のユーザーフォルダのパス組み立て()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Graft");

        AppPaths.DefaultUserDataDirectory().Should().Be(expected);
    }
}
