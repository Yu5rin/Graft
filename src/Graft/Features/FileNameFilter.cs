namespace Graft.Features;

/// <summary>
/// コンテキスト収集の窓で、ファイル一覧を相対パスの部分一致で絞り込む純ロジック。
///
/// 【照合の決め方と、その理由】
/// <list type="bullet">
/// <item>相対パス全体に対する**部分一致**にする（ファイル名だけではなく、フォルダ名も対象）。
/// 「view」と打てば <c>Views/</c> 配下も、<c>MainView.cs</c> も拾える。利用者が覚えているのは
/// 「どのフォルダのどんな名前か」の断片であることが多く、ファイル名だけに限ると
/// <c>src/api/user.ts</c> を「api user」と打っても見つからない。</item>
/// <item>大文字小文字は区別しない（<see cref="StringComparison.OrdinalIgnoreCase"/>）。Windows の
/// ファイルシステムが区別しないことと、「readme」と打って <c>README.md</c> を探す使い方に合わせる。
/// カルチャ依存の比較は使わない（トルコ語の i などで結果が環境によって変わるのを避け、
/// 10万件規模でも安価な序数比較にする）。</item>
/// <item><c>\</c> と <c>/</c> は同一視する。走査結果の相対パスは区切りが <c>/</c> だが、利用者は
/// Windows のエクスプローラやAIの出力からパスを貼り付けるため <c>\</c> で打つことがある
/// （<see cref="RequestedFileMatcher"/> と同じ事情）。</item>
/// <item>空白（全角空白を含む）区切りで複数語を入れたら、**すべての語を含む**ものに絞る（AND）。
/// 日本語入力中は全角空白が入りやすいため、<see cref="char.IsWhiteSpace(char)"/> に当たる文字で分ける。
/// 語の順序は問わない（「user api」でも <c>api/user.ts</c> に一致する）。</item>
/// </list>
/// 正規表現やワイルドカードは意図して持たない。入力途中の <c>[</c> や <c>*</c> で例外や
/// 全件一致にならず、打った文字がそのまま探す文字になるほうが「絞り込み」として予想しやすい。
/// </summary>
public static class FileNameFilter
{
    /// <summary>
    /// 入力文字列を語に分ける。<c>\</c> は <c>/</c> に揃え、空白（全角を含む）で区切り、
    /// 空の語は捨てる。空欄や空白だけの入力は空の一覧になる（＝絞り込みなし）。
    /// </summary>
    public static IReadOnlyList<string> ParseTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();

        // string.Split(null) は char.IsWhiteSpace に当たる文字（U+3000 の全角空白を含む）で区切る。
        return query.Replace('\\', '/')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// 相対パスが、すべての語を含むか。<paramref name="terms"/> が空なら true（絞り込みなし）。
    /// <paramref name="terms"/> は <see cref="ParseTerms"/> の結果を渡す（区切りが <c>/</c> に揃っている前提）。
    /// </summary>
    public static bool Matches(string relativePath, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count == 0) return true;

        // 走査結果は常に "/" 区切りなので、通常は文字列を作り直さない（10万件を毎回
        // 割り当てると絞り込みのたびに無駄な負荷になる）。"\" が混じる場合だけ揃える。
        var path = relativePath.Contains('\\') ? relativePath.Replace('\\', '/') : relativePath;
        foreach (var term in terms)
        {
            if (path.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        return true;
    }
}
