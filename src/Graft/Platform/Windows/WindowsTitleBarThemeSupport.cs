namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="WindowsTitleBarTheme"/>が使う純粋なロジックだけを切り出したもの。
/// Win32 P/Invokeを一切呼ばないため、<c>ForegroundActivationDecision.cs</c>と同じ理由で
/// <c>[SupportedOSPlatform("windows")]</c>は付けていない（どのOS上でもコンパイル・実行でき、
/// Linux上のtests/Graft.Tests（直接ソースを取り込む。csproj参照）で検証できる）。
/// </summary>
internal static class WindowsTitleBarThemeSupport
{
    /// <summary>
    /// DWMWA_CAPTION_COLOR・DWMWA_TEXT_COLORが導入されたWindows 11の最小ビルド番号。
    /// これ未満（Windows 10以下）ではDwmSetWindowAttributeがエラーを返すだけで実害は無いが、
    /// 依頼書のとおり明示的に呼び出し自体を打ち切り、エラーも出さない。
    /// </summary>
    internal const int MinSupportedBuild = 22000;

    /// <summary>現在のOSビルド番号が、タイトルバー配色APIに対応するWindows 11以降かどうか。</summary>
    internal static bool IsSupportedBuild(int buildNumber) => buildNumber >= MinSupportedBuild;

    /// <summary>
    /// タイトルバー配色を実際に適用すべきかどうかの最終判定。<see cref="WindowsTitleBarTheme"/>が
    /// <c>OperatingSystem.IsWindows()</c>・<c>Environment.OSVersion.Version.Build</c>という
    /// 環境依存の値を読んでからここへ渡す形にすることで、判定そのものは環境非依存の
    /// 純粋関数として単体テストで固定できる（依頼書のテスト方針: 「Windows 11未満では
    /// 適用しない」「非Windowsでは何もしない」の両方をここ1箇所の入出力で検証する）。
    /// </summary>
    internal static bool ShouldApply(bool isWindows, int buildNumber) => isWindows && IsSupportedBuild(buildNumber);

    /// <summary>
    /// AvaloniaのColor（ARGB。<c>Color.R/G/B</c>は各0-255）が持つR・G・B成分を、
    /// <c>DwmSetWindowAttribute</c>が要求するCOLORREF形式（<c>0x00BBGGRR</c>）へ詰め直す。
    ///
    /// 【赤と青が入れ替わる不具合について】
    /// AvaloniaのColorはARGB（メモリ上でもA,R,G,Bの並び）だが、Win32のCOLORREFはBGR
    /// （下位バイトから R, G, B の順）で、ちょうどRとBのバイト位置が逆になっている。
    /// 呼び出し側で<c>(uint)color.ToUint32()</c>のような値をそのまま
    /// <c>DwmSetWindowAttribute</c>へ渡すと、指定した色の赤と青が入れ替わって描画される
    /// （典型的な不具合）。この関数へ変換を一本化し、非対称な色（#FF0000・#0000FF・#123456）
    /// で単体テスト固定することで再発を防ぐ（依頼書1章）。
    ///
    /// Alpha（透過度）はCOLORREFに無い概念のため無視する（DWMのキャプション色は不透明）。
    /// </summary>
    internal static uint ToColorRef(byte r, byte g, byte b)
        => ((uint)b << 16) | ((uint)g << 8) | r;
}
