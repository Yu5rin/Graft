namespace Graft.Core;

/// <summary>
/// 仕様書16章のエラーコード。ユーザー操作に起因する失敗はすべてこの列挙で表現する。
/// </summary>
public enum ErrorCode
{
    /// <summary>ブロックが存在しない。</summary>
    E001,
    /// <summary>ヘッダの構文エラー。</summary>
    E002,
    /// <summary>SEARCH部が空。</summary>
    E003,
    /// <summary>summaryが未指定。</summary>
    E004,
    /// <summary>パッチが途中で切れている。</summary>
    E005,
    /// <summary>エスケープ規則の不整合。</summary>
    E006,
    /// <summary>パッチキュー内で同一ファイルのブロックが重複（16章の表には無いため追加）。</summary>
    E007,

    /// <summary>SEARCH部が見つからない。</summary>
    E101,
    /// <summary>複数箇所にマッチ。</summary>
    E102,
    /// <summary>終了アンカーが見つからない。</summary>
    E103,
    /// <summary>アンカー省略記法の範囲が閾値を超過（仕様書4.4の警告。16章の表には無いため追加）。</summary>
    E104,

    /// <summary>パスがルート外。</summary>
    E201,
    /// <summary>拡張子が未許可。</summary>
    E202,
    /// <summary>サイズ上限超過。</summary>
    E203,
    /// <summary>ファイルがロック中。</summary>
    E204,
    /// <summary>読み取り専用。</summary>
    E205,
    /// <summary>パス長が上限を超過。</summary>
    E206,
    /// <summary>同一パッチ内で旧パスを参照。</summary>
    E207,
    /// <summary>同一ファイルにFULL形式とSR形式が混在（16章の表には無いため追加）。</summary>
    E208,

    /// <summary>ベースハッシュ不一致。</summary>
    E301,
    /// <summary>適用済みパッチの再投入。</summary>
    E302,
    /// <summary>プロジェクト一致率が低い。</summary>
    E303,

    /// <summary>バックアップ作成失敗。</summary>
    E401,
    /// <summary>書き込み失敗。</summary>
    E402,
    /// <summary>前回の適用が未完了。</summary>
    E403,
    /// <summary>設定・履歴データの破損。</summary>
    E404,
    /// <summary>バックアップの実体が見つからない。</summary>
    E405,

    /// <summary>フック実行失敗。</summary>
    E501,
    /// <summary>フックのタイムアウト。</summary>
    E502,

    /// <summary>グローバルホットキーの登録に失敗（16章の表には無いため追加）。</summary>
    E601,
    /// <summary>クリップボード監視が利用できない（16章の表には無いため追加）。</summary>
    E602,
}

/// <summary>
/// エラーコードに対応する日本語の内容と対処方法。UI にそのまま表示する。
/// </summary>
public static class ErrorCatalog
{
    private static readonly Dictionary<ErrorCode, (string Summary, string Remedy)> Entries = new()
    {
        [ErrorCode.E001] = ("ブロックが存在しない", "形式を確認してください"),
        [ErrorCode.E002] = ("ヘッダの構文エラー", "該当行の記述を確認してください"),
        [ErrorCode.E003] = ("SEARCH部が空", "AIへ再依頼してください"),
        [ErrorCode.E004] = ("summaryが未指定", "要約を入力してください"),
        [ErrorCode.E005] = ("パッチが途中で切れている", "継続依頼プロンプトを生成します"),
        [ErrorCode.E006] = ("エスケープ規則の不整合", "該当行のエスケープを確認してください"),
        [ErrorCode.E007] = ("キュー内でブロックが重複", "同一ファイルに対する重複を確認し、不要な方を削除してください"),

        [ErrorCode.E101] = ("SEARCH部が見つからない", "インライン編集またはリカバリ支援を使用してください"),
        [ErrorCode.E102] = ("複数箇所にマッチ", "OCCURRENCE を指定してください"),
        [ErrorCode.E103] = ("終了アンカーが見つからない", "AIへ再依頼してください"),
        [ErrorCode.E104] = ("アンカー範囲が閾値を超過", "置換対象の範囲が広すぎないか確認してください"),

        [ErrorCode.E201] = ("パスがルート外", "適用を拒否しました"),
        [ErrorCode.E202] = ("拡張子が未許可", "設定で許可できます"),
        [ErrorCode.E203] = ("サイズ上限超過", "適用を拒否しました"),
        [ErrorCode.E204] = ("ファイルがロック中", "該当ファイルを開いているアプリを閉じてください"),
        [ErrorCode.E205] = ("読み取り専用", "確認のうえ属性を解除できます"),
        [ErrorCode.E206] = ("パス長が上限を超過", "長パス対応で再試行してください"),
        [ErrorCode.E207] = ("同一パッチ内で旧パスを参照", "適用を拒否しました"),
        [ErrorCode.E208] = ("FULL形式とSR形式が混在", "FULL形式を先に適用し、その結果にSR形式を解決します"),

        [ErrorCode.E301] = ("ベースハッシュ不一致", "警告のうえ続行できます"),
        [ErrorCode.E302] = ("適用済みパッチの再投入", "中止しました。強制続行も可能です"),
        [ErrorCode.E303] = ("プロジェクト一致率が低い", "プロジェクトを手動で選択してください"),

        [ErrorCode.E401] = ("バックアップ作成失敗", "適用を中止しました"),
        [ErrorCode.E402] = ("書き込み失敗", "ロールバックを実行しました"),
        [ErrorCode.E403] = ("前回の適用が未完了", "ロールバックを実行できます"),
        [ErrorCode.E404] = ("設定・履歴データの破損", "退避のうえ再生成しました"),
        [ErrorCode.E405] = ("バックアップの実体が見つからない", "復元不可として表示します"),

        [ErrorCode.E501] = ("フック実行失敗", "onFailure の設定に従います"),
        [ErrorCode.E502] = ("フックのタイムアウト", "警告を表示しました"),

        [ErrorCode.E601] = ("ホットキーの登録に失敗", "他のアプリが使用している可能性があります。設定で別のキーを指定してください"),
        [ErrorCode.E602] = ("クリップボード監視が利用できない", "この環境では監視を開始できません"),
    };

    /// <summary>エラーコードの内容（短い説明）を返す。</summary>
    public static string SummaryOf(ErrorCode code) => Entries[code].Summary;

    /// <summary>エラーコードの対処方法を返す。</summary>
    public static string RemedyOf(ErrorCode code) => Entries[code].Remedy;
}
