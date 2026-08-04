namespace Graft.Tests.TestSupport;

/// <summary>
/// テスト用フィクスチャファイル（Fixtures/Patches/配下）を読み込むヘルパ。
/// csproj の設定によりビルド出力へコピーされたファイルを、実行ディレクトリからの
/// 相対パスで読み込む。改行コードなどは一切変換せず、ファイルの内容をそのまま返す。
/// </summary>
internal static class FixtureLoader
{
    /// <summary>
    /// "Fixtures/Patches/&lt;名前&gt;.txt" を読み込み、内容をそのまま文字列で返す。
    /// </summary>
    public static string LoadPatch(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Patches", $"{name}.txt");
        if (!File.Exists(path))
            throw new FileNotFoundException($"パッチフィクスチャが見つかりません: {path}", path);
        return File.ReadAllText(path);
    }
}
