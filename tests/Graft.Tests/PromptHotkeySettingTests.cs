using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// C10: 依頼文コピーのOS全体のホットキー（既定 Ctrl+Shift+C）を、設定で変更・無効化できる。
/// settings.json に項目が無い古い設定は既定値で読め、空文字は「OS全体には登録しない」として保たれる。
/// </summary>
public class PromptHotkeySettingTests
{
    private static AppPaths MakePaths(TempWorkspace ws) => new(ws.CreateDirectory("app"));

    [Fact(DisplayName = "settings.jsonに項目が無くても、依頼文コピーのホットキーは既定のCtrl+Shift+Cで読める")]
    public async Task 項目が無ければ既定値()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        File.WriteAllText(paths.SettingsFilePath, "{ \"hotkey\": \"Ctrl+Alt+V\" }");

        var result = await new SettingsStore(paths).LoadAsync();

        result.Value.PromptHotkey.Should().Be("Ctrl+Shift+C");
    }

    [Fact(DisplayName = "空文字（OS全体には登録しない）は、既定へ戻されず空のまま読める")]
    public async Task 空文字は空のまま()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        File.WriteAllText(paths.SettingsFilePath, "{ \"promptHotkey\": \"\" }");

        var result = await new SettingsStore(paths).LoadAsync();

        result.Value.PromptHotkey.Should().BeEmpty();
    }

    [Fact(DisplayName = "変更した組み合わせは保存して読み直しても保たれる")]
    public async Task 変更は保存される()
    {
        using var ws = new TempWorkspace();
        var store = new SettingsStore(MakePaths(ws));

        await store.SaveAsync(new Settings { PromptHotkey = "Ctrl+Alt+C" });
        var result = await store.LoadAsync();

        result.Value.PromptHotkey.Should().Be("Ctrl+Alt+C");
    }
}
