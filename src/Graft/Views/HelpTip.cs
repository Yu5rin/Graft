using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Graft.Views;

/// <summary>
/// 「操作の説明」（ツールチップ）の4段階表示レベル。設定画面「一般」タブ・テーマの
/// すぐ下にある「操作の説明」ドロップダウンで選ぶ。既定は<see cref="Standard"/>。
///
/// 【検討書「ツールチップの4段階化」で<see cref="Minimal"/>を追加】
/// 既存の3段階（Off/Standard/Detailed）は「その操作が何をするか」の説明の詳しさを
/// 変えるものだが、<see cref="Minimal"/>は性質が異なり、「今その設定に入っている値」
/// だけを一言で示す（例:「インデント幅: 4」）。値を持つ設定項目にだけ意味があるため、
/// 値を持たないボタン等では<see cref="Standard"/>と同じ文言にフォールバックする
/// （<see cref="MinimalProperty"/>のコメント・<see cref="SelectText"/>参照）。
/// </summary>
public enum TooltipDetailLevel
{
    /// <summary>表示しない。ツールチップそのものを一切出さない。</summary>
    Off,

    /// <summary>
    /// 最低限。値を持つ設定項目（インデント幅・フォントサイズ・バックアップ保持数など）は
    /// 「現在設定されている値だけ」を示す。値を持たないコントロール（ボタン等）は
    /// <see cref="Standard"/>と同じ文言になる。
    /// </summary>
    Minimal,

    /// <summary>標準の説明（既定）。1〜2文の説明＋ショートカット併記。</summary>
    Standard,

    /// <summary>
    /// くわしい説明。「何をするか」だけでなく「いつ使うか」「押すと次に何が起きるか」まで、
    /// 前提知識ゼロの人でも読める言葉で書く。
    /// </summary>
    Detailed,
}

/// <summary>
/// ツールチップの表示レベルを一括で切り替える添付プロパティ。
///
/// 【なぜ添付プロパティ方式か】
/// 既存のツールチップはすべて各コントロールに静的な文字列として
/// <c>ToolTip.Tip="…"</c>と書かれている。これを維持しつつ「標準／くわしい／表示しない」の
/// 3段階を切り替えるには、コントロールごとに「標準の説明」と「くわしい説明」の2つの文字列を
/// 持たせ、実際に表示される<c>ToolTip.Tip</c>への反映だけをこのクラスに一元化するのが
/// 最も既存コードへの影響が小さい。<see cref="StandardProperty"/>・<see cref="DetailedProperty"/>
/// という2つの添付プロパティをコントロールへ足すだけでよく（<c>AutomationProperties.Name</c>は
/// 一切変更しない）、ViewModelからDataContext経由で個々のボタンへ設定値を配線し直す必要も無い。
///
/// 【検討書「ツールチップの4段階化」で<see cref="MinimalProperty"/>を追加】
/// 上と同じ添付プロパティ方式のまま3つ目の<see cref="MinimalProperty"/>を足すだけで4段階化できた。
/// StandardProperty/DetailedPropertyとの違いは、多くの利用箇所で固定文字列ではなく
/// <c>{Binding ...}</c>を渡す点だけであり、AvaloniaPropertyである以上バインディングも
/// 静的な文字列もどちらも当然に扱える（属性の型を変える必要が無かった）。詳細は
/// <see cref="MinimalProperty"/>のドキュメント参照。
///
/// 現在の表示レベルは<see cref="Graft.Themes.ThemeManager"/>と同じ設計で、アプリ全体で1つの
/// 静的な状態（<see cref="CurrentLevel"/>）として持つ。設定画面（<c>SettingsViewModel</c>）の
/// 「操作の説明」ドロップダウンが変わった瞬間に<see cref="SetLevel"/>を呼び、
/// <see cref="LevelChanged"/>を発火させることで、既に開いている全ウィンドウのツールチップを
/// 再起動なしで一括更新する（テーマの即時反映と同じ仕組み）。
///
/// 【購読管理について】
/// 各コントロールがレベル変更を検知できるよう、静的イベントへ購読させる必要があるが、
/// コントロールごとに購読・解除のペアを持たせると、ウィンドウを閉じ忘れた場合に解除漏れが
/// 起きやすい。そこで<see cref="ConditionalWeakTable{TKey,TValue}"/>で「今ツールチップの
/// 対象になっているコントロール」を弱参照として覚えておき、<see cref="LevelChanged"/>発火時に
/// まとめて再適用する方式にした。イベント購読は<see cref="HelpTip"/>の静的コンストラクタで
/// 一度きりであり、コントロール側は購読・解除を一切気にしなくてよい。ウィンドウが閉じられて
/// コントロールが不要になれば、弱参照テーブルからも自然に外れる（GCを妨げない）。
/// </summary>
public static class HelpTip
{
    /// <summary>
    /// ツールチップの最大幅。「くわしい説明」は長文になるため、指定した幅で折り返し、
    /// 画面外へはみ出さないようにする（標準の説明はこの幅に収まる長さなので実質影響しない）。
    /// </summary>
    private const double MaxTipWidth = 360;

    private static readonly ConditionalWeakTable<Control, object?> TrackedControls = new();

    private static TooltipDetailLevel _currentLevel = TooltipDetailLevel.Standard;

    /// <summary>現在の表示レベル。</summary>
    public static TooltipDetailLevel CurrentLevel => _currentLevel;

    /// <summary>表示レベルが変わるたびに発火する。</summary>
    public static event EventHandler? LevelChanged;

