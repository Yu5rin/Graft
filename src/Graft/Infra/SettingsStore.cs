using System.IO;
using Graft.Core;

namespace Graft.Infra;

/// <summary>エクスポート／インポートの範囲。14章「エクスポート／インポート」に対応する。</summary>
public enum SettingsExportScope
{
    /// <summary>設定のみ。パスを含むプロジェクト定義（projects.json）は含めない。</summary>
    SettingsOnly,

    /// <summary>設定とプロジェクト定義の両方を含める。</summary>
    IncludeProjects,
}

/// <summary>
/// settings.json の読み書き・検証・エクスポート／インポートを行う。
/// 不正な値は既定値へフォールバックし、フォールバックした項目を
/// Severity.Warning の <see cref="GraftIssue"/>（ErrorCode.E404）として通知する。
/// </summary>
public sealed class SettingsStore
{
    private static readonly HashSet<string> ValidThemes =
        new(StringComparer.OrdinalIgnoreCase) { "dark", "light", "system" };

    private static readonly HashSet<string> ValidApplyModes =
        new(StringComparer.OrdinalIgnoreCase) { "allOrNothing", "partial" };

    private static readonly HashSet<string> ValidClipboardActions =
        new(StringComparer.OrdinalIgnoreCase) { "notify", "passive", "active" };

    private static readonly HashSet<string> ValidLogLevels =
        new(StringComparer.OrdinalIgnoreCase) { "trace", "debug", "info", "warn", "error" };

    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;

    public SettingsStore(AppPaths paths, JsonFileStore? store = null)
    {
        _paths = paths;
        _store = store ?? new JsonFileStore();
    }

    /// <summary>
    /// settings.json を読み込む。ファイルが存在しない場合や破損している場合は
    /// 既定値から再生成し、フォールバックした内容を Issues として返す。
    /// </summary>
    public async Task<GraftResult<Settings>> LoadAsync(CancellationToken ct = default)
    {
        var readResult = await _store
            .ReadWithRecoveryAsync(_paths.SettingsFilePath, static () => new Settings(), JsonFileStore.DefaultOptions, ct)
            .ConfigureAwait(false);

        var issues = new List<GraftIssue>(readResult.Issues);
        var fixedSettings = Validate(readResult.Value, issues);
        return GraftResult<Settings>.Ok(fixedSettings, issues);
    }

    /// <summary>settings.json を書き込む。</summary>
    public async Task SaveAsync(Settings settings, CancellationToken ct = default)
        => await _store.WriteAsync(_paths.SettingsFilePath, settings, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);

    /// <summary>
    /// settings.json（および指定時は projects.json）を指定ディレクトリへ書き出す。
    /// </summary>
    public async Task<GraftResult<IReadOnlyList<string>>> ExportAsync(
        string destinationDirectory, SettingsExportScope scope, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        var exported = new List<string>();

        var settingsDest = Path.Combine(destinationDirectory, "settings.json");
        if (!await _store.CopyAsync(_paths.SettingsFilePath, settingsDest, ct).ConfigureAwait(false))
        {
            return GraftResult<IReadOnlyList<string>>.Fail(
                ErrorCode.E404, detail: "settings.json が見つかりません。", path: _paths.SettingsFilePath);
        }
        exported.Add(settingsDest);

        if (scope == SettingsExportScope.IncludeProjects)
        {
            var projectsDest = Path.Combine(destinationDirectory, "projects.json");
            if (await _store.CopyAsync(_paths.ProjectsFilePath, projectsDest, ct).ConfigureAwait(false))
            {
                exported.Add(projectsDest);
            }
        }

        return GraftResult<IReadOnlyList<string>>.Ok(exported);
    }

