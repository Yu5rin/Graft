namespace Graft.Core.Update;

/// <summary>
/// 自動更新で置き換える配布物のファイル名一覧。<c>tools/New-Release.ps1</c>の
/// <c>$requiredFiles</c>（win-x64版）と完全に一致させる、単一の情報源。
///
/// 【重要】ここに列挙されたファイル以外には一切触れない。ZIPの検証
/// （<see cref="UpdateZipInspector"/>）はこの一覧に無いファイルが1つでも含まれていたら
/// 展開そのものを中止する。settings.json・projects.json・back/・logs/ 等の利用者データは
/// この一覧に含まれておらず、自動更新の経路から物理的に触れない
/// （置き換え処理はこの一覧のファイル名でしか<c>File.Move</c>/<c>File.Copy</c>を呼ばないため）。
/// </summary>
public static class UpdateFiles
{
    /// <summary>Windows版配布物（win-x64）の必須ファイル一覧。</summary>
    public static readonly IReadOnlyList<string> RequiredFileNames = new[]
    {
        "Graft.exe",
        "av_libglesv2.dll",
        "libHarfBuzzSharp.dll",
        "libSkiaSharp.dll",
        "取扱説明書.md",
        "はじめにお読みください.txt",
    };

    /// <summary>
    /// 実行中でリネームしたファイルの一時退避に使う接尾辞。指示書の設計どおり
    /// "Graft.exe" → "Graft.exe.old" のようにリネームする。次回起動時に
    /// <see cref="PendingUpdateCleanup"/>がこの接尾辞を持つファイルだけを掃除する。
    /// </summary>
    public const string OldFileSuffix = ".old";

    /// <summary>配布物ZIP内で、配布アセット名のうちWindows版を見分けるための接尾辞。</summary>
    public const string WindowsAssetNameSuffix = "-win-x64.zip";
}
