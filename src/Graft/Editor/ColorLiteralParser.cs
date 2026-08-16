using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Graft.Editor;

/// <summary>
/// ARGB色1個ぶんの値。<see cref="Avalonia.Media.Color"/>と同じ形（A/R/G/B各byte）を持つが、
/// <see cref="ColorLiteralParser"/>・<see cref="ColorLiteralMatch"/>をAvalonia非依存の純粋ロジックの
/// まま保つために独自定義する（<c>tests/Graft.Tests</c>はAvalonia本体を参照しないプロジェクトの
/// ため、AvaloniaEdit/Avalonia依存のクラスは取り込めない。他の純粋ロジック
/// （<see cref="IndentGuideCalculator"/>等）と同じ方針）。UI側（<see cref="ColorPreviewElementGenerator"/>・
/// <c>Views/ColorPickerPopup.axaml.cs</c>）で<see cref="Avalonia.Media.Color"/>との間を変換する。
/// </summary>
public readonly record struct RgbaColor(byte A, byte R, byte G, byte B)
{
    public static RgbaColor FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);
}

/// <summary>コード中の色リテラルの記法種別。</summary>
public enum ColorNotationKind
{
    /// <summary><c>#RGB</c> / <c>#RGBA</c> / <c>#RRGGBB</c> / <c>#RRGGBBAA</c>。</summary>
    Hex,

    /// <summary><c>rgb(...)</c> / <c>rgba(...)</c>（カンマ記法・スペース記法の両方）。</summary>
    Rgb,

    /// <summary><c>hsl(...)</c> / <c>hsla(...)</c>（カンマ記法・スペース記法の両方）。</summary>
    Hsl,
}

/// <summary>
/// コード中に現れた1件の色リテラル。<see cref="Format"/>で新しい色に書き換えた文字列を得られる。
/// </summary>
public sealed record ColorLiteralMatch(int Start, string RawText, ColorNotationKind Kind, RgbaColor Color, bool HasAlpha)
{
    public int Length => RawText.Length;

    /// <summary>この位置のリテラルを<paramref name="newColor"/>に書き換えた文字列を返す。
    /// 記法（16進/rgb/hsl、桁数、区切り文字のスタイル等）は元のまま保つ
    /// （<see cref="ColorLiteralParser"/>クラスコメント参照）。</summary>
    public string Format(RgbaColor newColor) => ColorLiteralParser.Format(this, newColor);
}

/// <summary>
/// カラープレビュー機能（検討書「コード中のカラープレビュー」）: コード中の<c>#RRGGBB</c>・
/// <c>rgb()</c>・<c>hsl()</c>を検出し、色を書き換えるときも元の記法をそのまま保つための
/// 純粋ロジック。AvaloniaEdit・UIには一切依存しない（テスト容易性のため独立させた。
/// <see cref="ColorPreviewElementGenerator"/>から呼ばれる）。
///
/// 【記法を保つ方法 ― 数値部分だけを置換する】
/// 「色を書き換えたら記法が変わってしまう」不具合を避けるため、いったん元のリテラル文字列
/// （<see cref="ColorLiteralMatch.RawText"/>）を鋳型として使い、色を構成する数値部分の
/// 位置（正規表現の名前付きキャプチャの<c>Index</c>/<c>Length</c>）だけを新しい値へ
/// 差し替える。かっこ・カンマ・スペース・<c>%</c>・大文字小文字・<c>rgb</c>と<c>rgba</c>の
/// どちらだったか、といった数値以外の文字は一切さわらない。これにより
/// <c>rgb(255,0,0)</c>（カンマの後にスペースが無い記法）を選び直しても
/// <c>rgb(0,255,0)</c>のまま（<c>rgb(0, 255, 0)</c>にならない）になる。
///
/// 16進だけは数値部分＝#の後ろ全体が1つの塊なので、上記の「部分置換」ではなく、
/// 桁数（3/4/6/8）と大文字/小文字を元から読み取ってから丸ごと組み立て直す
/// （<see cref="FormatHex"/>）。3桁/4桁の短縮形は、新しい色が短縮形で正確に表現できる
/// 場合だけ維持し、できない場合のみ6桁/8桁へ広げる（検討書のPane仕様を踏襲）。
/// </summary>
public static class ColorLiteralParser
{
    // 直前が英数字・アンダースコアの場合は無視する（"abc#ff0000"のような識別子の一部への誤反応防止）。
    private static readonly Regex HexPattern = new(
        @"(?<!\w)#(?:(?<hex8>[0-9a-fA-F]{8})|(?<hex6>[0-9a-fA-F]{6})|(?<hex4>[0-9a-fA-F]{4})|(?<hex3>[0-9a-fA-F]{3}))(?![0-9a-fA-F])",
        RegexOptions.Compiled);

