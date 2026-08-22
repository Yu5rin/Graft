using System.IO;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="SelfUpdateInstaller"/>: 指示書の最重要事項2点を固定する。
/// (1) settings.json・projects.json・back/・logs/・datapath.txt 等の利用者データには
///     成功・失敗いずれの経路でも一切触れない。
/// (2) 途中のどの段階で失敗しても、それまでに変更した分だけを正確にロールバックし、
///     最終的にすべてのファイルが更新前の状態へ戻る。
/// </summary>
public class SelfUpdateInstallerTests
{
    // 6ファイル × (退避リネーム1回 + 新規配置コピー1回) = 12回の操作がある。
    private const int TotalOperations = 12;

    [Fact(DisplayName = "成功時: 6ファイルすべてが新しい内容に置き換わり、旧内容は.oldとして残る")]
    public void 成功時に6ファイルすべて置き換わる()
    {
        using var ws = new TempWorkspace();
        var scenario = new Scenario(ws);

        var outcome = SelfUpdateInstaller.Install(scenario.InstallDir, scenario.StagingDir, new RecordingUpdateFileSystem());

        outcome.Success.Should().BeTrue(outcome.ErrorMessage);
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.ReadAllText(Path.Combine(scenario.InstallDir, fileName)).Should().Be(Scenario.NewContent(fileName));
            File.ReadAllText(Path.Combine(scenario.InstallDir, fileName + UpdateFiles.OldFileSuffix))
                .Should().Be(Scenario.OldContent(fileName));
        }

