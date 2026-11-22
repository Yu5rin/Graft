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
        var custom = Sanitize(result.Value.Focuses)
            .Where(f => !f.IsBuiltIn && !builtInIds.Contains(f.Id))
            .ToList();
        var merged = BuiltIns.Concat(custom).ToList();
        return GraftResult<IReadOnlyList<PromptFocus>>.Ok(merged, result.Issues);
    }

    /// <summary>
    /// 手で編集された・別の版が書いた focus.json の不正な値で、依頼文の組み立てや設定画面が落ちないよう、読み込み時に整える。
    /// <list type="bullet">
    /// <item>一覧そのものが null（<c>"focuses": null</c>）・要素が null は無いものとする。</item>
    /// <item>Id が空の行と、同じ Id の 2 行目以降は捨てる（Id は選択の記憶の鍵なので、空・重複は扱えない）。名前が空の行も捨てる
    /// （チップに出せない）。</item>
    /// <item>見出し・ツールチップ・指示文の null は、見出しなら「自分用」、それ以外は空文字へ寄せる。指示文が空なら
    /// 「## 名前」だけの文にする（<see cref="EnsureHeading"/>。空のままだと依頼文に見出しの無い空行が混ざる）。</item>
    /// </list>
    /// 捨てた行は、次に自作の観点を保存したときにファイルからも消える。
    /// </summary>
    private static IEnumerable<PromptFocus> Sanitize(IReadOnlyList<PromptFocus>? focuses)
    {
        if (focuses is null) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var focus in focuses)
        {
            // 手で書かれた JSON では、必須のはずのプロパティが null で入りうる（型の上は非 null でも実際は null）。
            if (focus is null) continue;
            var id = (string?)focus.Id;
            var name = ((string?)focus.Name)?.Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrEmpty(name) || !seen.Add(id)) continue;

            var body = (string?)focus.Body;
            yield return focus with
            {
                Id = id,
                Name = name,
                Group = string.IsNullOrWhiteSpace(focus.Group) ? PromptFocusGroups.Mine : focus.Group,
                Tooltip = focus.Tooltip ?? string.Empty,
                Body = string.IsNullOrWhiteSpace(body) ? EnsureHeading(name, string.Empty) : body,
            };
        }
    }

    /// <summary>
    /// 自作の観点を保存する。組み込みは書き出さない。
    /// <para>
    /// 【書けなかったときは失敗で返す】 <see cref="JsonFileStore.WriteAsync{T}"/> は、読み取りに失敗したファイル（E402 の保護中）には
    /// 何も書かずに戻り、入出力の失敗は例外にする。どちらも成功として扱うと、設定画面が書かれていない観点を
    /// 「追加しました」と伝えてしまう。呼び出し側が利用者へ失敗を伝えられるよう、E402 で返す。
    /// </para>
    /// </summary>
    public async Task<GraftResult<bool>> SaveAsync(IReadOnlyList<PromptFocus> focuses, CancellationToken ct = default)
    {
        if (_jsonStore.IsWriteProtected(FocusFilePath))
        {
            return GraftResult<bool>.Fail(
                ErrorCode.E402,
                "focus.json を読み取れなかったため、上書き保存しませんでした（他のアプリが開いている可能性があります。閉じてから、もう一度試してください）。",
                path: FocusFilePath);
        }

        var custom = focuses.Where(f => !f.IsBuiltIn).ToArray();
        try
        {
            await _jsonStore.WriteAsync(FocusFilePath, new PromptFocusFile { Focuses = custom }, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E402, ExceptionMessages.Describe(ex), path: FocusFilePath);
        }

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
