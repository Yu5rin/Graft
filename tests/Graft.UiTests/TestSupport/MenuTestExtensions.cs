using Avalonia.Controls;
using Avalonia.VisualTree;
using Graft.Views;

namespace Graft.UiTests.TestSupport;

/// <summary>
/// ツールバー・右クリックのメニュー（標準の <see cref="MenuFlyout"/> / <see cref="ContextMenu"/>）を
/// ヘッドレスのテストから調べるための補助。ポップアップの中身は開くまで可視ツリーに入らないため、
/// メニューの定義（項目・コマンド・順序）を直接読む。
/// </summary>
public static class MenuTestExtensions
{
    /// <summary>ツールバーの「？」ボタンに付いたヘルプメニュー（MenuFlyout）の項目。</summary>
    public static IReadOnlyList<MenuItem> HelpMenuItems(this ShellWindow window)
        => FlyoutItems(window.GetControl<Button>("ShortcutsButton"));

    /// <summary>
    /// ボタンに付いた <see cref="MenuFlyout"/> の MenuItem（区切り線は含まない）。
    /// フライアウトの中身は開くまでDataContext（コマンドのバインド先）を受け取らないため、
    /// 開いてバインドを解決させてから読む。
    /// </summary>
    public static IReadOnlyList<MenuItem> FlyoutItems(Button button)
    {
        var flyout = button.Flyout as MenuFlyout
            ?? throw new InvalidOperationException($"「{button.Name}」には MenuFlyout が付いていない。");
        flyout.ShowAt(button);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // 閉じるとDataContext（コマンドのバインド先）が外れてCommandがnullへ戻るため、開いたままにする
        // （ウィンドウはテスト側が閉じる）。
        var items = flyout.Items.OfType<MenuItem>().ToList();
        return items;
    }

    /// <summary>メニュー項目を Header の文字で探す。</summary>
    public static MenuItem ItemByHeader(this IEnumerable<MenuItem> items, string header)
        => items.Single(i => Equals(i.Header, header));
}
