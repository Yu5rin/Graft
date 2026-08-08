using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Graft.Core;

namespace Graft.Features;

/// <summary>
/// 仕様書6.5 適用後フックの実行を担当する。プロジェクトルートを作業ディレクトリとして
/// コマンドを順次実行し、標準出力・標準エラーを逐次 <paramref name="onOutput"/> に相当する
/// コールバックへ通知する。
/// <para>
/// onFailure（ignore/warn/offerRollback/autoRollback）に基づく分岐は呼び出し側（UI/適用エンジン）
/// の責務であり、本クラスは実行結果を正しく <see cref="HookResult"/> に詰めることに徹する。
/// </para>
/// </summary>
public sealed class HookRunner
{
    private const int DefaultTimeoutSec = 120;

    /// <summary>プロジェクトの適用後フックを、登録順に順次実行する。</summary>
    public async Task<GraftResult<IReadOnlyList<HookResult>>> RunAsync(
        Project project, int timeoutSec, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var effectiveTimeout = timeoutSec > 0 ? timeoutSec : DefaultTimeoutSec;
        var results = new List<HookResult>();
        var issues = new List<GraftIssue>();

        foreach (var hook in project.PostApplyHooks)
        {
            ct.ThrowIfCancellationRequested();
            var (result, issue) = await RunOneAsync(project.Root, hook, effectiveTimeout, onOutput, ct)
                .ConfigureAwait(false);
            results.Add(result);
            if (issue is not null) issues.Add(issue);
        }

        return GraftResult<IReadOnlyList<HookResult>>.Ok(results, issues);
    }

    private static async Task<(HookResult Result, GraftIssue? Issue)> RunOneAsync(
        string projectRoot, PostApplyHook hook, int timeoutSec, Action<string>? onOutput, CancellationToken ct)
    {
        var output = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        Process process;

        try
        {
            process = StartProcess(projectRoot, hook.Command, output, onOutput);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            stopwatch.Stop();
            return BuildStartFailure(hook, stopwatch.ElapsedMilliseconds, ex);
        }

        using (process)
        {
            var timedOut = await WaitWithTimeoutAsync(process, timeoutSec, ct).ConfigureAwait(false);
            stopwatch.Stop();
            return BuildCompletion(hook, process, stopwatch.ElapsedMilliseconds, timedOut, timeoutSec, output);
        }
    }

    private static (HookResult, GraftIssue?) BuildStartFailure(PostApplyHook hook, long elapsedMs, Exception ex)
    {
        var result = new HookResult
        {
            Name = hook.Name,
            ExitCode = -1,
            DurationMs = elapsedMs,
            TimedOut = false,
            Output = ex.Message,
        };
        // Output（実行結果ログ）は原文のまま残し、issue.Detail（ダイアログ表示用）のみ日本語化する。
        var issue = GraftIssue.Of(ErrorCode.E501, detail: ExceptionMessages.Describe(ex), path: hook.Name);
        return (result, issue);
    }

    private static (HookResult, GraftIssue?) BuildCompletion(
        PostApplyHook hook, Process process, long elapsedMs, bool timedOut, int timeoutSec, StringBuilder output)
    {
        var result = new HookResult
        {
            Name = hook.Name,
            ExitCode = timedOut ? -1 : process.ExitCode,
            DurationMs = elapsedMs,
            TimedOut = timedOut,
            Output = output.ToString(),
        };

        var issue = timedOut
            ? GraftIssue.Of(ErrorCode.E502, detail: $"{hook.Name} が{timeoutSec}秒でタイムアウトしました。",
                path: hook.Name, severity: Severity.Warning)
            : null;

        return (result, issue);
    }

    private static Process StartProcess(string projectRoot, string command, StringBuilder output, Action<string>? onOutput)
    {
        var psi = BuildStartInfo(projectRoot, command);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Report(output, onOutput, e.Data);
        process.ErrorDataReceived += (_, e) => Report(output, onOutput, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void Report(StringBuilder output, Action<string>? onOutput, string? line)
    {
        if (line is null) return;
        lock (output)
        {
            output.AppendLine(line);
        }

        onOutput?.Invoke(line);
    }

    private static ProcessStartInfo BuildStartInfo(string workingDirectory, string command)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // シェル経由で実行する（ユーザーが設定したコマンドのみを起動し、ネットワーク通信は行わない）。
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        return psi;
    }

    private static async Task<bool> WaitWithTimeoutAsync(Process process, int timeoutSec, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                KillProcessTree(process);
                throw;
            }

            // タイムアウト: プロセスツリーを確実に終了させたうえで終了を待つ。
            KillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return true;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 既にプロセスが終了している場合は何もしない。
        }
    }
}