        scenario.AssertUserDataUntouched();
    }

    [Fact(DisplayName = "成功時でも利用者データファイルは1バイトも変更されない")]
    public void 成功時も利用者データは変更されない()
    {
        using var ws = new TempWorkspace();
        var scenario = new Scenario(ws);

        var outcome = SelfUpdateInstaller.Install(scenario.InstallDir, scenario.StagingDir, new RecordingUpdateFileSystem());

        outcome.Success.Should().BeTrue();
        scenario.AssertUserDataUntouched();
    }

    [Theory(DisplayName = "12回の操作のうち、どの回で失敗してもロールバックされ、6ファイルすべてが元の内容へ戻る")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void どの段階で失敗しても全ファイルが元に戻る(int failOnCallNumber)
    {
        using var ws = new TempWorkspace();
        var scenario = new Scenario(ws);
        var fileSystem = new RecordingUpdateFileSystem { FailOnCallNumber = failOnCallNumber };

        var outcome = SelfUpdateInstaller.Install(scenario.InstallDir, scenario.StagingDir, fileSystem);

        outcome.Success.Should().BeFalse($"{failOnCallNumber}回目の操作で失敗させたため");

        // 【最も重要】ロールバック後、6ファイルすべてが更新前の内容そのままであること。
        // .oldの残骸も一切残っていないこと（次回の更新の妨げにならないように）。
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            var targetPath = Path.Combine(scenario.InstallDir, fileName);
            File.Exists(targetPath).Should().BeTrue($"{fileName} は必ず元の場所に存在し続けること");
            File.ReadAllText(targetPath).Should().Be(Scenario.OldContent(fileName), $"{fileName} の内容は更新前のまま");
            File.Exists(targetPath + UpdateFiles.OldFileSuffix).Should().BeFalse($"{fileName} の.old残骸が残ってはいけない");
        }

        scenario.AssertUserDataUntouched();
    }

    [Fact(DisplayName = "展開先に必要なファイルが無い場合も、途中まで進んだ分だけ正確にロールバックされる")]
    public void 展開先にファイルが無い場合もロールバックされる()
    {
        using var ws = new TempWorkspace();
        var scenario = new Scenario(ws);
        // 3番目のファイル（配列順）の展開結果を消し、「準備不足」を模す。
        var missing = UpdateFiles.RequiredFileNames[2];
        File.Delete(Path.Combine(scenario.StagingDir, missing));

        var outcome = SelfUpdateInstaller.Install(scenario.InstallDir, scenario.StagingDir, new RecordingUpdateFileSystem());

        outcome.Success.Should().BeFalse();
        outcome.FailedFileName.Should().Be(missing);

        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            var targetPath = Path.Combine(scenario.InstallDir, fileName);
            File.ReadAllText(targetPath).Should().Be(Scenario.OldContent(fileName));
            File.Exists(targetPath + UpdateFiles.OldFileSuffix).Should().BeFalse();
        }

        scenario.AssertUserDataUntouched();
    }

    [Fact(DisplayName = "巻き戻し自体が一部失敗しても、可能な限り他のファイルは正しく巻き戻される（ベストエフォート）")]
    public void 巻き戻し自体の失敗はベストエフォートで続行する()
    {
        using var ws = new TempWorkspace();
        var scenario = new Scenario(ws);

        // 5回目（3ファイル目のリネーム）で失敗させ、1・2ファイル目は巻き戻し対象になる。
        // さらに、1ファイル目の.oldを巻き戻し直前に消してMoveFileが失敗するよう仕込み、
        // 「1件の巻き戻し失敗が他のファイルの巻き戻しを止めない」ことを確認する。
        var firstFile = UpdateFiles.RequiredFileNames[0];
        var fileSystem = new SabotagingFileSystem(
            failOnCallNumber: 5,
            sabotageOldPath: Path.Combine(scenario.InstallDir, firstFile + UpdateFiles.OldFileSuffix));

        var outcome = SelfUpdateInstaller.Install(scenario.InstallDir, scenario.StagingDir, fileSystem);

        outcome.Success.Should().BeFalse();

        // 2ファイル目（巻き戻しを妨害していない方）は正しく元へ戻る。
        var secondFile = UpdateFiles.RequiredFileNames[1];
        File.ReadAllText(Path.Combine(scenario.InstallDir, secondFile)).Should().Be(Scenario.OldContent(secondFile));

        scenario.AssertUserDataUntouched();
    }

    /// <summary>更新対象6ファイル＋利用者データ一式（settings.json等）を実ディスク上に用意する。</summary>
    private sealed class Scenario
    {
        public string InstallDir { get; }
        public string StagingDir { get; }

        private readonly Dictionary<string, string> _userDataFiles;

        public Scenario(TempWorkspace ws)
        {
            InstallDir = ws.CreateDirectory("install");
            StagingDir = ws.CreateDirectory("staging");

            foreach (var fileName in UpdateFiles.RequiredFileNames)
            {
                File.WriteAllText(Path.Combine(InstallDir, fileName), OldContent(fileName));
                File.WriteAllText(Path.Combine(StagingDir, fileName), NewContent(fileName));
            }

            // 指示書が名指しする利用者データファイル一式を、更新対象と同じフォルダに同居させる。
            _userDataFiles = new Dictionary<string, string>
            {
                ["settings.json"] = "{\"theme\":\"dark\"}",
                ["projects.json"] = "[{\"id\":\"p1\"}]",
                ["queue.json"] = "[]",
                ["layout.json"] = "{}",
                ["onboarding.done"] = "",
                ["datapath.txt"] = "",
            };
            foreach (var (name, content) in _userDataFiles)
            {
                File.WriteAllText(Path.Combine(InstallDir, name), content);
            }

            Directory.CreateDirectory(Path.Combine(InstallDir, "back", "p1", "r1_20260101_000000"));
            File.WriteAllText(Path.Combine(InstallDir, "back", "p1", "r1_20260101_000000", "manifest.json"), "{}");
            Directory.CreateDirectory(Path.Combine(InstallDir, "logs"));
            File.WriteAllText(Path.Combine(InstallDir, "logs", "20260101.log"), "起動しました");
        }

        public static string OldContent(string fileName) => $"OLD-{fileName}";
        public static string NewContent(string fileName) => $"NEW-{fileName}";

        /// <summary>利用者データが1バイトも変わっていない・増えても減ってもいないことを検証する。</summary>
        public void AssertUserDataUntouched()
        {
            foreach (var (name, content) in _userDataFiles)
            {
                File.ReadAllText(Path.Combine(InstallDir, name)).Should().Be(content, $"{name} は利用者データのため変更されてはいけない");
            }

            File.ReadAllText(Path.Combine(InstallDir, "back", "p1", "r1_20260101_000000", "manifest.json")).Should().Be("{}");
            File.ReadAllText(Path.Combine(InstallDir, "logs", "20260101.log")).Should().Be("起動しました");
        }
    }

    /// <summary>
    /// 指定回数目の操作で失敗させつつ、指定した.oldファイルを巻き戻し直前に削除しておくことで
    /// 「巻き戻し自体の失敗」を再現するフェイク。
    /// </summary>
    private sealed class SabotagingFileSystem : IUpdateFileSystem
    {
        private readonly RealUpdateFileSystem _real = new();
        private readonly int _failOnCallNumber;
        private readonly string _sabotageOldPath;
        private int _callCount;
        private bool _sabotaged;

        public SabotagingFileSystem(int failOnCallNumber, string sabotageOldPath)
        {
            _failOnCallNumber = failOnCallNumber;
            _sabotageOldPath = sabotageOldPath;
        }

        public bool FileExists(string path) => _real.FileExists(path);

        public void MoveFile(string sourcePath, string destinationPath)
        {
            // 巻き戻し（.old → 元の名前）が呼ばれるより前に、対象の.oldを消して失敗させる。
            if (!_sabotaged && string.Equals(sourcePath, _sabotageOldPath, StringComparison.Ordinal))
            {
                _real.DeleteFile(_sabotageOldPath);
                _sabotaged = true;
            }

            _callCount++;
            if (_callCount == _failOnCallNumber)
            {
                throw new IOException("テスト用に注入した失敗。");
            }

            _real.MoveFile(sourcePath, destinationPath);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        {
            _callCount++;
            if (_callCount == _failOnCallNumber)
            {
                throw new IOException("テスト用に注入した失敗。");
            }

            _real.CopyFile(sourcePath, destinationPath, overwrite);
        }

        public void DeleteFile(string path) => _real.DeleteFile(path);
    }
}
