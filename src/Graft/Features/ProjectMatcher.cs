using System.IO;
using Graft.Core;

namespace Graft.Features;

/// <summary>
/// プロジェクト自動判定1件分の評価結果。仕様書3.4。
/// </summary>
public sealed record ProjectMatchCandidate
{
    /// <summary>評価対象のプロジェクト。</summary>
    public required Project Project { get; init; }

    /// <summary>一致率（0.0〜1.0）。</summary>
    public double Ratio { get; init; }

    /// <summary>一致したパス（推定根拠としてUIに表示する）。</summary>
    public IReadOnlyList<string> MatchedPaths { get; init; } = Array.Empty<string>();

    /// <summary>一致しなかったパス。</summary>
    public IReadOnlyList<string> UnmatchedPaths { get; init; } = Array.Empty<string>();
}

/// <summary>自動判定の結論。仕様書3.4の3段階しきい値に対応する。</summary>
public enum ProjectMatchDecision
{
    /// <summary>一致率90%以上。自動選択する。</summary>
    AutoSelected,

    /// <summary>一致率50〜90%。候補として提示し、明示的な確認を求める。</summary>
    NeedsConfirmation,

    /// <summary>一致率50%未満。適用をブロックし手動選択を要求する。</summary>
    Blocked,
}

/// <summary>自動判定全体の結果。</summary>
public sealed record ProjectMatchOutcome
{
    /// <summary>判定結果。</summary>
    public ProjectMatchDecision Decision { get; init; }

    /// <summary>最も一致率が高い候補。判定不能の場合は null。</summary>
    public ProjectMatchCandidate? Best { get; init; }

    /// <summary>一致率降順の全候補。</summary>
    public IReadOnlyList<ProjectMatchCandidate> Candidates { get; init; } = Array.Empty<ProjectMatchCandidate>();
}

/// <summary>
/// パッチに含まれるファイルパスと各プロジェクトの実ファイル構成を照合し、
/// 一致率が最も高いプロジェクトを推定する（仕様書3.4）。複数プロジェクトを扱う際の
/// 誤爆事故を防ぐための必須機構であり、呼び出し側で無効化できてはならない。
/// </summary>
public sealed class ProjectMatcher
{
    /// <summary>
    /// パッチと候補プロジェクト一覧から一致率を算出し、判定結果を返す。
    /// ルート未接続のプロジェクトは候補から除く。
    /// </summary>
    public Task<GraftResult<ProjectMatchOutcome>> MatchAsync(
        Patch patch, IReadOnlyList<Project> projects, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(projects);

        var checkedPaths = ExtractCandidatePaths(patch);
        var connected = projects.Where(p => !p.IsDisconnected).ToList();

        if (checkedPaths.Count == 0)
        {
            return Task.FromResult(BuildUndeterminableOutcome(connected));
        }

        if (connected.Count == 0)
        {
            var issue = GraftIssue.Of(ErrorCode.E303, detail: "接続されたプロジェクトが登録されていません。");
            var empty = new ProjectMatchOutcome { Decision = ProjectMatchDecision.Blocked };
            return Task.FromResult(GraftResult<ProjectMatchOutcome>.Ok(empty, new[] { issue }));
        }

        var candidates = connected
            .Select(p => Evaluate(p, checkedPaths, ct))
            .OrderByDescending(c => c.Ratio)
            .ToList();

        return Task.FromResult(BuildOutcome(candidates));
    }

    private static GraftResult<ProjectMatchOutcome> BuildUndeterminableOutcome(IReadOnlyList<Project> connected)
    {
        var candidates = connected
            .Select(p => new ProjectMatchCandidate { Project = p, Ratio = 0.0 })
            .ToList();
        var outcome = new ProjectMatchOutcome
        {
            Decision = ProjectMatchDecision.NeedsConfirmation,
            Best = null,
            Candidates = candidates,
        };
        var issue = GraftIssue.Of(
            ErrorCode.E303,
            detail: "パッチの全ブロックが新規作成前提（FULL形式・MKDIR等）のため、既存ファイルとの一致率を算出できません。プロジェクトを手動で確認してください。",
            severity: Severity.Warning);
        return GraftResult<ProjectMatchOutcome>.Ok(outcome, new[] { issue });
    }

    private static GraftResult<ProjectMatchOutcome> BuildOutcome(IReadOnlyList<ProjectMatchCandidate> candidates)
    {
        var best = candidates[0];
        var issues = new List<GraftIssue>();

        ProjectMatchDecision decision;
        if (best.Ratio >= 0.9)
        {
            decision = ProjectMatchDecision.AutoSelected;
        }
        else if (best.Ratio >= 0.5)
        {
            decision = ProjectMatchDecision.NeedsConfirmation;
        }
        else
        {
            decision = ProjectMatchDecision.Blocked;
            issues.Add(GraftIssue.Of(
                ErrorCode.E303,
                detail: $"最も一致率が高いプロジェクト「{best.Project.DisplayName}」でも一致率{best.Ratio:P0}のため、適用をブロックしました。"));
        }

        var outcome = new ProjectMatchOutcome { Decision = decision, Best = best, Candidates = candidates };
        return GraftResult<ProjectMatchOutcome>.Ok(outcome, issues);
    }

    /// <summary>
    /// 一致率算出の分母となるパスを抽出する。FULL形式・MKDIRなど新規作成が前提の
    /// パスは、存在しなくて当然のため分母から除く。RENAMEは移動元のみを対象とする。
    /// 同一パスの重複は除去する（大文字小文字は区別しない）。
    /// </summary>
    private static IReadOnlyList<string> ExtractCandidatePaths(Patch patch)
    {
        var paths = new List<string>();
        foreach (var block in patch.Blocks)
        {
            switch (block)
            {
                case FullContentBlock:
                case MkdirBlock:
                    break;
                case RenameBlock rename:
                    paths.Add(rename.FromPath);
                    break;
                default:
                    paths.Add(block.Path);
                    break;
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static ProjectMatchCandidate Evaluate(Project project, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var matched = new List<string>();
        var unmatched = new List<string>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (ExistsCaseInsensitive(project.Root, path))
            {
                matched.Add(path);
            }
            else
            {
                unmatched.Add(path);
            }
        }

        var ratio = paths.Count == 0 ? 0.0 : (double)matched.Count / paths.Count;
        return new ProjectMatchCandidate
        {
            Project = project,
            Ratio = ratio,
            MatchedPaths = matched,
            UnmatchedPaths = unmatched,
        };
    }

    /// <summary>
    /// プロジェクト配下に相対パスが存在するかを大文字小文字を無視して判定する。
    /// 判定に必要な経路上のディレクトリだけを見るため、プロジェクト全体の
    /// ファイル列挙は行わない（大規模プロジェクトでも固まらない）。
    /// </summary>
    private static bool ExistsCaseInsensitive(string root, string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var isLast = i == segments.Length - 1;
            var direct = Path.Combine(current, segments[i]);

            if (isLast)
            {
                if (File.Exists(direct) || Directory.Exists(direct))
                {
                    return true;
                }
                return FindEntryCaseInsensitive(current, segments[i]) is not null;
            }

            if (Directory.Exists(direct))
            {
                current = direct;
                continue;
            }

            var match = FindEntryCaseInsensitive(current, segments[i]);
            if (match is null)
            {
                return false;
            }
            current = match;
        }

        return false;
    }

    private static string? FindEntryCaseInsensitive(string directory, string name)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
