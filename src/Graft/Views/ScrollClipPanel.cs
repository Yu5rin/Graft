using Avalonia;
using Avalonia.Controls;

namespace Graft.Views;

/// <summary>
/// 子を「幅の制約なし」で測り、自分の枠でクリップするパネル。差分ビューの本文（<see cref="CodeLineControl"/>）を
/// 横へずらして（<see cref="CodeLineControl.ScrollOffsetX"/>）見せるために使う（監査 H1）。
///
/// なぜ専用のパネルが要るか: 通常のレイアウトでは、子の DesiredSize は測るときの幅の制約で頭打ちになる。
/// TextBlock はその幅で文字を描く範囲も打ち切るため、枠の幅（例: 半分に分けた列の幅）より長い行は、
/// 横へずらしても「枠の幅ぶんの先頭部分」しか描かれず、続きが空白になる（実機で確認）。
/// ここで子を無制限の幅で測り、全長を描かせたうえで、枠を超える部分をクリップで隠す。
/// 折り返す設定（TextWrapping が NoWrap でない）の子は、折り返しの基準が要るので通常どおり枠の幅で測る。
/// 自分の幅は制約の幅（子の全長ではない）を返すので、並べている列の幅を押し広げない。
/// </summary>
public sealed class ScrollClipPanel : Panel
{
    public ScrollClipPanel()
    {
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        foreach (var child in Children)
        {
            var unbounded = child is TextBlock { TextWrapping: Avalonia.Media.TextWrapping.NoWrap };
            child.Measure(unbounded ? availableSize.WithWidth(double.PositiveInfinity) : availableSize);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            var unbounded = child is TextBlock { TextWrapping: Avalonia.Media.TextWrapping.NoWrap };
            var width = unbounded ? Math.Max(finalSize.Width, child.DesiredSize.Width) : finalSize.Width;
            child.Arrange(new Rect(0, 0, width, Math.Max(finalSize.Height, child.DesiredSize.Height)));
        }
        return finalSize;
    }
}
