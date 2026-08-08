using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 実機不具合対応（2026-08）: 「パッチ適用によってファイルが1つ完全に消えた」報告への回帰テスト。
/// <see cref="SafeFileWriter"/> は静的クラスで低レベルの File.Replace / File.Move に直接依存する
/// ため、Windows実機でしか自然には起きない「部分的な失敗」「例外を投げずに完了したのに直後に
/// 内容が失われる」状況を、フェイクの <see cref="IPrimaryReplaceOp"/>/<see cref="IMoveOp"/> を
/// 注入する内部限定オーバーロードで再現する（本番コードにテスト専用の分岐は増やさない）。
/// <para>
/// 検証する2つの保証:
/// (A) 一次経路・退避方式のいずれが失敗しても、最終手段（メモリ上の内容を直接書き戻す）まで
///     必ず試み、対象ファイルが「存在しない」状態のまま終わらないこと（新規作成が最後まで
///     すべて失敗した場合を除く）。
/// (B) 書き込みが例外なく完了しても、直後にディスク上の内容を検証し、一致しなければ
///     「成功した」と報告しないこと（実機で観測された、manifest.jsonが成功として記録した
///     のに実体が無かった不具合への対応）。
/// </para>
/// </summary>
public class SafeFileWriterTests
{
    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    // ------------------------------------------------------------------
    // (A) 消失防止: 一次経路・退避方式が失敗しても最終手段でファイルを残す
    // ------------------------------------------------------------------

    [Fact(DisplayName = "一次経路が失敗し退避方式も失敗しても、最終手段で新しい内容が書き戻される")]
    public async Task 一次経路と退避方式が両方失敗しても最終手段で復旧する()
    {
        using var ws = new TempWorkspace();
        var fullPath = ws.WriteText("lf-sample.txt", "旧内容");
        var newContent = Utf8("新しい内容");

        var primaryOp = new AlwaysThrowPrimaryOp();
        var moveOp = new AlwaysThrowMoveOp();

        var result = await SafeFileWriter.ReplaceAsync(fullPath, newContent, primaryOp, moveOp, default);

        result.IsSuccess.Should().BeTrue("メモリ上に残っている内容を直接書き戻す最終手段が働くべき");
        File.Exists(fullPath).Should().BeTrue("対象ファイルが消えたまま終わってはならない");
        (await File.ReadAllBytesAsync(fullPath)).Should().Equal(newContent);

        primaryOp.CallCount.Should().Be(3, "一次経路は状態が変化していない限り3回までリトライされる");
        moveOp.CallCount.Should().Be(3, "退避方式の最初のMove（対象→退避）も安全にリトライされる");

        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.GetFiles(directory).Where(f => Path.GetFileName(f).Contains("graft-tmp")).Should().BeEmpty();
        Directory.GetFiles(directory).Where(f => Path.GetFileName(f).Contains("graft-bak")).Should().BeEmpty(
            "退避（対象→バックアップ）自体がMoveの失敗で行えていないため、退避ファイルは残らない");

        result.Issues.Should().Contain(i => i.Severity == Severity.Warning,
            "緊急の書き戻しで復旧したことは警告として残す");
    }

    [Fact(DisplayName = "一次経路が対象・一時ファイルの両方を消した後に失敗しても、最終手段で復旧する（実機不具合の再現）")]
    public async Task 一次経路が対象を消した後に失敗しても最終手段で復旧する()
    {
        using var ws = new TempWorkspace();
        var fullPath = ws.WriteText("lf-sample.txt", "旧内容");
        var newContent = Utf8("新しい内容");

        // Windowsの File.Replace が内部の複数手順の途中で失敗し、対象ファイルと一時ファイルの
        // 双方が失われた状態で例外を投げる最悪ケースを模す（実機で観測された状況の仮説）。
        var primaryOp = new CorruptsThenThrowsPrimaryOp();

        var result = await SafeFileWriter.ReplaceAsync(fullPath, newContent, primaryOp, RealMoveOp.Instance, default);

        result.IsSuccess.Should().BeTrue();
        File.Exists(fullPath).Should().BeTrue("対象ファイルが消えたまま終わってはならない");
        (await File.ReadAllBytesAsync(fullPath)).Should().Equal(newContent);

        primaryOp.CallCount.Should().Be(1,
            "対象・一時ファイルの状態が直前の試行から変化した時点で、部分的に壊れた可能性があるとして" +
            "即座にリトライを諦めるべき（素朴なリトライで壊れた状態のまま次の試行に入ってはならない）");

        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.GetFiles(directory).Where(f => Path.GetFileName(f).Contains("graft-tmp")).Should().BeEmpty();
        Directory.GetFiles(directory).Where(f => Path.GetFileName(f).Contains("graft-bak")).Should().BeEmpty();
    }

