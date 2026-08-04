using System.IO;
using System.Windows;
using Graft.Infra;

namespace Graft.ViewModels;

/// <summary>
/// プロジェクトごとに記憶するペイン幅（仕様書8.2）。
/// 左ペイン内の「プロジェクト一覧」列と中央「ブロック一覧」列の幅を保持する。
/// 右ペイン（diff）は残り幅を使う可変列のため保存対象にしない。
/// </summary>
public sealed class ProjectPaneLayout
{
    public double ProjectColumnWidth { get; set; } = 260;
    public double BlockColumnWidth { get; set; } = 380;

    /// <summary>diffのコード表示フォントサイズ（仕様書8.4「プロジェクトごとに記憶する」）。</summary>
    public double CodeFontSize { get; set; } = 13;
}

/// <summary>
/// layout.json の内容全体（仕様書8.11）。
/// ウィンドウ位置・サイズ・最大化状態・左ペイン上下比率・プロジェクト別ペイン幅を保持する。
/// </summary>
public sealed class WindowLayoutState
{
    /// <summary>ウィンドウ左端。未保存時は <see cref="double.NaN"/>（初回起動を示す）。</summary>
    public double Left { get; set; } = double.NaN;

    /// <summary>ウィンドウ上端。未保存時は <see cref="double.NaN"/>。</summary>
    public double Top { get; set; } = double.NaN;

    public double Width { get; set; } = 1280;

    public double Height { get; set; } = 800;

    public bool IsMaximized { get; set; }

    /// <summary>左ペイン内のプロジェクト一覧（上段）が占める比率（0〜1）。残りを履歴が使う。</summary>
    public double LeftPaneSplitRatio { get; set; } = 0.55;

    /// <summary>プロジェクトIDをキーとしたペイン幅。仕様書8.2「ペイン幅はプロジェクトごとに記憶する」。</summary>
    public Dictionary<string, ProjectPaneLayout> ProjectPaneWidths { get; set; } = new();
}

/// <summary>
/// ウィンドウ・ペインレイアウトの永続化（仕様書8.11）。
/// 保存先は <c>AppPaths.BaseDirectory</c> 配下の <c>layout.json</c>
/// （<see cref="AppPaths"/> 自体には layout.json 用のプロパティが無いため、ここで組み立てる）。
/// </summary>
public sealed class WindowLayoutStore
{
    private readonly string _filePath;
    private readonly JsonFileStore _store = new();

    public WindowLayoutStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _filePath = Path.Combine(paths.BaseDirectory, "layout.json");
    }

    /// <summary>layout.json を読み込む。存在しない・破損している場合は既定値から再生成する。</summary>
    public async Task<WindowLayoutState> LoadAsync(CancellationToken ct = default)
    {
        var result = await _store
            .ReadWithRecoveryAsync(_filePath, static () => new WindowLayoutState(), JsonFileStore.DefaultOptions, ct)
            .ConfigureAwait(false);
        return result.Value;
    }

    /// <summary>layout.json を書き込む。</summary>
    public Task SaveAsync(WindowLayoutState state, CancellationToken ct = default)
        => _store.WriteAsync(_filePath, state, JsonFileStore.DefaultOptions, ct);

    /// <summary>
    /// 保存された位置・サイズから実際にウィンドウへ適用すべき矩形を求める。
    /// 現在のモニタ構成（仮想画面全体）でタイトルバー付近が画面外になる場合は
    /// プライマリモニタの作業領域中央へ補正する（仕様書8.11）。
    /// </summary>
    public static Rect ResolveWindowBounds(WindowLayoutState state, double minWidth, double minHeight)
    {
        var width = Math.Max(state.Width, minWidth);
        var height = Math.Max(state.Height, minHeight);

        if (double.IsNaN(state.Left) || double.IsNaN(state.Top) || !IsReachable(state.Left, state.Top, width))
        {
            return CenterOnPrimary(width, height);
        }

        return new Rect(state.Left, state.Top, width, height);
    }

    /// <summary>
    /// タイトルバー相当の矩形が現在の仮想画面（全モニタの合成領域）に一部でも
    /// かかっていれば「到達可能」とみなす。モニタ構成の変化（取り外し等）で
    /// 完全に画面外へ追いやられているケースだけを補正対象とする。
    /// </summary>
    private static bool IsReachable(double left, double top, double width)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        var titleBar = new Rect(left, top, Math.Min(width, 200), 40);
        return virtualScreen.IntersectsWith(titleBar);
    }

    private static Rect CenterOnPrimary(double width, double height)
    {
        var workArea = SystemParameters.WorkArea;
        var boundedWidth = Math.Min(width, workArea.Width);
        var boundedHeight = Math.Min(height, workArea.Height);
        var left = workArea.Left + Math.Max(0, (workArea.Width - boundedWidth) / 2);
        var top = workArea.Top + Math.Max(0, (workArea.Height - boundedHeight) / 2);
        return new Rect(left, top, boundedWidth, boundedHeight);
    }

    /// <summary>指定プロジェクトのペイン幅設定を取得する。無ければ既定値で作成して登録する。</summary>
    public static ProjectPaneLayout GetOrCreatePaneLayout(WindowLayoutState state, string projectId)
    {
        if (!state.ProjectPaneWidths.TryGetValue(projectId, out var layout))
        {
            layout = new ProjectPaneLayout();
            state.ProjectPaneWidths[projectId] = layout;
        }
        return layout;
    }
}
