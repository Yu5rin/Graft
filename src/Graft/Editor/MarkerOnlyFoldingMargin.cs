using Avalonia;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Folding;

namespace Graft.Editor;

/// <summary>
/// 折りたたみマージン（コードエディタ左端の＋/－が並ぶ帯）から、マーカーから下へ伸びる縦線と
/// 折りたたみ範囲の終端で右へ伸びる短い横線（合わせてL字に見える線）だけを消し、＋/－の
/// マーカー自体は残す<see cref="FoldingMargin"/>の派生クラス。実機での指摘（Windows）に基づく
/// 対処で、設定項目は追加せず常にL字線なしにする。
///
/// 【なぜブラシを透明にする方法ではダメか】
/// L字線は基底クラス<see cref="FoldingMargin"/>の<c>Render</c>が
/// <c>FoldingMarkerBrush</c>/<c>SelectedFoldingMarkerBrush</c>添付プロパティから作った
/// <c>Pen</c>で描いている。ところが＋/－のマーカー枠を描く<c>FoldingMarginMarker.Render</c>も
/// 同じ2つのブラシ（<c>FoldingMargin</c>の添付プロパティなので参照先は同一インスタンス）を
/// 使って枠線を描く。つまり線とマーカーは描画に使うブラシを共有しているため、ブラシを透明に
/// 差し替えるとL字線と一緒に＋/－の枠線まで消えてしまい、「線だけ消してマーカーは残す」
/// という要求を満たせない。
///
/// 【なぜこれでマーカーだけ残るか】
/// ＋/－マーカーは<c>FoldingMargin.OnTextViewVisualLinesChanged</c>（本クラスでは上書きしない）が
/// 生成する別コントロール<c>FoldingMarginMarker</c>であり、<c>VisualChildren</c>へ子として
/// 追加されて自分自身の<c>Render</c>で独立に描画される。一方、L字線は<c>FoldingMargin.Render</c>
/// 自身が（内部で<c>CalculateFoldLinesForFoldingsActiveAtStart</c>→
/// <c>CalculateFoldLinesForMarkers</c>→<c>DrawFoldLines</c>を順に呼び出し）マージンの
/// <c>drawingContext</c>へ直接描いている、マーカーの描画とは完全に別経路の処理である。
/// <c>FoldingMargin.Render</c>は<c>public override</c>でsealedではないため、本クラスで
/// <see cref="Render"/>だけを「何もしない」に再オーバーライドすれば、マージン自身が描く線は
/// 一切発生しなくなる一方、子コントロールであるマーカーの描画・レイアウト
/// （<c>MeasureOverride</c>/<c>ArrangeOverride</c>/<c>OnTextViewVisualLinesChanged</c>）は
/// 一切変更していないため、＋/－マーカーはそのまま表示され続ける。
///
/// 【実機での指摘2: マーカーにマウスを合わせてもIビームのまま】
/// <see cref="Avalonia.Input.InputElement.CursorProperty"/>は<c>inherits: true</c>で登録された
/// 継承プロパティである。AvaloniaEditの<c>SelectionMouseHandler</c>はエディタ本文を1回でも
/// クリックすると<c>TextArea.Cursor = Cursor.Parse("IBeam")</c>を<c>TextArea</c>自身への
/// ローカル値として設定する。<c>TextArea</c>はこの折りたたみマージンの祖先であり、以後は
/// マージンも（ローカル値を持たない限り）このIビームを継承してしまう。
/// <c>FoldingMarginMarker.OnPointerMoved</c>自体は自分の<c>Cursor</c>をその場で
/// <c>Cursor.Default</c>（矢印）へ戻すコードを持つが、これは実測（tests/Graft.UiTests/
/// FoldingMarginTests.cs参照）で不十分だと分かった: <c>FoldingMargin.OnTextViewVisualLinesChanged</c>は
/// 可視行が変わるたび（折りたたみの開閉・スクロール・編集など）に<c>FoldingMarginMarker</c>を
/// 全部作り直す。マウスを動かさずに（例えばクリックの結果としてだけ）可視行が変わると、
/// 画面上の同じ位置に「Cursorのローカル値をまだ一度も設定していない新しいマーカー」が
/// 入れ替わり、次にマウスが実際に動くまでIビームのまま取り残される。
///
/// 【なぜマージン側で明示設定するのが確実か】
/// マージン自身（本クラスのインスタンス）は使い回されるため、コンストラクタで一度
/// <c>Cursor</c>をローカル値として矢印に設定しておけば、継承によって
/// 「その時点でまだCursorのローカル値を持たない子（作り直された直後のマーカーを含む）」
/// すべてに矢印が伝播する。マーカー側の<c>OnPointerMoved</c>頼みだとマウスの実際の移動
/// イベントを待つ必要があるが、マージン側の継承元を直しておけば、マーカーが作り直された
/// 瞬間から（マウスが動かなくても）矢印になる。ブラシと違いCursorはFoldingMargin側の
/// 添付プロパティではなく通常の継承プロパティなので、ここで設定してもマーカーの見た目
/// （枠線・背景）には一切影響しない。
///
/// 【実機での指摘3: マージン周りの余白をPaneと同じにする】
/// 「本文エリアと＋－ボタンまでの余白、＋－ボタンから文頭までの余白をPaneと同じに」という
/// 依頼。移植元Pane（<c>src/style.css</c>・<c>src/editor.js</c>）は
/// <c>本文エリア左端 --5px-- マーカー左端 --15px(マーカー本体)-- マーカー右端 --5px-- コード開始位置</c>
/// という左右対称のレイアウト（<see cref="GapLeft"/>/<see cref="MarkerSize"/>/
/// <see cref="GapRight"/>、合計<see cref="TotalWidth"/>=25px）を固定値で採用している。
/// 対してAvaloniaEdit既定の<c>FoldingMargin.MeasureOverride</c>はマージン幅を
/// <c>1.3333 * FontSize</c>（奇数丸め）、<c>FoldingMarginMarker.MeasureCore</c>は
/// マーカー本体を<c>0.9333 * FontSize</c>（奇数丸め）で決めており、どちらもフォントサイズに
/// 追従する。フォントサイズ13px程度では幅17px・マーカー13px程度にしかならず、左右の隙間が
/// 2pxずつしかない（Paneの5px/5pxに対して狭すぎる、という指摘の原因）。Paneに合わせて
/// 固定pxにする（Paneも固定pxのため。Ctrl+ホイールでのフォントサイズ変更時の見え方は
/// 別途実機で確認済み。詳細は本タスクの報告参照）。
///
/// 【マーカー本体を15pxにする方法として採らなかった案】
/// <c>FoldingMarginMarker</c>（マーカー本体の実クラス）は<c>internal sealed</c>のため、
/// このクラス（Graftのアセンブリ）から派生や<c>MeasureCore</c>の上書きが一切できない。
/// 代替として「継承されるフォントサイズ（<c>TextBlock.FontSizeProperty</c>、
/// <c>FoldingMarginMarker.MeasureCore</c>が<c>0.9333倍</c>して丸める元の値）を、このマージン
/// （マーカーの論理・ビジュアル上の親）へローカル値として設定し、丸め後にちょうど15pxへ
/// 収まる値へ逆算する」という方法も検討したが、採らなかった。理由は次の2点:
///   (1) <c>PixelSnapHelpers.RoundToOdd</c>（丸め幅は<c>PixelSnapHelpers.GetPixelSize</c>が
///       常に返す1.0固定）による丸め後にちょうど15.0へ一致させるには、浮動小数点演算の
///       誤差に依存した「都合の良い」FontSize値（15÷0.9333…）を決め打ちする必要があり、
///       AvaloniaEdit側の丸め実装が将来変わると静かに14pxや16pxへずれる恐れがある。
///   (2) フォントサイズという継承プロパティを本来の意味（文字の大きさ）と無関係な
///       「マーカーの見た目上の一辺の長さ」を操作する目的で流用するのは、意図が
///       コードから読み取りにくく、他の用途（例えば将来アイコンフォントを使う変更）と
///       衝突するリスクがある。
/// 代わりに、下記<see cref="ArrangeOverride"/>で子（＝マーカー）のBounds（実際に描画・
/// ヒットテストに使われる矩形。<c>FoldingMarginMarker.Render</c>は<c>Bounds</c>から矩形を
/// 計算しており、<c>MeasureCore</c>が返す<c>DesiredSize</c>を直接は参照しない）を
/// 明示的に望むサイズ・位置へ<c>Arrange</c>し直す方法を採った。<c>FoldingMarginMarker</c>の
/// 型名を一切知らなくても、公開されている基底型<see cref="Layoutable"/>越しに
/// <c>Bounds</c>の読み取りと<c>Arrange</c>の呼び出しができるため、internalな型に一切
/// 依存しない。
/// </summary>
internal sealed class MarkerOnlyFoldingMargin : FoldingMargin
{
    /// <summary>本文エリア左端からマーカー左端までの隙間（Pane同数値）。</summary>
    internal const double GapLeft = 5;

