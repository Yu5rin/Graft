using System.IO;
using System.Text;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;

namespace Graft.Features;

/// <summary>
/// ミラーのリボンのファイル 1 つ（<c>CustomUI/customUI14.xml</c>・<c>CustomUI/customUI.xml</c>）の読み取り結果。
/// </summary>
/// <param name="Format">形式。</param>
/// <param name="RelativePath">ミラーの正規の相対パス（<c>CustomUI/customUI14.xml</c>）。</param>
/// <param name="FullPath">実際のファイルのフルパス（大文字小文字は、ディスク上の綴りのまま）。</param>
/// <param name="File">本文か、読めなかった理由。</param>
/// <param name="Shape">読めたときの文字コード・改行の形（取込で書き戻すときに保つ）。読めなかったときは null。</param>
/// <param name="Modified">ファイルの最終更新日時（読めたとき）。</param>
public sealed record VbaRibbonMirrorEntry(
    VbaRibbonFormat Format, string RelativePath, string FullPath, VbaRibbonMirrorFile File, TextShape? Shape, DateTimeOffset? Modified);

/// <summary>
/// コンテキスト収集の「リボン（customUI）」の行が読む、ミラーのリボンのファイル 1 つ（仕様書_ブックの情報と依頼の観点 16.14）。
/// </summary>
/// <param name="Format">形式。</param>
/// <param name="RelativePath">ミラーの正規の相対パス。依頼文の見出しにそのまま使う（AI が search/replace の対象のパスを書けるように）。</param>
/// <param name="Text">本文。</param>
/// <param name="Modified">ファイルの最終更新日時。</param>
/// <param name="Unpushed">ブックへまだ反映していないか（同期基準と本文が違う、または基準が無い）。</param>
public sealed record VbaRibbonContextFile(
    VbaRibbonFormat Format, string RelativePath, string Text, DateTimeOffset? Modified, bool Unpushed);

/// <summary>ミラーと同期基準だけから分かる、リボンの状態（ブックは読まない。バッジ用）。</summary>
/// <param name="Unpushed">ブックへまだ反映していない変更があるか（「リボン未反映」）。</param>
/// <param name="DeletePending">削除保留（記録があるのにミラーのファイルが無い）のミラーのパス。</param>
/// <param name="NeedsReviewCount">要確認（書き換えに失敗して戻せなかった）の形式の数。</param>
public sealed record VbaRibbonLocalStatus(bool Unpushed, IReadOnlyList<string> DeletePending, int NeedsReviewCount)
{
    /// <summary>リボンの同期をしていない・何も無い。</summary>
    public static VbaRibbonLocalStatus None { get; } = new(false, Array.Empty<string>(), 0);
}

/// <summary>
/// リボンの同期の 1 回分の材料（ブックの読み取り・ミラーのファイル・同期状態・計画）。<see cref="VbaSyncService"/> が、モジュールの
/// 計画と同じゲートの中で作って使う。
/// </summary>
/// <param name="Plan">計画（呼び出しの範囲に絞ったもの）。</param>
/// <param name="Book">ブックの読み取り結果。</param>
/// <param name="Mirror">ミラーにある形式ごとのファイル。</param>
/// <param name="State">計画に使った同期状態。</param>
/// <param name="LoadIssues">同期状態を自分で読んだときの警告（別のブックの記録だった E812 など）。モジュールの計画が読んだときは空。</param>
internal sealed record VbaRibbonWork(
    VbaRibbonSyncPlan Plan, VbaRibbonReadResult Book, IReadOnlyDictionary<VbaRibbonFormat, VbaRibbonMirrorEntry> Mirror,
    VbaSyncState State, IReadOnlyList<GraftIssue> LoadIssues);

