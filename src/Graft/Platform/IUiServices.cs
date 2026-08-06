namespace Graft.Platform;

/// <summary>
/// ViewModel層が必要とするUIフレームワーク固有の機能。
///
/// ViewModel の大半は <c>ICommand</c>（System.ObjectModel）とロジックだけで書けており、
/// UIフレームワークへの依存はクリップボード・画面情報・タイマーの3つに限られる。
/// これらをここへ抽象化することで、ViewModel を WPF 版と Avalonia 版で
/// そのまま共有できる（仕様書v2.1 19章・20章 L3）。
/// </summary>
public interface IUiServices
{
    /// <summary>クリップボード。</summary>
    IClipboardAccess Clipboard { get; }

    /// <summary>画面構成の問い合わせ。ウィンドウ位置の復元（9.7）に使う。</summary>
    IScreenInfo Screens { get; }

    /// <summary>UIスレッド上で動く反復タイマーを作る。デバウンス処理に使う。</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action onTick);
}

/// <summary>クリップボードの読み書き。</summary>
public interface IClipboardAccess
{
    /// <summary>テキストを書き込む。失敗しても例外を投げない。</summary>
    void SetText(string text);

    /// <summary>
    /// テキストを読み取る。取得できない場合は null。
    ///
    /// 非同期にしているのは、X11のクリップボードが「所有アプリへ要求を送り、応答を
    /// イベントループで受け取る」仕組みのため。UIスレッドを塞いで待つと、応答を処理する
    /// イベントループごと止まって取得に失敗する（Linuxで実際に発生した）。
    /// </summary>
    Task<string?> GetTextAsync();
}

/// <summary>画面構成の問い合わせ。</summary>
public interface IScreenInfo
{
    /// <summary>全モニタを合成した仮想画面の矩形。</summary>
    UiRect VirtualScreen { get; }

    /// <summary>プライマリモニタの作業領域（タスクバー等を除いた範囲）。</summary>
    UiRect PrimaryWorkArea { get; }

    /// <summary>OSの「アニメーションを表示する」設定が有効かどうか（9章のモーション制御）。</summary>
    bool IsAnimationEnabled { get; }
}

/// <summary>UIスレッド上で動く反復タイマー。</summary>
public interface IUiTimer : IDisposable
{
    /// <summary>開始する。すでに動いている場合は最初から数え直す。</summary>
    void Restart();

    /// <summary>停止する。</summary>
    void Stop();
}

/// <summary>
/// 矩形。UIフレームワークの Rect 型に依存しないための最小の値型。
/// </summary>
public readonly record struct UiRect(double Left, double Top, double Width, double Height)
{
    /// <summary>右端。</summary>
    public double Right => Left + Width;

    /// <summary>下端。</summary>
    public double Bottom => Top + Height;

    /// <summary>指定の矩形と少しでも重なるかどうか。</summary>
    public bool IntersectsWith(UiRect other)
        => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
}
