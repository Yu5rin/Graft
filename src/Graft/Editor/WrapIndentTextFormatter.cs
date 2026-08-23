using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace Graft.Editor;

/// <summary>
/// 課題#72。整形されようとしている<see cref="VisualLine"/>を記録するだけの
/// <see cref="IVisualLineTransformer"/>。
///
/// <para>
/// <c>TextView.BuildVisualLine()</c>は「要素の生成 → <c>VisualLine.RunTransformers()</c> →
/// <c>TextFormatter.FormatLine()</c>を折り返し行数ぶん回す整形ループ」という順で進む。
/// つまり<c>RunTransformers</c>の引数として渡ってくる
/// <see cref="ITextRunConstructionContext.VisualLine"/>（<c>public</c>なAPI）は、
/// <b>この直後に整形される行そのもの</b>である。整形器側（<see cref="WrapIndentTextFormatter"/>）は
/// 引数からは<c>VisualLine</c>を受け取れない（<c>ITextSource</c>と<c>TextParagraphProperties</c>
/// しか渡ってこない）ため、字下げ量の計算に必要な「行の要素（＝先頭の空白がどこまでか）」を
/// ここで橋渡しする。
/// </para>
///
/// <para>
/// 状態を<c>static</c>ではなくインスタンスに持つのは、エディタ（<c>TextView</c>）が複数あっても
/// 互いに干渉しないようにするため。<c>BuildVisualLine</c>はUIスレッド上で同期的に完結し、
/// 記録した値も同じ<c>BuildVisualLine</c>の中だけで消費されるため、単純なフィールドで足りる。
/// </para>
/// </summary>
internal sealed class WrapIndentVisualLineTracker : IVisualLineTransformer
{
    /// <summary>直近に<c>RunTransformers</c>が走ったVisualLine（＝これから整形される行）。</summary>
    public VisualLine? Current { get; private set; }

    public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
        => Current = context.VisualLine;
}

/// <summary>
/// 課題#72の本体。AvaloniaEditが握っている<see cref="TextFormatter"/>を包み、折り返しの
/// 2行目以降（<c>previousLineBreak != null</c>）に対して
/// <list type="number">
///   <item>折り返し幅を字下げ量ぶん狭めて内側の整形器を呼び</item>
///   <item>返ってきた<see cref="TextLine"/>をX方向へ字下げ量だけずらすデコレータ
///   （<see cref="WrapIndentTextLine"/>）で包んで返す</item>
/// </list>
/// という2段構えで、ぶら下げインデントを実現する。差し替えの手段とリフレクションが
/// 避けられない理由は<see cref="WrapIndentSupport"/>のクラスコメント参照。
///
/// <para>
/// 【なぜ幅を狭めるのか】 ①だけ・②だけでは不十分である。②だけだと右端が字下げ量ぶん
/// はみ出し（横スクロールが出る／文字が欠ける）、①だけだと折り返し位置は正しいのに
/// 描画が左端から始まって字下げにならない。両方やって初めて「1行目のインデント位置から
/// 始まり、右端で正しく折り返す」表示になる。
/// </para>
///
/// <para>
/// 【字下げ量の求め方】 AvaloniaEditの<c>TextView.BuildVisualLine()</c>（1105〜1123行）が
/// 本来やるはずだった計算をそのまま踏襲する。すなわち
/// <list type="bullet">
///   <item>行頭の空白が終わるVisual列を求め（<c>TextView.GetIndentationVisualColumn</c>と
///   同じ計算。あちらは<c>private static</c>なので呼べないが、
///   <see cref="VisualLineElement.IsWhitespace"/>が<c>public</c>なので同じものを書ける）</item>
///   <item>1行目の<see cref="TextLine.GetDistanceFromCharacterHit"/>でその列のX座標を測り</item>
///   <item><see cref="AvaloniaEdit.TextEditorOptions.WordWrapIndentation"/>を足し</item>
///   <item>エディタ幅の半分を超える字下げは捨てる（AvaloniaEdit本来の安全弁。深いインデントの
///   長い行で、折り返し後の1行あたりの文字数が極端に減るのを防ぐ）</item>
/// </list>
/// </para>
/// </summary>
internal sealed class WrapIndentTextFormatter : TextFormatter
{
    private readonly TextFormatter _inner;
    private readonly WrapIndentVisualLineTracker _tracker;
    private readonly TextView _textView;

