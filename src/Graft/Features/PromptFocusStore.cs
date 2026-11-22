using Graft.Core;
using Graft.Core.PromptFocus;
using Graft.Infra;

namespace Graft.Features;

/// <summary>focus.json のルート要素。<see cref="PromptTemplateFile"/> と同じ作り。</summary>
internal sealed record PromptFocusFile
{
    public IReadOnlyList<PromptFocus> Focuses { get; init; } = Array.Empty<PromptFocus>();
}

/// <summary>
/// 依頼の観点の管理。組み込みの観点（<see cref="BuiltIns"/>。17件）はコード側で供給して書き出さず、
/// 利用者が足した観点だけを <c>focus.json</c>（<see cref="AppPaths.FocusFilePath"/>）へ保存する。
/// 作りは <see cref="PromptTemplateStore"/> と同じ（<see cref="JsonFileStore"/> の復旧つき読み込み）。
///
/// 【組み込みを書き出さない理由】書き出すと、版を上げて指示文を直したときに、古い指示文が
/// <c>focus.json</c> に居座って新しい文が効かなくなる。コード側が常に最新の唯一の正とする
/// （<see cref="PromptTemplateStore"/> と同じ判断）。
/// </summary>
public sealed class PromptFocusStore
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _jsonStore = new();

    public PromptFocusStore(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>組み込みの観点17件（汎用14＋VBA専用3）。画面の並びの元になる並びで返す。</summary>
    public static IReadOnlyList<PromptFocus> BuiltIns { get; } = PromptFocusBuiltInData.Create();

    /// <summary>focus.json のパス。</summary>
    public string FocusFilePath => _paths.FocusFilePath;

    /// <summary>
    /// 組み込みと自作を合わせて返す（組み込みが先、自作は保存された順＝追加順）。
    /// ファイルが無い・壊れている場合は <see cref="JsonFileStore.ReadWithRecoveryAsync"/> が既定値で
    /// 立て直す（壊れた実物は退避される）ので、組み込みだけは必ず返る。
    /// </summary>
    public async Task<GraftResult<IReadOnlyList<PromptFocus>>> LoadAsync(CancellationToken ct = default)
    {
        var result = await _jsonStore.ReadWithRecoveryAsync(
            FocusFilePath,
            () => new PromptFocusFile(),
            ct: ct).ConfigureAwait(false);
        if (!result.IsSuccess) return GraftResult<IReadOnlyList<PromptFocus>>.Fail(result.Issues);

        // ファイルの中に IsBuiltIn=true の行があっても読み捨てる（手で書き換えられても、
        // 組み込みを偽装して編集不可のまま増えることがないように）。Id が組み込みと衝突する行も捨てる。
        var builtInIds = new HashSet<string>(BuiltIns.Select(b => b.Id), StringComparer.Ordinal);
        var custom = result.Value.Focuses
            .Where(f => !f.IsBuiltIn && !builtInIds.Contains(f.Id))
            .ToList();
        var merged = BuiltIns.Concat(custom).ToList();
        return GraftResult<IReadOnlyList<PromptFocus>>.Ok(merged, result.Issues);
    }

    /// <summary>自作の観点を保存する。組み込みは書き出さない。</summary>
    public async Task<GraftResult<bool>> SaveAsync(IReadOnlyList<PromptFocus> focuses, CancellationToken ct = default)
    {
        var custom = focuses.Where(f => !f.IsBuiltIn).ToArray();
        await _jsonStore.WriteAsync(FocusFilePath, new PromptFocusFile { Focuses = custom }, ct: ct).ConfigureAwait(false);
        return GraftResult<bool>.Ok(true);
    }

    /// <summary>
    /// 指示文の先頭が <c>## </c> で始まっていなければ、<c>## &lt;名前&gt;</c> の行を補う（仕様書6.6）。
    /// 依頼文の中で観点ごとの見出しが揃っていないと、AIがどこからどこまでが1つの観点か読み取れないため、
    /// 保存時に自動で整える。空の指示文も見出しだけの文になる（空のままだと見出しの無い空行が依頼文に混ざる）。
    /// </summary>
    public static string EnsureHeading(string name, string body)
    {
        var trimmed = (body ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\n');
        if (trimmed.StartsWith("## ", StringComparison.Ordinal)) return trimmed;
        var heading = "## " + (string.IsNullOrWhiteSpace(name) ? "観点" : name.Trim());
        return trimmed.Length == 0 ? heading : heading + "\n" + trimmed;
    }
}
