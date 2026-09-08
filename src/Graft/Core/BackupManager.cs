using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Graft.Infra;

namespace Graft.Core;

/// <summary>
/// リビジョン適用開始時のバックアップフォルダ作成を担う。仕様書2.2・6.3。
/// <c>back/&lt;プロジェクトID&gt;/r&lt;番号&gt;_&lt;yyyyMMdd_HHmmss&gt;/</c> フォルダを作成し、
/// <c>status: "in_progress"</c> の manifest.json を書いてから <see cref="BackupSession"/> を返す。
/// 個々のファイルの退避は <see cref="BackupSession"/> 側の責務とする。
/// </summary>
public sealed class BackupManager
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _jsonStore = new();
    private readonly RevisionIndex _revisionIndex;

    public BackupManager(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _revisionIndex = new RevisionIndex(paths);
    }

    /// <summary>
    /// バックアップフォルダを作成し、<c>status: "in_progress"</c> の manifest.json を
    /// 書き込んでから <see cref="BackupSession"/> を返す。失敗時は E401。
    /// </summary>
    public async Task<GraftResult<BackupSession>> BeginAsync(
        string projectId, string projectRoot, RevisionManifest initial, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return GraftResult<BackupSession>.Fail(ErrorCode.E401, "プロジェクトIDが空です");
        }
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return GraftResult<BackupSession>.Fail(ErrorCode.E401, "プロジェクトルートが空です");
        }

        var appliedAt = initial.AppliedAt == default ? DateTimeOffset.Now : initial.AppliedAt;
        var folderName = AppPaths.BuildRevisionFolderName(initial.Revision, appliedAt);
        var folderPath = _paths.GetRevisionDirectory(projectId, folderName);

        try
        {
            Directory.CreateDirectory(LongPath.Extended(folderPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<BackupSession>.Fail(
                ErrorCode.E401, $"バックアップフォルダを作成できません: {ExceptionMessages.Describe(ex)}", path: folderPath);
        }

        var manifest = initial with { Status = RevisionStatus.InProgress, AppliedAt = appliedAt };
        var manifestPath = _paths.GetManifestFilePath(projectId, folderName);

        try
        {
            await _jsonStore.WriteAsync(manifestPath, manifest, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<BackupSession>.Fail(
                ErrorCode.E401, $"manifest.json の書き込みに失敗しました: {ExceptionMessages.Describe(ex)}", path: manifestPath);
        }

        var session = new BackupSession(_jsonStore, _revisionIndex, projectId, projectRoot, folderPath, manifestPath, initial.Revision);
        return GraftResult<BackupSession>.Ok(session);
    }
}

/// <summary>
/// バックアップフォルダ・相対パス操作の共通処理。<see cref="BackupManager"/>・
/// <see cref="BackupSession"/>・<see cref="RevisionStore"/>・<see cref="RevisionRestorer"/> から使う。
/// </summary>
internal static class BackupPathUtil
{
    private static readonly Regex FolderNamePattern = new(@"^r(\d+)_(\d{8}_\d{6})$", RegexOptions.Compiled);

    /// <summary>フォルダ名（例: r24_20260804_143052）からリビジョン番号と適用日時を取り出す。</summary>
    public static (int Revision, DateTimeOffset AppliedAt)? TryParseFolderName(string folderName)
    {
        var match = FolderNamePattern.Match(folderName);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
        {
            return null;
        }
        if (!DateTime.TryParseExact(
                match.Groups[2].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return null;
        }
        return (revision, new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)));
    }

    /// <summary>ディレクトリ配下の全ファイルサイズ合計をバイト単位で返す。</summary>
    public static long ComputeDirectorySize(string directoryPath)
    {
        if (!Directory.Exists(directoryPath)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // サイズ取得に失敗しても集計は継続する（世代管理の目安値のため）
            }
        }
        return total;
    }

    /// <summary>
    /// 退避したプロジェクトファイルを格納するサブフォルダ名（新レイアウト、v1.0.16〜）。
    ///
    /// 【実機不具合の原因と、なぜサブフォルダへ分離するか】 以前はこのサブフォルダを設けず、
    /// 退避ファイルもリビジョンフォルダ直下（<see cref="AppPaths.GetRevisionDirectory(string,string)"/>
    /// が返す場所そのもの）へ相対パス構造を保ったまま平置きしていた。ところが
    /// リビジョンのメタデータ（<see cref="AppPaths.GetManifestFilePath"/>）も同じフォルダの
    /// 直下に <c>manifest.json</c> という名前で置かれるため、プロジェクト自身の直下に
    /// <c>manifest.json</c> というファイルがある場合（Chrome/Edge拡張の開発が典型例）、
    /// 退避先のパスがメタデータのパスと完全に一致してしまう。実機では次の順で互いを
    /// 上書きし合うことを実測で確認した: (1) <see cref="BackupManager.BeginAsync"/> が
    /// <c>status: "in_progress"</c> のメタデータを書く → (2) <see cref="BackupSession.StoreAsync"/>
    /// がプロジェクトの manifest.json を同じパスへコピーし(1)を潰す → (3) 適用確定時に
    /// 最終メタデータが同じパスへ書かれ(2)の退避コピーを潰す → (4) 取り消し時に
    /// <see cref="BackupSession.RestoreOneAsync"/> がこの壊れた「実体はメタデータ」の
    /// ファイルを読んでプロジェクトへ書き戻し、利用者のmanifest.jsonをGraftの内部データで
    /// 上書きしてしまう。壊れるのは名前が一致する manifest.json だけで、他のファイルは
    /// 正しく退避・復元できていた（衝突条件が「ファイル名が一致すること」そのものだった
    /// ため）。
    ///
    /// この種の衝突は「manifest.json」という名前に固有の問題ではなく、
    /// 「リビジョンフォルダ直下にメタデータ以外の何かを置く」構造そのものに起因するため、
    /// ファイル名で個別に回避するのではなく、退避ファイル一式をこのサブフォルダへ
    /// 物理的に分離した。名前は利用者のプロジェクトに実在しうる相対パスの「先頭の1段」に
    /// 一致してもかまわない（例えばプロジェクト自身に <c>files/</c> フォルダがあっても
    /// <c>files/files/…</c> という形で入れ子になるだけで、メタデータの置き場所
    /// （リビジョンフォルダ直下）とは階層が1段ずれるため構造的に衝突しない）。
    /// </summary>
    public const string FilesSubfolderName = "files";

    /// <summary>
    /// 退避ファイルの新規書き込み先の絶対パスを返す（<paramref name="revisionFolder"/>/files/
    /// <paramref name="normalizedRelativePath"/>）。新規に作成するバックアップは常にこちらへ書く。
    /// </summary>
    public static string GetBackupFilePathForWrite(string revisionFolder, string normalizedRelativePath)
        => Path.Combine(revisionFolder, FilesSubfolderName, normalizedRelativePath);

    /// <summary>
    /// 退避ファイルの読み取り先を解決する。新レイアウト（<see cref="FilesSubfolderName"/>配下）を
    /// 優先し、そちらに実体が無ければ旧レイアウト（リビジョンフォルダ直下のフラット配置）へ
    /// 後退する。
    ///
    /// 【なぜレイアウト移行スクリプトを書かず、読み取り側の後退で対応するか】 利用者の環境には
    /// 既にレイアウト変更前の（フラット配置の）バックアップが大量に存在する。これらを新レイアウトへ
    /// 一括で移し替えるスクリプトを書いて実行することも考えられるが、移行処理自体が失敗した場合
    /// （ディスク容量不足・権限不足・処理中のクラッシュ等）に既存の全バックアップを巻き添えで
    /// 壊しかねず、被害が今回の不具合よりはるかに大きくなる。読み取り側が新旧どちらの配置にも
    /// 対応する形にしておけば、ファイルは一切移動させずに済み、この種の被害が原理的に発生しない。
    /// </summary>
    public static string ResolveBackupFilePathForRead(string revisionFolder, string normalizedRelativePath)
    {
        var newPath = GetBackupFilePathForWrite(revisionFolder, normalizedRelativePath);
        return File.Exists(LongPath.Extended(newPath))
            ? newPath
            : Path.Combine(revisionFolder, normalizedRelativePath);
    }

    /// <summary>
    /// 退避ファイルを読み取る。新旧レイアウトの解決（<see cref="ResolveBackupFilePathForRead"/>）・
    /// 存在確認（E405）・読み取り失敗（E402）に加え、旧レイアウト特有の「退避コピーが
    /// メタデータ確定処理に上書きされて壊れている」ケース（E216）もここで検出する。
    /// <see cref="BackupSession.RestoreOneAsync"/>・<see cref="RevisionRestorer"/>の
    /// 復元系メソッドが共通で使う、退避ファイル読み取りの単一の入口。
    /// </summary>
    public static async Task<GraftResult<byte[]>> ReadBackupFileAsync(
        string revisionFolder, string normalizedRelativePath, CancellationToken ct)
    {
        var newPath = GetBackupFilePathForWrite(revisionFolder, normalizedRelativePath);
        var isNewLayout = File.Exists(LongPath.Extended(newPath));
        var resolvedPath = isNewLayout ? newPath : Path.Combine(revisionFolder, normalizedRelativePath);
        var resolvedIo = LongPath.Extended(resolvedPath);

        if (!File.Exists(resolvedIo))
        {
            return GraftResult<byte[]>.Fail(ErrorCode.E405, "退避ファイルが見つかりません", path: normalizedRelativePath);
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(resolvedIo, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<byte[]>.Fail(
                ErrorCode.E402, $"退避ファイルの読み取りに失敗しました: {ExceptionMessages.Describe(ex)}", path: normalizedRelativePath);
        }

        if (!isNewLayout && LooksOverwrittenByManifestMetadata(normalizedRelativePath, bytes))
        {
            return GraftResult<byte[]>.Fail(
                ErrorCode.E216,
                "旧レイアウト（v1.0.15以前）のバックアップで、このファイルの退避コピーが" +
                "Graftのリビジョン確定処理によって上書きされており、元の内容は失われています。" +
                "書き込むとファイルを壊すため復元を行いませんでした。Gitなど、Graft以外の" +
                "バックアップ手段からこのファイルを復元してください。",
                path: normalizedRelativePath);
        }

        return GraftResult<byte[]>.Ok(bytes);
    }

    /// <summary>
    /// 旧レイアウトの退避ファイルが、実際にはリビジョンのメタデータ（manifest.json確定処理）に
    /// 上書きされてしまっている（＝退避コピーとして壊れている）ことを検出する。
    ///
    /// 判定は2段構えにしている。
    /// (1) 相対パスが（サブフォルダを含まず）ちょうど<c>manifest.json</c>であること。
    ///     <see cref="FilesSubfolderName"/>のコメントに記載の衝突条件そのもの
    ///     （<see cref="AppPaths.GetManifestFilePath"/>と同じファイル名がリビジョンフォルダ
    ///     直下に来る唯一のケース）であり、<c>sub/manifest.json</c>のようにサブフォルダ配下は
    ///     この構造的な衝突を起こさないため対象外にする。
    /// (2) 読み込んだ内容がJSONオブジェクトとして解析でき、かつ<see cref="RevisionManifest"/>の
    ///     主要キー（revision/projectId/appliedAt/patchHash/entriesのいずれか。DefaultOptionsの
    ///     camelCase命名規則に合わせた表記）を1つでも持つこと。
    ///     (1)だけで判定を打ち切らない理由: 退避コピーの書き込み（StoreAsync）自体は成功したが、
    ///     その後の確定書き込み（メタデータでの上書き）がクラッシュ等で走らないまま終わった場合、
    ///     旧レイアウトかつパスがmanifest.jsonであっても、退避コピーの中身は実際には
    ///     利用者のmanifest.jsonのままで壊れていない（正しく復元できる）。(1)だけで拒否すると、
    ///     この正当なケースまで復元不能扱いにしてしまう。Chrome拡張のmanifest.json等が
    ///     たまたま revision/projectId のようなキーを持つ可能性は極めて低いため、
    ///     誤検出のリスクより見逃しのリスク（内部データを利用者のファイルへ書き込んでしまう）を
    ///     避けることを優先する。
    /// </summary>
    public static bool LooksOverwrittenByManifestMetadata(string normalizedRelativePath, byte[] content)
    {
        if (!string.Equals(normalizedRelativePath, "manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return doc.RootElement.TryGetProperty("revision", out _)
                || doc.RootElement.TryGetProperty("projectId", out _)
                || doc.RootElement.TryGetProperty("appliedAt", out _)
                || doc.RootElement.TryGetProperty("patchHash", out _)
                || doc.RootElement.TryGetProperty("entries", out _);
        }
        catch (JsonException)
        {
            // JSONとして解析できない = リビジョンのメタデータではない
            // （利用者のmanifest.jsonが非JSON、または壊れたJSONの場合はここに来るが、
            // いずれにせよ「メタデータに上書きされた」ケースではないため誤検出しない）。
            return false;
        }
    }

    /// <summary>
    /// 相対パスを検証し、区切り文字をOS標準へ揃えて返す。絶対パスと上位参照(..)を拒否する。
    /// </summary>
    public static GraftResult<string> NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "パスが空です", path: relativePath);
        }
        if (IsAbsolute(relativePath))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "絶対パスは許可されていません", path: relativePath);
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s == ".."))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "不正な相対パスです", path: relativePath);
        }

        return GraftResult<string>.Ok(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private static bool IsAbsolute(string path)
    {
        if (path.StartsWith('/') || path.StartsWith('\\')) return true;
        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
    }
}
