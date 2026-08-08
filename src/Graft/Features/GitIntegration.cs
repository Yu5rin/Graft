using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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

    /// <summary>git 管理下かどうか、未コミットの変更があるかを調べる。</summary>
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

    private static async Task<GitProcessResult> RunGitAsync(
        string projectRoot, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

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
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var combined = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}{stderr}";
            return new GitProcessResult(true, process.ExitCode, combined);
        }
    }

    private readonly record struct GitProcessResult(bool Started, int ExitCode, string Output);
}
