namespace Graft.Core.Vba;

/// <summary>
/// ブックの情報の「シートごとの見せ方」（仕様書_ブックの情報と依頼の観点 5.2。v0.4）。
/// <para>
/// 【なぜシートごとか】 「このシートは 5 行目まで、このシートは全部見せないと理解できない」のように、シートごとに
/// 事情が違う。ブック全体で「見出し＋N 行」の 1 種類しか選べないと、足りないシートでは AI が誤読し、
/// 足りすぎるシートでは社外秘が余計に渡る。
/// </para>
/// </summary>
public enum VbaSheetViewMode
{
    /// <summary>出さない。そのシートは何も渡さない（シート名・使用範囲・テーブル・シート限定の名前も出ない）。</summary>
    None,

    /// <summary>
    /// 構成だけ（既定）。シート名・コード名・使用範囲・テーブル名と列名。<b>セルの値は渡らない</b>
    /// （Excel のセルを読む COM 呼び出しも行わない）。
    /// </summary>
    StructureOnly,

    /// <summary>見出し＋N 行。使用範囲の先頭行（見出し）と、続く N 行の値。N はシートごと。</summary>
    HeaderRows,

    /// <summary>全部。使用範囲の値すべて。ただし 1 シートあたりの行数・列数に上限がある（設定 <c>Vba.WorkbookInfoAllMaxRows</c>／<c>AllMaxColumns</c>）。</summary>
    All,

    /// <summary>範囲を指定。利用者が入れた範囲（例 <c>A1:F30</c>）の値。</summary>
    Range,
}

/// <summary>
/// 1 枚のシートの見せ方。<see cref="PassesValues"/> が true のものだけが、セルの値を AI へ渡す
/// （渡す見せ方を選ぶときは確認ダイアログを出す。仕様書 5.2）。
/// </summary>
/// <param name="Mode">見せ方。</param>
/// <param name="HeaderRows">見出しに続けて渡す行数（<see cref="VbaSheetViewMode.HeaderRows"/> のときだけ意味を持つ。0 なら見出しだけ）。</param>
/// <param name="IncludeFormulas">数式も含めるか（値が渡る見せ方のときだけ意味を持つ）。</param>
/// <param name="Range">
/// 範囲（<see cref="VbaSheetViewMode.Range"/> のときだけ意味を持つ）。<see cref="VbaA1Range.TryParse"/> が通り、
/// 正規化した形（<c>A1:F30</c>）で持つ。検証に通らなかった入力はここへ入れない（null のまま＝何も渡さない）。
/// </param>
public sealed record VbaSheetView(
    VbaSheetViewMode Mode = VbaSheetViewMode.StructureOnly,
    int HeaderRows = VbaSheetViewLimits.DefaultHeaderRows,
    bool IncludeFormulas = false,
    string? Range = null)
{
    /// <summary>既定の見せ方（構成だけ）。</summary>
    public static VbaSheetView StructureOnly { get; } = new();

    /// <summary>出さない。</summary>
    public static VbaSheetView Excluded { get; } = new(VbaSheetViewMode.None);

    /// <summary>
    /// セルの値が AI に渡るか。<see cref="VbaSheetViewMode.Range"/> は、<b>有効な範囲が入っているときだけ</b>渡る
    /// （範囲が空・不正なら、何も渡さない。入力途中の文字列を依頼文へ入れないため）。
    /// </summary>
    public bool PassesValues => Mode switch
    {
        VbaSheetViewMode.HeaderRows or VbaSheetViewMode.All => true,
        VbaSheetViewMode.Range => Range is not null && VbaA1Range.TryParse(Range, out _, out _),
        _ => false,
    };

    /// <summary>
    /// ブック全体の出し方（<see cref="VbaWorkbookInfoLevel"/>）を掛けた、実際の見せ方。
    /// 「構成だけ」では値は渡さない（値が渡る見せ方は構成だけに落とす）。<b>「出さない」のシートは構成だけでも守る</b>
    /// （存在を知らせたくないと利用者が選んだシートを、全体を構成だけにしたら出す、という方向の漏れを作らないため）。
    /// 全体が「出さない」なら何も出さないので、ここでは扱わない（呼び出し側が先に空にする）。
    /// </summary>
    public VbaSheetView ForLevel(VbaWorkbookInfoLevel level)
        => level == VbaWorkbookInfoLevel.Full || Mode is VbaSheetViewMode.None or VbaSheetViewMode.StructureOnly
            ? this
            : StructureOnly;

    /// <summary>範囲を正規化して返す（<see cref="VbaA1Range"/> を通らなければ null）。</summary>
    public static string? NormalizeRange(string? text)
        => VbaA1Range.TryParse(text, out var range, out _) ? range.ToA1() : null;
}

/// <summary>見せ方まわりの上限と既定。</summary>
public static class VbaSheetViewLimits
{
    /// <summary>「見出し＋N 行」の N の既定（設定 <c>Vba.WorkbookInfoSampleRows</c> の既定と同じ）。</summary>
    public const int DefaultHeaderRows = 5;

    /// <summary>「見出し＋N 行」の N の上限（これより多く見せたいときは「全部」か「範囲を指定」を使う）。</summary>
    public const int MaxHeaderRows = 100;

    /// <summary>「全部」「範囲を指定」の 1 シートあたりの行数の既定。</summary>
    public const int DefaultAllMaxRows = 500;