    /// <summary>
    /// 【再入への番人】 <c>FormattedTextElement.PrepareText</c>（空白・タブ・改行記号の
    /// 可視化に使う小さなテキストの整形）は、<b>整形の最中に</b><c>FormatLine</c>を
    /// 再入呼び出しする。素通ししないと「段落の1行目」の状態（<see cref="_paragraphLine"/>・
    /// <see cref="_firstLine"/>・<see cref="_indent"/>）が、いま整形中の行とは無関係な
    /// 記号の整形で上書きされてしまう。設定「空白を表示する」をオンにした状態で
    /// 字下げが壊れる形で実際に再現した（WrapIndentTestsに回帰テストあり）。
    /// </summary>
    private bool _formatting;

    // いま整形中の段落（＝1つのVisualLine）の状態。
    private VisualLine? _paragraphLine;
    private TextLine? _firstLine;
    private double _indent;

    public WrapIndentTextFormatter(TextFormatter inner, WrapIndentVisualLineTracker tracker, TextView textView)
    {
        _inner = inner;
        _tracker = tracker;
        _textView = textView;
    }

    public override TextLine? FormatLine(
        ITextSource textSource, int firstTextSourceIndex, double paragraphWidth,
        TextParagraphProperties paragraphProperties, TextLineBreak? previousLineBreak = null)
    {
        // 再入中（＝空白記号などの整形）は、段落の状態に一切触れずそのまま内側へ渡す。
        if (_formatting)
        {
            return _inner.FormatLine(textSource, firstTextSourceIndex, paragraphWidth, paragraphProperties, previousLineBreak);
        }

        if (previousLineBreak is null)
        {
            // 段落の1行目。折り返しが無効なとき（TextWrapping.NoWrap＝paragraphWidthが無限大）も
            // 必ずここだけを通り、ぶら下げインデントの計算は一切走らない。
            // 折り返し無効時の追加コストが「このif分岐1つ」で済むのはこのため。
            TextLine? first;
            _formatting = true;
            try
            {
                first = _inner.FormatLine(textSource, firstTextSourceIndex, paragraphWidth, paragraphProperties, null);
            }
            finally
            {
                _formatting = false;
            }

            _paragraphLine = _tracker.Current;
            _firstLine = first;
            _indent = double.NaN; // まだ計算していないことを表す番兵。
            return first;
        }

        // 2行目以降。字下げ量は段落につき1回だけ計算すればよい（1行目の内容だけで決まるため）。
        if (double.IsNaN(_indent)) _indent = ComputeIndent(paragraphWidth);

        _formatting = true;
        try
        {
            if (_indent <= 0)
            {
                return _inner.FormatLine(textSource, firstTextSourceIndex, paragraphWidth, paragraphProperties, previousLineBreak);
            }

            // 折り返し幅を狭めてから整形し、結果をX方向へずらすデコレータで包む。
            // Math.Maxは、極端に細いウィンドウで幅が0以下にならないようにするための下限。
            var line = _inner.FormatLine(
                textSource, firstTextSourceIndex, Math.Max(1, paragraphWidth - _indent),
                paragraphProperties, previousLineBreak);
            return line is null ? null : new WrapIndentTextLine(line, _indent);
        }
        finally
        {
            _formatting = false;
        }
    }

