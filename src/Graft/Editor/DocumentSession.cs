using System.IO;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Graft.Core;

namespace Graft.Editor;

/// <summary>
/// 1ファイルの編集セッション（4.5節）。<see cref="Graft.Core.TextShape"/>（エンコーディング・
/// 改行・末尾改行）を保持し、AvaloniaEditの<see cref="TextDocument"/>・<see cref="UndoStack"/>と
/// 連携して未保存状態を追跡する。実I/Oは常にCoreの<see cref="FileTextIO"/>へ委譲し、
/// 本クラス自身がFile.WriteAllText等で直接ファイルへ書き込むことはない（附録A）。
/// v2.0のWPF版（AvalonEdit）からの移植。<see cref="TextDocument"/>・<see cref="UndoStack"/>の
/// APIはAvaloniaEditでも同名同形のため、大部分は名前空間の差し替えのみで移植できるが、
/// 1点だけAvaloniaEdit固有の吸収が必要（4.1節「APIの差だけを吸収する」の対象）。
/// AvaloniaEditの<see cref="TextDocument"/>はv2.0のWPF版と異なり、生成したスレッド以外からの
/// アクセスで例外を送出する（<c>VerifyAccess</c>によるスレッド固定。生成時に暗黙的に
/// 所有スレッドが確定する）。<see cref="FileTextIO"/>によるI/Oは<c>ConfigureAwait(false)</c>で
/// スレッドプール上へ移るため、続けて<see cref="TextDocument"/>を生成・変更する箇所だけは
/// <see cref="Dispatcher.UIThread"/>へ明示的に切り替えて実行する
/// （<see cref="OpenAsync"/>・<see cref="SaveAsync"/>・<see cref="ReloadAsync"/>参照）。
///
/// 【なぜUIスレッドへ戻す直前に<c>Dispatcher.UIThread</c>を毎回読み直さないか】
/// <c>Dispatcher.UIThread</c>は遅延生成のうえスレッド非安全な静的プロパティで、
/// まだ誰も読んでいない状態で最初に読んだスレッドの<c>IDispatcherImpl</c>を
/// キャッシュしてしまう。headlessテストではテストごとに1回、セッションのディスパッチャ
/// スレッドが<c>Dispatcher.ResetForUnitTests()</c>でこのキャッシュを空にしてから
/// headless用の実装を登録し直しており、この一瞬の窓の間に別スレッド（ここでは
/// <c>ConfigureAwait(false)</c>で移ったスレッドプール上のスレッド）が<c>Dispatcher.UIThread</c>を
/// 読むと、<c>IControlledDispatcherImpl</c>を実装しない壊れたインスタンスがキャッシュされ、
/// 以降そのテストプロセス全体で<c>PlatformNotSupportedException</c>が出るようになる
/// （実際にCIで間欠的に観測した）。そのため<see cref="OpenAsync"/>・<see cref="SaveAsync"/>・
/// <see cref="ReloadAsync"/>はいずれも、まだ呼び出し元のスレッド（UIスレッド）にいる
/// メソッド冒頭で<c>Dispatcher.UIThread</c>を一度だけローカル変数へ捕捉し、
/// <c>ConfigureAwait(false)</c>後はその捕捉済みインスタンスだけを使う。
/// </summary>
public sealed class DocumentSession : IDisposable
{
    // バイナリ判定（4.5節E703）はファイル全体を読まず、先頭のみをサンプリングする。
    private const int BinarySniffBytes = 8000;

    // サンプル中に占める「テキストとしては現れにくい制御文字」の割合がこれを超えたら
    // バイナリと判定する。タブ(0x09)・LF(0x0A)・CR(0x0D)は文書中に普通に出現するため除外し、
    // それ以外の0x00-0x08・0x0E-0x1Fの制御文字群を対象にする。NULバイト(0x00)は1バイトでも
    // 見つかった時点でバイナリ確定として扱う（テキストファイルに出現することはまず無いため）。
    private const double BinaryControlRatioThreshold = 0.3;

