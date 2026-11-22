using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core.PromptFocus;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="PromptFocusStore"/>（組み込み17件と focus.json）のテスト。仕様書_ブックの情報と依頼の観点 4.2・11.1。
/// </summary>
public class PromptFocusStoreTests
{
    private static readonly string[] ExpectedIds =
    {
        "focus-readable", "focus-maintainable", "focus-minimal",
        "focus-bugfix", "focus-robust", "focus-warnings",
        "focus-speed", "focus-security", "focus-tests", "focus-docs",
        "focus-design-look", "focus-design-usability", "focus-design-responsive", "focus-design-a11y",
        "focus-vba-64bit", "focus-vba-latebind", "focus-vba-guard",
    };

    [Fact(DisplayName = "組み込みは17件で、Id は仕様どおり・重複しない")]
    public void 組み込みの件数とId()
    {
        PromptFocusStore.BuiltIns.Select(f => f.Id).Should().Equal(ExpectedIds);
        PromptFocusStore.BuiltIns.Select(f => f.Id).Should().OnlyHaveUniqueItems();
        PromptFocusStore.BuiltIns.Should().OnlyContain(f => f.IsBuiltIn);
    }

    [Fact(DisplayName = "組み込みは見出し・名前・ツールチップ・指示文を持ち、指示文は「## 」で始まる")]
    public void 組み込みの中身()
    {
        foreach (var focus in PromptFocusStore.BuiltIns)
        {
            focus.Name.Should().NotBeNullOrWhiteSpace(focus.Id);
            focus.Tooltip.Should().NotBeNullOrWhiteSpace(focus.Id);
            focus.Body.Should().StartWith("## ", focus.Id);
            PromptFocusGroups.All.Should().Contain(focus.Group, focus.Id);
            focus.Body.Should().NotContain("\r", "改行は LF にそろえる（環境で依頼文のバイト列が変わらないように）");
        }
    }

    [Fact(DisplayName = "見出しの構成は 14チップ（汎用）＋VBA 3 で、VBA 専用は VBA の3つだけ")]
    public void 見出しの構成()
    {
        var byGroup = PromptFocusStore.BuiltIns.GroupBy(f => f.Group).ToDictionary(g => g.Key, g => g.Select(f => f.Name).ToArray());

        byGroup[PromptFocusGroups.Clean].Should().Equal("読みやすく", "保守しやすく", "変更は最小限");
        byGroup[PromptFocusGroups.Correct].Should().Equal("不具合を直す", "エラーに強く", "警告・型を直す");
        byGroup[PromptFocusGroups.Quality].Should().Equal("速く", "安全に", "テストを書く", "説明を書く");
        byGroup[PromptFocusGroups.Design].Should().Equal("見た目を整える", "使いやすく", "スマホ対応", "誰でも使える");
        byGroup[PromptFocusGroups.Vba].Should().Equal("64bit対応", "参照設定なし", "操作ミス対策");

        PromptFocusStore.BuiltIns.Where(f => f.VbaOnly).Select(f => f.Id)
            .Should().Equal("focus-vba-64bit", "focus-vba-latebind", "focus-vba-guard");
    }

    [Fact(DisplayName = "VBA 追記を持つのは仕様書6.5.2の13チップ（変更は最小限と VBA 専用3つは持たない）")]
    public void VBA追記を持つチップ()
    {
        var withAddendum = PromptFocusStore.BuiltIns.Where(f => !string.IsNullOrWhiteSpace(f.VbaAddendum)).Select(f => f.Name);

        withAddendum.Should().BeEquivalentTo(new[]
        {
            "読みやすく", "保守しやすく", "不具合を直す", "エラーに強く", "警告・型を直す", "速く", "安全に",
            "テストを書く", "説明を書く", "見た目を整える", "使いやすく", "スマホ対応", "誰でも使える",
        });
        PromptFocusStore.BuiltIns.Single(f => f.Id == "focus-minimal").VbaAddendum.Should().BeNull();
    }

    [Fact(DisplayName = "指示文は仕様書の全文のまま（代表的な文の一字一句を固定）")]
    public void 指示文は全文()
    {
        PromptFocusStore.BuiltIns.Single(f => f.Id == "focus-minimal").Body.Should().Be(
            "## 変更は最小限\n"
            + "- 目的: 依頼の達成に必要な行だけを変え、差分を小さくする。\n"
            + "- 整形・命名・並び順など、依頼と関係のない変更はしない。気付いた改善点は、コードを変えずに最後に箇条書きで挙げる。\n"
            + "- 既存の構造・書き方を活かし、新しい関数やファイルを足すのは避けられない場合だけにする。");

        PromptFocusStore.BuiltIns.Single(f => f.Id == "focus-vba-guard").Body.Should().Be(
            "## 利用者の操作ミスに備える\n"
            + "- 対象のシート・ブック・範囲が存在しない、保護されている、選択が想定と違う、空のセル、フィルターで行が隠れている、といった状況でも誤ったセルを書き換えないようにしてください。\n"
            + "- 大きな書き換えの前には、件数と対象を示して MsgBox で確認してください。");

        PromptFocusStore.BuiltIns.Single(f => f.Id == "focus-design-look").VbaAddendum.Should().Be(
            "- ユーザーフォームのコントロールの配置・色・大きさ（.frx）は変更できません。変えられるのは、コードで設定している Caption・Enabled・Visible などと、MsgBox の文言・アイコンだけです。");
    }

