using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// v1.0.8で実機報告された不具合の回帰テスト。
///
/// 【症状（実機）】「設定画面からデータ保存先をユーザーフォルダへ移動」した利用者の環境で
/// 自動更新を実行すると、必ず次のエラーで失敗した。
/// <code>
/// 更新に失敗しました
/// ファイルの置き換えに失敗しました。Graft.exe を退避できませんでした:
/// Could not find file 'C:\Users\YUGO\AppData\Roaming\Graft\Graft.exe'.
/// </code>
///
/// 【原因】<c>SettingsViewModel.Update.cs</c>が<see cref="UpdateInstallPipeline.RunAsync"/>の
/// <c>installDirectory</c>引数に<see cref="AppPaths.BaseDirectory"/>（settings.json等の
/// "データ保存先"）を渡していたが、これは"実行ファイルの置き場所"とは別物。ポータブル運用
/// （両者が一致する）では発覚せず、データ保存先移行済みの環境でだけ、存在しないフォルダの
/// Graft.exeを退避しようとして必ず失敗していた。
///
/// このファイルは (1) 両者が実際に食い違うことを<see cref="AppPaths"/>と
/// <see cref="AppRestart"/>それぞれの解決結果で裏付け、(2) 食い違った状態で
/// <see cref="UpdateInstallPipeline"/>へ「データ保存先」を渡すと実機と一字一句同じ失敗が
/// 再現すること、(3) 代わりに<see cref="AppRestart.TryResolveExecutableDirectory"/>の
/// 解決結果（修正後にSettingsViewModel.Update.csが実際に使う値）を渡せば成功することを
/// 検証する。修正前（AppRestart.TryResolveExecutableDirectoryが存在しない時点）は
/// このファイル自体がビルドできず、修正の全テストが失敗する状態から始めている。
/// </summary>
public class UpdateInstallDirectoryRegressionTests
{
    [Fact(DisplayName = "データ保存先移行済み環境では、AppPaths.BaseDirectoryと実行ファイルのフォルダが一致しない")]
    public void データ保存先移行済みでは両者が食い違う()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDataDir = ws.CreateDirectory("userdata");
        // 「設定画面からユーザーフォルダへ移動」を実行済みの状態を、ポインタファイルで模す。
        DataDirectoryPointer.TryWrite(exeDir, userDataDir).Should().BeTrue();

        var resolvedDataDirectory = AppPaths.ResolveBaseDirectory(exeDir);
        var fakeExePath = ws.WriteText("exe/Graft.exe", "dummy");
        var resolvedInstallDirectory = AppRestart.TryResolveExecutableDirectory(fakeExePath);

