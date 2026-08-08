using System.Globalization;
using FluentAssertions;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 不具合5対応（ShellWindow.axamlのコマンドバー）の中核となる
/// <see cref="Converters.ToolbarButtonsMaxWidth"/> の計算式そのものを検証する。
///
/// レイアウト（ShellWindowTests側）とは別に切り出すのは、ボタン列を包む
/// ScrollViewer.MaxWidthが「本当に横スクロールを要求する状態になるか」は
/// ワークフローボタン群の実際の描画幅（日本語ラベルのフォント計量）に依存し、
/// ヘッドレステスト環境と実機とでフォント計量が異なるため確実に再現できない
/// （実測で確認済み: ヘッドレス環境ではボタン列の自然幅が本コンバータの
/// 計算結果より小さく収まってしまい、横スクロール自体が発生しないことがある）。
/// 一方この計算式自体（ウィンドウ幅からComboBox・ショートカットボタンの実測幅と
/// 固定余白を差し引く）は入力値だけで決まる純粋な計算のため、フォント計量に
/// 左右されずに単体テストできる。
/// </summary>
public class ToolbarButtonsMaxWidthConverterTests
{
    [Fact(DisplayName = "不具合5: ウィンドウ幅からComboBoxとショートカットボタンの実測幅、固定余白36pxを差し引く")]
    public void ウィンドウ幅から実測幅と固定余白を差し引く()
    {
        var result = Converters.ToolbarButtonsMaxWidth.Convert(
            new object?[] { 960.0, 180.0, 28.0 }, typeof(double), null, CultureInfo.InvariantCulture);

        // 960 - 180（ComboBox実測幅） - 28（ショートカットボタン実測幅） - 36（固定余白） = 716
        result.Should().Be(716.0);
    }

    [Fact(DisplayName = "不具合5: ComboBoxの選択項目名が長く実測幅が広がるほど、ボタン列に残る幅は狭くなる")]
    public void ComboBoxの実測幅が広いほどボタン列の最大幅は狭まる()
    {
        // NameItemTemplateのMaxWidth（不具合1対応）により、ComboBoxは長い名前でも
        // 際限なく広がらないが、それでも短い名前より実測幅は広くなる。
        var narrow = (double)Converters.ToolbarButtonsMaxWidth.Convert(
            new object?[] { 960.0, 180.0, 28.0 }, typeof(double), null, CultureInfo.InvariantCulture)!;
        var wide = (double)Converters.ToolbarButtonsMaxWidth.Convert(
            new object?[] { 960.0, 340.0, 28.0 }, typeof(double), null, CultureInfo.InvariantCulture)!;

        wide.Should().BeLessThan(narrow, "ComboBoxが実際に広く描画されるほど、ボタン列に残せる幅は減るべき");
    }

    [Fact(DisplayName = "不具合5: 差し引いた結果が負になる場合は0にクランプする（幅が壊れるより横スクロール領域が0になる方が安全）")]
    public void 差し引き結果が負になる場合は0にクランプする()
    {
        var result = Converters.ToolbarButtonsMaxWidth.Convert(
            new object?[] { 200.0, 180.0, 28.0 }, typeof(double), null, CultureInfo.InvariantCulture);

        result.Should().Be(0.0);
    }

    [Fact(DisplayName = "不具合5: レイアウト前などで実測幅が届いていない値（要素数不足）でも例外を投げない")]
    public void 実測幅が届いていなくても例外を投げない()
    {
        var result = Converters.ToolbarButtonsMaxWidth.Convert(
            new object?[] { 960.0 }, typeof(double), null, CultureInfo.InvariantCulture);

        // ComboBox・ショートカットボタンの実測幅が0扱いになり、固定余白36pxだけ差し引く。
        result.Should().Be(924.0);
    }
}
