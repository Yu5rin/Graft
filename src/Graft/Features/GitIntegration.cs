using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Graft.Core;

namespace Graft.Features;

/// <summary>作業ツリーの状態。仕様書7.5。</summary>
public sealed record GitStatus
{
    /// <summary>プロジェクトルートが git 管理下かどうか。</summary>
    public bool IsRepository { get; init; }

    /// <summary>未コミットの変更があるかどうか。</summary>
    public bool HasUncommittedChanges { get; init; }

    /// <summary>現在のブランチ名。取得できない場合は null。</summary>
    public string? BranchName { get; init; }

    /// <summary>変更のあるパス（<c>git status --porcelain</c> の結果より）。</summary>
    public IReadOnlyList<string> ChangedPaths { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 仕様書4.7 Gitガター用。<see cref="GitIntegration.GetHeadFileContentAsync"/> の結果。
/// git管理外・git未検出の場合は<see cref="IsRepository"/>をfalseとして返す（エラーとしない）。
/// </summary>
public sealed record GitHeadContent
{
    /// <summary>プロジェクトルートが git 管理下かどうか。</summary>
    public bool IsRepository { get; init; }

    /// <summary>
    /// HEAD時点のファイル内容。ファイルがHEADに存在しない場合（新規追加ファイル、または
    /// 直近コミットが無いリポジトリ）は null とし、呼び出し側はファイル全体を追加行として扱う。
    /// </summary>
    public string? Content { get; init; }
}

/// <summary>
/// 課題3: 自動コミットの前提条件チェック結果。<see cref="GitIntegration.CommitAsync"/>自体は
/// gitが無い場合もリポジトリでない場合も「git add に失敗しました」という同じ形のエラーで
/// 返ってくる（git add/commitの実行結果を文字列で包むだけのため）ため、ログに残す理由を
/// 区別するにはコミットを試みる前にこのチェックを行う必要がある。
/// </summary>
public enum GitCommitPreflight
{
    /// <summary>gitコマンドが実行でき、対象がgitリポジトリである。コミットを試みてよい。</summary>
    Ready,

    /// <summary>gitコマンド自体が見つからない（未インストール、PATHが通っていない等）。</summary>
    GitCommandNotFound,

    /// <summary>gitコマンドは実行できたが、対象ディレクトリがgitリポジトリではない。</summary>
    NotARepository,
}

/// <summary>
/// 仕様書7.5 Git連携。git コマンドを子プロセスとして呼び出す（外部ライブラリは追加しない）。
/// git が見つからない、またはリポジトリでない場合はエラーとせず <see cref="GitStatus.IsRepository"/>
/// を false として返す。
/// </summary>
public sealed class GitIntegration
{
    /// <summary>
    /// 課題3: <see cref="CommitAsync"/>を呼ぶ前に前提条件を確認する。<c>git rev-parse
    /// --is-inside-work-tree</c>はgitが無ければプロセス自体を起動できず（Started=false）、
    /// git はあるがリポジトリでなければ非0の終了コードを返す（Started=true）ため、
    /// この2つを区別できる。
    /// </summary>
    public async Task<GitCommitPreflight> CheckCommitPreflightAsync(string projectRoot, CancellationToken ct = default)
    {
        var inside = await RunGitAsync(projectRoot, new[] { "rev-parse", "--is-inside-work-tree" }, ct)
            .ConfigureAwait(false);
        if (!inside.Started) return GitCommitPreflight.GitCommandNotFound;
        if (inside.ExitCode != 0) return GitCommitPreflight.NotARepository;
        return GitCommitPreflight.Ready;
    }

    /// <summary>
    /// git 管理下かどうか、未コミットの変更があるかを調べる。
    ///
    /// 実装点検メモ（クリップボード監視配線の調査で判明）: 現時点ではアプリ本流
    /// （MainViewModel.Git.cs等）からは呼ばれておらず、<c>GitIntegrationTests</c>からのみ
    /// 使われている。<see cref="CommitAsync"/>の前提確認には<see cref="CheckCommitPreflightAsync"/>
    /// を使っており、こちらは未コミット差分の有無や変更ファイル一覧まで含む上位互換のAPIとして
    /// 将来（例: 適用前に「未コミットの変更がある」ことを警告する機能等）のために残している。
    /// 既存テストを削除しない方針のため、本メソッドも削除せずここに留める。
    /// </summary>
    public async Task<GraftResult<GitStatus>> GetStatusAsync(string projectRoot, CancellationToken ct = default)
    {
        var inside = await RunGitAsync(projectRoot, new[] { "rev-parse", "--is-inside-work-tree" }, ct)
            .ConfigureAwait(false);
        if (!inside.Started || inside.ExitCode != 0)
        {
            return GraftResult<GitStatus>.Ok(new GitStatus { IsRepository = false });
        }

        var branch = await RunGitAsync(projectRoot, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, ct)
            .ConfigureAwait(false);
        var status = await RunGitAsync(projectRoot, new[] { "status", "--porcelain" }, ct)
            .ConfigureAwait(false);

        var changedPaths = ParsePorcelainPaths(status.Output);
        return GraftResult<GitStatus>.Ok(new GitStatus
        {
            IsRepository = true,
            HasUncommittedChanges = changedPaths.Count > 0,
            BranchName = branch.ExitCode == 0 ? branch.Output.Trim() : null,
            ChangedPaths = changedPaths,
        });
    }

    /// <summary>
    /// 4.7 Gitガター用。指定パス（プロジェクト相対、区切りは <c>/</c> または <c>\</c> のどちらでもよい）の
    /// HEAD時点の内容を <c>git show HEAD:&lt;path&gt;</c> で取得する。git管理外・git未検出の場合は
    /// 速やかに諦め、<see cref="GitHeadContent.IsRepository"/>をfalseとして返す（エラーとしない）。
    /// </summary>
    public async Task<GitHeadContent> GetHeadFileContentAsync(
        string projectRoot, string relativePath, CancellationToken ct = default)
    {
        var inside = await RunGitAsync(projectRoot, new[] { "rev-parse", "--is-inside-work-tree" }, ct)
            .ConfigureAwait(false);
        if (!inside.Started || inside.ExitCode != 0)
        {
            return new GitHeadContent { IsRepository = false };
        }

        var normalizedPath = relativePath.Replace('\\', '/');
        var show = await RunGitAsync(projectRoot, new[] { "show", $"HEAD:{normalizedPath}" }, ct)
            .ConfigureAwait(false);
        return new GitHeadContent
        {
            IsRepository = true,
            Content = show.Started && show.ExitCode == 0 ? show.Output : null,
        };
    }

    /// <summary>7.5 適用後に "type: summary" の形式でコミットする（type が null なら summary のみ）。</summary>
    public async Task<GraftResult<string>> CommitAsync(
        string projectRoot, string? type, string summary, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return GraftResult<string>.Fail(ErrorCode.E004, detail: "コミットメッセージのsummaryが空です。");
        }

        if (paths.Count > 0)
        {
            var addResult = await AddPathsAsync(projectRoot, paths, ct).ConfigureAwait(false);
            if (addResult is not null) return addResult;
        }

        var message = string.IsNullOrWhiteSpace(type) ? summary : $"{type}: {summary}";
        var commit = await RunGitAsync(projectRoot, new[] { "commit", "-m", message }, ct).ConfigureAwait(false);
        if (!commit.Started || commit.ExitCode != 0)
        {
            return GraftResult<string>.Fail(ErrorCode.E402, detail: $"git commit に失敗しました: {commit.Output}");
        }

        return GraftResult<string>.Ok(message);
    }

    private static async Task<GraftResult<string>?> AddPathsAsync(
        string projectRoot, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var addArgs = new List<string> { "add", "--" };
        addArgs.AddRange(paths);
        var add = await RunGitAsync(projectRoot, addArgs, ct).ConfigureAwait(false);
        if (!add.Started || add.ExitCode != 0)
        {
            return GraftResult<string>.Fail(ErrorCode.E402, detail: $"git add に失敗しました: {add.Output}");
        }

        return null;
    }

    private static IReadOnlyList<string> ParsePorcelainPaths(string output)
    {
        var result = new List<string>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4) continue;
            var path = line[3..];
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            result.Add(arrow >= 0 ? path[(arrow + 4)..] : path);
        }

