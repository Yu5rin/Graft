using System.IO.Compression;

namespace Graft.Core.Update;

/// <summary>
/// ダウンロードした配布物ZIPを検証・展開する。
///
/// 【なぜ検証してから展開するか】指示書の最重要事項: ZIPを素朴に展開すると、利用者の
/// 設定・プロジェクト一覧・バックアップ・履歴を巻き込みうる。想定外のファイルが1つでも
/// 含まれていたら、1バイトも書き出さずに中止する（<see cref="Validate"/>は展開を一切行わない
/// 読み取り専用の検査だけを行い、実際の書き出しは検証に通った後の<see cref="ExtractTo"/>でのみ
/// 行う2段構え）。展開先も、実際のインストール先（settings.json等がある<see
/// cref="Infra.AppPaths.BaseDirectory"/>）ではなく専用の一時フォルダに限定し、
/// <see cref="SelfUpdateInstaller"/>がそこから必要な6ファイルだけを個別にコピーする。
/// </summary>
public static class UpdateZipInspector
{
    /// <summary>検証結果。</summary>
    public sealed record ValidationOutcome
    {
        public required bool IsValid { get; init; }
        public string? ErrorMessage { get; init; }

        /// <summary>ファイル名 → ZIP内のエントリ名（FullName）。検証成功時のみ非null。</summary>
        public IReadOnlyDictionary<string, string>? EntryByFileName { get; init; }

        public static ValidationOutcome Ok(IReadOnlyDictionary<string, string> entryByFileName)
            => new() { IsValid = true, EntryByFileName = entryByFileName };

        public static ValidationOutcome Fail(string message) => new() { IsValid = false, ErrorMessage = message };
    }

    /// <summary>
    /// ZIPの中身が、<see cref="UpdateFiles.RequiredFileNames"/>と過不足なく一致するかを検証する。
    /// 展開は一切行わない。
    /// 失敗条件: (1) ZIPとして開けない、(2) 許可された6ファイル名以外のファイルが1つでもある、
    /// (3) 同じファイル名が複数のエントリに重複している、(4) 6ファイルのいずれかが欠けている。
    /// </summary>
    public static ValidationOutcome Validate(string zipPath)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zipPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return ValidationOutcome.Fail($"ダウンロードしたファイルをZIPとして開けませんでした: {ex.Message}");
        }

        using (archive)
        {
            var allowed = new HashSet<string>(UpdateFiles.RequiredFileNames, StringComparer.Ordinal);
            var entryByFileName = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var entry in archive.Entries)
            {
                // ディレクトリエントリ（末尾が '/'、中身が空）は無視する。
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue;
                }

                var fileName = Path.GetFileName(entry.FullName.Replace('\\', '/'));
                if (string.IsNullOrEmpty(fileName))
                {
                    continue;
                }

                if (!allowed.Contains(fileName))
                {
                    return ValidationOutcome.Fail(
                        $"配布物ZIPに想定外のファイルが含まれていたため、更新を中止しました: {entry.FullName}");
                }

                if (!entryByFileName.TryAdd(fileName, entry.FullName))
                {
                    return ValidationOutcome.Fail(
                        $"配布物ZIP内に同名のファイルが重複していたため、更新を中止しました: {fileName}");
                }
            }

            var missing = UpdateFiles.RequiredFileNames.Where(f => !entryByFileName.ContainsKey(f)).ToList();
            if (missing.Count > 0)
            {
                return ValidationOutcome.Fail(
                    $"配布物ZIPに必要なファイルが不足していたため、更新を中止しました: {string.Join(", ", missing)}");
            }

            return ValidationOutcome.Ok(entryByFileName);
        }
    }

    /// <summary>
    /// <see cref="Validate"/>が返したエントリだけを、<paramref name="destinationDir"/>直下へ
    /// フラットな構成（サブフォルダなし、ファイル名のみ）で展開する。<see cref="Validate"/>に
    /// 通っていない任意のZIPを渡さないこと（呼び出し元は必ずValidateを先に呼ぶ）。
    /// </summary>
    public static void ExtractTo(string zipPath, IReadOnlyDictionary<string, string> entryByFileName, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var (fileName, entryFullName) in entryByFileName)
        {
            var entry = archive.GetEntry(entryFullName)
                ?? throw new InvalidOperationException($"ZIP内にエントリが見つかりません: {entryFullName}");
            entry.ExtractToFile(Path.Combine(destinationDir, fileName), overwrite: true);
        }
    }
}