    /// <summary>
    /// 課題3（再設計）: 1行がこの文字数を超えたら「極端に長い行」とみなす
    /// （<see cref="HasExtremelyLongLine"/>）。
    ///
    /// 旧仕様では、この判定が立つと構文強調・括弧の対応付け・折りたたみ・折り返しを
    /// ファイル全体で一括無効化していた。しかし「1行でも極端に長い行があると、残り99%の
    /// 通常行まで色が消える」のは利用者から見て過剰であり、エディタとして致命的という
    /// 指摘を受けた。実測の結果（詳細は各機能の実装箇所のコメント参照）、無効化が必要
    /// だったのは実質「その極端に長い行1行だけを対象にした処理」であり、ファイル全体を
    /// 巻き込む必要は無かった。
    /// - 構文強調（<see cref="SyntaxHighlightBridge.ColorizeLine"/>）: 元々1行ずつ処理して
    ///   おり、可視行だけが対象。このフィールドを超える行だけ強調を打ち切る（VS Codeの
    ///   既定のトークナイズ上限=20,000文字にならい、同じ値をしきい値とする）。
    /// - 括弧の対応付け・自動閉じ（<see cref="BracketSupport.IsInsideStringOrComment"/>）:
    ///   このフィールドを超える行では言語認識（文字列/コメント内判定）を打ち切ってO(1)化する。
    ///   実測: 旧方式のまま1行10万文字に適用すると最悪ケースで数十秒〜（n=8,000で513ms、
    ///   O(n^2)で外挿すると n=100,000 で概算80秒）かかるのに対し、この打ち切りを入れると
    ///   4.3ms程度で完了する。
    /// - 折りたたみ（<see cref="FoldingSupport"/>）: 実測したところ全体再計算は1行10万文字の
    ///   ファイルで1ms未満、3万行＋1行10万文字の混在ファイルでも最大19ms程度であり、300msの
    ///   デバウンス予算に対して十分小さいため、無効化は不要と判断した（打ち切りなしで常に
    ///   利用者の設定に従う）。
    /// - 折り返し: 既定値の変更に伴い、強制無効化ではなく利用者の設定にそのまま従う。
    ///   ただし1行10万文字＋折り返し有効では書式計算が数百ms→1.5秒前後に悪化する実測が
    ///   あるため、通知バーからこのファイルに限って折り返しを切れる逃げ道を用意する
    ///   （<see cref="Graft.ViewModels.EditorTabViewModel.WordWrapDisabledForTab"/>）。
    /// </summary>
    public const int LongLineThreshold = 20_000;

    private bool _wasModified;
    private bool _disposed;

