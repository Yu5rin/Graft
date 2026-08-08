using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace Graft.Views;

/// <summary>
/// 14章 設定画面の即時反映方式のうち、数値・テキスト入力（TextBox）の確定タイミングを
/// 「フォーカスを外したとき、またはEnterキー」にするための添付ビヘイビア。
///
/// フォーカスを外したときの確定は、XAML側でバインディングへ明示的に
/// <c>UpdateSourceTrigger=LostFocus</c>を指定するだけでAvaloniaが面倒を見てくれる
/// （既定値は<c>PropertyChanged</c>で1文字ごとに確定してしまうため、これを明示するのが
/// 必須。「100」を「50」に打ち替える途中で「10」や空文字が確定してしまう不具合を防ぐ）。
/// 一方Enterキーは既定ではフォーカス移動もバインディング確定も起こさないため、
/// 個別のハンドリングが要る。Avaloniaの<see cref="BindingOperations.GetBindingExpressionBase"/>
/// が返す<see cref="Avalonia.Data.BindingExpressionBase"/>には、UpdateSourceTriggerの設定に
/// 関わらず今の表示値をバインディングソース（ViewModelのプロパティ）へ即座に送る
/// <c>UpdateSource()</c>がある（WPFの<c>BindingExpression.UpdateSource()</c>と同じ役割）。
/// これを使えばフォーカスを奪う・逃がすといった副作用のあるトリックを使わずに済む。
///
/// <c>AcceptsReturn=True</c>の複数行TextBox（許可する拡張子欄・JSON直接編集タブ）には
/// 意図的に適用しない。それらの欄ではEnterは改行そのものの入力手段であり、ここで
/// 割り込んで確定に使ってしまうと複数行の入力ができなくなるため。
/// </summary>
public static class TextBoxCommitBehavior
{
    /// <summary>trueを設定したTextBoxで、Enterキー押下時に現在のTextの値をバインディング
    /// ソースへ即座に反映する（AcceptsReturn=falseの単一行TextBox専用）。</summary>
    public static readonly AttachedProperty<bool> CommitOnEnterProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("CommitOnEnter", typeof(TextBoxCommitBehavior));

    public static bool GetCommitOnEnter(TextBox textBox) => textBox.GetValue(CommitOnEnterProperty);

    public static void SetCommitOnEnter(TextBox textBox, bool value) => textBox.SetValue(CommitOnEnterProperty, value);

    static TextBoxCommitBehavior()
    {
        CommitOnEnterProperty.Changed.AddClassHandler<TextBox>(OnCommitOnEnterChanged);
    }

    private static void OnCommitOnEnterChanged(TextBox textBox, AvaloniaPropertyChangedEventArgs e)
    {
        // XAMLの再適用やスタイルの再評価で複数回Changedが飛んできても二重登録しないよう、
        // 一旦外してから必要な場合のみ付け直す。
        textBox.KeyDown -= OnKeyDown;
        if (e.NewValue is true)
        {
            textBox.KeyDown += OnKeyDown;
        }
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox textBox) return;

        // UpdateSourceTrigger=LostFocusのままでも、明示的にUpdateSource()を呼べば
        // フォーカスを外さずに今の入力内容を確定できる。値の検証・保存の実行は
        // SettingsViewModel側のプロパティsetterが担うため、ここでは「確定を発生させる」
        // ことだけに責務を絞る。
        BindingOperations.GetBindingExpressionBase(textBox, TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
