using FluentAssertions;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// エディタの右クリックメニュー「選択範囲の修正依頼プロンプトをコピー」が組み立てる
/// プロンプト（<see cref="PromptTemplateStore.BuildSelectionFixRequestPrompt"/>）の単体テスト。
/// UIに依存しない純粋メソッドのため、AvaloniaEditやViewModelを介さずに検証できる。
/// </summary>
public class PromptTemplateStoreSelectionTests
{
    [Fact(DisplayName = "対象パス・行範囲・選択コード・修正依頼の誘導行を含む")]
    public void 対象パスと行範囲と選択コードと誘導行を含む()
    {
        var prompt = PromptTemplateStore.BuildSelectionFixRequestPrompt(
            "src/Graft/Foo.cs", startLine: 10, endLine: 12, selectedCode: "var x = 1;\n", fileExtension: ".cs");

        prompt.Should().Contain("対象: src/Graft/Foo.cs（10〜12行目）", "対象ファイルの相対パスと行範囲を明示する必要がある");
        prompt.Should().Contain("var x = 1;", "選択したコード本文を含める必要がある");
        prompt.Should().Contain("このコードの修正を依頼します。修正内容: ", "利用者が依頼内容を書き足すための誘導行が必要");
    }

    [Fact(DisplayName = "既定の修正依頼テンプレートの形式指示（標準SEARCH/REPLACE形式）を含む")]
    public void 修正依頼テンプレートの形式指示を含む()
    {
        var prompt = PromptTemplateStore.BuildSelectionFixRequestPrompt(
            "a.cs", 1, 1, "a();", ".cs");

        // v1.0.13で既定テンプレートを標準SR形式へ切り替えたのに合わせ、この選択範囲からの
        // 依頼も同じ形式指示を共用する（1つのGraftの中でAIへ2種類の形式を要求しないため）。
        prompt.Should().Contain("<<<<<<< SEARCH", "既定の「修正依頼」テンプレートの形式指示を先頭に含める必要がある");
        prompt.Should().Contain(">>>>>>> REPLACE", "既定の「修正依頼」テンプレートの形式指示を含める必要がある");
        prompt.Should().Contain("SEARCH/REPLACE", "既定の「修正依頼」テンプレートの形式指示を含める必要がある");
        prompt.Should().NotContain("{{standingContext}}", "選択範囲からの依頼では前提・対象ファイルのプレースホルダは使わない");
        prompt.Should().NotContain("{{files}}", "選択範囲からの依頼では前提・対象ファイルのプレースホルダは使わない");
    }

    [Theory(DisplayName = "拡張子から言語名付きのコードフェンスを付ける")]
    [InlineData(".cs", "```csharp")]
    [InlineData("cs", "```csharp")]
    [InlineData(".py", "```python")]
    [InlineData(".ts", "```typescript")]
    [InlineData(".json", "```json")]
    [InlineData(".CS", "```csharp")]
    public void 拡張子から言語名付きフェンスを付ける(string extension, string expectedFenceOpener)
    {
        var prompt = PromptTemplateStore.BuildSelectionFixRequestPrompt("a", 1, 1, "code", extension);

        prompt.Should().Contain(expectedFenceOpener);
    }

    [Fact(DisplayName = "未対応の拡張子は言語名なしのフェンスにする")]
    public void 未対応拡張子は言語名なしフェンスにする()
    {
        var prompt = PromptTemplateStore.BuildSelectionFixRequestPrompt("a.unknown", 1, 1, "code", ".unknown");

        prompt.Should().Contain("```\ncode");
    }

    [Fact(DisplayName = "選択コードの末尾に改行が無ければ補って閉じフェンスを次の行に置く")]
    public void 選択コードの末尾に改行を補う()
    {
        var prompt = PromptTemplateStore.BuildSelectionFixRequestPrompt("a.cs", 1, 1, "var x = 1;", ".cs");

        prompt.Should().Contain("var x = 1;\n```");
    }
}
