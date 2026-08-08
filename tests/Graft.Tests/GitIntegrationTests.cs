using System.Diagnostics;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 課題2（Git自動コミット）の土台となる<see cref="GitIntegration"/>単体テスト。
/// CommitAsync/GetStatusAsyncが実際のgitコマンドと正しくやり取りできることと、
/// gitリポジトリでない場合にエラーを投げず<see cref="GraftResult{T}"/>で失敗を表現することを
/// 実際のワーキングツリーで検証する（HookRunnerTests等と同様、テスト実行環境に
/// 対応するコマンド（ここではgit）が存在することを前提とする）。
/// </summary>
public class GitIntegrationTests
{
    [Fact(DisplayName = "gitリポジトリへコミットすると type: summary 形式のログが残る")]
    public async Task コミットするとtype_summary形式でログに残る()
    {
        using var ws = new TempWorkspace();
        await InitRepoAsync(ws.RootPath);
        ws.WriteText("a.py", "x=1\n");

        var git = new GitIntegration();
        var result = await git.CommitAsync(ws.RootPath, "feat", "テスト用の変更 (r1)", new[] { "a.py" });

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("feat: テスト用の変更 (r1)");

        var log = await RunGitAsync(ws.RootPath, "log", "-1", "--pretty=%s");
        log.Should().Be("feat: テスト用の変更 (r1)");

        var status = await git.GetStatusAsync(ws.RootPath);
        status.Value.IsRepository.Should().BeTrue();
        status.Value.HasUncommittedChanges.Should().BeFalse("コミット直後は未コミットの変更が無いはず");
    }

    [Fact(DisplayName = "typeが無い場合はsummaryのみでコミットする")]
    public async Task typeが無い場合はsummaryのみになる()
    {
        using var ws = new TempWorkspace();
        await InitRepoAsync(ws.RootPath);
        ws.WriteText("a.py", "x=1\n");

        var git = new GitIntegration();
        var result = await git.CommitAsync(ws.RootPath, type: null, "テスト用の変更", new[] { "a.py" });

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("テスト用の変更");
    }

    [Fact(DisplayName = "gitリポジトリでないフォルダではGetStatusAsyncがIsRepository=falseを返す")]
    public async Task gitリポジトリでない場合はIsRepositoryがfalse()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x=1\n"); // git init しない。

        var git = new GitIntegration();
        var status = await git.GetStatusAsync(ws.RootPath);

        status.IsSuccess.Should().BeTrue("gitリポジトリでないこと自体はエラーとして扱わない");
        status.Value.IsRepository.Should().BeFalse();
    }

    [Fact(DisplayName = "gitリポジトリでないフォルダへのCommitAsyncは例外を投げず失敗を返す")]
    public async Task gitリポジトリでない場合のコミットは失敗を返す()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x=1\n"); // git init しない。

        var git = new GitIntegration();
        var result = await git.CommitAsync(ws.RootPath, "feat", "テスト用の変更", new[] { "a.py" });

        result.IsSuccess.Should().BeFalse();
    }

    /// <summary>git init と、テスト実行環境のグローバル設定に依存しないローカルのuser設定を行う。</summary>
    private static async Task InitRepoAsync(string root)
    {
        await RunGitAsync(root, "init", "-q");
        await RunGitAsync(root, "config", "user.email", "test@example.com");
        await RunGitAsync(root, "config", "user.name", "Graft Test");
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return stdout.Trim();
    }
}
