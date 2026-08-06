using Graft.Platform.Null;

namespace Graft.Platform;

/// <summary>
/// 実行中のOSに応じて <see cref="IPlatformServices"/> の実装を選ぶファクトリ
/// （クロスプラットフォーム版。仕様書v2.1 19章・20章）。
///
/// WPF版の <c>Platform/PlatformServices.cs</c> と役割は同じだが、参照できる実装が異なる。
/// WPF版の <c>Platform/Windows</c> にはWPF依存（トレイアイコンのベクター描画に
/// <c>System.Windows.Media</c> を使う等）が残っているため、この版からは参照しない。
/// Windows実装・Linux実装（<c>Platform/Linux</c>: StatusNotifierItem・XGrabKey・
/// XFixes・XDG Trash・xdg-open・XDGポータル）はフェーズL4で追加する。
/// それまでは全OSでNull実装（利用不可を表明し、何もしない実装）が選ばれ、
/// トレイ常駐・ホットキー・クリップボード監視などが無効な状態で静かに縮退する。
/// </summary>
public static class AvaloniaPlatformServices
{
    private static readonly Lazy<IPlatformServices> Instance = new(Create);

    /// <summary>プロセス内で共有する既定インスタンス。</summary>
    public static IPlatformServices Current => Instance.Value;

    /// <summary>実行中のOSを判定し、対応する実装を新規に生成する。</summary>
    public static IPlatformServices Create() => new NullPlatformServices();
}
