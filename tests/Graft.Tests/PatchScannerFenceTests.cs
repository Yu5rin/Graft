using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="PatchScanner.Create"/> の「パッチ全体を囲む外側のMarkdownコードフェンスだけを
/// 剥がす」挙動の単体テスト（v1.0.8での修正）。<see cref="PatchScanner"/> はinternalのため、
/// <see cref="PatchParser"/> 経由で確認する。附録A.7の方針に従い、テストデータは
/// Fixtures/Patches 配下のファイルから読み込む。
/// </summary>
public class PatchScannerFenceTests
{
    [Fact(DisplayName = "バッククォート4個で囲まれ本文に```を含む場合_フェンス行だけ剥がれ本文の```は残る")]
    public void バッククォート4個で囲まれ本文に含む場合_本文のバッククォートは残る()
    {
        var text = FixtureLoader.LoadPatch("fence_yon_honbun_backtick_zan");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("開始と同数以上のバッククォートでなければ閉じフェンスとして扱われないはず");
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Pairs.Should().HaveCount(1);
        block.Pairs[0].ReplaceText.Should().Be(
            "## 使い方\n\n```python\nprint(\"hello\")\n```",
            "本文中の```（3個）は開始フェンス（4個）を閉じないため、本文としてそのまま残るはず");
    }

    [Fact(DisplayName = "囲みが無い生のパッチは従来どおり解析できる")]
    public void 囲みが無い生のパッチは従来どおり解析できる()
    {
        var text = FixtureLoader.LoadPatch("fence_nashi_kakunin");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("コードフェンスを含まない生のパッチはそのまま解析できるはず");
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Pairs[0].SearchText.Should().Be("x = 1");
        block.Pairs[0].ReplaceText.Should().Be("y = 1");
    }

    [Fact(DisplayName = "バッククォート3個で囲まれ本文に```が無ければ従来どおり外側が剥がれる")]
    public void バッククォート3個で囲まれ本文に含まなければ外側が剥がれる()
    {
        var text = FixtureLoader.LoadPatch("fence_san_honbun_backtick_nashi");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.HeaderLine.Should().Be(6, "外側のフェンス行（開始・終了）を除いても元テキストの行番号がそのまま使われるはず");
        block.Pairs[0].ReplaceText.Should().Be("def new_name():\n    pass");
    }

    [Fact(DisplayName = "バッククォート3個で囲まれ本文にも```があると囲みが途中で閉じ解析に失敗する_E009")]
    public void バッククォート3個で囲まれ本文にもあると解析に失敗する()
    {
        var text = FixtureLoader.LoadPatch("fence_san_honbun_backtick_ari_e009");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse(
            "本文中の最初の```が開始フェンス（3個）と同数のため、外側の囲みを早まって閉じてしまうはず");
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E009);
        var issue = result.Issues.Single(i => i.Code == ErrorCode.E009);
        issue.LineNumber.Should().Be(12, "本文中の最初の```（閉じ扱いされてしまった行）の行番号のはず");
        issue.Detail.Should().Be(
            "パッチ本文に ``` が含まれているため、囲みが途中で閉じられた可能性があります。" +
            "AIへの指示文のとおり、外側をバッククォート4個（````text 〜 ````）にして出力し直してください。",
            "利用者が原因と対処（4個化）を理解できる案内文であるはず");
    }

    [Fact(DisplayName = "情報文字列つきの開始フェンス_diff でも外側だけが剥がれる")]
    public void 情報文字列つきの開始フェンスでも外側だけが剥がれる()
    {
        var text = FixtureLoader.LoadPatch("fence_jouhou_moji_diff");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("```diff のような情報文字列つきの開始フェンスでも剥がせるはず");
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<DeleteBlock>().Subject;
        block.Path.Should().Be("old/legacy.py");
    }

    [Fact(DisplayName = "パッチの前後に説明文と無関係なコード例がある場合でも実際のパッチだけ剥がれる")]
    public void 説明文と無関係なコード例がある場合でも実際のパッチだけ剥がれる()
    {
        var text = FixtureLoader.LoadPatch("fence_setsumei_bun_rei_atari");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("前後の説明文・無関係なコード例に影響されず解析できるはず");
        result.Value.Blocks.Should().HaveCount(1, "説明文中のコード例はブロックとして解釈されないはず");
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Path.Should().Be("src/greet.py");
        block.Pairs[0].ReplaceText.Should().Be("print(\"こんにちは\")");
    }
}
