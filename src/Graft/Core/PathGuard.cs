using System.IO;

namespace Graft.Core;

/// <summary>
/// PathGuardの動作設定。既定値は仕様書14章 <c>safety</c> セクションに対応する。
/// </summary>
public sealed record PathGuardOptions
{
    private static readonly IReadOnlyList<string> DefaultAllowedExtensions = new[]
    {
        ".py", ".js", ".ts", ".tsx", ".cs", ".java", ".go",
        ".rs", ".html", ".css", ".json", ".yaml", ".yml",
        ".md", ".sql", ".xml", ".txt",
    };

    /// <summary>許可する拡張子（先頭ドット付き）。既定は.exe/.dll/.bat/.ps1等を含まないテキスト系のみ。</summary>
    public IReadOnlyList<string> AllowedExtensions { get; init; } = DefaultAllowedExtensions;

    /// <summary>1ファイルあたりの最大サイズ（MB）。</summary>
    public int MaxFileSizeMB { get; init; } = 10;

    /// <summary>1リビジョンあたりの最大ファイル数。</summary>
    public int MaxFilesPerRevision { get; init; } = 200;

    /// <summary>仕様書14章どおりの既定設定。</summary>
    public static PathGuardOptions Default { get; } = new();
}

/// <summary>既存ファイルに対する追加検証結果。</summary>
public sealed record FileCheck
{
    /// <summary>解決済みの絶対パス。</summary>
    public required string FullPath { get; init; }

    /// <summary>ファイルが既に存在するか。</summary>
    public bool Exists { get; init; }

    /// <summary>読み取り専用属性が付いているか。</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>排他ロック中か。</summary>
    public bool IsLocked { get; init; }

    /// <summary>ファイルサイズ（バイト）。存在しない場合は0。</summary>
    public long SizeBytes { get; init; }
}

/// <summary>
/// プロジェクトルート外への書き込みを防ぐ経路検証機構（4.7節/13章）。
/// 正規化後の絶対パスとシンボリックリンク解決後の実パスの両方でルート内判定を行う。
/// </summary>
public sealed class PathGuard
{
    private readonly string _root;
    private readonly PathGuardOptions _options;

    public PathGuard(string projectRoot, PathGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        ArgumentNullException.ThrowIfNull(options);

        _root = NormalizeRoot(projectRoot);
        _options = options;
    }

    /// <summary>
    /// v1.0.14 実機不具合対応: パス解決の途中で「拡張表記（<c>\\?\</c>）のまま先へ進んだ」
    /// 「相対パスへ転落した」といった異常を検知したときに呼ばれる記録先。
    /// <para>
    /// PathGuardは<see cref="Graft.Core"/>層のクラスで、生成箇所が多く（ApplyEngine・
    /// FileTreeService・MainViewModel等）<see cref="Infra.Logger"/>を引き回していない。かつ
    /// <see cref="NormalizeRoot"/>はstaticメソッドとして単独でも呼ばれる
    /// （<see cref="Infra.EnvironmentSummaryLogger"/>）。そのため
    /// <see cref="Infra.SuppressedExceptionTracker.Shared"/>と同じ流儀（附録A.3: DIコンテナを
    /// 使わない）で、staticなフックとして公開し、起動時に一度だけ配線する
    /// （<c>Views/StartupCoordinator.cs</c>）。
    /// </para>
    /// <para>
    /// 「いつのまにか直った／壊れた」で終わらせないための記録であり、動作そのものには
    /// 影響しない（未設定でも判定・復元は同じように行われる）。
    /// </para>
    /// </summary>
    public static Action<string>? AnomalyLogger { get; set; }

    private static void ReportAnomaly(string message) => AnomalyLogger?.Invoke(message);

