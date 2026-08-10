using System.IO;
using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 機能3（データ保存先の選択）の回帰テスト。
/// 「ポインタファイル（datapath.txt）が無ければ従来どおりexeと同じ階層、あればその場所を使う」
/// という<see cref="AppPaths.ResolveBaseDirectory"/>の解決ロジックと、
/// <see cref="DataDirectoryPointer"/>の読み書きを検証する。
///
/// <see cref="AppPaths"/>のコンストラクタへ明示的にbaseDirectoryを渡す既存の全テストは、
/// この解決ロジックを一切経由しない（コンストラクタのコメント参照）ため、この回帰テストの
/// 追加が既存テストの挙動へ影響することは無い。
/// </summary>
public class DataDirectoryPointerTests
{
    [Fact(DisplayName = "ポインタファイルが無ければexeと同じ階層をそのまま使う（ポータブル）")]
    public void ポインタファイルが無ければexe階層を使う()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");

        AppPaths.ResolveBaseDirectory(exeDir).Should().Be(exeDir);
    }

    [Fact(DisplayName = "ポインタファイルがあればその中身のパスを使う")]
    public void ポインタファイルがあればその場所を使う()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.CreateDirectory("user-data");
        DataDirectoryPointer.TryWrite(exeDir, userDir).Should().BeTrue();

        AppPaths.ResolveBaseDirectory(exeDir).Should().Be(userDir);
    }

    [Fact(DisplayName = "ポインタファイルが空行のみならexeと同じ階層にフォールバックする")]
    public void ポインタファイルが空ならexe階層にフォールバックする()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        File.WriteAllText(Path.Combine(exeDir, DataDirectoryPointer.FileName), "   " + Environment.NewLine);

        AppPaths.ResolveBaseDirectory(exeDir).Should().Be(exeDir);
    }

    [Fact(DisplayName = "TryClearでポインタファイルを消すと、以後は再びexeと同じ階層に解決される")]
    public void TryClearでポータブルへ戻る()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.CreateDirectory("user-data");
        DataDirectoryPointer.TryWrite(exeDir, userDir);
        AppPaths.ResolveBaseDirectory(exeDir).Should().Be(userDir);

        DataDirectoryPointer.TryClear(exeDir).Should().BeTrue();

        AppPaths.ResolveBaseDirectory(exeDir).Should().Be(exeDir);
        File.Exists(DataDirectoryPointer.PointerFilePath(exeDir)).Should().BeFalse();
    }

    [Fact(DisplayName = "AppPathsコンストラクタへ明示的にbaseDirectoryを渡すと、ポインタファイルの内容を無視する")]
    public void 明示的なbaseDirectoryはポインタファイルより優先される()
    {
        using var ws = new TempWorkspace();
        var explicitDir = ws.CreateDirectory("explicit");
        // explicitDir自身にポインタファイルを置いても、コンストラクタへ明示的に渡した
        // baseDirectoryはそのまま使われる（テストからの差し替えに影響を与えないための設計）。
        DataDirectoryPointer.TryWrite(explicitDir, ws.CreateDirectory("elsewhere"));

        var paths = new AppPaths(explicitDir);

        paths.BaseDirectory.Should().Be(explicitDir);
    }

    [Fact(DisplayName = "TryClearは元々ポインタファイルが無くても成功扱いにする")]
    public void ポインタファイルが元々無くてもTryClearは成功する()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");

        DataDirectoryPointer.TryClear(exeDir).Should().BeTrue();
    }
}
