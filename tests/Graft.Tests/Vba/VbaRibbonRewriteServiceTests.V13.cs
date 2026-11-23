using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 仕様書 v1.3（リボン書き換えの点検で見つかった不具合の修正）の回帰テスト。どれも、修正前の実装では落ち、修正後に通る。
/// <list type="bullet">
/// <item>不具合 1（データ損失）: 置き換えの途中で失敗したとき、ブックの SHA-256 を元と比べてから扱いを決める。</item>
/// <item>不具合 3: 丸ごと戻す操作の記録に種別と選んだ控えの SHA-256 を持たせ、置き換え済みを判定して基準を捨てる。</item>
/// <item>不具合 4: 実行中の失敗の文面に、「Graft が終了したため」「閉じたまま」を含めない。</item>
/// <item>不具合 5: 戻すための一時ファイルを、作る前に作業の記録へ書く。</item>
/// <item>疑わしい点: 想定外の例外でも開き直す・掃除はいま作った控えを消さない・別の場所のブックの控え・丸ごと戻す側の E823。</item>
/// </list>
/// 不具合 2（リボンだけの取り消し）は UI 側の <c>WorkbookRibbonFlowTests</c>、<c>.xml</c> の許可は <c>VbaExtensionPolicyTests</c>、
/// 取り込み直しの控えのマージは <c>VbaRibbonV13LogicTests</c>。
/// </summary>
public sealed partial class VbaRibbonRewriteServiceTests
{
    /// <summary>1 回目の置き換えだけ失敗させる（置き換えの途中でブックが消える。Win32 の 1176 の再現）。2 回目以降は本物の置き換え（控えからの復元）。</summary>
    private void FailFirstReplaceAfter(Action<string, string> beforeFailing, Func<int, bool>? failFrom = null)
    {
        var calls = 0;
        Ribbon.Io.Replace = (temp, dest) =>
        {
            calls++;
            if (calls == 1)
            {
                beforeFailing(temp, dest);
                throw new IOException("置き換えの途中で失敗");
            }

            if (failFrom is not null && failFrom(calls)) throw new IOException("復元の置き換えにも失敗");
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
        };
    }

    // ------------------------------------------------------------------
    // 不具合 1: 置き換えの途中の失敗でも、ブックか控えのどちらかが必ず残る
    // ------------------------------------------------------------------

