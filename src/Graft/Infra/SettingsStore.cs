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
            Update = raw.Update ?? new UpdateSettings(),
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
            Update = ValidateUpdate(safe.Update, issues, context),
        };
    }

    private static EditorSettings ValidateEditor(EditorSettings s, List<GraftIssue> issues, ValidationContext context)
        => s with
        {
            FontSize = NormalizeRange(s.FontSize, 6.0, 72.0, 13.0, "editor.fontSize", issues, context),
            // 上限16: タブ幅は「見た目のインデント量」であり、実測でtabSize=100000を入れると
            // 1階層のインデントだけで画面幅を埋め尽くし編集不能になる。一般的なエディタ
            // （VS Code既定8・多くのスタイルガイドは2〜8）を大きく超える実用上の余裕を見て16とした。
            TabSize = NormalizeIntRange(s.TabSize, 1, 16, 4, "editor.tabSize", issues, context),
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
            // 上限100000: 1リビジョン=back/配下に1フォルダを作る設計（AppPaths.GetRevisionDirectory）
            // のため、桁違いに大きい値は世代整理（Prune）のたびに大量のフォルダを列挙・削除する
            // コストに直結する。実務で必要になる保持数（数十〜数百）に対して十分な余裕を見た
            // 上限として100000とした。
            MaxRevisions = NormalizeIntRange(s.MaxRevisions, 0, 100000, 100, "backup.maxRevisions", issues, context),
            // 上限1048576（=1024MB×1024＝1TB相当）: 実測でmaxTotalMB=2147483647（≒2000TB相当）が
            // 警告なく採用されていた。ディスク容量として非現実的な値を弾きつつ、大規模プロジェクトの
            // バックアップ用途でも余裕がある値として1TBを上限にした。
            MaxTotalMB = NormalizeIntRange(s.MaxTotalMB, 0, 1_048_576, 500, "backup.maxTotalMB", issues, context),
        };

    private static MatchingSettings ValidateMatching(MatchingSettings s, List<GraftIssue> issues, ValidationContext context)
        => s with
        {
            SimilarityThreshold =
                NormalizeRange(s.SimilarityThreshold, 0.0, 1.0, 0.85, "matching.similarityThreshold", issues, context),
            // 上限100000: アンカー省略記法の警告閾値（行数）。実務上の警告対象範囲を大きく
            // 超える値を弾くための余裕を見た上限で、極端な値（int.MaxValue等）は「警告が
            // 事実上絶対に出ない」設定になってしまい安全機構の意味が薄れる。
            RangeWarningLines = NormalizeIntRange(s.RangeWarningLines, 1, 100000, 300, "matching.rangeWarningLines", issues, context),
        };

    private static DiffSettings ValidateDiff(DiffSettings s, List<GraftIssue> issues, ValidationContext context)
        // 上限999: diffの前後コンテキスト行数。実測でcontextLines=2147483647が警告なく採用され、
        // 実質「diff全体を常に展開表示する」設定と同義になっていた（差分表示が意味を成さなくなる）。
        => s with { ContextLines = NormalizeIntRange(s.ContextLines, 0, 999, 3, "diff.contextLines", issues, context) };

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
            // 上限1024（=1GB相当）: 実測でmaxFileSizeMB=2147483647（≒2000TB相当）が警告なく
            // 採用されていた。1ファイルの安全機構としての意味を保つため、通常の開発用途で
            // 扱うテキストファイルとして十分すぎる余裕（1GB）を上限にした。
            MaxFileSizeMB = NormalizeIntRange(s.MaxFileSizeMB, 1, 1024, 10, "safety.maxFileSizeMB", issues, context),
            // 上限100000: 1リビジョンに含めるファイル数の上限。極端な値は取り込み処理・
            // 一覧表示のいずれもUIをフリーズさせかねないため、実務上のプロジェクト規模を
            // 大きく超える余裕を見た値とした。
            MaxFilesPerRevision = NormalizeIntRange(s.MaxFilesPerRevision, 1, 100000, 200, "safety.maxFilesPerRevision", issues, context),
            AllowedExtensions = hasExtensions ? s.AllowedExtensions : new SafetySettings().AllowedExtensions,
        };
    }

    private static ContextSettings ValidateContext(ContextSettings s, List<GraftIssue> issues, ValidationContext context)
        => s with
        {
            // 範囲0.1〜100: 「文字数 / この値」でトークン数を概算する比率（TokenEstimator）。
            // 実測でtokenRatio=0.00001のような極端に小さい値を入れると、数MB程度の選択でも
            // 概算トークン数がint桁あふれで負数になり（不具合3）、上限警告（ExceedsWarnThreshold）
            // が常にfalseになって効かなくなることを確認した。下限0.1は「1文字＝10トークン」
            // 相当（実在のトークナイザでは起こりえないほど非効率な側）、上限100は
            // 「100文字＝1トークン」相当（同じく非現実的な側）に余裕を持たせて挟んだもので、
            // 現実のどのトークナイザ・言語の比率もこの範囲に収まる。
            TokenRatio = NormalizeRange(s.TokenRatio, 0.1, 100.0, 2.5, "context.tokenRatio", issues, context),
            // 上限10000000（1000万トークン）: 現行の主要LLMのコンテキスト長を大きく超える
            // 余裕を見た上限。int.MaxValue系の値は「警告が事実上絶対に出ない」設定と
            // 同義になり、安全機構の意味が薄れるため弾く。
            TokenWarnThreshold = NormalizeIntRange(s.TokenWarnThreshold, 1, 10_000_000, 50000, "context.tokenWarnThreshold", issues, context),
        };

    private static HookSettings ValidateHooks(HookSettings s, List<GraftIssue> issues, ValidationContext context)
        // 上限3600（1時間）: 実測でtimeoutSec=2147483647（約68年）が警告なく採用されていた。
        // フックがハングした場合にアプリが延々応答不能になるのを防ぐタイムアウト機構
        // としての意味を保つため、通常のビルド・テスト系フックとして十分すぎる1時間を上限にした。
        => s with { TimeoutSec = NormalizeIntRange(s.TimeoutSec, 1, 3600, 120, "hooks.timeoutSec", issues, context) };

    /// <summary>
    /// 異常系点検「中」2件目の対応: 自動更新の確認先URL（<see cref="UpdateSettings.CheckUrl"/>）は
    /// 従来検証対象に入っておらず、実測で空文字を設定しても警告0件でそのまま採用され、
    /// 実際に更新確認を行うと<see cref="Graft.Core.Update.GitHubReleaseFeed.GetLatestReleaseAsync"/>が
    /// 空URLで黙ってnullを返すため「更新の確認に失敗しました」としか出ず、原因（URLが空である
    /// こと）が利用者に伝わらなかった。ここで「空でない」「絶対URL」「スキームがhttps」を
    /// 検証し、どれに違反したかが分かる日本語のDetailを出す。https以外（http等）を弾くのは、
    /// 更新確認の応答（ダウンロードURLを含む）が平文でやり取りされる経路を設定画面から
    /// 作れてしまうことを防ぐため（実行ファイルの差し替えに繋がりうる通信のため、
    /// 他の設定項目より厳しく見る）。
    /// </summary>
    private static UpdateSettings ValidateUpdate(UpdateSettings s, List<GraftIssue> issues, ValidationContext context)
        => s with { CheckUrl = NormalizeCheckUrl(s.CheckUrl, new UpdateSettings().CheckUrl, issues, context) };

    private static string NormalizeCheckUrl(
        string? value, string fallback, List<GraftIssue> issues, ValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(GraftIssue.Of(ErrorCode.E404,
                detail: DescribeInvalid("update.checkUrl が空です。", context, $"既定値 \"{fallback}\" を使用します。"),
                severity: Severity.Warning));
            return fallback;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            issues.Add(GraftIssue.Of(ErrorCode.E404,
                detail: DescribeInvalid($"update.checkUrl の値 \"{value}\" は絶対URLではありません。", context, $"既定値 \"{fallback}\" を使用します。"),
                severity: Severity.Warning));
            return fallback;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E404,
                detail: DescribeInvalid($"update.checkUrl のスキーム \"{uri.Scheme}\" はhttpsである必要があります。", context, $"既定値 \"{fallback}\" を使用します。"),
                severity: Severity.Warning));
            return fallback;
        }

        return value;
    }

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

    /// <summary>
    /// 異常系点検「中」2件目の対応: 下限のみを見る<c>NormalizeMin</c>だった頃は、
    /// <c>editor.tabSize=100000</c>・<c>safety.maxFileSizeMB=2147483647</c>・
    /// <c>backup.maxTotalMB=2147483647</c>・<c>hooks.timeoutSec=2147483647</c>・
    /// <c>diff.contextLines=2147483647</c>のような、下限は満たすが実用上あり得ない値が
    /// 警告0件のまま採用されていた（settings.jsonを手で書いてLoadAsyncする実測で確認済み）。
    /// tabSize=100000は表示そのものが破綻し、maxFileSizeMB等のint.MaxValue系は「上限が
    /// 無いのと実質同じ」で安全機構（13章）の意味が薄れる。すべての呼び出し元に
    /// <paramref name="max"/>を必須で持たせ、値の妥当な範囲を1箇所（このメソッド）で
    /// 強制する形にした。各上限の根拠は呼び出し元のコメントを参照。
    /// </summary>
    private static int NormalizeIntRange(
        int value, int min, int max, int fallback, string key, List<GraftIssue> issues, ValidationContext context)
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
}
