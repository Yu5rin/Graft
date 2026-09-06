using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Graft.Core;

/// <summary>
/// .NET例外の <see cref="Exception.Message"/> を利用者向けの日本語メッセージへ変換する。
/// 「UI文言はすべて日本語」という方針に対し、実機検証で見つかった不具合3
/// （存在しないフォルダをrootに持つプロジェクトで、ファイル監視の開始失敗ダイアログに
/// .NETの英語例外メッセージがそのまま出ていた）への対応。
/// <para>
/// よくある原因（フォルダ・ファイルが無い／アクセス拒否／他アプリが使用中／実行ファイルが
/// 見つからない）だけを判定して日本語の一言に置き換える。それ以外の想定外の例外までは
/// 無理に翻訳せず、「次に何をすればよいか」が分かる一般的な文言を返す。
/// </para>
/// <para>
/// いずれの場合も元の英語メッセージは「（詳細: ...）」として結果に残す。この層の呼び出し元の
/// 多くは静的なI/Oヘルパー（<c>Core</c>/<c>Features</c>配下）で、原因調査用のロガーへの参照を
/// 持たない。ロガーを引き回すにはコンストラクタ注入等の設計変更が必要になり本修正の範囲を
/// 超えるため、原文は握り潰さずメッセージ自体に残す方式を採った（ログへの転記が必要な場合は
/// 上位のUIハンドラ側で改めて記録できる）。
/// </para>
/// </summary>
public static class ExceptionMessages
{
    /// <summary>
    /// 例外を「日本語の理由＋（詳細: 原文）」の1文へ変換する。
    /// </summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var reason = ex switch
        {
            DirectoryNotFoundException => "フォルダが見つかりません。移動または削除された可能性があります。",
            FileNotFoundException => "ファイルが見つかりません。移動または削除された可能性があります。",
            // FileSystemWatcher等、一部のAPIは存在しないフォルダに対してArgumentExceptionを
            // 投げる（DirectoryNotFoundExceptionではない）。メッセージ文言で判定する。
            ArgumentException when LooksLikeMissingPath(ex)
                => "フォルダが見つかりません。移動または削除された可能性があります。",
            UnauthorizedAccessException => "アクセスが拒否されました。権限を確認してください。",
            // 更新の確認・ダウンロード（Core.Update）。以前は通信断のときHttpRequestExceptionの
            // 原文（英語の内部メッセージ）がそのままダイアログへ出る経路があった
            // （HttpUpdateDownloader）。ここで日本語の一言に落とす。
            HttpRequestException => "ネットワークに接続できませんでした。接続状態を確認してから、もう一度お試しください。",
            // フック実行（Process.Start）で実行ファイルが見つからない・起動できない場合。
            Win32Exception => "コマンドを実行できませんでした。実行ファイルが見つからないか、PATHが通っていない可能性があります。",
            IOException when IsSharingViolation(ex)
                => "他のアプリがファイルを使用中の可能性があります。閉じてから再試行してください。",
            _ => "予期しないエラーが発生しました。解決しない場合は時間をおいて再試行するか、ログを確認してください。",
        };

