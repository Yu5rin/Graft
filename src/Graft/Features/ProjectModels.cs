using Graft.Core.Vba;

namespace Graft.Features;

/// <summary>適用後フックの定義。仕様書3.1・6.5。</summary>
public sealed record PostApplyHook
{
    /// <summary>フック名。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>実行するコマンド。プロジェクトルートを作業ディレクトリとして起動する。</summary>
    public string Command { get; init; } = string.Empty;
    /// <summary>失敗時の挙動。<see cref="HookFailureAction"/> の値。</summary>
    public string OnFailure { get; init; } = HookFailureAction.Warn;
}

/// <summary>フック失敗時の挙動。仕様書6.5。</summary>
public static class HookFailureAction
{
    /// <summary>記録のみ。</summary>
    public const string Ignore = "ignore";
    /// <summary>警告表示。適用は維持。</summary>
    public const string Warn = "warn";
    /// <summary>ロールバックを提案するダイアログを表示。</summary>
    public const string OfferRollback = "offerRollback";
    /// <summary>自動的にロールバックする。</summary>
    public const string AutoRollback = "autoRollback";
}

/// <summary>プロジェクト単位の設定上書き。仕様書3.1。</summary>
public sealed record ProjectOverrides
{
    /// <summary>追加の除外パターン。</summary>
    public IReadOnlyList<string> Excludes { get; init; } = Array.Empty<string>();
    /// <summary>拡張子の増減。"+.sql" は追加、"-.md" は除外を意味する。</summary>
    public IReadOnlyList<string> AllowedExtensions { get; init; } = Array.Empty<string>();
    /// <summary>新規ファイルのエンコーディング。</summary>
    public string? NewFileEncoding { get; init; }
    /// <summary>
    /// コンテキスト収集（10章）の3状態選択（内容も出す／構成だけ／出さない）のうち、
    /// 既定（内容も出す）から外れているファイルだけを記録する差分方式。
    /// キーはプロジェクトルートからの相対パス（"/"区切り）、値は <c>ContextFileState</c> の
    /// <see cref="Enum.ToString()"/>（"StructureOnly" | "Hidden"）。
    /// <para>
    /// 差分方式にしている理由は2つ。(1) 既定が全部「内容も出す」（3.既定オン）のため、
    /// 大半のファイルは記録不要で済み projects.json が肥大化しない。(2) 新規に追加された
    /// ファイルは記録が無い＝既定のまま「内容も出す」から始まり、後方互換（このキーを
    /// 持たない古い projects.json を読んでも全部既定オンになる）も自然に成り立つ。
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> ContextFileStates { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// 依頼の観点（<c>focus-readable</c> など）の、最後に選んだ Id（仕様書_ブックの情報と依頼の観点 4.1・6.2）。
    /// 既定は空＝何も選ばない（依頼文は従来と同じ）。<see cref="ContextFileStates"/> と同じく、画面で選んだ
    /// 状態の記憶なのでここに置く。キーを持たない既存の projects.json は空のまま読める。
    /// <para>
    /// 一覧に無い Id（削除された自作の観点）と、VBA でないプロジェクトでの VBA 専用の観点は、
    /// 読み込み時に黙って捨てる（<c>PromptFocusSelectionViewModel</c>）。ここでは生の Id を保持するだけで、
    /// 解釈しない。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> PromptFocusIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Excel ブックとの紐付け（仕様書_VBA連携 4.1）。<see cref="Project.Vba"/> が非 null のプロジェクトが
/// VBA プロジェクトで、<see cref="Project.Root"/> はミラーフォルダ（モジュール本文のテキスト）を指す。
/// <para>
/// 【<see cref="ProjectOverrides"/> ではなくここに置く理由】 <see cref="AutoPushAfterApply"/> と
/// <see cref="SaveWorkbookAfterPush"/> を変える画面は、プロジェクトの右クリックメニューのチェック項目で、
/// <c>ProjectOverrides</c> を編集する画面は存在しない（編集されるのはコンテキスト収集の
/// Excludes／ContextFileStates だけ）ため。
/// </para>
/// </summary>
public sealed record VbaLink
{
    /// <summary>紐付けたブックのフルパス（ROT の表示名と照合する。大文字小文字は区別しない）。</summary>
    public string WorkbookFullName { get; init; } = string.Empty;

    /// <summary>適用後に Excel へ自動反映するか。既定は true。</summary>
    public bool AutoPushAfterApply { get; init; } = true;

    /// <summary>反映後にブックを保存するか。既定は false（保存は利用者が Excel で行う）。</summary>
    public bool SaveWorkbookAfterPush { get; init; }

    /// <summary>
    /// 保存時の自動反映（機能 D。仕様書_ブックの情報と依頼の観点 8章）。オンのとき、エディタで VBA のミラー内の同期対象の
    /// モジュールを保存すると、そのモジュールだけを Excel へ送る。<b>既定はオフ</b>: 保存のたびに Excel のコードが変わると、
    /// VBE で同じモジュールを開いている利用者や、実行中のマクロに思わぬ影響が出うるため、利用者が選んでオンにする。
    /// この項目が無い既存の projects.json は、オフで読める。
    /// </summary>
    public bool AutoPushOnSave { get; init; }

    /// <summary>
    /// ブックの情報（仕様書_ブックの情報と依頼の観点 5章）の出し方。<c>"Full"</c>（内容も出す。既定）・
    /// <c>"StructureOnly"</c>（構成だけ）・<c>"Hidden"</c>（出さない）。<c>ProjectOverrides.ContextFileStates</c> と
    /// 同じく、列挙型ではなく名前の文字列で持つ。不明な文字列は <c>Full</c> として読む
    /// （<see cref="Graft.Core.Vba.VbaWorkbookInfoLevels.Parse"/>）。この項目が無い既存の projects.json は既定値で読める。
    /// </summary>
    public string WorkbookInfoState { get; init; } = "Full";

    /// <summary>
    /// 【v0.4 より前の項目。移行のためだけに読む】 ブック全体の「値の見本」のオン・オフ。<b>既定はオフ</b>。
    /// v0.4 で、値はシートごとの見せ方（<see cref="WorkbookInfoSheetViews"/>）に一本化した。
    /// <see cref="WorkbookInfoSheetViews"/> が null（シートごとの見せ方をまだ一度も触っていない）で、この項目がオンの
    /// 既存のプロジェクトは、「全シート 見出し＋N 行（N は設定 <c>Vba.WorkbookInfoSampleRows</c>。数式は
    /// <see cref="WorkbookInfoIncludeFormulas"/> のまま）」として読む（<see cref="ResolveSheetViews"/>）。
    /// 利用者が最初にシートの見せ方を変えた時点で、シートごとの記録へ書き出し、この項目はオフに戻す
    /// （<see cref="WithSheetViews"/>）。新しい版はこの項目をオンにしない。
    /// </summary>
    public bool WorkbookInfoSampleValues { get; init; }

    /// <summary>【v0.4 より前の項目。移行のためだけに読む】 値の見本に数式も含めるか。<see cref="WorkbookInfoSampleValues"/> と同じ扱い。</summary>
    public bool WorkbookInfoIncludeFormulas { get; init; }

    /// <summary>
    /// シートごとの見せ方（仕様書_ブックの情報と依頼の観点 4.1・5.2。v0.4）。<b>シートのコード名</b>に結び付けて持つ
    /// （表示名を変えても外れない）。記録の無いシートは「構成だけ」。消えたシートの記録は残すが、画面には出さない。
    /// <para>
    /// null は「まだ一度も触っていない」（この項目が無い既存の projects.json）で、<see cref="WorkbookInfoSampleValues"/> が
    /// オンなら移行する。空の一覧は「触ったあと、全シート構成だけ」で、移行しない。
    /// </para>
    /// </summary>
    public IReadOnlyList<VbaSheetViewSetting>? WorkbookInfoSheetViews { get; init; }

    /// <summary>
    /// シートごとの見せ方を、コード名で引ける一覧にする。<see cref="WorkbookInfoSheetViews"/> が null で
    /// <see cref="WorkbookInfoSampleValues"/> がオンなら、移行として全シート「見出し＋<paramref name="legacySampleRows"/> 行」
    /// （数式は <see cref="WorkbookInfoIncludeFormulas"/>）にする。値の見本をオンにする確認は、以前に済んでいる扱い。
    /// </summary>
    /// <param name="legacySampleRows">移行のときの N（設定 <c>Vba.WorkbookInfoSampleRows</c>）。</param>
    public VbaSheetViewSet ResolveSheetViews(int legacySampleRows)
    {
        if (WorkbookInfoSheetViews is null)
        {
            return WorkbookInfoSampleValues
                ? new VbaSheetViewSet(
                    new VbaSheetView(VbaSheetViewMode.HeaderRows, VbaSheetViewLimits.ClampHeaderRows(legacySampleRows), WorkbookInfoIncludeFormulas),
                    Array.Empty<KeyValuePair<string, VbaSheetView>>())
                : VbaSheetViewSet.AllStructureOnly;
        }

        return new VbaSheetViewSet(
            VbaSheetView.StructureOnly,
            WorkbookInfoSheetViews.Select(v => new KeyValuePair<string, VbaSheetView>(v.CodeName, v.ToView())));
    }

    /// <summary>
    /// シートごとの見せ方を書き込んだ <see cref="VbaLink"/> を返す。同時に、v0.4 より前の 2 項目
    /// （<see cref="WorkbookInfoSampleValues"/>・<see cref="WorkbookInfoIncludeFormulas"/>）をオフに戻す
    /// （移行は済んだので、以後は新しい項目だけを見る。残すと、利用者が全シートを構成だけに戻しても、
    /// 古い項目から再び「全シート 見出し＋N 行」に移行してしまう）。
    /// 「構成だけ」（既定）と同じ記録は書かない。一覧に載っている記録は、いま画面に無いシート（消えたシート）の分も残す。
    /// </summary>
    /// <param name="views">全シート分の見せ方（<see cref="ResolveSheetViews"/> と同じ一覧の更新後）。</param>
    public VbaLink WithSheetViews(VbaSheetViewSet views) => WithSheetViews(views, null);

    /// <summary>
    /// <see cref="WithSheetViews(VbaSheetViewSet)"/> の、シートの表示名を記録に添える版。
    /// <para>
    /// 【表示名を記録する理由】 記録の鍵はコード名（表示名を変えても外れない）だが、Excel はシートを削除したあとに同じコード名
    /// （<c>Sheet3</c> など）を別のシートへ割り当てうる。コード名だけで引くと、消えたシートの「全部」が、同じコード名の
    /// 別のシートへ確認なしで引き継がれる。表示名も持っておけば、食い違いに気づける（<see cref="ReconcileSheetViews"/>）。
    /// </para>
    /// </summary>
    /// <param name="views">全シート分の見せ方。</param>
    /// <param name="sheetNames">
    /// 記録に書くシートの表示名（コード名 → 表示名）。ここに無いコード名は、いまある記録の表示名をそのまま残す
    /// （食い違いを確認なしに上書きして、無かったことにしないため）。
    /// </param>
    public VbaLink WithSheetViews(VbaSheetViewSet views, IReadOnlyDictionary<string, string>? sheetNames)
    {
        ArgumentNullException.ThrowIfNull(views);
        var existing = (WorkbookInfoSheetViews ?? Array.Empty<VbaSheetViewSetting>())
            .GroupBy(r => r.CodeName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().SheetName, StringComparer.OrdinalIgnoreCase);
        var records = views.Overrides
            .Where(p => p.Value != VbaSheetView.StructureOnly)
            .Select(p =>
            {
                var name = sheetNames is not null && sheetNames.TryGetValue(p.Key, out var given)
                    ? given
                    : existing.GetValueOrDefault(p.Key);
                return VbaSheetViewSetting.From(p.Key, p.Value) with { SheetName = name };
            })
            .OrderBy(r => r.CodeName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return this with { WorkbookInfoSheetViews = records, WorkbookInfoSampleValues = false, WorkbookInfoIncludeFormulas = false };
    }

    /// <summary>
    /// 取得したブックのシート（<paramref name="sheets"/>）と記録を突き合わせ、<b>別のシートへ確認なしで引き継がれる恐れのある、
    /// 値が渡る見せ方の記録を落とす</b>（「構成だけ」に戻る＝再び選ぶときに確認が出る）。
    /// <list type="bullet">
    /// <item>記録のコード名が、取得結果のどのシートにも無い（シートを削除した）: 値が渡る見せ方の記録を落とす。
    /// 後で別の内容のシートが同じコード名になっても、確認なしで値が渡ることがない。
    /// 「出さない」「構成だけ」の記録は、値が渡らないので残す（「出さない」を残すほうが安全側）。</item>
    /// <item>コード名は同じだが、記録した表示名と取得結果の表示名が違う: 値が渡る見せ方の記録を落とす。
    /// <b>表示名を変えただけ</b>のシートと、<b>別のシートが同じコード名になった</b>場合とは、記録からは区別できないので、
    /// どちらも安全側（再確認）に倒す。「出さない」の記録は残し、表示名を更新する。</item>
    /// <item>記録に表示名が無い（この項目が無かった版の記録）: 取得結果の表示名を添えるだけで、見せ方は変えない
    /// （既存の利用者の設定を、版が上がった途端に落とさない）。</item>
    /// </list>
    /// 記録が無い（<see cref="WorkbookInfoSheetViews"/> が null）ときは何もしない。
    /// </summary>
    /// <param name="sheets">取得結果の全シート（<see cref="VbaWorkbookInfoSnapshot.KnownSheets"/>）。</param>
    public VbaSheetViewReconcileResult ReconcileSheetViews(IReadOnlyList<VbaSheetIdentity> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        if (WorkbookInfoSheetViews is null) return new VbaSheetViewReconcileResult(this, false, Array.Empty<string>(), Array.Empty<string>());

        var kept = new List<VbaSheetViewSetting>();
        var renamed = new List<string>();
        var removed = new List<string>();
        var changed = false;
        foreach (var record in WorkbookInfoSheetViews)
        {
            var passes = VbaSheetViewModes.IsValueMode(VbaSheetViewModes.Parse(record.Mode));
            var match = sheets.FirstOrDefault(s => string.Equals(s.CodeName, record.CodeName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                if (passes)
                {
                    removed.Add(record.CodeName);
                    changed = true;
                }
                else
                {
                    kept.Add(record);
                }

                continue;
            }

            if (record.SheetName is null)
            {
                kept.Add(record with { SheetName = match.Name });
                changed = true;
            }
            else if (!string.Equals(record.SheetName, match.Name, StringComparison.OrdinalIgnoreCase))
            {
                changed = true;
                if (passes) renamed.Add(record.CodeName);
                else kept.Add(record with { SheetName = match.Name });
            }
            else
            {
                kept.Add(record);
            }
        }

        return changed
            ? new VbaSheetViewReconcileResult(this with { WorkbookInfoSheetViews = kept.ToArray() }, true, renamed, removed)
            : new VbaSheetViewReconcileResult(this, false, renamed, removed);
    }

    /// <summary>
    /// 紐付けるブックを <paramref name="workbookFullName"/> に結び直した <see cref="VbaLink"/> を返す。
    /// <para>
    /// 【別のブックへ結び直したときは、値が渡る見せ方をすべて戻す】 値が渡る見せ方を選ぶ確認ダイアログは、
    /// 「<b>このブック</b>の値を AI へ渡してよいか」の確認だった。同じプロジェクトを別のブックへ結び直しても
    /// 見せ方を引き継ぐと、新しいブックの値（別の社外秘かもしれない。コード名は <c>Sheet1</c> など別のブックでも重なる）が
    /// 確認なしで AI へ渡る。パスが変わるときだけ、シートごとの見せ方を（「出さない」も含め）全部消して全シート構成だけに戻し、
    /// v0.4 より前の値の見本の項目もオフにして、確認をやり直させる。同じブックの再追加（パスが同じ）では選択を保つ。
    /// </para>
    /// </summary>
    public VbaLink WithWorkbook(string workbookFullName)
    {
        var same = VbaRotNames.SamePath(WorkbookFullName, workbookFullName);
        return this with
        {
            WorkbookFullName = workbookFullName,
            WorkbookInfoSampleValues = WorkbookInfoSampleValues && same,
            WorkbookInfoSheetViews = same ? WorkbookInfoSheetViews : null,
        };
    }
}

/// <summary>
/// シート 1 枚の見せ方の記録（<see cref="VbaLink.WorkbookInfoSheetViews"/> の 1 要素。projects.json）。
/// 見せ方は名前の文字列で持つ（<c>ContextFileStates</c> と同じ作法。不明な文字列は構成だけ＝値を渡さない側に読む）。
/// </summary>
public sealed record VbaSheetViewSetting
{
    /// <summary>シートのコード名（<c>Sheet1</c> など。表示名ではない）。</summary>
    public string CodeName { get; init; } = string.Empty;

    /// <summary><see cref="VbaSheetViewMode"/> の名前（<c>None</c>・<c>StructureOnly</c>・<c>HeaderRows</c>・<c>All</c>・<c>Range</c>）。</summary>
    public string Mode { get; init; } = "StructureOnly";

    /// <summary>見出しに続ける行数（<c>HeaderRows</c> のとき）。</summary>
    public int HeaderRows { get; init; } = VbaSheetViewLimits.DefaultHeaderRows;

    /// <summary>数式も含めるか（値が渡る見せ方のとき）。</summary>
    public bool IncludeFormulas { get; init; }

    /// <summary>範囲（<c>Range</c> のとき。正規化した A1 形式）。</summary>
    public string? Range { get; init; }

    /// <summary>
    /// 記録したときのシートの表示名。コード名が同じでも表示名が食い違えば、別のシートかもしれないので、値が渡る見せ方は
    /// 構成だけに戻して再確認を求める（<see cref="VbaLink.ReconcileSheetViews"/>）。この項目が無い既存の記録は null
    /// （食い違いの判定をせず、次に取得したときに表示名を添える）。
    /// </summary>
    public string? SheetName { get; init; }

    /// <summary>実行時の見せ方にする。範囲が壊れていれば捨てる（値を渡さない側）。</summary>
    public VbaSheetView ToView() => new(
        VbaSheetViewModes.Parse(Mode),
        VbaSheetViewLimits.ClampHeaderRows(HeaderRows),
        IncludeFormulas,
        VbaSheetView.NormalizeRange(Range));

    /// <summary>実行時の見せ方から記録にする。</summary>
    public static VbaSheetViewSetting From(string codeName, VbaSheetView view) => new()
    {
        CodeName = codeName,
        Mode = view.Mode.ToString(),
        HeaderRows = view.HeaderRows,
        IncludeFormulas = view.IncludeFormulas,
        Range = view.Range,
    };
}

/// <summary><see cref="VbaLink.ReconcileSheetViews"/> の結果。</summary>
/// <param name="Link">突き合わせ後の紐付け（変わらなければ元のもの）。</param>
/// <param name="Changed">記録が変わったか（表示名を添えただけの変更も含む）。</param>
/// <param name="Renamed">表示名が食い違い、値が渡る見せ方を構成だけに戻したシートのコード名。</param>
/// <param name="Removed">取得結果に無く、値が渡る見せ方の記録を落としたシートのコード名。</param>
public sealed record VbaSheetViewReconcileResult(
    VbaLink Link, bool Changed, IReadOnlyList<string> Renamed, IReadOnlyList<string> Removed);

/// <summary>プロジェクト定義。projects.json の1要素。仕様書3.1。</summary>
public sealed record Project
{
    /// <summary>ルートパスのハッシュから生成したID。フォルダ名変更に耐える。</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>表示名。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>プロジェクトルートの絶対パス。</summary>
    public string Root { get; init; } = string.Empty;
    /// <summary>タグ。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    /// <summary>ピン留め。一覧の先頭に並べる。</summary>
    public bool Pinned { get; init; }

    /// <summary>
    /// ピン留めした日時。要望対応（ピン留め同士を「ピン留めした順」に並べる）: <see cref="Pinned"/>を
    /// falseからtrueへ切り替えた瞬間に記録し、trueからfalseへ戻すと再びnullにする
    /// （<see cref="ProjectPaneViewModel"/>の右クリックメニュー→<see cref="ProjectStore"/>の更新経路。
    /// 一度解除して再度ピン留めすると新しい日時になり、ピン留め済みグループの最後尾に来る。これは
    /// 「解除→再ピン留め」を「今から改めてピン留めし直した」という自然な意味に一致させるための挙動）。
    /// <see cref="ProjectStore.Sort"/>はピン留め済み同士をこの値の昇順（先にピン留めしたものが上）で
    /// 並べる。
    /// <para>
    /// 【旧形式projects.jsonからの移行】 このフィールドが無い旧形式のJSONを読み込むと、
    /// <see cref="Pinned"/>がtrueでもこちらはnullになる。<see cref="ProjectStore.Sort"/>は、
    /// 「この機能が入る前から既にピン留めされていた＝これから新規にピン留めするものより先に
    /// ピン留めされていたはず」という考え方（<see cref="LastAppliedAt"/>の移行で
    /// <see cref="NextRevision"/>&gt;1というシグナルを使ったのと同じく、既に永続化済みの状態から
    /// 代用値を導く考え方）で、値が無くPinnedがtrueの項目には <c>DateTimeOffset.MinValue</c>
    /// （最も古い扱い）を代用し、ピン留め済みグループの先頭側へ置く。値が無い者同士がタイになった
    /// 場合は、その次の並べ替えキー（<see cref="LastAppliedAt"/>相当の
    /// <see cref="ProjectStore.Sort"/>内ロジック）で決着するため、読み込みのたびに順序が
    /// 入れ替わることはない。
    /// </para>
    /// </summary>
    public DateTimeOffset? PinnedAt { get; init; }
    /// <summary>
    /// 最終使用日時。新規登録・場所の再結び付け（<see cref="ProjectStore.RelocateAsync"/>）の
    /// たびに更新される「Graftでこのプロジェクトを開いた（触った）日時」。並べ替え
    /// （<see cref="ProjectStore.Sort"/>）では、<see cref="LastAppliedAt"/> が無い（一度も
    /// パッチを適用していない）旧形式のprojects.json由来のプロジェクトを移行救済する際の
    /// 代用値としてのみ使う（詳しくは <see cref="LastAppliedAt"/> 参照）。
    /// </summary>
    public DateTimeOffset LastUsedAt { get; init; }

    /// <summary>
    /// 不具合3対応（プロジェクト一覧の並び順）: パッチを最後に適用した日時。null は
    /// 「一度もパッチを適用していない」ことを表す（プロジェクト追加直後はこの状態）。
    /// <see cref="ProjectStore.Sort"/> は、ピン留め優先の次にこの値の降順で並べ、null（未適用）
    /// は常に最下部へ送る。更新するのは <see cref="ProjectStore.MarkAppliedAsync"/> の1箇所のみ
    /// （呼び出し元は「パッチの適用」「ここまで戻す」「このリビジョンを取り消す（単発復元）」の
    /// 3箇所。判断理由はMainViewModel.Apply.cs・HistoryPaneViewModel.csのコメント参照）。
    /// <para>
    /// 【旧形式projects.jsonからの移行】 このフィールドが無い旧形式のJSONを読み込むと
    /// 既定値（null）になる。そのまま「未適用」扱いにすると、既に使い込んでいた既存プロジェクトが
    /// 軒並み最下部に落ちて利用者を驚かせてしまう。そこで <see cref="ProjectStore.Sort"/> は、
    /// LastAppliedAt が null でも <see cref="NextRevision"/> が1より大きい（＝
    /// <see cref="ProjectStore.ConsumeNextRevisionAsync"/> が過去に一度以上呼ばれた、つまり
    /// このプロジェクトへの適用・ここまで戻す操作を試みたことがある）場合に限り、
    /// 暫定的に <see cref="LastUsedAt"/> を代用値として並べ替えに使う。新規登録直後のプロジェクトは
    /// 常に NextRevision=1 から始まるため、この代用は効かず正しく最下部に並ぶ（要件どおり）。
    /// </para>
    /// </summary>
    public DateTimeOffset? LastAppliedAt { get; init; }
    /// <summary>次に付与するリビジョン番号。</summary>
    public int NextRevision { get; init; } = 1;
    /// <summary>常設コンテキスト。仕様書3.3。</summary>
    public string? StandingContext { get; init; }
    /// <summary>既定のプロンプトテンプレートID。</summary>
    public string? PromptTemplateId { get; init; }
    /// <summary>設定の上書き。</summary>
    public ProjectOverrides Overrides { get; init; } = new();
    /// <summary>適用後フック。</summary>
    public IReadOnlyList<PostApplyHook> PostApplyHooks { get; init; } = Array.Empty<PostApplyHook>();

    /// <summary>
    /// Excel ブックとの紐付け（仕様書_VBA連携 4.1）。null のプロジェクトは従来どおりのファイルプロジェクト。
    /// この項目が無い既存の projects.json はそのまま読めて null になる（JSON のキーは camelCase の
    /// <c>vba</c>）。
    /// </summary>
    public VbaLink? Vba { get; init; }

    /// <summary>ルートが存在せず未接続かどうか。永続化はせず起動時の検証で設定する。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDisconnected { get; init; }

    /// <summary>
    /// 表示用に正規化した名前（不具合2対応）。空・改行混じり・空白のみといった異常な
    /// <see cref="Name"/> でも一覧やドロップダウンの見た目が崩れないよう、都度算出する。
    /// 正規化ルールは <see cref="ProjectNameFormatter"/> 参照。永続化はしない
    /// （projects.json 上は常に生の Name のまま。理由は同クラスのコメント参照）。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => ProjectNameFormatter.Normalize(Name, Root);
}

/// <summary>projects.json のルート要素。</summary>
public sealed record ProjectCatalog
{
    /// <summary>プロジェクト定義。</summary>
    public IReadOnlyList<Project> Projects { get; init; } = Array.Empty<Project>();
}
