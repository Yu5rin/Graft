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

    // ------------------------------------------------------------------
    // 3.2 並べ替え・未接続検出
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Sortはピン留めを先頭に、次に最終使用日時の降順で並べる")]
    public void 並べ替えはピン留め優先で最終使用日時降順()
    {
        var now = DateTimeOffset.Now;
        var a = new Project { Id = "p_a", Name = "A", Pinned = false, LastUsedAt = now };
        var b = new Project { Id = "p_b", Name = "B", Pinned = true, LastUsedAt = now.AddDays(-10) };
        var c = new Project { Id = "p_c", Name = "C", Pinned = false, LastUsedAt = now.AddDays(-1) };
        var d = new Project { Id = "p_d", Name = "D", Pinned = true, LastUsedAt = now };

        var sorted = ProjectStore.Sort(new[] { a, b, c, d });

        sorted.Select(p => p.Id).Should().ContainInOrder("p_d", "p_b", "p_a", "p_c");
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
