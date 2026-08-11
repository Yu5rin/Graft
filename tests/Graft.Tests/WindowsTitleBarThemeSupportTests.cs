using FluentAssertions;
using Graft.Platform.Windows;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 利用者からの要望: タイトルバー色のテーマ連動。DWM呼び出し自体はWindows 11実機以外では
/// 検証できないため（ForegroundActivationDecisionTests.csと同じ役割分担）、ここでは
/// <see cref="WindowsTitleBarThemeSupport"/>が持つ純粋関数（P/Invokeを一切呼ばない）のみを
/// 検証する。
///
/// 特にCOLORREF変換（<see cref="WindowsTitleBarThemeSupport.ToColorRef"/>）は、依頼書1章が
/// 名指しした「赤と青が入れ替わる典型的な不具合」の再発防止が目的のため、必ず非対称な色
/// （R・G・Bが互いに異なる値）で固定する。対称な色（グレー等）では入れ替わりバグがあっても
/// テストが気付けない。
/// </summary>
public class WindowsTitleBarThemeSupportTests
{
    // --- COLORREF変換（AvaloniaのColor ARGB → Win32のCOLORREF 0x00BBGGRR） ---

    [Fact(DisplayName = "#FF0000（赤）はCOLORREFで0x000000FFになる（赤と青が入れ替わっていない）")]
    public void 赤はCOLORREFで下位バイトに来る()
    {
        WindowsTitleBarThemeSupport.ToColorRef(r: 0xFF, g: 0x00, b: 0x00)
            .Should().Be(0x000000FFu, "COLORREFは0x00BBGGRRであり、赤(R)は下位バイトに来るはず");
    }

    [Fact(DisplayName = "#0000FF（青）はCOLORREFで0x00FF0000になる（赤と青が入れ替わっていない）")]
    public void 青はCOLORREFで上位バイトに来る()
    {
        WindowsTitleBarThemeSupport.ToColorRef(r: 0x00, g: 0x00, b: 0xFF)
            .Should().Be(0x00FF0000u, "COLORREFは0x00BBGGRRであり、青(B)は上位バイトに来るはず（ここが赤青入れ替わりバグの典型的な発生源）");
    }

    [Fact(DisplayName = "#123456（R・G・Bが全て異なる非対称な色）が正しくバイト順変換される")]
    public void 非対称な色で全バイト位置を同時に固定する()
    {
        // 元のColor: R=0x12, G=0x34, B=0x56。
        // 期待するCOLORREF（0x00BBGGRR）: 0x00 56 34 12。
        WindowsTitleBarThemeSupport.ToColorRef(r: 0x12, g: 0x34, b: 0x56)
            .Should().Be(0x00563412u, "R・G・Bのいずれか1バイトでも位置を間違えると一致しない、最も検出力の高いケース");
    }

    [Fact(DisplayName = "アルファは無視される（COLORREFに透過度は無い）")]
    public void アルファに相当する上位バイトは常に0()
    {
        // ToColorRefはA(アルファ)を受け取らない設計そのものが「アルファは無視する」契約を
        // 表しているため、ここでは戻り値が0x00RRGGBBの範囲（上位8bitが常に0）に収まることで、
        // COLORREFの最上位バイトが未定義値で汚染されないことを確認する。
        WindowsTitleBarThemeSupport.ToColorRef(r: 0xFF, g: 0xFF, b: 0xFF)
            .Should().Be(0x00FFFFFFu);
    }

    // --- Windows 11未満（ビルド22000未満）では適用しない ---

    [Theory(DisplayName = "ビルド22000以上（Windows 11以降）はタイトルバー配色APIに対応する")]
    [InlineData(22000)]
    [InlineData(22621)]
    [InlineData(26100)]
    public void ビルド22000以上は対応(int build)
    {
        WindowsTitleBarThemeSupport.IsSupportedBuild(build).Should().BeTrue();
    }

    [Theory(DisplayName = "ビルド21999以下（Windows 10以下）はタイトルバー配色APIに対応しない")]
    [InlineData(21999)]
    [InlineData(19045)] // Windows 10 22H2
    [InlineData(7601)]  // Windows 7 SP1
    public void ビルド21999以下は非対応(int build)
    {
        WindowsTitleBarThemeSupport.IsSupportedBuild(build).Should().BeFalse();
    }

    // --- 非Windowsでは何もしない・Windows11未満では適用しない（最終判定の統合テスト） ---

    [Fact(DisplayName = "Windows・ビルド22000以上のときだけ適用してよい")]
    public void Windowsかつビルド22000以上のみtrue()
    {
        WindowsTitleBarThemeSupport.ShouldApply(isWindows: true, buildNumber: 22000).Should().BeTrue();
    }

    [Fact(DisplayName = "非Windows（Linux等）ではビルド番号に関わらず適用しない")]
    public void 非Windowsは常にfalse()
    {
        // ビルド番号だけ見ればWindows 11条件を満たす値でも、isWindows=falseなら
        // 何もしない（依頼書4章「Linuxでは何もしない」）。
        WindowsTitleBarThemeSupport.ShouldApply(isWindows: false, buildNumber: 26100).Should().BeFalse();
    }

    [Fact(DisplayName = "Windowsでもビルド22000未満（Windows 10以下）では適用しない")]
    public void Windowsでもビルドが古ければfalse()
    {
        WindowsTitleBarThemeSupport.ShouldApply(isWindows: true, buildNumber: 19045).Should().BeFalse();
    }
}
