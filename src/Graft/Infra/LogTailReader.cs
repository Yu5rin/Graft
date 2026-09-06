using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Graft.Infra;

/// <summary>
/// 機能2（ログの参照手段）: 設定画面「バージョン情報」タブの「最新のログを表示」で使う、
/// 最新のログファイルの探索と末尾の切り出しロジック。UI（<c>Graft.Views.AboutView</c>・
/// <c>Graft.Views.LogViewerWindow</c>）から独立させ、単体テストだけで検証できるようにする。
///
/// <see cref="Logger"/>はlogs/配下へ1行1JSON（JSON Lines）形式で追記する。ファイル名は
/// <c>yyyyMMdd-&lt;pid&gt;.log</c>（不具合1の修正でプロセスIDを含めるようにした新形式。
/// <see cref="AppPaths.GetLogFilePath"/>参照）で、移行前に残っている<c>yyyyMMdd.log</c>
/// （旧形式・プロセスIDなし）も引き続き読める。同じ日付に複数プロセス分のファイルが
/// 並びうるため、<see cref="FindLatestDateLogFiles"/>・<see cref="ReadTailMerged"/>で
/// それらをまとめて時刻順に扱う。
///
/// <see cref="ReadTail"/>・<see cref="ReadTailMerged"/>はいずれも中身を一切整形しない
/// （インデント付与や見やすい並び替えを行わない）。理由は2つ:
/// (1) 1行=1レコードという構造そのものが「何件出力されたか」「順序」を素早く見て取る手がかりであり、
///     整形して1レコードを複数行に展開すると、その視認性が失われる。
/// (2) 不具合報告のやり取りでは、表示した内容をそのままコピーして貼り付けられる単純さのほうが、
///     見た目の綺麗さより実用上重要（<c>LogViewerWindow</c>の「コピー」ボタン参照）。
/// </summary>
public static class LogTailReader
{
    /// <summary>「最新のログを表示」で既定として表示する末尾の行数。</summary>
    public const int DefaultMaxLines = 200;

    /// <summary>
    /// <paramref name="logsDirectory"/>直下から最新のログファイルを探す。
    /// ファイル名（新形式<c>yyyyMMdd-&lt;pid&gt;.log</c>・旧形式<c>yyyyMMdd.log</c>）の
    /// 先頭8文字を日付として解釈できるものはその日付順で比較し、想定外の名前のファイル
    /// （解釈できないもの）は更新日時で比較する。1件も無い、またはディレクトリ自体が
    /// 無ければnull。
    ///
    /// 同じ最新の日付に複数プロセス分のファイルが並ぶ場合（不具合1の修正）、このメソッドは
    /// そのうちの1つしか返さない。「最新のログを表示」からはそれらすべてを対象にすべき
    /// なので、そちらは<see cref="FindLatestDateLogFiles"/>を使うこと。このメソッド自体は
    /// 単体テスト（<c>LogTailReaderTests</c>）との後方互換のために残している。
    /// </summary>
    public static string? FindLatestLogFile(string logsDirectory)
    {
        if (!Directory.Exists(logsDirectory)) return null;

        string? latestPath = null;
        DateTime latestKey = DateTime.MinValue;

        foreach (var file in Directory.EnumerateFiles(logsDirectory, "*.log"))
        {
            var key = TryParseDateFromFileName(Path.GetFileNameWithoutExtension(file)) ?? SafeGetLastWriteTimeUtc(file);
            if (latestPath is null || key > latestKey)
            {
                latestPath = file;
                latestKey = key;
            }
        }

        return latestPath;
    }