        return result;
    }

    /// <summary>
    /// BOM無しUTF-8。git の標準出力・標準エラーは常にこのエンコーディングで読む
    /// （不具合1対応。理由は<see cref="RunGitAsync"/>のコメント参照）。
    /// </summary>
    private static readonly UTF8Encoding GitOutputEncoding = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// git 子プロセス起動用の<see cref="ProcessStartInfo"/>を組み立てる。<see cref="RunGitAsync"/>から
    /// 分離しているのは、実際にgitを起動しなくても設定内容（エンコーディング・グローバル引数）を
    /// 単体テストできるようにするため（不具合1の回帰防止。Linux上ではOS既定エンコーディングが
    /// 元々UTF-8のため文字化けが再現せず、実行結果からは検知できない）。internalなのはテスト用。
    /// </summary>
    internal static ProcessStartInfo BuildProcessStartInfo(string projectRoot, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 不具合1: 未指定だと.NETはOSの既定コードページ（日本語版WindowsではCP932）で
            // 標準出力・標準エラーをデコードする。gitの出力はUTF-8のため、日本語を含む
            // コミットメッセージやパスがCP932として誤読され文字化けする（実機のテスト失敗で確認）。
            // 常にBOM無しUTF-8として読むことで、OSの既定コードページに依存しないようにする。
            StandardOutputEncoding = GitOutputEncoding,
            StandardErrorEncoding = GitOutputEncoding,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // gitの出力エンコーディング自体も明示する。i18n.logOutputEncodingは既定では未設定で、
        // その場合commitに記録されたエンコーディング（通常UTF-8）がそのまま出力されるため
        // 実害は薄いが、利用者のグローバルgit設定でi18n.commitEncoding/logOutputEncodingが
        // UTF-8以外へ変更されていた場合の保険として明示しておく。core.quotepath=falseは
        // 日本語ファイル名が"\346\..."のような8進数エスケープ表記へ変換される既定動作を止め、
        // git status --porcelain の結果（ChangedPaths）やgit show HEAD:<path>への
        // パス引数をそのまま扱えるようにする。
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("i18n.logOutputEncoding=UTF-8");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>
    /// 実機フリーズ調査での発見: <see cref="Process.Start(ProcessStartInfo)"/>
    /// はC#の非同期メソッドの仕様上、最初のawait（<see cref="Process.WaitForExitAsync"/>）に
    /// 到達するまで呼び出し元のスレッド上で同期的に実行される。<see cref="GitGutterProvider"/>は
    /// タブを開くたびに<c>ApplyActiveTab</c>の同期呼び出し列の中から
    /// （最初のawaitへ到達する前に）このメソッドへ到達するため、これまでは
    /// <c>Process.Start</c>そのものがUIスレッド上で実行されていた。LinuxやCI環境ではgitの
    /// プロセス生成は数ms程度で無害だが、Windows実機ではウイルス対策ソフトの新規プロセス作成
    /// フック（実行ファイルのスキャン・クラウド照会等）によって<c>CreateProcess</c>自体が
    /// 数秒〜数十秒（利用者報告: 「数十秒待っても復帰しない」）ブロックすることが知られており、
    /// これがUIスレッド上で起きるとアプリ全体が完全に無応答になる（ファイルを何個か開いた
    /// 直後に何の前触れもなく固まる、という報告の症状と一致する）。
    /// <see cref="Task.Run(Func{Task{GitProcessResult}}, CancellationToken)"/>で丸ごと
    /// スレッドプールへ逃がすことで、<c>Process.Start</c>を含め本メソッドの実行が呼び出し元の
    /// スレッド（UIスレッド）を一切塞がないようにする。
    /// </summary>
    private static Task<GitProcessResult> RunGitAsync(
        string projectRoot, IReadOnlyList<string> args, CancellationToken ct)
        => Task.Run(() => RunGitCoreAsync(projectRoot, args, ProcessTimeout, ct), ct);

    /// <summary>
    /// 実機フリーズ調査での発見: 従来はここに一切のタイムアウトが無く、
    /// <see cref="GitGutterProvider.SetTarget"/>による<c>CancellationTokenSource.Cancel()</c>は
    /// 「.NET側の待ち合わせ（<see cref="Process.WaitForExitAsync"/>）を諦める」だけで、既に
    /// 起動済みのOSプロセス自体は<c>Kill</c>されず、生きたまま裏で走り続けていた（多数の
    /// ファイルを立て続けに開くと未回収のgitプロセスが積み上がり得る一因）。ここでは
    /// このタイムアウトを超えたら呼び出し元のキャンセルとは独立に諦め、プロセスツリーごと
    /// <see cref="Process.Kill(bool)"/>する（<see cref="HookRunner"/>のタイムアウト処理と
    /// 同じ方針）。git・ファイルI/Oは通常このタイムアウトに対して十分小さいため、健全な環境
    /// では実質発火しない。既定値は本番用（<see cref="RunGitAsync"/>）。テストからは
    /// <see cref="RunForTestAsync"/>経由でより短いタイムアウトを指定できる（実際にハングした
    /// gitプロセスを模した子プロセスで打ち切り・後始末を検証するため、5秒待つのは非現実的）。
    /// </summary>
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// テスト専用の入口。<see cref="RunGitAsync"/>と同じくスレッドプールへ逃がしたうえで、
    /// 任意のタイムアウト値を指定して<see cref="RunGitCoreAsync"/>を呼べるようにする
    /// （<see cref="BuildProcessStartInfo"/>と同じ「internalでテストから直接検証できるように
    /// する」方針）。戻り値はprivateな<see cref="GitProcessResult"/>を外へ出さないよう
    /// タプルへ詰め替える。
    /// </summary>
    internal static Task<(bool Started, int ExitCode, string Output)> RunForTestAsync(
        string projectRoot, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var result = await RunGitCoreAsync(projectRoot, args, timeout, ct).ConfigureAwait(false);
            return (result.Started, result.ExitCode, result.Output);
        }, ct);

    private static async Task<GitProcessResult> RunGitCoreAsync(
        string projectRoot, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = BuildProcessStartInfo(projectRoot, args);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new IOException("git プロセスを起動できませんでした。");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // git 未インストールなど。呼び出し側は IsRepository = false として扱う。
            return new GitProcessResult(false, -1, ex.Message);
        }

        using (process)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 呼び出し元のctではなくProcessTimeout側が発火した場合のみここへ来る
                // （ct自体が既にキャンセル済みの通常のキャンセル経路はこのcatchを素通りし、
                // 従来どおり呼び出し元へOperationCanceledExceptionとして伝播する）。
                TryKill(process);
                return new GitProcessResult(false, -1, $"git コマンドが{timeout.TotalSeconds:F0}秒でタイムアウトしました。");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var combined = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}{stderr}";
            return new GitProcessResult(true, process.ExitCode, combined);
        }
    }

    /// <summary>タイムアウト時にハングしたgitプロセス（子プロセスも含む）を後始末する。
    /// 既に終了している・終了処理中で失敗する場合は無視する（後始末の失敗で更に例外を
    /// 積み上げない。16章の「例外を投げず穏当に扱う」方針）。</summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private readonly record struct GitProcessResult(bool Started, int ExitCode, string Output);
}