    // カンマ記法: rgb(255, 102, 0) / rgba(255,102,0,.8)
    private static readonly Regex RgbCommaPattern = new(
        @"\brgba?\(\s*(?<r>\d{1,3})\s*,\s*(?<g>\d{1,3})\s*,\s*(?<b>\d{1,3})\s*(?:,\s*(?<a>[0-9]*\.?[0-9]+%?))?\s*\)",
        RegexOptions.Compiled);

    // スペース記法（CSS Color Module Level 4）: rgb(255 102 0 / 80%)
    private static readonly Regex RgbSlashPattern = new(
        @"\brgba?\(\s*(?<r>\d{1,3})\s+(?<g>\d{1,3})\s+(?<b>\d{1,3})\s*(?:/\s*(?<a>[0-9]*\.?[0-9]+%?))?\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex HslCommaPattern = new(
        @"\bhsla?\(\s*(?<h>-?[0-9]*\.?[0-9]+)\s*,\s*(?<s>[0-9]*\.?[0-9]+)%\s*,\s*(?<l>[0-9]*\.?[0-9]+)%\s*(?:,\s*(?<a>[0-9]*\.?[0-9]+%?))?\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex HslSlashPattern = new(
        @"\bhsla?\(\s*(?<h>-?[0-9]*\.?[0-9]+)\s+(?<s>[0-9]*\.?[0-9]+)%\s+(?<l>[0-9]*\.?[0-9]+)%\s*(?:/\s*(?<a>[0-9]*\.?[0-9]+%?))?\s*\)",
        RegexOptions.Compiled);

    /// <summary>1行分のテキストから、その行内に現れるすべての色リテラルを検出する（開始位置は行内相対）。
    /// 呼び出し側（<see cref="ColorPreviewElementGenerator"/>）が可視行だけに絞って呼ぶことで、
    /// 全体を舐めない性能要件を満たす。</summary>
    public static IReadOnlyList<ColorLiteralMatch> FindAll(string lineText)
    {
        if (string.IsNullOrEmpty(lineText)) return Array.Empty<ColorLiteralMatch>();

        var results = new List<ColorLiteralMatch>();
        var used = new HashSet<int>();
        CollectHex(lineText, results, used);
        CollectFunctional(lineText, RgbCommaPattern, ColorNotationKind.Rgb, results, used);
        CollectFunctional(lineText, RgbSlashPattern, ColorNotationKind.Rgb, results, used);
        CollectFunctional(lineText, HslCommaPattern, ColorNotationKind.Hsl, results, used);
        CollectFunctional(lineText, HslSlashPattern, ColorNotationKind.Hsl, results, used);
        results.Sort((a, b) => a.Start.CompareTo(b.Start));
        return results;
    }

    private static void CollectHex(string text, List<ColorLiteralMatch> results, HashSet<int> used)
    {
        foreach (Match m in HexPattern.Matches(text))
        {
            if (!used.Add(m.Index)) continue;
            var digits = m.Value[1..];
            var hasAlpha = digits.Length is 4 or 8;
            RgbaColor color;
            if (digits.Length is 3 or 4)
            {
                var r = ExpandNibble(digits[0]);
                var g = ExpandNibble(digits[1]);
                var b = ExpandNibble(digits[2]);
                var a = hasAlpha ? ExpandNibble(digits[3]) : (byte)255;
                color = RgbaColor.FromArgb(a, r, g, b);
            }
            else
            {
                var r = Convert.ToByte(digits.Substring(0, 2), 16);
                var g = Convert.ToByte(digits.Substring(2, 2), 16);
                var b = Convert.ToByte(digits.Substring(4, 2), 16);
                var a = hasAlpha ? Convert.ToByte(digits.Substring(6, 2), 16) : (byte)255;
                color = RgbaColor.FromArgb(a, r, g, b);
            }
            results.Add(new ColorLiteralMatch(m.Index, m.Value, ColorNotationKind.Hex, color, hasAlpha));
        }
    }

