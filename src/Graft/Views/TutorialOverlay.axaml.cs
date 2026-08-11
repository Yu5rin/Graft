using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Graft.Views;

/// <summary>
/// 画面上のチュートリアル（コーチマーク方式）のオーバーレイ。TutorialOverlay.axamlのクラス
/// コメント参照。制御の実体（ステップの進行・実際の解析/適用/復元の実行）はShellWindow.
/// Tutorial.csが持ち、このクラスは「指定された矩形を明るく見せて、その近くに吹き出しを出す」
/// 描画・配置計算のみを担う。
/// </summary>
public partial class TutorialOverlay : UserControl
{
    // 対象コントロールと吹き出し・ハイライト枠との間隔、画面端からの最小余白。
    private const double Gap = 12;
    private const double EdgeMargin = 12;
    private const double BubbleWidth = 340;
    // 対象が見つからない・小さすぎる場合のフォールバック高さ（吹き出しの位置計算用）。
    private const double FallbackBubbleHeight = 160;
    // ハイライト枠を対象の実際の矩形より一回り大きくする量（片側）。
    private const double HighlightPadding = 4;

    /// <summary>「次へ」（ステップによっては「適用する」「元に戻す」等に文言が変わる）。</summary>
    public event EventHandler? NextRequested;

    /// <summary>「戻る」。</summary>
    public event EventHandler? BackRequested;

    /// <summary>「終了」（吹き出し右上のボタン）。Escキーの処理はShellWindow.Keyboard.cs側が担う。</summary>
    public event EventHandler? ExitRequested;

    public TutorialOverlay()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 1ステップぶんの表示を更新する。<paramref name="targetBounds"/>はこのコントロール自身の
    /// 座標系（=ShellWindowの本体全体）における対象コントロールの矩形。nullなら対象コントロールが
    /// 見つからなかった／非表示のときで、ハイライトなし・吹き出しは画面中央付近に出す。
    /// </summary>
    public void ShowStep(
        Rect? targetBounds, string stepIndicator, string title, string message,
        string primaryLabel, bool backEnabled)
    {
        IsVisible = true;
        StepIndicatorText.Text = stepIndicator;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryLabel;
        BackButton.IsEnabled = backEnabled;

        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            // 初回表示直後などまだ計測前の場合のフォールバック（Windowの既定サイズ相当）。
            size = new Size(1280, 800);
        }

        UpdateScrimAndHighlight(size, targetBounds);
        PositionBubble(size, targetBounds);