    static HelpTip()
    {
        StandardProperty.Changed.AddClassHandler<Control>((c, _) => Track(c));
        DetailedProperty.Changed.AddClassHandler<Control>((c, _) => Track(c));
        MinimalProperty.Changed.AddClassHandler<Control>((c, _) => Track(c));

        // 1つの静的イベントハンドラで、購読中の全コントロールへ再適用する
        // （コントロール側で個別に購読・解除を管理しないための設計。クラス冒頭のコメント参照）。
        LevelChanged += (_, _) =>
        {
            foreach (var entry in TrackedControls)
            {
                Apply(entry.Key);
            }
        };
    }

    /// <summary>
    /// 表示レベルを変更し、購読している全コントロールのツールチップへ即時反映する。
    /// 設定画面が即時反映方式のため、保存を待たずにこの場で呼び出す
    /// （<see cref="Graft.ViewModels.SettingsViewModel.SelectedTooltipDetail"/>参照）。
    /// </summary>
    public static void SetLevel(TooltipDetailLevel level)
    {
        if (_currentLevel == level) return;
        _currentLevel = level;
        LevelChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>settings.json の <c>tooltipDetail</c>（"off" / "minimal" / "standard" / "detailed"）を
    /// 読み替える。未知の値（"minimal"追加前の値も含む）は標準として扱う。</summary>
    public static TooltipDetailLevel ParseLevel(string? value) => value switch
    {
        "off" => TooltipDetailLevel.Off,
        "minimal" => TooltipDetailLevel.Minimal,
        "detailed" => TooltipDetailLevel.Detailed,
        _ => TooltipDetailLevel.Standard,
    };

    /// <summary><see cref="ParseLevel"/>の逆変換。settings.json へ書き戻す文字列を返す。</summary>
    public static string ToSettingsValue(TooltipDetailLevel level) => level switch
    {
        TooltipDetailLevel.Off => "off",
        TooltipDetailLevel.Minimal => "minimal",
        TooltipDetailLevel.Detailed => "detailed",
        _ => "standard",
    };

    /// <summary>標準の説明（1〜2文＋ショートカット併記）。</summary>
    public static readonly AttachedProperty<string?> StandardProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Standard", typeof(HelpTip));

    /// <summary>くわしい説明。未指定のコントロールでは「くわしい説明」選択時も標準の説明を使う。</summary>
    public static readonly AttachedProperty<string?> DetailedProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Detailed", typeof(HelpTip));

    /// <summary>
    /// 最低限（現在の値だけ）。<see cref="TooltipDetailLevel.Minimal"/>のenumドキュメント参照。
    /// 固定文字列ではなく、値を持つ設定項目では<c>{Binding ...}</c>で「今の値」を反映させて使う
    /// （例: <c>views:HelpTip.Minimal="{Binding EditorTabSizeText, StringFormat='タブ幅: {}{0}'}"</c>）。
    /// <see cref="StandardProperty"/>・<see cref="DetailedProperty"/>と同じ通常のAvaloniaProperty
    /// なので、バインディングも静的な文字列も両方そのまま使える。未指定のコントロール
    /// （値を持たないボタン等）では「最低限」選択時も標準の説明にフォールバックする
    /// （<see cref="SelectText"/>）。
    /// </summary>
    public static readonly AttachedProperty<string?> MinimalProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Minimal", typeof(HelpTip));

    public static string? GetStandard(Control control) => control.GetValue(StandardProperty);

    public static void SetStandard(Control control, string? value) => control.SetValue(StandardProperty, value);

    public static string? GetDetailed(Control control) => control.GetValue(DetailedProperty);

    public static void SetDetailed(Control control, string? value) => control.SetValue(DetailedProperty, value);

    public static string? GetMinimal(Control control) => control.GetValue(MinimalProperty);

    public static void SetMinimal(Control control, string? value) => control.SetValue(MinimalProperty, value);

    private static void Track(Control control)
    {
        // AddOrUpdateなら複数回Changedが飛んできても例外にならない（Standard・Detailedの
        // どちらか一方だけ設定される場合も、両方設定される場合も同じ経路で通る）。
        TrackedControls.AddOrUpdate(control, null);
        Apply(control);
    }

    private static void Apply(Control control)
    {
        var text = SelectText(control);
        ToolTip.SetTip(control, BuildTipContent(text));
    }

    private static string? SelectText(Control control) => _currentLevel switch
    {
        TooltipDetailLevel.Off => null,
        // 値を持たないコントロール（Minimalが未設定＝ボタン等）は標準の説明にフォールバックする
        // （「値を持たないボタンでは、最低限＝標準と同じで構わない」という設計判断。
        // TooltipDetailLevel.Minimalのenumドキュメント参照）。
        TooltipDetailLevel.Minimal => GetMinimal(control) ?? GetStandard(control),
        TooltipDetailLevel.Detailed => GetDetailed(control) ?? GetStandard(control),
        _ => GetStandard(control),
    };

    /// <summary>
    /// ツールチップの中身を組み立てる。プレーンな文字列のままToolTip.Tipへ渡すと、
    /// Avaloniaの既定のToolTip表示は折り返さない（1行で伸び続け、長い「くわしい説明」が
    /// 画面外へはみ出す）ため、TextWrapping.WrapとMaxWidthを指定したTextBlockを明示的に
    /// 組み立てて渡す。テキストが無い（＝「表示しない」、または文言未設定）場合はnullを返し、
    /// 空文字ではなくツールチップ自体を割り当てない状態にする
    /// （Avaloniaのツールチップサービスは Tip が null なら開かない）。
    /// </summary>
    private static object? BuildTipContent(string? text)
        => string.IsNullOrEmpty(text)
            ? null
            : new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = MaxTipWidth };
}
