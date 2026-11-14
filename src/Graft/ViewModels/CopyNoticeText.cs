namespace Graft.ViewModels;

/// <summary>
/// 「パスをコピー」「相対パスをコピー」を押したときに、数秒で消える通知へ出す文面。
///
/// 以前はプロジェクト一覧だけが知らせを出し、エクスプローラ・エディタのタブ・検索結果・接ぎ木パネルは
/// 黙ってコピーするだけだった。押しても何も起きないように見えるため、全画面で同じ型の知らせを出す。
/// 文面の組み立てをここへ集め、場所ごとに言い方がずれないようにする。
/// </summary>
public static class CopyNoticeText
{
    /// <summary>プロジェクトのフォルダ自体のパスをコピーしたとき（プロジェクト一覧・エクスプローラの余白）。</summary>
    public const string ProjectPathCopied = "プロジェクトのパスをコピーしました";

    /// <summary>「パスをコピー」。<paramref name="path"/> の最後の部分（ファイル名・フォルダ名）を名前として使う。</summary>
    public static string PathCopied(string path) => $"{NameOf(path)} のパスをコピーしました";

    /// <summary>「相対パスをコピー」。<paramref name="path"/> の最後の部分を名前として使う。</summary>
    public static string RelativePathCopied(string path) => $"{NameOf(path)} の相対パスをコピーしました";

    /// <summary>
    /// パスの最後の部分。区切りは / と \ の両方を見る（相対パスは / 区切り、フルパスはOS依存のため）。
    /// 末尾の区切りは無視する。区切りが無ければ全体を返す。
    /// </summary>
    internal static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }
}