    [Fact(DisplayName = "新規作成対象で一次経路・退避方式が両方失敗しても、最終手段でファイルが作成される")]
    public async Task 新規作成でも一次経路と退避方式が両方失敗すれば最終手段で作成される()
    {
        using var ws = new TempWorkspace();
        var fullPath = Path.Combine(ws.RootPath, "created.txt");
        var content = Utf8("新規作成された内容");

        var primaryOp = new AlwaysThrowPrimaryOp();
        var moveOp = new AlwaysThrowMoveOp();

        File.Exists(fullPath).Should().BeFalse("前提: 対象は元々存在しない");

        var result = await SafeFileWriter.ReplaceAsync(fullPath, content, primaryOp, moveOp, default);

        result.IsSuccess.Should().BeTrue();
        File.Exists(fullPath).Should().BeTrue("新規作成対象でも最終手段まで尽くしてファイルを残すべき");
        (await File.ReadAllBytesAsync(fullPath)).Should().Equal(content);

        primaryOp.CallCount.Should().Be(3);
        // 新規作成（targetExisted=false）のため退避（対象→バックアップ）は試みられず、
        // 一時ファイル→対象のMoveのみが3回リトライされる。
        moveOp.CallCount.Should().Be(3);
    }

    // ------------------------------------------------------------------
    // (B) 書き込み後の検証: 例外なく完了しても、内容が一致しなければ成功と報告しない
    // ------------------------------------------------------------------

    [Fact(DisplayName = "書き込み直後にファイルが失われても検証で検知し、やり直して復旧する")]
    public async Task 書き込み直後の消失を検証で検知しやり直して復旧する()
    {
        using var ws = new TempWorkspace();
        var fullPath = ws.WriteText("lf-sample.txt", "旧内容");
        var newContent = Utf8("新しい内容");

        // 一次経路自体は例外を投げずに完了する（＝Windowsから見れば「成功」）が、その直後に
        // 対象ファイルが失われる状況（アンチウイルス等の外部要因を想定）を1回だけ模す。
        var primaryOp = new SilentCorruptionPrimaryOp(corruptOnFirstNCalls: 1);

        var result = await SafeFileWriter.ReplaceAsync(fullPath, newContent, primaryOp, RealMoveOp.Instance, default);

        result.IsSuccess.Should().BeTrue("1回の自己修復（書き直し）で復旧できるはず");
        File.Exists(fullPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(fullPath)).Should().Equal(newContent);
        primaryOp.CallCount.Should().Be(2, "1回目の検証失敗を受けて2回目の書き込みが行われるはず");

        result.Issues.Should().Contain(i => i.Severity == Severity.Warning && i.Code == ErrorCode.E402,
            "「成功と判定されたのに直後の検証で不一致だった」ことは警告として記録されるべき");
    }

    [Fact(DisplayName = "書き込み直後の内容がバイト数は同じでも異なる場合、ハッシュ照合で検知される")]
    public async Task サイズが同じでも内容が異なれば検証で検知される()
    {
        using var ws = new TempWorkspace();
        var fullPath = ws.WriteText("lf-sample.txt", "旧内容ですよ");
        var newContent = Utf8("新しい内容です"); // 差し替え先と同バイト数にする

        var primaryOp = new SilentContentSwapPrimaryOp(newContent.Length, corruptOnFirstNCalls: 1);

        var result = await SafeFileWriter.ReplaceAsync(fullPath, newContent, primaryOp, RealMoveOp.Instance, default);

        result.IsSuccess.Should().BeTrue();
        (await File.ReadAllBytesAsync(fullPath)).Should().Equal(newContent,
            "サイズ一致だけでは見抜けない内容の差し替えも、ハッシュ照合で検知してやり直せているはず");
        primaryOp.CallCount.Should().Be(2);
    }

