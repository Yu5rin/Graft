using System.Collections.Concurrent;
using System.IO;
using Graft.Core;
using Graft.Infra;

namespace Graft.Features;

/// <summary>AI向けプロンプトテンプレート。仕様書4.8。</summary>
public sealed record PromptTemplate
{
    /// <summary>識別子。</summary>
    public required string Id { get; init; }
    /// <summary>表示名。</summary>
    public required string Name { get; init; }
    /// <summary>本文。<c>{{standingContext}}</c> 等の変数を含められる（4.8.2）。</summary>
    public required string Body { get; init; }
    /// <summary>同梱の既定テンプレートかどうか。既定テンプレートは <see cref="PromptTemplateStore.SaveAsync"/> で保存しない。</summary>
    public bool IsBuiltIn { get; init; }
    /// <summary>継続用の短縮版かどうか（4.8.1）。</summary>
    public bool IsContinuation { get; init; }
}

/// <summary>templates.json のルート要素。</summary>
internal sealed record PromptTemplateFile
{
    public IReadOnlyList<PromptTemplate> Templates { get; init; } = Array.Empty<PromptTemplate>();
}

/// <summary>
/// 仕様書4.8のプロンプトテンプレート管理を担う。既定テンプレート（<see cref="BuiltIns"/>）は
/// 仕様書4.8・4.8.1・4.8.3の本文を一字一句そのまま同梱し、ユーザー定義テンプレートは
/// <c>templates.json</c> へ保存する。4.8.1の「直近1時間以内にコピー済みか」の判定はプロセス内
/// メモリのみで保持し、永続化はしない（仕様上も履歴の永続化は不要）。
/// </summary>
public sealed class PromptTemplateStore
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _jsonStore = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCopyAt = new();

    public PromptTemplateStore(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>既定テンプレートの一覧（4.8「初回用（完全版）」、4.8.1の短縮版、4.8.3の3種類）。</summary>
    public static IReadOnlyList<PromptTemplate> BuiltIns { get; } = BuildBuiltIns();

    /// <summary>templates.json のパス。settings.json 等と同じ階層に置く。</summary>
    public string TemplatesFilePath => _paths.TemplatesFilePath;

    /// <summary>既定テンプレートとユーザー定義テンプレートを合わせて返す。</summary>
    public async Task<GraftResult<IReadOnlyList<PromptTemplate>>> LoadAsync(CancellationToken ct = default)
    {
        var result = await _jsonStore.ReadWithRecoveryAsync(
            TemplatesFilePath,
            () => new PromptTemplateFile(),
            ct: ct).ConfigureAwait(false);
        if (!result.IsSuccess) return GraftResult<IReadOnlyList<PromptTemplate>>.Fail(result.Issues);

        var custom = result.Value.Templates.Where(t => !t.IsBuiltIn).ToList();
        var merged = BuiltIns.Concat(custom).ToList();
        return GraftResult<IReadOnlyList<PromptTemplate>>.Ok(merged, result.Issues);
    }

    /// <summary>ユーザー定義テンプレートを保存する。既定テンプレートは書き出さない（常にコード側で供給する）。</summary>
    public async Task<GraftResult<bool>> SaveAsync(IReadOnlyList<PromptTemplate> templates, CancellationToken ct = default)
    {
        var custom = templates.Where(t => !t.IsBuiltIn).ToArray();
        await _jsonStore.WriteAsync(TemplatesFilePath, new PromptTemplateFile { Templates = custom }, ct: ct).ConfigureAwait(false);
        return GraftResult<bool>.Ok(true);
    }

    /// <summary>指定プロジェクトでテンプレートをコピーした日時を記録する（4.8.1）。</summary>
    public void RecordCopy(string projectId, DateTimeOffset at)
    {
        _lastCopyAt[projectId] = at;
    }

    /// <summary>
    /// 同一プロジェクトで直近1時間以内にコピー済みなら true を返す。true のとき、
    /// 呼び出し側は継続用の短縮版（<see cref="PromptTemplate.IsContinuation"/>）を既定表示にする。
    /// </summary>
    public bool ShouldUseContinuation(string projectId, DateTimeOffset now)
    {
        if (!_lastCopyAt.TryGetValue(projectId, out var last)) return false;
        return now - last <= TimeSpan.FromHours(1);
    }

    /// <summary>
    /// エディタの選択範囲から修正依頼プロンプトを組み立てる（右クリックメニュー
    /// 「選択範囲の修正依頼プロンプトをコピー」）。「修正依頼」テンプレートの形式指示
    /// （v1.0.13で <see cref="FixRequestFormatInstruction"/> から標準SR形式の
    /// <see cref="StandardFormatInstruction"/> へ切り替えた。既定テンプレートと同じ形式で
    /// AIに出力させるため）に続けて、対象ファイルのプロジェクト相対パス・
    /// 行範囲・選択コード（``` フェンス。拡張子から言語名を付けられれば付ける）を並べ、末尾に
    /// 依頼内容を書き足すための誘導行を置く。10章のコンテキスト収集を介さない単発の依頼のため、
    /// <c>{{standingContext}}</c>・<c>{{files}}</c> のようなプレースホルダは使わない。
    /// UIに依存しない純粋メソッドとして置き、単体テストで検証できるようにする。
    /// </summary>
    /// <param name="relativePath">対象ファイルのプロジェクト相対パス。</param>
    /// <param name="startLine">選択範囲の開始行（1始まり）。</param>
    /// <param name="endLine">選択範囲の終了行（1始まり）。</param>
    /// <param name="selectedCode">選択されたコード本文。</param>
    /// <param name="fileExtension">対象ファイルの拡張子（先頭の <c>.</c> の有無は問わない）。</param>
    public static string BuildSelectionFixRequestPrompt(
        string relativePath, int startLine, int endLine, string selectedCode, string fileExtension)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(selectedCode);
        ArgumentNullException.ThrowIfNull(fileExtension);

        var fenceLanguage = FenceLanguageForExtension(fileExtension);
        var code = selectedCode.EndsWith('\n') ? selectedCode : selectedCode + "\n";

        return StandardFormatInstruction +
            $"\n\n対象: {relativePath}（{startLine}〜{endLine}行目）\n\n" +
            $"```{fenceLanguage}\n{code}```\n\n" +
            "このコードの修正を依頼します。修正内容: ";
    }

    /// <summary>
    /// コードフェンスに付ける言語識別子。<see cref="Graft.Core.LanguageRule"/>の
    /// シンタックスハイライト対応拡張子と合わせるが、フェンス表記はMarkdownで一般的な言語名
    /// （<c>csharp</c>等）を使う。対応外の拡張子は言語名なしのフェンス（<c>```</c>のみ）にする。
    /// </summary>
    private static string FenceLanguageForExtension(string extension)
    {
        var normalized = (extension.StartsWith('.') ? extension[1..] : extension).Trim().ToLowerInvariant();
        return normalized switch
        {
            "cs" => "csharp",
            "py" => "python",
            "js" or "jsx" => "javascript",
            "ts" or "tsx" => "typescript",
            "json" => "json",
            "html" or "htm" => "html",
            "xml" or "axaml" or "xaml" => "xml",
            "css" => "css",
            "md" => "markdown",
            "sh" or "bash" => "bash",
            "sql" => "sql",
            "yaml" or "yml" => "yaml",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// 既定テンプレートの一覧を組み立てる。
    ///
    /// 【v1.0.13での方針転換: 既定を標準SEARCH/REPLACE形式へ切り替えた】
    /// 形式指示に従わないAIが実際に問題になっており（仕様書5.1の背景と同じ事情）、
    /// Graft独自形式より、Aiderが広めCline・Roo Code等も採用した標準SR形式の方が、
    /// マーカーがgitのマージコンフリクトマーカーそのものである分だけLLMが自然に出せる。
    /// そこでAIに出させる形式そのものを標準へ寄せ、<c>builtin-full</c> 等の既定として
    /// 選ばれるIDに標準SR形式の本文を割り当てた。
    ///
    /// 【従来のGraft独自形式のテンプレートも残す理由】既定を入れ替えるだけにすると、
    /// これまでGraft独自形式で運用してきた利用者が、慣れた指示文を二度と選べなくなる。
    /// またGraft独自形式にしかできないこと（RENAME・MKDIR・APPEND/PREPEND・
    /// パッチ全体のsummary/type・base ハッシュ）が実際に必要になる場面がある。
    /// そこでIDを分けた別テンプレート（<c>builtin-graft-*</c>）として本文をそのまま残し、
    /// 設定を増やさずに「一覧から選ぶだけ」で従来どおり使えるようにした。
    /// 並び順は標準SR形式を先にしてある。継続用の自動選択
    /// （<c>PromptCopyViewModel</c> は <see cref="PromptTemplate.IsContinuation"/> が
    /// 一致する最初の要素を選ぶ）で標準SR形式が既定になるようにするため。
    /// </summary>
    private static IReadOnlyList<PromptTemplate> BuildBuiltIns() => new[]
    {
        new PromptTemplate { Id = "builtin-full", Name = "初回用（完全版）", Body = StandardFullBody, IsBuiltIn = true },
        new PromptTemplate { Id = "builtin-continuation", Name = "継続用（短縮版）", Body = StandardContinuationBody, IsBuiltIn = true, IsContinuation = true },
        new PromptTemplate { Id = "builtin-fix-request", Name = "修正依頼", Body = StandardFixRequestBody, IsBuiltIn = true },
        new PromptTemplate { Id = "builtin-new-file", Name = "新規実装", Body = StandardNewFileBody, IsBuiltIn = true },
        new PromptTemplate { Id = "builtin-investigate", Name = "調査依頼", Body = InvestigateBody, IsBuiltIn = true },

        new PromptTemplate { Id = "builtin-graft-full", Name = "初回用（Graft独自形式）", Body = FullBody, IsBuiltIn = true },
        new PromptTemplate { Id = "builtin-graft-continuation", Name = "継続用（Graft独自形式）", Body = ContinuationBody, IsBuiltIn = true, IsContinuation = true },
        new PromptTemplate { Id = "builtin-graft-fix-request", Name = "修正依頼（Graft独自形式）", Body = FixRequestBody, IsBuiltIn = true },
        new PromptTemplate { Id = "builtin-graft-new-file", Name = "新規実装（Graft独自形式）", Body = NewFileBody, IsBuiltIn = true },
    };

    // ==================================================================
    // 標準SEARCH/REPLACE形式（仕様書5.2）の既定テンプレート
    // ==================================================================

    /// <summary>
    /// 標準SR形式の4種類の書き方。利用者の会社で運用されている「SRルール」の記述に揃えてある。
    ///
    /// 【仮のパスに「相対パス」という文字列を使い続ける理由】クリップボード監視の誤検知対策。
    /// テンプレート本文をコピーしただけで（AIに何も聞く前から）パッチとして自動検知されると
    /// 邪魔になるため、<see cref="Graft.Core.PatchTextDetector"/> は「パスらしい見た目
    /// （"." または "/" を含む）」を検知の条件にしている。標準の書き方の例示で使われがちな
    /// <c>path/to/file</c> のような文字列をそのまま置くと、この条件を満たしてしまい
    /// テンプレート自身が検知されてしまう。従来のGraft独自形式のテンプレートと同じく
    /// 「相対パス」という日本語のプレースホルダにしておけば、その事故が起きない。
    /// </summary>
    private const string StandardFormatBlocks =
        "【1】既存ファイルの部分修正\n" +
        "相対パス\n" +
        "<<<<<<< SEARCH\n" +
        "（現在のファイルに存在する完全一致テキスト）\n" +
        "=======\n" +
        "（置換後テキスト）\n" +
        ">>>>>>> REPLACE\n" +
        "\n" +
        "【2】新規ファイルの作成\n" +
        "相対パス\n" +
        "<<<<<<< NEW_FILE\n" +
        "（ファイルの全内容）\n" +
        ">>>>>>> END_FILE\n" +
        "\n" +
        "【3】既存ファイルの全面書き換え\n" +
        "相対パス\n" +
        "<<<<<<< WHOLE_FILE\n" +
        "（ファイルの全内容）\n" +
        ">>>>>>> END_FILE\n" +
        "\n" +
        "【4】ファイルの削除\n" +
        "相対パス\n" +
        "<<<<<<< DELETE_FILE\n" +
        ">>>>>>> END_FILE\n";

    /// <summary>
    /// 標準SR形式の運用ルール。会社の「SRルール」の運用規定をそのまま反映しつつ、
    /// Graft側で実際に効く挙動（空SEARCHでも新規作成として受け付ける・親階層へ出るパスは
    /// 拒否する・NEED_MORE_CONTEXT はE710として利用者へ提示される）と食い違わないようにしてある。
    /// </summary>
    private const string StandardRuleNote =
        "ブロックの直前の行には、対象ファイルのパスだけを単独で書いてください" +
        "（引用符・太字・行番号・見出し記号は付けない）。\n" +
        "パスは必ずプロジェクトからの相対パスにし、親階層へ出る記法は使わないでください" +
        "（安全のためGraft側で拒否します）。\n" +
        "【2】は、SEARCH 部を空にした【1】の書き方でも構いません（どちらでも新規作成として扱われます）。\n" +
        "\n" +
        "【必ず守ること】\n" +
        "- SEARCH 部は現在のファイルの内容と1文字も違わないこと" +
        "（インデント・空行・コメントを含めて完全一致させる）。\n" +
        "- SEARCH 部はファイル内で一意に定まるまで広く取ること。" +
        "同じ並びが他にもある場合は、前後の行まで含めて一意にしてください。\n" +
        "- 省略記号（3点リーダや ... など）やプレースホルダを書かないこと。書くと一致しなくなります。\n" +
        "- 依頼と無関係な整形（インデント・並べ替え・改行位置の変更）はしないこと。\n" +
        "- SEARCH 部を正確に作れない場合は、推測でブロックを書かず、" +
        "NEED_MORE_CONTEXT の1行だけを出力してください。\n" +
        "- 説明文はブロックの外に書いてください。";

    /// <summary>
    /// Graft独自の拡張記法への導線。「基本は標準SR形式、拡張は必要なときだけ」という位置づけを
    /// 明示する。マーカーそのもの（<c>&lt;&lt;&lt;&lt; RENAME:</c> 等）を行頭に書くと、この
    /// テンプレート本文自身がGraft独自形式のパッチとして自動検知されてしまうため、
    /// 記法名だけを挙げて具体的な書き方は「Graft独自形式」テンプレート側へ委ねる。
    ///
    /// 【混在を禁じる理由】<see cref="Graft.Core.PatchParser"/> はGraft専用ヘッダが1つでもあれば
    /// 全体をGraft独自形式として解析する（後方互換を最優先した設計判断。同クラス参照）。
    /// 1回の出力に両形式を混ぜると、標準形式で書いたブロックが取り込まれない。
    /// </summary>
    private const string StandardExtensionNote =
        "【必要なときだけ使う追加記法】ファイルの移動・改名（RENAME）、フォルダ作成（MKDIR）、" +
        "末尾への追記（APPEND）、先頭への挿入（PREPEND）は上の4種では表せません。" +
        "これらが必要なときだけ、Graft独自形式（テンプレート「初回用（Graft独自形式）」を参照）へ切り替えてください。" +
        "ただし1回の出力で2つの形式を混ぜないでください。混ぜるとGraft独自形式として解釈され、" +
        "標準形式で書いたブロックが取り込まれません。\n" +
        "また、SEARCH 部をどれだけ広げても一意にできない場合に限り、" +
        "SEARCH マーカーの行末へ OCCURRENCE=2 や OCCURRENCE=ALL と書いて何番目を対象にするか指定できます。";

    /// <summary>
    /// 標準SR形式版のコードブロック指示。<see cref="CodeBlockWrapNote"/> と役割は同じで、
    /// 囲む範囲の説明だけを標準SR形式の言い方（最初のパス指定行から最後の終了マーカーまで）に
    /// 差し替えたもの。会社の「SRルール」も「出力全体を1つの text コードフェンスで囲む」ことを
    /// 定めており、Graft側の既存の指示と方向が一致している。
    /// </summary>
    private const string StandardCodeBlockWrapNote =
        "出力全体（最初のパス指定行から最後の >>>>>>> REPLACE ・ >>>>>>> END_FILE まで。説明文は含めない）を、" +
        "1つのコードブロックとして囲んで出力してください（1行目に ```text、最終行に ``` " +
        "と書きます。チャットのコピー機能で一括コピーできるようにするためです）。" +
        "本文に ``` が含まれる場合（Markdownファイルの編集など）は、外側をバッククォート4個にし、" +
        "1行目を ````text、最終行を ```` としてください。";

    /// <summary>標準SR形式の形式指示。初回用・修正依頼・選択範囲の修正依頼で共用する。</summary>
    private const string StandardFormatInstruction =
        "コードの修正を提案する際は、必ず次の SEARCH/REPLACE ブロック形式で出力してください。\n" +
        "\n" +
        StandardFormatBlocks +
        "\n" +
        StandardRuleNote +
        "\n\n" +
        StandardExtensionNote +
        "\n\n" +
        StandardCodeBlockWrapNote;

    /// <summary>既定の「初回用（完全版）」。標準SR形式。</summary>
    private const string StandardFullBody = StandardFormatInstruction;

    /// <summary>
    /// 既定の「継続用（短縮版）」。標準SR形式。トークン消費を抑えるため要点だけに絞るが、
    /// 実際に事故が起きやすい3点（パスは直前の行に単独で書く・SEARCH は完全一致・
    /// 出力全体を1つのコードブロックで囲む）は削らない。
    /// </summary>
    private const string StandardContinuationBody =
        "先ほどと同じ SEARCH/REPLACE ブロック形式で出力してください。" +
        "対象ファイルの相対パスはブロックの直前の行に単独で書き、SEARCH 部は現在のファイルと完全一致させてください。" +
        "出力全体を1行目```text・最終行```で囲んでください（本文に```を含む場合は````）。";

    /// <summary>既定の「修正依頼」。標準SR形式の形式指示＋standingContext＋files。</summary>
    private const string StandardFixRequestBody =
        StandardFormatInstruction + "\n\n# 前提\n{{standingContext}}\n\n# 対象ファイル\n{{files}}";

    /// <summary>
    /// 既定の「新規実装」。標準SR形式。新規作成が主目的のため【2】NEW_FILE（と空SEARCH）を
    /// 前面に出すが、既存ファイルへの手当てが同時に必要になることは多いため
    /// 【1】部分修正の書き方も併記する。
    /// </summary>
    private const string StandardNewFileBody =
        "コードの新規作成を提案する際は、必ず次の SEARCH/REPLACE ブロック形式で出力してください。" +
        "新規ファイルは【2】を使ってください。\n" +
        "\n" +
        StandardFormatBlocks +
        "\n" +
        StandardRuleNote +
        "\n\n" +
        StandardExtensionNote +
        "\n\n" +
        StandardCodeBlockWrapNote +
        "\n\n# 前提\n{{standingContext}}\n\n# プロジェクト構成\n{{tree}}";

    /// <summary>仕様書4.9末尾「プロンプトテンプレートにもこの規則を記載し、AIがエスケープ済みの形で出力するよう指示する。」に対応する追記。</summary>
    private const string EscapeRuleNote =
        "【エスケープ規則】ブロック本文の行頭に <<<<、>>>>、======= が現れる場合は、直前に \\ を置いて" +
        "エスケープしてください（例: \\<<<< FILE: 相対パス）。パーサ適用時に先頭の \\ を1つ取り除きます。" +
        "多数のエスケープが必要な場合は、ヘッダに FENCE=<任意文字列> を指定し、終了マーカーを " +
        ">>>> END:<任意文字列> に変更できます。";

    /// <summary>
    /// 案件2対応: 実際に起きた事故（PATCHメタが二重に出力され、1つ目が">>>>"で閉じられていない
    /// まま2つ目が始まった）を踏まえた追記。3文で、(1)PATCHメタは1回だけ・必ず閉じる、
    /// (2)開いたブロックは対応する終了マーカーで必ず閉じる、(3)出力前にマーカーの対応を
    /// 自分で確認する、を伝える。長くしすぎないため、各文はこの3点に一対一で対応させ、
    /// エスケープ規則や閉じマーカーの具体名を重複して説明しない（EscapeRuleNoteと役割分担）。
    /// </summary>
    private const string BlockIntegrityNote =
        "<<<< PATCH は出力全体で1回だけにし、必ず >>>> で閉じてください。" +
        "SEARCH/REPLACEは ======= と >>>>>>> REPLACE で、MODE=FULLは >>>> END で、開いたブロックは必ず閉じてください。" +
        "出力する前に、開始マーカーと終了マーカーの数が対応しているか自分で確認してください。";

    /// <summary>
    /// 利用者からの指摘対応（案件3）: 「回答がコードブロックで出力されず、コピーが面倒だった」。
    /// パッチ全体（<<<< PATCH から最後の >>>> ・>>>> END まで、説明文を除く）を、AIのチャットUIの
    /// 「コピー」ボタンでまとめて1回でコピーできるよう、1つのコードブロックとして出力させる。
    /// パッチを出力しないテンプレート（<see cref="InvestigateBody"/>）には付けない。
    ///
    /// 【v1.0.8での訂正: Graft側の実際の挙動】以前このコメントは「PatchScannerが```で始まる行を
    /// 個数に関わらずすべて読み飛ばすため支障なく取り込める」としていたが、これは誤りだった
    /// （v1.0.5〜1.0.7で持ち越していた不具合。<see cref="Core.PatchScanner"/> のクラスコメント参照）。
    /// 実際には、パッチ本文に```を含むパッチをこの指示に反して外側バッククォート3個のまま
    /// 出力すると、本文中の```が外側フェンスを閉じたと誤解釈され、フェンス行が無言で消えたまま
    /// 本文の一部が欠落する（コピーボタン云々とは別に、Graft側の解析結果が壊れる）不具合があった。
    /// v1.0.8で<see cref="Core.PatchScanner"/>を修正し、CommonMarkのフェンス規則
    /// （終了フェンスは開始と同数以上のバッククォートが必要）に従って外側の1個だけを剥がすように
    /// なったため、下記の「本文に```を含む場合は外側をバッククォート4個にする」指示が実際に
    /// 意味を持つようになった。それでもAIがこの指示に反して3個のまま出力し、かつ本文にも```を
    /// 含めてしまった場合は、Graft側は黙って本文を欠落させず、E009として解析に失敗させ、
    /// 4個化を促す案内を表示する（<see cref="Core.PatchScanner"/> 参照）。
    ///
    /// 【1.0.4後も再発した「コピーボタンが出ない」報告の原因調査】
    /// 1.0.4で「1つの```で囲む」という指示自体は入れていたが、それでも再発した。主な原因として
    /// 次の2点を検討した。
    /// (a) パッチ本文にMarkdownファイルの編集など```を含む場合、AIが単純に外側と同じ3個の
    ///     ```を使うと、本文中の```で外側のフェンスが途中で閉じたと解釈され、チャットUIの
    ///     コードブロックが分断される（後半が生のテキストとして表示され、コピーボタンが
    ///     付かない）。これは十分ありうる原因のため、本文に```を含む場合は外側をバッククォート
    ///     4個（````）にするよう明示的に指示することにした（Markdownの仕様上、内側の3個の
    ///     ```では4個のフェンスを閉じられない）。
    /// (b) 言語識別子の無い裸の```だとコードブロックとして扱われないUIがある、という仮説は
    ///     検討したが、Markdownの仕様上は言語識別子の有無はコードブロックとしての成立に
    ///     関係せず、主要なチャットUIで裸の```が原因でコピーボタンが消える動作は確認できな
    ///     かった。とはいえ「言語識別子を明示する」こと自体はAI・利用者の双方にとって
    ///     『どこからどこまでが1つのコードブロックか』を誤りにくくする効果があり、コストも
    ///     1語で済むため、(a)ほど確信は無いが保険として採用する（``` text と最終行の```を
    ///     明示させる）。
    /// 上記のとおり、原因を(a)（バッククォート数の衝突）に絞らず両方を指示に反映した。
    ///
    /// 【副作用の調査、および後日談】このとおりAIが常に囲むようになった結果、単一のコードフェンスで
    /// 丸ごと囲まれたパッチが既定シナリオになったため、クリップボード監視の自動検知
    /// （<see cref="Core.PatchTextDetector"/>）側もその後の案件3対応で「段階2」として救済する
    /// 方針転換を行い、対象にした（同クラスのコメント・
    /// PatchTextDetectorTests.単一フェンスで丸ごと囲まれた本物のパッチは自動検知される 参照。
    /// 本コメント執筆時点では「以後は自動検知が働きにくくなる」としていたが、その後の対応で
    /// 解消済み）。手動の「貼り付け→解析」（PatchScanner経由）は元々この変更の影響を受けず、
    /// 従来どおり成功する（v1.0.8での外側フェンス除去の修正後も、本文に```を含み外側が
    /// バッククォート4個で正しく囲まれている限り同様に成功する）。
    /// </summary>
    private const string CodeBlockWrapNote =
        "パッチ全体（<<<< PATCH から最後の >>>> や >>>> END まで。説明文は含めない）を、" +
        "1つのコードブロックとして囲んで出力してください（1行目に ```text、最終行に ``` " +
        "と書きます。チャットのコピー機能で一括コピーできるようにするためです）。" +
        "パッチ本文に ``` が含まれる場合（Markdownファイルの編集など）は、外側をバッククォート4個にし、" +
        "1行目を ````text、最終行を ```` としてください。";

    /// <summary>仕様書4.8「初回用（完全版）」の本文。</summary>
    private const string FullBody =
        "コードの修正を提案する際は、必ず以下の形式で出力してください。\n" +
        "\n" +
        "まずパッチ全体の要約を書きます。\n" +
        "<<<< PATCH\n" +
        "summary: （変更内容を1行で）\n" +
        "type: feat|fix|refactor|docs|test|chore\n" +
        ">>>>\n" +
        "\n" +
        "【原則】既存ファイルの修正はSEARCH/REPLACE形式を使い、\n" +
        "ファイル全文の再出力はしないでください。\n" +
        "\n" +
        "<<<< FILE: 相対パス\n" +
        "<<<<<<< SEARCH  # このペアの変更内容を1行で\n" +
        "（修正前のコード。一意に特定できる最小限の行数）\n" +
        "=======\n" +
        "（修正後のコード）\n" +
        ">>>>>>> REPLACE\n" +
        "\n" +
        "新規ファイルのみ以下を使用してください。\n" +
        "<<<< FILE: 相対パス MODE=FULL\n" +
        "（全文）\n" +
        ">>>> END\n" +
        "\n" +
        "説明文はブロックの外に書いてください。\n" +
        "\n" +
        EscapeRuleNote +
        "\n\n" +
        BlockIntegrityNote +
        "\n\n" +
        CodeBlockWrapNote;

    /// <summary>
    /// 仕様書4.8.1 継続用の短縮テンプレートの本文。案件3対応でコードブロックの指示を
    /// 追記した（トークン消費最小化の方針に合わせ、理由の説明までは繰り返さず一言だけ足す）。
    /// 案件2対応: 「<<<< PATCH は1回だけ」を追記した。継続依頼は直前の出力を踏まえて
    /// 再出力させる場面であり、実際の事故（PATCHメタの二重出力）が最も起きやすい場面の
    /// ひとつと判断したため、短縮版でもこの1点だけは削らずに残す。
    /// </summary>
    private const string ContinuationBody =
        "先ほどと同じGraft形式（PATCHメタ + SEARCH/REPLACE）で出力してください。" +
        "<<<< PATCH は1回だけにし、パッチ全体を1行目```text・最終行```で囲んでください（本文に```を含む場合は````）。";

    /// <summary>
    /// 4.8.3「修正依頼」の形式指示部分（standingContext/filesを含まない）。単体でも、
    /// 選択範囲からの修正依頼プロンプト（<see cref="BuildSelectionFixRequestPrompt"/>）でも使う。
    /// </summary>
    private const string FixRequestFormatInstruction =
        "コードの修正を提案する際は、必ず以下の形式で出力してください。\n" +
        "\n" +
        "まずパッチ全体の要約を書きます。\n" +
        "<<<< PATCH\n" +
        "summary: （変更内容を1行で）\n" +
        "type: feat|fix|refactor|docs|test|chore\n" +
        ">>>>\n" +
        "\n" +
        "【原則】既存ファイルの修正はSEARCH/REPLACE形式を使い、\n" +
        "ファイル全文の再出力はしないでください。\n" +
        "\n" +
        "<<<< FILE: 相対パス\n" +
        "<<<<<<< SEARCH  # このペアの変更内容を1行で\n" +
        "（修正前のコード。一意に特定できる最小限の行数）\n" +
        "=======\n" +
        "（修正後のコード）\n" +
        ">>>>>>> REPLACE\n" +
        "\n" +
        "説明文はブロックの外に書いてください。\n" +
        "\n" +
        EscapeRuleNote +
        "\n\n" +
        BlockIntegrityNote +
        "\n\n" +
        CodeBlockWrapNote;

    /// <summary>4.8.3「修正依頼」: 形式指示（SR優先）＋standingContext＋files。</summary>
    private const string FixRequestBody =
        FixRequestFormatInstruction + "\n\n# 前提\n{{standingContext}}\n\n# 対象ファイル\n{{files}}";

    /// <summary>4.8.3「新規実装」: 形式指示（FULL許可）＋standingContext＋tree。</summary>
    private const string NewFileBody =
        "コードの新規作成を提案する際は、必ず以下の形式で出力してください。\n" +
        "\n" +
        "まずパッチ全体の要約を書きます。\n" +
        "<<<< PATCH\n" +
        "summary: （変更内容を1行で）\n" +
        "type: feat|fix|refactor|docs|test|chore\n" +
        ">>>>\n" +
        "\n" +
        "新規ファイルは以下の形式を使用してください。\n" +
        "<<<< FILE: 相対パス MODE=FULL\n" +
        "（全文）\n" +
        ">>>> END\n" +
        "\n" +
        "説明文はブロックの外に書いてください。\n" +
        "\n" +
        EscapeRuleNote +
        "\n\n" +
        BlockIntegrityNote +
        "\n\n" +
        CodeBlockWrapNote +
        "\n\n# 前提\n{{standingContext}}\n\n# プロジェクト構成\n{{tree}}";

    /// <summary>4.8.3「調査依頼」: 「まず原因を説明し、修正案の合意が取れてからコードを出力してください」＋files。</summary>
    private const string InvestigateBody =
        "まず原因を説明し、修正案の合意が取れてからコードを出力してください。\n\n# 対象ファイル\n{{files}}";
}
