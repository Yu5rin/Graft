using System.Globalization;
using Avalonia.Media;

namespace Graft.Themes;

/// <summary>
/// 仕様書2.5・17章（E705）。日本語グリフを表示できるフォントが環境に1つも無い場合を検出する。
///
/// 【判定方法をどう選んだか】
/// Avaloniaのフォント関連APIには主に2系統ある。
///   (a) <see cref="Typeface"/> を具体的なフォント名（2.5のフォールバック列に列挙した
///       "Noto Sans CJK JP" 等）で作り、<see cref="IGlyphTypeface.TryGetGlyph"/> で
///       特定の1フォントにグリフがあるかを見る方法。
///   (b) <see cref="FontManager.TryMatchCharacter"/> で「この文字を描画できるフォントが
///       システムにあるか」を、フォント名を問わず横断的に問い合わせる方法。
/// (a)は誤検知（誤って警告を出す側）のリスクが高いため採らなかった: 2.5のフォールバック列は
/// 代表的な製品名を列挙しているだけで、実際にインストールされているフォントの正式名は
/// ディストリビューション・パッケージによって微妙に揺れる（例: このリポジトリの開発コンテナには
/// 日本語フォントとして「IPAGothic」が入っているが、Tokens.axamlのフォールバック列は
/// 「IPAexGothic」）。(a)方式だと、実際には日本語を描画できる環境なのに、たまたま列挙した
/// 名前と厳密一致しないという理由だけで「日本語フォントが無い」と誤警告してしまう。
/// (b)のTryMatchCharacterはOSのフォントサブシステム（Windows: DirectWrite、Linux: SkiaSharp
/// 経由のfontconfig）へ「この文字を描けるフォントを探して」と問い合わせるAPIで、
/// フォント名の一致に依存しない。これは「文字化けを無言で放置しない」という仕様の意図
/// （2.5）に対して最も直接的な問い合わせ方法でもある: 知りたいのは「特定のフォント名が
/// あるか」ではなく「日本語という文字そのものを描画できるか」だからである。
/// 開発コンテナ上でXvfb経由の実機同等環境（.UsePlatformDetect()、Skia+X11）で実測したところ、
/// 「あ」「ア」「日」いずれもTryMatchCharacterがtrueを返し、WenQuanYi Zen Hei（CJK対応の
/// 中国語フォントだが、Unicode上は同じCJK統合漢字・仮名の範囲を含むため見つかる）が
/// マッチした。一方、Avalonia.Headless（Graft.UiTestsが使う画面なしのテスト用プラットフォーム）
/// では常にfalseを返すことも確認済みで、この判定を単体テストで直接検証すると
/// 実機に依存せず常に「フォント無し」という誤った結果になる。そのため本クラスは
/// 判定関数を差し替え可能にし（<see cref="CharacterMatcher"/>）、単体テストは
/// フェイクの判定関数で「ある場合」「無い場合」の両方を検証する
/// （JapaneseFontAvailabilityTests参照）。実際のアプリはヘッドレスではなく通常の
/// プラットフォーム（Win32/X11）で動くため、既定実装（<see cref="MatchViaFontManager"/>）を
/// そのまま使う。
///
/// 【誤検知を避ける設計】
/// 依頼書の指示（「CJKフォントが皆無の環境は稀」「判定に自信が持てない場合は警告を出さない
/// 安全側に倒す」）に従い、以下の2点で安全側に倒している。
///   - 判定文字を1つだけに絞らず、ひらがな「あ」・カタカナ「ア」・常用漢字「日」の3つを試し、
///     いずれか1つでも描画できるフォントが見つかれば「ある」とみなす（OR条件）。
///     日本語の文章はこの3種の文字体系を組み合わせて書かれるため、1種類でも欠けていれば
///     文字化けは実質避けられないが、逆にどれか1つでも描画できるなら「フォントが1つも無い」
///     という最も深刻な状態（仕様書2.5が警告対象とする状態）ではないと判断できる。
///   - 判定中に例外が飛んだ場合（環境依存のフォントAPI呼び出しのため何が起こるか
///     完全には予測できない）は、警告を出さない側（「ある」とみなす）へ倒す。
/// </summary>
public static class JapaneseFontAvailability
{
    // 判定に使う代表文字（Unicodeコードポイント）。クラスコメント【誤検知を避ける設計】参照。
    private static readonly int[] ProbeCodepoints = { 'あ', 'ア', '日' };

    /// <summary>
    /// 「指定コードポイントを描画できるフォントがあるか」を問い合わせる関数の型。
    /// 既定は<see cref="MatchViaFontManager"/>（実際のFontManager経由）。単体テストからは
    /// フェイクの実装を差し込める。
    /// </summary>
    public delegate bool CharacterMatcher(int codepoint);

    /// <summary>
    /// 日本語（ひらがな・カタカナ・漢字のいずれか）を描画できるフォントが
    /// システムに1つでもあるかどうかを判定する。
    /// </summary>
    /// <param name="matcher">
    /// 省略時は実際の<see cref="FontManager"/>を使う。単体テストからフェイクを渡すための引数。
    /// </param>
    public static bool HasJapaneseCapableFont(CharacterMatcher? matcher = null)
    {
        matcher ??= MatchViaFontManager;

        foreach (var codepoint in ProbeCodepoints)
        {
            bool found;
            try
            {
                found = matcher(codepoint);
            }
            catch (Exception)
            {
                // 判定不能＝安全側（警告を出さない＝フォントがあるとみなす）。
                // クラスコメント【誤検知を避ける設計】参照。フォントAPIの例外は
                // プラットフォーム・環境依存で網羅できないため種類を問わず吸収する。
                return true;
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchViaFontManager(int codepoint)
        => FontManager.Current.TryMatchCharacter(
            codepoint, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
            fontFamily: null, CultureInfo.GetCultureInfo("ja-JP"), out _);
}