    [Fact(DisplayName = "focus.json が無ければ組み込みだけを返し、読み込みでファイルを作らない")]
    public async Task ファイルが無ければ組み込みだけ()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));

        var loaded = await new PromptFocusStore(paths).LoadAsync();

        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Select(f => f.Id).Should().Equal(ExpectedIds);
        File.Exists(paths.FocusFilePath).Should().BeFalse();
    }

    [Fact(DisplayName = "自作の観点は focus.json に往復でき、組み込みは書き出されない")]
    public async Task 自作の往復と組み込みは書き出さない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new PromptFocusStore(paths);
        var mine = new PromptFocus
        {
            Id = "custom-1", Group = PromptFocusGroups.Mine, Name = "自作", Tooltip = "ひとこと",
            Body = "## 自作\n- 文", VbaAddendum = "- 追記", VbaOnly = true,
        };

        await store.SaveAsync(PromptFocusStore.BuiltIns.Concat(new[] { mine }).ToArray());

        var json = await File.ReadAllTextAsync(paths.FocusFilePath);
        json.Should().Contain("custom-1");
        json.Should().NotContain("focus-readable", "組み込みは書き出さない（版を上げたときに古い文が居座らないように）");

        var loaded = await store.LoadAsync();
        loaded.Value.Should().HaveCount(18);
        loaded.Value.Last().Should().BeEquivalentTo(mine);
        loaded.Value.Take(17).Select(f => f.Id).Should().Equal(ExpectedIds, "自作は組み込みの後ろ");
    }

    [Fact(DisplayName = "focus.json の中に IsBuiltIn の行や組み込みと同じ Id の行があっても、読み捨てる")]
    public async Task 偽装された組み込みは読み捨てる()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        await File.WriteAllTextAsync(paths.FocusFilePath,
            """
            {"focuses":[
              {"id":"focus-readable","group":"きれいに","name":"乗っ取り","body":"## x","isBuiltIn":false},
              {"id":"custom-x","group":"自分用","name":"偽の組み込み","body":"## y","isBuiltIn":true},
              {"id":"custom-ok","group":"自分用","name":"正しい自作","body":"## z"}
            ]}
            """);

        var loaded = await new PromptFocusStore(paths).LoadAsync();

        loaded.Value.Single(f => f.Id == "focus-readable").Name.Should().Be("読みやすく", "組み込みの Id は上書きできない");
        loaded.Value.Should().NotContain(f => f.Id == "custom-x");
        loaded.Value.Select(f => f.Id).Last().Should().Be("custom-ok");
    }

    [Fact(DisplayName = "壊れた focus.json は退避され、組み込みだけで復旧する")]
    public async Task 壊れたJSONから復旧する()
    {
        using var ws = new TempWorkspace();
        var appDir = ws.CreateDirectory("app");
        var paths = new AppPaths(appDir);
        await File.WriteAllTextAsync(paths.FocusFilePath, "{ これはJSONではない");

        var loaded = await new PromptFocusStore(paths).LoadAsync();

        loaded.IsSuccess.Should().BeTrue("壊れていても組み込みで復旧し、成功として返る");
        loaded.Issues.Should().ContainSingle();
        loaded.Value.Select(f => f.Id).Should().Equal(ExpectedIds);
        Directory.GetFiles(appDir, "focus.json.corrupt.*").Should().ContainSingle("壊れた実物は消さずに退避する");
    }

    [Theory(DisplayName = "指示文の先頭が「## 」でなければ「## 名前」の行を補う")]
    [InlineData("名前", "", "## 名前")]
    [InlineData("名前", "- 本文", "## 名前\n- 本文")]
    [InlineData("名前", "## 自分の見出し\n- 本文", "## 自分の見出し\n- 本文")]
    [InlineData("名前", "\n\n- 先頭に空行", "## 名前\n- 先頭に空行")]
    [InlineData("名前", "#見出しの形ではない", "## 名前\n#見出しの形ではない")]
    public void 見出しを補う(string name, string body, string expected)
    {
        PromptFocusStore.EnsureHeading(name, body).Should().Be(expected);
    }
}