        resolvedDataDirectory.Should().Be(userDataDir, "移行済みならBaseDirectoryはユーザーフォルダを指す");
        resolvedInstallDirectory.Should().Be(exeDir, "実行ファイルの場所は移行の影響を受けない");
        resolvedDataDirectory.Should().NotBe(resolvedInstallDirectory,
            "この食い違いこそが今回の不具合の直接の原因");
    }

    [Fact(DisplayName = "ポータブル運用（移行していない）なら両者は一致する")]
    public void ポータブル運用では両者が一致する()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("portable-exe");
        var fakeExePath = ws.WriteText("portable-exe/Graft.exe", "dummy");

        var resolvedDataDirectory = AppPaths.ResolveBaseDirectory(exeDir);
        var resolvedInstallDirectory = AppRestart.TryResolveExecutableDirectory(fakeExePath);

        resolvedDataDirectory.Should().Be(exeDir);
        resolvedInstallDirectory.Should().Be(exeDir);
        resolvedDataDirectory.Should().Be(resolvedInstallDirectory,
            "ポータブル運用ではデータ保存先＝実行ファイルの場所なので、従来どおり区別する必要がない");
    }

    [Fact(DisplayName = "不具合そのものの再現: installDirectoryへデータ保存先を渡すと実機と同じ失敗（Graft.exeを退避できない）になる")]
    public async Task 不具合再現_データ保存先を渡すと失敗する()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDataDir = ws.CreateDirectory("userdata"); // 実行ファイルは一切置かれていない、移行後のデータ保存先。
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.WriteAllText(Path.Combine(exeDir, fileName), $"old-{fileName}");
        }

        var zipBytes = BuildValidZip();
        var asset = new GitHubReleaseAsset
        {
            Name = "Graft-1.0.9-win-x64.zip",
            BrowserDownloadUrl = "https://example.invalid/x.zip",
            Digest = $"sha256:{ComputeSha256Hex(zipBytes)}",
        };
        var pipeline = new UpdateInstallPipeline(new FakeDownloader(zipBytes));

        // 修正前のバグ再現: installDirectoryへ「データ保存先」（実行ファイルが無いフォルダ）を渡す。
        var result = await pipeline.RunAsync(
            asset, userDataDir, ws.Combine("work-buggy"), downloadProgress: null, CancellationToken.None);

        result.Status.Should().Be(UpdateInstallStatus.InstallFailed);
        result.ErrorMessage.Should().Contain("Graft.exe");
        result.ErrorMessage.Should().Contain("を退避できませんでした");
        result.ErrorMessage.Should().Contain("Could not find file",
            "実機報告のエラーメッセージ（File.Moveの例外メッセージ）と一致すること");

        // exeDir（実際の実行ファイルの場所）には一切手を付けていないはず。
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.ReadAllText(Path.Combine(exeDir, fileName)).Should().Be($"old-{fileName}");
        }
    }

    [Fact(DisplayName = "修正後: installDirectoryへ実行ファイルのフォルダ（AppRestart.TryResolveExecutableDirectory）を渡すと成功する")]
    public async Task 修正後_実行ファイルのフォルダを渡すと成功する()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDataDir = ws.CreateDirectory("userdata");
        DataDirectoryPointer.TryWrite(exeDir, userDataDir).Should().BeTrue();
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.WriteAllText(Path.Combine(exeDir, fileName), $"old-{fileName}");
        }
        var fakeExePath = Path.Combine(exeDir, "Graft.exe"); // RequiredFileNamesの1つとして上で既に作成済み。

        var zipBytes = BuildValidZip();
        var asset = new GitHubReleaseAsset
        {
            Name = "Graft-1.0.9-win-x64.zip",
            BrowserDownloadUrl = "https://example.invalid/x.zip",
            Digest = $"sha256:{ComputeSha256Hex(zipBytes)}",
        };
        var pipeline = new UpdateInstallPipeline(new FakeDownloader(zipBytes));

        // SettingsViewModel.Update.cs修正後の実際の呼び出しと同じ解決経路。
        var installDirectory = AppRestart.TryResolveExecutableDirectory(fakeExePath);
        installDirectory.Should().Be(exeDir);

        var result = await pipeline.RunAsync(
            asset, installDirectory!, ws.Combine("work-fixed"), downloadProgress: null, CancellationToken.None);

        result.Status.Should().Be(UpdateInstallStatus.Success, result.ErrorMessage);
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.ReadAllText(Path.Combine(exeDir, fileName)).Should().Be($"new-{fileName}");
        }
        // 「データ保存先」側（settings.json相当が置かれうる場所）には自動更新は一切触れない。
        Directory.GetFileSystemEntries(userDataDir).Should().BeEmpty(
            "更新はデータ保存先を一切経由しないはず");
    }

    [Fact(DisplayName = "書き込み権限の判定は実行ファイルのフォルダに対して行われる（データ保存先が書き込めなくても実行ファイル側が書き込めれば通る）")]
    public void 書き込み権限判定は実行ファイルのフォルダを見る()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Windowsのアクセス制御はUnixのパーミッションビットと仕組みが異なり、
            // File.SetUnixFileModeでは再現できないため対象外とする
            // （AppPathsWritabilityTests.書き込めない場所ではfalseを返すと同じ方針）。
            return;
        }

        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe3");
        var userDataDir = ws.CreateDirectory("userdata3");

        if (!TryMakeUnwritable(userDataDir))
        {
            // root権限下ではパーミッションが効かず検証しようがないためスキップ扱い。
            return;
        }

        try
        {
            // 修正前の誤り: データ保存先を判定していた。
            AppPaths.CanWriteToDirectory(userDataDir).Should().BeFalse(
                "データ保存先自体は書き込めない状態にしてある");

            // 修正後: 実際にファイルを置き換える実行ファイルのフォルダを判定する。
            AppPaths.CanWriteToDirectory(exeDir).Should().BeTrue(
                "実行ファイルのフォルダは書き込めるので、自動更新をあきらめてはいけない");
        }
        finally
        {
            MakeWritable(userDataDir);
        }
    }

    private static byte[] BuildValidZip()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var fileName in UpdateFiles.RequiredFileNames)
            {
                var entry = archive.CreateEntry($"Graft/{fileName}");
                using var writer = new StreamWriter(entry.Open());
                writer.Write($"new-{fileName}");
            }
        }
        return stream.ToArray();
    }

    private static string ComputeSha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    private sealed class FakeDownloader : IUpdateDownloader
    {
        private readonly byte[] _zipBytes;

        public FakeDownloader(byte[] zipBytes) => _zipBytes = zipBytes;

        public async Task<UpdateDownloadOutcome> DownloadAsync(
            string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
        {
            await File.WriteAllBytesAsync(destinationPath, _zipBytes, ct);
            progress?.Report(1.0);
            return new UpdateDownloadOutcome(UpdateDownloadStatus.Success);
        }
    }
}
