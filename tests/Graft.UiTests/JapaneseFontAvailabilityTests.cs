using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Themes;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 依頼1（E705）の単体テスト。<see cref="JapaneseFontAvailability"/>のクラスコメント
/// （【判定方法をどう選んだか】参照）のとおり、Avalonia.Headlessでは<c>FontManager.
/// TryMatchCharacter</c>が常にfalseを返すため、既定の判定関数では「ある/無い」の両方を
/// 検証できない。そのためフェイクの<see cref="JapaneseFontAvailability.CharacterMatcher"/>を
/// 注入して判定ロジック自体（OR条件・例外時の安全側フォールバック）を検証する。
/// Avalonia.Media（FontManager）に依存するクラスのため、Avalonia非依存のtests/Graft.Tests
/// ではなくここ（tests/Graft.UiTests）に置く。
/// </summary>
public class JapaneseFontAvailabilityTests
{
    // 判定対象の3文字（クラスコメント【誤検知を避ける設計】参照）。
    private const int Hiragana = 'あ';
    private const int Katakana = 'ア';
    private const int Kanji = '日';

    [AvaloniaFact(DisplayName = "3文字のうちいずれか1つでも描画できればフォントありと判定する（OR条件）")]
    public void いずれか1文字でも描画できればありと判定する()
    {
        // 漢字「日」だけ描画できるフォントが見つかる状況を模す。
        JapaneseFontAvailability.HasJapaneseCapableFont(codepoint => codepoint == Kanji)
            .Should().BeTrue("ひらがな・カタカナが見つからなくても、漢字が1つでも描画できれば十分とみなす仕様のため");
    }

    [AvaloniaFact(DisplayName = "3文字すべて描画できなければフォントなしと判定する")]
    public void 全文字描画できなければなしと判定する()
    {
        JapaneseFontAvailability.HasJapaneseCapableFont(_ => false)
            .Should().BeFalse("あ・ア・日のいずれも描画できないなら日本語フォントは無いと判定する必要がある");
    }

    [AvaloniaFact(DisplayName = "判定対象は「あ」「ア」「日」の3文字ちょうどであり、それ以外は問い合わせない")]
    public void 判定対象は3文字ちょうどである()
    {
        var probed = new List<int>();
        JapaneseFontAvailability.HasJapaneseCapableFont(codepoint =>
        {
            probed.Add(codepoint);
            return false; // すべて失敗させ、3文字とも問い合わせさせる。
        });

        probed.Should().Equal(new[] { Hiragana, Katakana, Kanji },
            "ひらがな「あ」・カタカナ「ア」・常用漢字「日」の順に、過不足なく問い合わせる必要がある");
    }

    [AvaloniaFact(DisplayName = "最初の文字で見つかれば残りの文字は問い合わせない（早期終了）")]
    public void 最初の文字で見つかれば早期終了する()
    {
        var probed = new List<int>();
        JapaneseFontAvailability.HasJapaneseCapableFont(codepoint =>
        {
            probed.Add(codepoint);
            return true; // 最初の「あ」で即座に見つかったことにする。
        });

        probed.Should().Equal(new[] { Hiragana }, "1文字目で見つかった時点で残りを問い合わせる必要はない");
    }

    [AvaloniaFact(DisplayName = "判定関数が例外を投げた場合は安全側（フォントありとみなす）へ倒す")]
    public void 判定関数が例外を投げたら安全側へ倒す()
    {
        JapaneseFontAvailability.HasJapaneseCapableFont(_ => throw new System.InvalidOperationException("環境依存のフォントAPI障害を模す"))
            .Should().BeTrue("判定不能な場合は警告を誤って出さないよう、フォントがあるとみなす安全側へ倒す仕様のため");
    }

    [AvaloniaFact(DisplayName = "既定の判定関数（実際のFontManager経由）を呼び出しても例外にならない")]
    public void 既定の判定関数を呼び出しても例外にならない()
    {
        // Avalonia.Headlessでは常にfalseが返る想定（クラスコメント参照）だが、
        // ここではあくまで「例外にならず呼び出せること」だけを確認する
        // （実機同等の判定はXvfb経由の手動検証で別途確認済み。実装ノート参照）。
        var act = () => JapaneseFontAvailability.HasJapaneseCapableFont();
        act.Should().NotThrow();
    }
}