    private static byte ExpandNibble(char c)
    {
        var v = Convert.ToByte(c.ToString(), 16);
        return (byte)(v * 17); // "f" -> 0xff（上位ニブル・下位ニブルとも同じ値になる16進の短縮規則）
    }

    private static void CollectFunctional(
        string text, Regex pattern, ColorNotationKind kind, List<ColorLiteralMatch> results, HashSet<int> used)
    {
        foreach (Match m in pattern.Matches(text))
        {
            if (!used.Add(m.Index)) continue;
            var color = kind == ColorNotationKind.Rgb ? ParseRgbMatch(m) : ParseHslMatch(m);
            if (color is null) continue;
            var hasAlpha = m.Groups["a"].Success;
            results.Add(new ColorLiteralMatch(m.Index, m.Value, kind, color.Value, hasAlpha));
        }
    }

    private static RgbaColor? ParseRgbMatch(Match m)
    {
        if (!TryParseByte(m.Groups["r"].Value, out var r)) return null;
        if (!TryParseByte(m.Groups["g"].Value, out var g)) return null;
        if (!TryParseByte(m.Groups["b"].Value, out var b)) return null;
        byte a = 255;
        if (m.Groups["a"].Success && !TryParseAlpha(m.Groups["a"].Value, out a)) return null;
        return RgbaColor.FromArgb(a, r, g, b);
    }

