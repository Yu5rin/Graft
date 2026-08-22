using System.Globalization;

namespace Graft.Core.Update;

/// <summary>
/// バージョン表記（例: "v1.0.7"・"1.0.7"・"1.0.7.0"）を数値の並びとして解釈し、
/// 桁数が揃っていなくても正しく大小比較できるようにする。
///
/// 【なぜ必要か】タグ名（"v1.0.10"等）と現在の実行アセンブリのバージョン（.csprojの
/// &lt;Version&gt;が3桁までのとき、.NET SDKが自動的に4桁目へ0を補って生成する
/// "1.0.10.0"のような形）を突き合わせる。文字列としてそのまま比較すると
/// "1.0.10" が "1.0.9" より辞書順で小さいと誤判定される（'1' &lt; '9'）。
/// 数値へ分解してから桁ごとに比較することで、この誤判定を避ける。
/// </summary>
public readonly struct UpdateVersion : IComparable<UpdateVersion>, IEquatable<UpdateVersion>
{
    private readonly int[] _segments;

    private UpdateVersion(int[] segments) => _segments = segments;

    /// <summary>解析した数値の並び（例: "1.0.7" → [1, 0, 7]）。</summary>
    public IReadOnlyList<int> Segments => _segments;

    /// <summary>
    /// 文字列を解析する。先頭の "v"/"V" は許容して取り除く。各セグメントは0以上の整数で
    /// なければならず、1つでも数値として解釈できないセグメントがあれば失敗（false）を返す。
    /// 空文字列・null・セグメントが1つも無い（"."だけ等）場合も失敗。
    /// </summary>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed[1..];
        }

        // プレリリースやビルドメタデータの接尾辞（例: "1.0.8-beta"）は本アプリのタグ運用では
        // 使わない想定（<see cref="ErrorMessages"/>ではなくここでコメントとして明記: releases/latest
        // APIはprerelease=trueのリリースを返さない仕様のため、通常のバージョン確認経路では
        // そのようなタグに出会わない）。万一含まれていた場合は「'-'以降を無視」等の推測をせず、
        // 数値として解釈できないセグメントとして素直に失敗させる（誤って新しいと判定しないため）。
        var parts = trimmed.Split('.');
        if (parts.Length == 0) return false;

        var segments = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }
            segments[i] = n;
        }

        version = new UpdateVersion(segments);
        return true;
    }

    /// <summary>
    /// 桁数の違いは短い方を0で補って比較する（例: "1.0.7" と "1.0.7.0" は等しい）。
    /// </summary>
    public int CompareTo(UpdateVersion other)
    {
        var length = Math.Max(_segments.Length, other._segments.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < _segments.Length ? _segments[i] : 0;
            var b = i < other._segments.Length ? other._segments[i] : 0;
            var cmp = a.CompareTo(b);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    public bool Equals(UpdateVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is UpdateVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        // 末尾の0はハッシュに含めない（Equalsが"1.0.7"と"1.0.7.0"を等しいとみなすため、
        // ハッシュもそれに揃える必要がある）。
        var significantLength = _segments.Length;
        while (significantLength > 0 && _segments[significantLength - 1] == 0) significantLength--;
        for (var i = 0; i < significantLength; i++) hash.Add(_segments[i]);
        return hash.ToHashCode();
    }

    public override string ToString() => _segments.Length == 0 ? "" : string.Join('.', _segments);

    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;
    public static bool operator ==(UpdateVersion left, UpdateVersion right) => left.Equals(right);
    public static bool operator !=(UpdateVersion left, UpdateVersion right) => !left.Equals(right);
}
