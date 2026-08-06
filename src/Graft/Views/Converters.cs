using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Graft.Core;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// アプリ全体で共有する値コンバータ（仕様書v2.1 19章 L3）。v2.0のWPF版では各Viewの
/// コードビハインドに散っていたものを1ファイルへ集約した。
///
/// Avaloniaには<c>System.Windows.Visibility</c>に相当する3値の列挙が無く、表示制御は
/// <c>IsVisible</c>（bool）で行う。そのためv2.0のWPF版の「〜ToVisibility」系コンバータは
/// すべてboolを返す形に置き換わり、<c>BooleanToVisibilityConverter</c>のように
/// 変換自体が不要になったものは存在しない（バインドを直接<c>IsVisible</c>へ繋ぐ）。
/// </summary>
public static class Converters
{
    /// <summary>真偽値を反転する。</summary>
    public static readonly IValueConverter InverseBoolean =
        new FuncValueConverter<bool, bool>(value => !value);

    /// <summary>nullなら false、非nullなら true。<c>IsVisible</c>へのバインドに使う。</summary>
    public static readonly IValueConverter NotNull =
        new FuncValueConverter<object?, bool>(value => value is not null);

    /// <summary>空文字・nullなら false、内容があれば true。</summary>
    public static readonly IValueConverter NotEmptyString =
        new FuncValueConverter<string?, bool>(value => !string.IsNullOrEmpty(value));

    /// <summary>コレクションに1件以上あれば true。</summary>
    public static readonly IValueConverter HasItems =
        new FuncValueConverter<ICollection?, bool>(value => value is { Count: > 0 });

    /// <summary>コレクションが0件なら true（空状態表示用）。</summary>
    public static readonly IValueConverter IsEmptyCollection =
        new FuncValueConverter<ICollection?, bool>(value => value is null or { Count: 0 });

    /// <summary>件数（int）が1件以上なら true。</summary>
    public static readonly IValueConverter CountIsNotZero =
        new FuncValueConverter<int, bool>(count => count != 0);

    /// <summary>件数（int）が0件なら true。</summary>
    public static readonly IValueConverter CountIsZero =
        new FuncValueConverter<int, bool>(count => count == 0);

    /// <summary>
    /// <see cref="GraftIssue"/>を「コード＋内容（対処: 対処方法）」の1行表示へ変換する（8.8章）。
    /// </summary>
    public static readonly IValueConverter IssueToText =
        new FuncValueConverter<GraftIssue?, string>(
            issue => issue is null ? string.Empty : $"{issue.ToDisplayText()}（対処: {issue.Remedy}）");

    /// <summary>ファイルツリーの階層深さ（int）を左インデントの<see cref="Thickness"/>へ変換する。</summary>
    public static readonly IValueConverter IndentToMargin =
        new FuncValueConverter<int, Thickness>(level => new Thickness(level * 16.0, 2, 0, 2));

    /// <summary>trueなら残り幅いっぱい（1*）、falseなら0。差分ビューの片側折りたたみ用。</summary>
    public static readonly IValueConverter BoolToStarGridLength =
        new FuncValueConverter<bool, GridLength>(
            value => value ? new GridLength(1, GridUnitType.Star) : new GridLength(0));

    /// <summary>boolを<see cref="TextWrapping"/>へ変換する（8.13の折り返しトグル用）。</summary>
    public static readonly IValueConverter BoolToTextWrapping =
        new FuncValueConverter<bool, TextWrapping>(
            value => value ? TextWrapping.Wrap : TextWrapping.NoWrap);

    /// <summary><see cref="DateTimeOffset"/>とDatePickerの<see cref="DateTime"/>を相互変換する。</summary>
    public static readonly IValueConverter DateTimeOffsetToDate = new DateTimeOffsetConverter();

    /// <summary><see cref="CenterPaneState"/>を<see cref="EmptyStateMode"/>へ変換する。</summary>
    public static readonly IValueConverter CenterPaneStateToEmptyState =
        new FuncValueConverter<CenterPaneState, EmptyStateMode>(state => state switch
        {
            CenterPaneState.Loading => EmptyStateMode.Loading,
            CenterPaneState.Empty => EmptyStateMode.Empty,
            CenterPaneState.Error => EmptyStateMode.Error,
            _ => EmptyStateMode.None,
        });

