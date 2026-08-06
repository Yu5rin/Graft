using System.IO;
using Graft.Core;
using Graft.Infra;

namespace Graft.Tests.TestSupport;

/// <summary>
/// 適用エンジン（<see cref="ApplyEngine"/>）・バックアップ（<see cref="BackupManager"/>）・
/// リビジョン（<see cref="RevisionStore"/>）を一時ディレクトリ上に組み立てるための共通ヘルパ。
/// <c>ApplyEngineTests</c>・<c>BackupRevisionTests</c> から共通利用する。
/// アプリのデータディレクトリ（settings.json・back/ 等が置かれる場所）とプロジェクトルートは
/// 同一 <see cref="TempWorkspace"/> 配下の別サブフォルダとして分離する。
/// </summary>
public sealed class ApplyHarness
{
    public TempWorkspace Workspace { get; }
    public string ProjectRoot { get; }
    public string ProjectId { get; }
    public AppPaths Paths { get; }
    public BackupManager Backup { get; }
    public RevisionStore Revisions { get; }
    public MatchEngine Matcher { get; }
    public ApplyEngine Engine { get; }

    public ApplyHarness(TempWorkspace workspace, string projectId = "p_test01")
    {
        Workspace = workspace;
        ProjectId = projectId;
        ProjectRoot = workspace.CreateDirectory("project");
        Paths = new AppPaths(workspace.CreateDirectory("app"));
        Backup = new BackupManager(Paths);
        Revisions = new RevisionStore(Paths);
        Matcher = new MatchEngine();
        Engine = new ApplyEngine(Backup, Revisions, Matcher);
    }

    /// <summary>パッチ本文を解析する。構文エラーがあれば GraftResult.Value の例外で気づける。</summary>
    public static Patch Parse(string patchText) => new PatchParser().Parse(patchText).Value;

    /// <summary>プロジェクトルートを対象とした適用文脈を組み立てる。</summary>
    public ApplyContext MakeContext(
        int revision, Settings? settings = null, bool forceReapply = false, bool allowReadOnlyOverride = false)
    {
        var guard = new PathGuard(ProjectRoot, PathGuardOptions.Default);
        return new ApplyContext
        {
            ProjectId = ProjectId,
            ProjectRoot = ProjectRoot,
            Revision = revision,
            Settings = settings ?? new Settings(),
            Guard = guard,
            ForceReapply = forceReapply,
            AllowReadOnlyOverride = allowReadOnlyOverride,
        };
    }

    /// <summary>ドライランを実行する。仕様書6.1どおり常に成功として返る（Plans内に失敗要素を含みうる）。</summary>
    public async Task<DryRunResult> DryRunAsync(string patchText, ApplyContext ctx)
    {
        var patch = Parse(patchText);
        var result = await Engine.DryRunAsync(patch, ctx);
        return result.Value;
    }

    public Task<GraftResult<RevisionManifest>> ApplyAsync(DryRunResult plan, ApplyContext ctx)
        => Engine.ApplyAsync(plan, ctx);

    /// <summary>プロジェクトルート配下へ相対パスでバイト列を書き込む。親フォルダが無ければ作成する。</summary>
    public string WriteProjectBytes(string relativePath, byte[] content)
    {
        var full = Path.Combine(ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(full, content);
        return full;
    }

    /// <summary>プロジェクトルート配下へ相対パスでテキスト（UTF-8・BOM無し）を書き込む。</summary>
    public string WriteProjectText(string relativePath, string content)
        => WriteProjectBytes(relativePath, new System.Text.UTF8Encoding(false).GetBytes(content));

    /// <summary>プロジェクトルート配下のファイルをバイト列として読み込む。</summary>
    public byte[] ReadProjectBytes(string relativePath)
        => File.ReadAllBytes(Path.Combine(ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>プロジェクトルート配下のファイル存在を判定する。</summary>
    public bool ProjectFileExists(string relativePath)
        => File.Exists(Path.Combine(ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>プロジェクトルート配下にファイルの代わりに置いた「フォルダのつもりの通常ファイル」を作る
    /// （FULL形式の親フォルダ作成が失敗するケースを再現するために使う）。</summary>
    public string WriteProjectFileAsBlocker(string relativePath, string content)
        => WriteProjectText(relativePath, content);
}
