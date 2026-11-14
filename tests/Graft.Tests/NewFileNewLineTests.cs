using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// B3の回帰テスト: 新規ファイルが常にCRLFで作られ、LFで統一したプロジェクト（Linux/macOS）に
/// CRLFが混ざっていた（実測: Linuxで MODE=FULL の run.py が <c>import os\r\nprint(1)\r\n</c>）。
/// 新規ファイルの改行コードは 設定 → 同じフォルダ（無ければプロジェクト）の既存ファイルで優勢な改行
/// → OSの既定 の順で決める。改行が1つも無い既存ファイルへの追記も同じ決め方にする。
/// </summary>
public class NewFileNewLineTests
{
    private const string FullPatch = "<<<< FILE: {0} MODE=FULL\nimport os\nprint(1)\n>>>> END\n";

    private static Settings WithNewLine(string value)
        => new() { Encoding = new EncodingSettings { NewFileNewLine = value } };

    // ------------------------------------------------------------------
    // 適用（結合テスト）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "B3: 周りのファイルがLFなら、設定が自動の新規ファイルはLFで作られる（修正前は常にCRLF）")]
    public async Task 周りがLFなら新規ファイルもLF()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectBytes("src/a.py", Encoding.UTF8.GetBytes("a = 1\nb = 2\n"));
        harness.WriteProjectBytes("src/b.py", Encoding.UTF8.GetBytes("c = 3\n"));
        var ctx = harness.MakeContext(1);

        var dryRun = await harness.DryRunAsync(string.Format(FullPatch, "src/run.py"), ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue();
        Encoding.UTF8.GetString(harness.ReadProjectBytes("src/run.py")).Should().Be("import os\nprint(1)\n");
    }

    [Fact(DisplayName = "B3: 周りのファイルがCRLFなら、新規ファイルもCRLFで作られる")]
    public async Task 周りがCRLFなら新規ファイルもCRLF()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectBytes("a.py", Encoding.UTF8.GetBytes("a = 1\r\nb = 2\r\n"));
        var ctx = harness.MakeContext(1);

        var dryRun = await harness.DryRunAsync(string.Format(FullPatch, "run.py"), ctx);
        await harness.ApplyAsync(dryRun, ctx);

