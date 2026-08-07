using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// バグ2の確認テスト: 破損した templates.json が仕様書13.1どおり退避・再生成されることを
/// <see cref="PromptTemplateStore"/> 単体で検証する。実機での全ファイル破損テストでは
/// シェル初期化失敗（バグ1）に巻き込まれて退避されたかどうか確認できなかったため、
/// ここでは他のストアと切り離して直接 LoadAsync を呼び、退避＋既定テンプレートでの
/// 再生成を確認する。
/// </summary>
public class PromptTemplateStoreRecoveryTests
{
    [Fact(DisplayName = "破損したtemplates.jsonはLoadAsyncで退避され、既定テンプレートで再生成される")]
    public async Task 破損templates_jsonを退避して既定テンプレートで再生成する()
    {
        using var ws = new TempWorkspace();
        var appDir = ws.CreateDirectory("app");
        var paths = new AppPaths(appDir);
        var templatesPath = paths.TemplatesFilePath;
        await File.WriteAllTextAsync(templatesPath, "{ これはJSONではない");

        var store = new PromptTemplateStore(paths);
        var result = await store.LoadAsync();

        result.IsSuccess.Should().BeTrue("破損していても既定テンプレートで復旧し成功として返るはず");
        result.Issues.Should().ContainSingle("破損の警告が1件記録されるはず");
        result.Value.Should().BeEquivalentTo(
            PromptTemplateStore.BuiltIns,
            "ユーザー定義テンプレートが失われても既定テンプレートは常に一式そろっているはず");

        var quarantined = Directory.GetFiles(appDir, "templates.json.corrupt.*");
        quarantined.Should().ContainSingle("壊れたtemplates.jsonは消さずに退避しておく必要がある");
        File.Exists(templatesPath).Should().BeTrue("既定値（ユーザー定義なし）のtemplates.jsonが再生成されているはず");
    }

    [Fact(DisplayName = "存在しないtemplates.jsonは破損扱いにせず既定テンプレートのみ返す")]
    public async Task 存在しない場合は既定テンプレートのみ返す()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new PromptTemplateStore(paths);

        var result = await store.LoadAsync();

        result.IsSuccess.Should().BeTrue();
        result.Issues.Should().BeEmpty("ファイルが無いだけでは破損扱いにしないはず");
        result.Value.Should().BeEquivalentTo(PromptTemplateStore.BuiltIns);
        File.Exists(paths.TemplatesFilePath).Should().BeFalse("読み込みだけではファイルを新規作成しないはず");
    }

    [Fact(DisplayName = "ユーザー定義テンプレートを保存後、再読込すると既定テンプレートに追加された形で返る")]
    public async Task ユーザー定義テンプレートが保存され再読込できる()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new PromptTemplateStore(paths);
        var custom = new PromptTemplate { Id = "custom-1", Name = "自作テンプレート", Body = "本文" };

        var saveResult = await store.SaveAsync(new[] { custom });
        saveResult.IsSuccess.Should().BeTrue();

        var reloaded = await store.LoadAsync();
        reloaded.IsSuccess.Should().BeTrue();
        reloaded.Value.Should().Contain(t => t.Id == "custom-1", "保存したユーザー定義テンプレートが読み戻せるはず");
        reloaded.Value.Should().HaveCount(
            PromptTemplateStore.BuiltIns.Count + 1,
            "既定テンプレート一式にユーザー定義1件が加わるはず");
    }
}
