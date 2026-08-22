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
    // テーマプリセット9種＋システム追従（検討書「テーマプリセット9種」）。既存の
    // "dark"/"light"/"system"はそのまま残し、7つのプリセットidを追加しただけなので、
    // 古いsettings.jsonの値はここでも引き続き妥当な値として扱われる。idの綴りは
    // Graft.Themes.ThemeManager.ParseTheme と揃える（対応表を二重に持たない）。
    private static readonly HashSet<string> ValidThemes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "dark", "light", "system",
            "sepia", "github", "solarized-light", "solarized-dark", "nord", "dracula", "night",
        };

    // ツールチップ4段階（検討書「ツールチップの4段階化」）。既存の"off"/"standard"/"detailed"は
    // そのまま残し、"minimal"（最低限）を追加しただけなので、古いsettings.jsonの値は
    // 引き続き妥当な値として扱われる。
    private static readonly HashSet<string> ValidTooltipDetails =
        new(StringComparer.OrdinalIgnoreCase) { "off", "minimal", "standard", "detailed" };

    private static readonly HashSet<string> ValidApplyModes =
        new(StringComparer.OrdinalIgnoreCase) { "allOrNothing", "partial" };

    private static readonly HashSet<string> ValidClipboardActions =
        new(StringComparer.OrdinalIgnoreCase) { "notify", "passive", "active" };

    private static readonly HashSet<string> ValidLogLevels =
        new(StringComparer.OrdinalIgnoreCase) { "trace", "debug", "info", "warn", "error" };

    private static readonly HashSet<string> ValidCloseBehaviors =
        new(StringComparer.OrdinalIgnoreCase) { "exit", "tray" };

    // 検討書「インデントガイド（縦線）」の3モード。既定は"foldable"。
    private static readonly HashSet<string> ValidIndentGuideModes =
        new(StringComparer.OrdinalIgnoreCase) { "none", "foldable", "all" };

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
        var fixedSettings = Validate(readResult.Value, issues, ValidationContext.Load);
        return GraftResult<Settings>.Ok(fixedSettings, issues);
    }

    /// <summary>settings.json を書き込む。</summary>
    public async Task SaveAsync(Settings settings, CancellationToken ct = default)
        => await _store.WriteAsync(_paths.SettingsFilePath, settings, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);

    /// <summary>
    /// 指定した設定値を検証するだけで、ディスクへは書き込まない。
    ///
    /// <see cref="LoadAsync"/>が使う<see cref="Validate"/>は「読み込んだ値が不正だった場合、
    /// 既定値へ差し替えて延命する」ためのものであり、settings.jsonが外部改変や破損で
    /// 壊れていても起動できることを優先する（13.1章）。しかし設定画面が即時反映方式
    /// （変更のたびに保存する）へ移行したことで、同じ延命ロジックを保存前にも使ってしまうと
    /// 「画面に入力されている値」と「実際にディスクへ書き込まれる値」が黙って食い違う
    /// 事故になる（例: 上限を-1に打ち替えたら、画面には-1が残ったまま裏で既定値が
    /// 保存される）。そこでこのメソッドでは同じ検証規則を流用しつつ、正規化結果を
    /// 「保存してよい値」としてではなく、あくまで「この入力に何が問題あるか」を
    /// 判定するために使う。呼び出し側（<c>SettingsViewModel</c>）は
    /// <see cref="GraftResult{T}.Issues"/>が1件でもあれば保存自体を見送る。
    /// </summary>
    public static GraftResult<Settings> ValidateOnly(Settings raw)
    {
        var issues = new List<GraftIssue>();

        // ValidationContext.PreSaveを渡すことで、Validate()配下のNormalize*ヘルパーが
        // 生成するDetail文自体を「既定値Xを使用します」ではなく「この値は保存されません」へ
        // 出し分ける（バグ2の対応）。以前はDetailをLoadAsync向けの文言のまま使い回し、
        // コード（Summary/Remedy）だけをE406へ差し替えていたため、「既定値100を使用します」
        // という実態と異なる文言が残っていた（実機で確認済み: 実際には直前の正しい値のまま
        // 保存されない）。Detailの生成自体を文脈で分けることで、E404（読み込み時・本当に
        // 既定値へ差し替える）とE406（保存前・何も差し替えず保留するだけ）のどちらでも
        // 正確な説明になる。
        var normalized = Validate(raw, issues, ValidationContext.PreSave);

        // Detailは上のValidationContextで既に保存前検証向けの文言になっているため、
        // ここではコード（E404→E406）だけを差し替える。Summary/Remedyはコードから
        // 導出されるため、これだけで「入力値が保存条件を満たしていない」
        // 「値を修正すると自動的に保存されます」という表示に揃う。
        var remapped = issues
            .Select(issue => issue.Code == ErrorCode.E404 ? issue with { Code = ErrorCode.E406 } : issue)
            .ToList();

        return GraftResult<Settings>.Ok(normalized, remapped);
    }

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

    /// <summary>
    /// <see cref="Validate"/>配下のNormalize*ヘルパーが生成するDetail文を、呼び出し元の文脈に
    /// よって出し分けるための区別。同じ検証規則（範囲・選択肢のチェック）を
    /// <see cref="LoadAsync"/>（起動時の読み込み）と<see cref="ValidateOnly"/>（保存前検証）の
    /// 両方で共有しているが、不正値に対して実際に起きることが文脈で異なるため
    /// （バグ2の対応: 「既定値を使用します」は読み込み時にしか成り立たない）。
    /// </summary>
    private enum ValidationContext
    {
        /// <summary>起動時などにsettings.jsonを読み込む文脈。不正値は本当に既定値へ差し替わる。</summary>
        Load,

        /// <summary>設定画面での保存前検証の文脈。不正値は差し替えず、保存自体を保留するだけ。</summary>
        PreSave,
    }

    private static Settings Validate(Settings raw, List<GraftIssue> issues, ValidationContext context)
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
            Theme = NormalizeChoice(safe.Theme, ValidThemes, "system", "theme", issues, context),
            TooltipDetail = NormalizeChoice(safe.TooltipDetail, ValidTooltipDetails, "standard", "tooltipDetail", issues, context),
            // 実機不具合対応: 既定値をSettings.ApplyModeと揃えて"partial"にする（Settings.csのコメント参照）。
            // NormalizeChoiceはsafe.ApplyModeが有効な値（allOrNothing/partialのいずれか）なら
            // その値をそのまま返すため、既に保存済みのsettings.jsonでapplyModeが明示されている
            // 利用者の設定はこの既定値変更の影響を受けない（fallbackはキーが無い・不正なときだけ使う）。
            ApplyMode = NormalizeChoice(safe.ApplyMode, ValidApplyModes, "partial", "applyMode", issues, context),
            LogLevel = NormalizeChoice(safe.LogLevel, ValidLogLevels, "info", "logLevel", issues, context),
            CloseBehavior = NormalizeChoice(safe.CloseBehavior, ValidCloseBehaviors, "exit", "closeBehavior", issues, context),
            Hotkey = NormalizeNotEmpty(safe.Hotkey, "Ctrl+Alt+V", "hotkey", issues, context),
            ClipboardWatch = ValidateClipboardWatch(safe.ClipboardWatch, issues, context),
            Backup = ValidateBackup(safe.Backup, issues, context),
            Matching = ValidateMatching(safe.Matching, issues, context),
            Diff = ValidateDiff(safe.Diff, issues, context),
            Safety = ValidateSafety(safe.Safety, issues, context),
            Context = ValidateContext(safe.Context, issues, context),
            Hooks = ValidateHooks(safe.Hooks, issues, context),
            Editor = ValidateEditor(safe.Editor, issues, context),
        };
    }

    private static EditorSettings ValidateEditor(EditorSettings s, List<GraftIssue> issues, ValidationContext context)
        => s with
        {
            FontSize = NormalizeRange(s.FontSize, 6.0, 72.0, 13.0, "editor.fontSize", issues, context),
            TabSize = NormalizeMin(s.TabSize, 1, 4, "editor.tabSize", issues, context),
            IndentGuideMode = NormalizeChoice(
                s.IndentGuideMode, ValidIndentGuideModes, "foldable", "editor.indentGuideMode", issues, context),
        };

    private static ClipboardWatchSettings ValidateClipboardWatch(
        ClipboardWatchSettings s, List<GraftIssue> issues, ValidationContext context)
        => s with
        {
            Action = NormalizeChoice(s.Action, ValidClipboardActions, "notify", "clipboardWatch.action", issues, context),
        };

    private static BackupSettings ValidateBackup(BackupSettings s, List<GraftIssue> issues, ValidationContext context)
        => s with
        {
            MaxRevisions = NormalizeMin(s.MaxRevisions, 0, 100, "backup.maxRevisions", issues, context),
            MaxTotalMB = NormalizeMin(s.MaxTotalMB, 0, 500, "backup.maxTotalMB", issues, context),
        };

    private static MatchingSettings ValidateMatching(MatchingSettings s, List<GraftIssue> issues, ValidationContext context)
        => s with
        {
            SimilarityThreshold =
                NormalizeRange(s.SimilarityThreshold, 0.0, 1.0, 0.85, "matching.similarityThreshold", issues, context),
            RangeWarningLines = NormalizeMin(s.RangeWarningLines, 1, 300, "matching.rangeWarningLines", issues, context),
        };

    private static DiffSettings ValidateDiff(DiffSettings s, List<GraftIssue> issues, ValidationContext context)
        => s with { ContextLines = NormalizeMin(s.ContextLines, 0, 3, "diff.contextLines", issues, context) };

    private static SafetySettings ValidateSafety(SafetySettings s, List<GraftIssue> issues, ValidationContext context)
    {
        var hasExtensions = s.AllowedExtensions is { Count: > 0 };
        if (!hasExtensions)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E404,
                detail: DescribeInvalid("safety.allowedExtensions が空です。", context, "既定の拡張子一覧を使用します。"),
                severity: Severity.Warning));
        }

        return s with
        {
            MaxFileSizeMB = NormalizeMin(s.MaxFileSizeMB, 1, 10, "safety.maxFileSizeMB", issues, context),
            MaxFilesPerRevision = NormalizeMin(s.MaxFilesPerRevision, 1, 200, "safety.maxFilesPerRevision", issues, context),
            AllowedExtensions = hasExtensions ? s.AllowedExtensions : new SafetySettings().AllowedExtensions,
        };
    }

    private static ContextSettings ValidateContext(ContextSettings s, List<GraftIssue> issues, ValidationContext context)
        => s with
        {
            TokenRatio = NormalizePositive(s.TokenRatio, 2.5, "context.tokenRatio", issues, context),
            TokenWarnThreshold = NormalizeMin(s.TokenWarnThreshold, 1, 50000, "context.tokenWarnThreshold", issues, context),
        };

    private static HookSettings ValidateHooks(HookSettings s, List<GraftIssue> issues, ValidationContext context)
        => s with { TimeoutSec = NormalizeMin(s.TimeoutSec, 1, 120, "hooks.timeoutSec", issues, context) };

    /// <summary>
    /// バグ2の対応: Normalize*ヘルパー共通の「不正値をどう説明するか」の出し分け。
    /// <paramref name="requirement"/>は問題そのものの説明（例: "backup.maxRevisions の値 -5 は
    /// 0 以上である必要があります。"）。Loadでは実際に既定値へ差し替わるのでその旨を続け、
    /// PreSaveでは何も差し替えず保存を保留するだけなので、その旨を続ける
    /// （「既定値100を使用します」という実態と異なる文言をPreSaveで出さないため）。
    /// PreSave側は「値を修正すると自動的に保存されます」まで書かない。この文言は
    /// ErrorCode.E406のRemedy（<see cref="Graft.Core.ErrorCatalog"/>）が既に持っており、
    /// 表示時にDetailの直後へ「（対処: …）」として連結される
    /// （<see cref="Graft.Views.Converters"/>のIssueToDisplayText参照）ため、ここでも書くと
    /// 同じ一文が2回表示されてしまう。
    /// </summary>
    private static string DescribeInvalid(string requirement, ValidationContext context, string loadFallbackPhrase)
        => context == ValidationContext.Load
            ? $"{requirement}{loadFallbackPhrase}"
            : $"{requirement}この値は保存されていません。";

    private static string NormalizeChoice(
        string? value, HashSet<string> allowed, string fallback, string key, List<GraftIssue> issues, ValidationContext context)
    {
        if (!string.IsNullOrWhiteSpace(value) && allowed.Contains(value))
        {
            return value;
        }

        issues.Add(GraftIssue.Of(ErrorCode.E404,
            detail: DescribeInvalid($"{key} の値 \"{value}\" は不正です。", context, $"既定値 \"{fallback}\" を使用します。"),
            severity: Severity.Warning));
        return fallback;
    }

    private static string NormalizeNotEmpty(
        string? value, string fallback, string key, List<GraftIssue> issues, ValidationContext context)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        issues.Add(GraftIssue.Of(ErrorCode.E404,
            detail: DescribeInvalid($"{key} が未指定です。", context, $"既定値 \"{fallback}\" を使用します。"),
            severity: Severity.Warning));
        return fallback;
    }

    private static int NormalizeMin(
        int value, int min, int fallback, string key, List<GraftIssue> issues, ValidationContext context)
    {
        if (value >= min)
        {
            return value;
        }

        issues.Add(GraftIssue.Of(ErrorCode.E404,
            detail: DescribeInvalid($"{key} の値 {value} は {min} 以上である必要があります。", context, $"既定値 {fallback} を使用します。"),
            severity: Severity.Warning));
        return fallback;
    }

    private static double NormalizeRange(
        double value, double min, double max, double fallback, string key, List<GraftIssue> issues, ValidationContext context)
    {
        if (value >= min && value <= max)
        {
            return value;
        }

        issues.Add(GraftIssue.Of(ErrorCode.E404,
            detail: DescribeInvalid($"{key} の値 {value} は {min}〜{max} の範囲外です。", context, $"既定値 {fallback} を使用します。"),
            severity: Severity.Warning));
        return fallback;
    }

    private static double NormalizePositive(
        double value, double fallback, string key, List<GraftIssue> issues, ValidationContext context)
    {
        if (value > 0)
        {
            return value;
        }

        issues.Add(GraftIssue.Of(ErrorCode.E404,
            detail: DescribeInvalid($"{key} の値 {value} は正の数である必要があります。", context, $"既定値 {fallback} を使用します。"),
            severity: Severity.Warning));
        return fallback;
    }
}
