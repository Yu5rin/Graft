namespace Graft.Core.Vba;

/// <summary>
/// リボン（customUI）の読み取りに掛ける大きさの上限（仕様書_ブックの情報と依頼の観点 15章「機能 E: リボン（customUI）の読み取り」）。
/// <para>
/// 【なぜ上限がいるか】 .xlsm は zip で、中の部品は圧縮されている。壊れた（または悪意のある）ブックは、数 KB の圧縮データが
/// 数 GB に膨らむ「zip 爆弾」にできる。実際の customUI は多くても数十 KB（実測の目安: 大きなリボンでも 200 KB 未満）なので、
/// 読む大きさを小さく切っておき、<b>展開しながら上限を 1 バイトでも超えたら読むのをやめる</b>（展開後の大きさとして zip の
/// 見出しに書かれた値は信用しない。嘘を書けるため）。
/// </para>
/// </summary>
/// <param name="MaxPartBytes">customUI の部品 1 つの、展開後の最大バイト数。</param>
/// <param name="MaxRelationshipsBytes">関係の一覧（<c>_rels/.rels</c>）の、展開後の最大バイト数。</param>
public sealed record VbaRibbonLimits(
    int MaxPartBytes = VbaRibbonLimits.DefaultMaxPartBytes,
    int MaxRelationshipsBytes = VbaRibbonLimits.DefaultMaxRelationshipsBytes)
{
    /// <summary>customUI の部品 1 つの、読む大きさの既定（2 MB。実物は多くても数百 KB なので十分に余裕がある）。</summary>
    public const int DefaultMaxPartBytes = 2 * 1024 * 1024;

    /// <summary>関係の一覧の、読む大きさの既定（256 KB。実物は数 KB）。</summary>
    public const int DefaultMaxRelationshipsBytes = 256 * 1024;
}

/// <summary>読み取りの結果の種類（画面の 1 行の理由に使う）。</summary>
public enum VbaRibbonReadStatus
{
    /// <summary>customUI の部品を 1 つ以上、読めた。</summary>
    Ok,

    /// <summary>ブックを読めたが、customUI の設定が無い（リボンを足していないブック）。</summary>
    NoRibbon,

    /// <summary>古い形式（.xls。OLE の複合ファイル）。対象外。</summary>
    LegacyFormat,

    /// <summary>ファイルが無い（移動・削除・名前の変更）。</summary>
    FileNotFound,

    /// <summary>ファイルを開けない（他のプログラムが排他で使っている・権限が無い）。</summary>
    Locked,

    /// <summary>zip として読めない・パッケージの構造が不正。</summary>
    Corrupt,

    /// <summary>customUI の部品はあったが、すべて読めなかった（XML が壊れている・DTD を含む・大きすぎる・部品が無い）。</summary>
    PartsUnreadable,
}

/// <summary>パッケージから読んだ customUI の部品 1 つ（読めたもの、または読めなかった理由）。</summary>
/// <param name="PartName">パッケージ内の部品名（<c>customUI/customUI14.xml</c>。先頭の <c>/</c> は付けない）。</param>
/// <param name="IsOffice2010Format">
/// 新しい形式（関係の種類が <c>…/2007/relationships/ui/extensibility</c>。通常 <c>customUI14.xml</c>）か。
/// false は古い形式（<c>…/2006/relationships/ui/extensibility</c>。通常 <c>customUI.xml</c>）。
/// </param>
/// <param name="Xml">XML の本文（読めたときだけ。BOM は除く）。読めなかったときは null。</param>
/// <param name="Problem">読めなかった理由（1 行）。読めたときは null。</param>
public sealed record VbaRibbonRawPart(string PartName, bool IsOffice2010Format, string? Xml, string? Problem)
{
    /// <summary>読めたか。</summary>
    public bool IsReadable => Xml is not null;
}

/// <summary>パッケージの読み取り結果（XML の解析の前）。</summary>
/// <param name="Status">結果の種類。</param>
/// <param name="Reason">読めなかった（または対象外の）ときの理由 1 行。<see cref="VbaRibbonReadStatus.Ok"/> のときは null。</param>
/// <param name="Parts">見つかった部品（読めなかったものも理由つきで含む）。</param>
/// <param name="FileModified">ファイルの最終更新日時（読めたとき。画面と依頼文に「最後に保存した内容」の目安として出す）。</param>
public sealed record VbaRibbonReadResult(
    VbaRibbonReadStatus Status, string? Reason, IReadOnlyList<VbaRibbonRawPart> Parts, DateTimeOffset? FileModified = null)
{
    /// <summary>読めた部品だけ。</summary>
    public IEnumerable<VbaRibbonRawPart> ReadableParts => Parts.Where(p => p.IsReadable);
}

/// <summary>リボンの構成の 1 要素（タブ・グループ・コントロールなど）。</summary>
/// <param name="Depth">入れ子の深さ（0 始まり。<c>customUI</c> 自身が 0）。構成として出す要素だけを数える。</param>
/// <param name="Kind">要素名（<c>tab</c>・<c>group</c>・<c>button</c>・<c>dropDown</c> など。名前空間は付けない）。</param>
/// <param name="Id">独自の識別子（<c>id</c>、無ければ <c>idQ</c>）。無ければ null。</param>
/// <param name="IdMso">組み込みコントロールの識別子（<c>idMso</c>）。無ければ null。</param>
/// <param name="Label">表示名（<c>label</c> 属性。<c>getLabel</c> のように動的なものは null）。</param>
/// <param name="Callbacks">この要素が持つコールバック（<c>on*</c>・<c>get*</c>・<c>loadImage</c> の各属性）。</param>
public sealed record VbaRibbonNode(
    int Depth, string Kind, string? Id, string? IdMso, string? Label, IReadOnlyList<VbaRibbonCallbackRef> Callbacks)
{
    /// <summary>画面・依頼文に出す、この要素の呼び名（<c>button btn1</c>・<c>button idMso=Copy</c>）。</summary>
    public string Display => Id is not null ? $"{Kind} {Id}" : IdMso is not null ? $"{Kind} idMso={IdMso}" : Kind;
}

