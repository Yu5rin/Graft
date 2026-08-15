using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using FluentAssertions;
using Graft.Editor;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 検討書「フォント設定」。<see cref="SystemFontCatalog"/>（Avalonia依存のフォント列挙・
/// 等幅判定の実装）の単体テスト。純粋な幅比較ロジック（<see cref="MonospaceHeuristic"/>）は
/// tests/Graft.Tests側で検証済み（そちらはAvalonia非依存の数値計算のみ）。ここでは
/// Avaloniaの<see cref="FontFamily"/>・<see cref="FontManager"/>に実際に触れる部分を扱う。
/// </summary>
public class FontCatalogTests
{
    [AvaloniaFact(DisplayName = "フォント列挙に失敗しても例外を投げず、空リストへフォールバックする")]
    public void 列挙失敗時は空リストへフォールバックする()
    {
        var catalog = new SystemFontCatalog(familyNameSource: () => throw new InvalidOperationException("列挙失敗を模擬"));

        var act = () => catalog.AllFamilyNames;

        act.Should().NotThrow("検討書の要件: フォントの列挙に失敗してもアプリが落ちないこと");
        catalog.AllFamilyNames.Should().BeEmpty();
        catalog.MonospaceFamilyNames.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "等幅判定に失敗した書体だけ等幅リストから除外し、全体は空にしない")]
    public void 等幅判定の失敗は該当書体だけ除外する()
    {
        var catalog = new SystemFontCatalog(
            familyNameSource: () => new[] { "FontA", "FontB", "FontC" },
            isMonospacePredicate: name => name switch
            {
                "FontA" => true,
                "FontB" => throw new InvalidOperationException("計測失敗を模擬"),
                _ => false,
            });

        catalog.AllFamilyNames.Should().BeEquivalentTo(new[] { "FontA", "FontB", "FontC" });
        catalog.MonospaceFamilyNames.Should().BeEquivalentTo(new[] { "FontA" },
            "計測に失敗したFontBは「等幅ではない」側へ倒れ、FontCはもともと非等幅");
    }

    [AvaloniaFact(DisplayName = "列挙結果は名前順に並び、重複は除去される")]
    public void 列挙結果は名前順で重複が無い()
    {
        var catalog = new SystemFontCatalog(
            familyNameSource: () => new[] { "Zebra", "Apple", "apple", "Mango" },
            isMonospacePredicate: _ => false);

        catalog.AllFamilyNames.Should().Equal("Apple", "Mango", "Zebra");
    }

    [AvaloniaFact(DisplayName = "空文字・空白のみのフォント名は列挙結果から除かれる")]
    public void 空のフォント名は除外される()
    {
        var catalog = new SystemFontCatalog(
            familyNameSource: () => new[] { "Valid", "", "   ", null! },
            isMonospacePredicate: _ => false);

        catalog.AllFamilyNames.Should().Equal("Valid");
    }

    [AvaloniaTheory(DisplayName = "フォント名に'や\\を含んでいても列挙・FontFamily構築のいずれも例外にならない")]
    [InlineData("O'Reilly")]
    [InlineData(@"Back\Slash")]
    [InlineData(@"O'Reilly\'s ""Mono""")]
    public void フォント名に引用符やバックスラッシュを含んでいても壊れない(string trickyName)
    {
        var catalog = new SystemFontCatalog(
            familyNameSource: () => new[] { trickyName },
            isMonospacePredicate: _ => true);

        catalog.AllFamilyNames.Should().Equal(trickyName);
        catalog.MonospaceFamilyNames.Should().Equal(trickyName);

        // AppFontManager/設定画面が実際に行うのと同じ経路（Avalonia.Media.FontFamilyの
        // コンストラクタへそのまま渡す）で壊れないことも確認する。PaneのようにCSS文字列を
        // 組み立てるわけではないため、'・\ のエスケープは不要（AppFontManager.csの
        // コメント参照）。
        var act = () => new FontFamily(trickyName);
        act.Should().NotThrow();
    }

    [AvaloniaFact(DisplayName = "既定のフォント列挙（FontManager.Current.SystemFonts経由）は例外を投げない")]
    public void 既定の列挙元は例外を投げない()
    {
        // ヘッドレステスト環境にインストールされているフォントの内容までは断定できないが、
        // 少なくとも列挙処理自体が例外で落ちないこと（検討書の要件）はどの環境でも確認できる。
        var catalog = new SystemFontCatalog();

        var act = () => { _ = catalog.AllFamilyNames; _ = catalog.MonospaceFamilyNames; };

        act.Should().NotThrow();
    }

    [AvaloniaFact(DisplayName = "EmptyFontCatalogは常に空を返す（未対応環境向けのNull Object）")]
    public void EmptyFontCatalogは常に空()
    {
        IFontCatalog catalog = new EmptyFontCatalog();

        catalog.AllFamilyNames.Should().BeEmpty();
        catalog.MonospaceFamilyNames.Should().BeEmpty();
    }
}