        // Enter/Escで操作できるよう、既定ボタンへフォーカスを当てる。表示直後はまだ
        // レイアウトが確定しておらずFocus()が効かないことがあるため、1フレーム遅らせる
        // （ShellWindow.axaml.csの検索ボックスへのフォーカスと同じ作法）。
        Dispatcher.UIThread.Post(() => PrimaryButton.Focus(), DispatcherPriority.Background);
    }

    /// <summary>チュートリアルの中断・完了時に呼ぶ。</summary>
    public void HideOverlay() => IsVisible = false;

    private void OnBackClicked(object? sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void OnPrimaryClicked(object? sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);

    private void OnExitClicked(object? sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 暗幕（ScrimPath）に対象矩形ぶんの「穴」を空け、ハイライト枠をその縁に重ねる。
    /// FillRule.EvenOddで「画面全体の矩形」と「対象の矩形」の2つの図形を重ねると、
    /// 奇数回覆われる部分（画面全体のみ＝対象の外側）だけが塗られ、対象の内側は塗られない
    /// （＝暗幕の下の実際の画面がそのまま見える）。
    /// </summary>
    private void UpdateScrimAndHighlight(Size bounds, Rect? targetBounds)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.SetFillRule(FillRule.EvenOdd);

            ctx.BeginFigure(new Point(0, 0), isFilled: true);
            ctx.LineTo(new Point(bounds.Width, 0));
            ctx.LineTo(new Point(bounds.Width, bounds.Height));
            ctx.LineTo(new Point(0, bounds.Height));
            ctx.EndFigure(isClosed: true);

            if (targetBounds is { } t && t.Width > 0 && t.Height > 0)
            {
                var hole = Inflate(ClampToBounds(t, bounds), HighlightPadding, bounds);
                ctx.BeginFigure(hole.TopLeft, isFilled: true);
                ctx.LineTo(new Point(hole.Right, hole.Top));
                ctx.LineTo(new Point(hole.Right, hole.Bottom));
                ctx.LineTo(new Point(hole.Left, hole.Bottom));
                ctx.EndFigure(isClosed: true);

                HighlightBorder.IsVisible = true;
                HighlightBorder.Margin = new Thickness(hole.Left, hole.Top, 0, 0);
                HighlightBorder.Width = hole.Width;
                HighlightBorder.Height = hole.Height;
            }
            else
            {
                HighlightBorder.IsVisible = false;
            }
        }

        ScrimPath.Data = geometry;
    }

    /// <summary>
    /// 吹き出しの位置を決める。対象の下→上→右→左の順で、はみ出さずに収まる置き場所を探し、
    /// どれも収まりきらない場合でも最終的に画面内へクランプする（要件: 画面外にはみ出さない）。
    /// 対象が無い場合は画面中央付近に置く。
    /// </summary>
    private void PositionBubble(Size bounds, Rect? targetBounds)
    {
        BubbleBorder.Width = BubbleWidth;
        BubbleBorder.Measure(new Size(BubbleWidth, double.PositiveInfinity));
        var measuredHeight = BubbleBorder.DesiredSize.Height;
        var bubbleHeight = measuredHeight > 0 ? measuredHeight : FallbackBubbleHeight;

        double x, y;
        if (targetBounds is { } t && t.Width > 0 && t.Height > 0)
        {
            var target = Inflate(ClampToBounds(t, bounds), HighlightPadding, bounds);

            if (target.Bottom + Gap + bubbleHeight <= bounds.Height - EdgeMargin)
            {
                x = target.Left;
                y = target.Bottom + Gap;
            }
            else if (target.Top - Gap - bubbleHeight >= EdgeMargin)
            {
                x = target.Left;
                y = target.Top - Gap - bubbleHeight;
            }
            else if (target.Right + Gap + BubbleWidth <= bounds.Width - EdgeMargin)
            {
                x = target.Right + Gap;
                y = target.Top;
            }
            else if (target.Left - Gap - BubbleWidth >= EdgeMargin)
            {
                x = target.Left - Gap - BubbleWidth;
                y = target.Top;
            }
            else
            {
                // 対象が画面いっぱいに近い等、どこにも余白が無い極端なケース。
                // 下に置いたうえで、このあとの最終クランプで画面内へ収める。
                x = target.Left;
                y = target.Bottom + Gap;
            }
        }
        else
        {
            x = (bounds.Width - BubbleWidth) / 2;
            y = (bounds.Height - bubbleHeight) / 2;
        }

        // 要件: 対象が画面端にある・小さい等どんな場合でも、吹き出し自体は必ず画面内に収める。
        x = Math.Clamp(x, EdgeMargin, Math.Max(EdgeMargin, bounds.Width - BubbleWidth - EdgeMargin));
        y = Math.Clamp(y, EdgeMargin, Math.Max(EdgeMargin, bounds.Height - bubbleHeight - EdgeMargin));

        BubbleBorder.Margin = new Thickness(x, y, 0, 0);
    }

    /// <summary>矩形を画面（このコントロール）の範囲内へ収める。対象が画面端で一部しか見えない場合の保険。</summary>
    private static Rect ClampToBounds(Rect rect, Size bounds)
    {
        var left = Math.Clamp(rect.Left, 0, bounds.Width);
        var top = Math.Clamp(rect.Top, 0, bounds.Height);
        var right = Math.Clamp(rect.Right, 0, bounds.Width);
        var bottom = Math.Clamp(rect.Bottom, 0, bounds.Height);
        if (right <= left) right = Math.Min(bounds.Width, left + 1);
        if (bottom <= top) bottom = Math.Min(bounds.Height, top + 1);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>矩形を四方へ<paramref name="amount"/>だけ広げ、画面内へ再クランプする。</summary>
    private static Rect Inflate(Rect rect, double amount, Size bounds)
        => ClampToBounds(new Rect(rect.Left - amount, rect.Top - amount, rect.Width + amount * 2, rect.Height + amount * 2), bounds);
}