/// <summary>
/// リボン（customUI）の同期のうち、ミラーと取込の側（仕様書_ブックの情報と依頼の観点 16.3・16.4。段階 F2）。
/// 判断は純ロジックの <see cref="VbaRibbonSyncPlanner"/> にあり、ここはミラーのファイルとブックのファイルを読み、取り込みを書き、
/// 同期状態の <c>ribbon</c> を更新するための部品を持つ。流れの束ね（ゲート・未保存の確認・状態の保存）は、モジュールと同じ
/// <see cref="VbaSyncService"/> の取込が行う（同期状態のファイルを 1 回の読み→更新→書きで扱い、モジュールとリボンの記録を
/// 別々に書いて片方を古い内容で上書きしないため）。ブックへの書き換え（16.5）は段階 F3 でここへ足す。
/// <para>
/// 【相手は Excel ではなくブックのファイル】 Excel のオブジェクトモデルに customUI を読み書きする手段は無い（付録A-24）。取込は
/// ディスク上のブックを <see cref="VbaRibbonPackageReader"/> で読むだけなので、<b>Excel が開いていなくても、モジュールの取込が
/// E804・E805 で失敗しても行う</b>（16.4）。読むのは最後に保存した内容で、Excel で編集中の未保存の変更は入らない。
/// </para>
/// <para>
/// 【使わないとき】 <see cref="PackageReader"/> が null（ブックの読み方を配線していない。純ロジックのテストで使う
/// <see cref="VbaSyncService"/> の既定）か、設定 <c>Vba.RibbonEditEnabled</c> がオフ（<see cref="IsEnabled"/>）のときは、
/// 何もしない（v0.6 までと同じ。<c>CustomUI/</c> を作らず、あっても同期しない。E810 の警告も従来どおり出す）。
/// </para>
/// </summary>
public sealed class VbaRibbonSyncService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly VbaSyncStateStore _states;
    private readonly Func<DateTimeOffset> _clock;

    internal VbaRibbonSyncService(VbaSyncStateStore states, Func<DateTimeOffset> clock)
    {
        _states = states;
        _clock = clock;
    }

    /// <summary>
    /// ブックのファイルを読む処理（既定は未設定＝リボンを同期しない）。<c>MainViewModel</c> が
    /// <see cref="VbaRibbonPackageReader.Read(string, VbaRibbonLimits?)"/> を渡す。画面のテストは、Excel の代役が開いている
    /// 「ブック」に実ファイルが無いので、一時ファイルの zip を読む処理に差し替える。引数はブックのフルパス。例外は投げない約束
    /// （<see cref="VbaRibbonPackageReader"/> は失敗を結果の種類で返す）。
    /// </summary>
    public Func<string, VbaRibbonReadResult>? PackageReader { get; set; }

    /// <summary>設定 <c>Vba.RibbonEditEnabled</c>（呼び出しのたびに読む。設定画面の変更を、作り直さずに反映するため）。</summary>
    public Func<bool> IsEnabled { get; set; } = () => true;

    /// <summary>取込・ログの記録先。</summary>
    public Logger? Logger { get; set; }

    /// <summary>リボンを同期するか（ブックの読み方が配線され、設定でオンになっている）。</summary>
    public bool IsActive => PackageReader is not null && IsEnabled();

    // ------------------------------------------------------------------
    // ミラーのファイル
    // ------------------------------------------------------------------

    /// <summary>
    /// ミラーのリボンのファイルを読む（ある形式だけ）。フォルダ名・ファイル名の大文字小文字は区別しない（Windows と同じ扱い。
    /// Linux の CI でも、利用者が <c>customui/CUSTOMUI14.XML</c> と書いたものを別物にしない）。
    /// 読めないファイルは、本文の代わりに理由を持たせて返す（<b>「無い」と扱わない</b>。16.4）。
    /// </summary>
    internal static async Task<IReadOnlyDictionary<VbaRibbonFormat, VbaRibbonMirrorEntry>> ReadMirrorAsync(string root, CancellationToken ct)
    {
        var entries = new Dictionary<VbaRibbonFormat, VbaRibbonMirrorEntry>();
        var folder = FindRibbonFolder(root);
        if (folder is null) return entries;

        foreach (var format in Enum.GetValues<VbaRibbonFormat>())
        {
            var full = FindChildFile(folder, VbaRibbonMirrorLayout.FileName(format));
            if (full is null) continue;

            var relative = VbaRibbonMirrorLayout.ToRelativePath(format);
            var read = await FileTextIO.ReadAsync(full, ct).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                var reason = read.Issues.FirstOrDefault()?.Detail ?? "読めませんでした";
                entries[format] = new VbaRibbonMirrorEntry(format, relative, full, new VbaRibbonMirrorFile(null, reason), null, null);
                continue;
            }

            if (read.Value.Shape.UnsupportedEncoding)
            {
                // 文字コードを判定できず、置換つきで読んだ本文。ハッシュを取ると「変わった」と誤り、取込で書き戻すと元に戻せない
                // 形に壊す（FileTextIO.WriteAsync も止める）。読めないものとして同期から外す。
                entries[format] = new VbaRibbonMirrorEntry(
                    format, relative, full, new VbaRibbonMirrorFile(null, FileTextIO.UnsupportedEncodingDetail), null, null);
                continue;
            }

            DateTimeOffset? modified = null;
            try
            {
                modified = File.GetLastWriteTime(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 更新日時は依頼文の目安に出すだけ。取れなくても本文は使える。
            }

            entries[format] = new VbaRibbonMirrorEntry(
                format, relative, full, new VbaRibbonMirrorFile(read.Value.Text), read.Value.Shape, modified);
        }

        return entries;
    }

    /// <summary>
    /// ミラーのリボンのファイルのフルパス（作り直しでごみ箱へ送る対象。<c>VbaSyncService.SyncTargetFiles</c>）。中身は読まない。
    /// </summary>
    internal static IReadOnlyList<string> MirrorFiles(string root)
    {
        var folder = FindRibbonFolder(root);
        if (folder is null) return Array.Empty<string>();
        return Enum.GetValues<VbaRibbonFormat>()
            .Select(format => FindChildFile(folder, VbaRibbonMirrorLayout.FileName(format)))
            .Where(path => path is not null)
            .Select(path => path!)
            .ToList();
    }

    private static string? FindRibbonFolder(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
        var exact = Path.Combine(root, VbaRibbonMirrorLayout.FolderName);
        if (Directory.Exists(exact)) return exact;
        try
        {
            return Directory.EnumerateDirectories(root)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), VbaRibbonMirrorLayout.FolderName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindChildFile(string folder, string fileName)
    {
        var exact = Path.Combine(folder, fileName);
        if (File.Exists(exact)) return exact;
        try
        {
            return Directory.EnumerateFiles(folder)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 取り込んだ XML をミラーへ書く（16.3）。既存のファイルへは、その文字コード・BOM・改行・末尾の改行を保つ（VBA連携仕様 4.2 と
    /// 同じ作法）。新しく作るときは UTF-8（BOM なし）で、改行は部品にあったまま（XML の本文をそのまま。モジュールのように CRLF へ
    /// そろえない: ブックへ書き戻すときもミラーの本文をそのまま書くので、往復で形を変えないほうが差分が小さい）。
    /// </summary>
    internal static async Task<GraftResult<bool>> WriteMirrorAsync(
        string root, VbaRibbonMirrorEntry? existing, VbaRibbonFormat format, string xml, CancellationToken ct)
    {
        var relative = VbaRibbonMirrorLayout.ToRelativePath(format);
        string full;
        try
        {
            var folder = FindRibbonFolder(root) ?? Path.Combine(root, VbaRibbonMirrorLayout.FolderName);
            full = existing?.FullPath ?? FindChildFile(folder, VbaRibbonMirrorLayout.FileName(format)) ?? Path.Combine(folder, VbaRibbonMirrorLayout.FileName(format));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E204, ExceptionMessages.Describe(ex), path: relative);
        }

        // 読み取り（VbaRibbonPackageReader）は先頭の BOM を除いて返すが、念のため本文に残った U+FEFF も除く（残すと、BOM なしで
        // 書いたつもりのファイルの先頭に BOM の文字が入り、次の比較で 1 文字ずれる）。
        var body = xml.Length > 0 && xml[0] == '﻿' ? xml[1..] : xml;
        TextShape shape;
        string text;
        if (existing?.Shape is { } kept)
        {
            shape = kept;
            var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
            text = kept.NewLine == "\n" ? lines : lines.Replace("\n", kept.NewLine);
            if (lines.Length > 0 && kept.EndsWithNewLine) text += kept.NewLine;
        }
        else
        {
            shape = new TextShape
            {
                Encoding = Utf8NoBom,
                HasBom = false,
                NewLine = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
                EndsWithNewLine = body.EndsWith('\n'),
            };
            text = body;
        }

        var written = await FileTextIO.WriteAsync(full, text, shape, ct).ConfigureAwait(false);
        return written.IsSuccess ? written : GraftResult<bool>.Fail(written.Issues.Select(i => i with { Path = relative }));
    }

    // ------------------------------------------------------------------
    // 計画
    // ------------------------------------------------------------------

    /// <summary>
    /// 呼び出しの範囲（<paramref name="scope"/>）のうち、リボンの 2 パスを正規の綴りで返す。null は「全部」。
    /// </summary>
    internal static HashSet<string>? RibbonScope(IReadOnlyCollection<string>? scope)
    {
        if (scope is null) return null;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in scope)
        {
            if (VbaRibbonMirrorLayout.TryParse(path, out var parsed) && parsed is not null) result.Add(parsed.RelativePath);
        }

        return result;
    }

    /// <summary>
    /// リボンの計画を作る。同期しない（<see cref="IsActive"/> が false）、または範囲にリボンが入っていないときは null。
    /// </summary>
    /// <param name="mirrorRoot">ミラーのフォルダ。</param>
    /// <param name="projectId">プロジェクトの Id（同期状態の読み出しに使う）。</param>
    /// <param name="workbookFullName">紐付けたブック。</param>
    /// <param name="scope">範囲（null なら全部）。</param>
    /// <param name="state">モジュールの計画が読んだ同期状態（無ければここで読む）。</param>
    /// <param name="ct">取り消し。</param>
    internal async Task<VbaRibbonWork?> BuildWorkAsync(
        string mirrorRoot, string projectId, string workbookFullName, IReadOnlyCollection<string>? scope, VbaSyncState? state,
        CancellationToken ct)
    {
        var reader = PackageReader;
        if (reader is null || !IsEnabled()) return null;
        var ribbonScope = RibbonScope(scope);
        if (ribbonScope is { Count: 0 }) return null;

        IReadOnlyList<GraftIssue> loadIssues = Array.Empty<GraftIssue>();
        if (state is null)
        {
            var loaded = await _states.LoadAsync(projectId, workbookFullName, ct).ConfigureAwait(true);
            state = loaded.Value;
            loadIssues = loaded.Issues;
        }

        var mirror = await ReadMirrorAsync(mirrorRoot, ct).ConfigureAwait(true);

        // ブックの zip の展開は大きなブックで時間がかかりうるので、UI スレッドを塞がないようスレッドプールで読む。
        var book = await Task.Run(() => CheckParts(reader(workbookFullName)), ct).ConfigureAwait(true);

        var plan = VbaRibbonSyncPlanner.Plan(
            book,
            mirror.ToDictionary(kv => kv.Key, kv => kv.Value.File),
            state.RibbonRecordsByFormat(),
            _clock());
        if (ribbonScope is not null)
        {
            plan = plan with { Items = plan.Items.Where(i => ribbonScope.Contains(i.MirrorPath)).ToList() };
        }

        return new VbaRibbonWork(plan, book, mirror, state, loadIssues);
    }

    /// <summary>
    /// ブックの部品のうち、XML として読めないもの（壊れている・DTD を含む・入れ子が深すぎる）に理由を付ける（16.4）。
    /// <para>
    /// 【なぜ読み取り（<see cref="VbaRibbonPackageReader"/>）の結果のままにしないか】 読み取りが「読めない」とするのは、部品が無い・
    /// 大きすぎる・文字コードの問題だけで、XML の中身は見ない（中身の解析は表示の側の <see cref="VbaRibbonXmlAnalyzer"/> が行う）。
    /// そのままだと、DTD（外部実体）を含む部品や壊れた XML を、取込でそのままミラーへ書いてしまう。仕様 16.4 は「部品が壊れている・
    /// DTD・大きすぎるときは、その形式を計画から外して警告」と決めているので、表示と同じ安全な解析（DTD 禁止・外部実体なし・
    /// 入れ子 128 段・文字数の上限）を通らない部品は、読めない部品（<see cref="VbaRibbonRawPart.Problem"/> つき）に直してから計画に渡す。
    /// </para>
    /// </summary>
    internal static VbaRibbonReadResult CheckParts(VbaRibbonReadResult read)
    {
        if (read.Status != VbaRibbonReadStatus.Ok) return read;
        var parts = read.Parts
            .Select(part => part.Xml is { } xml && !VbaRibbonXmlAnalyzer.TryParse(xml, VbaRibbonLimits.DefaultMaxPartBytes, out _, out var problem)
                ? part with { Xml = null, Problem = problem ?? "XML として読めません。" }
                : part)
            .ToList();
        return read with { Parts = parts };
    }

    /// <summary>
    /// 取り込む形式（16.4）: 取込の判定のもの、「ブック側を採用」と言われた衝突、「Excel から復元」で名指しされた削除保留。
    /// 自動の取込では、削除保留は復活させない（利用者が消したファイルを、次の取込で黙って戻さない）。
    /// </summary>
    internal static List<VbaRibbonSyncItem> PullTargets(VbaRibbonSyncPlan plan, bool restoreDeletePending, IReadOnlySet<string>? adopt)
        => plan.Items.Where(i => i.Action == VbaRibbonSyncAction.Pull
                                 || (i.Action == VbaRibbonSyncAction.Conflict && adopt is not null && adopt.Contains(i.MirrorPath))
                                 || (i.Action == VbaRibbonSyncAction.DeletePending && restoreDeletePending))
            .ToList();

    /// <summary>
    /// 1 形式を取り込む（ブックの部品の XML をミラーへ書く）。成功したら、新しい基準（ミラーもブックも取り込んだ本文）を返す。
    /// 基準を作るのは書けたあとだけ（失敗したのに基準だけ進むと、次の比較で変更を見落とすため。モジュールと同じ約束）。
    /// </summary>
    internal async Task<GraftResult<VbaRibbonSyncRecord>> PullOneAsync(
        string mirrorRoot, VbaRibbonWork work, VbaRibbonSyncItem item, CancellationToken ct)
    {
        var part = work.Book.Parts.FirstOrDefault(p => VbaRibbonMirrorLayout.FormatOf(p) == item.Format && p.IsReadable);
        if (part?.Xml is not { } xml)
        {
            // 計画を作ったあとに部品が消えることは無い（同じ読み取り結果を使う）が、念のため書かずに止める。
            return GraftResult<VbaRibbonSyncRecord>.Fail(GraftIssue.Of(
                ErrorCode.E810, $"{item.MirrorPath}: ブックのリボンの部品を読めなかったため、取り込みませんでした。",
                path: item.MirrorPath, severity: Severity.Warning));
        }

        work.Mirror.TryGetValue(item.Format, out var existing);
        var written = await WriteMirrorAsync(mirrorRoot, existing, item.Format, xml, ct).ConfigureAwait(true);
        if (!written.IsSuccess) return GraftResult<VbaRibbonSyncRecord>.Fail(written.Issues);

        var hash = VbaTextNormalizer.Hash(xml);
        Logger?.Info(
            "vba-pull",
            $"ブックのファイルからリボンを取り込みました（部品: {VbaRibbonText.OneLine(part.PartName)}）: {item.MirrorPath}",
            targetPath: Path.Combine(mirrorRoot, item.MirrorPath.Replace('/', Path.DirectorySeparatorChar)));
        return GraftResult<VbaRibbonSyncRecord>.Ok(new VbaRibbonSyncRecord(item.PartName, hash, hash, SyncedAt: _clock()));
    }

    /// <summary>
    /// 実行の済んだ形式以外の、I/O を伴わない記録の直し（基準の作り直し・削除保留の印・部品名の直し）と、記録の破棄を反映する
    /// （モジュールの <c>ApplyLocalChanges</c> と同じ考え方）。
    /// </summary>
    internal static VbaSyncState ApplyLocalChanges(VbaRibbonSyncPlan plan, VbaSyncState state, IReadOnlyCollection<string> executed)
    {
        foreach (var item in plan.Items)
        {
            if (executed.Contains(item.MirrorPath, StringComparer.OrdinalIgnoreCase)) continue;
            if (item.Action == VbaRibbonSyncAction.DiscardRecord)
            {
                state = state.WithoutRibbon(item.MirrorPath);
            }
            else if (item.Baseline is not null)
            {
                state = state.WithRibbon(item.MirrorPath, item.Baseline);
            }
        }

        return state;
    }

    // ------------------------------------------------------------------
    // ブックを読まずに分かる状態（バッジ）
    // ------------------------------------------------------------------

    /// <summary>
    /// ミラーと同期基準だけから、リボンの未反映・削除保留・要確認を数える（ブックは読まない。モジュールの
    /// <c>GetLocalStatusAsync</c> と同じ）。同期しないときは <see cref="VbaRibbonLocalStatus.None"/>。
    /// </summary>
    internal async Task<VbaRibbonLocalStatus> ComputeLocalStatusAsync(string mirrorRoot, VbaSyncState state, CancellationToken ct)
    {
        if (!IsActive) return VbaRibbonLocalStatus.None;
        var mirror = await ReadMirrorAsync(mirrorRoot, ct).ConfigureAwait(true);
        return ComputeLocalStatus(mirror, state);
    }

    /// <summary><see cref="ComputeLocalStatusAsync"/> の判定（ファイルに触れない）。</summary>
    internal static VbaRibbonLocalStatus ComputeLocalStatus(IReadOnlyDictionary<VbaRibbonFormat, VbaRibbonMirrorEntry> mirror, VbaSyncState state)
    {
        var records = state.RibbonRecordsByFormat();
        var unpushed = false;
        var pending = new List<string>();
        var review = 0;
        foreach (var format in Enum.GetValues<VbaRibbonFormat>())
        {
            mirror.TryGetValue(format, out var entry);
            records.TryGetValue(format, out var record);

            // 読めないだけで、無いわけではない（削除保留にも未反映にも数えない）。
            if (entry is not null && !entry.File.IsReadable) continue;
            if (record is null)
            {
                // 記録が無く、ミラーにだけある: 新規の追加（customUI の無いブックへ足す）の反映待ち。
                if (entry is not null) unpushed = true;
                continue;
            }

            if (record.NeedsReview) review++;
            if (entry is null)
            {
                pending.Add(VbaRibbonMirrorLayout.ToRelativePath(format));
                continue;
            }

            if (!string.Equals(VbaTextNormalizer.Hash(entry.File.Text!), record.MirrorHash, StringComparison.Ordinal)) unpushed = true;
        }

        return new VbaRibbonLocalStatus(unpushed, pending, review);
    }

    // ------------------------------------------------------------------
    // コンテキスト収集（16.14）
    // ------------------------------------------------------------------

    /// <summary>
    /// コンテキスト収集の「リボン（customUI）」の行が読む、ミラーのリボンのファイル（16.14）。同期しない・ミラーに読めるリボンの
    /// ファイルが無いときは null（行は 15 章のとおりブックのファイルを読む）。
    /// <para>
    /// 【なぜミラーから読むか（16.16-10）】 機能 F でミラーにファイルができると、AI がパッチを書く相手はミラーのファイルになる。
    /// 依頼文にブックの XML を出すと、未反映のあいだ AI が古い XML を見てパッチを書き、ミラーと一致しなくなる。
    /// </para>
    /// <para>
    /// 読めないファイルは載せない（「読めない」をブックの内容で埋めると、どちらの内容かが分からなくなる）。両方読めなければ null で、
    /// 15 章のとおりブックを読む。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<VbaRibbonContextFile>?> ReadForContextAsync(Project project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!IsActive || project.Vba is not { } link) return null;

        var mirror = await ReadMirrorAsync(project.Root, ct).ConfigureAwait(false);
        var readable = mirror.Values.Where(e => e.File.IsReadable).OrderBy(e => e.Format).ToList();
        if (readable.Count == 0) return null;

        // 反映済みかは、同期基準と比べて決める。別のブックの記録（E812）は使わないので、そのときはすべて「まだ反映していません」。
        var loaded = await _states.LoadAsync(project.Id, link.WorkbookFullName, ct).ConfigureAwait(false);
        var records = loaded.Value.RibbonRecordsByFormat();
        return readable
            .Select(e => new VbaRibbonContextFile(
                e.Format, e.RelativePath, e.File.Text!, e.Modified,
                Unpushed: !records.TryGetValue(e.Format, out var record)
                          || !string.Equals(VbaTextNormalizer.Hash(e.File.Text!), record.MirrorHash, StringComparison.Ordinal)))
            .ToList();
    }
}
