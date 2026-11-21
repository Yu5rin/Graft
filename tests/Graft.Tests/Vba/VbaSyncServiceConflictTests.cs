using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 段階 P3 で <see cref="VbaSyncService"/> に足した、衝突の解決（「Graft 側で上書き」「Excel 側を採用」。仕様書_VBA連携 7.5）と
/// 検査（<see cref="VbaSyncService.CheckAsync"/>）のテスト。ダイアログの画面と、適用の流れへの差し込みは
/// <c>Graft.UiTests</c> の <c>VbaP3*Tests</c> で確かめる。
/// </summary>
public class VbaSyncServiceConflictTests : IDisposable
{
    private const string Book = @"C:\Work\計画.xlsm";

    private readonly TempWorkspace _ws = new();
    private readonly AppPaths _paths;
    private readonly FakeVbaBridge _bridge = new();
    private readonly VbaSyncStateStore _states;
    private readonly VbaSyncService _service;
    private readonly DateTimeOffset _now = new(2026, 11, 21, 10, 0, 0, TimeSpan.FromHours(9));

    public VbaSyncServiceConflictTests()
    {
        _paths = new AppPaths(_ws.CreateDirectory("app"));
        _states = new VbaSyncStateStore(_paths);
        _service = new VbaSyncService(_bridge, _states, _paths, trash: null, () => _now);
    }

    public void Dispose() => _ws.Dispose();

    private string MirrorRoot => Path.Combine(_paths.VbaMirrorsDirectory, VbaMirrorFolderName.Build(Book));