    private DocumentSession(
        string fullPath, string relativePath, TextDocument document, TextShape shape, bool hasExtremelyLongLine,
        int initialTextLength, int initialLineCount)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        FileName = Path.GetFileName(fullPath);
        Document = document;
        Shape = shape;
        HasExtremelyLongLine = hasExtremelyLongLine;
        InitialTextLength = initialTextLength;
        InitialLineCount = initialLineCount;
        Document.UndoStack.PropertyChanged += OnUndoStackPropertyChanged;
    }

    /// <summary>ファイルの絶対パス。エクスプローラでのリネームに追従して<see cref="UpdatePath"/>で更新されうる。</summary>
    public string FullPath { get; private set; }

    /// <summary>プロジェクトルートからの相対パス（表示用、区切りは常に '/')。</summary>
    public string RelativePath { get; private set; }

    /// <summary>ファイル名のみ。</summary>
    public string FileName { get; private set; }

    /// <summary>AvaloniaEditの編集対象文書。アンドゥスタックもこれが保持する。</summary>
    public TextDocument Document { get; }

    /// <summary>読み込み時に判定したエンコーディング・改行・末尾改行の見た目。</summary>
    public TextShape Shape { get; private set; }

    /// <summary>
    /// 課題3: <see cref="LongLineThreshold"/>を超える行を含むかどうか。<see cref="OpenAsync"/>時に
    /// 一度だけ判定する。trueの場合、エディタ側（EditorPane）はその極端に長い行に限って
    /// 構文強調・括弧の言語認識を打ち切り（ファイル全体は対象外）、ステータスバーで
    /// その旨を通知する（<see cref="LongLineThreshold"/>のコメント参照）。
    /// </summary>
    public bool HasExtremelyLongLine { get; }

    /// <summary>
    /// Markdownプレビュー機能: 読み込み時点の文字数。<see cref="EditorTabViewModel"/>の
    /// コンストラクタがプレビュー可否（<see cref="Graft.Core.MarkdownPreviewSizeGuard"/>）を
    /// 判定するために使う。
    ///
    /// 【なぜDocument.TextLengthを直接使わないか】AvaloniaEditの<see cref="TextDocument"/>は
    /// 生成したスレッド以外からのアクセスで例外を送出する（クラス冒頭のコメント参照）。
    /// <see cref="EditorTabManager.OpenAsync"/>は<c>ConfigureAwait(false)</c>で継続するため、
    /// <see cref="EditorTabViewModel"/>のコンストラクタがUIスレッド以外で実行される可能性があり、
    /// そこで<c>Document.TextLength</c>へ触れると<see cref="InvalidOperationException"/>になる
    /// （実際にヘッドレステストで再現した）。<see cref="OpenAsync"/>内、まだ<see cref="TextDocument"/>を
    /// 構築する前のプレーンな文字列（スレッド非依存）から計算した値をここに保持しておくことで、
    /// この問題を避ける。
    /// </summary>
    public int InitialTextLength { get; }

    /// <summary>Markdownプレビュー機能: 読み込み時点の行数。<see cref="InitialTextLength"/>と同じ理由でここに保持する。</summary>
    public int InitialLineCount { get; }

    /// <summary>
    /// 未保存の変更があるかどうか。AvaloniaEditのアンドゥスタックが持つ
    /// 「元ファイルの状態まで戻ったか」を表す<see cref="UndoStack.IsOriginalFile"/>の否定で
    /// 判定するため、アンドゥで編集前に戻せば自動的に未保存扱いが解除される。
    /// </summary>
    public bool IsModified => !Document.UndoStack.IsOriginalFile;

    /// <summary><see cref="IsModified"/>が変化するたびに発火する。</summary>
    public event EventHandler? ModifiedChanged;

    /// <summary>
    /// ファイルを読み込みセッションを開く。バイナリ判定に外れた場合はE703、
    /// 読み込み失敗はCoreがそのまま返すコード（E204等）で失敗を返す。
    /// </summary>
    public static async Task<GraftResult<DocumentSession>> OpenAsync(
        string fullPath, string projectRoot, CancellationToken ct = default)
    {
        // クラス冒頭のコメント参照: まだUIスレッドにいるこの時点で捕捉しておく
        // （このあとConfigureAwait(false)でスレッドプールへ移るため）。
        var ui = Dispatcher.UIThread;

        if (!File.Exists(LongPath.Extended(fullPath)))
        {
            return GraftResult<DocumentSession>.Fail(ErrorCode.E204, "ファイルが見つかりません", path: fullPath);
        }

        if (await LooksBinaryAsync(fullPath, ct).ConfigureAwait(false))
        {
            return GraftResult<DocumentSession>.Fail(ErrorCode.E703, "バイナリまたは未対応形式のため開けません", path: fullPath);
        }

        var read = await FileTextIO.ReadAsync(fullPath, ct).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return GraftResult<DocumentSession>.Fail(read.Issues);
        }

        var (text, shape) = read.Value;
        var relativePath = ComputeRelativePath(fullPath, projectRoot);

        // 課題3: 読み込んだ全文はここで既にメモリ上にあるため、追加のI/Oなしで判定できる。
        // ワーカースレッド上（ConfigureAwait(false)のまま）で行い、UIスレッドの待ち時間を増やさない。
        var hasExtremelyLongLine = TextNormalizer.HasLineLongerThan(text, LongLineThreshold);

        // Markdownプレビュー機能: InitialTextLength/InitialLineCountの由来（プレーンな文字列の
        // うちに計算する理由）はプロパティのコメント参照。
        var initialTextLength = text.Length;
        var initialLineCount = TextNormalizer.SplitLines(text).Count;

        // TextDocumentの生成はUIスレッドへ切り替えてから行う（クラス冒頭のコメント参照）。
        // DispatcherOperationはConfigureAwaitを持たないため素直にawaitする。
        var session = await ui.InvokeAsync(
            () => new DocumentSession(
                fullPath, relativePath, new TextDocument(text), shape, hasExtremelyLongLine,
                initialTextLength, initialLineCount));
        return GraftResult<DocumentSession>.Ok(session, read.Issues);
    }

    /// <summary>
    /// Shapeどおりにエンコーディング・BOM・改行・末尾改行を保持して保存する。
    /// 成功時はアンドゥスタックへ「ここが保存済み状態」を記録し、以降<see cref="IsModified"/>は
    /// falseに戻る（さらに編集してから保存前の状態までアンドゥすれば再びfalseになる）。
    /// </summary>
    public async Task<GraftResult<bool>> SaveAsync(CancellationToken ct = default)
    {
        // クラス冒頭のコメント参照: まだUIスレッドにいるこの時点で捕捉しておく。
        var ui = Dispatcher.UIThread;

        var result = await FileTextIO.WriteAsync(FullPath, Document.Text, Shape, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            // FileTextIO.WriteAsyncはCore内部の書き込み失敗としてE402を返すが、エディタからの
            // 保存失敗は仕様書17章のE701として表示する必要があるため、コードのみE701へ包み直す
            // （元のDetail・行番号・パス・深刻度はそのまま保持する）。
            return GraftResult<bool>.Fail(result.Issues.Select(
                i => GraftIssue.Of(ErrorCode.E701, i.Detail, i.LineNumber, i.Path, i.Severity)));
        }

        // UndoStackの更新はTextDocumentの所有スレッド（UIスレッド）で行う必要がある
        // （クラス冒頭のコメント参照）。
        await ui.InvokeAsync(() => Document.UndoStack.MarkAsOriginalFile());
        return result;
    }

    /// <summary>
    /// ディスク上の内容で再読込する。呼び出し側（4.6/4.8）が未保存変更との競合有無を
    /// 判断した後に呼ぶことを想定し、本メソッド自体は確認を行わない。
    ///
    /// ただし、ディスク上の内容が編集中の内容と同一の場合は文書へ触れない。
    /// 自分で保存した直後もファイル監視（4.6）は変更として通知してくるため、
    /// 無条件に差し替えるとカーソル位置とアンドゥ履歴が保存のたびに失われる
    /// （実機で「保存するとカーソルが末尾へ飛ぶ」不具合として現れた）。
    /// 内容が同じなら再読込の必要そのものが無いので、誰が書いたかを問わず何もしない。
    /// </summary>
    public async Task<GraftResult<bool>> ReloadAsync(CancellationToken ct = default)
    {
        // クラス冒頭のコメント参照: まだUIスレッドにいるこの時点で捕捉しておく。
        var ui = Dispatcher.UIThread;

        var read = await FileTextIO.ReadAsync(FullPath, ct).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return GraftResult<bool>.Fail(read.Issues);
        }

        var (text, shape) = read.Value;

        // Document/UndoStackの更新はTextDocumentの所有スレッド（UIスレッド）で行う
        // 必要がある（クラス冒頭のコメント参照）。Document.Textの読み取りも同様。
        await ui.InvokeAsync(() =>
        {
            if (Shape == shape && string.Equals(Document.Text, text, StringComparison.Ordinal))
            {
                return;
            }

            Shape = shape;
            Document.Text = text;
            Document.UndoStack.ClearAll();
            Document.UndoStack.MarkAsOriginalFile();
        });
        return GraftResult<bool>.Ok(true, read.Issues);
    }

    /// <summary>
    /// 現在の文書内容から優勢なインデント（タブ/スペース・幅）を検出する（15章 detectIndent）。
    /// スペースの幅は、行頭空白の出現長のうち最小のものを候補とする簡易法（GCD等の厳密な
    /// 計算は行わない）。判定できない場合は<paramref name="fallbackWidth"/>を返す。
    /// </summary>
    public (bool UseTabs, int Width) DetectIndent(int fallbackWidth)
    {
        // 18章: 10万行のファイルでも開く操作が遅延しないよう、判定は先頭の一定行数に限る。
        // インデントの流儀はファイル内で一貫しているのが普通で、全行を走査する必要はない。
        const int SampleLines = 2000;
        var lines = ReadLeadingLines(SampleLines);
        if (TextNormalizer.DominantIndentChar(lines) == '\t')
        {
            return (true, fallbackWidth);
        }

        var counts = new Dictionary<int, int>();
        foreach (var line in lines)
        {
            var length = TextNormalizer.LeadingWhitespace(line).Length;
            if (length > 0 && length <= 16)
            {
                counts[length] = counts.GetValueOrDefault(length) + 1;
            }
        }

        return (false, counts.Count == 0 ? fallbackWidth : counts.Keys.Min());
    }

    /// <summary>
    /// 文書の先頭から指定行数までを取り出す。<c>Document.Text</c> は全文を1本の文字列として
    /// 確保するため、10万行規模のファイルでは行単位で読むほうが速く、割り当ても小さい。
    /// </summary>
    private IReadOnlyList<string> ReadLeadingLines(int maxLines)
    {
        var count = Math.Min(maxLines, Document.LineCount);
        var lines = new List<string>(count);
        for (var i = 1; i <= count; i++)
        {
            var line = Document.GetLineByNumber(i);
            lines.Add(Document.GetText(line.Offset, line.Length));
        }
        return lines;
    }

    /// <summary>
    /// エクスプローラでのリネーム・移動に追従してパス表示を更新する（4.2/4.8）。中身は変更しない。
    /// </summary>
    public void UpdatePath(string newFullPath, string projectRoot)
    {
        FullPath = newFullPath;
        FileName = Path.GetFileName(newFullPath);
        RelativePath = ComputeRelativePath(newFullPath, projectRoot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Document.UndoStack.PropertyChanged -= OnUndoStackPropertyChanged;
    }

    private void OnUndoStackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UndoStack.IsOriginalFile)) return;

        var modified = IsModified;
        if (modified == _wasModified) return;
        _wasModified = modified;
        ModifiedChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string ComputeRelativePath(string fullPath, string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot))
        {
            return fullPath;
        }

        try
        {
            return Path.GetRelativePath(projectRoot, fullPath).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return fullPath;
        }
    }

    /// <summary>
    /// バイナリ判定（4.5節E703）。先頭<see cref="BinarySniffBytes"/>バイトのみを読み、
    /// NULバイトが1つでもあれば即バイナリ、それ以外はタブ・LF・CRを除く制御文字の比率が
    /// <see cref="BinaryControlRatioThreshold"/>を超えたらバイナリとみなす。ファイル全体を
    /// 読まないため巨大なバイナリファイルでも高速に判定できる。
    /// internalなのは、ファイルからのパッチ解析（<see cref="Graft.ViewModels.MainViewModel"/>の
    /// MainViewModel.FileParse.cs）が同じ判定をD&D・ファイル選択の両経路で再利用するため。
    /// </summary>
    internal static async Task<bool> LooksBinaryAsync(string fullPath, CancellationToken ct)
    {
        var ioPath = LongPath.Extended(fullPath);
        await using var stream = new FileStream(
            ioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
        var length = (int)Math.Min(BinarySniffBytes, stream.Length);
        if (length == 0) return false;

        var buffer = new byte[length];
        var read = await stream.ReadAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
        if (read == 0) return false;

        var suspicious = 0;
        for (var i = 0; i < read; i++)
        {
            var b = buffer[i];
            if (b == 0) return true;
            var isControlNonText = b < 0x09 || (b > 0x0D && b < 0x20);
            if (isControlNonText) suspicious++;
        }

        return (double)suspicious / read > BinaryControlRatioThreshold;
    }
}
