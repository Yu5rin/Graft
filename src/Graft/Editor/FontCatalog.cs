using Avalonia.Media;

namespace Graft.Editor;

/// <summary>
/// 設定画面のフォント選択欄（検討書「フォント設定」）向けに、OSへインストール済みの
/// フォントファミリ名を列挙する。移植元はPane（github.com/Yu5rin/pane）の
/// <c>Pane/FontService.cs</c>だが、Paneは<c>System.Drawing.Text.InstalledFontCollection</c>
/// （WinForms・Windows専用）を使っているのに対し、Avaloniaに同じAPIは無い。代わりに
/// <see cref="FontManager.SystemFonts"/>で列挙する（<see cref="SystemFontCatalog"/>参照）。
///
/// 【なぜインターフェースに切り出すか】
/// 実装（<see cref="SystemFontCatalog"/>）はAvalonia依存（<see cref="FontManager"/>を使う）
/// のため、tests/Graft.Tests（Avalonia非依存の純粋ロジックのみを取り込む方針。
/// Graft.Tests.csproj冒頭のコメント参照）には持ち込めない。呼び出し側
/// （<see cref="Graft.ViewModels.SettingsViewModel"/>）をこのインターフェース越しに
/// 実装させることで、tests/Graft.UiTests側でフェイク実装に差し替えたテストが書ける
/// （ThemeManagerが<see cref="Graft.Platform.ISystemThemeWatcher"/>を経由するのと同じ設計）。
/// </summary>
public interface IFontCatalog
{
    /// <summary>インストール済みの全フォントファミリ名（名前順）。列挙に失敗した場合は空リスト。</summary>
    IReadOnlyList<string> AllFamilyNames { get; }

    /// <summary>インストール済みのうち等幅とみなせるフォントファミリ名（名前順）。列挙に失敗した場合は空リスト。</summary>
    IReadOnlyList<string> MonospaceFamilyNames { get; }
}

/// <summary>
/// 常に空のリストを返す<see cref="IFontCatalog"/>（Null Object）。フォント列挙が使えない環境
/// 向けのフォールバックに使う（<see cref="Graft.Platform.Null.NullSystemThemeWatcher"/>と
/// 同じ位置付け）。設定画面側は空リストを「フォント選択欄をテキスト入力へフォールバックする」
/// 合図として扱う（検討書「失敗時は既定フォントのままにして、設定欄はテキスト入力へ
/// フォールバックする」の要件）。
/// </summary>
public sealed class EmptyFontCatalog : IFontCatalog
{
    public IReadOnlyList<string> AllFamilyNames { get; } = Array.Empty<string>();

    public IReadOnlyList<string> MonospaceFamilyNames { get; } = Array.Empty<string>();
}

/// <summary>
/// <see cref="FontManager.SystemFonts"/>を使った実際のフォント列挙・等幅判定
/// （<see cref="IFontCatalog"/>の本実装）。結果は初回アクセス時に一度だけ計算し、
/// 以後はキャッシュを返す（フォント数が多い環境では等幅判定の計測コストが無視できないため。
/// Pane/FontService.csの方針を踏襲）。
///
/// 【等幅判定: AvaloniaにPaneと同じAPIが無いため調べ直した】
/// PaneはWinForms(System.Drawing)の<c>InstalledFontCollection</c>を使っており、
/// このAPIはフォントが「等幅」であることを示す正式なメタデータ（OpenType/TrueTypeの
/// postテーブルの<c>isFixedPitch</c>フラグ）を公開していないため、"i"と"W"の描画幅を
/// 自前で比較する近似計測に頼っていた（<see cref="MonospaceHeuristic"/>参照）。
/// Avaloniaには<c>InstalledFontCollection</c>相当のAPIは無いが、代わりに
/// <see cref="IGlyphTypeface"/>（SkiaSharp経由でフォントファイルを直接読む）の
/// <c>Metrics.IsFixedPitch</c>で、まさにこの正式なメタデータを直接取得できる
/// （Paneが自前計測に頼らざるを得なかった理由がAvaloniaには当てはまらない）。
/// そのためこちらを主判定に使い、極めて稀にこのフラグを正しく申告しないフォントの保険として
/// <see cref="MonospaceHeuristic"/>の幅比較を併用する（どちらか一方でも等幅と判定すれば
/// 等幅とみなす）。
///
/// 【失敗時にアプリを落とさない】
/// <paramref name="familyNameSource"/>・<paramref name="isMonospacePredicate"/>の呼び出しは
/// いずれもtry/catchで包み、例外時は空リストへフォールバックする（検討書「フォントの列挙に
/// 失敗してもアプリが落ちないこと」）。省略すると既定の実装（<see cref="FontManager.Current"/>
/// 経由）を使う。tests/Graft.UiTests側はこれらを差し替えて、列挙失敗時のフォールバックや、
/// フォント名に<c>'</c>や<c>\</c>を含む場合の挙動を検証する（<see cref="FontFamily"/>は
/// CSS文字列を組み立てるわけではないため、Paneが行っていたようなエスケープ処理はそもそも
/// 不要。詳細はPR説明参照）。
/// </summary>
public sealed class SystemFontCatalog : IFontCatalog
{
    // 幅比較の保険（MonospaceHeuristic）で使う計測用フォントサイズ。実際に表示するサイズとは
    // 無関係（比率判定のためどのサイズで測っても結果は変わらない）。
    private const double MeasureFontSize = 16;

