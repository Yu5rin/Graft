using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// ブックの情報の「シートごとの見せ方」の入力欄（見出しに続く行数・読む範囲）で、<b>打っている途中の文字を確定する契機</b>を
/// 画面に結ぶ添付ビヘイビア。
/// <para>
/// 【LostFocus だけに頼らない】 入力欄の文字は 1 文字ごとに行の下書き（<see cref="VbaSheetViewRowViewModel.RangeDraft"/> など）へ
/// 書き込まれる。確定（検証・保存・取り直し）は、ここで<b>フォーカスを外したとき</b>と<b>Enter</b>に行うほか、コピー・
/// ファイル保存・窓を閉じる直前にも持ち主側が行う（<see cref="ContextCollectViewModel.CommitPendingInputsAsync"/>）。
/// 以前は <c>UpdateSourceTrigger=LostFocus</c> の束縛だけが確定の契機だったので、入力した直後にショートカットでコピーする・
/// 窓を閉じると、確定が間に合わず、画面に見えている入力と違う内容が出力されうる、または入力が保存されないまま消えた。
/// </para>
/// </summary>
public static class SheetViewDraftBehavior
{
    /// <summary>true を設定した TextBox（<see cref="VbaSheetViewRowViewModel"/> を DataContext に持つ行の入力欄）で、確定の契機を結ぶ。</summary>
    public static readonly AttachedProperty<bool> CommitDraftsProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("CommitDrafts", typeof(SheetViewDraftBehavior));

    public static bool GetCommitDrafts(TextBox textBox) => textBox.GetValue(CommitDraftsProperty);

    public static void SetCommitDrafts(TextBox textBox, bool value) => textBox.SetValue(CommitDraftsProperty, value);

    static SheetViewDraftBehavior()
    {
        CommitDraftsProperty.Changed.AddClassHandler<TextBox>(OnChanged);
    }

    private static void OnChanged(TextBox textBox, AvaloniaPropertyChangedEventArgs e)
    {
        // 二重登録を避けるため、一度外してから必要なときだけ付け直す（TextBoxCommitBehavior と同じ作法）。
        textBox.LostFocus -= OnLostFocus;
        textBox.KeyDown -= OnKeyDown;
        if (e.NewValue is true)
        {
            textBox.LostFocus += OnLostFocus;
            textBox.KeyDown += OnKeyDown;
        }
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e) => Commit(sender);

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Commit(sender);
        e.Handled = true;
    }

    private static void Commit(object? sender)
    {
        if (sender is TextBox { DataContext: VbaSheetViewRowViewModel row })
        {
            _ = SafeHandler.RunAsync("シートの見せ方の入力の確定", row.CommitDraftsAsync);
        }
    }
}