    /// <summary>「全部」「範囲を指定」の 1 シートあたりの列数の既定。</summary>
    public const int DefaultAllMaxColumns = 50;

    /// <summary>N を 0〜<see cref="MaxHeaderRows"/> に収める。</summary>
    public static int ClampHeaderRows(int rows) => Math.Clamp(rows, 0, MaxHeaderRows);
}

/// <summary><see cref="VbaSheetViewMode"/> の文字列（保存用）と画面の名前との往復。</summary>
public static class VbaSheetViewModes
{
    /// <summary>画面に並べる順（表の順。出さない → 構成だけ → 見出し＋N 行 → 全部 → 範囲を指定）。</summary>
    public static IReadOnlyList<VbaSheetViewMode> InDisplayOrder { get; } = new[]
    {
        VbaSheetViewMode.None, VbaSheetViewMode.StructureOnly, VbaSheetViewMode.HeaderRows,
        VbaSheetViewMode.All, VbaSheetViewMode.Range,
    };

    /// <summary>
    /// 保存された文字列を読む。null・空・不明は構成だけ（値が渡らない側＝安全側に倒す。
    /// 壊れた値で、確認なしに値が渡り始めてはならない）。
    /// </summary>
    public static VbaSheetViewMode Parse(string? text)
        => Enum.TryParse<VbaSheetViewMode>(text, ignoreCase: false, out var mode) && Enum.IsDefined(mode)
            ? mode
            : VbaSheetViewMode.StructureOnly;

    /// <summary>画面の名前。</summary>
    public static string Label(VbaSheetViewMode mode) => mode switch
    {
        VbaSheetViewMode.None => "出さない",
        VbaSheetViewMode.HeaderRows => "見出し＋N 行",
        VbaSheetViewMode.All => "全部",
        VbaSheetViewMode.Range => "範囲を指定",
        _ => "構成だけ",
    };

    /// <summary>値が渡る見せ方か（範囲の有効・無効は見ない。確認ダイアログを出すかの判断に使う）。</summary>
    public static bool IsValueMode(VbaSheetViewMode mode)
        => mode is VbaSheetViewMode.HeaderRows or VbaSheetViewMode.All or VbaSheetViewMode.Range;
}

/// <summary>
/// ブック全体の、シートごとの見せ方の一覧。保存先はシートの<b>コード名</b>（表示名を変えても外れない）。
/// 一覧に無いシートは <see cref="Default"/>。コード名の大文字小文字は区別しない（VBA のコード名は区別しない）。
/// </summary>
public sealed class VbaSheetViewSet
{
    private readonly Dictionary<string, VbaSheetView> _byCodeName;

    /// <summary>一覧に無いシートの見せ方。</summary>
    public VbaSheetView Default { get; }

    /// <summary>全シートを構成だけにした一覧。</summary>
    public static VbaSheetViewSet AllStructureOnly { get; } = new(VbaSheetView.StructureOnly, Array.Empty<KeyValuePair<string, VbaSheetView>>());

    /// <param name="defaultView">一覧に無いシートの見せ方。</param>
    /// <param name="overrides">コード名ごとの見せ方。同じコード名が重なったら後のものを採る。</param>
    public VbaSheetViewSet(VbaSheetView defaultView, IEnumerable<KeyValuePair<string, VbaSheetView>> overrides)
    {
        ArgumentNullException.ThrowIfNull(defaultView);
        ArgumentNullException.ThrowIfNull(overrides);
        Default = defaultView;
        _byCodeName = new Dictionary<string, VbaSheetView>(StringComparer.OrdinalIgnoreCase);
        foreach (var (codeName, view) in overrides)
        {
            if (!string.IsNullOrEmpty(codeName) && view is not null) _byCodeName[codeName] = view;
        }
    }

    /// <summary>明示した見せ方を持つコード名（保存されている記録）。</summary>
    public IReadOnlyCollection<string> CodeNames => _byCodeName.Keys;

    /// <summary>明示した見せ方（<see cref="CodeNames"/> の分）。</summary>
    public IEnumerable<KeyValuePair<string, VbaSheetView>> Overrides => _byCodeName;

    /// <summary>シートの見せ方。</summary>
    public VbaSheetView For(string codeName)
        => !string.IsNullOrEmpty(codeName) && _byCodeName.TryGetValue(codeName, out var view) ? view : Default;

    /// <summary>ブック全体の出し方を掛けた一覧（<see cref="VbaSheetView.ForLevel"/>）。</summary>
    public VbaSheetViewSet ForLevel(VbaWorkbookInfoLevel level)
        => level == VbaWorkbookInfoLevel.Full
            ? this
            : new VbaSheetViewSet(
                Default.ForLevel(level),
                _byCodeName.Select(p => new KeyValuePair<string, VbaSheetView>(p.Key, p.Value.ForLevel(level))));

    /// <summary>1 枚の見せ方を差し替えた新しい一覧。</summary>
    public VbaSheetViewSet With(string codeName, VbaSheetView view)
    {
        var copy = _byCodeName.Where(p => !string.Equals(p.Key, codeName, StringComparison.OrdinalIgnoreCase)).ToList();
        copy.Add(new KeyValuePair<string, VbaSheetView>(codeName, view));
        return new VbaSheetViewSet(Default, copy);
    }
}
