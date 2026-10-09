using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 同じ番号のバックアップフォルダを起動時に振り直す処理（<see cref="RevisionDuplicateRepairer"/>）のテスト。
/// 実機（EPSEnhance）で起きた「r36〜r42が2つずつあり、その後にr43・r44が続く」形を再現する。
/// </summary>
public class RevisionDuplicateRepairerTests
{
    private const string ProjectId = "p_dup001";
    private static readonly DateTime Day = new(2026, 10, 9);

    private sealed class Fixture : IDisposable
    {
        private readonly TempWorkspace _ws = new();
        public AppPaths Paths { get; }
        public string ProjectDir => Paths.GetProjectBackupDirectory(ProjectId);
        public RevisionIndex Index { get; }
        public RevisionStore Store { get; }

        public Fixture()
        {
            Paths = new AppPaths(_ws.CreateDirectory("app"));
            Directory.CreateDirectory(ProjectDir);
            Index = new RevisionIndex(Paths);
            Store = new RevisionStore(Paths);
        }

        /// <summary>
        /// 1リビジョン分のフォルダ・manifest.json・退避ファイル・history.jsonlの1行を作る。
        /// 退避ファイル(files/marker.txt)には識別用の文字列を入れ、振り直し後も中身が付いてくるかを確かめる。
        /// </summary>
        public async Task AddAsync(int revision, DateTime appliedAt, string marker, bool withFolder = true, bool withHistory = true)
        {
            var at = new DateTimeOffset(appliedAt, TimeZoneInfo.Local.GetUtcOffset(appliedAt));
            var folderName = AppPaths.BuildRevisionFolderName(revision, at);
            var manifest = new RevisionManifest
            {
                Revision = revision,
                ProjectId = ProjectId,
                Summary = marker,
                Type = "fix",
                AppliedAt = at,
                Status = RevisionStatus.Success,
                Entries = new[]
                {
                    new RevisionEntry { Path = "marker.txt", Operation = EntryOperation.Modify },
                },
            };

            if (withFolder)
            {
                var folder = Path.Combine(ProjectDir, folderName);
                Directory.CreateDirectory(Path.Combine(folder, "files"));
                await File.WriteAllTextAsync(Path.Combine(folder, "files", "marker.txt"), marker);
                await new JsonFileStore().WriteAsync(Path.Combine(folder, "manifest.json"), manifest, JsonFileStore.DefaultOptions);
            }

            if (withHistory)
            {
                await Index.AppendAsync(ProjectId, new RevisionIndexEntry
                {
                    Revision = revision,
                    Summary = marker,
                    Type = "fix",
                    AppliedAt = at,
                    Status = RevisionStatus.Success,
                    FolderName = folderName,
                });
            }
        }

        public async Task<RevisionManifest> ReadManifestAsync(string folderName)
        {
            var r = await new JsonFileStore().ReadWithRecoveryAsync<RevisionManifest>(
                Path.Combine(ProjectDir, folderName, "manifest.json"),
                () => throw new InvalidOperationException("manifestが読めない: " + folderName),
                JsonFileStore.DefaultOptions);
            return r.Value;
        }