/// <summary>要素の属性が指しているコールバック 1 つ（<c>onAction="OnBtn"</c> なら属性 <c>onAction</c>・名前 <c>OnBtn</c>）。</summary>
/// <param name="Attribute">属性名（<c>onAction</c>・<c>getEnabled</c>・<c>onLoad</c> など）。</param>
/// <param name="Name">属性の値（VBA の手続き名。<c>Module1.OnBtn</c> のようにモジュール名つきのこともある）。</param>
/// <param name="Owner">どの要素のものか（<see cref="VbaRibbonNode.Display"/>）。</param>
public sealed record VbaRibbonCallbackRef(string Attribute, string Name, string Owner);

/// <summary>解析した customUI の部品 1 つ。</summary>
/// <param name="Raw">読んだ部品（XML の本文）。</param>
/// <param name="Nodes">構成（解析できたとき）。</param>
/// <param name="Problem">XML として解析できなかった理由 1 行（解析できたときは null）。</param>
public sealed record VbaRibbonPart(VbaRibbonRawPart Raw, IReadOnlyList<VbaRibbonNode> Nodes, string? Problem)
{
    /// <summary>
    /// 解析できて、依頼文に出せる部品か。<see cref="VbaRibbonRawPart.Xml"/> の有無は見ない:
    /// 「構成だけ」「出さない」へ下げたときは、XML の全文を<b>メモリから捨てて</b>（構成は残す）持つため。
    /// </summary>
    public bool IsUsable => Problem is null;
}

/// <summary>コールバックの名前を VBA のミラーと照合した結果。</summary>
public enum VbaRibbonCallbackStatus
{
    /// <summary>照合できていない（ミラーを読めなかった）。</summary>
    Unchecked,

    /// <summary>Public（または修飾なし）の <c>Sub</c>／<c>Function</c> が見つかった。</summary>
    Found,

    /// <summary>同名はあるが <c>Private</c>／<c>Friend</c> で、リボンからは呼べない。</summary>
    NotPublic,

    /// <summary>見つからない。</summary>
    NotFound,

    /// <summary><c>Module1.OnBtn</c> のようにモジュール名を付けたが、そのモジュールが無い。</summary>
    ModuleNotFound,
}

/// <summary>照合に使う VBA のモジュール 1 つ（ミラーのファイル）。</summary>
/// <param name="Name">コンポーネント名（<c>Module1</c>）。</param>
/// <param name="Kind">種別。</param>
/// <param name="Text">本文。</param>
public sealed record VbaRibbonSourceModule(string Name, VbaModuleKind Kind, string Text);

/// <summary>コールバック 1 つ（名前ごとに 1 件にまとめたもの）の照合結果。</summary>
/// <param name="Name">属性に書かれた名前（<c>Module1.OnBtn</c> など、書かれたまま）。</param>
/// <param name="Usages">使われている所（<c>onAction（button btn1）</c> の形）。</param>
/// <param name="Status">照合結果。</param>
/// <param name="Detail">補足（見つかったモジュール・Private の語など。無ければ空）。</param>
public sealed record VbaRibbonCallbackCheck(
    string Name, IReadOnlyList<string> Usages, VbaRibbonCallbackStatus Status, string Detail);

/// <summary>
/// リボン（customUI）の取得結果。<b>メモリにだけ持ち、ディスクへは書かない</b>（<c>label</c> などに業務名が入りうるため）。
/// </summary>
/// <param name="WorkbookName">ブックのファイル名。</param>
/// <param name="WorkbookPath">読んだブックのフルパス（別のブックへ結び直されたとき、前のブックの結果を捨てるための突き合わせに使う）。</param>
/// <param name="ReadAt">読み取った日時。</param>
/// <param name="FileModified">ファイルの最終更新日時。</param>
/// <param name="Status">読み取りの結果の種類。</param>
/// <param name="Reason">読めなかったときの理由 1 行（<see cref="VbaRibbonReadStatus.Ok"/> のときも、一部の部品が読めなかったなら、その旨）。</param>
/// <param name="Parts">解析した部品。</param>
/// <param name="Callbacks">コールバックの一覧と、VBA との照合結果（名前ごと）。</param>
public sealed record VbaRibbonSnapshot(
    string WorkbookName, string WorkbookPath, DateTimeOffset ReadAt, DateTimeOffset? FileModified,
    VbaRibbonReadStatus Status, string? Reason,
    IReadOnlyList<VbaRibbonPart> Parts, IReadOnlyList<VbaRibbonCallbackCheck> Callbacks)
{
    /// <summary>依頼文に出せる部品があるか。</summary>
    public bool HasContent => Status == VbaRibbonReadStatus.Ok && Parts.Any(p => p.IsUsable);

    /// <summary>画面の 1 行に出す結果の説明。</summary>
    public string Describe()
    {
        var at = ReadAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var modified = FileModified?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return Status switch
        {
            VbaRibbonReadStatus.Ok => $"取得: {at}（ファイルの更新: {modified ?? "不明"}。最後に保存した内容です）"
                                      + (Reason is null ? string.Empty : $"。{Reason}"),
            VbaRibbonReadStatus.NoRibbon => "このブックにリボンの設定はありません",
            _ => Reason ?? "リボンを読めませんでした",
        };
    }
}