    /// <summary><see cref="ProjectPaneState"/>を<see cref="EmptyStateMode"/>へ変換する。</summary>
    public static readonly IValueConverter ProjectPaneStateToEmptyState =
        new FuncValueConverter<ProjectPaneState, EmptyStateMode>(state => state switch
        {
            ProjectPaneState.Loading => EmptyStateMode.Loading,
            ProjectPaneState.Empty => EmptyStateMode.Empty,
            ProjectPaneState.Error => EmptyStateMode.Error,
            _ => EmptyStateMode.None,
        });

    /// <summary><see cref="HistoryPaneState"/>を<see cref="EmptyStateMode"/>へ変換する。</summary>
    public static readonly IValueConverter HistoryPaneStateToEmptyState =
        new FuncValueConverter<HistoryPaneState, EmptyStateMode>(state => state switch
        {
            HistoryPaneState.Loading => EmptyStateMode.Loading,
            HistoryPaneState.Empty => EmptyStateMode.Empty,
            HistoryPaneState.Error => EmptyStateMode.Error,
            _ => EmptyStateMode.None,
        });

    /// <summary>エクスプローラにノードがあれば通常表示、無ければ空状態にする。</summary>
    public static readonly IValueConverter HasNodesToEmptyState =
        new FuncValueConverter<bool, EmptyStateMode>(
            hasNodes => hasNodes ? EmptyStateMode.None : EmptyStateMode.Empty);

    /// <summary>キューが空なら空状態、そうでなければ通常表示にする。</summary>
    public static readonly IValueConverter IsEmptyToEmptyState =
        new FuncValueConverter<bool, EmptyStateMode>(
            isEmpty => isEmpty ? EmptyStateMode.Empty : EmptyStateMode.None);

    /// <summary>
    /// 現在のサイドビュー種別が<c>ConverterParameter</c>で指定した種別と一致するかを返す。
    /// サイドビューの切り替え（9.2）で、表示対象のビューだけを<c>IsVisible</c>にするために使う。
    /// </summary>
    public static readonly IValueConverter SideViewIs = new SideViewKindMatchConverter();

    /// <summary>
    /// チェックボックスの活性状態を「除外されていない」かつ「収集モードがファイル選択を伴う」の
    /// 両方を満たす場合のみtrueにする（ツリーのみモードではチェックしても意味が無いため）。
    /// </summary>
    public static readonly IMultiValueConverter FileCheckEnabled =
        new FuncMultiValueConverter<bool, bool>(values =>
        {
            var list = values.ToList();
            var isExcluded = list.Count > 0 && list[0];
            var showFileTree = list.Count > 1 && list[1];
            return !isExcluded && showFileTree;
        });

    /// <summary>
    /// ノードの除外状態と「除外ファイルを表示」トグルから表示可否を決める
    /// （4.2「除外中のノードはグレー表示」「除外ファイルを表示トグル」）。
    /// </summary>
    public static readonly IMultiValueConverter ExcludedNodeVisible =
        new FuncMultiValueConverter<bool, bool>(values =>
        {
            var list = values.ToList();
            var isExcluded = list.Count > 0 && list[0];
            var showExcluded = list.Count > 1 && list[1];
            return !isExcluded || showExcluded;
        });
}

/// <summary>
/// <see cref="DateTimeOffset"/>とDatePickerが扱う<see cref="DateTime"/>の相互変換。
/// 逆変換が必要なため<see cref="FuncValueConverter{TIn,TOut}"/>では表現できず、クラスにしている。
/// </summary>
public sealed class DateTimeOffsetConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTimeOffset offset ? offset.LocalDateTime.Date : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime date ? new DateTimeOffset(date) : null;
}

/// <summary>
/// <c>ConverterParameter</c>に渡された<see cref="SideViewKind"/>名と一致するかを判定する。
/// パラメータを使うため<see cref="FuncValueConverter{TIn,TOut}"/>では表現できない。
/// </summary>
public sealed class SideViewKindMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SideViewKind kind
            && parameter is string target
            && Enum.TryParse<SideViewKind>(target, out var wanted)
            && kind == wanted;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("表示専用の変換のため逆変換はサポートしない。");
}