        public string[] FolderNames() => Directory.EnumerateDirectories(ProjectDir)
            .Select(d => Path.GetFileName(d)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        /// <summary>back/&lt;id&gt;/ 配下の全ファイルの相対パス→SHA-256と、全ディレクトリの相対パス。</summary>
        public string Snapshot()
        {
            var lines = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(ProjectDir, "*", SearchOption.AllDirectories))
                lines.Add("D " + Path.GetRelativePath(ProjectDir, dir));
            foreach (var file in Directory.EnumerateFiles(ProjectDir, "*", SearchOption.AllDirectories))
                lines.Add("F " + Path.GetRelativePath(ProjectDir, file) + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))
                          + " " + File.GetLastWriteTimeUtc(file).Ticks.ToString(CultureInfo.InvariantCulture));
            lines.Sort(StringComparer.Ordinal);
            return string.Join("\n", lines);
        }

        public void Dispose() => _ws.Dispose();
    }

    /// <summary>
    /// 実機の形: r35（重複なし）、r36〜r42（13:30〜）、そして r36〜r44（15:59〜）の2組。
    /// 振り直し後は r35 はそのまま、時刻順に r36〜r51 になる。
    /// </summary>
    private static async Task BuildRealWorldShapeAsync(Fixture fx)
    {
        await fx.AddAsync(35, Day.AddHours(13).AddMinutes(26), "first-r35");
        for (var i = 0; i < 7; i++)
            await fx.AddAsync(36 + i, Day.AddHours(13).AddMinutes(30 + i * 10), $"first-r{36 + i}");
        for (var i = 0; i < 9; i++)
            await fx.AddAsync(36 + i, Day.AddHours(15).AddMinutes(59 + i * 3), $"second-r{36 + i}");
    }

    [Fact(DisplayName = "r36〜r42が2組とr43・r44の16件は、時刻順にr36〜r51へ振り直され、番号・manifest・history.jsonl・フォルダ名がそろう")]
    public async Task 実機の形の16件が時刻順の連番になる()
    {
        using var fx = new Fixture();
        await BuildRealWorldShapeAsync(fx);

        var result = await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);

        result.Issues.Should().BeEmpty();
        // 先の組(r36〜r42)は時刻順でも先頭なので番号はそのまま。後の組(9件)だけがr43〜r51へ動く。
        result.Value.Should().HaveCount(9);
        result.Value.Select(c => (c.OldRevision, c.NewRevision)).Should().Equal(
            Enumerable.Range(0, 9).Select(i => (36 + i, 43 + i)));

        var expected = new List<(int Rev, string Marker)> { (35, "first-r35") };
        expected.AddRange(Enumerable.Range(0, 7).Select(i => (36 + i, $"first-r{36 + i}")));
        expected.AddRange(Enumerable.Range(0, 9).Select(i => (43 + i, $"second-r{36 + i}")));

        var folders = fx.FolderNames();
        folders.Should().HaveCount(17, "r35 + 16件。消えたフォルダも増えたフォルダも無い");
        folders.Should().NotContain(n => n.EndsWith(".repairing"), "一時名のまま残らない");
        folders.Select(n => BackupPathUtil.TryParseFolderName(n)!.Value.Revision).Should().OnlyHaveUniqueItems();

        foreach (var (rev, marker) in expected)
        {
            var folder = folders.Single(n => n.StartsWith($"r{rev}_"));
            (await File.ReadAllTextAsync(Path.Combine(fx.ProjectDir, folder, "files", "marker.txt"))).Should().Be(marker,
                $"r{rev} のフォルダの中身は{marker}のはず（中身ごと動く）");
            (await fx.ReadManifestAsync(folder)).Revision.Should().Be(rev, "manifestのrevisionも新しい番号になる");
        }

        var history = (await fx.Index.ReadAllAsync(ProjectId)).Value;
        history.Should().HaveCount(17);
        foreach (var (rev, marker) in expected)
        {
            var entry = history.Single(e => e.Summary == marker);
            entry.Revision.Should().Be(rev, $"history.jsonlの{marker}の行は番号が{rev}になる");
            entry.FolderName.Should().Be(folders.Single(n => n.StartsWith($"r{rev}_")));
        }

        // 履歴一覧が17件すべてを返し、最大が51、nextRevision補正が52になる。
        var list = await fx.Store.ListAsync(ProjectId);
        list.Value.Select(r => r.Manifest.Revision).Should().Equal(Enumerable.Range(35, 17).Reverse());
        var max = await fx.Store.DetectMaxRevisionAsync(ProjectId);
        max.Value.Should().Be(51);
        ProjectStore.ReconcileRevision(new Project { Id = ProjectId, NextRevision = 45 }, max.Value).NextRevision.Should().Be(52);

        File.Exists(Path.Combine(fx.ProjectDir, "repair-journal.json")).Should().BeFalse("完了したら途中経過の記録は片付ける");
    }

    [Fact(DisplayName = "番号の順序と時刻の順序が入り混じる重複でも、重複する最小番号以上がすべて時刻順に並び直る")]
    public async Task 時刻順と番号順が食い違う入り混じりも時刻順になる()
    {
        using var fx = new Fixture();
        await fx.AddAsync(4, Day.AddHours(9), "keep-r4");            // 重複より小さい番号: 触れない
        await fx.AddAsync(5, Day.AddHours(10), "A");                 // 重複(5)の1つ目
        await fx.AddAsync(6, Day.AddHours(10).AddMinutes(30), "C");  // 時刻はAとBの間
        await fx.AddAsync(5, Day.AddHours(11), "B");                 // 重複(5)の2つ目
        await fx.AddAsync(7, Day.AddHours(9).AddMinutes(30), "D");   // 番号は大きいが時刻は最も古い(r4より後・A以前)

        var result = await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);

        result.Issues.Should().BeEmpty();
        // 時刻順は D(9:30) → A(10:00) → C(10:30) → B(11:00)。最小の重複番号5から連番。
        async Task<string> MarkerAt(int rev)
        {
            var folder = fx.FolderNames().Single(n => n.StartsWith($"r{rev}_"));
            return await File.ReadAllTextAsync(Path.Combine(fx.ProjectDir, folder, "files", "marker.txt"));
        }
        (await MarkerAt(4)).Should().Be("keep-r4");
        (await MarkerAt(5)).Should().Be("D");
        (await MarkerAt(6)).Should().Be("A");
        (await MarkerAt(7)).Should().Be("C");
        (await MarkerAt(8)).Should().Be("B");
        fx.FolderNames().Should().HaveCount(5);
    }

    [Fact(DisplayName = "重複が無いときは、ディスク上の何も（フォルダ・ファイル・更新時刻）変わらない")]
    public async Task 重複が無ければ何にも触れない()
    {
        using var fx = new Fixture();
        for (var i = 1; i <= 5; i++) await fx.AddAsync(i, Day.AddHours(9 + i), $"r{i}");
        var before = fx.Snapshot();

        var result = await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);

        result.Value.Should().BeEmpty();
        result.Issues.Should().BeEmpty();
        fx.Snapshot().Should().Be(before, "重複が無ければフォルダにもhistory.jsonlにも一切書かない");
        fx.FolderNames().Should().HaveCount(5);
    }

    [Fact(DisplayName = "バックアップ領域が無いプロジェクトでも例外にならず、フォルダも作らない")]
    public async Task バックアップ領域が無くても何もしない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));

        var result = await new RevisionDuplicateRepairer(paths).RepairAsync("p_none");

        result.Value.Should().BeEmpty();
        Directory.Exists(paths.GetProjectBackupDirectory("p_none")).Should().BeFalse();
    }

    [Fact(DisplayName = "同じ秒に適用された重複は、元の番号の小さい順、それでも同じならフォルダ名の順に並ぶ")]
    public async Task 同時刻なら元の番号とフォルダ名の順になる()
    {
        using var fx = new Fixture();
        var t = Day.AddHours(12);
        await fx.AddAsync(3, t, "x-r3");
        await fx.AddAsync(3, t.AddMinutes(5), "later-r3");
        await fx.AddAsync(4, t, "x-r4"); // r3と同じ秒

        await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);

        async Task<string> MarkerAt(int rev)
        {
            var folder = fx.FolderNames().Single(n => n.StartsWith($"r{rev}_"));
            return await File.ReadAllTextAsync(Path.Combine(fx.ProjectDir, folder, "files", "marker.txt"));
        }
        // 同時刻(12:00)の x-r3(元3) と x-r4(元4) は元の番号順、その後に later-r3。
        (await MarkerAt(3)).Should().Be("x-r3");
        (await MarkerAt(4)).Should().Be("x-r4");
        (await MarkerAt(5)).Should().Be("later-r3");
    }

    [Fact(DisplayName = "manifest.jsonはrevisionだけが変わり、他の項目（未知の項目を含む）はそのまま残る")]
    public async Task manifestはrevisionだけが変わる()
    {
        using var fx = new Fixture();
        await fx.AddAsync(2, Day.AddHours(9), "first");
        await fx.AddAsync(2, Day.AddHours(10), "second");
        var secondFolder = fx.FolderNames().Single(n => n.Contains("_" + Day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "_100000"));
        var manifestPath = Path.Combine(fx.ProjectDir, secondFolder, "manifest.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        node["futureField"] = "将来の版が足す項目";
        await File.WriteAllTextAsync(manifestPath, node.ToJsonString());

        await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);

        var newFolder = fx.FolderNames().Single(n => n.StartsWith("r3_"));
        var after = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fx.ProjectDir, newFolder, "manifest.json")))!.AsObject();
        after["revision"]!.GetValue<int>().Should().Be(3);
        after["futureField"]!.GetValue<string>().Should().Be("将来の版が足す項目");
        after["summary"]!.GetValue<string>().Should().Be("second");
        after["status"]!.GetValue<string>().Should().Be("success");
    }

    [Fact(DisplayName = "フォルダが無くhistory.jsonlにだけ残る記録も、並び順が崩れないよう同じ連番に含める")]
    public async Task 履歴だけの記録も連番に含まれる()
    {
        using var fx = new Fixture();
        await fx.AddAsync(7, Day.AddHours(9), "dup-first");
        await fx.AddAsync(7, Day.AddHours(11), "dup-second");
        await fx.AddAsync(8, Day.AddHours(11).AddMinutes(30), "history-only", withFolder: false); // 最も新しい。フォルダは無い

        var result = await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);

        result.Value.Should().HaveCount(2);
        result.Value.Single(c => !c.HasFolder).NewRevision.Should().Be(9);
        var history = (await fx.Index.ReadAllAsync(ProjectId)).Value;
        history.Single(e => e.Summary == "dup-first").Revision.Should().Be(7);
        history.Single(e => e.Summary == "dup-second").Revision.Should().Be(8);
        history.Single(e => e.Summary == "history-only").Revision.Should().Be(9, "フォルダが無くても時刻順の連番に含まれる");
        fx.FolderNames().Where(n => n.StartsWith('r')).Select(n => n.Split('_')[0]).Should().BeEquivalentTo(new[] { "r7", "r8" });
    }

    [Fact(DisplayName = "history.jsonlの解析できない行は、修復後もそのまま残る")]
    public async Task 壊れた履歴行は変えない()
    {
        using var fx = new Fixture();
        await fx.AddAsync(2, Day.AddHours(9), "a");
        await fx.AddAsync(2, Day.AddHours(10), "b");
        await File.AppendAllTextAsync(fx.Index.GetIndexPath(ProjectId), "{ broken json" + Environment.NewLine);

        await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);

        (await File.ReadAllTextAsync(fx.Index.GetIndexPath(ProjectId))).Should().Contain("{ broken json");
        (await fx.Index.ReadAllAsync(ProjectId)).Value.Select(e => e.Revision).Should().BeEquivalentTo(new[] { 2, 3 });
    }

    [Fact(DisplayName = "修復の前に履歴の複製（history.jsonl.before-renumber-…）が残る")]
    public async Task 修復前の履歴の複製が残る()
    {
        using var fx = new Fixture();
        await fx.AddAsync(2, Day.AddHours(9), "a");
        await fx.AddAsync(2, Day.AddHours(10), "b");
        var original = await File.ReadAllTextAsync(fx.Index.GetIndexPath(ProjectId));

        await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);

        var backup = Directory.EnumerateFiles(fx.ProjectDir, "history.jsonl.before-renumber-*").Single();
        (await File.ReadAllTextAsync(backup)).Should().Be(original);
    }

    [Fact(DisplayName = "修復を2回続けて呼んでも、2回目は何も変えない（冪等）")]
    public async Task 修復は冪等()
    {
        using var fx = new Fixture();
        await BuildRealWorldShapeAsync(fx);
        var repairer = new RevisionDuplicateRepairer(fx.Paths);
        await repairer.RepairAsync(ProjectId);
        var after1 = fx.Snapshot();

        var second = await repairer.RepairAsync(ProjectId);

        second.Value.Should().BeEmpty();
        fx.Snapshot().Should().Be(after1);
    }

    // ------------------------------------------------------------------
    // 途中で落ちた場合（一時名の2段階リネームと対応表からの再開）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "全フォルダを一時名へ移した直後に落ちていても、次回の修復で最終名・manifest・履歴まで完了し、フォルダは1つも失われない")]
    public async Task 一時名へ移した直後に落ちても再開できる()
    {
        using var fx = new Fixture();
        await fx.AddAsync(2, Day.AddHours(9), "a");
        await fx.AddAsync(2, Day.AddHours(10), "b");
        // 修復を「全フォルダを一時名へ移した」状態で止めた姿を作る: aはそのまま(r2)、bはr3になるはず。
        var bOld = fx.FolderNames().Single(n => n.EndsWith("_100000"));
        var bNew = "r3_" + bOld.Split('_', 2)[1];
        Directory.Move(Path.Combine(fx.ProjectDir, bOld), Path.Combine(fx.ProjectDir, bNew + ".repairing"));
        var journal = new JsonObject
        {
            ["phase"] = "movedToTemp",
            ["historyBackupFile"] = null,
            ["items"] = new JsonArray(new JsonObject
            {
                ["oldRevision"] = 2, ["newRevision"] = 3, ["oldFolderName"] = bOld, ["newFolderName"] = bNew, ["hasFolder"] = true,
            }),
        };
        await File.WriteAllTextAsync(Path.Combine(fx.ProjectDir, "repair-journal.json"), journal.ToJsonString());

        var result = await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);

        result.Issues.Should().BeEmpty();
        var folders = fx.FolderNames();
        folders.Should().NotContain(n => n.EndsWith(".repairing"));
        folders.Should().Contain(bNew);
        (await File.ReadAllTextAsync(Path.Combine(fx.ProjectDir, bNew, "files", "marker.txt"))).Should().Be("b");
        (await fx.ReadManifestAsync(bNew)).Revision.Should().Be(3);
        (await fx.Index.ReadAllAsync(ProjectId)).Value.Single(e => e.Summary == "b").Revision.Should().Be(3);
        File.Exists(Path.Combine(fx.ProjectDir, "repair-journal.json")).Should().BeFalse();
    }

    [Fact(DisplayName = "対応表が無いまま一時名で残ったフォルダは、名前に含まれる最終名へ戻される")]
    public async Task 対応表が無くても一時名のフォルダは戻される()
    {
        using var fx = new Fixture();
        await fx.AddAsync(2, Day.AddHours(9), "a");
        var name = fx.FolderNames().Single();
        Directory.Move(Path.Combine(fx.ProjectDir, name), Path.Combine(fx.ProjectDir, name + ".repairing"));

        await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);

        fx.FolderNames().Should().Equal(name);
    }

    [Fact(DisplayName = "読み取れない対応表は脇へ退避して警告し、毎回同じ警告を出し続けない")]
    public async Task 壊れた対応表は退避される()
    {
        using var fx = new Fixture();
        await fx.AddAsync(2, Day.AddHours(9), "a");
        await File.WriteAllTextAsync(Path.Combine(fx.ProjectDir, "repair-journal.json"), "{ not json");

        var first = await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);
        var second = await new RevisionDuplicateRepairer(fx.Paths).RepairAsync(ProjectId);

        first.Issues.Should().ContainSingle();
        second.Issues.Should().BeEmpty();
        File.Exists(Path.Combine(fx.ProjectDir, "repair-journal.json")).Should().BeFalse();
    }

    [Fact(DisplayName = "BackupManagerは、同じ番号のバックアップフォルダが（時刻違いでも）既にあれば作らずに失敗する")]
    public async Task 同じ番号のフォルダは作れない()
    {
        using var fx = new Fixture();
        await fx.AddAsync(5, Day.AddHours(9), "existing");
        var before = fx.Snapshot();

        var began = await new BackupManager(fx.Paths).BeginAsync(
            ProjectId, fx.ProjectDir, new RevisionManifest { Revision = 5, ProjectId = ProjectId, AppliedAt = new DateTimeOffset(Day.AddHours(15)) });

        began.IsSuccess.Should().BeFalse("既存のr5と同じ番号では作れない");
        fx.Snapshot().Should().Be(before, "失敗したら何も作らない");

        var ok = await new BackupManager(fx.Paths).BeginAsync(
            ProjectId, fx.ProjectDir, new RevisionManifest { Revision = 6, ProjectId = ProjectId, AppliedAt = new DateTimeOffset(Day.AddHours(15)) });
        ok.IsSuccess.Should().BeTrue("別の番号なら作れる");
    }
}
