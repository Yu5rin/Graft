using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.Features;

namespace Graft.Views;

/// <summary>
/// キーボードショートカット一覧ウィンドウ。<see cref="ShellWindow"/>のキーボード操作
/// （ShellWindow.Keyboard.cs）に定義されているショートカットを機能分類ごとに一覧表示する
/// だけの静的な画面のため、ViewModelは持たない。開く手段はShellWindow.axamlの「?」ボタンと
/// Ctrl+/の2通り。v2.0のWPF版には存在しない新規画面（利用者からの指摘対応）。
///
/// 一覧の中身（キー表記・説明文）は<see cref="ShortcutCatalog"/>を唯一の情報源として
/// <see cref="BuildShortcutList"/>が組み立てる（コマンドパレットのキー表記表示と二重管理に
/// ならないための設計。ShortcutCatalog.csのクラスコメント参照）。
/// </summary>
public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        BuildShortcutList();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    /// <summary>
    /// ShortcutCatalog.Entriesを分類（Category）ごとにグループ化し、元のXAMLが持っていたのと
    /// 同じ視覚構造（見出しTextBlock→Border[shortcutGroup]→行ごとのGrid[shortcutRow]）を
    /// コードから組み立てて<see cref="ShortcutListPanel"/>へ追加する。スタイル（categoryHeading・
    /// shortcutGroup・shortcutRow・keyBadge・keyText・descText）はShortcutsWindow.axamlの
    /// Window.Stylesに定義済みのものをそのまま流用する。
    /// </summary>
    private void BuildShortcutList()
    {
        foreach (var group in ShortcutCatalog.Entries.GroupBy(e => e.Category))
        {
            ShortcutListPanel.Children.Add(new TextBlock { Classes = { "categoryHeading" }, Text = group.Key });

            var rows = new StackPanel();
            var entries = group.ToList();
            for (var i = 0; i < entries.Count; i++)
            {
                rows.Children.Add(BuildShortcutRow(entries[i], isLast: i == entries.Count - 1));
            }

            ShortcutListPanel.Children.Add(new Border { Classes = { "shortcutGroup" }, Child = rows });
        }
    }

    private static Grid BuildShortcutRow(ShortcutEntry entry, bool isLast)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
        row.Classes.Add("shortcutRow");
        if (isLast) row.Classes.Add("last");

        var badge = new Border { Classes = { "keyBadge" }, Child = new TextBlock { Classes = { "keyText" }, Text = entry.Gesture } };
        Grid.SetColumn(badge, 0);

        var desc = new TextBlock { Classes = { "descText" }, Text = entry.Description };
        Grid.SetColumn(desc, 1);

        row.Children.Add(badge);
        row.Children.Add(desc);
        return row;
    }
}
