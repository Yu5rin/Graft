using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// コンテキスト収集ViewModelのうち、リボン（customUI。仕様書_ブックの情報と依頼の観点 15章）を持つ部分。
/// 「ブックの情報」（<c>ContextCollectViewModel.WorkbookInfo.cs</c>）と同じ作りで、別の行・別の状態にしてある
/// （3 状態の記憶が独立で、読む先が Excel（COM）ではなくブックのファイルのため）。
/// <para>
/// 【取得結果はメモリにだけ持つ】 <c>label</c> などに業務名が入りうるので、ディスクへは書かない（<c>projects.json</c> に保存するのは
/// 「出し方」の選択だけ）。「構成だけ」「出さない」へ下げたら、XML の全文は直ちにメモリから捨てる（構成だけは残す）。
/// </para>
/// <para>
/// 【読めなくても止めない】 ファイルが無い・壊れている・ロックされている・customUI が無い、のいずれでも収集は続け、
/// 理由を行の 1 行に出す（customUI が無いときは警告にしない。リボンを足していないブックは普通にある）。
/// 前回成功した分は使い回さない（ファイルを毎回読み直すだけの軽い処理で、古い XML を「今の」ものとして渡すより、無いほうが安全なため）。
/// </para>
/// </summary>
public sealed partial class ContextCollectViewModel
{
    private VbaRibbonSnapshot? _ribbonSnapshot;

    // 【取得の世代】 ブックの情報の取得と同じ理由（待っている間に、利用者が出し方を変えて次の取得が走りうる）。
    private int _ribbonGeneration;

    // 【依頼文の文のキャッシュ】 BuildRibbonText は、トークン概算（3 状態のクリックのたび、UI スレッド）・依頼文コピー・プレビューから
    // 何度も呼ばれる。整形は取得結果が同じなら同じ文になるので、「取得結果・出し方・XML の上限」が前回と同じ間は作り直さない。
    // 取得結果は変わるたびに別のオブジェクトになる（WithoutXml も新しく作る）ので、参照の一致で同じかを見分けられる。
    private VbaRibbonSnapshot? _ribbonTextSnapshot;
    private VbaWorkbookInfoLevel _ribbonTextLevel;
    private int _ribbonTextMaxChars;
    private string _ribbonTextCache = string.Empty;

    /// <summary>リボン（customUI）の行（コンテキスト収集の窓。VBA プロジェクトのときだけ表示される）。</summary>
    public VbaRibbonRowViewModel Ribbon { get; private set; } = null!;

    /// <summary>直近のリボンの取得で出た警告（読めなかった理由）。無ければ null。customUI が無いのは警告にしない。</summary>
    public string? RibbonWarning { get; private set; }

    private void InitializeRibbon(Project project, VbaWorkbookInfoSource? source)
    {
        var cycle = new AsyncRelayCommand(CycleRibbonLevelAsync, context: "リボンの出し方の切り替え");
        Ribbon = new VbaRibbonRowViewModel(cycle);
        // 行の初期値は、前回までに保存した選択（VbaLink）。VBA プロジェクトでなければ既定のまま、行は出さない。
        Ribbon.Level = VbaWorkbookInfoLevels.Parse(project.Vba?.RibbonState);
        Ribbon.IsVisible = source?.FetchRibbonAsync is not null && source.IsAvailable();
    }

