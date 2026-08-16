using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.Document;
using Graft.Editor;

namespace Graft.Views;

/// <summary>
/// 検討書「コード中のカラープレビュー」の統合部分。スウォッチの描画・検出そのものは
/// <see cref="ColorPreviewElementGenerator"/>（<see cref="EditorPane"/>コンストラクタで
/// <c>Editor.TextArea.TextView.ElementGenerators</c>へ登録済み）に任せ、本ファイルは
/// 「クリックされたらカラーピッカーを開き、選ばれた色をドキュメントへ書き込む」までを担う
/// （1ファイル400行上限のため<see cref="EditorPane"/>本体から分割。EditorPane.MarkdownPreview.cs
/// と同じ方針）。
/// </summary>
public partial class EditorPane
{
    /// <summary>設定<c>editor.colorPreviewInCode</c>の反映（<see cref="ApplyDocumentTab"/>から呼ぶ）。</summary>
    private void ApplyColorPreviewOption()
    {
        _colorPreview.SetEnabled(_viewModel?.ColorPreviewInCode ?? true);
    }

    /// <summary>
    /// スウォッチのクリック。カラーピッカーを開き、確定されたら1回の<see cref="TextDocument.
    /// Replace(int,int,string)"/>で書き込む（通常のCtrl+Zで1回分の変更として戻せる）。
    ///
    /// クリック時点の<see cref="TextDocument"/>を捕まえておく（<c>Editor.Document</c>を
    /// 適用時に読み直さない）。カラーピッカーはモーダルではない（Pane同様「色を選んでいる間も
    /// 下のコードが見える」ことを優先した設計、ColorPickerPopupクラスコメント参照）ため、
    /// 開いている間に利用者がタブを切り替える余地があり、その場合でも元のタブの文書へ
    /// 正しく書き込むため。
    /// </summary>
    private void OnColorSwatchClicked(object? sender, ColorSwatchClickedEventArgs e)
    {
        var document = Editor.Document;
        var match = e.Match;

        var popup = new Graft.Views.ColorPickerPopup();
        popup.Configure(ToAvaloniaColor(match.Color), match.HasAlpha);
        popup.ColorConfirmed += (_, newColor) => ApplyColorEdit(document, match, newColor);

        var owner = TopLevel.GetTopLevel(this) as Window;
        // ウィンドウのSizeToContentが確定してから位置決めする（開く前はまだ実サイズが0のため）。
        // 実機のXvfb起動で発覚: Show()直後はキーボードフォーカスがまだ元のウィンドウに残ることがあり、
        // その状態だとEscで閉じる操作が効かない。ウィンドウが実際に表示された後（Opened）に
        // 明示的にアクティブ化してフォーカスを移す。
        popup.Opened += (_, _) =>
        {
            PositionColorPickerNear(popup, e.ScreenPoint);
            popup.Activate();
            popup.Focus();
        };
        if (owner is not null) popup.Show(owner); else popup.Show();
    }

    /// <summary>スウォッチのクリック位置の少し下にパネルを出す（クリックした場所の近くに出すことで
    /// 「どのリテラルを編集しているか」が分かりやすくする。Paneのような「対象と重ならない」ことの
    /// 保証まではせず、ドラッグで動かせること自体で解決する設計、ColorPickerPopupクラスコメント参照）。
    /// 画面外にはみ出さないよう、その画面の作業領域でクランプする。</summary>
    private static void PositionColorPickerNear(Window popup, PixelPoint clickScreenPoint)
    {
        var x = clickScreenPoint.X;
        var y = clickScreenPoint.Y + 20;

        var screen = popup.Screens.ScreenFromPoint(clickScreenPoint) ?? popup.Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var width = (int)popup.Bounds.Width;
            var height = (int)popup.Bounds.Height;
            x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width));
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height));
        }
        popup.Position = new PixelPoint(x, y);
    }

    private static void ApplyColorEdit(TextDocument document, ColorLiteralMatch match, Avalonia.Media.Color newColor)
    {
        // 安全側の確認: クリックからカラーピッカーで色を選ぶまでの間に、対象の文字列そのものが
        // 編集されていないかを確かめてから書き込む（ずれた位置へ書き込んで無関係な文字を
        // 壊すことを避ける）。一致しなければ何もしない（利用者が別の編集をしたと判断し、
        // 黙って諦める。エラー表示までは要さない軽微な競合のため）。
        if (match.Start < 0 || match.Start + match.Length > document.TextLength) return;
        if (document.GetText(match.Start, match.Length) != match.RawText) return;

        var replacement = match.Format(new RgbaColor(newColor.A, newColor.R, newColor.G, newColor.B));
        document.Replace(match.Start, match.Length, replacement);
    }

    private static Avalonia.Media.Color ToAvaloniaColor(RgbaColor color)
        => Avalonia.Media.Color.FromArgb(color.A, color.R, color.G, color.B);
}
