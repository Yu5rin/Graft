using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="UpdateInstallPipeline"/>の一連の流れ（ダウンロード→SHA256検証→ZIP検証→展開→
/// 自己置き換え）を、HTTP通信を一切行わずに検証する。<see cref="IUpdateDownloader"/>は
/// 実際に用意したZIPバイト列をファイルへ書き出すだけのフェイクに差し替える。
/// </summary>
public class UpdateInstallPipelineTests
{
    [Fact(DisplayName = "digestが無いアセットはインストールしない（ChecksumUnavailable）")]
    public async Task digestが無ければインストールしない()
    {
        using var ws = new TempWorkspace();
        var zipBytes = BuildValidZip();
        var scenario = new Scenario(ws);
        var pipeline = new UpdateInstallPipeline(new FakeDownloader(zipBytes));
        var asset = new GitHubReleaseAsset { Name = "Graft-1.0.8-win-x64.zip", BrowserDownloadUrl = "https://example.invalid/x.zip", Digest = null };

        var result = await pipeline.RunAsync(asset, scenario.InstallDir, scenario.WorkDir, downloadProgress: null, CancellationToken.None);

        result.Status.Should().Be(UpdateInstallStatus.ChecksumUnavailable);
        scenario.AssertInstallDirUntouched();
    }

    [Fact(DisplayName = "SHA256が一致しないときはインストールしない（ChecksumMismatch）")]
    public async Task SHA256が一致しなければインストールしない()
    {
        using var ws = new TempWorkspace();
        var zipBytes = BuildValidZip();
        var scenario = new Scenario(ws);
        var pipeline = new UpdateInstallPipeline(new FakeDownloader(zipBytes));
        var wrongHash = new string('0', 64);
        var asset = new GitHubReleaseAsset
        {
            Name = "Graft-1.0.8-win-x64.zip", BrowserDownloadUrl = "https://example.invalid/x.zip", Digest = $"sha256:{wrongHash}",
        };

        var result = await pipeline.RunAsync(asset, scenario.InstallDir, scenario.WorkDir, downloadProgress: null, CancellationToken.None);

        result.Status.Should().Be(UpdateInstallStatus.ChecksumMismatch);
        scenario.AssertInstallDirUntouched();

        // 失敗時も作業フォルダ自体は掃除される（finallyで必ず実行されるため）。
        Directory.Exists(scenario.WorkDir).Should().BeFalse("失敗時も作業フォルダ自体は後始末されるべき");
    }

    [Fact(DisplayName = "SHA256が一致すれば検証を通り、実際にインストールされる")]
    public async Task SHA256が一致すればインストールされる()
    {
        using var ws = new TempWorkspace();
        var zipBytes = BuildValidZip();
        var scenario = new Scenario(ws);
        var pipeline = new UpdateInstallPipeline(new FakeDownloader(zipBytes));
        var asset = new GitHubReleaseAsset
        {
            Name = "Graft-1.0.8-win-x64.zip",
            BrowserDownloadUrl = "https://example.invalid/x.zip",
            Digest = $"sha256:{ComputeSha256Hex(zipBytes)}",
        };

        var progressReports = new List<double>();
        var progress = new Progress<double>(p => progressReports.Add(p));

        var result = await pipeline.RunAsync(asset, scenario.InstallDir, scenario.WorkDir, progress, CancellationToken.None);

        result.Status.Should().Be(UpdateInstallStatus.Success, result.ErrorMessage);
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.ReadAllText(Path.Combine(scenario.InstallDir, fileName)).Should().Be($"new-{fileName}");
        }
        scenario.AssertUserDataUntouched();

