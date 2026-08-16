using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Graft.Views;

/// <summary>
/// カラープレビュー機能（検討書「コード中のカラープレビュー」）のカラーピッカー本体。
/// <c>ColorPreviewElementGenerator</c>のスウォッチクリックで開き、選んだ色を1件返す。
///
/// 【Avalonia.Controls.ColorPickerパッケージを使った判断】
/// Avaloniaに標準の<c>ColorPicker</c>／<c>ColorView</c>（<c>Avalonia.Controls.ColorPicker</c>
/// パッケージ、Avalonia本体と同じMITライセンス）があるかをまず調べたところ存在し、
/// <c>Views/OpenSourceLicensesWindow.axaml.cs</c>のライセンス表記にも元々含まれていた
/// （Avalonia本体のライセンスを共用するため追加のライセンス埋め込みは不要）。色相・彩度・
/// 明度の面、16進/RGB入力欄、アルファスライダーを一通り備えており、自前でHSV変換や
/// スライダーUIを組むより既製の作り込みを使うほうが品質・保守性の両面で妥当と判断し、
/// これを土台にした（Graft.csprojのコメント・報告参照）。
///
/// 【Paneのようなリアルタイム反映はしない】
/// Pane（移植元）はドラッグ中も本文へ即座に反映し、<c>Transaction.addToHistory.of(false)</c>相当
/// （アンドゥ履歴に積まない）の仕組みで中間状態を隠していた。GraftのAvaloniaEdit
/// （<see cref="AvaloniaEdit.Document.UndoStack"/>）にも<c>StartUndoGroup</c>/<c>EndUndoGroup</c>は
/// あるが、それだけでは「アンドゥ履歴に一切積まない書き込み」は表現できず、無理に近い動きを
/// 再現するよりも設計をシンプルにする方を選んだ。本パネルは色を選んでいる間はドキュメントに
/// 一切書き込まず、「適用」（またはEnter・パネル外クリック）で初めて1回だけ書き込む。
/// これにより:
///   - アンドゥ: 書き込みは常に1回の<c>TextDocument.Replace</c>なので、通常のCtrl+Zでそのまま戻る。
///   - キャンセル（Esc・「キャンセル」ボタン）: そもそも何も書いていないため、閉じるだけで安全に
///     実現できる（Pane同様「開いた時の色に戻す」ための復元処理が不要）。
/// という、利用者指示の要件（アンドゥとキャンセルを用意する）を、Paneより単純な仕組みで満たす。
///
/// 【パネル外クリックの扱い ― Paneとの意図的な違い】
/// Pane仕様は「パネル外クリックはその時点の色を確定して適用」とする（リアルタイム反映済みの
/// 色が消えたと受け取られないため）。本パネルはリアルタイム反映をしないため、その理由が
/// そもそも当てはまらない。「明示的に選ばない限りドキュメントは変わらない」方が驚きが少ないと
/// 判断し、ウィンドウの非アクティブ化（<see cref="OnDeactivated"/>）は**キャンセル**として扱う
/// （Escキー・「キャンセル」ボタンと同じ）。
/// </summary>
public partial class ColorPickerPopup : Window
{
    private bool _resolved;

    /// <summary>「適用」（Enter・ボタンいずれか）で確定したときに、選ばれた色を1回だけ通知する。</summary>
    public event EventHandler<Color>? ColorConfirmed;

    /// <summary>Esc・「キャンセル」・パネル外クリックのいずれかで閉じたときに通知する
    /// （ドキュメントには何も書き込まれていない）。</summary>
    public event EventHandler? Cancelled;

    public ColorPickerPopup()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        Deactivated += OnDeactivated;
        // ウィンドウ自体ではなく実際のコントロールへフォーカスを当てる
        // （QueueWindow等、本プロジェクトの他のダイアログと同じ作法。Window.Activate()だけでは
        // キーボードフォーカスが移らない環境があったため、実際にフォーカス可能な要素を明示的に
        // フォーカスすることで確実性を上げる）。
        Loaded += (_, _) => ApplyButton.Focus();
    }

    /// <summary>表示前に呼ぶ。<paramref name="alphaEnabled"/>は編集対象のリテラルが元々アルファを
    /// 持っていた場合のみtrue（Pane仕様§4.1「元のリテラルがアルファを持っていた場合のみ、
    /// 不透明度スライダー」の踏襲）。</summary>
    public void Configure(Color initialColor, bool alphaEnabled)
    {
        Picker.IsAlphaEnabled = alphaEnabled;
        Picker.IsAlphaVisible = alphaEnabled;
        Picker.Color = initialColor;
    }

    private void OnApplyClicked(object? sender, RoutedEventArgs e) => Resolve(confirmed: true);

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Resolve(confirmed: false);

    private void OnDeactivated(object? sender, EventArgs e) => Resolve(confirmed: false);

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Resolve(confirmed: false);
        e.Handled = true;
    }

    private void Resolve(bool confirmed)
    {
        if (_resolved) return;
        _resolved = true;
        Deactivated -= OnDeactivated;
        if (confirmed) ColorConfirmed?.Invoke(this, Picker.Color);
        else Cancelled?.Invoke(this, EventArgs.Empty);
        Close();
    }

    /// <summary>利用者指示「ドラッグで移動できる必要がある」。装飾なしウィンドウ
    /// （<c>SystemDecorations="None"</c>）のため、上部のDragHandleを掴んだときだけ
    /// OS標準の移動ドラッグ（<see cref="Window.BeginMoveDrag"/>、X11/Wayland/Windowsいずれでも
    /// 実際のウィンドウ移動として扱われる）を開始する。</summary>
    private void OnDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