    private string MirrorFile(string relative) => Path.Combine(MirrorRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    private Project NewProject()
        => new() { Id = ProjectStore.CreateId(MirrorRoot), Name = "計画", Root = MirrorRoot, Vba = new VbaLink { WorkbookFullName = Book } };

    private async Task<Project> ConflictingProjectAsync()
    {
        _bridge.AddWorkbook(Book, FakeVbaBridge.Std("Conf", "base"), FakeVbaBridge.Std("Fine", "fine"));
        (await _service.CreateMirrorAsync(Book, MirrorRoot, rebuild: false)).IsSuccess.Should().BeTrue();
        File.WriteAllText(MirrorFile("Modules/Conf.bas"), "graft side\r\n");
        _bridge.Get(Book)!.Find("Conf")!.Code = "excel side";
        return NewProject();
    }

    private async Task<VbaSyncState> StateAsync() => (await _states.LoadAsync(NewProject().Id, Book)).Value;

    [Fact(DisplayName = "衝突の詳細: Pull も Push も、衝突したモジュールの両側の本文を結果に載せる")]
    public async Task 衝突の詳細が結果に載る()
    {
        var project = await ConflictingProjectAsync();

        var pull = await _service.PullAsync(project);
        var push = await _service.PushAsync(project);

        foreach (var item in new[] { pull.Value.ConflictItems.Single(), push.Value.ConflictItems.Single() })
        {
            item.RelativePath.Should().Be("Modules/Conf.bas");
            item.Kind.Should().Be(VbaModuleKind.Standard);
            item.Component.Should().Be("Conf");
            item.ExcelText.Should().Be("excel side");
            item.MirrorText.Should().Be("graft side\r\n");
        }
    }

    [Fact(DisplayName = "Excel 側を採用: 衝突したモジュールだけ Excel の本文でミラーを置き換え、基準を更新する。次は何も要らない")]
    public async Task Excel側を採用する()
    {
        var project = await ConflictingProjectAsync();

        var pull = await _service.PullAsync(project, adoptConflicts: new[] { "Modules/Conf.bas" });

        pull.Value.Pulled.Should().Equal("Modules/Conf.bas");
        pull.Value.Conflicts.Should().BeEmpty();
        pull.Issues.Should().NotContain(i => i.Code == ErrorCode.E812);
        File.ReadAllText(MirrorFile("Modules/Conf.bas")).Should().StartWith("excel side");
        var record = (await StateAsync()).Modules["Modules/Conf.bas"];
        record.MirrorHash.Should().Be(VbaTextNormalizer.Hash("excel side")).And.Be(record.ExcelHash);
        (await _service.PullAsync(project)).Value.IsEmpty.Should().BeTrue();
        (await _service.GetLocalStatusAsync(project)).UnpushedCount.Should().Be(0);
    }

    [Fact(DisplayName = "Excel 側を採用: 衝突していないモジュールを名指ししても何も起きない（Pull 判定ではないものを巻き込まない）")]
    public async Task 衝突していないものを採用しても何も起きない()
    {
        var project = await ConflictingProjectAsync();
        File.WriteAllText(MirrorFile("Modules/Fine.bas"), "graft fine\r\n"); // Fine はミラー側だけの変更（Push 判定）

        var pull = await _service.PullAsync(project, adoptConflicts: new[] { "Modules/Fine.bas" });

        pull.Value.Pulled.Should().BeEmpty();
        File.ReadAllText(MirrorFile("Modules/Fine.bas")).Should().Be("graft fine\r\n");
        pull.Value.Conflicts.Should().Equal(new[] { "Modules/Conf.bas" }, "名指ししていない衝突は未解決のまま");
    }

    [Fact(DisplayName = "Graft 側で上書き: Excel 側の本文を .rescue/ に控えてから Excel へ書き、基準を更新する。衝突の一覧には残らない")]
    public async Task Graft側で上書きする()
    {
        var project = await ConflictingProjectAsync();

        var push = await _service.PushAsync(project, overwriteConflicts: new[] { "Modules/Conf.bas" });

        push.Value.Pushed.Should().Equal("Modules/Conf.bas");
        push.Value.Conflicts.Should().BeEmpty();
        push.Issues.Should().NotContain(i => i.Code == ErrorCode.E812);
        _bridge.Get(Book)!.Find("Conf")!.Code.Should().Be("graft side");
        var rescue = Directory.GetFiles(_paths.GetVbaRescueDirectory(project.Id)).Should().ContainSingle().Subject;
        Path.GetFileName(rescue).Should().StartWith("Conf-20261121-100000");
        File.ReadAllText(rescue).Should().Be("excel side");
        File.ReadAllText(MirrorFile("Modules/Conf.bas")).Should().Be("graft side\r\n", "ミラーは書き換えない");
        (await StateAsync()).Modules["Modules/Conf.bas"].MirrorHash.Should().Be(VbaTextNormalizer.Hash("graft side"));
        (await _service.GetLocalStatusAsync(project)).UnpushedCount.Should().Be(0);
    }

    [Fact(DisplayName = "Graft 側で上書き: 控えを書けなければ上書きしない（E809）。Excel 側の本文は失われない")]
    public async Task 控えを書けなければ上書きしない()
    {
        var project = await ConflictingProjectAsync();
        // 控えのフォルダの場所をファイルで塞ぐ。
        Directory.CreateDirectory(_paths.VbaSyncDirectory);
        File.WriteAllText(_paths.GetVbaRescueDirectory(project.Id), "blocker");

        var push = await _service.PushAsync(project, overwriteConflicts: new[] { "Modules/Conf.bas" });

        push.Value.Failed.Should().Equal("Modules/Conf.bas");
        push.Value.Pushed.Should().NotContain("Modules/Conf.bas");
        push.Issues.Should().Contain(i => i.Code == ErrorCode.E809 && i.Detail!.Contains("控え"));
        _bridge.Get(Book)!.Find("Conf")!.Code.Should().Be("excel side");
        _bridge.Calls.Should().NotContain(c => c.StartsWith("Write:") && c.Contains("Conf"));
    }

    [Fact(DisplayName = "Graft 側で上書き: 衝突でないモジュールを名指ししても、通常の Push と同じ（控えは作らない）")]
    public async Task 衝突でなければ控えを作らない()
    {
        var project = await ConflictingProjectAsync();
        File.WriteAllText(MirrorFile("Modules/Fine.bas"), "graft fine\r\n");

        var push = await _service.PushAsync(project, new[] { "Modules/Fine.bas" }, overwriteConflicts: new[] { "Modules/Fine.bas" });

        push.Value.Pushed.Should().Equal("Modules/Fine.bas");
        Directory.Exists(_paths.GetVbaRescueDirectory(project.Id)).Should().BeFalse();
    }

    [Fact(DisplayName = "検査: 何も書かずに、取り込むもの・反映するもの・衝突を分けて返す")]
    public async Task 検査は何も書かない()
    {
        var project = await ConflictingProjectAsync();
        _bridge.Get(Book)!.Modules.Add(new FakeVbaBridge.Module("OnlyExcel", VbaModuleKind.Standard, "x"));
        File.WriteAllText(MirrorFile("Modules/Fine.bas"), "graft fine\r\n");
        var mirrorBefore = File.ReadAllText(MirrorFile("Modules/Conf.bas"));
        _bridge.Calls.Clear();

        var check = await _service.CheckAsync(project);

        check.IsSuccess.Should().BeTrue();
        check.Value.Pull.Should().Equal("Modules/OnlyExcel.bas");
        check.Value.Push.Should().Equal("Modules/Fine.bas");
        check.Value.Conflicts.Select(c => c.RelativePath).Should().Equal("Modules/Conf.bas");
        check.Value.IsClean.Should().BeFalse();
        _bridge.Calls.Should().NotContain(c => c.StartsWith("Write:") || c == "Save");
        File.Exists(MirrorFile("Modules/OnlyExcel.bas")).Should().BeFalse("検査では取り込まない");
        File.ReadAllText(MirrorFile("Modules/Conf.bas")).Should().Be(mirrorBefore);
        _bridge.Get(Book)!.Find("Conf")!.Code.Should().Be("excel side");
    }

    [Fact(DisplayName = "検査: 名前で絞れる。変更が無ければ clean。接続できなければ失敗を返す")]
    public async Task 検査の絞り込みと失敗()
    {
        var project = await ConflictingProjectAsync();

        var scoped = await _service.CheckAsync(project, new[] { "Modules/Fine.bas" });
        scoped.Value.IsClean.Should().BeTrue("衝突は対象の外");
        scoped.Value.Conflicts.Should().BeEmpty();

        _bridge.CloseWorkbook(Book);
        var closed = await _service.CheckAsync(project);
        closed.IsSuccess.Should().BeFalse();
        closed.Errors.Should().Contain(i => i.Code == ErrorCode.E804);
    }

    [Fact(DisplayName = "Push の結果: ミラーのファイルが無く Excel には残っているモジュールは削除保留として返す（取り消しの後の通知に使う）")]
    public async Task 削除保留が結果に載る()
    {
        var project = await ConflictingProjectAsync();
        File.Delete(MirrorFile("Modules/Fine.bas"));

        var push = await _service.PushAsync(project, new[] { "Modules/Fine.bas" });

        push.Value.DeletePending.Should().Equal("Modules/Fine.bas");
        (await StateAsync()).Modules["Modules/Fine.bas"].DeletePending.Should().BeTrue();
        _bridge.Get(Book)!.Find("Fine").Should().NotBeNull("Excel 側のモジュールは消さない");
    }
}

/// <summary>衝突の要求・結果・差分の組み立て（純ロジック）。</summary>
public class VbaConflictModelsTests
{
    private static VbaConflictItem Item(string path = "Modules/A.bas", string excel = "e", string mirror = "m")
        => new(path, VbaModuleKind.Standard, "A", excel, mirror);

