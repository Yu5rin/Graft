using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Graft.Views;

/// <summary>
/// メニュー項目の右側に出るキーの表記を、指定した文字に差し替える添付プロパティ。
///
/// 【なぜ必要か】 <see cref="MenuItem.InputGesture"/> の表示は Avalonia が <c>KeyGesture.ToString()</c> で
/// 作るため、Enter キーが「Return」と出る（Avalonia のキー名は <c>Key.Return</c>）。日本語の Windows の
/// 利用者が見慣れているのは「Enter」で、メニューの右側だけ「Return」だと、同じ画面のほかの説明
/// （「Enter キーで開く」）と食い違う。InputGesture 自体は意味（どのキーか）のために残し、見た目の文字だけを
/// 差し替える。ほかのキー（F2・Delete・Ctrl+Z など）は Avalonia の表記のままで食い違わないため使わない。
///
/// 差し替え先はテンプレートの <c>PART_InputGestureText</c>（Fluent テーマのメニュー項目の部品名）。
/// メニューの中身は開くまでテンプレートが作られないため、<see cref="TemplatedControl.TemplateApplied"/> で待つ。
/// </summary>
public static class MenuGestureLabel
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<MenuItem, string?>("Text", typeof(MenuGestureLabel));

    static MenuGestureLabel()
    {
        TextProperty.Changed.AddClassHandler<MenuItem>((item, _) =>
        {
            item.TemplateApplied -= OnTemplateApplied;
            item.TemplateApplied += OnTemplateApplied;
        });
    }

    public static string? GetText(MenuItem item) => item.GetValue(TextProperty);

    public static void SetText(MenuItem item, string? value) => item.SetValue(TextProperty, value);

    private static void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (sender is not MenuItem item || GetText(item) is not { } text) return;
        if (e.NameScope.Find<TextBlock>("PART_InputGestureText") is { } block) block.Text = text;
    }
}
