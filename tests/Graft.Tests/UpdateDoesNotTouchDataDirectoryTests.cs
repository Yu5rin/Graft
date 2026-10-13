using System.IO;
using System.IO.Compression;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 1.0.26: 自動更新が exe 直下の data フォルダを入れ替え・削除しないことの確認。
/// 入れ替え（<see cref="SelfUpdateInstaller"/>）は <see cref="UpdateFiles.RequiredFileNames"/> の
/// 6 ファイル名だけ、掃除（<see cref="PendingUpdateCleanup"/>）はその「.old」だけを、
/// インストール先の直下で扱う。data 配下には、同名のファイルがあっても届かない。
/// </summary>
public class UpdateDoesNotTouchDataDirectoryTests
{
    private static string Snapshot(string dir) => string.Join("\n",
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(dir, f) + "=" + File.ReadAllText(f)));

    private static void PopulateData(TempWorkspace ws)
    {
        ws.WriteText("exe/data/settings.json", "{}");
        ws.WriteText("exe/data/back/p/r1/manifest.json", "{}");
        ws.WriteText("exe/data/logs/20260101-1.log", "log");
        // 配布物と同じ名前のファイルや .old を data の中に置いても、触れられないこと。
        foreach (var name in UpdateFiles.RequiredFileNames)
        {
            ws.WriteText($"exe/data/{name}", "data内の同名");
            ws.WriteText($"exe/data/{name}.old", "data内の同名old");
        }
    }

    [Fact(DisplayName = "入れ替え（成功）の前後で data 配下は1バイトも変わらない")]
    public void 入れ替えはdataに触れない()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        var staging = ws.CreateDirectory("staging");
        foreach (var name in UpdateFiles.RequiredFileNames)
        {
            ws.WriteText($"exe/{name}", "旧" + name);
            ws.WriteText($"staging/{name}", "新" + name);
        }
        PopulateData(ws);
        var before = Snapshot(Path.Combine(exe, "data"));

        var outcome = SelfUpdateInstaller.Install(exe, staging);

        outcome.Success.Should().BeTrue(outcome.ErrorMessage);
        Snapshot(Path.Combine(exe, "data")).Should().Be(before);
        File.ReadAllText(Path.Combine(exe, "Graft.exe")).Should().Be("新Graft.exe");
    }

    [Fact(DisplayName = "次回起動時の.old掃除は、exe 直下の.oldだけを消し、data 配下の同名.oldは消さない")]
    public void 掃除はdataに及ばない()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        foreach (var name in UpdateFiles.RequiredFileNames) ws.WriteText($"exe/{name}.old", "old");
        PopulateData(ws);
        var before = Snapshot(Path.Combine(exe, "data"));

        var removed = PendingUpdateCleanup.Run(exe);

        removed.Should().BeEquivalentTo(UpdateFiles.RequiredFileNames);
        Snapshot(Path.Combine(exe, "data")).Should().Be(before);
    }

    [Fact(DisplayName = "配布物ZIPに data/ 配下のファイルが含まれていたら、展開せずに中止する")]
    public void ZIPにdataが含まれていたら中止する()
    {
        using var ws = new TempWorkspace();
        var zipPath = ws.Combine("x.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in UpdateFiles.RequiredFileNames)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write("x");
            }
            using var w2 = new StreamWriter(zip.CreateEntry("data/settings.json").Open());
            w2.Write("{}");
        }

        var result = UpdateZipInspector.Validate(zipPath);

        result.IsValid.Should().BeFalse();
    }
}
