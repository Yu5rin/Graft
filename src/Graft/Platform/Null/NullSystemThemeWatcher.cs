namespace Graft.Platform.Null;

/// <summary>
/// <see cref="ISystemThemeWatcher"/> の何もしない実装。システムテーマの判定に対応しない環境
/// （非Windows。Linux実装はフェーズL4）向け。仕様書2.3のとおり、判定できない場合は
/// 呼び出し側でダークへフォールバックさせるため、常に null を返す。
/// </summary>
public sealed class NullSystemThemeWatcher : ISystemThemeWatcher
{
    public bool IsSupported => false;

    public string? UnsupportedReason => "この環境ではシステムテーマの自動判定に対応していません。";

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public bool? TryReadIsLightTheme() => null;

    public void StartWatching()
    {
        // 何もしない。
    }

    public void StopWatching()
    {
        // 何もしない。
    }

    public void Dispose()
    {
        // 何もしない。
    }
}
