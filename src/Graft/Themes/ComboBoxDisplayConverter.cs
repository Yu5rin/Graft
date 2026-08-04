using System.Globalization;
using System.Windows.Data;

namespace Graft.Themes;

/// <summary>
/// ComboBox の「閉じた状態のボックス」に表示する文字列を求める。
/// WPF はカスタム ControlTemplate を与えた ComboBox で、開いているときの一覧（Popup内）は
/// DisplayMemberPath どおりに表示できるが、閉じた状態の表示（SelectionBoxItem /
/// SelectionBoxItemTemplate）には DisplayMemberPath が反映されないことがあり、
/// その場合は選択中オブジェクトの既定の ToString()（クラスの完全修飾名）がそのまま
/// 表示されてしまう。これを避けるため、DisplayMemberPath を自前で反映する。
/// </summary>
public sealed class ComboBoxDisplayConverter : IMultiValueConverter
{
    /// <summary>values[0]=選択中の項目（SelectionBoxItem）、values[1]=ComboBox.DisplayMemberPath。</summary>
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is null)
        {
            return string.Empty;
        }

        var item = values[0]!;
        var path = values[1] as string;
        if (string.IsNullOrEmpty(path))
        {
            // DisplayMemberPath が未指定の ComboBox（文字列を直接並べる等）はそのまま表示する。
            return item;
        }

        var property = item.GetType().GetProperty(path);
        return property?.GetValue(item) ?? item;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("表示専用の変換のため逆変換はサポートしない。");
}
