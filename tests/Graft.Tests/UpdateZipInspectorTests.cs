using System.IO;
using System.IO.Compression;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="UpdateZipInspector"/>: 指示書の最重要事項（ZIPを素朴に展開すると利用者データを
/// 巻き込みうる）への対応。想定外のファイルが1つでも含まれていたら、1バイトも展開せずに
/// 中止することを固定する。
/// </summary>
public class UpdateZipInspectorTests
{
    [Fact(DisplayName = "6ファイルちょうどのZIPは検証を通り、すべて展開できる")]
    public void 正しい構成のZIPは検証を通る()
    {
        using var ws = new TempWorkspace();
        var zipPath = CreateZip(ws, "Graft", UpdateFiles.RequiredFileNames.ToDictionary(f => f, f => $"content-of-{f}"));

        var result = UpdateZipInspector.Validate(zipPath);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
        result.EntryByFileName.Should().HaveCount(UpdateFiles.RequiredFileNames.Count);

        var destination = ws.CreateDirectory("staged");
        UpdateZipInspector.ExtractTo(zipPath, result.EntryByFileName!, destination);

        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            var extracted = Path.Combine(destination, fileName);
            File.Exists(extracted).Should().BeTrue();
            File.ReadAllText(extracted).Should().Be($"content-of-{fileName}");
        }
    }

    [Fact(DisplayName = "想定外のファイルが1つでも含まれていたら中止する（展開しない）")]
    public void 想定外のファイルがあれば中止する()
    {
        using var ws = new TempWorkspace();
        var files = UpdateFiles.RequiredFileNames.ToDictionary(f => f, f => $"content-of-{f}");
        files["settings.json"] = "{\"theme\":\"dark\"}"; // 利用者データを装った想定外の混入。
        var zipPath = CreateZip(ws, "Graft", files);

        var result = UpdateZipInspector.Validate(zipPath);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("settings.json");
        result.EntryByFileName.Should().BeNull();
    }

    [Fact(DisplayName = "1ファイルでも欠けていたら中止する")]
    public void ファイルが不足していれば中止する()
    {
        using var ws = new TempWorkspace();
        var files = UpdateFiles.RequiredFileNames.ToDictionary(f => f, f => $"content-of-{f}");
        files.Remove("Graft.exe");
        var zipPath = CreateZip(ws, "Graft", files);

        var result = UpdateZipInspector.Validate(zipPath);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graft.exe");
    }

    [Fact(DisplayName = "同名ファイルが重複していたら中止する")]
    public void 同名ファイルの重複は中止する()
    {
        using var ws = new TempWorkspace();
        var zipPath = ws.Combine("update.zip");
        using (var stream = File.Create(zipPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var f in UpdateFiles.RequiredFileNames)
            {
                WriteEntry(archive, $"Graft/{f}", $"content-of-{f}");
            }
            // Graft.exeを別ディレクトリ配下にもう1つ重複させる。
            WriteEntry(archive, $"Graft/sub/Graft.exe", "duplicate");
        }

        var result = UpdateZipInspector.Validate(zipPath);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graft.exe");
    }

    [Fact(DisplayName = "ZIPとして開けないファイルはエラーとして扱う（例外を投げない）")]
    public void ZIPとして開けない場合はエラーを返す()
    {
        using var ws = new TempWorkspace();
        var notAZip = ws.WriteText("broken.zip", "これはZIPではありません");

        var result = UpdateZipInspector.Validate(notAZip);

        result.IsValid.Should().BeFalse();
    }

    /// <summary>指定したファイル名→内容の一覧を、トップレベルフォルダ<paramref name="rootFolder"/>付きのZIPとして書き出す。</summary>
    private static string CreateZip(TempWorkspace ws, string rootFolder, IReadOnlyDictionary<string, string> files)
    {
        var zipPath = ws.Combine($"{Guid.NewGuid():N}.zip");
        using var stream = File.Create(zipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            WriteEntry(archive, $"{rootFolder}/{name}", content);
        }
        return zipPath;
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