    /// <summary>
    /// 指定ディレクトリの settings.json（および指定時は projects.json）を取り込む。
    /// 取り込み前に settings.json がJSONとして妥当かを検証する。
    /// </summary>
    public async Task<GraftResult<Settings>> ImportAsync(
        string sourceDirectory, SettingsExportScope scope, CancellationToken ct = default)
    {
        var sourceSettings = Path.Combine(sourceDirectory, "settings.json");
        var validated = await _store
            .ValidateJsonAsync<Settings>(sourceSettings, JsonFileStore.DefaultOptions, ct)
            .ConfigureAwait(false);
        if (!validated.IsSuccess)
        {
            return validated;
        }

        await _store.CopyAsync(sourceSettings, _paths.SettingsFilePath, ct).ConfigureAwait(false);

        if (scope == SettingsExportScope.IncludeProjects)
        {
            var sourceProjects = Path.Combine(sourceDirectory, "projects.json");
            await _store.CopyAsync(sourceProjects, _paths.ProjectsFilePath, ct).ConfigureAwait(false);
        }

        return await LoadAsync(ct).ConfigureAwait(false);
    }

    private static Settings Validate(Settings raw, List<GraftIssue> issues)
    {
        // ネストしたセクションが JSON 上で明示的に null にされていても
        // 落ちないよう、検証前に既定インスタンスへ置き換える。
        var safe = raw with
        {
            ClipboardWatch = raw.ClipboardWatch ?? new ClipboardWatchSettings(),
            Backup = raw.Backup ?? new BackupSettings(),
            Matching = raw.Matching ?? new MatchingSettings(),
            Encoding = raw.Encoding ?? new EncodingSettings(),
            Syntax = raw.Syntax ?? new SyntaxSettings(),
            Diff = raw.Diff ?? new DiffSettings(),
            Safety = raw.Safety ?? new SafetySettings(),
            Context = raw.Context ?? new ContextSettings(),
            Hooks = raw.Hooks ?? new HookSettings(),
            Git = raw.Git ?? new GitSettings(),
            Editor = raw.Editor ?? new EditorSettings(),
        };

        return safe with
        {
            Theme = NormalizeChoice(safe.Theme, ValidThemes, "system", "theme", issues),
            ApplyMode = NormalizeChoice(safe.ApplyMode, ValidApplyModes, "allOrNothing", "applyMode", issues),
            LogLevel = NormalizeChoice(safe.LogLevel, ValidLogLevels, "info", "logLevel", issues),
            Hotkey = NormalizeNotEmpty(safe.Hotkey, "Ctrl+Alt+V", "hotkey", issues),
            ClipboardWatch = ValidateClipboardWatch(safe.ClipboardWatch, issues),
            Backup = ValidateBackup(safe.Backup, issues),
            Matching = ValidateMatching(safe.Matching, issues),
            Diff = ValidateDiff(safe.Diff, issues),
            Safety = ValidateSafety(safe.Safety, issues),
            Context = ValidateContext(safe.Context, issues),
            Hooks = ValidateHooks(safe.Hooks, issues),
            Editor = ValidateEditor(safe.Editor, issues),
        };
    }

    private static EditorSettings ValidateEditor(EditorSettings s, List<GraftIssue> issues)
        => s with
        {
            FontSize = NormalizeRange(s.FontSize, 6.0, 72.0, 13.0, "editor.fontSize", issues),
            TabSize = NormalizeMin(s.TabSize, 1, 4, "editor.tabSize", issues),
        };

    private static ClipboardWatchSettings ValidateClipboardWatch(ClipboardWatchSettings s, List<GraftIssue> issues)
        => s with
        {
            Action = NormalizeChoice(s.Action, ValidClipboardActions, "notify", "clipboardWatch.action", issues),
        };

    private static BackupSettings ValidateBackup(BackupSettings s, List<GraftIssue> issues)
        => s with
        {
            MaxRevisions = NormalizeMin(s.MaxRevisions, 0, 100, "backup.maxRevisions", issues),
            MaxTotalMB = NormalizeMin(s.MaxTotalMB, 0, 500, "backup.maxTotalMB", issues),
        };

