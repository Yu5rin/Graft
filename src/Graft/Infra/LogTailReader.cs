using System.Globalization;
using System.IO;

namespace Graft.Infra;

/// <summary>
/// 機能2（ログの参照手段）: 設定画面「バージョン情報」タブの「最新のログを表示」で使う、
/// 最新のログファイルの探索と末尾の切り出しロジック。UI（<c>Graft.Views.AboutView</c>・
/// <c>Graft.Views.LogViewerWindow</c>）から独立させ、単体テストだけで検証できるようにする。
///
/// <see cref="Logger"/>はlogs/配下へ<c>yyyyMMdd.log</c>という1行1JSON（JSON Lines）形式で
/// 追記する。<see cref="ReadTail"/>はこの中身を一切整形しない（インデント付与や見やすい並び替え
/// を行わない）。理由は2つ:
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
    /// ファイル名（<see cref="AppPaths.GetLogFilePath"/>が作る<c>yyyyMMdd.log</c>形式）を
    /// 日付として解釈できるものはその日付順で比較し、想定外の名前のファイル（解釈できない
    /// もの）は更新日時で比較する。1件も無い、またはディレクトリ自体が無ければnull。
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
    /// 指定ファイルの末尾<paramref name="maxLines"/>行を、元の順序のまま改行区切りで返す。
    /// ファイル全体をメモリへ読み込まず、固定サイズ（<paramref name="maxLines"/>件）のキューへ
    /// 1行ずつ流し込みながら古い行を捨てる方式のため、ファイルが大きくてもメモリ使用量は
    /// 表示件数に比例するだけで済む。
    /// </summary>
    public static string ReadTail(string filePath, int maxLines = DefaultMaxLines)
    {
        if (maxLines <= 0) return string.Empty;

        var buffer = new Queue<string>(maxLines);
        foreach (var line in File.ReadLines(filePath))
        {
            if (buffer.Count == maxLines) buffer.Dequeue();
            buffer.Enqueue(line);
        }

        return string.Join(Environment.NewLine, buffer);
    }

    private static DateTime? TryParseDateFromFileName(string name)
        => DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

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
