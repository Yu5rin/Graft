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
}

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