    [Fact(DisplayName = "不具合 1: 置き換えの途中でブックが消えて失敗しても、控えを消さずに戻す。ブックは元のバイト列・控えは残る・E821")]
    public async Task 置き換えの途中でブックが消えたら控えから戻す()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        FailFirstReplaceAfter((_, dest) => File.Delete(dest));

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("控えから元の内容に戻しました", StringComparison.Ordinal));
        result.Issues.Single().Detail.Should().NotContain("元のブックは変えていません", "ブックが一度消えているので、変えていないとは言わない");
        BookShouldBeUnchanged(original, "ブックが消えていたので、控えから戻した");
        BackupFiles().Should().ContainSingle("戻せたあとも、控えは残す（消さない）");
        File.ReadAllBytes(BackupFiles()[0]).Should().Equal(original);
        NothingLeftBehind();
        (await _states.LoadAsync(Project.Id, _book)).Value.Ribbon![Ribbon14].NeedsReview.Should().BeFalse("戻せたので要確認にはしない");
    }

    [Fact(DisplayName = "不具合 1: 置き換えが済んだあとで例外が出ても、「元のまま」とは言わず、控えから元の内容へ戻す。控えは残る")]
    public async Task 置き換えが済んだあとの失敗は控えから戻す()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        FailFirstReplaceAfter((temp, dest) => VbaRibbonPackageWriter.ReplaceFile(temp, dest));

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("控えから元の内容に戻しました", StringComparison.Ordinal));
        BookShouldBeUnchanged(original, "置き換わっていたブックを、控えから元の内容へ戻した（書き換えが済んだのに「元のまま」と報告しない）");
        BackupFiles().Should().ContainSingle();
        NothingLeftBehind();
    }

    [Fact(DisplayName = "不具合 1: 置き換えの途中でブックが消え、控えからも戻せないときは E823。控えは消さず、書き換え後の一時ファイルも残し、要確認の印を立てる")]
    public async Task 戻せなければブックも控えも失わない()
    {
        var original = await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        FailFirstReplaceAfter((_, dest) => File.Delete(dest), failFrom: _ => true);

        var result = await Ribbon.RewriteAsync(Project, plan);

        var issue = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E823).Subject;
        File.Exists(_book).Should().BeFalse("置き換えの途中でブックが消えたまま、戻せなかった");
        BackupFiles().Should().ContainSingle("ブックが無くても、控えが元のブックを守っている");
        File.ReadAllBytes(BackupFiles()[0]).Should().Equal(original);
        var temps = TempFilesNextToBook();
        temps.Should().ContainSingle("新しい内容の一時ファイルは、唯一の写しかもしれないので消さない（戻すための .restore の一時ファイルは消える）");
        issue.Detail.Should().Contain(BackupDir).And.Contain(temps[0], "控えと一時ファイルの場所を知らせる");
        File.Exists(JobFile).Should().BeFalse();
        (await _states.LoadAsync(Project.Id, _book)).Value.Ribbon![Ribbon14].NeedsReview.Should().BeTrue("次の同期で衝突として扱い、黙って上書きしない");
    }

    [Fact(DisplayName = "不具合 1: 開いているブックで置き換えの途中に失敗しても、控えから戻し、開き直す（券は残らない）")]
    public async Task 開いているブックの置き換えの途中の失敗()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        FailFirstReplaceAfter((_, dest) => File.Delete(dest));

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().Contain(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("控えから元の内容に戻しました", StringComparison.Ordinal));
        result.Issues.First().Detail.Should().Contain("開き直しました");
        BookShouldBeUnchanged(original, "控えから戻した");
        OpenBook.ReopenCount.Should().Be(1);
        NoTicketLeft();
        NothingLeftBehind();
    }

    // ------------------------------------------------------------------
    // 不具合 3: 丸ごと戻す操作が途中で落ちたとき、置き換え済みと判定して基準を捨てる
    // ------------------------------------------------------------------

    [Fact(DisplayName = "不具合 3: 丸ごと戻す操作が置き換えのあとで落ちたら、後始末は「置き換え済み」と判定し、リボンの同期基準を捨てる。「外部で保存された」とは案内しない")]
    public async Task 丸ごと戻す操作が落ちたあとの判定()
    {
        var (original, _) = await RewriteOnceAsync();
        var plan = await RestorePlanAsync();
        Ribbon.Io.Replace = (temp, dest) =>
        {
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
            throw new SimulatedCrash();
        };

        await FluentActions.Awaiting(() => Ribbon.RestoreFromBackupAsync(Project, plan)).Should().ThrowAsync<SimulatedCrash>();
        File.ReadAllBytes(_book).Should().Equal(original, "置き換えは済んでから落ちた");
        File.Exists(JobFile).Should().BeTrue();
        (await _states.LoadAsync(Project.Id, _book)).Value.Ribbon.Should().NotBeNull("落ちたので、基準はまだ古いまま残っている");

        _service = NewService();
        var recovery = await Ribbon.RecoverInterruptedAsync(Project);

        recovery!.State.ToString().Should().Be("Replaced", "ブックのハッシュが、選んだ控えと同じ");
        recovery.Message.Should().Contain("控えでブックを置き換える操作").And.Contain("置き換え済み").And.Contain(plan.BackupFileName)
            .And.NotContain("Excel などで保存された", "戻せていたのに「外部で保存された」と案内しない")
            .And.NotContain("リボンの書き換え");
        var state = (await _states.LoadAsync(Project.Id, _book)).Value;
        (state.Ribbon is null || state.Ribbon.Count == 0).Should().BeTrue("成功したときと同じく、リボンの同期基準を捨てる");
        NothingLeftBehind();
    }

    [Fact(DisplayName = "不具合 3: 丸ごと戻す操作が置き換えの前に落ちたら「置き換える前のまま」。基準は捨てない")]
    public async Task 丸ごと戻す操作が置き換えの前に落ちた()
    {
        var (_, rewritten) = await RewriteOnceAsync();
        var plan = await RestorePlanAsync();
        Ribbon.Io.Replace = (_, _) => throw new SimulatedCrash();

        await FluentActions.Awaiting(() => Ribbon.RestoreFromBackupAsync(Project, plan)).Should().ThrowAsync<SimulatedCrash>();
        _service = NewService();
        var recovery = await Ribbon.RecoverInterruptedAsync(Project);

        recovery!.State.Should().Be(VbaRibbonInterruptedState.Original);
        recovery.Message.Should().Contain("置き換える前のままです");
        File.ReadAllBytes(_book).Should().Equal(rewritten);
        (await _states.LoadAsync(Project.Id, _book)).Value.Ribbon.Should().NotBeNull("置き換えていないので、基準は捨てない");
    }

    // ------------------------------------------------------------------
    // 不具合 4: 実行中の失敗の文面
    // ------------------------------------------------------------------

    [Fact(DisplayName = "不具合 4: 書き換えの最中の入出力の失敗の文面に、「Graft が終了したため」「閉じたまま」は出ない。「開き直しました」と矛盾しない")]
    public async Task 実行中の失敗の文面()
    {
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        // 置き換えのあとの読み直しで、入出力の失敗（段階ごとには捕まえていない所の IOException）。
        Ribbon.PackageReader = path => PartText(File.ReadAllBytes(path), "customUI/customUI14.xml") == XmlB
            ? throw new IOException("読み直しで失敗")
            : VbaRibbonPackageReader.Read(path);

        var result = await Ribbon.RewriteAsync(Project, plan);

        var text = string.Join(" / ", result.Issues.Select(i => i.Detail));
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E821);
        text.Should().Contain("開き直しました").And.Contain("書き換え済み");
        text.Should().NotContain("Graft が終了したため").And.NotContain("閉じたまま");
        NoTicketLeft();
    }

    // ------------------------------------------------------------------
    // 不具合 5: 戻すための一時ファイルを、作る前に記録へ書く
    // ------------------------------------------------------------------

    [Fact(DisplayName = "不具合 5: 丸ごと戻す側の「置き換える前の控えから戻す」の一時ファイルを、作る前に作業の記録へ書く（作った瞬間に、記録に載っている）")]
    public async Task 戻すための一時ファイルは記録してから作る()
    {
        var (_, rewritten) = await RewriteOnceAsync();
        var plan = await RestorePlanAsync();
        var calls = 0;
        Ribbon.Io.Replace = (temp, dest) =>
        {
            calls++;
            // 1 回目（控えの内容での置き換え）だけ壊れた内容を書く → 照合が合わず、置き換える前の控えから戻す段階へ進む。
            if (calls == 1) File.WriteAllBytes(temp, new byte[] { 9, 9, 9 });
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
        };
        var defaultCreate = Ribbon.Io.CreateNew;
        var restoreTemps = new List<string>();
        string? jobTextWhenCreated = null;
        Ribbon.Io.CreateNew = path =>
        {
            if (path.EndsWith(".restore.tmp", StringComparison.Ordinal))
            {
                restoreTemps.Add(path);
                // 2 つ目（置き換える前の控えから戻すための一時ファイル）を作る瞬間に、作業の記録に載っているか。ここで落ちると（プロセスが
                // 死ぬと）、finally も走らず、ブック全体の写しがブックの隣に残る。そのとき消せるのは、記録に名前があるときだけ。
                if (restoreTemps.Count == 2) jobTextWhenCreated = File.ReadAllText(JobFile);
            }

            return defaultCreate(path);
        };

        var result = await Ribbon.RestoreFromBackupAsync(Project, plan);

        result.Issues.Should().Contain(i => i.Code == ErrorCode.E821, "置き換える前の控えから戻せた");
        File.ReadAllBytes(_book).Should().Equal(rewritten);
        restoreTemps.Should().HaveCount(2);
        jobTextWhenCreated.Should().NotBeNull().And.Contain(Path.GetFileName(restoreTemps[1]), "作る前に、作業の記録へ書いてある");
        NothingLeftBehind();
    }

    // ------------------------------------------------------------------
    // 疑わしい点
    // ------------------------------------------------------------------

    [Fact(DisplayName = "想定外の例外（IOException 以外）でも、保存して閉じたブックは開き直してから抜ける（書き換え）")]
    public async Task 想定外の例外でも開き直す_書き換え()
    {
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        Ribbon.Io.Replace = (_, _) => throw new InvalidOperationException("想定外");

        await FluentActions.Awaiting(() => Ribbon.RewriteAsync(Project, plan)).Should().ThrowAsync<InvalidOperationException>();

        CloseOpenCalls().Should().Equal("SaveClose", "Reopen");
        OpenBook.Should().NotBeNull("閉じたまま黙って放置しない");
        NoTicketLeft();

        // 開き直したので、次の後始末の知らせは「閉じたまま」と言わない。
        var recovery = await Ribbon.RecoverInterruptedAsync(Project);
        recovery!.Message.Should().NotContain("閉じたまま");
        TempFilesNextToBook().Should().BeEmpty("後始末が、置き換えの途中で残った一時ファイルを消す");
    }

    [Fact(DisplayName = "想定外の例外でも、保存して閉じたブックは開き直してから抜ける（丸ごと戻す）")]
    public async Task 想定外の例外でも開き直す_丸ごと戻す()
    {
        await RewriteOnceAsync(open: true);
        var plan = await RestorePlanAsync();
        _bridge.Calls.Clear();
        Ribbon.Io.Replace = (_, _) => throw new InvalidOperationException("想定外");

        await FluentActions.Awaiting(() => Ribbon.RestoreFromBackupAsync(Project, plan)).Should().ThrowAsync<InvalidOperationException>();

        CloseOpenCalls().Should().Equal("SaveClose", "Reopen");
        NoTicketLeft();
    }

    [Fact(DisplayName = "控えの掃除は、いま作った控えを日時に依らず必ず残す（時計が戻っていても）")]
    public async Task 掃除はいま作った控えを消さない()
    {
        await ArrangeAsync(book14: XmlA);
        Ribbon.MaxBackupGenerations = () => 1;
        Directory.CreateDirectory(BackupDir);
        // 時計が戻った状況: すでに「未来」の日時の控えがある。日時だけで最新を決めると、いま作った控えが古い扱いになって消える。
        var future = Path.Combine(BackupDir, "計画-20271231-235959-ribbon.xlsm");
        File.WriteAllBytes(future, new byte[] { 1, 2, 3 });
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        File.Exists(result.Value.BackupPath).Should().BeTrue("いま作った控えは、書き換えを元へ戻せる唯一の控え。消さない");
    }

    [Fact(DisplayName = "控えのブックのフルパスが、いまのブックと違えば、確認の文面に「別の場所のブックの控えです」と出す（記録が無い控えは、同期状態の紐付け先と比べる）")]
    public async Task 別の場所のブックの控え()
    {
        await RewriteOnceAsync();
        var backupName = Path.GetFileName(BackupFiles().Single());
        var elsewhere = new Project
        {
            Id = Project.Id, Name = "計画", Root = _mirror, Vba = new VbaLink { WorkbookFullName = @"D:\other\計画.xlsm" },
        };
        // ファイルの実体はいまのブックのまま。フルパスだけが違う（結び直したあとの、別の場所のブック、という状況の代わり）。
        Ribbon.WorkbookFileOf = _ => _book;

        var same = await Ribbon.PrepareRestoreFromBackupAsync(Project, backupName);
        same.IsSuccess.Should().BeTrue();
        VbaWorkbookRestoreText.Message(same.Value).Should().NotContain("別の場所のブックの控えです");

        var other = await Ribbon.PrepareRestoreFromBackupAsync(elsewhere, backupName);
        other.IsSuccess.Should().BeTrue("名前と拡張子が同じなので止めはしない（結び直した・移しただけの同じブックかもしれない）。強い確認で利用者が決める");
        var message = VbaWorkbookRestoreText.Message(other.Value);
        message.Should().Contain("別の場所のブックの控えです").And.Contain($"いま置き換えるブック: {_book}");
        message.Should().StartWith("⚠ この控えは、別の場所のブックの控えです");

        // 記録の無い控え（出どころの記録を足す前の控え）は、同期状態に残っている紐付け先のブックと比べる。
        Directory.Delete(Path.Combine(BackupDir, ".origins"), recursive: true);
        var fallback = await Ribbon.PrepareRestoreFromBackupAsync(elsewhere, backupName);
        VbaWorkbookRestoreText.Message(fallback.Value).Should().Contain("別の場所のブックの控えです").And.Contain(_book);
        var fallbackSame = await Ribbon.PrepareRestoreFromBackupAsync(Project, backupName);
        VbaWorkbookRestoreText.Message(fallbackSame.Value).Should().NotContain("別の場所のブックの控えです");
    }

    [Fact(DisplayName = "丸ごと戻す側の E823 でも、リボンの同期状態に要確認の印を立て、基準のハッシュを捨てる（書き換えの E823 と揃える）")]
    public async Task 丸ごと戻すE823は要確認にする()
    {
        await RewriteOnceAsync();
        var plan = await RestorePlanAsync();
        var calls = 0;
        Ribbon.Io.Replace = (temp, dest) =>
        {
            calls++;
            if (calls > 1) throw new IOException("復元の置き換えに失敗");
            File.WriteAllBytes(temp, new byte[] { 9, 9, 9 });
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
        };

        var result = await Ribbon.RestoreFromBackupAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E823);
        var record = (await _states.LoadAsync(Project.Id, _book)).Value.Ribbon![Ribbon14];
        record.NeedsReview.Should().BeTrue("ブックの中身が元とも控えとも違うので、次の同期で衝突として確認させる");
        record.MirrorHash.Should().BeEmpty("基準のハッシュは意味を失った");
        record.FileHash.Should().BeEmpty();
        File.Exists(JobFile).Should().BeFalse();
    }
}
