// 區塊職責：在 `Main` 之前裝上崩潰報告（Senate.Core/CrashLog）—— Server 版。
// 物理意義：Server 是常駐的，炸掉時沒有人在看它的 stdout；而 Windows 事件記錄只在某些身分下才留得住
//           （2026-09-27 沙箱身分的 senate.exe 崩潰，事件記錄裡沒有那一筆）。⇒ 自己落一份檔。
// 數值影響：只訂閱例外事件，不改任何行為。repo 根與 Program.cs 同一支（ServerBootstrap.FindRepoRoot）。
using System.Runtime.CompilerServices;
using Senate.Core;

static class CrashLogInit
{
#pragma warning disable CA2255   // 要的正是比 Main 早，理由見檔頭
    [ModuleInitializer]
    internal static void Init()
    {
        try { CrashLog.Install(ServerBootstrap.FindRepoRoot(), "senate-server"); }
        catch (Exception) { }
    }
#pragma warning restore CA2255
}