    private readonly Func<IEnumerable<string>> _familyNameSource;
    private readonly Func<string, bool> _isMonospacePredicate;
    private readonly Lazy<(IReadOnlyList<string> All, IReadOnlyList<string> Monospace)> _cache;

    public SystemFontCatalog(
        Func<IEnumerable<string>>? familyNameSource = null,
        Func<string, bool>? isMonospacePredicate = null)
    {
        _familyNameSource = familyNameSource ?? DefaultFamilyNameSource;
        _isMonospacePredicate = isMonospacePredicate ?? DefaultIsMonospace;
        _cache = new Lazy<(IReadOnlyList<string>, IReadOnlyList<string>)>(Compute);
    }

    public IReadOnlyList<string> AllFamilyNames => _cache.Value.All;

    public IReadOnlyList<string> MonospaceFamilyNames => _cache.Value.Monospace;

    private (IReadOnlyList<string> All, IReadOnlyList<string> Monospace) Compute()
    {
        List<string> names;
        try
        {
            names = _familyNameSource()
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            // 列挙自体に失敗した環境（フォント設定が壊れている等）。空リストへ縮退する
            // （検討書の要件どおり、ここで例外を外へ投げない）。
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var mono = new List<string>();
        foreach (var name in names)
        {
            if (TryIsMonospace(name))
            {
                mono.Add(name);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        mono.Sort(StringComparer.OrdinalIgnoreCase);
        return (names, mono);
    }

    private bool TryIsMonospace(string familyName)
    {
        try
        {
            return _isMonospacePredicate(familyName);
        }
        catch (Exception)
        {
            // 1書体だけの判定失敗でリスト全体を諦める必要は無い。その書体だけ
            // 「等幅ではない」側へ倒す（Pane/FontService.IsMonospaceと同じ方針）。
            return false;
        }
    }

    private static IEnumerable<string> DefaultFamilyNameSource()
        => FontManager.Current.SystemFonts.Select(f => f.Name);

    private static bool DefaultIsMonospace(string familyName)
    {
        var typeface = new Typeface(familyName);
        if (!FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface) || glyphTypeface is null)
        {
            return false;
        }

        if (glyphTypeface.Metrics.IsFixedPitch)
        {
            // フォント自身が申告する正式なメタデータを最優先で信じる（クラス冒頭コメント参照）。
            return true;
        }

        // 保険: "i"（細い文字）と"W"（太い文字）の描画幅（advance width）を比較する。
        var narrow = MeasureAdvance(glyphTypeface, 'i');
        var wide = MeasureAdvance(glyphTypeface, 'W');
        return MonospaceHeuristic.IsMonospace(narrow, wide);
    }

    // GetGlyphAdvanceはem単位（DesignEmHeight基準）の値を返すため、実際のフォントサイズへ
    // スケールする（Avalonia.Media.FormattedTextの内部実装と同じ計算式）。
    private static double MeasureAdvance(IGlyphTypeface glyphTypeface, char ch)
    {
        if (!glyphTypeface.TryGetGlyph(ch, out var glyphIndex) || glyphIndex == 0)
        {
            return 0;
        }

        var advance = glyphTypeface.GetGlyphAdvance(glyphIndex);
        return advance * MeasureFontSize / glyphTypeface.Metrics.DesignEmHeight;
    }
}
