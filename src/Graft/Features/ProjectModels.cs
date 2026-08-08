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
    /// <summary>最終使用日時。</summary>
    public DateTimeOffset LastUsedAt { get; init; }
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
