using Avalonia;
using Avalonia.Controls;

namespace Graft.Views;

/// <summary>
/// 絞り込み・検索の入力欄に付ける目印。Esc を押したとき、文字が入っていれば欄を空にする
/// （ShellWindow.Keyboard.cs の <c>TryClearFilterOnEscape</c> が読む）。
///
/// 【なぜ目印方式か】 Esc は「いちばん手前のものを1つ閉じる」に統一した（解析結果の破棄には使わない）。
/// 入力欄の Esc は、絞り込み欄では「文字を消す」が自然だが、エクスプローラの名前変更欄や
/// 設定の入力欄では「編集をやめる」「何もしない」など、欄ごとに別の意味を持つ。TextBox 全般で
/// 文字を消してしまうとそれらの Esc を横取りして壊すため、消してよい欄にだけこの目印を付ける。
/// </summary>
public static class EscapeClearBehavior
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Enabled", typeof(EscapeClearBehavior));

    public static bool GetEnabled(TextBox element) => element.GetValue(EnabledProperty);

    public static void SetEnabled(TextBox element, bool value) => element.SetValue(EnabledProperty, value);
}