    [Theory(DisplayName = "衝突ダイアログの選択肢は場面で決まる: 適用の後・復元の後は「Excel 側を採用」を出さず「反映しない」を出す。取り消しは適用の後だけ")]
    [InlineData(VbaConflictScene.BeforeAnalysis, true, false, true, false)]
    [InlineData(VbaConflictScene.Reanalysis, true, false, true, false)]
    [InlineData(VbaConflictScene.ManualPull, true, false, true, false)]
    [InlineData(VbaConflictScene.ManualPush, true, false, true, false)]
    [InlineData(VbaConflictScene.AfterApply, false, true, false, true)]
    [InlineData(VbaConflictScene.AfterRestore, false, true, false, false)]
    public void 場面ごとの選択肢(VbaConflictScene scene, bool adopt, bool skip, bool abort, bool undo)
    {
        var request = new VbaConflictRequest(scene, new[] { Item() });

        request.CanAdoptExcel.Should().Be(adopt);
        request.CanSkip.Should().Be(skip);
        request.CanAbort.Should().Be(abort);
        request.CanUndoApply.Should().Be(undo);
    }

    [Fact(DisplayName = "選ばずに閉じたときの既定: 何もせず戻れる場面は中止、ファイルが既に変わっている場面は全件「反映しない」")]
    public void 既定の結果()
    {
        var items = new[] { Item("Modules/A.bas"), Item("Modules/B.bas") };

        new VbaConflictRequest(VbaConflictScene.BeforeAnalysis, items).DefaultDecision.Aborted.Should().BeTrue();

        var after = new VbaConflictRequest(VbaConflictScene.AfterApply, items).DefaultDecision;
        after.Aborted.Should().BeFalse();
        after.UndoApply.Should().BeFalse("取り消しは既定にしない");
        after.PathsWith(VbaConflictChoice.Skip).Should().BeEquivalentTo("Modules/A.bas", "Modules/B.bas");
    }