    /// <summary>
    /// <paramref name="logsDirectory"/>直下から、最新の日付に属するログファイルを
    /// すべて返す（不具合1の修正）。ファイル名の先頭8文字が<c>yyyyMMdd</c>として
    /// 解釈できるものだけを対象に、その最大値と一致するものをすべて集める
    /// （新形式<c>yyyyMMdd-&lt;pid&gt;.log</c>は同じ日付なら複数該当しうる）。
    /// 日付として解釈できるファイルが1つも無い場合は、更新日時が最新の1件のみを返す
    /// （<see cref="FindLatestLogFile"/>と同じフォールバック）。
    /// 戻り値はファイル名の辞書順（＝プロセスIDの文字列順。時刻順の並べ替えは
    /// <see cref="ReadTailMerged"/>側で行毎に行うためここでは順序を保証しない）。
    /// </summary>
    public static string[] FindLatestDateLogFiles(string logsDirectory)
    {
        if (!Directory.Exists(logsDirectory)) return Array.Empty<string>();

        var dated = new List<(string Path, DateTime Date)>();
        var undated = new List<(string Path, DateTime Mtime)>();

        foreach (var file in Directory.EnumerateFiles(logsDirectory, "*.log"))
        {
            var date = TryParseDateFromFileName(Path.GetFileNameWithoutExtension(file));
            if (date is { } d)
            {
                dated.Add((file, d));
            }
            else
            {
                undated.Add((file, SafeGetLastWriteTimeUtc(file)));
            }
        }

        if (dated.Count > 0)
        {
            var maxDate = dated.Max(x => x.Date);
            return dated.Where(x => x.Date == maxDate)
                .Select(x => x.Path)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();
        }

        if (undated.Count == 0) return Array.Empty<string>();

        var maxMtime = undated.Max(x => x.Mtime);
        return undated.Where(x => x.Mtime == maxMtime)
            .Select(x => x.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 指定ファイルの末尾<paramref name="maxLines"/>行を、元の順序のまま改行区切りで返す。
    /// ファイル全体をメモリへ読み込まず、固定サイズ（<paramref name="maxLines"/>件）のキューへ
    /// 1行ずつ流し込みながら古い行を捨てる方式のため、ファイルが大きくてもメモリ使用量は
    /// 表示件数に比例するだけで済む。
    ///
    /// 【実機クラッシュの修正】読み取り対象は多くの場合、<see cref="Logger"/>が今まさに
    /// 書き込み中の当日ログ（例: <c>20260811.log</c>）である。<see cref="File.ReadLines(string)"/>
    /// は既定の共有指定（<c>FileShare.Read</c>）でファイルを開くため、Windowsの強制排他ロック規則
    /// （新規オープン側が要求する共有範囲に、既存ハンドルが握っているアクセス権が収まっている必要が
    /// ある）により、<see cref="Logger"/>側が<c>FileAccess.Write</c>で開いている当日ログを開けず
    /// <c>IOException</c>（「別のプロセスが使用中」）で失敗していた（実機ログで確認済み）。
    /// <see cref="Logger.OpenWriterSafe(string)"/>は書き込みを<c>FileShare.Read</c>で開いており、
    /// これは他プロセスからの読み取りは許すが、読み取り側が要求する共有範囲に書き込み側の
    /// <c>FileAccess.Write</c>が含まれていないと今度は読み取り側のオープンが弾かれる。そのため
    /// ここでは読み取り側の共有範囲を<c>FileShare.ReadWrite | FileShare.Delete</c>まで広げ、
    /// 「他プロセス（＝Loggerの書き込みハンドル）のWriteアクセスを妨げない」ことを明示して開く。
    /// </summary>
    public static string ReadTail(string filePath, int maxLines = DefaultMaxLines)
    {
        if (maxLines <= 0) return string.Empty;

        var buffer = new Queue<string>(maxLines);
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (buffer.Count == maxLines) buffer.Dequeue();
            buffer.Enqueue(line);
        }

        return string.Join(Environment.NewLine, buffer);
    }

    /// <summary>
    /// 複数のログファイル（不具合1の修正で同じ日付に並びうる、別プロセス分のファイル群）の
    /// 末尾をまとめ、全体を各行のtimestampフィールドで時刻順に並べ替えたうえで、
    /// 末尾<paramref name="maxLines"/>行だけを返す。
    ///
    /// 各ファイルからはまず<see cref="ReadTail"/>で末尾<paramref name="maxLines"/>行だけを
    /// 読む（最終的に採用されるのは全体で末尾<paramref name="maxLines"/>行なので、
    /// 1ファイルからそれ以上の行が採用されることはなく、ファイルごとにこれだけ読めば足りる）。
    /// 並べ替えは<see cref="Enumerable.OrderBy{TSource,TKey}(IEnumerable{TSource},System.Func{TSource,TKey})"/>
    /// （安定ソート）を使うため、同時刻・timestamp読み取り不可の行同士は元の
    /// 読み込み順（＝ファイルの列挙順→ファイル内の出現順）を保つ。
    ///
    /// timestampフィールドが無い・JSONとして壊れている行（実測で確認された、複数プロセスの
    /// 書き込み競合による破損行を含む）は<see cref="DateTimeOffset.MinValue"/>として扱い
    /// 先頭側へ寄せる。表示上は残るため、壊れた行があったこと自体は利用者から見えたままになる。
    /// </summary>
    public static string ReadTailMerged(IReadOnlyList<string> filePaths, int maxLines = DefaultMaxLines)
    {
        if (maxLines <= 0 || filePaths.Count == 0) return string.Empty;
        if (filePaths.Count == 1) return ReadTail(filePaths[0], maxLines);

        var merged = new List<(DateTimeOffset Timestamp, string Line)>();
        foreach (var path in filePaths)
        {
            var tail = ReadTail(path, maxLines);
            if (tail.Length == 0) continue;

            foreach (var line in tail.Split(Environment.NewLine))
            {
                merged.Add((TryParseTimestamp(line) ?? DateTimeOffset.MinValue, line));
            }
        }

        var ordered = merged.OrderBy(x => x.Timestamp).Select(x => x.Line).ToArray();
        var start = Math.Max(0, ordered.Length - maxLines);
        return string.Join(Environment.NewLine, ordered[start..]);
    }

    /// <summary>1行1JSONの<c>timestamp</c>フィールドだけを読み取る。壊れた行・欠落時はnull。</summary>
    private static DateTimeOffset? TryParseTimestamp(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            if (doc.RootElement.TryGetProperty("timestamp", out var prop)
                && prop.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(prop.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
            {
                return ts;
            }
        }
        catch (JsonException)
        {
            // 壊れた行はタイムスタンプ不明として扱う（先頭側へ寄せる）
        }

        return null;
    }

    /// <summary>
    /// ファイル名（拡張子なし）の先頭8文字を<c>yyyyMMdd</c>として解釈する。
    /// 新形式<c>yyyyMMdd-&lt;pid&gt;</c>・旧形式<c>yyyyMMdd</c>のどちらも先頭8文字が
    /// 日付なので、名前の残り（9文字目以降）は見ずに済む。
    /// </summary>
    private static DateTime? TryParseDateFromFileName(string name)
    {
        var datePart = name.Length >= 8 ? name[..8] : name;
        return DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static DateTime SafeGetLastWriteTimeUtc(string file)
    {
        try
        {
            return File.GetLastWriteTimeUtc(file);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }
}
