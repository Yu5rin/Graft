namespace Graft.Editor;

/// <summary>
/// 行間（機能改善「行間を設定できるようにする」）の4段階。<see cref="IndentGuideMode"/>と
/// 同じ作法（enum＋Parser、settings.jsonは文字列）を踏襲する。
///
/// 【AvaloniaEdit側の実装（依頼2の調査結果）】
/// AvaloniaEdit 11.1.0（本プロジェクトが元々参照していたバージョン）では、行の高さを決める
/// <c>AvaloniaEdit.Rendering.VisualLineTextParagraphProperties</c>（internal sealed）が
/// <c>LineHeight =&gt; DefaultTextRunProperties.FontRenderingEmSize * 1.35</c>という固定式を
/// 返すだけで、外部から行間を変える手段が一切無かった（ilspycmdでの逆コンパイルで確認。
/// <c>TextView.CreateParagraphProperties</c>もこのプロパティへ値を渡す経路を持たない）。
/// Avalonia本体側のテキストレイアウト（<c>Avalonia.Media.TextFormatting</c>）自体は
/// <c>TextParagraphProperties.LineHeight</c>を正しく尊重する（Avalonia.Base側を逆コンパイルして
/// 別途確認済み）ため、これはAvaloniaEdit側だけの制限だった。
///
/// 2025-10-01にAvaloniaUI/AvaloniaEditへ<c>TextEditorOptions.LineHeightFactor</c>
/// （PR #539 "Add TextEditorOptions.LineHeightFactor"）が正式にマージされ、11.4.0以降の
/// パッケージへ含まれている。これにより行の高さは
/// <c>行の自然な高さ（フォントの実測メトリクスから決まる） と 自然な高さ×LineHeightFactor の
/// 大きい方</c>（<c>VisualLine.SetTextLines</c>等が内部で使う<c>TextView.DefaultLineHeight</c>）
/// になり、行番号・折りたたみマーカー・インデントガイド・検索ハイライト・キャレット位置・
/// クリック位置判定など、行の高さや行のY座標を必要とする箇所は<b>すべて</b>この値を単一の
/// 情報源として使う（<c>VisualLine.GetTextLineVisualYPosition</c>／
/// <c>GetTextLineByVisualYPosition</c>等で実際に使われていることを逆コンパイルで確認済み）。
/// そのためGraft側の各描画（<see cref="IndentGuideRenderer"/>・<see cref="MarkerOnlyFoldingMargin"/>・
/// <c>SearchOverlay.SearchHighlightRenderer</c>）は行の高さ自体を計算し直しておらず、いずれも
/// AvaloniaEditが提供する<c>VisualLine.Height</c>・<c>BackgroundGeometryBuilder</c>越しに
/// 座標を得ているため、追加のコード変更なしにLineHeightFactorへ追従する
/// （<c>Graft.csproj</c>のAvalonia.AvaloniaEditを11.1.0→11.4.1へ更新した）。
///
/// 【既定値の較正】
/// 既定（<see cref="Normal"/>）は「見た目を変えない」という要求のため、旧AvaloniaEdit
/// （11.1.0、固定倍率1.35）での行の高さにできるだけ近づける値を選んだ。
/// tests/Graft.UiTests側の一時計測コード（Avalonia.Headless、実際のCodeFontFamily
/// フォールバック列・FontSize=13で計測、作業完了後に削除済み）で
/// <c>LineHeightFactor=1.0</c>（フロアを実質無効化した自然な高さ）を測定したところ
/// 15.1328125pxだった。旧式の高さは 13 × 1.35 = 17.55px なので、
/// 必要な倍率は 17.55 / 15.1328125 ≈ 1.1597。これはAvaloniaEdit側が新たに採用した既定値
/// 1.16（コメントに"matches the line height in the Visual Studio text editor"とある、
/// 特定のフォントに依存しない一般的な値）とほぼ一致したため、Graftの「標準」もそのまま1.16を
/// 採用する（環境によって実際に選ばれるフォールバックフォントは変わるため、全環境で
/// 旧表示と完全に一致する値は原理上存在しない。1.16はAvaloniaEditが複数フォントを想定して
/// 選定した値であり、Cascadia Mono系フォントが実際に使われるWindows実機でも大きくは
/// 外れないと判断した）。
/// </summary>
public enum LineSpacingMode
{
    /// <summary>せまい。フォントの自然な行高さのまま（AvaloniaEditのフロアを実質無効化）。</summary>
    Narrow,

    /// <summary>標準（既定）。旧バージョンの見た目にほぼ一致する広さ。</summary>
    Normal,

    /// <summary>広め。</summary>
    Wide,

    /// <summary>さらに広い。</summary>
    Wider,
}

/// <summary>
/// settings.json の文字列表現（<c>"narrow" / "normal" / "wide" / "wider"</c>）と
/// <see cref="LineSpacingMode"/>・実際に<c>TextEditorOptions.LineHeightFactor</c>へ渡す
/// 倍率との相互変換。<see cref="IndentGuideModeParser"/>と同じ作法
/// （未知の値・欠落した値は既定へフォールバック）。
/// </summary>
public static class LineSpacingModeParser
{
    // クラスコメント「既定値の較正」参照。Normal=1.16（AvaloniaEdit自身の既定値と同一）。
    // Narrowは自然な高さそのもの（1.0）。Wide/Widerは1.16を基準に段階的に広げた、
    // 既存の「フォントサイズ」欄などと同じく分かりやすいラウンドナンバー。
    public const double NarrowFactor = 1.0;
    public const double NormalFactor = 1.16;
    public const double WideFactor = 1.4;
    public const double WiderFactor = 1.7;

    public static LineSpacingMode Parse(string? value) => value switch
    {
        "narrow" => LineSpacingMode.Narrow,
        "wide" => LineSpacingMode.Wide,
        "wider" => LineSpacingMode.Wider,
        _ => LineSpacingMode.Normal, // "normal"に加え、null・未知の値もここへ倒す。
    };

    public static string ToSettingValue(LineSpacingMode mode) => mode switch
    {
        LineSpacingMode.Narrow => "narrow",
        LineSpacingMode.Wide => "wide",
        LineSpacingMode.Wider => "wider",
        _ => "normal",
    };

    /// <summary>settings.jsonの文字列から、<c>TextEditorOptions.LineHeightFactor</c>へ直接渡せる倍率へ。</summary>
    public static double ToLineHeightFactor(string? value) => Parse(value) switch
    {
        LineSpacingMode.Narrow => NarrowFactor,
        LineSpacingMode.Wide => WideFactor,
        LineSpacingMode.Wider => WiderFactor,
        _ => NormalFactor,
    };
}