    private static MatchingSettings ValidateMatching(MatchingSettings s, List<GraftIssue> issues)
        => s with
        {
            SimilarityThreshold =
                NormalizeRange(s.SimilarityThreshold, 0.0, 1.0, 0.85, "matching.similarityThreshold", issues),
            RangeWarningLines = NormalizeMin(s.RangeWarningLines, 1, 300, "matching.rangeWarningLines", issues),
        };

    private static DiffSettings ValidateDiff(DiffSettings s, List<GraftIssue> issues)
        => s with { ContextLines = NormalizeMin(s.ContextLines, 0, 3, "diff.contextLines", issues) };

    private static SafetySettings ValidateSafety(SafetySettings s, List<GraftIssue> issues)
    {
        var hasExtensions = s.AllowedExtensions is { Count: > 0 };
        if (!hasExtensions)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E404,
                detail: "safety.allowedExtensions が空のため既定の拡張子一覧を使用します。",
                severity: Severity.Warning));
        }

        return s with
        {
            MaxFileSizeMB = NormalizeMin(s.MaxFileSizeMB, 1, 10, "safety.maxFileSizeMB", issues),
            MaxFilesPerRevision = NormalizeMin(s.MaxFilesPerRevision, 1, 200, "safety.maxFilesPerRevision", issues),
            AllowedExtensions = hasExtensions ? s.AllowedExtensions : new SafetySettings().AllowedExtensions,
        };
    }

    private static ContextSettings ValidateContext(ContextSettings s, List<GraftIssue> issues)
        => s with
        {
            TokenRatio = NormalizePositive(s.TokenRatio, 2.5, "context.tokenRatio", issues),
            TokenWarnThreshold = NormalizeMin(s.TokenWarnThreshold, 1, 50000, "context.tokenWarnThreshold", issues),
        };

    private static HookSettings ValidateHooks(HookSettings s, List<GraftIssue> issues)
        => s with { TimeoutSec = NormalizeMin(s.TimeoutSec, 1, 120, "hooks.timeoutSec", issues) };

    private static string NormalizeChoice(
        string? value, HashSet<string> allowed, string fallback, string key, List<GraftIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(value) && allowed.Contains(value))
        {
            return value;
        }

        issues.Add(GraftIssue.Of(ErrorCode.E404,
            detail: $"{key} の値 \"{value}\" は不正なため既定値 \"{fallback}\" を使用します。",
            severity: Severity.Warning));
        return fallback;
    }

    private static string NormalizeNotEmpty(string? value, string fallback, string key, List<GraftIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        issues.Add(GraftIssue.Of(ErrorCode.E404,
            detail: $"{key} が未指定のため既定値 \"{fallback}\" を使用します。",
            severity: Severity.Warning));
        return fallback;
    }

    private static int NormalizeMin(int value, int min, int fallback, string key, List<GraftIssue> issues)
    {
        if (value >= min)
        {
            return value;
        }

        issues.Add(GraftIssue.Of(ErrorCode.E404,
            detail: $"{key} の値 {value} は {min} 以上である必要があるため既定値 {fallback} を使用します。",
            severity: Severity.Warning));
        return fallback;
    }

    private static double NormalizeRange(
        double value, double min, double max, double fallback, string key, List<GraftIssue> issues)
    {
        if (value >= min && value <= max)
        {
            return value;
        }

        issues.Add(GraftIssue.Of(ErrorCode.E404,
            detail: $"{key} の値 {value} は {min}〜{max} の範囲外のため既定値 {fallback} を使用します。",
            severity: Severity.Warning));
        return fallback;
    }

    private static double NormalizePositive(double value, double fallback, string key, List<GraftIssue> issues)
    {
        if (value > 0)
        {
            return value;
        }

        issues.Add(GraftIssue.Of(ErrorCode.E404,
            detail: $"{key} の値 {value} は正の数である必要があるため既定値 {fallback} を使用します。",
            severity: Severity.Warning));
        return fallback;
    }
}