    /// <summary>
    /// この段落の字下げ量（px）を求める。AvaloniaEdit <c>TextView.cs</c> 1105〜1123行と同じ計算。
    /// 求められない場合（VisualLineを捕まえられなかった等）は0を返し、素の挙動へ縮退する。
    /// </summary>
    private double ComputeIndent(double paragraphWidth)
    {
        var visualLine = _paragraphLine;
        var firstLine = _firstLine;
        if (visualLine is null || firstLine is null) return 0;

        // 【RTL・双方向テキストでは字下げしない（安全側の縮退）】
        // AvaloniaEditの段落は常に左横書き固定（VisualLineTextParagraphPropertiesが
        // FlowDirection.LeftToRight・TextAlignment.Leftを返す）だが、行の中にRTLの文字列が
        // あると、その範囲の字形は視覚的に並べ替えられる。このとき行頭の空白は必ずしも
        // 行の左端にあるとは限らず、GetDistanceFromCharacterHitが返すのは「その列の字形が
        // 実際に描かれるX座標」であって「行頭からのインデント幅」ではなくなる。
        // 実測（ヘブライ語の行、TextView幅312px、行頭に空白8個）でもインデント幅として
        // 246pxという明らかに誤った値が返り、たまたまAvaloniaEdit本来の安全弁
        // （幅の半分を超えたら捨てる）に救われている状態だった。幅の広いウィンドウでは
        // その安全弁もすり抜けて誤った字下げになるため、RTLを含む行では最初から
        // 字下げしない（＝従来どおりの表示）ことにする。
        if (ContainsRightToLeftRun(firstLine)) return 0;

        var options = _textView.Options;
        double indentation = 0;

        if (options.InheritWordWrapIndentation)
        {
            var indentVisualColumn = GetIndentationVisualColumn(visualLine);
            // 「1行目の途中までしかインデントが無い」ことを確認してから測る。行全体が空白の
            // 場合（indentVisualColumn >= firstLine.Length）に測ると、字下げ＝行の全幅になり
            // 意味を成さないため、AvaloniaEdit本来の実装と同じくその場合は0のままにする。
            if (indentVisualColumn > 0 && indentVisualColumn < firstLine.Length)
            {
                indentation = firstLine.GetDistanceFromCharacterHit(new CharacterHit(indentVisualColumn, 0));
            }
        }

        indentation += options.WordWrapIndentation;

        // エディタ幅の半分を超える字下げは捨てる（AvaloniaEdit本来の安全弁）。
        return indentation > 0 && indentation * 2 < paragraphWidth ? indentation : 0;
    }

    /// <summary>
    /// 行の中に右横書き（RTL）の字形範囲が含まれるか。<see cref="ShapedTextRun.BidiLevel"/>
    /// （<c>public</c>）が奇数の範囲＝RTL、というUnicode双方向アルゴリズムの規約をそのまま使う。
    /// 判定は段落につき1回（1行目のTextRunだけ）で済むため、費用は無視できる。
    /// </summary>
    private static bool ContainsRightToLeftRun(TextLine firstLine)
    {
        foreach (var run in firstLine.TextRuns)
        {
            if (run is ShapedTextRun shaped && (shaped.BidiLevel & 1) != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// 行頭の空白が終わるVisual列を求める。<c>TextView.GetIndentationVisualColumn</c>
    /// （<c>private static</c>のため呼べない）の写しで、<see cref="VisualLineElement.IsWhitespace"/>
    /// が<c>public</c>であることによって同じ計算を外から書ける。
    /// タブ・全角空白のような「1文字が複数列を占める」要素も、列単位で問い合わせる
    /// この形なら正しく数えられる。
    /// </summary>
    private static int GetIndentationVisualColumn(VisualLine visualLine)
    {
        if (visualLine.Elements.Count == 0) return 0;

        var column = 0;
        var elementIndex = 0;
        var element = visualLine.Elements[elementIndex];
        while (element.IsWhitespace(column))
        {
            column++;
            if (column == element.VisualColumn + element.VisualLength)
            {
                elementIndex++;
                if (elementIndex == visualLine.Elements.Count) break;
                element = visualLine.Elements[elementIndex];
            }
        }
        return column;
    }
}
