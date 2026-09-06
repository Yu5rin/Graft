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

    /// <summary>
    /// v1.0.15 セキュリティ対応: 拡張子を持たないファイル名のうち、書き込みを許可するものの一覧。
    /// <para>
    /// 【なぜ必要か】 従来は「<c>Path.GetExtension</c> が空文字なら拡張子ホワイトリストの判定
    /// そのものを飛ばす」という作りだった（<c>Dockerfile</c> や <c>LICENSE</c> を通すための対応）。
    /// しかしこれは「拡張子が無い名前は<b>すべて</b>無条件に通る」という意味になり、
    /// <c>.git/hooks/pre-commit</c>・<c>Makefile</c>・<c>script</c>・<c>CON</c> といった名前が
    /// そのまま書き込めることを実測で確認した。拡張子が無いこと自体は「安全である根拠」に
    /// まったくならない（実行権限を付ければ動く）。そこで<b>飛ばす</b>のをやめ、
    /// <b>名前そのものの許可リスト</b>で判定する。
    /// </para>
    /// <para>
    /// 【何を入れて、何を入れないか】 入れているのは「利用者が明示的にビルド／実行コマンドを
    /// 叩いたときだけ読まれる」ファイルである（<c>make</c>・<c>docker build</c>・<c>rake</c> 等）。
    /// これは既に許可している <c>.py</c> や <c>.js</c> と同じ性質で、書けること自体が
    /// 直ちに実行を意味しない。逆に<b>入れていない</b>のは、利用者が何も指示しなくても
    /// 勝手に走る種類のもの——<c>.git/hooks/*</c>（Gitの操作で自動実行。そもそも
    /// <see cref="PathGuard"/>が<c>.git</c>配下ごと拒否する）や、
    /// <c>.bashrc</c>・<c>.profile</c>・<c>.envrc</c>（シェルやdirenvが起動時に自動で読み込む）
    /// である。この線引きが、今回のセキュリティ点検で見つかった問題の再発を防ぐ基準になる。
    /// </para>
    /// <para>
    /// 【ドットで始まる名前を同じ一覧に入れている理由】 <c>Path.GetExtension(".gitignore")</c> は
    /// <c>".gitignore"</c>（＝名前全体）を返すため、拡張子ホワイトリストで判定すると必ず外れる。
    /// 実際、修正前は <c>Makefile</c> が通るのに <c>.gitignore</c> が E202 で拒否されるという
    /// 不整合が起きていた。<c>.gitignore</c> は<b>プロジェクト直下の普通のファイル</b>であり、
    /// <c>.git</c> フォルダとは別物なので許可されるべきである。ドットで始まり他にドットを
    /// 含まない名前は「実質的に拡張子を持たない名前」として、同じ許可リストで判定する
    /// （<see cref="PathGuard"/>の判定コード参照）。
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> DefaultAllowedExtensionlessNames = new[]
    {
        // ビルド・実行の定義ファイル（利用者が明示的にコマンドを叩いたときだけ読まれる）
        "Dockerfile", "Containerfile", "Makefile", "GNUmakefile",
        "Rakefile", "Gemfile", "Procfile", "Jenkinsfile", "Vagrantfile", "Brewfile", "Justfile",
        // 説明・法務系のテキスト（拡張子を付けない慣習が強いもの）
        "LICENSE", "LICENCE", "COPYING", "NOTICE", "README", "CHANGELOG", "HISTORY",
        "AUTHORS", "CONTRIBUTORS", "CONTRIBUTING", "TODO", "VERSION", "CODEOWNERS",
        // ドットで始まる設定ファイル（自動実行されない、宣言的な設定のみ）
        ".gitignore", ".gitattributes", ".gitmodules", ".gitkeep",
        ".editorconfig", ".dockerignore", ".npmignore", ".npmrc",
        ".prettierrc", ".prettierignore", ".eslintrc", ".eslintignore",
        ".env",
    };

    /// <summary>許可する拡張子（先頭ドット付き）。既定は.exe/.dll/.bat/.ps1等を含まないテキスト系のみ。</summary>
    public IReadOnlyList<string> AllowedExtensions { get; init; } = DefaultAllowedExtensions;

    /// <summary>
    /// 拡張子を持たないファイル名のうち書き込みを許可するもの（<c>Dockerfile</c>・<c>Makefile</c>・
    /// <c>.gitignore</c> 等）。判定は大文字小文字を無視する。既定値の選定理由は
    /// <see cref="DefaultAllowedExtensionlessNames"/> のコメント参照。
    /// <para>
    /// 設定画面には出していない（<see cref="Infra.SafetySettings"/>に対応する項目を作っていない）。
    /// 拡張子と違って利用者が増やしたくなる場面がまだ見えておらず、増やせる口を先に作ると
    /// 「安全側の既定」を素通りさせる手段だけが残るため。プロジェクト個別の要求が出てきたら、
    /// <see cref="AllowedExtensions"/>と同じ経路（<see cref="Features.ProjectOverrideResolver"/>）で
    /// 足せるように、ここは<c>init</c>で差し替え可能にしてある。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AllowedExtensionlessNames { get; init; } = DefaultAllowedExtensionlessNames;

    /// <summary>
    /// v1.0.15 セキュリティ対応: 配下への書き込みを常に拒否する、バージョン管理システムの
    /// 内部フォルダ名。判定は大文字小文字を無視する（Windowsでは <c>.GIT</c> も同じ場所を指す）。
    /// 理由は<see cref="PathGuard"/>の該当判定のコメント参照。
    /// </summary>
    public IReadOnlyList<string> DeniedDirectorySegments { get; init; } = new[] { ".git", ".hg", ".svn" };

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
/// <para>
/// v1.0.14: リンクの実体解決は<b>ルートより下の構成要素だけ</b>を対象にする
/// （<see cref="ResolveRealPathBelowRootCore"/>）。ルート自身の実体解決は
/// <see cref="NormalizeRoot"/>が構築時に1回だけ行い、以後はその結果を起点にする。
/// 判定が外れた場合の第二の砦として、ルートの実体（<see cref="IsWithinRealRoot"/>）とも
/// 突き合わせる。いずれもルート外を指すリンクの拒否は従来どおりで、緩めていない。
/// </para>
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
    /// セキュリティ点検（v1.0.15）指摘対応: データ保存先（<see cref="Infra.AppPaths.BaseDirectory"/>。
    /// settings.json・projects.json・back/（バックアップ・履歴の実体）・logs/等が置かれる場所）の
    /// 絶対パス。設定されていれば、<see cref="Resolve"/>は「プロジェクトルート自身がデータ保存先と
    /// 同じか、データ保存先を配下に含んでいる」場合に、そのプロジェクトへのあらゆる書き込みを
    /// 拒否する（<see cref="ErrorCode.E213"/>）。
    /// <para>
    /// 【判定基準はプロジェクトルート単位であり、個々のファイルパスではない】 危険なのは
    /// 「settings.json・projects.json（データ保存先の直下）が、あるプロジェクトの<see cref="Resolve"/>で
    /// 到達可能かどうか」であり、これが成り立つのは<b>プロジェクトルートがデータ保存先と同じか、
    /// その祖先（親・祖先フォルダ）である場合だけ</b>である。データ保存先の内側にプロジェクト
    /// ルートを置くケース（例: ポータブル運用でexeフォルダの直下に作業用サブフォルダを作る）は
    /// 逆に安全側であり誤って拒否してはならない——<see cref="Resolve"/>は結合後のパスが
    /// プロジェクトルート自身の配下であることを別途要求する（<c>IsWithinRoot</c>・上位ディレクトリ
    /// 参照<c>..</c>の禁止）ため、そのプロジェクトルートの兄弟であるsettings.json等へは
    /// そもそも<c>..</c>無しに到達できない。実際にこの逆方向まで拒否する実装を一度入れたところ、
    /// 「exeフォルダの直下にサンプルプロジェクトを作る」という既存の単体テスト
    /// （<c>OnboardingProjectRegistrationTests</c>）が壊れることで発覚した（実測）。
    /// </para>
    /// <para>
    /// 【なぜ登録時チェック（<see cref="Features.ProjectStore.RegisterAsync"/>）だけでは
    /// 不十分か】 <c>.json</c>はProjectStoreの拡張子ホワイトリストに含まれる一般的な拡張子
    /// （<see cref="Infra.Settings"/>のAllowedExtensions参照）であり、データ保存先（または
    /// それを含むフォルダ）を誤って、あるいは悪意を持ってプロジェクト登録すると、
    /// <c>projects.json</c>や<c>settings.json</c>自体をパッチの書き込み対象にできてしまう。
    /// 前者は<c>postApplyHooks</c>を、後者は<c>update.checkUrl</c>を差し替える経路になり、
    /// 次回の適用やフック実行・更新確認で任意コード実行につながりうる。登録時チェックだけだと
    /// 「登録後にデータ保存先を設定画面からユーザーフォルダへ移動した」ケース（両者が事後的に
    /// 重なる）を検知できないため、実際の書き込み経路であるここ（<see cref="PathGuard"/>）で
    /// 経路によらず一貫して拒否する方式にした。
    /// </para>
    /// <para>
    /// <see cref="AnomalyLogger"/>と同じ理由（附録A.3: DIコンテナを使わず、生成箇所が多い
    /// <see cref="Graft.Core"/>層のクラスへ<see cref="Infra.AppPaths"/>を引き回さない）で、
    /// staticなアンビエント設定として公開し、起動時に一度だけ配線する
    /// （<c>Views/StartupCoordinator.cs</c>）。未設定（null）の間は従来どおり制限なし
    /// （単体テストや配線前の一時的な状態でプロジェクトルート内の操作まで巻き込んで
    /// 拒否してしまわないため）。
    /// </para>
    /// </summary>
    public static string? ProtectedDataDirectory { get; set; }

    /// <summary>
    /// このガードの<c>_root</c>自身が<see cref="ProtectedDataDirectory"/>と同じか、
    /// それを配下に含む（＝データ保存先の祖先である）かどうか。未設定（null・空）なら常にfalse。
    /// trueなら、このプロジェクトルートに対するすべての<see cref="Resolve"/>を拒否する
    /// （個々の相対パスによらない。クラスコメント参照）。
    /// </summary>
    private bool RootOverlapsProtectedDataDirectory()
    {
        var dataDirectory = ProtectedDataDirectory;
        if (string.IsNullOrEmpty(dataDirectory)) return false;

        string normalizedDataDirectory;
        try
        {
            normalizedDataDirectory = NormalizeTrailingSeparator(Path.GetFullPath(dataDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // ProtectedDataDirectory自体が解決できない異常値なら、判定しようがないため
            // 安全側（制限なし）ではなく「誤検知で全拒否」も避け、単に判定をスキップする。
            // 通常はAppPaths.BaseDirectory（起動時に検証済み）を渡すため到達しない想定。
            return false;
        }

        // IsWithin(candidate, root)は「candidateがrootと同じか配下か」を見る関数なので、
        // 引数を逆に渡すと「データ保存先が_rootと同じか配下か」＝「_rootがデータ保存先の
        // 祖先（または本人）か」の判定になる。
        return IsWithin(normalizedDataDirectory, _root);
    }

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
        // セキュリティ点検（v1.0.15）指摘対応: プロジェクトルート自体がデータ保存先と同じか
        // その祖先だった場合、settings.json・projects.json自体がこのプロジェクトの書き込み対象に
        // なってしまう。個々の相対パスの中身によらずプロジェクト全体を拒否する
        // （ProtectedDataDirectory・RootOverlapsProtectedDataDirectoryのコメント参照）。
        if (RootOverlapsProtectedDataDirectory())
        {
            return GraftResult<string>.Fail(ErrorCode.E213, "プロジェクトルートがデータ保存先と重なっています", path: relativePath);
        }

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

        // v1.0.15 セキュリティ対応（穴3）: セグメントに ':' を含むパスを、パスを組み立てる前に拒否する。
        //
        // 【なぜ要るか】 Windowsの代替データストリーム（NTFSのADS）の記法 <ファイル名>:<ストリーム名>
        // により、拡張子ホワイトリストを回避できることを実測で確認した。
        //   evil.exe:payload.txt → Path.GetExtension は ".txt" を返す → 許可拡張子として通過
        //   payload.txt:hidden   → ".txt:hidden"                      → E202（たまたま拒否）
        //   a.txt::$DATA         → ".txt::$DATA"                      → E202（たまたま拒否）
        // つまり「危険な拡張子の実体を書き込みたい」側から見れば、ストリーム名の末尾を
        // 許可拡張子で終わらせるだけでホワイトリストを素通りできる。ホワイトリストの側を
        // いくら整えても、拡張子の切り出し規則そのものを悪用されるため塞げない。
        //
        // 【なぜ正常な用途を壊さないか】 Windowsではファイル名に ':' を使えない（予約文字）。
        // Linux/macOSでは使えるが、Graftが対象とするプロジェクト（Windowsとの往復を前提とした
        // ソースツリー）でファイル名に ':' を使うことは実質的に無い。両OSで同じ結論になる方が
        // 「Windows実機だけ挙動が違う」事故を生まないため、プラットフォームで分岐せず一律で拒否する。
        //
        // 【なぜ絶対パス判定より後か】 絶対パス（"C:\..."）はドライブ文字のところで ':' を含む。
        // 先にこの判定を置くと、絶対パスが E201（ルート外）ではなく E212 になってしまい、
        // 従来のエラー表示と食い違う。絶対パスは手前の IsAbsolutePath で既に弾いてある。
        //
        // 【実機確認の限界（正直な報告）】 Windows実機で Path.GetFullPath(@"C:\proj\evil.exe:payload.txt")
        // が何を返し、実際にADSとして書き込まれるかまでは未確認である（開発環境がLinuxのため）。
        // ただし「拡張子の切り出しがホワイトリストを回避する」ことはLinux上でも実測済みで、
        // 塞ぐこと自体に正常な用途への害が無いため、実機確認を待たずに塞ぐ。
        var colonSegment = segments.FirstOrDefault(s => s.Contains(':'));
        if (colonSegment is not null)
        {
            return GraftResult<string>.Fail(
                ErrorCode.E212,
                $"パスの構成要素 '{colonSegment}' に使用できない文字 ':' が含まれています",
                path: relativePath);
        }

        // v1.0.15 セキュリティ対応（穴1・最優先）: バージョン管理システムの内部フォルダ配下への
        // 書き込みを常に拒否する。
        //
        // 【なぜ最優先か】 .git 配下へ書けることは、実質的に「利用者の環境での任意コード実行」である。
        //   - git は対象リポジトリの .git/config を読む。core.fsmonitor 等の設定値はコマンドとして
        //     実行されるため、config を書き換えられた時点で、以後の任意の git 実行が乗っ取られる。
        //     Graftは editor.gitGutter が既定オンで（Infra/Settings.cs）、GitIntegration が
        //     プロジェクトルートを作業ディレクトリに git を起動する。つまり利用者が何もしなくても
        //     この経路は踏まれる。
        //   - .git/hooks/pre-commit 等のフックは、git commit のたびに実行される。git.autoCommit が
        //     オンなら適用直後に、オフでも利用者自身の次のコミットで走る。
        // 実測では、AIの出力に混ぜた <<<< FILE: .git/hooks/pre-commit の1ブロックが、
        // 解析（canApply=True）から適用（applySuccess=True）まで通り、実際にフックが書き込まれた。
        //
        // 【なぜ拡張子検査より手前か / なぜ checkExtension に関係なく効くか】 上記のとおり
        // .git/hooks/pre-commit や .git/config は拡張子を持たない、あるいは許可拡張子を持つ名前で
        // あり、拡張子の判定では止まらない。またフォルダ作成（ResolveDirectory）や取り込み
        // （ResolveImportTarget）は拡張子検査を通さない設計のため、拡張子検査の中に置くと
        // それらの経路が素通りしてしまう。「どの経路であっても .git の中身は書き換えない」という
        // 一本の規則にするため、経路によらず必ず通るこの位置に置く。
        //
        // 【なぜ E201 ではないか】 E201 は「パスがルート外」であり、.git はルートの「内側」に
        // ある。E201 を流用すると利用者には「ルート外だと言われたが、どう見てもルート内にある」と
        // しか読めず、何が起きたのか伝わらない。専用の E211 を新設した（E707 は仕様書17章で
        // 「欠番」と明記されているため使わない。ErrorCodes.cs の E709/E710 と同じ判断）。
        //
        // 【先頭セグメントに限定しない理由】 依頼は「先頭セグメントが .git のパス」だったが、
        // 判定はすべてのセグメントに対して行う。サブモジュールや vendor/ に取り込んだ
        // 入れ子のリポジトリ（vendor/lib/.git/hooks/pre-commit）も、利用者がそのフォルダで
        // git を操作すれば同じようにフックが走るため、危険度は先頭と変わらない。一方で
        // 「.git という名前のフォルダの中に置きたい正当なファイル」は存在しないため、
        // 広げても正常な用途を壊さない。
        //
        // 【前方一致にしない理由】 .github（GitHub Actions のワークフロー等）・.gitignore は
        // まったくの別物で、書けなくなると実用上困る。比較は必ずセグメント全体の一致で行う。
        var deniedSegment = segments.FirstOrDefault(
            s => _options.DeniedDirectorySegments.Any(d => string.Equals(d, s, StringComparison.OrdinalIgnoreCase)));
        if (deniedSegment is not null)
        {
            return GraftResult<string>.Fail(
                ErrorCode.E211,
                $"バージョン管理の内部フォルダ '{deniedSegment}' の配下は書き換えられません",
                path: relativePath);
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
        //
        // v1.0.14: 実体解決の起点を「ルートそのもの」へ変えた（詳細は
        // <see cref="ResolveRealPathBelowRootCore"/> のコメント）。ルートより上の構成要素を
        // ファイル1件ごとに解決し直していたことが、構築時に1回だけ解決した_rootとの間に
        // 表記の食い違いを生んでいた。
        var real = LongPath.StripExtendedPrefix(ResolveRealPathBelowRoot(combined, trace: null));
        if (!IsWithinRoot(real) && !IsWithinRealRoot(real))
        {
            ReportLinkEscape(relativePath, combined, real);
            return GraftResult<string>.Fail(ErrorCode.E201, "シンボリックリンク経由でルート外を参照しています", path: relativePath);
        }

        if (checkExtension)
        {
            var fileName = Path.GetFileName(combined);

            // v1.0.15 セキュリティ対応（穴2・穴4）: 「拡張子が無ければ無条件に通す」のをやめた。
            //
            // 【従来の作りと、それが穴だった理由】 不具合2対応として入れた
            // 「extension.Length > 0 のときだけホワイトリストで判定する」は、"Dockerfile" や
            // LICENSE を通すための対応だったが、実際には「拡張子の無い名前をすべて素通りさせる」
            // 判定になっていた。実測で .git/hooks/pre-commit・Makefile・script・CON・NUL・COM1 が
            // すべて通過し、適用エンジン経由で実際に書き込めることを確認している。
            // 拡張子ホワイトリストは「AIやGraft自身がテキストとして新規に書き込む内容」に対する
            // 安全策（ResolveImportTargetのコメント参照）であり、拡張子の有無はその判断材料に
            // ならない。そこで拡張子が無い場合は「名前そのものの許可リスト」で判定する。
            //
            // 【ドットで始まる名前をどう扱うか】 Path.GetExtension(".gitignore") は名前全体
            // （".gitignore"）を返すため、これを拡張子として扱うと必ずホワイトリストから外れる。
            // 実測でも .gitignore・.gitattributes・.editorconfig・.dockerignore はすべて E202 に
            // なっていた（Makefile は通るのに .gitignore は通らないという不整合）。これらは
            // .git フォルダとは別物の、プロジェクト直下の普通のファイルであり、許可されるべきである。
            // 「先頭がドットで、それ以外にドットを含まない」名前は実質的に拡張子を持たない名前と
            // みなし、拡張子ではなく名前の許可リストで判定する。
            //   .gitignore   → 名前の許可リスト（許可）
            //   .hidden.exe  → 2つ目のドットがあるので拡張子 ".exe" として判定（拒否）
            //   .env.sh      → 拡張子 ".sh" として判定（拒否）
            //
            // 【Windowsの予約デバイス名（CON/NUL/COM1 等）について】 これらも拡張子を持たないため
            // 従来は素通りしていたが、名前の許可リストに入れていないので自動的に拒否される。
            // Windows実機で Path.GetFullPath(@"C:\proj\CON") が何を返すか（\\.\CON を返すなら
            // IsWithinRoot で既に外れる）は未確認だが、どちらに転んでも拒否されるため追加対応は不要。
            var isDotOnlyName = fileName.StartsWith('.') && fileName.IndexOf('.', 1) < 0;
            var extension = isDotOnlyName ? string.Empty : Path.GetExtension(combined);

            if (extension.Length > 0)
            {
                if (!_options.AllowedExtensions.Any(a => string.Equals(a, extension, StringComparison.OrdinalIgnoreCase)))
                {
                    return GraftResult<string>.Fail(ErrorCode.E202, $"拡張子 '{extension}' は許可されていません", path: relativePath);
                }
            }
            else if (!_options.AllowedExtensionlessNames.Any(
                         n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)))
            {
                return GraftResult<string>.Fail(
                    ErrorCode.E202,
                    $"拡張子の無いファイル名 '{fileName}' は許可されていません" +
                    "（Dockerfile・Makefile・LICENSE・.gitignore などの決まった名前のみ許可しています）",
                    path: relativePath);
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

    private bool IsWithinRoot(string candidate) => IsWithin(candidate, _root);

    /// <summary>
    /// v1.0.14: <paramref name="candidate"/>が<paramref name="root"/>と同じか、その配下かを
    /// 前方一致で判定する。従来<see cref="IsWithinRoot"/>に直書きしていた判定を、比較相手を
    /// 差し替えられるよう切り出しただけで、判定基準そのものは変えていない
    /// （末尾区切りを落としたうえで、大文字小文字を無視した完全一致か
    /// 「root + 区切り文字」で始まるか）。
    /// <para>
    /// <c>internal</c>にしているのは単体テストのため。表記が食い違う組み合わせ
    /// （<c>Z:</c>表記のルート と UNC表記の実体）で前方一致がどう転ぶかは、
    /// 実際のネットワークドライブが無いと再現できないが、判定そのものは純粋な文字列比較なので
    /// ここを直接固定できればLinux上でも表を検証できる。
    /// </para>
    /// </summary>
    internal static bool IsWithin(string candidate, string root)
    {
        if (string.IsNullOrEmpty(root)) return false;

        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedCandidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return normalizedCandidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// v1.0.14 実機不具合対応（E201の第二の砦）: <paramref name="real"/>が「ルートの実体」の
    /// 配下かどうかを判定する。<see cref="IsWithinRoot"/>（＝<c>_root</c>との前方一致）が
    /// 外れたときにだけ呼ぶ。
    /// <para>
    /// 【なぜ要るか】 実機ログ（20260902）では、同じ<c>Z:</c>ドライブ上の同じプロジェクトが、
    /// 時刻によって <c>Z:\営業部\...</c> と <c>\\gfs\inaden\営業部\...</c> の2通りに解決されていた
    /// （14:03 EPSEnhance→UNC / 14:03 MAI-History→Z: / 15:37 MAI-History→UNC）。
    /// <c>Z:</c>は<c>\\gfs\inaden</c>へのマップなので同じ場所を指すが、文字列としては
    /// 前方一致しない。<c>_root</c>が<c>Z:</c>表記で<c>real</c>がUNC表記になった瞬間だけ
    /// E201が出ていた（16:17）。表記が揃っていた時間帯（15:39・15:46・15:51）は適用が
    /// 成功している。<b>したがって「片方の表記へ寄せる」のではなく、「ルート側も同じ実体解決に
    /// かけた値」とも突き合わせる</b>のが筋になる。
    /// </para>
    /// <para>
    /// 【安全性】 ここで通すのは「ルートの実体の配下」だけである。本当にルート外を指すリンクは
    /// <c>_root</c>からも実体ルートからも外れるため、従来どおりE201で拒否される
    /// （<c>PathGuardTests</c>で固定済み）。判定を緩めているのではなく、<b>同じ場所を指す2つの
    /// 表記のどちらで返ってきても同じ結論になるようにしている</b>だけである。
    /// </para>
    /// <para>
    /// 【なぜコンストラクタで一度だけ求めないのか】 依頼時の案は「コンストラクタで実体ルートを
    /// 求めて持っておく」だったが、そうしなかった理由が2つある。(1) 実機ログが示すとおり
    /// リンク解決の結果自体が時刻によって揺れる。構築時の値を焼き付けると、揺れた側の値を
    /// 掴んだまま固定されてしまう。(2) 通常は<see cref="IsWithinRoot"/>で通るため、ここへ
    /// 来るのは失敗経路だけであり、そこで1回だけ余分に解決してもコストが問題にならない
    /// （逆に、構築時に必ず1回走らせるとネットワーク越しの往復が全PathGuard生成で発生する）。
    /// </para>
    /// </summary>
    private bool IsWithinRealRoot(string real)
    {
        string realRoot;
        try
        {
            realRoot = NormalizeTrailingSeparator(LongPath.StripExtendedPrefix(ResolveRealPath(_root)));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // ルートの実体解決に失敗したら「実体ルートは分からない」＝この砦は使えない、と扱う。
            // 呼び出し元は_rootとの前方一致が既に外れているため、結果はE201のままになる。
            return false;
        }

        // 実体ルートが_rootと同じなら、判定内容は_root側と完全に重複する（無駄な二度手間）。
        if (string.Equals(realRoot, _root, StringComparison.OrdinalIgnoreCase)) return false;

        return IsWithin(real, realRoot);
    }

    /// <summary>
    /// v1.0.14: E201（リンク経由のルート外参照）で拒否したときに、判断材料をそのままログへ残す。
    /// <para>
    /// 【なぜ要るか】 v1.0.14で入れた<c>path-guard</c>ログは「絶対パスへ解決できなかった」
    /// 異常時にしか出ない。実機ログ（20260902）にこのイベントが1件も無かったため、
    /// 「E201が出たとき<c>_root</c>と<c>real</c>が実際どういう文字列だったのか」は
    /// <c>environment</c>行から間接的に推定するしかなかった。同じ推定作業を二度としないよう、
    /// <b>拒否した瞬間の値をすべて</b>記録する。記録するのはパスだけで、ファイルの中身や
    /// AIの応答は一切含めない（既存のログ方針どおり）。
    /// </para>
    /// <para>
    /// 「解決の内訳」は、どの構成要素がリンクとして解決され、何を返したかの一覧である。
    /// 失敗経路でのみ実体解決をもう一度走らせて集めるため、成功時のコストは増えない。
    /// </para>
    /// </summary>
    private void ReportLinkEscape(string relativePath, string combined, string real)
    {
        if (AnomalyLogger is null) return;

        var trace = new List<string>();
        string realAgain;
        try
        {
            realAgain = LongPath.StripExtendedPrefix(ResolveRealPathBelowRoot(combined, trace));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            realAgain = $"（再解決に失敗: {ex.GetType().Name}）";
        }

        var detail = trace.Count == 0
            ? "リンクとして解決された構成要素は無し"
            : string.Join(" / ", trace);

        ReportAnomaly(
            $"ルート外参照として拒否しました（E201）。相対パス={relativePath}" +
            $" / ルート={_root} / 結合後={combined} / 実体解決後={real}" +
            $" / 再解決={realAgain} / 解決の内訳: {detail}");
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
    /// <see cref="ResolveRealPathBelowRootCore"/>を、実際のファイルシステムを見る
    /// <see cref="ResolveIfLink"/>で実行する。
    /// </summary>
    private string ResolveRealPathBelowRoot(string combined, IList<string>? trace)
        => ResolveRealPathBelowRootCore(_root, combined, ResolveIfLink, trace);

    /// <summary>
    /// v1.0.14 実機不具合対応（今回の真因）: リンクの実体解決を<b>ルートより下の構成要素だけ</b>に
    /// 限定して行う。起点は<paramref name="root"/>そのもの（＝<c>_root</c>）で、
    /// <paramref name="combined"/>のうちルートを超えた部分のセグメントだけを1つずつ辿る。
    /// <para>
    /// 【従来の作りと、それが壊れていた理由】 従来は<see cref="ResolveRealPathCore"/>で
    /// <paramref name="combined"/>を<b>ドライブルートから丸ごと</b>辿り直していた。つまり
    /// <c>Z:\営業部</c>・<c>Z:\営業部\02-国営課</c>…といったルート自身の構成要素まで、
    /// ファイルを1つ解決するたびに毎回リンク判定し直していた。ところが<c>_root</c>の側は
    /// <see cref="NormalizeRoot"/>が<b>PathGuardを作った時点で1回だけ</b>同じ解決を行った結果である。
    /// <b>この2回の解決結果が食い違うと、同じ場所を指しているのに前方一致が外れてE201になる。</b>
    /// </para>
    /// <para>
    /// 【食い違いは実在した（実機ログ 20260902）】 マップ済みネットワークドライブ
    /// <c>Z:</c>（<c>\\gfs\inaden</c> へのマップ）上のプロジェクトで、
    /// <c>environment</c> ログのプロジェクトルートが時刻によって
    /// <c>Z:\営業部\...</c> になったり <c>\\gfs\inaden\営業部\...</c> になったりしていた
    /// （14:03:09 EPSEnhance→UNC表記 / 14:03:57 MAI-History→<c>Z:</c>表記 /
    /// 15:37:23 同じMAI-History→UNC表記）。入力は同じ<c>projects.json</c>の値である。
    /// つまり<c>Directory.ResolveLinkTarget</c>が「そこはリンクだ」と報告するかどうか自体が
    /// 揺れている。かつ v1.0.14 で入れた<c>path-guard</c>ログ（絶対パスへ解決できなかった
    /// ときだけ出る）はこのログに<b>1件も出ていない</b>ため、揺れは「解決に失敗して後退した」
    /// のではなく「そもそもリンクとして検出されなかった」側で起きている。
    /// E201が出たのは<c>_root</c>が<c>Z:</c>表記だった16:17台だけで、両者の表記が
    /// 揃っていた15:39・15:46・15:51の適用は成功している。
    /// </para>
    /// <para>
    /// 【なぜルートより下だけで安全か】 <paramref name="combined"/>は、呼び出し元が
    /// 「絶対パスでない」「<c>..</c>を含まない」ことを確認したうえで<c>_root</c>に連結し、
    /// さらに<c>_root</c>配下であることを確認した値である。したがって<b>ルートより上の構成要素は
    /// 利用者・AIが与えた相対パスの影響を一切受けない</b>。ルート自身がどこの実体を指すかは
    /// 「プロジェクトをどこに置いたか」の話であって、脱出の検査対象ではない
    /// （そこは<see cref="NormalizeRoot"/>が構築時に1回解決済み）。防ぎたいのは
    /// 「相対パスが指すセグメントのどれかがリンクで、ルート外へ出る」ことであり、それは
    /// 起点をルートにしたこの走査で従来どおり検出できる（<c>PathGuardTests</c>で固定）。
    /// </para>
    /// <para>
    /// 【副次的な効果】 ネットワーク上のプロジェクトでは、ファイル1件を解決するたびに
    /// ルートの深さぶん（実機の例では8段）の<c>Directory.Exists</c>／属性照会がSMB越しに
    /// 発生していた。エクスプローラの一覧はファイル数ぶんこれを繰り返すため、往復回数が
    /// 「ファイル数×ルートの深さ」から「ファイル数×相対パスの深さ」へ減る。
    /// </para>
    /// </summary>
    /// <param name="root">プロジェクトルート（正規化済み。<c>_root</c>）。走査の起点になる。</param>
    /// <param name="combined">ルート配下であることを確認済みの結合後の絶対パス。</param>
    /// <param name="resolveIfLink">リンク解決。テストから差し替えるために引数化している。</param>
    /// <param name="trace">
    /// リンクとして解決された構成要素の記録先（診断ログ用）。不要ならnull。
    /// </param>
    internal static string ResolveRealPathBelowRootCore(
        string root,
        string combined,
        Func<string, string?> resolveIfLink,
        IList<string>? trace = null)
    {
        if (string.IsNullOrEmpty(combined)) return combined;

        // 判定は必ず<see cref="IsWithin"/>と同じ境界条件（完全一致か「root + 区切り文字」で始まるか）
        // で行う。単なるStartsWith(root)にすると、root="/proj" に対して combined="/proj2/a.txt" のような
        // 「区切り文字を挟まない同名接頭辞」が素通りしてしまう。その場合remainderが "2/a.txt" になり、
        // 走査が /proj → /proj/2 → /proj/2/a.txt と<b>実在しない別のパスへ組み替わる</b>。
        // しかもその値は呼び出し元のルート内判定を通ってしまうため、ルート外のパスがルート内に
        // 見える形へ化けることになる（Copilotのレビュー指摘。現在の呼び出し経路では手前で
        // IsWithinRoot(combined)を通しているため到達しないが、防御的に置いたコードが黙って
        // 誤ったパスを作るのは筋が悪く、internalとして直接テストもされているため塞ぐ）。
        if (!IsWithin(combined, root))
        {
            // 想定外（呼び出し元がルート内判定を済ませているはず）。安全側に倒し、従来どおり
            // 丸ごと辿る経路へ落とす。ここを黙って通すと走査の起点がずれてしまう。
            ReportAnomaly($"結合後のパスがルート配下ではありません。全体を辿ります: ルート={root} / 結合後={combined}");
            return ResolveRealPathCore(combined, resolveIfLink);
        }

        var remainder = combined[root.Length..];
        var parts = remainder.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);

            var resolved = resolveIfLink(current);
            if (resolved is null) continue;

            trace?.Add($"{current} → {resolved}");

            if (!IsAbsolutePath(resolved))
            {
                // v1.0.14と同じ最後の砦。相対パスのまま組み立てを続けると、呼び出し元の
                // Path.GetFullPathでカレントディレクトリと連結されてしまう。
                ReportAnomaly(
                    $"リンクの実体解決が相対パスを返したため、実体解決を打ち切って元のパスを使います。" +
                    $" リンク={current} / 解決結果={resolved} / 採用={combined}");
                return combined;
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