    [Fact(DisplayName = "外部要因が持続的にファイルを壊し続ける場合は、成功したと偽らずに失敗として報告する")]
    public async Task 持続的な外部干渉では成功と偽らず失敗を返す()
    {
        using var ws = new TempWorkspace();
        var fullPath = ws.WriteText("lf-sample.txt", "旧内容");
        var newContent = Utf8("新しい内容");

        // 毎回「例外なく完了」した直後にファイルを壊し続ける、Graftでは制御不能な外部干渉を模す。
        var primaryOp = new SilentCorruptionPrimaryOp(corruptOnFirstNCalls: int.MaxValue);

        var result = await SafeFileWriter.ReplaceAsync(fullPath, newContent, primaryOp, RealMoveOp.Instance, default);

        // ここで最も重要な性質: 「例外が出なかった＝成功」と偽らないこと。
        // Graft側の書き込みが正しく行えたと嘘の報告をしないのが本質であり、
        // 外部から書き込むそばから壊され続ける状況そのものはアプリ単体では防げない。
        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(i => i.Code == ErrorCode.E402);
        result.Errors.First().Detail.Should().Contain("確認");
    }

    // ------------------------------------------------------------------
    // フェイク実装
    // ------------------------------------------------------------------

    /// <summary>常に例外を投げ、ファイルには一切手を付けない（一時的なロック等を模す）。</summary>
    private sealed class AlwaysThrowPrimaryOp : IPrimaryReplaceOp
    {
        public int CallCount { get; private set; }

        public void Execute(string extendedTarget, string extendedTemp, bool targetExisted)
        {
            CallCount++;
            throw new IOException("テスト用: 常に失敗するフェイク（一次経路）");
        }
    }

    private sealed class AlwaysThrowMoveOp : IMoveOp
    {
        public int CallCount { get; private set; }

        public void Move(string extendedSource, string extendedDestination)
        {
            CallCount++;
            throw new IOException("テスト用: 常に失敗するフェイク（Move）");
        }
    }

    /// <summary>
    /// Windowsの File.Replace が内部の複数手順の途中で失敗し、対象ファイルと一時ファイルの
    /// 双方を失った状態で例外を投げる最悪ケースを模す。
    /// </summary>
    private sealed class CorruptsThenThrowsPrimaryOp : IPrimaryReplaceOp
    {
        public int CallCount { get; private set; }

        public void Execute(string extendedTarget, string extendedTemp, bool targetExisted)
        {
            CallCount++;
            if (File.Exists(extendedTarget)) File.Delete(extendedTarget);
            if (File.Exists(extendedTemp)) File.Delete(extendedTemp);
            throw new IOException("テスト用: 対象・一時ファイルの双方を失った状態で失敗するフェイク");
        }
    }

    /// <summary>
    /// 実際の置換は本物のRealPrimaryReplaceOpに委譲して成功させたうえで、例外を投げずに
    /// 対象ファイルを削除する（＝呼び出し側からは「成功」に見えるが実体が失われる）。
    /// アンチウイルス等、Graftのプロセス外の要因による直後の消失を模す。
    /// </summary>
    private sealed class SilentCorruptionPrimaryOp : IPrimaryReplaceOp
    {
        private readonly int _corruptOnFirstNCalls;
        public int CallCount { get; private set; }

        public SilentCorruptionPrimaryOp(int corruptOnFirstNCalls) => _corruptOnFirstNCalls = corruptOnFirstNCalls;

        public void Execute(string extendedTarget, string extendedTemp, bool targetExisted)
        {
            CallCount++;
            RealPrimaryReplaceOp.Instance.Execute(extendedTarget, extendedTemp, targetExisted);
            if (CallCount <= _corruptOnFirstNCalls)
            {
                File.Delete(extendedTarget);
            }
        }
    }

    /// <summary>
    /// SilentCorruptionPrimaryOpの亜種。ファイルを消す代わりに、同じバイト数だが異なる内容へ
    /// 差し替える（存在確認・サイズ照合だけでは見抜けない、ハッシュ照合固有の検知力を確認する）。
    /// </summary>
    private sealed class SilentContentSwapPrimaryOp : IPrimaryReplaceOp
    {
        private readonly int _sameLength;
        private readonly int _corruptOnFirstNCalls;
        public int CallCount { get; private set; }

        public SilentContentSwapPrimaryOp(int sameLength, int corruptOnFirstNCalls)
        {
            _sameLength = sameLength;
            _corruptOnFirstNCalls = corruptOnFirstNCalls;
        }

        public void Execute(string extendedTarget, string extendedTemp, bool targetExisted)
        {
            CallCount++;
            RealPrimaryReplaceOp.Instance.Execute(extendedTarget, extendedTemp, targetExisted);
            if (CallCount <= _corruptOnFirstNCalls)
            {
                var different = Enumerable.Repeat((byte)'X', _sameLength).ToArray();
                File.WriteAllBytes(extendedTarget, different);
            }
        }
    }
}