    /// <summary>
    /// v1.0.7実機不具合対応: projectRootは呼び出し元（MainViewModel等）がprojects.jsonから
    /// 読み込んだProject.Rootをそのまま渡す経路が複数あり、ProjectStore側の防御
    /// （RegisterAsync/RelocateAsync/LoadAsync）を経由しないまま渡される可能性がある。
    /// ここでも同じ復元をかけておくことで、万一拡張表記や化けたUNC表記のRootが渡っても
    /// カレントディレクトリ基準の誤った絶対パスへ解決してしまうことを防ぐ（LongPath.cs参照）。
    /// <para>
    /// コンストラクタ本体から切り出しているのは、環境要約ログ（v1.0.7、
    /// <see cref="Infra.EnvironmentSummaryLogger"/>参照）が「PathGuardが実際に使う正規化後の
    /// 値」をログへ残すために、PathGuardインスタンスを作らずこの正規化だけを呼びたいため。
    /// </para>
    /// <para>
    /// v1.0.14 実機不具合対応: <see cref="ResolveRealPath"/>の戻り値へ
    /// <see cref="LongPath.StripExtendedPrefix"/>を掛け、さらに「絶対パスであること」を
    /// 最後に確かめる。実機ログでは、マップ済みネットワークドライブ上のプロジェクトルート
    /// <c>Z:\営業部\...</c> を渡したときの本メソッドの戻り値が
    /// <c>C:\追加\Graft\UNC\gfs\inaden\営業部\...</c>（<c>C:\追加\Graft</c>はexeのフォルダ）に
    /// なっていた。<c>Path.GetFullPath</c>は<see cref="ResolveRealPath"/>より手前にしか無いため、
    /// カレントディレクトリが混ざったのは<see cref="ResolveRealPath"/>の内部
    /// （<see cref="ResolveIfLink"/>）である。詳しい経緯は<see cref="ResolveIfLink"/>のコメント参照。
    /// 絶対パスでなくなっていた場合は、リンク解決前の値（＝少なくとも絶対パスであることが
    /// 保証されている値）へ後退する。リンク解決はシンボリックリンク経由のルート外参照を
    /// 防ぐための補助であり、それを諦めてでも「まったく別の場所を指す壊れたルート」を
    /// 採用しない方が安全なため。
    /// </para>
    /// </summary>
    public static string NormalizeRoot(string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);

        var recoveredRoot = LongPath.RecoverProjectRoot(projectRoot);
        var normalizedRoot = NormalizeTrailingSeparator(Path.GetFullPath(recoveredRoot));

        var realRoot = LongPath.StripExtendedPrefix(ResolveRealPath(normalizedRoot));
        if (!IsAbsolutePath(realRoot))
        {
            ReportAnomaly(
                $"プロジェクトルートの実体解決が絶対パスになりませんでした。リンク解決前の値を使います。" +
                $" 入力={projectRoot} / 解決結果={realRoot} / 採用={normalizedRoot}");
            return normalizedRoot;
        }

