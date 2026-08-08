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
