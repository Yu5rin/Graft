namespace Graft.Core.Vba;

/// <summary>
/// リボンの書き換え（仕様書_ブックの情報と依頼の観点 16.5・16.6）の待ち時間の置き場（実機の結果で見直す値を、1 か所に集める）。
/// <para>
/// 【なぜ設定にせず定数か】 いまの値は「Excel が閉じた直後はハンドルが残ることがある見込み」という推測で決めた初期値で、実機検証 12
/// （<c>tools/Test-ExcelReopen.ps1</c> が、閉じてから排他で開けるまでの時間を測る）の結果で変えるかもしれない。利用者が触る値にすると、
/// 結果が出る前に設定のキーと範囲が固まってしまう。結果しだいで、(1) 定数を変える、(2) <c>Settings.Vba</c> の項目に昇格する、のどちらかに
/// 決め、決めたら仕様書の 16.5 の段階 7 と付録 B を直す。<c>VbaRibbonRewriteIo</c> が既定値として読み、テストは短い値へ差し替える。
/// </para>
/// </summary>
public static class VbaRibbonRewriteTimings
{
    /// <summary>
    /// 排他で開けるまで待つ上限（16.5 の段階 7。Excel で開いていなかったブックの経路）。ウイルス対策・同期ソフトが一時的に
    /// 掴む時間を見込んだ値。<b>実機で要確認</b>（付録 B）。
    /// </summary>
    public static readonly TimeSpan ExclusiveTimeout = TimeSpan.FromSeconds(10);

    /// <summary>排他で開き直す間隔。</summary>
    public static readonly TimeSpan ExclusiveInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// <b>Graft が Excel でブックを閉じた直後</b>の、排他で開けるまで待つ上限（16.6）。Excel はブックを閉じたあとも、プレビュー・
    /// サムネイル・保存直後のウイルス対策の走査などでしばらくファイルを掴むことがある見込みで、Excel で開いていなかったブックより
    /// 長くなりうるため、別の値にして独立に見直せるようにした。初期値は <see cref="ExclusiveTimeout"/> と同じ。
    /// <b>実機で要確認</b>（検証 12。<c>tools/Test-ExcelReopen.ps1</c> の「閉じてから排他で開けるまで」の実測を見て決める）。
    /// </summary>
    public static readonly TimeSpan ExclusiveTimeoutAfterExcelClose = ExclusiveTimeout;
}