    [Fact(DisplayName = "全件に同じ選択を付けた結果。パスの大文字小文字は区別しない")]
    public void 全件に適用()
    {
        var request = new VbaConflictRequest(VbaConflictScene.ManualPush, new[] { Item("Modules/A.bas"), Item("Classes/B.cls") });

        var decision = VbaConflictDecision.All(request, VbaConflictChoice.OverwriteExcel);

        decision.PathsWith(VbaConflictChoice.OverwriteExcel).Should().BeEquivalentTo("Modules/A.bas", "Classes/B.cls");
        decision.Choices["modules/a.bas"].Should().Be(VbaConflictChoice.OverwriteExcel);
        decision.PathsWith(VbaConflictChoice.AdoptExcel).Should().BeEmpty();
    }

    [Fact(DisplayName = "選択ダイアログの要求: 既定のボタン（IsDefault）が結果の既定。無指定なら先頭")]
    public void 選択ダイアログの既定()
    {
        var withDefault = new VbaChoiceRequest("t", "m", new[] { new VbaChoiceOption("a", "A"), new VbaChoiceOption("b", "B", IsDefault: true) });
        var without = new VbaChoiceRequest("t", "m", new[] { new VbaChoiceOption("a", "A"), new VbaChoiceOption("b", "B") });

        withDefault.Default.Id.Should().Be("b");
        without.Default.Id.Should().Be("a");
    }

    [Fact(DisplayName = "衝突の差分: 左が Excel 側、右が Graft 側。改行コードの違いだけでは差分にならず、行内の違いは件数に出る")]
    public void 衝突の差分()
    {
        var plan = VbaConflictDiff.BuildPlan(
            Item("Modules/A.bas", excel: "Sub A()\r\n  x = 1\r\nEnd Sub", mirror: "Sub A()\n  x = 2\nEnd Sub\n"), contextLines: 3);

        plan.CanApply.Should().BeFalse("適用の対象ではない");
        plan.BeforeText.Should().Be("Sub A()\r\n  x = 1\r\nEnd Sub");
        plan.AfterText.Should().StartWith("Sub A()\r\n  x = 2");
        plan.Added.Should().Be(1);
        plan.Removed.Should().Be(1);

        var same = VbaConflictDiff.BuildPlan(Item(excel: "a\r\nb", mirror: "a\nb"), contextLines: 3);
        same.Added.Should().Be(0);
        same.Removed.Should().Be(0);
    }

    [Fact(DisplayName = "衝突の項目: Excel 側のハッシュは末尾の改行の違いに左右されない（再検査で承認した上書きの照合に使う）")]
    public void 項目のハッシュ()
    {
        Item(excel: "x\r\ny").ExcelHash.Should().Be(Item(excel: "x\r\ny\r\n").ExcelHash);
        Item(excel: "x").ExcelHash.Should().NotBe(Item(excel: "y").ExcelHash);
    }
}
