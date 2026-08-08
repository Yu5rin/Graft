using System.IO;
using System.Runtime.Versioning;
using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 課題1（バグ）: 書き込み権限の無いフォルダから起動しても何の警告も出ないまま
/// 普通に起動してしまい、settings.json・projects.json・back/・logs/ のいずれも
/// 保存できないまま利用者の変更が次回起動時に静かに消えていた不具合の再発防止。
///
/// <see cref="AppPaths.CanWriteToBaseDirectory"/> が「書き込めない場所を検出して
/// 問題として報告できること」「書き込める場所では何も報告しない（=trueを返す）こと」の
/// 両方を検証する。権限操作を伴うテストは、root権限で動く環境（一部のCI等）では
/// chmod・UnixFileModeによる制限が効かず検証しようがないため、その場合は
/// 何もせず正常終了する（X11統合テストのDISPLAY無し時と同じ簡易スキップの考え方。
/// <see cref="X11ClipboardWriterIntegrationTests"/>参照）。
/// </summary>
public class AppPathsWritabilityTests
{
    [Fact(DisplayName = "書き込み権限のある通常のフォルダではtrueを返す（余計な警告を出さない）")]
    public void 書き込める場所ではtrueを返す()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));

        paths.CanWriteToBaseDirectory().Should().BeTrue();
    }

    [Fact(DisplayName = "確認用の一時ファイルは残らない")]
    public void 確認後に一時ファイルが残らない()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("app");
        var paths = new AppPaths(dir);

        paths.CanWriteToBaseDirectory().Should().BeTrue();

        Directory.GetFiles(dir).Should().BeEmpty("確認用の一時ファイルを削除し忘れてはいけない");
    }

    [Fact(DisplayName = "書き込み権限の無いフォルダではfalseを返す（Linux実機のみ、root環境ではスキップ）")]
    public void 書き込めない場所ではfalseを返す()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Windowsのアクセス制御はUnixのパーミッションビットと仕組みが異なり、
            // File.SetUnixFileModeでは再現できないため対象外とする。
            return;
        }

        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("readonly-app");

        if (!TryMakeUnwritable(dir))
        {
            // root権限で動く環境（CI等）ではパーミッションが効かず、検証しようがないため
            // スキップ扱いにする（実機検証は課題本文のXvfb手順で別途行う）。
            return;
        }

        try
        {
            var paths = new AppPaths(dir);
            paths.CanWriteToBaseDirectory().Should().BeFalse();
        }
        finally
        {
            // TempWorkspaceのDispose（削除）が行えるよう書き込み権限を戻す。
            MakeWritable(dir);
        }
    }

    [Fact(DisplayName = "書き込めないフォルダでは、そのままEnsureCoreDirectoriesExistが例外になる（対策前のクラッシュ原因の裏付け）")]
    public void 書き込めない場所でEnsureCoreDirectoriesExistは例外を投げる()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("readonly-app2");

        if (!TryMakeUnwritable(dir)) return; // root環境ではスキップ。

        try
        {
            var paths = new AppPaths(dir);

            // back/・logs/ がまだ存在しない状態で書き込み不可のフォルダに置かれると、
            // EnsureCoreDirectoriesExist()はUnauthorizedAccessExceptionを送出する
            // （実機検証で確認した、対策前の「ダイアログすら出せずクラッシュする」症状の原因）。
            // StartupCoordinatorは事前にCanWriteToBaseDirectory()で検出してから
            // このメソッドをtry-catchで包むことで、この例外が起動処理全体を落とさないようにしている。
            var act = () => paths.EnsureCoreDirectoriesExist();
            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            MakeWritable(dir);
        }
    }

    /// <summary>
    /// 555相当（自分を含め誰も書き込めない）へ変更する。実際に書き込みが阻止されるかまで
    /// 確認したうえでtrueを返す。root権限下ではchmod自体は成功してもパーミッション
    /// チェックがバイパスされ書き込めてしまうため、その場合はfalseを返しテストを
    /// スキップさせる。
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static bool TryMakeUnwritable(string dir)
    {
        File.SetUnixFileMode(dir,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var probe = Path.Combine(dir, ".perm_probe");
        try
        {
            using (File.Create(probe))
            {
            }

            // 作成できてしまった＝権限チェックがバイパスされている（root等）。
            File.Delete(probe);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    [SupportedOSPlatform("linux")]
    private static void MakeWritable(string dir)
    {
        File.SetUnixFileMode(dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
