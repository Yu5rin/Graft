using System.Text;
using System.Text.RegularExpressions;

namespace Graft.Core;

/// <summary>
/// 機能1: エラー/失敗ダイアログの「詳細をコピー」ボタンで、クリップボードへ書き出す文面を
/// 組み立てる。UI（<c>Graft.Platform.AvaloniaDialogService</c>）から独立した純粋関数として
/// 分離し、単体テストだけで組み立てロジックを検証できるようにする。
///
/// 【「エラーダイアログかどうか」の判定方法】
/// <c>Graft.Platform.IDialogService</c> の <c>ConfirmAsync</c>/<c>ConfirmThreeWayAsync</c>/
/// <c>ShowMessageAsync</c> は文字列（title/message）だけを受け取る設計のため、呼び出し側を
/// 書き換えずに「エラー由来のメッセージだけにボタンを出す」ことは、渡された文字列自体から
/// 判定するしかない。<see cref="GraftIssue.ToDisplayText"/> は必ず「E101 ...」のような
/// エラーコードを先頭に含む1行を生成するため、ここでは <see cref="ContainsErrorCode"/> で
/// メッセージ中にエラーコードのパターン（例: E101、E402）が含まれるかどうかを見て判定する。
/// 通常の確認ダイアログ（「削除しますか？」等）にはエラーコードが含まれないため、この方式で
/// 「エラー由来のメッセージにだけボタンを出す／通常のメッセージには出さない」を、
/// 呼び出し側を一切変更せず単一箇所（ダイアログ実装）で実現できる。
/// </summary>
public static class ErrorDetailFormatter
{
    // ErrorCodeの命名規則（E + 3桁の数字。ErrorCodes.cs参照）に合わせた検出パターン。
    private static readonly Regex ErrorCodePattern = new(@"\bE\d{3}\b", RegexOptions.Compiled);

    /// <summary>メッセージ中に「E101」のようなエラーコードのパターンが含まれるかどうか。</summary>
    public static bool ContainsErrorCode(string message)
        => !string.IsNullOrEmpty(message) && ErrorCodePattern.IsMatch(message);

    /// <summary>
    /// 「詳細をコピー」でクリップボードへ書き出す文面を組み立てる。
    ///
    /// <paramref name="message"/> には呼び出し元（各ViewModel）が
    /// <see cref="GraftIssue.ToDisplayText"/> 等で組み立てたエラーコード・要約・詳細が
    /// 既に含まれているため、そのまま先頭に載せる。対処方法（Remedy）はメッセージ文字列には
    /// 含まれていないことが多いため、メッセージ中で見つかったエラーコードごとに
    /// <see cref="ErrorCatalog"/> から引き直して別セクションとして追記する
    /// （同じコードが複数回登場しても重複させない）。
    /// </summary>
    public static string BuildCopyText(string title, string message, string appVersion, string osDescription)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(appVersion);
        ArgumentNullException.ThrowIfNull(osDescription);

        var builder = new StringBuilder();
        builder.AppendLine(title);
        builder.AppendLine(message);

        var remedySection = BuildRemedySection(message);
        if (remedySection is not null)
        {
            builder.AppendLine();
            builder.AppendLine("対処:");
            builder.Append(remedySection);
        }

        builder.AppendLine();
        builder.Append("バージョン: Graft ").AppendLine(appVersion);
        builder.Append("OS: ").AppendLine(osDescription);

        return builder.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// メッセージ中の各エラーコードについて「・E101 SEARCH部が見つからない: 対処文」を
    /// 1行ずつ並べたセクションを組み立てる。1件も見つからなければnull。
    /// </summary>
    private static string? BuildRemedySection(string message)
    {
        var codes = ErrorCodePattern.Matches(message)
            .Select(m => m.Value)
            .Distinct()
            .Select(TryParseCode)
            .Where(code => code is not null)
            .Select(code => code!.Value)
            .ToList();

        if (codes.Count == 0) return null;

        var builder = new StringBuilder();
        foreach (var code in codes)
        {
            builder.Append("・").Append(code).Append(' ').Append(ErrorCatalog.SummaryOf(code))
                .Append(": ").AppendLine(ErrorCatalog.RemedyOf(code));
        }

        return builder.ToString();
    }

    // メッセージ中の "E999" のような、ErrorCode列挙に実在しない値（誤検知）は対処セクションから除く。
    private static ErrorCode? TryParseCode(string text)
        => Enum.TryParse<ErrorCode>(text, out var code) ? code : null;
}
