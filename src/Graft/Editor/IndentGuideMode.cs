namespace Graft.Editor;

/// <summary>
/// インデントガイド（縦線）の表示モード。移植元検討書（Pane、
/// <c>/workspace/pane/src/settings.js</c> の <c>codeIndentGuides</c>）が試行錯誤の末に
/// たどり着いた3値をそのまま踏襲する。既定は <see cref="FoldableRangesOnly"/>。
/// </summary>
public enum IndentGuideMode
{
    /// <summary>表示しない。</summary>
    None,

    /// <summary>折りたたみできる範囲だけに縦線を引く（既定）。</summary>
    FoldableRangesOnly,

    /// <summary>すべてのインデント階層に縦線を引く（折りたたみ範囲の有無を問わない）。</summary>
    AllIndentation,
}

/// <summary>
/// settings.json の文字列表現（<c>"none" / "foldable" / "all"</c>）と
/// <see cref="IndentGuideMode"/> の相互変換。<see cref="Graft.Themes.ThemeManager.ParseTheme"/>と
/// 同じ作法（未知の値・欠落した値は既定へフォールバック）で、古いsettings.json
/// （このキー自体が存在しないバージョンで書かれたもの）でも既定の"foldable"として動く。
/// </summary>
public static class IndentGuideModeParser
{
    public static IndentGuideMode Parse(string? value) => value switch
    {
        "none" => IndentGuideMode.None,
        "all" => IndentGuideMode.AllIndentation,
        _ => IndentGuideMode.FoldableRangesOnly, // "foldable"に加え、null・未知の値もここへ倒す。
    };

    public static string ToSettingValue(IndentGuideMode mode) => mode switch
    {
        IndentGuideMode.None => "none",
        IndentGuideMode.AllIndentation => "all",
        _ => "foldable",
    };
}