    private static RgbaColor? ParseHslMatch(Match m)
    {
        if (!double.TryParse(m.Groups["h"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return null;
        if (!double.TryParse(m.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return null;
        if (!double.TryParse(m.Groups["l"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var l)) return null;
        byte a = 255;
        if (m.Groups["a"].Success && !TryParseAlpha(m.Groups["a"].Value, out a)) return null;
        return HslToRgb(h, s, l, a);
    }

    private static bool TryParseByte(string s, out byte value)
    {
        value = 0;
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return false;
        if (v is < 0 or > 255) return false;
        value = (byte)v;
        return true;
    }

    private static bool TryParseAlpha(string s, out byte value)
    {
        value = 255;
        if (s.EndsWith('%'))
        {
            if (!double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return false;
            pct = Math.Clamp(pct, 0, 100);
            value = (byte)Math.Round(pct / 100.0 * 255);
            return true;
        }
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var frac)) return false;
        frac = Math.Clamp(frac, 0, 1);
        value = (byte)Math.Round(frac * 255);
        return true;
    }

    /// <summary>色を書き換えた新しいリテラル文字列を組み立てる（クラスコメント参照）。</summary>
    internal static string Format(ColorLiteralMatch match, RgbaColor newColor) => match.Kind switch
    {
        ColorNotationKind.Hex => FormatHex(match, newColor),
        ColorNotationKind.Rgb => FormatFunctional(match, newColor),
        ColorNotationKind.Hsl => FormatFunctional(match, newColor),
        _ => match.RawText,
    };

    private static string FormatHex(ColorLiteralMatch match, RgbaColor newColor)
    {
        var digits = match.RawText[1..];
        // 元の表記に大文字が1つでもあれば大文字で書き戻す（"#FF0000"→"#00FF00"の要件）。
        var upper = digits.Any(char.IsAsciiLetterUpper);
        var originalShort = digits.Length is 3 or 4;
        var hasAlpha = match.HasAlpha;
        var canShort = originalShort
            && IsNibbleExpandable(newColor.R) && IsNibbleExpandable(newColor.G) && IsNibbleExpandable(newColor.B)
            && (!hasAlpha || IsNibbleExpandable(newColor.A));

        string BytePart(byte v) => canShort ? (v & 0xF).ToString("x1") : v.ToString("x2");

        var body = BytePart(newColor.R) + BytePart(newColor.G) + BytePart(newColor.B)
            + (hasAlpha ? BytePart(newColor.A) : string.Empty);
        if (upper) body = body.ToUpperInvariant();
        return "#" + body;
    }

    private static bool IsNibbleExpandable(byte v) => (v >> 4) == (v & 0x0F);

    private static string FormatFunctional(ColorLiteralMatch match, RgbaColor newColor)
    {
        var isHsl = match.Kind == ColorNotationKind.Hsl;
        var commaPattern = isHsl ? HslCommaPattern : RgbCommaPattern;
        var slashPattern = isHsl ? HslSlashPattern : RgbSlashPattern;

        var m = commaPattern.Match(match.RawText);
        if (!IsFullMatch(m, match.RawText)) m = slashPattern.Match(match.RawText);
        if (!IsFullMatch(m, match.RawText)) return match.RawText; // 想定外の形（安全側でそのまま返す）

        var replacements = new List<(int Index, int Length, string Text)>();
        if (isHsl)
        {
            var (h, s, l) = RgbToHsl(newColor.R, newColor.G, newColor.B);
            AddReplacement(replacements, m.Groups["h"], FormatDegrees(h));
            AddReplacement(replacements, m.Groups["s"], FormatPercent(s));
            AddReplacement(replacements, m.Groups["l"], FormatPercent(l));
        }
        else
        {
            AddReplacement(replacements, m.Groups["r"], newColor.R.ToString(CultureInfo.InvariantCulture));
            AddReplacement(replacements, m.Groups["g"], newColor.G.ToString(CultureInfo.InvariantCulture));
            AddReplacement(replacements, m.Groups["b"], newColor.B.ToString(CultureInfo.InvariantCulture));
        }

        if (match.HasAlpha && m.Groups["a"].Success)
        {
            // キャプチャ済みの範囲(m.Groups["a"])は"%"記号自体を含む（正規表現側で%?をグループの
            // 内側に置いているため）。置換文字列側にも"%"を付けないと記号ごと消えてしまう。
            var isPercent = m.Groups["a"].Value.EndsWith('%');
            var alphaText = isPercent
                ? FormatPercent(newColor.A / 255.0 * 100) + "%"
                : FormatAlphaDecimal(newColor.A / 255.0);
            AddReplacement(replacements, m.Groups["a"], alphaText);
        }

        var sb = new StringBuilder(match.RawText);
        foreach (var (index, length, text) in replacements.OrderByDescending(r => r.Index))
        {
            sb.Remove(index, length);
            sb.Insert(index, text);
        }
        return sb.ToString();
    }

    private static bool IsFullMatch(Match m, string text) => m.Success && m.Index == 0 && m.Length == text.Length;

    private static void AddReplacement(List<(int, int, string)> list, Group g, string text)
    {
        if (g.Success) list.Add((g.Index, g.Length, text));
    }

    private static string FormatDegrees(double h) => ((((int)Math.Round(h) % 360) + 360) % 360).ToString(CultureInfo.InvariantCulture);

    private static string FormatPercent(double v) => ((int)Math.Round(Math.Clamp(v, 0, 100))).ToString(CultureInfo.InvariantCulture);

    private static string FormatAlphaDecimal(double value)
    {
        value = Math.Clamp(value, 0, 1);
        var text = Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
        return text.Length == 0 ? "0" : text;
    }

    // ---- RGB <-> HSL 変換（標準的な変換式。Avalonia.Media.HslColorを使わず自前実装にしたのは、
    //      本クラスをUIに依存しない純粋ロジックのまま保つため） ----

    internal static (double H, double S, double L) RgbToHsl(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        double h, s;
        var l = (max + min) / 2;
        if (max == min)
        {
            h = 0; s = 0;
        }
        else
        {
            var d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == rf) h = (gf - bf) / d + (gf < bf ? 6 : 0);
            else if (max == gf) h = (bf - rf) / d + 2;
            else h = (rf - gf) / d + 4;
            h *= 60;
        }
        return (h, s * 100, l * 100);
    }

    internal static RgbaColor HslToRgb(double h, double s, double l, byte a = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 100) / 100.0;
        l = Math.Clamp(l, 0, 100) / 100.0;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        double rf, gf, bf;
        if (h < 60) { rf = c; gf = x; bf = 0; }
        else if (h < 120) { rf = x; gf = c; bf = 0; }
        else if (h < 180) { rf = 0; gf = c; bf = x; }
        else if (h < 240) { rf = 0; gf = x; bf = c; }
        else if (h < 300) { rf = x; gf = 0; bf = c; }
        else { rf = c; gf = 0; bf = x; }
        var r = (byte)Math.Round((rf + m) * 255);
        var g = (byte)Math.Round((gf + m) * 255);
        var b = (byte)Math.Round((bf + m) * 255);
        return RgbaColor.FromArgb(a, r, g, b);
    }
}