    /// <summary>マーカー本体の一辺（Pane同数値。<c>FOLD_MARKER_SIZE</c>）。</summary>
    internal const double MarkerSize = 15;

    /// <summary>マーカー右端からコード開始位置までの隙間（Pane同数値）。</summary>
    internal const double GapRight = 5;

    /// <summary>マージン全体の幅（左右対称: 5+15+5）。</summary>
    internal const double TotalWidth = GapLeft + MarkerSize + GapRight;

    public MarkerOnlyFoldingMargin()
    {
        // クラスコメント「実機での指摘2」参照。TextAreaから継承したIビームを、この
        // マージン（と、その子である＋/－マーカー）にだけ矢印へ上書きする。
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    /// <summary>
    /// 基底クラスの実装を意図的に呼ばない（呼ぶとL字線が復活する）。何もしないことで
    /// マージン自身が描く線を消す。＋/－マーカーは子コントロールとして別途描画されるため、
    /// ここで何もしなくても消えない（クラスコメント参照）。
    /// </summary>
    public override void Render(DrawingContext drawingContext)
    {
        // 意図的に何もしない。
    }

    /// <summary>
    /// クラスコメント「実機での指摘3」参照。マージン幅をフォントサイズに追従させず
    /// <see cref="TotalWidth"/>固定にする。基底の<c>MeasureOverride</c>は幅の計算以外に
    /// 「各マーカー（内部の<c>_markers</c>リスト、internalで参照不可）へ<c>Measure</c>を
    /// 呼ぶ」という副作用も持っており、これを省くとレイアウトエンジン側でMeasure未実施の
    /// まま<see cref="ArrangeOverride"/>を呼ぶことになってしまう（<c>Layoutable.Arrange</c>は
    /// 未Measureなら自動で救済してくれるが、基底の挙動をそのまま踏襲する意味でも呼んでおく）。
    /// 戻り値（フォントサイズに追従する幅。ここでは使わない）は捨てて、固定幅を返す。
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        _ = base.MeasureOverride(availableSize);
        return new Size(TotalWidth, 0);
    }

