// 區塊職責：「啟動 senate.exe 時把常駐 Server 拉起來」的設定與動作，以及 ServerAdminPage 的刷新間隔設定（TASK-0329）。
// 物理意義：Tim 2026-09-29：「Server 加一個後台設定（放在 ServerAdminPage）—— 開啟後 Server 會在啟動 senate.exe 時啟動
//           （假如尚未啟動的話），設定預設開啟；ServerAdminPage 開著時一定間隔刷新 Server 狀態（預設 1 秒）」。
//           🩸 起因：TASK-0315 把每日結算搬進 main Server 之後，第一次真跑（2026-09-29）晚了 13 小時 ——
//             半夜**沒有任何一顆 Server 在跑**，要等到早上第一則需要它的發文 autostart 才補結算。
//             每日結算的實際前提是「有人先叫醒過 Server」。
//           ⚠ 本設定**只縮短**那段空窗（任何一次 senate.exe 啟動都會叫醒它），⛔ 不是常駐保證 ——
//             一整晚沒有人跑 senate.exe 的話，它照樣不會自己起來。
// 數值影響：`EnsureOnLaunch` 最多替每一顆常駐 Server 各 spawn 一次（走 `ServerSpawn.TrySpawn` 那條唯一咽喉，
//           build 進行中會被 BuildGuard 擋下）；只讀寫 `senate.pages.local.json` 的兩格設定。
#nullable enable
using SCP.Core.Prefs;
using SCP.Core.Proc;

namespace Senate.Core;

public static class ServerLaunchAutoStart
{
    /// <summary>啟動 senate.exe 時，常駐 Server 沒在跑就拉起來。預設 true ＝ 開（Tim 2026-09-29）。</summary>
    public static readonly SCP_PrefKey<bool> Enabled = SCP_PrefKey.Bool("server", "autostartOnLaunch", true);

    /// <summary>ServerAdminPage 開著時多久重新探測一次（秒）。預設 1。</summary>
    public static readonly SCP_PrefKey<double> AdminRefreshSeconds = SCP_PrefKey.Double("server", "adminRefreshSeconds", 1d);

    /// <summary>刷新間隔的下限（秒）。⚠ 探測每次都讀 registry＋心跳檔 ⇒ 不給設成每幀。</summary>
    public const double MinRefreshSeconds = 0.2;

    /// <summary>
    /// 常駐的那幾顆 —— ServerAdminPage「就算從沒跑過也要列出來」的清單與本設定要拉的清單**同一份**。
    /// <para>⛔ 兩邊各寫一份的話，加一顆常駐 Server 時只會有一邊被改，而漏掉的那邊不會叫。</para>
    /// </summary>
    public static readonly string[] ResidentIds = { SCP_ServerIds.Default, SCP_ServerIds.Tavern };

    /// <summary>
    /// 這一個子命令要不要觸發 launch autostart。
    /// <para>排除的每一格都有理由：</para>
    /// <para>· `server`：生命週期本身 —— `server stop` 之後自己又被拉起來＝停不掉；`server start` 自己就是在起。</para>
    /// <para>· `pages-check`／`selftest`：build／驗收流程裡跑的，⛔ 不該在驗收當中生出常駐行程。</para>
    /// <para>· `--version`／`help`：只想問一題的人，⛔ 不替他生一顆行程。</para>
    /// <para>· `sync-window`（雙擊 senate-sync.exe）：**先同步、再起 Server** —— Server 一起來就補跨日發券、
    ///   把信件庫寫髒，同步頁就全部跳過（Tim 2026-10-02）。那顆 exe 存在的理由就是這一格。</para>
    /// </summary>
    public static bool AppliesTo(string iCmd)
    {
        switch (iCmd)
        {
            case "server":
            case "sync-window":
            case "pages-check":
            case "selftest":
            case "--version": case "-v": case "version":
            case "--help": case "-h": case "help":
                return false;
            default:
                return true;
        }
    }

    /// <summary>設定現值與它是哪一層給的（預設／設定／讀不了退回預設）。</summary>
    public static bool ResolveEnabled(string iRepoRoot, out string oSource)
        => ServerConsolePref.Resolve(iRepoRoot, Enabled, null, out oSource);

    public static (bool Ok, string Message) SaveEnabled(string iRepoRoot, bool iOn)
        => SenatePageStore.For(iRepoRoot).Write(Enabled, iOn);

