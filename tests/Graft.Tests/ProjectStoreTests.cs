using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書3章（プロジェクト管理）の単体テスト。<see cref="ProjectStore"/> のID生成・並べ替え・
/// 未接続検出・世代補正と、<see cref="ProjectOverrideResolver"/> の overrides 解決を検証する。
/// </summary>
public class ProjectStoreTests
{
    // ------------------------------------------------------------------
    // 3.1 ID生成
    // ------------------------------------------------------------------

    [Fact(DisplayName = "CreateIdは末尾スラッシュの有無で同じIDになる")]
    public void ID生成は末尾スラッシュの有無で変わらない()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("myproj");

        var idWithoutSlash = ProjectStore.CreateId(dir);
        var idWithSlash = ProjectStore.CreateId(dir + "/");

        idWithSlash.Should().Be(idWithoutSlash);
    }

    [Fact(DisplayName = "CreateIdは同じパスに対して常に同じIDを返す（決定的）")]
    public void ID生成は決定的である()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("stable");

        ProjectStore.CreateId(dir).Should().Be(ProjectStore.CreateId(dir));
    }

    [Fact(DisplayName = "Linux上ではCreateIdは大文字小文字が異なるパスを別プロジェクトとして扱うべき（仕様書v2.1 3章）")]
    public void ID生成はLinuxでは大文字小文字を区別すべき()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windowsのファイルシステム（NTFS既定）は大文字小文字を区別しないため、この2つの
            // ディレクトリは同一パス扱いになりそもそも両方は実在できない。この検証はLinux
            // （ext4等の大文字小文字を区別するファイルシステム）固有の要件であり、Windows側の
            // 正しい挙動（大文字小文字違いを同一プロジェクトとして扱う）は
            // ID生成はWindowsでは大文字小文字を区別しない で別途検証する。
            return;
        }

        using var ws = new TempWorkspace();
        var lower = ws.CreateDirectory("caseproj");
        var upperCandidate = Path.Combine(ws.RootPath, "CASEPROJ");
        Directory.CreateDirectory(upperCandidate);
        // Linux（ext4等の大文字小文字を区別するファイルシステム）では、この2つは別ディレクトリ
        // として実在する。プロジェクトIDの正規化はプラットフォームの比較規則に委ねるべき
        // （仕様書v2.1 3章「プロジェクトIDの生成に使う正規化も同様に、比較規則をプラットフォームへ委ねる」）。
        Directory.Exists(lower).Should().BeTrue();
        Directory.Exists(upperCandidate).Should().BeTrue();

        var idLower = ProjectStore.CreateId(lower);
        var idUpper = ProjectStore.CreateId(upperCandidate);

        idLower.Should().NotBe(idUpper,
            "ProjectStore.NormalizeRootForHashが常にToLowerInvariant()で正規化しているため、" +
            "Linux上で大文字小文字違いの別ディレクトリが同一プロジェクトID扱いになってしまう" +
            "（仕様書v2.1 3章の要件に反する）。");
    }

    [Fact(DisplayName = "Windows上ではCreateIdは大文字小文字が異なるパスを同一プロジェクトとして扱うべき（仕様書v2.1 3章）")]
    public void ID生成はWindowsでは大文字小文字を区別しない()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Windowsのパス比較規則（大文字小文字を区別しない）を前提とした検証のため、
            // Windows以外では成立しない（上のLinux版テストと対になる）。
            return;
        }

        using var ws = new TempWorkspace();
        var lower = ws.CreateDirectory("caseproj2");
        var upperPath = Path.Combine(ws.RootPath, "CASEPROJ2");

        var idLower = ProjectStore.CreateId(lower);
        var idUpper = ProjectStore.CreateId(upperPath);

        idLower.Should().Be(idUpper,
            "Windowsのファイルシステムは大文字小文字を区別しないため、大文字小文字違いの" +
            "パスは同一プロジェクトとして扱う必要がある（仕様書v2.1 3章）。");
    }

    // ------------------------------------------------------------------
    // 3.2 並べ替え・未接続検出
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Sortはピン留めを先頭に、次に最終適用日時の降順で並べる")]
    public void 並べ替えはピン留め優先で最終適用日時降順()
    {
        var now = DateTimeOffset.Now;
        // NextRevision>1にしておかないとEffectiveLastAppliedAtの移行救済フォールバックが
        // 効かずLastAppliedAtがそのまま使われる（このテストではLastAppliedAtを明示しているため
        // 実質どちらでもよいが、実際の適用済みプロジェクトを模してNextRevisionも進めておく）。
        var a = new Project { Id = "p_a", Name = "A", Pinned = false, LastAppliedAt = now, NextRevision = 2 };
        var b = new Project { Id = "p_b", Name = "B", Pinned = true, LastAppliedAt = now.AddDays(-10), NextRevision = 2 };
        var c = new Project { Id = "p_c", Name = "C", Pinned = false, LastAppliedAt = now.AddDays(-1), NextRevision = 2 };
        var d = new Project { Id = "p_d", Name = "D", Pinned = true, LastAppliedAt = now, NextRevision = 2 };

        var sorted = ProjectStore.Sort(new[] { a, b, c, d });

        sorted.Select(p => p.Id).Should().ContainInOrder("p_d", "p_b", "p_a", "p_c");
    }

    // ------------------------------------------------------------------
    // 不具合3: プロジェクト一覧の並び順（ピン留め＞最終適用日時＞未適用は最下部）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "追加直後（一度も適用していない）のプロジェクトは最下部に来る")]
    public void 未適用のプロジェクトは最下部に来る()
    {
        var now = DateTimeOffset.Now;
        var applied = new Project { Id = "p_applied", Name = "適用済み", LastAppliedAt = now.AddDays(-30), NextRevision = 2 };
        var justAdded = new Project { Id = "p_new", Name = "追加直後", LastUsedAt = now, NextRevision = 1, LastAppliedAt = null };

        var sorted = ProjectStore.Sort(new[] { justAdded, applied });

        sorted.Select(p => p.Id).Should().ContainInOrder(new[] { "p_applied", "p_new" },
            "追加直後（LastUsedAtが最新でもLastAppliedAtが無い）プロジェクトは、" +
            "適用日時が古いプロジェクトより下に来なければならない");
    }

    [Fact(DisplayName = "パッチを適用すると一覧の上へ移動する")]
    public async Task パッチを適用すると上へ移動する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var oldRoot = ws.CreateDirectory("old-applied");
        var newRoot = ws.CreateDirectory("new-project");

        await store.SaveAsync(new[]
        {
            new Project
            {
                Id = "p_old", Name = "既に適用済み", Root = oldRoot,
                LastAppliedAt = DateTimeOffset.Now.AddDays(-5), NextRevision = 2,
            },
            new Project { Id = "p_target", Name = "これから適用", Root = newRoot, NextRevision = 1 },
        });

        var beforeSorted = ProjectStore.Sort((await store.LoadAsync()).Value);
        beforeSorted.Select(p => p.Id).Should().ContainInOrder(new[] { "p_old", "p_target" },
            "適用前はp_targetが未適用のため最下部にいるはず");

        var marked = await store.MarkAppliedAsync("p_target", DateTimeOffset.Now);
        marked.IsSuccess.Should().BeTrue();

        var afterSorted = ProjectStore.Sort((await store.LoadAsync()).Value);
        afterSorted.Select(p => p.Id).Should().ContainInOrder(new[] { "p_target", "p_old" },
            "MarkAppliedAsync後はp_targetの方が最終適用日時が新しいため上に来るはず");
    }

    [Fact(DisplayName = "ピン留めは最終適用日時に関わらず常に最上位に来る")]
    public void ピン留めは適用日時に関わらず最上位()
    {
        var now = DateTimeOffset.Now;
        var pinnedButNeverApplied = new Project { Id = "p_pinned", Name = "ピン留め・未適用", Pinned = true, NextRevision = 1 };
        var appliedRecently = new Project { Id = "p_recent", Name = "非ピン留め・最近適用", LastAppliedAt = now, NextRevision = 2 };

        var sorted = ProjectStore.Sort(new[] { appliedRecently, pinnedButNeverApplied });

        sorted.Select(p => p.Id).Should().ContainInOrder(new[] { "p_pinned", "p_recent" },
            "ピン留め・未適用でも、非ピン留め・適用済みより上に来なければならない");
    }

    [Fact(DisplayName = "旧形式（lastAppliedAtキーの無い）projects.jsonを読み込んでも順序が壊れない")]
    public async Task 旧形式のprojects_jsonを読み込んでも順序は壊れない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        paths.EnsureCoreDirectoriesExist();
        var rootA = ws.CreateDirectory("legacy-a");
        var rootB = ws.CreateDirectory("legacy-b");
        // lastAppliedAtキー自体を持たない旧形式。p_legacy_usedはnextRevisionが進んでおり
        // 過去に適用を試みたことがある（＝LastUsedAtを移行救済の代用として使うべき）、
        // p_legacy_freshはnextRevisionが1のまま（＝一度も適用していない、最下部が正しい）。
        var legacyJson = $$"""
            {"projects":[
                {"id":"p_legacy_used","name":"旧・使用済み","root":"{{rootA.Replace("\\", "\\\\")}}",
                 "lastUsedAt":"2020-01-01T00:00:00+00:00","nextRevision":5},
                {"id":"p_legacy_fresh","name":"旧・未使用","root":"{{rootB.Replace("\\", "\\\\")}}",
                 "lastUsedAt":"2024-06-01T00:00:00+00:00","nextRevision":1}
            ]}
            """;
        await File.WriteAllTextAsync(paths.ProjectsFilePath, legacyJson);

        var store = new ProjectStore(paths);
        var loaded = await store.LoadAsync();

        loaded.IsSuccess.Should().BeTrue();
        var legacyUsed = loaded.Value.Single(p => p.Id == "p_legacy_used");
        var legacyFresh = loaded.Value.Single(p => p.Id == "p_legacy_fresh");
        legacyUsed.LastAppliedAt.Should().BeNull("旧形式のJSONにキーが無いため既定値nullで読めるはず");
        legacyFresh.LastAppliedAt.Should().BeNull();

        var sorted = ProjectStore.Sort(loaded.Value);

        // p_legacy_freshはlastUsedAtの値自体はp_legacy_usedより新しいが、nextRevision=1
        // （一度も適用したことがない）なので移行救済の代用が効かず、最下部に来るはず。
        // p_legacy_usedはnextRevision=5（過去に適用を試みたことがある旧プロジェクト）なので
        // LastUsedAtを代用値として使い、驚くほど下に落ちることなく妥当な位置に来る。
        sorted.Select(p => p.Id).Should().ContainInOrder(new[] { "p_legacy_used", "p_legacy_fresh" },
            "移行救済（NextRevision>1のときのみLastUsedAtを代用）が働き、既存の使用歴が" +
            "あるプロジェクトが最下部に落ちてはならない");
    }

    // ------------------------------------------------------------------
    // 要望対応: ピン留め済み同士は「ピン留めした順」（昇順）で並ぶ
    // ------------------------------------------------------------------

    [Fact(DisplayName = "複数をピン留めすると、ピン留めした順（PinnedAt昇順）に並ぶ")]
    public void 複数ピン留めはピン留めした順に並ぶ()
    {
        var now = DateTimeOffset.Now;
        // 実際に「先にピン留めしたものが上」になることを検証するため、PinnedAtの前後関係と
        // LastAppliedAt（適用日時）の前後関係をわざと逆にしておく。適用日時基準で並んでいたら
        // このテストは失敗するはず。
        var firstPinned = new Project
        {
            Id = "p_first", Name = "最初にピン留め", Pinned = true,
            PinnedAt = now.AddMinutes(-30), LastAppliedAt = now.AddDays(-10), NextRevision = 2,
        };
        var secondPinned = new Project
        {
            Id = "p_second", Name = "次にピン留め", Pinned = true,
            PinnedAt = now.AddMinutes(-20), LastAppliedAt = now.AddDays(-5), NextRevision = 2,
        };
        var thirdPinned = new Project
        {
            Id = "p_third", Name = "3番目にピン留め", Pinned = true,
            PinnedAt = now.AddMinutes(-10), LastAppliedAt = now, NextRevision = 2,
        };

        var sorted = ProjectStore.Sort(new[] { thirdPinned, firstPinned, secondPinned });

        sorted.Select(p => p.Id).Should().ContainInOrder(new[] { "p_first", "p_second", "p_third" },
            "ピン留め済み同士は最終適用日時ではなく、PinnedAtの昇順（先にピン留めしたものが上）で並ぶはず");
    }

    [Fact(DisplayName = "ピン留めを解除して再度ピン留めすると、ピン留め済みの最後尾に来る")]
    public void ピン留め解除後の再ピン留めは最後尾に来る()
    {
        var now = DateTimeOffset.Now;
        var staysFirst = new Project { Id = "p_stays", Name = "ずっとピン留め", Pinned = true, PinnedAt = now.AddHours(-2), NextRevision = 2 };
        var staysSecond = new Project { Id = "p_stays2", Name = "ずっとピン留め2", Pinned = true, PinnedAt = now.AddHours(-1), NextRevision = 2 };
        // p_rePinnedは最初はp_staysより先にピン留めされていたが、一度解除して今しがた
        // 再度ピン留めした想定（＝解除中はPinnedAt=null、再ピン留め時に現在時刻へ更新される。
        // 実際の更新経路はProjectPaneViewModel.ToggleSelectedPinAsync参照）。
        var rePinned = new Project { Id = "p_rePinned", Name = "解除後に再ピン留め", Pinned = true, PinnedAt = now, NextRevision = 2 };

        var sorted = ProjectStore.Sort(new[] { rePinned, staysFirst, staysSecond });

        sorted.Select(p => p.Id).Should().ContainInOrder(new[] { "p_stays", "p_stays2", "p_rePinned" },
            "再度ピン留めしたプロジェクトは新しいPinnedAtを持つため、既存のピン留め済みグループの最後尾に来るはず");
    }

    [Fact(DisplayName = "UpdateAsyncでの解除→再ピン留め（PinnedAtの記録・クリア）が永続化・並び順へ正しく反映される")]
    public async Task ピン留めの解除と再ピン留めがUpdateAsync経由で反映される()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var rootA = ws.CreateDirectory("proj-a");
        var rootB = ws.CreateDirectory("proj-b");

        await store.SaveAsync(new[]
        {
            new Project { Id = "p_a", Name = "A", Root = rootA, NextRevision = 1 },
            new Project { Id = "p_b", Name = "B", Root = rootB, NextRevision = 1 },
        });

        // ProjectPaneViewModel.ToggleSelectedPinAsyncと同じ更新方式（オンでPinnedAtを記録、
        // オフでnullへ戻す）をここでも使い、実際の更新経路を模す。
        Project PinToggle(Project p) => p.Pinned
            ? p with { Pinned = false, PinnedAt = null }
            : p with { Pinned = true, PinnedAt = DateTimeOffset.Now };

        await store.UpdateAsync("p_a", PinToggle); // Aをピン留め
        await Task.Delay(10);
        await store.UpdateAsync("p_b", PinToggle); // Bをピン留め（Aより後）

        var afterBothPinned = ProjectStore.Sort((await store.LoadAsync()).Value);
        afterBothPinned.Select(p => p.Id).Should().ContainInOrder(new[] { "p_a", "p_b" },
            "先にピン留めしたAが上に来るはず");

        await store.UpdateAsync("p_a", PinToggle); // Aのピン留めを解除
        var afterUnpinA = (await store.LoadAsync()).Value.Single(p => p.Id == "p_a");
        afterUnpinA.Pinned.Should().BeFalse();
        afterUnpinA.PinnedAt.Should().BeNull("解除するとPinnedAtはnullへ戻るはず");

        await Task.Delay(10);
        await store.UpdateAsync("p_a", PinToggle); // Aを再度ピン留め

        var final = ProjectStore.Sort((await store.LoadAsync()).Value);
        final.Select(p => p.Id).Should().ContainInOrder(new[] { "p_b", "p_a" },
            "解除して再度ピン留めしたAは新しいPinnedAtを持つため、Bより後（ピン留め済みの最後尾）に来るはず");
    }

    [Fact(DisplayName = "ピン留め済みプロジェクトが増えても、ピン留めしていないものの並び（適用日時降順・未適用は最下部）は変わらない")]
    public void ピン留め有無混在でも非ピン留めの並びは変わらない()
    {
        var now = DateTimeOffset.Now;
        var pinnedFirst = new Project { Id = "p_pin1", Name = "ピン留め1", Pinned = true, PinnedAt = now.AddHours(-2), NextRevision = 2 };
        var pinnedSecond = new Project { Id = "p_pin2", Name = "ピン留め2", Pinned = true, PinnedAt = now.AddHours(-1), NextRevision = 2 };
        var appliedRecent = new Project { Id = "p_recent", Name = "非ピン留め・最近適用", LastAppliedAt = now, NextRevision = 2 };
        var appliedOld = new Project { Id = "p_old", Name = "非ピン留め・昔に適用", LastAppliedAt = now.AddDays(-30), NextRevision = 2 };
        var neverApplied = new Project { Id = "p_never", Name = "非ピン留め・未適用", NextRevision = 1 };

        var sorted = ProjectStore.Sort(new[] { neverApplied, appliedOld, pinnedSecond, appliedRecent, pinnedFirst });

        sorted.Select(p => p.Id).Should().ContainInOrder(
            new[] { "p_pin1", "p_pin2", "p_recent", "p_old", "p_never" },
            "ピン留め済み2件（ピン留め順）の下に、非ピン留め（適用日時降順・未適用は最下部）が" +
            "従来どおりの並びで続くはず");
    }

    [Fact(DisplayName = "旧形式（pinnedAtキーの無い・複数ピン留め済み）projects.jsonを読み込んでも順序が安定する")]
    public async Task 旧形式で複数ピン留め済みでも順序は安定する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        paths.EnsureCoreDirectoriesExist();
        var rootA = ws.CreateDirectory("legacy-pin-a");
        var rootB = ws.CreateDirectory("legacy-pin-b");
        var rootC = ws.CreateDirectory("legacy-pin-c");
        // pinnedAtキー自体を持たない旧形式。3件ともpinned=trueで、うち2件（p_legacy_pin_a/b）は
        // pinnedAtが無いため移行救済フォールバック（DateTimeOffset.MinValueを代用）が働く。
        var legacyJson = $$"""
            {"projects":[
                {"id":"p_legacy_pin_a","name":"旧ピンA","root":"{{rootA.Replace("\\", "\\\\")}}",
                 "pinned":true,"lastAppliedAt":"2023-01-01T00:00:00+00:00","nextRevision":3},
                {"id":"p_legacy_pin_b","name":"旧ピンB","root":"{{rootB.Replace("\\", "\\\\")}}",
                 "pinned":true,"lastAppliedAt":"2023-06-01T00:00:00+00:00","nextRevision":3},
                {"id":"p_legacy_unpinned","name":"旧・非ピン","root":"{{rootC.Replace("\\", "\\\\")}}",
                 "pinned":false,"lastAppliedAt":"2024-01-01T00:00:00+00:00","nextRevision":2}
            ]}
            """;
        await File.WriteAllTextAsync(paths.ProjectsFilePath, legacyJson);

        var store = new ProjectStore(paths);
        var loaded = await store.LoadAsync();
        loaded.IsSuccess.Should().BeTrue();

        var legacyPinA = loaded.Value.Single(p => p.Id == "p_legacy_pin_a");
        var legacyPinB = loaded.Value.Single(p => p.Id == "p_legacy_pin_b");
        legacyPinA.PinnedAt.Should().BeNull("旧形式のJSONにキーが無いため既定値nullで読めるはず");
        legacyPinB.PinnedAt.Should().BeNull();

        // 何度読み込んでソートしても、ピン留め済み同士（p_legacy_pin_a/b）の順序が入れ替わらない
        // こと（安定していること）を確認する。移行救済によりPinnedAtは両方ともMinValueで
        // タイになるため、次の並べ替えキー（EffectiveLastAppliedAt降順）で決着する
        // （lastAppliedAtが新しいp_legacy_pin_bが先に来る）。
        for (var i = 0; i < 5; i++)
        {
            var reloaded = await store.LoadAsync();
            var sorted = ProjectStore.Sort(reloaded.Value);
            sorted.Select(p => p.Id).Should().ContainInOrder(
                new[] { "p_legacy_pin_b", "p_legacy_pin_a", "p_legacy_unpinned" },
                "旧形式・複数ピン留めでも、読み込みのたびに順序が入れ替わってはならない");
        }
    }

    [Fact(DisplayName = "未適用のプロジェクト同士は毎回同じ順序（登録順）で並ぶ")]
    public void 未適用同士の順序は実行のたびに変わらない()
    {
        var p1 = new Project { Id = "p_1", Name = "1番目に登録", NextRevision = 1 };
        var p2 = new Project { Id = "p_2", Name = "2番目に登録", NextRevision = 1 };
        var p3 = new Project { Id = "p_3", Name = "3番目に登録", NextRevision = 1 };
        var input = new[] { p1, p2, p3 };

        for (var i = 0; i < 5; i++)
        {
            var sorted = ProjectStore.Sort(input);
            sorted.Select(p => p.Id).Should().ContainInOrder(new[] { "p_1", "p_2", "p_3" },
                "未適用同士はSort呼び出しのたびに入力順（登録順）を安定して保つ必要がある");
        }
    }

    [Fact(DisplayName = "ルートが存在しないプロジェクトは削除されず未接続として扱われる")]
    public async Task 未接続プロジェクトは削除されず検出される()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var existingRoot = ws.CreateDirectory("exists");
        var missingRoot = Path.Combine(ws.RootPath, "does-not-exist");

        var projects = new[]
        {
            new Project { Id = "p_ok", Name = "接続中", Root = existingRoot },
            new Project { Id = "p_gone", Name = "未接続", Root = missingRoot },
        };

        var validated = await store.ValidateAsync(projects);

        validated.IsSuccess.Should().BeTrue();
        validated.Value.Should().HaveCount(2, "未接続でも一覧から削除されてはならない（仕様書3.2）");
        validated.Value.Single(p => p.Id == "p_ok").IsDisconnected.Should().BeFalse();
        validated.Value.Single(p => p.Id == "p_gone").IsDisconnected.Should().BeTrue();
    }

    [Fact(DisplayName = "RegisterAsyncは既存ルートを再登録すると重複せず最終使用日時のみ更新する")]
    public async Task 同一ルートの再登録は重複しない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var root = ws.CreateDirectory("proj1");

        var first = await store.RegisterAsync(root, "最初の名前");
        first.IsSuccess.Should().BeTrue();
        var second = await store.RegisterAsync(root, null);
        second.IsSuccess.Should().BeTrue();

        var loaded = await store.LoadAsync();
        loaded.Value.Should().HaveCount(1, "同一ルートの再登録はプロジェクトを重複登録してはならない");
        loaded.Value[0].Id.Should().Be(first.Value.Id);
        loaded.Value[0].Name.Should().Be("最初の名前", "再登録時に名前がnullなら既存の名前を保つはず");
    }

    // ------------------------------------------------------------------
    // 13.1 リビジョン番号の補正
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ReconcileRevisionは実体の最大リビジョン+1未満のnextRevisionのみ補正する")]
    public void リビジョン番号は実体の最大値未満のときのみ補正される()
    {
        var project = new Project { Id = "p_x", NextRevision = 3 };

        var reconciled = ProjectStore.ReconcileRevision(project, actualMaxRevision: 10);
        reconciled.NextRevision.Should().Be(11);

        var unchanged = ProjectStore.ReconcileRevision(project with { NextRevision = 20 }, actualMaxRevision: 10);
        unchanged.NextRevision.Should().Be(20, "既に実体より大きい場合は補正しないはず");
    }

    // ------------------------------------------------------------------
    // 不具合2: nextRevisionの消費（ConsumeNextRevisionAsync）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ConsumeNextRevisionAsyncは消費前の値を返し、projects.jsonへは+1した値を永続化する")]
    public async Task 番号消費は消費前の値を返しつつ1つ進めて永続化する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        await store.SaveAsync(new[] { new Project { Id = "p_a", Root = ws.CreateDirectory("a"), NextRevision = 1 } });

        var first = await store.ConsumeNextRevisionAsync("p_a");
        first.IsSuccess.Should().BeTrue();
        first.Value.Should().Be(1, "1回目に使う番号は消費前の値（1）のはず");

        var second = await store.ConsumeNextRevisionAsync("p_a");
        second.Value.Should().Be(2, "1回目の消費で2へ進んでいるはず");

        var reloaded = await store.LoadAsync();
        reloaded.Value.Single(p => p.Id == "p_a").NextRevision.Should().Be(3,
            "2回消費したので永続化されたnextRevisionは3になっているはず");
    }

    [Fact(DisplayName = "ConsumeNextRevisionAsyncは複数プロジェクトのnextRevisionを独立して扱う")]
    public async Task 番号消費は他プロジェクトへ影響しない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        await store.SaveAsync(new[]
        {
            new Project { Id = "p_a", Root = ws.CreateDirectory("a"), NextRevision = 1 },
            new Project { Id = "p_b", Root = ws.CreateDirectory("b"), NextRevision = 5 },
        });

        await store.ConsumeNextRevisionAsync("p_a");
        await store.ConsumeNextRevisionAsync("p_a");

        var reloaded = await store.LoadAsync();
        reloaded.Value.Single(p => p.Id == "p_a").NextRevision.Should().Be(3);
        reloaded.Value.Single(p => p.Id == "p_b").NextRevision.Should().Be(5, "別プロジェクトのnextRevisionは変化しないはず");
    }

    [Fact(DisplayName = "ConsumeNextRevisionAsyncは存在しないプロジェクトIDに対して失敗を返す")]
    public async Task 番号消費は未知のプロジェクトIDでは失敗する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);

        var result = await store.ConsumeNextRevisionAsync("p_不存在");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact(DisplayName = "nextRevisionキーを持たない旧形式のprojects.jsonを読み込んでも1から正しく消費できる")]
    public async Task 旧形式のprojects_jsonでも番号消費は1から始まる()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var root = ws.CreateDirectory("legacy");
        // 実機で確認された最小形式（nextRevisionキー自体が存在しない）を模す。
        var legacyJson = $$"""
            {"projects":[{"id":"p_legacy","name":"旧形式プロジェクト","root":"{{root.Replace("\\", "\\\\")}}"}]}
            """;
        await File.WriteAllTextAsync(paths.ProjectsFilePath, legacyJson);

        var store = new ProjectStore(paths);
        var loaded = await store.LoadAsync();
        loaded.Value.Single().NextRevision.Should().Be(1, "nextRevisionキーが無い場合は既定値1で読めるはず");

        var consumed = await store.ConsumeNextRevisionAsync("p_legacy");

        consumed.IsSuccess.Should().BeTrue();
        consumed.Value.Should().Be(1, "旧形式でも初回の消費は1番から始まるはず");
        var reloaded = await store.LoadAsync();
        reloaded.Value.Single().NextRevision.Should().Be(2, "消費後はprojects.jsonへ2として永続化されるはず");
    }

    // ------------------------------------------------------------------
    // 不具合対応: 消費した番号の返却（ReleaseRevisionAsync）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ReleaseRevisionAsyncはNextRevisionが消費直後（revision+1）のときだけ番号を戻す")]
    public async Task 番号返却は消費直後のときだけ戻す()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        await store.SaveAsync(new[] { new Project { Id = "p_a", Root = ws.CreateDirectory("a"), NextRevision = 1 } });

        var consumed = await store.ConsumeNextRevisionAsync("p_a");
        consumed.Value.Should().Be(1);
        (await store.LoadAsync()).Value.Single(p => p.Id == "p_a").NextRevision.Should().Be(2);

        var released = await store.ReleaseRevisionAsync("p_a", consumed.Value);

        released.IsSuccess.Should().BeTrue();
        released.Value.Should().BeTrue("消費直後（NextRevision=2=revision+1）なので返却できるはず");
        var reloaded = await store.LoadAsync();
        reloaded.Value.Single(p => p.Id == "p_a").NextRevision.Should().Be(1, "返却により消費前の番号へ戻るはず");
    }

    [Fact(DisplayName = "ReleaseRevisionAsyncはNextRevisionが既に進んでいる場合は何もしない（番号の重複を避ける）")]
    public async Task 番号返却は既に進んでいる場合は何もしない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        await store.SaveAsync(new[] { new Project { Id = "p_a", Root = ws.CreateDirectory("a"), NextRevision = 1 } });

        var consumed = await store.ConsumeNextRevisionAsync("p_a"); // consumed.Value == 1, NextRevisionは2へ
        await store.ConsumeNextRevisionAsync("p_a"); // 別操作が続けて消費し、NextRevisionは3へ進む

        var released = await store.ReleaseRevisionAsync("p_a", consumed.Value); // revision=1で返却を試みる

        released.IsSuccess.Should().BeTrue();
        released.Value.Should().BeFalse("NextRevisionは既に3（revision+1=2ではない）まで進んでいるため返却してはならない");
        var reloaded = await store.LoadAsync();
        reloaded.Value.Single(p => p.Id == "p_a").NextRevision.Should().Be(3,
            "ここで1へ戻すと次の消費が既に使用済みの番号2と重複するため、何もせずNextRevisionは3のままのはず");
    }

    [Fact(DisplayName = "ReleaseRevisionAsyncは存在しないプロジェクトIDに対して失敗を返す")]
    public async Task 番号返却は未知のプロジェクトIDでは失敗する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);

        var result = await store.ReleaseRevisionAsync("p_不存在", 1);

        result.IsSuccess.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 3.1 overrides の +/- 解決
    // ------------------------------------------------------------------

    [Fact(DisplayName = "overrides.allowedExtensionsの+は全体設定へ追加し-は除外する")]
    public void 拡張子overridesの追加と除外が反映される()
    {
        var baseSettings = new Settings();
        var project = new Project
        {
            Id = "p_x",
            Overrides = new ProjectOverrides { AllowedExtensions = new[] { "+.sql", "-.txt" } },
        };

        var resolved = ProjectOverrideResolver.Apply(baseSettings, project);

        resolved.Safety.AllowedExtensions.Should().Contain(".sql");
        resolved.Safety.AllowedExtensions.Should().NotContain(".txt");
        resolved.Safety.AllowedExtensions.Should().Contain(".py", "+/-指定のみの場合は全体設定を基準に増減するはず");
    }

    [Fact(DisplayName = "overrides.allowedExtensionsに接頭辞なしの項目があれば全体設定を置き換える")]
    public void 接頭辞なしの拡張子overridesは全体設定を置き換える()
    {
        var baseSettings = new Settings();
        var project = new Project
        {
            Id = "p_x",
            Overrides = new ProjectOverrides { AllowedExtensions = new[] { "rb", "go" } },
        };

        var resolved = ProjectOverrideResolver.Apply(baseSettings, project);

        resolved.Safety.AllowedExtensions.Should().BeEquivalentTo(new[] { ".rb", ".go" });
    }

    [Fact(DisplayName = "overrides.newFileEncodingが指定されていれば全体設定を上書きする")]
    public void newFileEncodingのoverrideが反映される()
    {
        var baseSettings = new Settings();
        var project = new Project { Id = "p_x", Overrides = new ProjectOverrides { NewFileEncoding = "shift_jis" } };

        var resolved = ProjectOverrideResolver.Apply(baseSettings, project);

        resolved.Encoding.NewFileEncoding.Should().Be("shift_jis");
    }

    // ------------------------------------------------------------------
    // 10章追加要件: コンテキスト収集の3状態選択の永続化（ProjectOverrides.ContextFileStates）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ContextFileStatesはSaveAsync→LoadAsyncで往復する")]
    public async Task ContextFileStatesが往復する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(appPaths);
        var project = new Project
        {
            Id = "p_ctxstate",
            Name = "ctx",
            Root = ws.CreateDirectory("proj"),
            Overrides = new ProjectOverrides
            {
                ContextFileStates = new Dictionary<string, string>
                {
                    ["lib/helper.py"] = ContextFileState.StructureOnly.ToString(),
                    ["secret.env"] = ContextFileState.Hidden.ToString(),
                },
            },
        };

        await store.SaveAsync(new[] { project });
        var reloaded = await store.LoadAsync();

        reloaded.IsSuccess.Should().BeTrue();
        var loadedProject = reloaded.Value.Single();
        loadedProject.Overrides.ContextFileStates.Should().ContainKey("lib/helper.py")
            .WhoseValue.Should().Be(ContextFileState.StructureOnly.ToString());
        loadedProject.Overrides.ContextFileStates.Should().ContainKey("secret.env")
            .WhoseValue.Should().Be(ContextFileState.Hidden.ToString());
    }

    [Fact(DisplayName = "ContextFileStatesを持たない古いprojects.jsonを読んでも空（＝全部既定の内容も出す）になる")]
    public async Task ContextFileStates無しの旧形式は空になる()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        appPaths.EnsureCoreDirectoriesExist();
        // 「contextFileStates」キーを持たない旧形式のprojects.jsonを直接書く（後方互換の検証）。
        var legacyJson = """
            { "projects": [ { "id": "p_legacy", "name": "legacy", "root": "/tmp/legacy" } ] }
            """;
        await File.WriteAllTextAsync(appPaths.ProjectsFilePath, legacyJson);

        var store = new ProjectStore(appPaths);
        var reloaded = await store.LoadAsync();

        reloaded.IsSuccess.Should().BeTrue();
        reloaded.Value.Single().Overrides.ContextFileStates.Should().BeEmpty(
            "既定はキー自体が無い旧形式であり、その場合は全ファイルが既定（内容も出す）扱いになるはず");
    }

    [Fact(DisplayName = "overridesが未指定の場合は全体設定のままになる")]
    public void overrides未指定なら全体設定のままになる()
    {
        var baseSettings = new Settings();
        var project = new Project { Id = "p_x" };

        var resolved = ProjectOverrideResolver.Apply(baseSettings, project);

        resolved.Safety.AllowedExtensions.Should().BeEquivalentTo(baseSettings.Safety.AllowedExtensions);
        resolved.Encoding.NewFileEncoding.Should().Be(baseSettings.Encoding.NewFileEncoding);
    }
}