        Encoding.UTF8.GetString(harness.ReadProjectBytes("run.py")).Should().Be("import os\r\nprint(1)\r\n");
    }

    [Fact(DisplayName = "B3: 設定でLFを指定すると、周りがCRLFでもLFで作られる")]
    public async Task 設定が周りより優先される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectBytes("a.py", Encoding.UTF8.GetBytes("a = 1\r\nb = 2\r\n"));
        var ctx = harness.MakeContext(1, WithNewLine("lf"));

        var dryRun = await harness.DryRunAsync(string.Format(FullPatch, "run.py"), ctx);
        await harness.ApplyAsync(dryRun, ctx);

        Encoding.UTF8.GetString(harness.ReadProjectBytes("run.py")).Should().Be("import os\nprint(1)\n");
    }

    [Fact(DisplayName = "B3: 手がかりが無い（空のプロジェクト）なら、OSの既定の改行で作られる")]
    public async Task 手がかりが無ければOSの既定()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var ctx = harness.MakeContext(1);

        var dryRun = await harness.DryRunAsync(string.Format(FullPatch, "run.py"), ctx);
        await harness.ApplyAsync(dryRun, ctx);

        Encoding.UTF8.GetString(harness.ReadProjectBytes("run.py"))
            .Should().Be("import os" + NewLineResolver.OsDefault + "print(1)" + NewLineResolver.OsDefault);
    }

    [Fact(DisplayName = "B3: 改行が1つも無い既存ファイルへのAPPENDも、周りの改行に合わせる")]
    public async Task 改行の無い既存ファイルへの追記も周りに合わせる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectBytes("a.py", Encoding.UTF8.GetBytes("a = 1\nb = 2\n"));
        harness.WriteProjectBytes("one.py", Encoding.UTF8.GetBytes("x = 0")); // 改行が1つも無い
        var ctx = harness.MakeContext(1);

        var dryRun = await harness.DryRunAsync("<<<< APPEND: one.py\ny = 1\nz = 2\n>>>> END\n", ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue();
        Encoding.UTF8.GetString(harness.ReadProjectBytes("one.py")).Should().Be("x = 0\ny = 1\nz = 2",
            "修正前は\\r\\nで追記されていた。末尾改行が無い元の形は維持される");
    }

    [Fact(DisplayName = "B3: 改行のある既存ファイルは、これまでどおりそのファイルの改行のまま")]
    public async Task 既存ファイルの改行は変わらない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectBytes("crlf.py", Encoding.UTF8.GetBytes("a = 1\r\nb = 2\r\n"));
        harness.WriteProjectBytes("lf1.py", Encoding.UTF8.GetBytes("x\ny\nz\n"));
        harness.WriteProjectBytes("lf2.py", Encoding.UTF8.GetBytes("x\ny\nz\n"));
        var ctx = harness.MakeContext(1);

        var dryRun = await harness.DryRunAsync("<<<< APPEND: crlf.py\nc = 3\n>>>> END\n", ctx);
        await harness.ApplyAsync(dryRun, ctx);

        Encoding.UTF8.GetString(harness.ReadProjectBytes("crlf.py")).Should().Be("a = 1\r\nb = 2\r\nc = 3\r\n");
    }

    // ------------------------------------------------------------------
    // NewLineResolver
    // ------------------------------------------------------------------

    [Fact(DisplayName = "B3: 同じフォルダに手がかりが無ければ親フォルダ（プロジェクトのルートまで）を見る")]
    public void 親フォルダまでたどる()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("proj");
        File.WriteAllBytes(Path.Combine(root, "top.txt"), Encoding.UTF8.GetBytes("a\r\nb\r\n"));
        var sub = Path.Combine(root, "deep", "er");
        Directory.CreateDirectory(sub);

        NewLineResolver.Resolve("auto", Path.Combine(sub, "new.txt"), root, osDefault: "\n").Should().Be("\r\n");
    }

    [Fact(DisplayName = "B3: プロジェクトのルートより上は見ない")]
    public void ルートより上は見ない()
    {
        using var ws = new TempWorkspace();
        var outer = ws.CreateDirectory("outer");
        File.WriteAllBytes(Path.Combine(outer, "outside.txt"), Encoding.UTF8.GetBytes("a\r\nb\r\n"));
        var root = Path.Combine(outer, "proj");
        Directory.CreateDirectory(root);

        NewLineResolver.Resolve("auto", Path.Combine(root, "new.txt"), root, osDefault: "\n").Should().Be("\n");
    }

    [Fact(DisplayName = "B3: バイナリ・改行の無いファイルは手がかりにせず、拮抗したらOSの既定")]
    public void 手がかりにならないファイルは飛ばす()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("proj");
        File.WriteAllBytes(Path.Combine(root, "bin.dat"), new byte[] { 0, 1, 2, 0x0D, 0x0A, 0x0D, 0x0A });
        File.WriteAllBytes(Path.Combine(root, "oneline.txt"), Encoding.UTF8.GetBytes("no newline"));
        File.WriteAllBytes(Path.Combine(root, "tie1.txt"), Encoding.UTF8.GetBytes("a\r\n"));
        File.WriteAllBytes(Path.Combine(root, "tie2.txt"), Encoding.UTF8.GetBytes("a\n"));

        NewLineResolver.Resolve("auto", Path.Combine(root, "new.txt"), root, osDefault: "\r\n").Should().Be("\r\n");
        NewLineResolver.Resolve("auto", Path.Combine(root, "new.txt"), root, osDefault: "\n").Should().Be("\n");
    }

    [Fact(DisplayName = "B3: 大量のファイルがあっても全件は開かない（候補の上限で打ち切る）")]
    public void 走査は軽く済ませる()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("proj");
        // 改行の無いファイルを大量に置く。全件を開くと遅いが、上限で打ち切って手がかり無しになる。
        for (var i = 0; i < 3000; i++) File.WriteAllText(Path.Combine(root, $"f{i:D4}.txt"), "x");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = NewLineResolver.Resolve("auto", Path.Combine(root, "new.txt"), root, osDefault: "\n");
        sw.Stop();

        result.Should().Be("\n");
        sw.ElapsedMilliseconds.Should().BeLessThan(500, "上限で打ち切るので3000ファイルあっても軽い");
    }

    [Theory(DisplayName = "B3: 設定値は大文字小文字を問わず、未知の値は自動として扱う")]
    [InlineData("CRLF", "\r\n")]
    [InlineData("Lf", "\n")]
    [InlineData("nonsense", "\r\n")] // 手がかり無し → osDefault
    public void 設定値の解釈(string setting, string expected)
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("proj");

        NewLineResolver.Resolve(setting, Path.Combine(root, "n.txt"), root, osDefault: "\r\n").Should().Be(expected);
    }

    // ------------------------------------------------------------------
    // 設定ファイルの互換
    // ------------------------------------------------------------------

    [Fact(DisplayName = "B3: newFileNewLine の無い旧版の settings.json は auto として読め、値は保存して読み戻せる")]
    public async Task 設定ファイルの互換()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        File.WriteAllText(paths.SettingsFilePath, """{ "encoding": { "newFileEncoding": "utf-8", "newFileBom": false } }""");
        var store = new SettingsStore(paths);

        var loaded = await store.LoadAsync();
        loaded.Value.Encoding.NewFileNewLine.Should().Be("auto");

        await store.SaveAsync(loaded.Value with { Encoding = loaded.Value.Encoding with { NewFileNewLine = "lf" } });
        (await new SettingsStore(paths).LoadAsync()).Value.Encoding.NewFileNewLine.Should().Be("lf");
    }
}