    /// <summary>刷新間隔（秒）。低於下限、非數字或讀不了 ⇒ 用預設／下限，並在 <paramref name="oSource"/> 說出來。</summary>
    public static double ResolveRefreshSeconds(string iRepoRoot, out string oSource)
    {
        SCP_PrefRead<double> aRead = SenatePageStore.For(iRepoRoot).Read(AdminRefreshSeconds);
        double aVal;
        switch (aRead.State)
        {
            case SCP_PrefState.Present: aVal = aRead.Value; oSource = "設定 " + AdminRefreshSeconds.Path; break;
            case SCP_PrefState.Missing: aVal = AdminRefreshSeconds.Default; oSource = "預設（沒設過）"; break;
            default:
                aVal = AdminRefreshSeconds.Default;
                oSource = "預設（⚠ 設定讀不了：" + (aRead.Error ?? "沒有說明") + "）";
                break;
        }
        if (double.IsNaN(aVal) || aVal < MinRefreshSeconds)
        {
            oSource += "（⚠ 值 " + aVal + " 低於下限 ⇒ 用 " + MinRefreshSeconds + "）";
            aVal = MinRefreshSeconds;
        }
        return aVal;
    }

    public static (bool Ok, string Message) SaveRefreshSeconds(string iRepoRoot, double iSeconds)
        => SenatePageStore.For(iRepoRoot).Write(AdminRefreshSeconds, iSeconds);

    /// <summary>
    /// 設定開著就把沒在跑的常駐 Server 拉起來。回要印給人看的幾行（**全部都在跑時回空** —— 每一次 CLI 呼叫都會經過這裡，
    /// 沒事的時候不該多一行字）。
    /// <para>⚠ 判準跟 ServerAdminPage 的「允許啟動」同一格：**確定沒在跑**才起 ——
    /// 身分驗不出來的記錄還在時 ⛔ 不起（可能其實在跑，起了就是多開一顆被單例鎖退回）。</para>
    /// <para>⚠ 只送出、⛔ 不等它上線：等的話每一次 CLI 呼叫都要付心跳的秒數；
    /// 真的需要 Server 的委派 Cmd 自己有 `SCP_ServerAutoStart.Ensure` 在等。</para>
    /// </summary>
    public static List<string> EnsureOnLaunch(string iRepoRoot)
    {
        var aLines = new List<string>();
        bool aOn;
        string aSource;
        try { aOn = ResolveEnabled(iRepoRoot, out aSource); }
        catch (Exception e) { aLines.Add("⚠ 啟動時拉起 Server：設定讀不了（" + e.GetType().Name + "：" + e.Message + "）⇒ 這一趟沒有拉"); return aLines; }
        if (!aOn) return aLines;

        foreach (string aId in ResidentIds)
        {
            ServerStatus aNow;
            try { aNow = ServerHost.Probe(iRepoRoot, aId); }
            catch (Exception e)
            {
                // 量不到 ≠ 沒在跑 ⇒ ⛔ 不起（同 ServerAdminPage 的 Allowed.None）
                aLines.Add("⚠ 啟動時拉起 Server：`" + aId + "` 量不到（" + e.GetType().Name + "）⇒ 沒有拉");
                continue;
            }
            if (aNow.IsRunning) continue;
            if (aNow.Unverifiable.Count > 0)
            {
                aLines.Add("⚠ 啟動時拉起 Server：`" + aId + "` 有身分驗不出來的記錄 ⇒ 沒有拉（可能其實在跑）");
                continue;
            }

            try
            {
                if (ServerSpawn.TrySpawn(iRepoRoot, aId, null, out int aPid, out string aErr, out _, out _))
                    aLines.Add("・啟動 senate 時拉起 Server `" + aId + "`（已送出，pid=" + aPid + "；設定 "
                               + Enabled.Path + "＝開，" + aSource + "）");
                else
                    aLines.Add("⚠ 啟動時拉起 Server `" + aId + "` 沒有起來：" + aErr);
            }
            catch (Exception e)
            {
                aLines.Add("⚠ 啟動時拉起 Server `" + aId + "` 丟例外（" + e.GetType().Name + "：" + e.Message + "）⇒ 沒有起來");
            }
        }
        return aLines;
    }
}
