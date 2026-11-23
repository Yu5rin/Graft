using Avalonia.Controls;
using Avalonia.Threading;
using Graft.Views;

namespace Graft.UiTests.TestSupport;

/// <summary>コンテキスト収集の窓（<see cref="ContextCollectWindow"/>）を操作するテスト用の補助。</summary>
public static class CollectWindowTestExtensions
{
    /// <summary>
    /// 「プレビュー」タブを選ぶ。タブの番号ではなく見出しで選ぶ: VBA プロジェクトで窓が狭いときは、
    /// 「ファイル選択」と「プレビュー」の間に「ブックの情報」タブが入り、番号が変わるため
    /// （広い窓では、そのタブは非表示のまま番号だけを占める）。
    /// </summary>
    public static void SelectPreviewTab(this ContextCollectWindow window)
    {
        var tabs = window.FindControl<TabControl>("MainTabs")!;
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, "プレビュー"));
        Dispatcher.UIThread.RunJobs();
    }
}