        return $"{reason}（詳細: {ex.Message}）";
    }

    /// <summary>
    /// <see cref="JsonException"/> を、利用者が直せる形の日本語1文へ変換する（設定のJSON直接編集用）。
    ///
    /// <para>
    /// 【実機不具合対応】 以前は <c>$"JSONを解析できませんでした: {ex.Message}"</c> をそのまま
    /// 画面へ出していた。実際の表示は
    /// <c>JSONを解析できませんでした: '@' is an invalid start of a value. Path: $ | LineNumb…</c>
    /// で、英語であるうえに、唯一役に立つ情報である行番号が右端で見切れて読めなかった
    /// （表示先が横並びのStackPanel内で折り返せなかったことも重なっている。
    /// RawJsonSettingsView.axaml側も併せて直した）。
    /// </para>
    ///
    /// <para>
    /// 【原文を正規表現で読まない理由】 <c>'@' is an invalid start of a value</c> のような
    /// 文面から不正な文字を取り出すこともできるが、System.Text.Jsonのメッセージ文面は
    /// .NETのバージョンで変わりうる非公開の実装詳細で、将来黙って壊れる。代わりに
    /// <see cref="JsonException.LineNumber"/>・<see cref="JsonException.BytePositionInLine"/>
    /// （公開API）と、呼び出し元が持っている元テキストから、位置と実際の文字を自分で求める。
    /// </para>
    /// </summary>
    /// <param name="ex">解析に失敗した例外。</param>
    /// <param name="sourceText">
    /// 解析しようとした元のJSON文字列。渡せる場合は「その位置に実際に何の文字があったか」まで
    /// 示せる。null や位置が特定できない場合は行番号までにとどめる。
    /// </param>
    public static string DescribeJsonParseFailure(JsonException ex, string? sourceText = null)
    {
        ArgumentNullException.ThrowIfNull(ex);

        // エラーコード体系に乗せる。JSONタブは「保存前の検証に失敗し、何も差し替えずに
        // 保存を保留している」場面そのものなのでE406が当てはまる（ErrorCode.E406参照）。
        // ただし対処文（ErrorCatalog）の「値を修正すると自動的に保存されます」は他タブの
        // 即時反映方式の話で、明示保存であるJSONタブには当てはまらないため、ここでは
        // この画面に即した対処を自前で書く。
        var head = "E406 JSONとして読み取れません";

        var location = DescribeJsonLocation(ex, sourceText);
        var tail = "カンマ・引用符・波かっこ（{}）の対応を確認してから、もう一度「検証して保存」を押してください。";
        return location is null ? $"{head}。{tail}" : $"{head}: {location}。{tail}";
    }

    /// <summary>
    /// 「3行目6文字目のあたりが不正です（その位置の文字: 「@」）」のような位置説明を組み立てる。
    /// 位置が分からなければ null。
    /// </summary>
    private static string? DescribeJsonLocation(JsonException ex, string? sourceText)
    {
        // LineNumber・BytePositionInLine はいずれも0始まり。利用者向けには1始まりへ直す。
        if (ex.LineNumber is not { } zeroBasedLine) return null;
        var lineNumber = zeroBasedLine + 1;

        if (sourceText is null || ex.BytePositionInLine is not { } byteOffset)
        {
            return $"{lineNumber}行目のあたりが不正です";
        }

        // 改行コードはCRLF/LF/CRのいずれもありうる（利用者が外部エディタで貼り付けた場合を含む）。
        // ここでは行の切り出しにしか使わないため、まとめてLFへ正規化してから分割する。
        var normalized = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal)
                                   .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (zeroBasedLine < 0 || zeroBasedLine >= lines.Length)
        {
            return $"{lineNumber}行目のあたりが不正です";
        }

        var line = lines[(int)zeroBasedLine];
        var charIndex = ByteOffsetToCharIndex(line, byteOffset);
        var columnNumber = charIndex + 1;

        // その位置の文字まで示せると、利用者は自分の入力のどこを直せばよいかを一目で判断できる
        // （実機で問題になったのは「@」を値の先頭に書いてしまった例）。行末を超えている場合
        // （閉じ括弧の不足など、「足りない」種類のエラー）は示せる文字が無いので位置だけ出す。
        if (charIndex >= line.Length)
        {
            return $"{lineNumber}行目{columnNumber}文字目のあたりが不正です";
        }

        return $"{lineNumber}行目{columnNumber}文字目のあたりが不正です（その位置の文字: 「{line[charIndex]}」）";
    }

    /// <summary>
    /// UTF-8のバイト位置を、その行の先頭からの文字位置（0始まり）へ直す。
    /// <see cref="JsonException.BytePositionInLine"/>は名前のとおり<b>バイト</b>単位のため、
    /// 日本語を含む行ではそのまま文字位置として扱うと大きくずれる（UTF-8で1文字3バイト）。
    /// 設定JSONにはコメント文字列などで日本語が入りうるので、必ず変換する。
    /// </summary>
    private static int ByteOffsetToCharIndex(string line, long byteOffset)
    {
        if (byteOffset <= 0) return 0;

        var consumedBytes = 0L;
        for (var i = 0; i < line.Length; i++)
        {
            if (consumedBytes >= byteOffset) return i;

            // サロゲートペア（絵文字など）は2つのcharで1文字。ペアのまま数えないと
            // Encoding.UTF8.GetByteCountが不正な単独サロゲートとして扱ってしまう。
            var isPair = char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]);
            var length = isPair ? 2 : 1;
            consumedBytes += Encoding.UTF8.GetByteCount(line.AsSpan(i, length));
            if (isPair) i++;
        }

        return line.Length;
    }

    private static bool LooksLikeMissingPath(Exception ex)
        => ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 共有違反（他プロセスが使用中のファイル）の簡易判定。Windowsは HRESULT 0x80070020
    /// （ERROR_SHARING_VIOLATION）で判定できるが、.NETのIOExceptionはOS間で共通の型しか
    /// 持たないため、それ以外の環境向けにメッセージ文言でも補足判定する。
    /// </summary>
    private static bool IsSharingViolation(Exception ex)
        => ex.HResult == unchecked((int)0x80070020)
           || ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);
}
