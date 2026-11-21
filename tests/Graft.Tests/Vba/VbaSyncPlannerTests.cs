using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaSyncPlanner"/> のテスト（仕様書_VBA連携 6.5・10.1）。6.5 の表の全パターンを網羅する。
/// 以降の表記: M=ミラーの現在の本文、E=Excel の現在の本文、Bm/Be=同期基準のミラー側/Excel 側。
/// </summary>
public class VbaSyncPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 10, 0, 0, TimeSpan.FromHours(9));
    private const string Std = "Modules/Module1.bas";

    // --- 入力の組み立て -------------------------------------------------

    private static VbaMirrorModule Mirror(string text, VbaModuleKind kind = VbaModuleKind.Standard, string name = "Module1")
        => new(VbaMirrorLayout.ToRelativePath(kind, name), kind, name,
            VbaTextNormalizer.Hash(text), VbaTextNormalizer.LooseHash(text));

    private static VbaExcelModule Excel(
        string text, VbaModuleKind kind = VbaModuleKind.Standard, string name = "Module1", string? caption = null)
        => new(name, kind, VbaTextNormalizer.Hash(text), VbaTextNormalizer.LooseHash(text), caption);

    /// <summary>最後に同期したとき、ミラーが <paramref name="mirrorText"/>、Excel が <paramref name="excelText"/> だった状態。</summary>
    private static VbaSyncState State(
        string mirrorText, string excelText, VbaModuleKind kind = VbaModuleKind.Standard, string name = "Module1",
        bool deletePending = false, string? caption = null)
    {
        var module = VbaModuleState.Create(
            kind, name, caption, VbaTextNormalizer.Hash(mirrorText), VbaTextNormalizer.Hash(excelText),
            VbaTextNormalizer.LooseHash(excelText), Now.AddDays(-1)) with { DeletePending = deletePending };
        return new VbaSyncState { WorkbookFullName = @"C:\a.xlsm" }.WithModule(VbaMirrorLayout.ToRelativePath(kind, name), module);
    }

    private static VbaModulePlan PlanOne(
        VbaMirrorModule? mirror, VbaExcelModule? excel, VbaSyncState? state)
    {
        var plan = VbaSyncPlanner.Plan(
            mirror is null ? Array.Empty<VbaMirrorModule>() : new[] { mirror },
            excel is null ? Array.Empty<VbaExcelModule>() : new[] { excel },
            state, Now);
        return plan.Modules.Should().ContainSingle().Subject;
    }

    private const string Base = "Sub A()\r\nEnd Sub";
    private const string Edited = "Sub A()\r\n  MsgBox 1\r\nEnd Sub";
    private const string OtherEdit = "Sub A()\r\n  MsgBox 2\r\nEnd Sub";

    // ------------------------------------------------------------------
    // mirrorChanged / excelChanged の4通り
    // ------------------------------------------------------------------

    [Fact(DisplayName = "どちらも変わっていない → 何もしない（基準の更新もない）")]
    public void どちらも変わっていない()
    {
        var plan = PlanOne(Mirror(Base), Excel(Base), State(Base, Base));

        plan.Action.Should().Be(VbaSyncAction.None);
        plan.Baseline.Should().BeNull();
        plan.Notice.Should().Be(VbaSyncNotice.None);
    }

    [Fact(DisplayName = "ミラー側だけが変わった → Push（未反映）")]
    public void ミラーだけ変わったらPush()
    {
        var plan = PlanOne(Mirror(Edited), Excel(Base), State(Base, Base));

        plan.Action.Should().Be(VbaSyncAction.Push);
        plan.RelativePath.Should().Be(Std);
        plan.Kind.Should().Be(VbaModuleKind.Standard);
        plan.Component.Should().Be("Module1");
    }

    [Fact(DisplayName = "Excel 側だけが変わった → Pull")]
    public void Excelだけ変わったらPull()
    {
        var plan = PlanOne(Mirror(Base), Excel(Edited), State(Base, Base));

        plan.Action.Should().Be(VbaSyncAction.Pull);
    }

    [Fact(DisplayName = "両方変わっていて本文が違う → 衝突")]
    public void 両方変わって違えば衝突()
    {
        var plan = PlanOne(Mirror(Edited), Excel(OtherEdit), State(Base, Base));

        plan.Action.Should().Be(VbaSyncAction.Conflict);
    }

    [Fact(DisplayName = "両方変わっていて本文が同じ → 基準だけ更新（mirrorHash=M、excelHash=E）")]
    public void 両方変わって同じなら基準だけ更新()
    {
        var plan = PlanOne(Mirror(Edited), Excel(Edited), State(Base, Base));

        plan.Action.Should().Be(VbaSyncAction.Rebaseline);
        plan.Baseline.Should().NotBeNull();
        plan.Baseline!.MirrorHash.Should().Be(VbaTextNormalizer.Hash(Edited));
        plan.Baseline.ExcelHash.Should().Be(VbaTextNormalizer.Hash(Edited));
        plan.Baseline.ExcelLooseHash.Should().Be(VbaTextNormalizer.LooseHash(Edited));
        plan.Baseline.SyncedAt.Should().Be(Now);
        plan.Baseline.DeletePending.Should().BeFalse();
    }

    [Fact(DisplayName = "両方変わっていて、ゆるい形（空白・大文字小文字無視）が同じ → 基準だけ更新（mirrorHash=M、excelHash=E は別の値）")]
    public void 両方変わってゆるい形が同じなら基準だけ更新()
    {
        var mirror = "Sub A()\r\n  x=1\r\nEnd Sub";
        var excel = "Sub A()\r\n  x = 1\r\nEnd Sub";

        var plan = PlanOne(Mirror(mirror), Excel(excel), State(Base, Base));

        plan.Action.Should().Be(VbaSyncAction.Rebaseline);
        plan.Baseline!.MirrorHash.Should().Be(VbaTextNormalizer.Hash(mirror));
        plan.Baseline.ExcelHash.Should().Be(VbaTextNormalizer.Hash(excel));
        plan.Baseline.MirrorHash.Should().NotBe(plan.Baseline.ExcelHash, "整形差のぶん、基準は2本に分かれる");
    }

    [Fact(DisplayName = "末尾の空行の数が違うだけの本文は変更と見なさない（ハッシュは末尾の CR/LF を除く）")]
    public void 末尾の空行の違いは変更ではない()
    {
        var plan = PlanOne(Mirror(Base + "\r\n\r\n"), Excel(Base), State(Base + "\r\n", Base + "\r\n\r\n\r\n"));

        plan.Action.Should().Be(VbaSyncAction.None);
    }

    // ------------------------------------------------------------------
    // 整形差だけの再基準化（VBE の大文字小文字の伝播）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "整形差だけ: 別のモジュールへの Push で識別子の大文字小文字だけ変わった Excel 側は、変更ではない（Pull しない。excelHash だけ更新）")]
    public void 大文字小文字の伝播は変更ではない()
    {
        var before = "Sub A()\r\n  myVar = 1\r\nEnd Sub";
        var propagated = "Sub A()\r\n  MyVar = 1\r\nEnd Sub";

        var plan = PlanOne(Mirror(before), Excel(propagated), State(before, before));

        plan.Action.Should().Be(VbaSyncAction.None, "Pull すると、履歴に無い変更をミラーが受けて取り消しが E301 になる");
        plan.Baseline.Should().NotBeNull();
        plan.Baseline!.ExcelHash.Should().Be(VbaTextNormalizer.Hash(propagated));
        plan.Baseline.MirrorHash.Should().Be(VbaTextNormalizer.Hash(before), "ミラー側の基準は変えない");
    }

    [Fact(DisplayName = "整形差だけ: スペースだけ変わった Excel 側も変更ではない")]
    public void 空白の整形差は変更ではない()
    {
        var plan = PlanOne(Mirror("x=1"), Excel("x = 1"), State("x=1", "x=1"));

        plan.Action.Should().Be(VbaSyncAction.None);
        plan.Baseline!.ExcelHash.Should().Be(VbaTextNormalizer.Hash("x = 1"));
    }

    [Fact(DisplayName = "整形差だけ + ミラー側が変わった → 偽の衝突にならず Push")]
    public void 整形差とミラーの変更はPush()
    {
        var before = "Sub A()\r\n  myVar = 1\r\nEnd Sub";
        var propagated = "Sub A()\r\n  MyVar = 1\r\nEnd Sub";

        var plan = PlanOne(Mirror(Edited), Excel(propagated), State(before, before));

        plan.Action.Should().Be(VbaSyncAction.Push);
    }

    [Fact(DisplayName = "整形差ではない本物の変更（文字が違う）は Pull")]
    public void 本物の変更はPull()
    {
        var plan = PlanOne(Mirror(Base), Excel("Sub B()\r\nEnd Sub"), State(Base, Base));

        plan.Action.Should().Be(VbaSyncAction.Pull);
    }

    // ------------------------------------------------------------------
    // 基準の記録がないとき
    // ------------------------------------------------------------------

    [Theory(DisplayName = "記録なし・ミラーにだけある（標準・クラス）→ 新規作成の Push")]
    [InlineData(VbaModuleKind.Standard, "Module2")]
    [InlineData(VbaModuleKind.Class, "Cls_A")]
    public void 記録なしミラーだけ_作成できる種別(VbaModuleKind kind, string name)
    {
        var plan = PlanOne(Mirror(Base, kind, name), null, null);

        plan.Action.Should().Be(VbaSyncAction.PushCreate);
        plan.Issue.Should().BeNull();
    }

    [Theory(DisplayName = "記録なし・ミラーにだけある（ドキュメント・フォーム）→ 作成不可（E808 の警告）。Push の対象にしない")]
    [InlineData(VbaModuleKind.Document, "Sheet9")]
    [InlineData(VbaModuleKind.Form, "UserForm9")]
    public void 記録なしミラーだけ_作成不可の種別(VbaModuleKind kind, string name)
    {
        var plan = PlanOne(Mirror(Base, kind, name), null, null);

        plan.Action.Should().Be(VbaSyncAction.NotCreatable);
        plan.Issue!.Code.Should().Be(ErrorCode.E808);
        plan.Issue.Severity.Should().Be(Severity.Warning);
    }

    [Theory(DisplayName = "記録なし・Excel にだけある → Pull（全種別）")]
    [InlineData(VbaModuleKind.Standard)]
    [InlineData(VbaModuleKind.Class)]
    [InlineData(VbaModuleKind.Document)]
    [InlineData(VbaModuleKind.Form)]
    public void 記録なしExcelだけ(VbaModuleKind kind)
    {
        var plan = PlanOne(null, Excel(Base, kind, "Thing"), null);

        plan.Action.Should().Be(VbaSyncAction.Pull);
    }

    [Fact(DisplayName = "記録なし・両方ある・本文が同じ → 基準を作るだけ")]
    public void 記録なし両方あり同じなら基準を作る()
    {
        var plan = PlanOne(Mirror(Base), Excel(Base, caption: null), null);

        plan.Action.Should().Be(VbaSyncAction.Rebaseline);
        plan.Baseline!.MirrorHash.Should().Be(VbaTextNormalizer.Hash(Base));
        plan.Baseline.ExcelHash.Should().Be(VbaTextNormalizer.Hash(Base));
    }

    [Fact(DisplayName = "記録なし・両方ある・本文が違う → 衝突（初回の食い違い）")]
    public void 記録なし両方あり違えば衝突()
    {
        var plan = PlanOne(Mirror(Edited), Excel(Base), null);

        plan.Action.Should().Be(VbaSyncAction.Conflict);
    }

    [Fact(DisplayName = "記録なし・ドキュメントのシート表示名（Caption）は新しい基準に引き継がれる")]
    public void 記録なしでもキャプションを引き継ぐ()
    {
        var plan = PlanOne(
            Mirror(Base, VbaModuleKind.Document, "Sheet1"),
            Excel(Base, VbaModuleKind.Document, "Sheet1", caption: "集計"),
            null);

        plan.Baseline!.Caption.Should().Be("集計");
    }

    // ------------------------------------------------------------------
    // 記録あり・片方が無い
    // ------------------------------------------------------------------

    [Fact(DisplayName = "記録あり・ミラーのファイルが無い・Excel にはある → 削除保留（自動 Pull で復活させない）")]
    public void ミラー消失は削除保留()
    {
        var plan = PlanOne(null, Excel(Base), State(Base, Base));

        plan.Action.Should().Be(VbaSyncAction.DeletePending);
        plan.Action.Should().NotBe(VbaSyncAction.Pull);
        plan.Notice.Should().Be(VbaSyncNotice.MirrorFileDeleted);
        plan.Baseline!.DeletePending.Should().BeTrue();
        plan.Baseline.MirrorHash.Should().Be(VbaTextNormalizer.Hash(Base), "基準そのものは変えず、印だけを付ける");
    }

    [Fact(DisplayName = "削除保留のまま何度計画しても Pull にならず、既に印が付いていれば基準の更新も要らない")]
    public void 削除保留は繰り返しても復活しない()
    {
        var plan = PlanOne(null, Excel(Base), State(Base, Base, deletePending: true));

        plan.Action.Should().Be(VbaSyncAction.DeletePending);
        plan.Baseline.Should().BeNull();
    }

    [Fact(DisplayName = "削除保留の解除: ミラーのファイルが再び現れ、変わっていなければ印を外して通常の判定（何もしない）に戻る")]
    public void ミラーが再び現れたら印を外す()
    {
        var plan = PlanOne(Mirror(Base), Excel(Base), State(Base, Base, deletePending: true));

        plan.Action.Should().Be(VbaSyncAction.None);
        plan.Baseline!.DeletePending.Should().BeFalse();
    }

    [Fact(DisplayName = "削除保留の解除: ミラーのファイルが再び現れて変わっていれば Push の判定に戻る")]
    public void ミラーが再び現れて変わっていればPush()
    {
        var plan = PlanOne(Mirror(Edited), Excel(Base), State(Base, Base, deletePending: true));

        plan.Action.Should().Be(VbaSyncAction.Push);
    }

    [Fact(DisplayName = "記録あり・ミラーにある・Excel に無い・ミラーが変わっていない → 何もしない（警告の印だけ）")]
    public void Excel消失でミラー未変更は何もしない()
    {
        var plan = PlanOne(Mirror(Base), null, State(Base, Base));

        plan.Action.Should().Be(VbaSyncAction.None);
        plan.Notice.Should().Be(VbaSyncNotice.ExcelModuleMissing);
    }

    [Fact(DisplayName = "記録あり・Excel に無い・ミラーが変わっている（標準）→ 再作成の Push")]
    public void Excel消失でミラー変更_標準は再作成()
    {
        var plan = PlanOne(Mirror(Edited), null, State(Base, Base));

        plan.Action.Should().Be(VbaSyncAction.PushCreate);
        plan.Notice.Should().Be(VbaSyncNotice.ExcelModuleMissing);
    }

    [Fact(DisplayName = "記録あり・Excel に無い・ミラーが変わっている（クラス）→ 再作成の Push")]
    public void Excel消失でミラー変更_クラスは再作成()
    {
        var plan = PlanOne(
            Mirror(Edited, VbaModuleKind.Class, "Cls"), null, State(Base, Base, VbaModuleKind.Class, "Cls"));

        plan.Action.Should().Be(VbaSyncAction.PushCreate);
    }

    [Theory(DisplayName = "記録あり・Excel に無い・ミラーが変わっている（ドキュメント・フォーム）→ 作成不可")]
    [InlineData(VbaModuleKind.Document, "Sheet1")]
    [InlineData(VbaModuleKind.Form, "UserForm1")]
    public void Excel消失でミラー変更_作成不可の種別(VbaModuleKind kind, string name)
    {
        var plan = PlanOne(Mirror(Edited, kind, name), null, State(Base, Base, kind, name));

        plan.Action.Should().Be(VbaSyncAction.NotCreatable);
        plan.Issue!.Code.Should().Be(ErrorCode.E808);
    }

    [Fact(DisplayName = "記録あり・両方無い → 記録を破棄")]
    public void 両方無ければ記録を破棄()
    {
        var plan = PlanOne(null, null, State(Base, Base));

        plan.Action.Should().Be(VbaSyncAction.DiscardRecord);
    }

    // ------------------------------------------------------------------
    // 種別の取り違え・名前の扱い
    // ------------------------------------------------------------------

    [Fact(DisplayName = "同名でも種別が違えば別のファイル（Classes/Foo.cls と Documents/Foo.cls）。ただし Excel 側に同名があるので、クラスの新規作成は NotCreatable で止める（不具合 1）")]
    public void 同名でも種別が違えば別物()
    {
        var plans = VbaSyncPlanner.Plan(
            new[] { Mirror(Base, VbaModuleKind.Class, "Foo") },
            new[] { Excel(Base, VbaModuleKind.Document, "Foo") },
            null, Now);

        plans.Modules.Select(m => (m.RelativePath, m.Action)).Should().BeEquivalentTo(new[]
        {
            // 以前は PushCreate だった。Excel の Document の Foo を、ブリッジが名前だけで引いて上書きしてしまうため、止める。
            ("Classes/Foo.cls", VbaSyncAction.NotCreatable),
            ("Documents/Foo.cls", VbaSyncAction.Pull),
        });
    }

    [Fact(DisplayName = "パスの大文字小文字が違っても同じモジュール（記録のキーが小文字でも一致する）")]
    public void 大文字小文字を区別しない()
    {
        var state = new VbaSyncState().WithModule(
            "modules/module1.bas",
            VbaModuleState.Create(
                VbaModuleKind.Standard, "Module1", null, VbaTextNormalizer.Hash(Base), VbaTextNormalizer.Hash(Base),
                VbaTextNormalizer.LooseHash(Base), Now));

        var plan = PlanOne(Mirror(Edited), Excel(Base), state);

        plan.Action.Should().Be(VbaSyncAction.Push, "別のモジュールとして扱うと、衝突や二重の新規作成になる");
    }

    [Theory(DisplayName = "Excel 側のコンポーネント名がミラーのファイルにできない（Windows の予約名）→ 対象外（E810 の警告）。Pull しない")]
    [InlineData("CON")]
    [InlineData("aux")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void 予約名のExcelコンポーネントは対象外(string name)
    {
        var plan = PlanOne(null, Excel(Base, VbaModuleKind.Standard, name), null);

        plan.Action.Should().Be(VbaSyncAction.Unsupported);
        plan.Issue!.Code.Should().Be(ErrorCode.E810);
        plan.Issue.Severity.Should().Be(Severity.Warning);
    }

    // ------------------------------------------------------------------
    // 計画全体
    // ------------------------------------------------------------------

    [Fact(DisplayName = "複数のモジュールを一度に判定し、並びはパスの順で決まる（入力の順に依存しない）")]
    public void 複数モジュールの判定と順序()
    {
        var state = State(Base, Base, name: "Module1")
            .WithModule("Modules/Module2.bas", VbaModuleState.Create(
                VbaModuleKind.Standard, "Module2", null, VbaTextNormalizer.Hash(Base), VbaTextNormalizer.Hash(Base),
                VbaTextNormalizer.LooseHash(Base), Now));

        var plan = VbaSyncPlanner.Plan(
            new[] { Mirror(Edited, name: "Module2"), Mirror(Base, name: "Module1") },
            new[] { Excel(Base, name: "Module2"), Excel(Edited, name: "Module1"), Excel(Base, name: "Module0") },
            state, Now);

        plan.Modules.Select(m => (m.Component, m.Action)).Should().Equal(
            ("Module0", VbaSyncAction.Pull),
            ("Module1", VbaSyncAction.Pull),
            ("Module2", VbaSyncAction.Push));
        plan.HasWork.Should().BeTrue();
        plan.HasConflicts.Should().BeFalse();
    }

    [Fact(DisplayName = "HasConflicts／HasWork: 衝突があれば真、基準の更新だけなら HasWork は偽")]
    public void 計画の集計()
    {
        var conflict = VbaSyncPlanner.Plan(new[] { Mirror(Edited) }, new[] { Excel(OtherEdit) }, State(Base, Base), Now);
        conflict.HasConflicts.Should().BeTrue();
        conflict.HasWork.Should().BeTrue();

        var rebaselineOnly = VbaSyncPlanner.Plan(new[] { Mirror(Edited) }, new[] { Excel(Edited) }, State(Base, Base), Now);
        rebaselineOnly.HasConflicts.Should().BeFalse();
        rebaselineOnly.HasWork.Should().BeFalse("基準の更新だけで、Pull・Push・利用者の判断は要らない");

        VbaSyncPlanner.Plan(Array.Empty<VbaMirrorModule>(), Array.Empty<VbaExcelModule>(), null, Now).Modules.Should().BeEmpty();
    }

    [Fact(DisplayName = "ApplyLocalChanges: I/O なしの基準の更新を反映し、破棄の指示で記録を消し、ほかは触らない")]
    public void ローカルな基準の更新を反映する()
    {
        var state = State(Base, Base, name: "Module1")
            .WithModule("Modules/Gone.bas", VbaModuleState.Create(
                VbaModuleKind.Standard, "Gone", null, "x", "y", "z", Now))
            .WithModule("Modules/Keep.bas", VbaModuleState.Create(
                VbaModuleKind.Standard, "Keep", null, VbaTextNormalizer.Hash(Base), VbaTextNormalizer.Hash(Base),
                VbaTextNormalizer.LooseHash(Base), Now.AddDays(-3)));

        var plan = VbaSyncPlanner.Plan(
            new[] { Mirror(Edited, name: "Module1"), Mirror(Base, name: "Keep") },
            new[] { Excel(Edited, name: "Module1"), Excel(Base, name: "Keep") },
            state, Now);

        var applied = plan.ApplyLocalChanges(state);

        applied.Modules.Should().NotContainKey("Modules/Gone.bas");
        applied.Modules["Modules/Module1.bas"].MirrorHash.Should().Be(VbaTextNormalizer.Hash(Edited));
        applied.Modules["Modules/Keep.bas"].SyncedAt.Should().Be(Now.AddDays(-3), "変更のないモジュールは触らない");
    }

    [Fact(DisplayName = "計画は純粋: 入力の状態を書き換えない")]
    public void 入力を書き換えない()
    {
        var state = State(Base, Base);
        var before = state.Modules["Modules/Module1.bas"];

        _ = VbaSyncPlanner.Plan(new[] { Mirror(Edited) }, new[] { Excel(Edited) }, state, Now);

        state.Modules["Modules/Module1.bas"].Should().Be(before);
    }
}