        // 不具合修正（利用者からの指摘・穴1「一時フォルダの入れ物が残る」）: 以前はZIP・展開先の
        // 中身だけを消して入れ物のworkDirectory自体は空フォルダのまま残していたが、
        // workDirectory自体を再帰削除するようにした。更新を試みるたびに空フォルダが
        // 溜まり続けることを防ぐ。
        Directory.Exists(scenario.WorkDir).Should().BeFalse("作業フォルダ自体（%TEMP%\\GraftUpdate\\<GUID>\\相当）も後始末されるべき");
    }

    [Fact(DisplayName = "ZIPの中身が想定外ならUnexpectedZipContentsで中止する")]
    public async Task ZIPの中身が想定外なら中止する()
    {
        using var ws = new TempWorkspace();
        var zipBytes = BuildZipWithUnexpectedEntry();
        var scenario = new Scenario(ws);
        var pipeline = new UpdateInstallPipeline(new FakeDownloader(zipBytes));
        var asset = new GitHubReleaseAsset
        {
            Name = "Graft-1.0.8-win-x64.zip",
            BrowserDownloadUrl = "https://example.invalid/x.zip",
            Digest = $"sha256:{ComputeSha256Hex(zipBytes)}",
        };

        var result = await pipeline.RunAsync(asset, scenario.InstallDir, scenario.WorkDir, downloadProgress: null, CancellationToken.None);

        result.Status.Should().Be(UpdateInstallStatus.UnexpectedZipContents);
        scenario.AssertInstallDirUntouched();
    }

    [Fact(DisplayName = "ダウンロードに失敗したらインストールしない（DownloadFailed）")]
    public async Task ダウンロード失敗ならインストールしない()
    {
        using var ws = new TempWorkspace();
        var scenario = new Scenario(ws);
        var pipeline = new UpdateInstallPipeline(new FakeDownloader(new UpdateDownloadOutcome(UpdateDownloadStatus.Failed, "接続できませんでした。")));
        var asset = new GitHubReleaseAsset { Name = "x.zip", BrowserDownloadUrl = "https://example.invalid/x.zip", Digest = $"sha256:{new string('a', 64)}" };

        var result = await pipeline.RunAsync(asset, scenario.InstallDir, scenario.WorkDir, downloadProgress: null, CancellationToken.None);

        result.Status.Should().Be(UpdateInstallStatus.DownloadFailed);
        scenario.AssertInstallDirUntouched();
    }

    [Fact(DisplayName = "中断した場合はCancelledを返す")]
    public async Task 中断した場合はCancelledを返す()
    {
        using var ws = new TempWorkspace();
        var scenario = new Scenario(ws);
        var pipeline = new UpdateInstallPipeline(new FakeDownloader(new UpdateDownloadOutcome(UpdateDownloadStatus.Cancelled, "中断しました。")));
        var asset = new GitHubReleaseAsset { Name = "x.zip", BrowserDownloadUrl = "https://example.invalid/x.zip", Digest = $"sha256:{new string('a', 64)}" };

        var result = await pipeline.RunAsync(asset, scenario.InstallDir, scenario.WorkDir, downloadProgress: null, CancellationToken.None);

        result.Status.Should().Be(UpdateInstallStatus.Cancelled);
        scenario.AssertInstallDirUntouched();
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

    private static byte[] BuildZipWithUnexpectedEntry()
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
            var settingsEntry = archive.CreateEntry("Graft/settings.json");
            using var settingsWriter = new StreamWriter(settingsEntry.Open());
            settingsWriter.Write("{}");
        }
        return stream.ToArray();
    }

    private static string ComputeSha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class FakeDownloader : IUpdateDownloader
    {
        private readonly byte[]? _zipBytes;
        private readonly UpdateDownloadOutcome _forcedOutcome;

        public FakeDownloader(byte[] zipBytes)
        {
            _zipBytes = zipBytes;
            _forcedOutcome = new UpdateDownloadOutcome(UpdateDownloadStatus.Success);
        }

        public FakeDownloader(UpdateDownloadOutcome forcedOutcome)
        {
            _zipBytes = null;
            _forcedOutcome = forcedOutcome;
        }

        public async Task<UpdateDownloadOutcome> DownloadAsync(
            string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
        {
            if (_forcedOutcome.Status != UpdateDownloadStatus.Success || _zipBytes is null)
            {
                return _forcedOutcome;
            }

            await File.WriteAllBytesAsync(destinationPath, _zipBytes, ct);
            progress?.Report(1.0);
            return _forcedOutcome;
        }
    }

    /// <summary>インストール先（利用者データ含む）と作業フォルダを用意する。</summary>
    private sealed class Scenario
    {
        public string InstallDir { get; }
        public string WorkDir { get; }

        private readonly Dictionary<string, string> _installDirSnapshot = new();

        public Scenario(TempWorkspace ws)
        {
            InstallDir = ws.CreateDirectory("install");
            WorkDir = ws.Combine("work");

            foreach (var fileName in UpdateFiles.RequiredFileNames)
            {
                File.WriteAllText(Path.Combine(InstallDir, fileName), $"old-{fileName}");
            }
            File.WriteAllText(Path.Combine(InstallDir, "settings.json"), "{\"theme\":\"dark\"}");
            File.WriteAllText(Path.Combine(InstallDir, "projects.json"), "[]");

            foreach (var f in Directory.GetFiles(InstallDir))
            {
                _installDirSnapshot[Path.GetFileName(f)] = File.ReadAllText(f);
            }
        }

        /// <summary>installDir内のファイル集合・内容が1件も変わっていないことを検証する（失敗時は何も書き換えないため）。</summary>
        public void AssertInstallDirUntouched()
        {
            var currentFiles = Directory.GetFiles(InstallDir).Select(Path.GetFileName).ToList();
            currentFiles.Should().BeEquivalentTo(_installDirSnapshot.Keys, "失敗時はインストール先に一切変更が無いこと");
            foreach (var (name, content) in _installDirSnapshot)
            {
                File.ReadAllText(Path.Combine(InstallDir, name)).Should().Be(content);
            }
        }

        public void AssertUserDataUntouched()
        {
            File.ReadAllText(Path.Combine(InstallDir, "settings.json")).Should().Be("{\"theme\":\"dark\"}");
            File.ReadAllText(Path.Combine(InstallDir, "projects.json")).Should().Be("[]");
        }
    }
}