        return NormalizeTrailingSeparator(realRoot);
    }

    /// <summary>相対パスを検証し、ルート内の絶対パスへ解決する。E201/E202/E206を返しうる。</summary>
    public GraftResult<string> Resolve(string relativePath) => Resolve(relativePath, checkExtension: true);

    /// <summary>
    /// フォルダの相対パスを検証し、ルート内の絶対パスへ解決する。
    /// 拡張子ホワイトリスト（13章）はファイルに対する規則のため、フォルダには適用しない。
    /// </summary>
    public GraftResult<string> ResolveDirectory(string relativePath) => Resolve(relativePath, checkExtension: false);

    /// <summary>
    /// エクスプローラへの既存ファイル・フォルダの取り込み（ドラッグ＆ドロップ・「ファイルを追加」）用に
    /// 相対パスを検証し、ルート内の絶対パスへ解決する。<see cref="ResolveDirectory"/>と同じく
    /// 拡張子ホワイトリストは適用しない。
    /// <para>
    /// 拡張子ホワイトリスト（<see cref="PathGuardOptions.AllowedExtensions"/>）は、AIやGraft自身が
    /// テキストとして新規に書き込む内容（<see cref="Resolve(string)"/>を使うパッチ適用・新規ファイル
    /// 作成）に対する安全策であり、.exe/.batのような実行可能ファイルをAIが誤って（あるいは
    /// 悪意を持って）書き込むことを防ぐ趣旨である。取り込みは利用者が明示的にファイル選択
    /// ダイアログやドラッグ＆ドロップで選んだ「既存の」ファイルをそのままコピーするだけの操作で、
    /// 画像・PDF等の非テキスト資産を持ち込みたいという要望（「素材の画像を放り込む」）が
    /// 主な動機のため、ホワイトリストの趣旨に合わない。ルート外への書き込み・シンボリックリンク
    /// 経由の脱出防止（本メソッドが検証する内容）は取り込みでも従来どおり必須のため適用する。
    /// </para>
    /// </summary>
    public GraftResult<string> ResolveImportTarget(string relativePath) => Resolve(relativePath, checkExtension: false);

    private GraftResult<string> Resolve(string relativePath, bool checkExtension)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "パスが空です", path: relativePath);
        }

        if (IsAbsolutePath(relativePath))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "絶対パスは許可されていません", path: relativePath);
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "上位ディレクトリの参照(..)は許可されていません", path: relativePath);
        }

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(_root, string.Join(Path.DirectorySeparatorChar, segments)));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return GraftResult<string>.Fail(ErrorCode.E201, $"不正なパスです: {ex.Message}", path: relativePath);
        }

        if (!IsWithinRoot(combined))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "パスがプロジェクトルート外です", path: relativePath);
        }

        // v1.0.14: 実体解決の結果に拡張表記（\\?\・\\?\UNC\）が混ざると、通常表記で保持している
        // _rootとの前方一致（IsWithinRoot）が必ず外れ、ルート内のファイルまでE201になってしまう。
        // 拡張表記はファイルAPI呼び出しの直前だけで使う、というLongPath.csの設計方針どおり、
        // ここで通常表記へ戻してから判定する。
        var real = LongPath.StripExtendedPrefix(ResolveRealPath(combined));
        if (!IsWithinRoot(real))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "シンボリックリンク経由でルート外を参照しています", path: relativePath);
        }

        if (checkExtension)
        {
            var extension = Path.GetExtension(combined);
            // 不具合2対応: 拡張子ホワイトリストは「.exe/.bat等の危険な拡張子を遮断する」ことが
            // 目的であり、"Dockerfile"やLICENSEのような拡張子そのものが無いファイル名は
            // 遮断対象の想定外だった（エクスプローラで拡張子なしのファイルを新規作成できない
            // 不具合の原因）。拡張子が付いている場合のみホワイトリストで判定する。
            if (extension.Length > 0 &&
                !_options.AllowedExtensions.Any(a => string.Equals(a, extension, StringComparison.OrdinalIgnoreCase)))
            {
                return GraftResult<string>.Fail(ErrorCode.E202, $"拡張子 '{extension}' は許可されていません", path: relativePath);
            }
        }

        if (LongPath.ExceedsExtendedLimit(combined))
        {
            return GraftResult<string>.Fail(ErrorCode.E206, "長パス対応のプレフィックス込みでも上限を超えています", path: relativePath);
        }

        return GraftResult<string>.Ok(combined);
    }

    /// <summary>既存ファイルに対する追加検証（サイズE203、ロックE204、読み取り専用E205）。</summary>
    public GraftResult<FileCheck> Inspect(string relativePath)
    {
        var resolved = Resolve(relativePath);
        if (!resolved.IsSuccess)
        {
            return GraftResult<FileCheck>.Fail(resolved.Issues);
        }

        var fullPath = resolved.Value;
        var ioPath = LongPath.Extended(fullPath);
        if (!File.Exists(ioPath))
        {
            var absent = new FileCheck { FullPath = fullPath, Exists = false, IsReadOnly = false, IsLocked = false, SizeBytes = 0 };
            return GraftResult<FileCheck>.Ok(absent);
        }

        var info = new FileInfo(ioPath);
        var isReadOnly = info.IsReadOnly;
        var sizeBytes = info.Length;
        var isLocked = IsLocked(ioPath);

        var check = new FileCheck { FullPath = fullPath, Exists = true, IsReadOnly = isReadOnly, IsLocked = isLocked, SizeBytes = sizeBytes };

        var issues = new List<GraftIssue>();
        var maxBytes = (long)_options.MaxFileSizeMB * 1024 * 1024;
        if (sizeBytes > maxBytes)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E203, $"サイズが上限（{_options.MaxFileSizeMB}MB）を超えています", path: relativePath));
        }
        if (isLocked)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E204, "ファイルが排他ロック中です", path: relativePath));
        }
        if (isReadOnly)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E205, "読み取り専用属性です", path: relativePath, severity: Severity.Warning));
        }

        if (issues.Any(i => i.Severity == Severity.Error))
        {
            return GraftResult<FileCheck>.Fail(issues);
        }

        return GraftResult<FileCheck>.Ok(check, issues);
    }

    /// <summary>1リビジョンあたりのファイル数上限判定（E203相当）。</summary>
    public GraftResult<bool> CheckFileCount(int count)
    {
        if (count > _options.MaxFilesPerRevision)
        {
            return GraftResult<bool>.Fail(ErrorCode.E203,
                $"1リビジョンあたりのファイル数上限（{_options.MaxFilesPerRevision}）を超えています");
        }
        return GraftResult<bool>.Ok(true);
    }

    private bool IsWithinRoot(string candidate)
    {
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedCandidate, _root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return normalizedCandidate.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocked(string ioPath)
    {
        try
        {
            using var stream = new FileStream(ioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 読み取り専用等のアクセス拒否はE205側で検出するためロック扱いにしない
            return false;
        }
    }

    private static bool IsAbsolutePath(string path)
    {
        if (path.StartsWith('/') || path.StartsWith('\\')) return true;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') return true;
        return false;
    }

    private static string NormalizeTrailingSeparator(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? fullPath : trimmed;
    }

    /// <summary>
    /// 既存のパス構成要素に含まれるシンボリックリンク／ジャンクションを最終的な実体まで
    /// 解決する。存在しない末尾部分（新規作成予定のファイル等）はそのまま維持する。
    /// </summary>
    private static string ResolveRealPath(string fullPath)
        => ResolveRealPathCore(fullPath, ResolveIfLink);

    /// <summary>
    /// <see cref="ResolveRealPath"/>の本体。リンク解決だけを引数として差し替え可能にしている
    /// （<see cref="LongPath.ExtendedCore"/>と同じ方針）。
    /// <para>
    /// v1.0.14 実機不具合対応: リンク解決の戻り値が相対パスだった場合に、それをそのまま
    /// 組み立て続けると最終結果まで相対パスになり、呼び出し元の<c>Path.GetFullPath</c>で
    /// カレントディレクトリと連結されてしまう（実機では
    /// <c>UNC\gfs\inaden\営業部\...</c> が <c>C:\追加\Graft\UNC\gfs\inaden\営業部\...</c> に
    /// なっていた）。**相対パスへ転落した時点で異常**とみなし、入力をそのまま返して
    /// 安全側に倒す。リンク解決はシンボリックリンク経由のルート外参照を防ぐための補助であり、
    /// 「解決できないので元のパスのまま扱う」は既存の失敗時の振る舞い
    /// （<see cref="ResolveIfLink"/>のcatch節）と同じで、意味論を変えない。
    /// </para>
    /// <para>
    /// 併せて、<c>Path.GetPathRoot</c>がルートを取れなかった場合（＝入力がそもそも絶対パスと
    /// 認識されていない）は、組み立てても相対パスにしかならないため、何もせず入力を返す。
    /// 従来はこの場合<c>current</c>が空文字から始まり、区切りで分解したセグメントを
    /// 順に連結して相対パスを組み上げてしまっていた。
    /// </para>
    /// </summary>
    internal static string ResolveRealPathCore(string fullPath, Func<string, string?> resolveIfLink)
    {
        if (string.IsNullOrEmpty(fullPath)) return fullPath;

        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (string.IsNullOrEmpty(root))
        {
            // 絶対パスとして認識できない入力。ここで組み立てを始めると相対パスしか作れない。
            ReportAnomaly($"実体解決の対象がルートを持たないパスでした。そのまま扱います: {fullPath}");
            return fullPath;
        }

        var rest = fullPath[root.Length..];
        var parts = rest.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);

            var resolved = resolveIfLink(current);
            if (resolved is null) continue;

            if (!IsAbsolutePath(resolved))
            {
                // 転落を検知。ここまでの組み立て結果ごと捨て、入力をそのまま返す。
                ReportAnomaly(
                    $"リンクの実体解決が相対パスを返したため、実体解決を打ち切って元のパスを使います。" +
                    $" リンク={current} / 解決結果={resolved} / 採用={fullPath}");
                return fullPath;
            }

            current = resolved;
        }

        return current;
    }

    /// <summary>
    /// パス構成要素1つがシンボリックリンク／ジャンクションなら、その最終的な実体のパスを返す。
    /// リンクでない場合・解決できない場合はnullを返し、呼び出し元は元のパスを使う。
    /// <para>
    /// 【v1.0.14 実機不具合対応（今回の真因）】 <c>Directory.ResolveLinkTarget(path,
    /// returnFinalTarget: true)</c> は、.NET内部で<c>GetFinalPathNameByHandle</c>を呼ぶ。この
    /// Win32 APIは<b>常に拡張表記</b>を返し、対象がネットワーク上（UNC共有・マップ済み
    /// ネットワークドライブの実体）の場合は <c>\\?\UNC\サーバ名\共有名\...</c> という
    /// <b>拡張UNC表記</b>になる。ところが .NET 8 の実装
    /// （<c>System.Private.CoreLib</c> の <c>System.IO.FileSystem.GetFinalLinkTarget</c>。
    /// win-x64ランタイム 8.0.29 を逆コンパイルして確認）は、呼び出し側が渡したパスが拡張表記で
    /// なければ<b>戻り値の先頭4文字を無条件に切り落とす</b>:
    /// <code>
    /// int num2 = ((!PathInternal.IsExtended(linkPath.AsSpan())) ? 4 : 0);
    /// return new string(array, num2, (int)num - num2);
    /// </code>
    /// これは <c>\\?\C:\...</c> → <c>C:\...</c> を意図した処理だが、<c>\\?\UNC\...</c> に対しては
    /// <c>UNC\サーバ名\共有名\...</c> という<b>相対パスに見える文字列</b>を生む。
    /// <see cref="FileSystemInfo.ToString"/>（＝<c>OriginalPath</c>。同じ逆コンパイルで確認）は
    /// この生の文字列を返すが、<see cref="FileSystemInfo.FullName"/> は
    /// <c>Path.GetFullPath</c>済み——すなわち<b>カレントディレクトリと連結済み</b>——のため、
    /// 従来のように<c>FullName</c>を使うと <c>C:\追加\Graft\UNC\gfs\inaden\営業部</c>
    /// （<c>C:\追加\Graft</c>はexeのフォルダ）になってしまう。実機ログの
    /// <c>PathGuard.NormalizeRoot</c> の戻り値がまさにこの形だった。
    /// </para>
    /// <para>
    /// 【対応】 <c>FullName</c>（連結済みで手遅れの値）ではなく、まず
    /// <see cref="FileSystemInfo.ToString"/>が返す生の値を見る。生の値であれば
    /// <see cref="LongPath.RecoverProjectRoot"/>——v1.0.7で入れた「<c>UNC\...</c> の頭に
    /// <c>\\</c>を補う」「<c>\\?\</c>を剥がす」処理——がそのまま正しく効く
    /// （v1.0.7の時点では「先頭4文字が失われる」発生源が不明だったが、それがここだった）。
    /// 生の値から絶対パスを得られない場合だけ<c>FullName</c>へ後退し、それでも絶対パスに
    /// ならなければnull（＝リンクとして解決しない）を返す。
    /// </para>
    /// <para>
    /// なお、この化け方が起きるのは対象がネットワーク上にある場合だけである
    /// （ローカルなら <c>\\?\C:\...</c> → 4文字落として <c>C:\...</c> で正しい）。ローカル
    /// プロジェクトでだけ不具合が再現しなかった実機報告と整合する。
    /// <b>ここは Windows 実機でしか実行確認できない</b>（Linuxではネットワークパスも
    /// <c>GetFinalPathNameByHandle</c>も存在しない）。文字列処理として切り出した
    /// <see cref="SanitizeLinkTarget"/>・<see cref="ResolveRealPathCore"/>は単体テストで固定し、
    /// 実際にこの経路を通ることの確認は実機に委ねる。
    /// </para>
    /// </summary>
    private static string? ResolveIfLink(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                if (info.LinkTarget is not null)
                {
                    return SanitizeLinkTarget(Directory.ResolveLinkTarget(path, returnFinalTarget: true));
                }
            }
            else if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not null)
                {
                    return SanitizeLinkTarget(File.ResolveLinkTarget(path, returnFinalTarget: true));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // リンク解決に失敗した場合は元のパスを維持する（後続のルート内判定に委ねる）
        }

        return null;
    }

    /// <summary>
    /// <see cref="ResolveIfLink"/>が受け取った<see cref="FileSystemInfo"/>から、実際に使う
    /// パス文字列を取り出す。<see cref="SanitizeLinkTarget(string?, string?)"/>へ
    /// 「生の値（<see cref="FileSystemInfo.ToString"/>＝OriginalPath）」と
    /// 「絶対パス化済みの値（<see cref="FileSystemInfo.FullName"/>）」を渡すだけの薄い橋渡し。
    /// 判断そのものを文字列だけの純粋な関数へ寄せることで、Windows実機でしか作れない
    /// <see cref="FileSystemInfo"/>を用意しなくても単体テストで固定できるようにしている。
    /// </summary>
    private static string? SanitizeLinkTarget(FileSystemInfo? target)
        => target is null ? null : SanitizeLinkTarget(target.ToString(), target.FullName);

    /// <summary>
    /// リンクの実体として .NET が返した値を、Graftが安全に使える絶対パスへ整える。
    /// 経緯と根拠は<see cref="ResolveIfLink"/>のコメント参照。
    /// </summary>
    /// <param name="rawTarget">
    /// <see cref="FileSystemInfo.ToString"/>（OriginalPath）。.NETが
    /// <c>GetFinalPathNameByHandle</c>の結果から先頭4文字を落としただけの生の文字列で、
    /// ネットワーク対象では <c>UNC\サーバ名\共有名\...</c> になりうる。
    /// </param>
    /// <param name="fullNameTarget">
    /// <see cref="FileSystemInfo.FullName"/>。<paramref name="rawTarget"/>を
    /// <c>Path.GetFullPath</c>したもので、相対パスだった場合は<b>既にカレントディレクトリと
    /// 連結されてしまっている</b>。そのため第一候補にはしない。
    /// </param>
    /// <returns>絶対パスとして使える実体のパス。得られない場合はnull（リンクとして解決しない）。</returns>
    internal static string? SanitizeLinkTarget(string? rawTarget, string? fullNameTarget)
    {
        if (!string.IsNullOrEmpty(rawTarget))
        {
            // 生の値。UNC\... なら \\ を補い、\\?\ ・ \\?\UNC\ は剥がす（LongPath参照）。
            var recovered = LongPath.RecoverProjectRoot(rawTarget);
            if (IsAbsolutePath(recovered)) return recovered;

            ReportAnomaly(
                $"リンクの実体解決が絶対パスにならない値を返しました。FullNameへ後退します:" +
                $" 生の値={rawTarget} / 復元後={recovered}");
        }

        if (!string.IsNullOrEmpty(fullNameTarget))
        {
            // 後退経路。少なくとも拡張表記のまま後続の判定へ渡さないようにする
            // （_rootは通常表記で保持する、というLongPath.csの設計方針に合わせるため）。
            var stripped = LongPath.StripExtendedPrefix(fullNameTarget);
            if (IsAbsolutePath(stripped)) return stripped;

            ReportAnomaly($"リンクの実体解決の結果が絶対パスではありませんでした: {fullNameTarget}");
        }

        return null;
    }
}