    /// <summary>
    /// クラスコメント「実機での指摘3」・「マーカー本体を15pxにする方法として採らなかった案」
    /// 参照。基底の<c>ArrangeOverride</c>にまず縦位置（対応する行の中央）を計算・配置させ
    /// （<c>FoldingMarginMarker.VisualLine</c>/<c>FoldingSection</c>はinternalフィールドで
    /// Graft側から参照できないため、縦位置の計算式自体を移植することはできない・したくない。
    /// AvaloniaEdit内部の行高さ計算と食い違うと将来のバージョンアップで崩れるおそれがある）、
    /// その結果（<c>Bounds</c>の中心Y）だけを保ったまま、横方向（X・幅）と高さを
    /// <see cref="GapLeft"/>・<see cref="MarkerSize"/>で上書きする。マージン幅を
    /// <see cref="TotalWidth"/>（25px）固定にしたうえでマーカーを幅<see cref="MarkerSize"/>
    /// （15px）・X=<see cref="GapLeft"/>（5px）へ配置するため、右側の隙間は自動的に
    /// <c>25 - 5 - 15 = 5px</c>となり、依頼の要点である左右対称が数値計算だけで成立する
    /// （右側の隙間を別途計算し直す必要が無い）。
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);

        // VisualChildrenはFoldingMarginMarker（internal sealed）を含むが、公開されている
        // 基底型Layoutableとしてなら型名を出さずに扱える（Bounds読み取り・Arrange呼び出しの
        // どちらもLayoutable/Visualの公開メンバー）。
        foreach (var child in VisualChildren)
        {
            if (child is not Layoutable marker) continue;

            var bounds = marker.Bounds;
            var centerY = bounds.Y + bounds.Height / 2.0;
            marker.Arrange(new Rect(GapLeft, centerY - MarkerSize / 2.0, MarkerSize, MarkerSize));
        }

        return result;
    }
}
