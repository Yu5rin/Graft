using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// VBA プロジェクトのロック（E803）の窓の文面と選択肢（<see cref="VbaLockedText"/>）の純ロジックのテスト。
/// 場面ごとの既定（Enter・Esc・× の結果）と、窓を閉じない「VBE を開く」が選択肢の並びに入っていることを固定する。
/// </summary>
public class VbaLockedTextTests
{
    private static Task Noop() => Task.CompletedTask;

    [Fact(DisplayName = "適用の後: 既定は「中止してこの適用（rN）を取り消す」。VBE を開くは窓を閉じない操作で、順序は 取り消す・VBE・もう一度・残す")]
    public void 適用の後の選択肢()
    {
        var options = VbaLockedText.Options(VbaLockedScene.AfterApply, 7, Noop);

        options.Select(o => o.Id).Should().Equal(VbaLockedText.UndoId, VbaLockedText.VbeId, VbaLockedText.RetryId, VbaLockedText.KeepId);
        options.Single(o => o.IsDefault).Id.Should().Be(VbaLockedText.UndoId);
        options[0].Label.Should().Be("中止してこの適用（r7）を取り消す");
        options[1].KeepOpenAction.Should().NotBeNull("窓は閉じない");
        options[1].IsDefault.Should().BeFalse();
        options.Where(o => o.Id != VbaLockedText.VbeId).Should().OnlyContain(o => o.KeepOpenAction == null);
        new VbaChoiceRequest("t", "m", options).Default.Id.Should().Be(VbaLockedText.UndoId, "Esc・× もこの結果");
    }

    [Fact(DisplayName = "適用の前: 既定は「中止してパッチを破棄する」。選択肢は 破棄・VBE・もう一度試す・ファイルだけ適用する")]
    public void 適用の前の選択肢()
    {
        var options = VbaLockedText.Options(VbaLockedScene.BeforeApply, null, Noop);

        options.Select(o => o.Id).Should().Equal(VbaLockedText.DiscardId, VbaLockedText.VbeId, VbaLockedText.RetryId, VbaLockedText.FileOnlyId);
        options.Single(o => o.IsDefault).Id.Should().Be(VbaLockedText.DiscardId);
        options[2].Label.Should().Be("解除したのでもう一度試す");
    }

    [Fact(DisplayName = "手動の反映・取込: 取り消しも破棄も出さない（既定は閉じる）。適用の後でも、取り消す適用が無ければ取り消しは出ない")]
    public void 取り消しを出さない場面()
    {
        foreach (var scene in new[] { VbaLockedScene.Manual, VbaLockedScene.Info })
        {
            var options = VbaLockedText.Options(scene, null, Noop);
            options.Select(o => o.Id).Should().NotContain(new[] { VbaLockedText.UndoId, VbaLockedText.DiscardId });
            options.Single(o => o.IsDefault).Id.Should().Be(VbaLockedText.KeepId);
            options.Select(o => o.Id).Should().Contain(VbaLockedText.VbeId);
        }

        VbaLockedText.Options(VbaLockedScene.Manual, null, Noop).Select(o => o.Id).Should().Contain(VbaLockedText.RetryId);
        VbaLockedText.Options(VbaLockedScene.AfterApply, null, Noop).Select(o => o.Id).Should().NotContain(VbaLockedText.UndoId);
    }

    [Fact(DisplayName = "本文: 解除の方法・取り消す適用の番号・「以前から溜まっていた未反映は戻さない」・理由が入る")]
    public void 本文()
    {
        var text = VbaLockedText.Message(VbaLockedScene.AfterApply, "r7 の変更は適用済みです。", 7, "E803 理由");

        text.Should().Contain("r7 の変更は適用済みです。").And.Contain("Alt+F11").And.Contain("この適用（r7）だけを取り消して")
            .And.Contain("以前から溜まっていた未反映は戻しません").And.Contain("E803 理由");
        VbaLockedText.Title.Should().Be("VBA プロジェクトがロックされています");
    }
}
