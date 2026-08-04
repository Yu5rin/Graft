namespace Graft.Core;

/// <summary>
/// 深刻度。致命的失敗と、続行可能な警告を区別する。
/// </summary>
public enum Severity
{
    /// <summary>情報。</summary>
    Info,
    /// <summary>警告。適用は継続できる。</summary>
    Warning,
    /// <summary>致命的失敗。allOrNothing では全体を中止する。</summary>
    Error,
}

/// <summary>
/// ユーザー操作に起因する失敗の表現。例外の代わりにこれを返す。
/// </summary>
public sealed record GraftIssue
{
    /// <summary>エラーコード。</summary>
    public required ErrorCode Code { get; init; }

    /// <summary>深刻度。</summary>
    public Severity Severity { get; init; } = Severity.Error;

    /// <summary>追加の説明。該当行の内容など、コード共通文の補足に使う。</summary>
    public string? Detail { get; init; }

    /// <summary>関係するパッチ本文の行番号（1始まり）。不明な場合は null。</summary>
    public int? LineNumber { get; init; }

    /// <summary>関係するプロジェクト相対パス。不明な場合は null。</summary>
    public string? Path { get; init; }

    /// <summary>内容の短い説明。</summary>
    public string Summary => ErrorCatalog.SummaryOf(Code);

    /// <summary>対処方法。</summary>
    public string Remedy => ErrorCatalog.RemedyOf(Code);

    /// <summary>「E101 SEARCH部が見つからない」形式の1行表現を返す。</summary>
    public string ToDisplayText()
    {
        var head = $"{Code} {Summary}";
        if (!string.IsNullOrEmpty(Path)) head = $"{head}（{Path}）";
        if (LineNumber is > 0) head = $"{head} {LineNumber}行目";
        return string.IsNullOrEmpty(Detail) ? head : $"{head}: {Detail}";
    }

    /// <summary>指定コードの問題を生成する。</summary>
    public static GraftIssue Of(ErrorCode code, string? detail = null, int? line = null, string? path = null,
        Severity severity = Severity.Error)
        => new() { Code = code, Detail = detail, LineNumber = line, Path = path, Severity = severity };
}

/// <summary>
/// 成功値と問題の一覧を同時に運ぶ結果オブジェクト。警告付き成功を表現できる。
/// </summary>
public sealed class GraftResult<T>
{
    private readonly T? _value;

    private GraftResult(bool ok, T? value, IReadOnlyList<GraftIssue> issues)
    {
        IsSuccess = ok;
        _value = value;
        Issues = issues;
    }

    /// <summary>成功したかどうか。</summary>
    public bool IsSuccess { get; }

    /// <summary>検出された問題の一覧。成功時も警告を含むことがある。</summary>
    public IReadOnlyList<GraftIssue> Issues { get; }

    /// <summary>致命的失敗のみを抽出する。</summary>
    public IEnumerable<GraftIssue> Errors => Issues.Where(i => i.Severity == Severity.Error);

    /// <summary>成功値。失敗時に参照すると例外を投げる。</summary>
    public T Value => IsSuccess && _value is not null
        ? _value
        : throw new InvalidOperationException("失敗した結果から値を取得しようとしました。");

    /// <summary>成功値。失敗時は既定値を返す。</summary>
    public T? ValueOrDefault => _value;

    /// <summary>成功を生成する。警告を伴う成功も表現できる。</summary>
    public static GraftResult<T> Ok(T value, IEnumerable<GraftIssue>? issues = null)
        => new(true, value, issues?.ToArray() ?? Array.Empty<GraftIssue>());

    /// <summary>失敗を生成する。</summary>
    public static GraftResult<T> Fail(params GraftIssue[] issues)
        => new(false, default, issues);

    /// <summary>単一のエラーコードから失敗を生成する。</summary>
    public static GraftResult<T> Fail(ErrorCode code, string? detail = null, int? line = null, string? path = null)
        => Fail(GraftIssue.Of(code, detail, line, path));

    /// <summary>失敗を生成する。</summary>
    public static GraftResult<T> Fail(IEnumerable<GraftIssue> issues)
        => new(false, default, issues.ToArray());
}
