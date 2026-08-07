namespace Graft.Features;

/// <summary>
/// クイックオープン（Ctrl+P）のあいまい一致ロジック。WPF/Avalonia非依存の純粋なクラスとし、
/// tests/Graft.Tests から直接検証できるようにする。
///
/// 一致規則: 入力文字列（クエリ）が、対象の相対パス（区切りは "/"）に対して大文字小文字を
/// 区別しないサブシーケンス一致（文字が順序どおり現れればよく、連続している必要はない）で
/// あれば一致とみなす。例えば "svm" は "src/ViewModels/ShellViewModel.cs" に一致する。
///
/// スコアリング: (1)ファイル名の先頭からクエリが連続一致 &gt; (2)ファイル名内でのサブシーケンス
/// 一致（先頭一致ではない） &gt; (3)ファイル名だけでは一致せずディレクトリ部分を含めて初めて
/// 一致、の3段階（<see cref="MatchTier"/>）。同点はパスが短い順に並べる（呼び出し側で
/// <see cref="RelativePathLength"/> を使ってソートする）。
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>一致の強さ。数値が小さいほど優先度が高い（ソートにそのまま使える）。</summary>
    public enum MatchTier
    {
        /// <summary>ファイル名の先頭からクエリが連続一致する。</summary>
        FileNamePrefix = 0,
        /// <summary>ファイル名内でクエリがサブシーケンス一致する（先頭一致ではない）。</summary>
        FileNameContains = 1,
        /// <summary>ファイル名単独では一致せず、ディレクトリ部分を含めて初めて一致する。</summary>
        PathOnly = 2,
    }

    /// <summary>1件の一致結果。<see cref="IsMatch"/>がfalseのときは他のメンバーに意味がない。</summary>
    public readonly record struct FuzzyMatch(bool IsMatch, MatchTier Tier, int RelativePathLength)
    {
        /// <summary>不一致を表す既定値。</summary>
        public static readonly FuzzyMatch None = default;
    }

    /// <summary>
    /// クエリと相対パスを比較する。クエリが空文字列の場合は常に一致し、
    /// 最も優先度の低い<see cref="MatchTier.PathOnly"/>を返す（呼び出し側で空欄時の
    /// 扱いを別途判断できるようにするため。詳細はQuickOpenViewModelを参照）。
    /// </summary>
    public static FuzzyMatch TryMatch(string query, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(relativePath);

        if (query.Length == 0)
        {
            return new FuzzyMatch(true, MatchTier.PathOnly, relativePath.Length);
        }

        if (!IsSubsequence(query, relativePath))
        {
            return FuzzyMatch.None;
        }

        var fileName = GetFileName(relativePath);
        var tier = fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase)
            ? MatchTier.FileNamePrefix
            : IsSubsequence(query, fileName)
                ? MatchTier.FileNameContains
                : MatchTier.PathOnly;

        return new FuzzyMatch(true, tier, relativePath.Length);
    }

    /// <summary>
    /// <paramref name="query"/>の各文字が、大文字小文字を区別せず<paramref name="text"/>内に
    /// この順序で（連続していなくてよい）現れればtrue。
    /// </summary>
    public static bool IsSubsequence(string query, string text)
    {
        var queryIndex = 0;
        foreach (var c in text)
        {
            if (queryIndex >= query.Length) break;
            if (char.ToUpperInvariant(c) == char.ToUpperInvariant(query[queryIndex])) queryIndex++;
        }
        return queryIndex >= query.Length;
    }

    private static string GetFileName(string relativePath)
    {
        var slashIndex = relativePath.LastIndexOf('/');
        return slashIndex < 0 ? relativePath : relativePath[(slashIndex + 1)..];
    }
}
