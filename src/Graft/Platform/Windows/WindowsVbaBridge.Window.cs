using System.Runtime.InteropServices;
using Graft.Core;
using Graft.Core.Vba;

namespace Graft.Platform.Windows;

// VBE・Excel のウィンドウ操作（仕様 7.6）。
public sealed partial class WindowsVbaBridge
{
    private GraftResult<bool> ShowInVbeCore(string fullName, string componentName, int line, CancellationToken abandoned)
    {
        using var session = NewSession(abandoned);
        var opened = OpenProject(session, fullName, requireDesignMode: false);
        if (opened.Issue is not null) return GraftResult<bool>.Fail(opened.Issue);

        dynamic wb = opened.Workbook!;
        dynamic project = opened.Project!;

        var compsObj = session.Own(session.Call(() => (object)project.VBComponents));
        if (compsObj is null) return GraftResult<bool>.Fail(ErrorCode.E801, "VBComponents を取得できませんでした。");

        var position = FindComponentPosition(session, compsObj, componentName);
        if (position is null)
        {
            return GraftResult<bool>.Fail(ErrorCode.E808, $"{componentName}: Excel 側にこのモジュールがありません。", path: fullName);
        }

        dynamic comps = compsObj;
        var componentObj = session.Own(session.Call(() => (object)comps.Item(position.Value)));
        dynamic component = componentObj!;
        var cmObj = session.Own(session.Call(() => (object)component.CodeModule));
        dynamic cm = cmObj!;
        var paneObj = session.Own(session.Call(() => (object)cm.CodePane));
        dynamic pane = paneObj!;

        // コードウィンドウを開き、指定行へ移動する。行は 1 始まりで、列は先頭にそろえる。
        var target = Math.Max(1, line);
        session.Call(() => { pane.Show(); return 0; });
        session.Call(() => { pane.SetSelection(target, 1, target, 1); return 0; });
        session.Call(() => { pane.TopLine = Math.Max(1, target - 5); return 0; });

        // VBE 本体を表示して前面化する。ウィンドウのハンドルは VBE のメインウィンドウの HWnd から取る。
        var appObj = session.Own(session.Call(() => (object)wb.Application));
        dynamic app = appObj!;
        var vbeObj = session.Own(session.Call(() => (object)app.VBE));
        dynamic vbe = vbeObj!;
        var mainWindowObj = session.Own(session.Call(() => (object)vbe.MainWindow));
        dynamic mainWindow = mainWindowObj!;
        session.Call(() => { mainWindow.Visible = true; return 0; });
        var hwnd = session.Call(() => Convert.ToInt64((object)mainWindow.HWnd));

        return GraftResult<bool>.Ok(WindowsForeground.BringToFront(new IntPtr(hwnd)));
    }

    private GraftResult<bool> ActivateWorkbookCore(string fullName, CancellationToken abandoned)
    {
        using var session = NewSession(abandoned);
        var workbook = FindWorkbook(session, fullName);
        if (workbook is null)
        {
            return GraftResult<bool>.Fail(ErrorCode.E804, $"次のブックが開いていません: {fullName}", path: fullName);
        }

        dynamic wb = workbook;
        session.Call(() => { wb.Activate(); return 0; });

        var appObj = session.Own(session.Call(() => (object)wb.Application));
        dynamic app = appObj!;
        var hwnd = session.Call(() => Convert.ToInt64((object)app.Hwnd));
        return GraftResult<bool>.Ok(WindowsForeground.BringToFront(new IntPtr(hwnd)));
    }

    private static int? FindComponentPosition(Session s, object compsObject, string componentName)
    {
        dynamic comps = compsObject;
        var count = s.Call(() => (int)comps.Count);
        for (var i = 1; i <= count; i++)
        {
            using var item = new ComScope();
            var comp = item.Own(s.Call(() => (object)comps.Item(i)));
            if (comp is null) continue;
            dynamic component = comp;
            if (string.Equals(s.Call(() => (string)component.Name), componentName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }
}