    /// <summary>
    /// リボンをブックのファイルから読む。取る契機はブックの情報（<see cref="FetchWorkbookInfoAsync"/>）と同じ
    /// （コンテキスト収集を開いたとき・依頼文コピーのとき）。取れなくても例外にせず、行に理由を出して続ける。
    /// <para>
    /// 取らない条件（いずれもファイルを開かない）: VBA プロジェクトでない・機能が無効・この環境で使えない、
    /// 設定 <c>Vba.CollectWorkbookInfo</c> が false、行の状態が「出さない」。
    /// </para>
    /// </summary>
    public async Task FetchRibbonAsync(CancellationToken ct = default)
    {
        var generation = ++_ribbonGeneration;
        RibbonWarning = null;
        EnsureCurrentWorkbook();

        var source = _workbookInfoSource;
        var available = source?.FetchRibbonAsync is not null && source.IsAvailable();
        Ribbon.IsVisible = available;
        if (!available)
        {
            ClearRibbon(string.Empty);
            return;
        }

        if (Ribbon.Level == VbaWorkbookInfoLevel.Hidden)
        {
            ClearRibbon("出さない設定のため、ブックのファイルを読んでいません。");
            return;
        }

        var requestPath = source!.CurrentWorkbookFullName?.Invoke();
        VbaRibbonSnapshot? snapshot;
        try
        {
            snapshot = await source.FetchRibbonAsync!(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 【取り消されたら、取得結果を残さない】 取り消された取得のあとに古い結果（前回の構成・XML の全文）が残ると、
            // 取り消した操作がそのまま出力へ進んだときに、古い内容が「今の」ものとして出てしまう。
            // 新しい取得がもう始まっていれば（世代が違えば）、それが持つ結果を消さない。
            if (generation == _ribbonGeneration) ClearRibbon("取得を中断しました。");
            throw;
        }
        catch (Exception ex)
        {
            // 想定外の失敗でも、依頼文の作成は止めない。記録だけ残して、リボンなしで続ける。
            SafeHandler.OnUnexpected?.Invoke("リボン（customUI）の取得", ex);
            RibbonWarning = "リボン（customUI）の取得中に想定外の問題が起きました。リボンなしで続けます。";
            ClearRibbon("取得できませんでした。");
            PublishRibbonResult();
            return;
        }

        // 待っている間により新しい取得が始まっていたら、その結果に任せる。
        if (generation != _ribbonGeneration) return;

        // 【待っている間に「出さない」へ切り替えられていたら、結果を捨てる】 「出さない」は「何も持たない」状態（仕様 15.5・15.7）。
        // 世代は CycleRibbonLevelAsync でも進めているが、ここでも見る（どちらかが外れても、取得済みの内容が行に残らないように）。
        if (Ribbon.Level == VbaWorkbookInfoLevel.Hidden)
        {
            ClearRibbon("出さない設定のため、ブックのファイルを読んでいません。");
            return;
        }

        // 待っている間に別のブックへ結び直されていたら、読んだ内容は前のブックのもの。格納しない。
        if (!IsSamePathOrUnknown(requestPath, source.CurrentWorkbookFullName?.Invoke()))
        {
            EnsureCurrentWorkbook();
            return;
        }

        if (snapshot is null)
        {
            // 設定でブックの情報の取得をオフにしている。前回の結果も使わない（オフにしたのに古い内容が出続けるのは意図に反する）。
            ClearRibbon("設定（VBA 連携）でブックの情報の取得をオフにしているため、リボンも読みません。");
            PublishRibbonResult();
            return;
        }

        // 【全文はメモリに持つのも「内容も出す」のときだけ】 構成だけなら XML の全文は使わないので、読んだ直後に捨てる。
        _ribbonSnapshot = Ribbon.Level == VbaWorkbookInfoLevel.Full ? snapshot : WithoutXml(snapshot);
        Ribbon.IsFromMirror = snapshot.IsFromMirror;
        Ribbon.StatusText = snapshot.Describe();
        Ribbon.ShowCallbacks(snapshot.HasContent ? snapshot.Callbacks : null);

        // 読めなかったことだけ警告にする。customUI が無い・古い形式（.xls）は、読めなかったのではなく「対象が無い」ので警告にしない。
        if (snapshot.Status is VbaRibbonReadStatus.FileNotFound or VbaRibbonReadStatus.Locked
            or VbaRibbonReadStatus.Corrupt or VbaRibbonReadStatus.PartsUnreadable or VbaRibbonReadStatus.Encrypted)
        {
            RibbonWarning = $"リボン（customUI）を読めませんでした。リボンなしで続けます（{snapshot.Reason}）";
        }
        else if (snapshot.Status == VbaRibbonReadStatus.Ok && snapshot.Reason is not null)
        {
            RibbonWarning = $"リボン（customUI）の一部を読めませんでした（{snapshot.Reason}）";
        }

        PublishRibbonResult();
    }

    private void ClearRibbon(string status)
    {
        _ribbonSnapshot = null;
        Ribbon.IsFromMirror = false;
        _ribbonTextSnapshot = null;
        _ribbonTextCache = string.Empty;
        Ribbon.StatusText = status;
        Ribbon.ShowCallbacks(null);
    }

    private void PublishRibbonResult()
    {
        PublishVbaWarningToStatus();
        WorkbookInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>取得結果が別のブックのものなら捨てる（<c>EnsureCurrentWorkbook</c> から呼ぶ）。</summary>
    private void DropRibbonIfOtherWorkbook(string currentWorkbook)
    {
        if (_ribbonSnapshot is null || VbaRotNames.SamePath(_ribbonSnapshot.WorkbookPath, currentWorkbook)) return;
        ClearRibbon("ブックが結び直されたため、前のブックのリボンの情報を捨てました。");
    }

    private static VbaRibbonSnapshot WithoutXml(VbaRibbonSnapshot snapshot)
        => snapshot with
        {
            Parts = snapshot.Parts
                .Select(part => part with { Raw = part.Raw with { Xml = null } })
                .ToArray(),
        };

    /// <summary>
    /// 依頼文・収集の出力に載せる、リボンの文。載せないときは空。
    /// 載せない条件: 行が出ていない（VBA でない・機能が無効・環境が非対応）、「出さない」、
    /// customUI が無い・読めなかった、設定でブックの情報の取得をオフにしている。
    /// </summary>
    public string BuildRibbonText()
    {
        EnsureCurrentWorkbook();
        if (_ribbonSnapshot is null || !Ribbon.IsVisible || Ribbon.Level == VbaWorkbookInfoLevel.Hidden) return string.Empty;

        // 【出力の直前に、設定と環境の「いま」を引き直す】 ブックの情報（<see cref="BuildWorkbookInfoText"/>）と同じ理由。
        var source = _workbookInfoSource;
        if (source is null || !source.IsAvailable() || source.IsCollecting?.Invoke() == false) return string.Empty;

        var maxChars = source.RibbonMaxChars?.Invoke() ?? _settings.Vba.RibbonMaxChars;
        if (!ReferenceEquals(_ribbonTextSnapshot, _ribbonSnapshot) || _ribbonTextLevel != Ribbon.Level || _ribbonTextMaxChars != maxChars)
        {
            _ribbonTextCache = VbaRibbonFormatter.Format(_ribbonSnapshot, Ribbon.Level, maxChars);
            _ribbonTextSnapshot = _ribbonSnapshot;
            _ribbonTextLevel = Ribbon.Level;
            _ribbonTextMaxChars = maxChars;
        }

        return _ribbonTextCache;
    }

    /// <summary>
    /// 「# 対象ファイル」に内容つきで載るファイルに合わせて、リボンの文を作り直す関数（仕様書_ブックの情報と依頼の観点 16.14。
    /// <see cref="ContextRequest.RibbonTextFor"/> に渡す）。ミラーから読んだ取得結果を「内容も出す」で出すときだけ作り、それ以外
    /// （ブックから読んだ・構成だけ・出さない・リボンが無い）は null（<see cref="BuildRibbonText"/> の文がそのまま使われ、出力は従来と
    /// 1 バイトも変わらない）。
    /// <para>
    /// 【不変の値だけを閉じ込める】 収集はスレッドプールで走る（<c>CollectAsync</c>）。ViewModel の状態を後から読むと、利用者の操作と
    /// 競合しうるので、いまの取得結果・出し方・上限・文を写し取って閉じ込める。対象ファイルにリボンのファイルが無ければ、作り直さずに
    /// 同じ文を返す（整形を繰り返さない）。
    /// </para>
    /// </summary>
    public Func<IReadOnlySet<string>, string>? BuildRibbonTextFor()
    {
        var text = BuildRibbonText();
        if (text.Length == 0 || _ribbonSnapshot is not { IsFromMirror: true } snapshot || Ribbon.Level != VbaWorkbookInfoLevel.Full)
        {
            return null;
        }

        var level = Ribbon.Level;
        var maxChars = _ribbonTextMaxChars;
        var paths = snapshot.Parts.Select(p => p.Raw.PartName).ToArray();
        return files => paths.Any(files.Contains) ? VbaRibbonFormatter.Format(snapshot, level, maxChars, files) : text;
    }

    /// <summary>
    /// 破棄のとき、リボンの取得結果を手放す（<see cref="Dispose"/> から呼ぶ）。世代も進めて、破棄のあとに終わった取得が
    /// 結果（XML の全文を含みうる）を書き戻さないようにする。
    /// </summary>
    private void ReleaseRibbon()
    {
        _ribbonGeneration++;
        _ribbonSnapshot = null;
        _ribbonTextSnapshot = null;
        _ribbonTextCache = string.Empty;
    }

    /// <summary>
    /// 行のクリック: 内容も出す → 構成だけ → 出さない → 内容も出す、と巡る。選んだ状態は保存する。
    /// 構成だけ・出さないへ下げたら、XML の全文を直ちにメモリから捨てる。「出さない」から戻したときは読み直す
    /// （読んでいなかったので結果が無い）。
    /// </summary>
    public async Task CycleRibbonLevelAsync()
    {
        var before = Ribbon.Level;
        var next = VbaWorkbookInfoLevels.Next(before);
        Ribbon.Level = next;
        if (!await PersistWorkbookInfoAsync(link => link with { RibbonState = next.ToString() }).ConfigureAwait(true))
        {
            // 保存できなかった。保存してある出し方（変わっていない）に、画面とメモリの値を合わせる。
            Ribbon.Level = before;
            AfterWorkbookInfoSelectionChanged();
            return;
        }

        if (next == VbaWorkbookInfoLevel.Hidden)
        {
            // 取得の最中なら、その結果は要らない。世代を進めて、戻ってきた取得が結果を入れないようにする。
            _ribbonGeneration++;
            ClearRibbon("出さない設定のため、ブックのファイルを読んでいません。");
        }
        else if (next == VbaWorkbookInfoLevel.StructureOnly && _ribbonSnapshot is not null)
        {
            _ribbonSnapshot = WithoutXml(_ribbonSnapshot);
        }

        if (before == VbaWorkbookInfoLevel.Hidden)
        {
            try
            {
                await FetchRibbonAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // 窓を閉じるなどで中断された。
            }
        }

        // プレビュー欄の古い出力を捨て、トークンの概算と依頼文の再計算を促す。
        AfterWorkbookInfoSelectionChanged();
    }
}
