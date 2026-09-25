// 區塊職責：**Server 管理頁** —— 列出**所有**常駐 Server（main／tavern／…），逐顆看它活著沒、
//           心跳新不新鮮、build 對不對，並且逐顆啟動／停止。
// 物理意義：Tim 2026-09-18 交辦（「新增一個 Page 用來管理 Server 狀態，至少包含啟動＆關閉」）；
//           2026-09-25 擴成多顆（「酒館&銀行兩個常駐 Server …管理所有常駐 Server」）。
//           🩸 擴之前本頁寫死只看 `main`，要看酒館那顆得去終端機打 `--id` ⇒
//             **酒館那顆掛了，這一頁照樣全綠**，而那正是 TASK-0244 那格警告過的形狀。
//           讀數本體全部來自 `ServerHost.Probe`，⛔ 本頁**不自己判活著**——
//           那會變成同一件事的第二個判準，而兩份漂掉時**兩邊都說得通**。
//           ⇒ 這一頁跟 `senate server status` 是同一份事實的兩個投影，不是兩個真相源。
// 數值影響：只有兩顆鈕會寫東西：
//           · 啟動 ＝ **另開一個行程**（`senate server start` 是前景常駐，⛔ 不能在本頁 inline 跑
//             —— 那會把 GUI 卡死在它的迴圈裡）
//           · 停止 ＝ `ServerHost.Stop`（先請它自退，5 秒等不到才 kill）
//
// 🩸 為什麼停止要二段確認，而啟動不用：
//   停掉 Server ＝ **全體的金流當場失敗**（2026-09-18 起權威在新銀行，而寫入端只有 Server）。
//   那個代價落在別人身上，而按鈕不會替他們喊 —— 照 TASK-0216 那條：
//   **一個規矩正確、而代價落在別人身上的動作，不會有任何一層喊。**
//   ⇒ 所以這裡自己長一格出來：按下去先變成「待確認」，再按一次才真的動。
//   而啟動最壞是多一顆被拒絕的行程（`server start` 已有一顆在跑會自己拒絕），代價不對稱。
using System;
using System.Collections.Generic;
using SCP.Core.Gui;
using Senate.Core;
using SCP.Core.Proc;

namespace Senate.Cli.Pages;

public sealed class ServerAdminPage : SCP_GuiToolPage
{
    public const string PageKey = "server";

    /// <summary>待確認的動作（session 欄位；空 ＝ 沒有待確認）。⚠ **每一顆各一格**：見 <see cref="PendingIdFor"/>。</summary>
    public const string PendingId = "server/pending";

    /// <summary>
    /// 某一顆的「待確認停止」欄位。
    /// <para>🔴 一顆一格，⛔ 不共用：共用的話，在 `main` 按下第一次、再去 `tavern` 按一次，
    /// 就會停掉**你第二次按的那顆**，而你只對第一顆看過那句警告。</para>
    /// </summary>
    static string PendingIdFor(string iServerId) => PendingId + "/" + iServerId;

    /// <summary>
    /// 就算這棵樹上從沒跑過也要列出來的幾顆 —— 否則「從沒起來過」跟「不存在」在這頁上同形，
    /// 而那一顆恰好是你最需要按「啟動」的時候。
    /// </summary>
    static readonly string[] s_AlwaysListed = { SCP_ServerIds.Default, SCP_ServerIds.Tavern };

    readonly SenateModel m_Model;

    /// <summary>
    /// 每一顆的探測結果（TASK-0244 之後 Server 不只一顆 ⇒ 本頁一次列全部）。
    /// <para>值為 null ＝ **探針自己失敗**，⛔ 不是沒在跑 —— 原因在 <see cref="m_ProbeErrors"/>。</para>
    /// </summary>
    readonly SortedDictionary<string, ServerStatus?> m_Statuses = new SortedDictionary<string, ServerStatus?>(StringComparer.Ordinal);
    readonly Dictionary<string, string> m_ProbeErrors = new Dictionary<string, string>(StringComparer.Ordinal);
    string? m_ListError;
    string? m_Message;

    public ServerAdminPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key { get { return PageKey; } }
    public override string Title { get { return "Server 管理"; } }
    public override string? MenuGroup { get { return "管理"; } }

    public override void OnPush()
    {
        base.OnPush();
        Reload();
    }

    /// <summary>要列哪幾顆：`KnownIds`（registry ∪ 心跳檔）∪ 常駐清單。</summary>
    List<string> ListIds()
    {
        var aIds = new SortedSet<string>(s_AlwaysListed, StringComparer.Ordinal);
        m_ListError = null;
        try { foreach (string aId in ServerHost.KnownIds(m_Model.RepoRoot)) aIds.Add(SCP_ServerIds.Normalize(aId)); }
        catch (Exception e)
        {
            // ⚠ 列不到 ≠ 只有常駐那幾顆：可能正好漏掉一顆還在寫檔的。
            m_ListError = "⚠ 列舉失敗（" + e.GetType().Name + "：" + e.Message + "）⇒ 下面只有常駐清單那幾顆，"
                          + "⛔ 不代表這棵樹上只有它們。";
        }
        return new List<string>(aIds);
    }

    void Reload()
    {
        m_Statuses.Clear();
        m_ProbeErrors.Clear();
        foreach (string aId in ListIds()) ReloadOne(aId);
    }

    void ReloadOne(string iServerId)
    {
        try { m_Statuses[iServerId] = ServerHost.Probe(m_Model.RepoRoot, iServerId); m_ProbeErrors.Remove(iServerId); }
        catch (Exception e)
        {
            m_Statuses[iServerId] = null;
            // ⛔ 探不到**不等於**沒在跑：前者是「我的量具壞了」，後者是一個讀數。
            //   兩者壓成同一句話的話，這一頁會在自己壞掉的時候叫別人去啟動一顆已經在跑的 Server。
            m_ProbeErrors[iServerId] = "🔴 探針自己失敗了（" + e.GetType().Name + "：" + e.Message
                                       + "）—— ⛔ 這**不是**「Server 沒在跑」，是本頁量不到。";
        }
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (iUi.Button("重新探測全部", "server/reload")) { Reload(); m_Message = "・已重新探測全部"; }
        iUi.Label("｜" + SummaryLabel());
    }

    /// <summary>頂欄一句話：幾顆在跑／共幾顆，有異常就點名。</summary>
    /// <remarks>⚠ 「在跑」與「健康」分開數：版本不符的那顆**確實在跑**，
    /// 把它從在跑裡扣掉會印出「在跑 0／2」而兩顆都在寫檔（Debug 版本頁的 build 天生是 `unversioned`）。</remarks>
    string SummaryLabel()
    {
        int aRunning = 0;
        var aBad = new List<string>();
        foreach (var aKv in m_Statuses)
        {
            ServerStatus? s = aKv.Value;
            if (s != null && s.IsRunning) aRunning++;
            // 量不到／卡住／版本不符 ⇒ 點名；沒在跑不算異常（它可能本來就不需要常駐）
            if (s == null || (s.IsRunning && (s.Heartbeat == null || !s.HeartbeatFresh || !s.BuildMatches)))
                aBad.Add(aKv.Key);
        }
        string aHead = "● 在跑 **" + aRunning + "**／" + m_Statuses.Count + " 顆";
        return aBad.Count == 0 ? aHead : aHead + "　⚠ 要看：" + string.Join("、", aBad);
    }

    /// <summary>一句話狀態 —— 與 `senate server status` 的 `🔢 server_state` 同一組語彙（⛔ 不另造措辭）。</summary>
    static string StateLabel(ServerStatus? iStatus)
    {
        if (iStatus == null) return "**量不到**（探針失敗）";
        if (!iStatus.IsRunning) return "・**沒在跑**";
        if (iStatus.Heartbeat == null) return "⚠ **活著但沒心跳**（卡住）";
        if (!iStatus.HeartbeatFresh) return "⚠ **心跳停了**（卡住）";
        if (!iStatus.BuildMatches) return "⚠ **在跑，但版本不符**";
        return "● **在跑**";
    }

    /// <summary>
    /// 這一顆停掉之後誰會先痛 —— 待確認那句警告的內容。
    /// ⚠ 只寫**這棵程式碼裡看得到的**分工；不認得的 id 就說不認得，⛔ 不猜它負責什麼。
    /// </summary>
    static string StopImpact(string iServerId)
    {
        if (string.Equals(iServerId, SCP_ServerIds.Default, StringComparison.Ordinal))
            return "銀行的寫入端（權威在新銀行，寫入端只有它，TASK-0216 ⑨）⇒ 停掉期間**全體的領薪／扣款會當場失敗**";
        if (string.Equals(iServerId, SCP_ServerIds.Tavern, StringComparison.Ordinal))
            return "酒館的寫入端（`tavern.writer = server` 時所有酒館發文都經過它，TASK-0106）⇒ 停掉之後**下一則發文要先重新拉起它**，拉不起來那一則整筆失敗";
        return "`" + iServerId + "` 這一顆的分工本頁不認得 ⇒ ⛔ 不替它猜停掉的代價";
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Note("所有常駐 Senate Server 的狀態與生命週期。讀數來自 `ServerHost.Probe`，"
               + "與 `senate server status --id <id>` **同一份事實**（⛔ 本頁不自己判活著）。");
        g.Note("⚠ 需要 Server 的 Cmd 在它沒在跑時會**自己拉一顆**（TASK-0267 autostart）⇒ "
               + "「停止」只停得了現在這一顆，⛔ 不是關掉那條路 —— 下一筆需要它的寫入會把它再拉起來。"
               + " 而 autostart **拉不起來就整筆失敗**（不降級）。");

        if (m_Message != null) g.Note(m_Message);
        if (m_ListError != null) g.Note(m_ListError);

        foreach (var aKv in m_Statuses)
        {
            g.Separator();
            DrawServer(g, aKv.Key, aKv.Value);
        }
    }

    void DrawServer(SCP_Ui g, string iServerId, ServerStatus? iStatus)
    {
        g.Title("`" + iServerId + "`　" + StateLabel(iStatus));
        g.Note("・分工：" + StopImpact(iServerId));

        if (iStatus == null)
        {
            g.Note(m_ProbeErrors.TryGetValue(iServerId, out string? aErr) ? aErr : "🔴 量不到（沒有說明）");
            g.Note("⛔ 沒有讀數 —— 修好探針之前，⛔ 不要拿這一格的空白當「沒在跑」。");
            DrawButtons(g, iServerId, iAllowStop: false);
            return;
        }

        DrawReadings(g, iServerId, iStatus);
        DrawButtons(g, iServerId, iAllowStop: iStatus.IsRunning);
    }

    void DrawReadings(SCP_Ui g, string iServerId, ServerStatus s)
    {
        g.Label("· 本 CLI build＝`" + s.MyBuildId + "`");

        if (!s.IsRunning)
        {
            // 「沒在跑」與「認不出來」不同形：後者有東西，只是沒辦法說它是不是 Server。
            if (s.Unverifiable.Count > 0)
            {
                g.Label("？ 沒有 Alive 的 Server，而 registry 裡有 **" + s.Unverifiable.Count
                        + "** 筆 `" + ServerHost.TagFor(iServerId) + "` 身分**驗不出來**"
                        + "（pid=" + string.Join(",", s.Unverifiable.ConvertAll(r => r.Pid.ToString())) + "）");
                g.Note("⛔ 那幾筆不能當活著，也不能當死了 —— 去 ProcessAdminPage 看它們。");
            }
            else g.Label("・Server **沒在跑**");

            if (s.Heartbeat != null)
                g.Note("⚠ 但心跳檔還在（pid=" + s.Heartbeat.Pid + "）—— 上一顆沒收乾淨；"
                       + "「停止」會順手清掉。");
            return;
        }

        g.Label("● 在跑　pid=**" + s.Alive!.Pid + "**　start=" + s.Alive.StartTimeUtcText
                + "　registered_by=" + s.Alive.RegisteredBy);

        if (s.Heartbeat == null)
        {
            g.Note("⚠ 心跳讀不到：" + (s.HeartbeatError ?? "（沒有說明）")
                   + " —— **process 活著但沒在跳 ＝ 卡住**，不是正常。");
            return;
        }

        double? aAge = s.Heartbeat.AgeSeconds();
        g.Label("· 心跳 " + (aAge.HasValue ? aAge.Value.ToString("0.0") + "s 前" : "（算不出年齡）")
                + "　build=`" + s.Heartbeat.BuildId + "`　started=" + s.Heartbeat.StartedAtUtc);

        if (!s.HeartbeatFresh)
            g.Note("⚠ 心跳超過 " + ServerHost.HeartbeatStaleSeconds.ToString("0")
                   + " 秒沒跳 ⇒ 視為**卡住**。出口：停止（等不到自退會 kill）再啟動。");

        if (!s.BuildMatches)
            // 兩顆 exe 在畫面上長得一模一樣 —— 這一行是唯一分得出來的地方。
            g.Note("⚠ **版本不符**：Server build=`" + s.Heartbeat.BuildId + "`，本頁 build=`"
                   + s.MyBuildId + "` ⇒ 先停止再啟動，⛔ 別讓舊的那顆替新的跑。");
    }

    void DrawButtons(SCP_Ui g, string iServerId, bool iAllowStop)
    {
        string aPending = PendingIdFor(iServerId);

        // ── 啟動 ───────────────────────────────────────────
        // ⚠ 在跑的那顆不畫啟動鈕：按了也只會被單例鎖拒絕，而「按得下去」會讓人以為它有作用。
        if (!iAllowStop && g.Button("啟動 `" + iServerId + "`（另開視窗）", "server/start/" + iServerId))
            m_Message = StartDetached(iServerId);

        // ── 停止（二段確認，每一顆各自一格）──────────────────
        if (!iAllowStop)
        {
            g.SetField(aPending, "");
            return;
        }

        bool aArmed = g.FieldValue(aPending, "") == "stop";
        if (g.Button(aArmed ? "⚠ 再按一次＝真的停掉 `" + iServerId + "`" : "停止 `" + iServerId + "`", "server/stop/" + iServerId))
        {
            if (!aArmed)
            {
                g.SetField(aPending, "stop");
                m_Message = "⚠ 待確認停止 `" + iServerId + "`：它是" + StopImpact(iServerId) + "。再按一次才會真的停。";
            }
            else
            {
                g.SetField(aPending, "");
                m_Message = StopNow(iServerId);
                ReloadOne(iServerId);
            }
        }
        if (aArmed && g.Button("取消", "server/stop/cancel/" + iServerId))
        { g.SetField(aPending, ""); m_Message = "・已取消（`" + iServerId + "` 一格都沒動）"; }
    }

    // ===========================================================
    // 區塊職責：啟動 —— **另開一個行程**，⛔ 不在本頁 inline 跑。
    // 🩸 `ServerHost.RunForeground` 是前景常駐迴圈：在這裡叫它，GUI 會停在那一行不再畫面更新，
    //   而畫面上的樣子是「按了沒反應」——跟當掉一模一樣。
    // ⚠ 用**自己這顆 exe**（`Environment.ProcessPath`）：版本天生一致。
    //   寫死 "senate" 走 PATH 的話，可能拉起**另一顆 build 的 exe**，
    //   而那正是本頁上面那行「版本不符」在抱怨的東西（我不想自己造出它）。
    // ===========================================================
    string StartDetached(string iServerId)
    {
        // ⚠ 這道閘量的是**上一次探測**的結果，而那可能是幾秒鐘以前的 ⇒ 現場重採一次。
        //   ⛔ 真正擋得住第二顆的不是本頁，是 Server 自己的單例鎖（OS advisory lock）——
        //   本頁這一格只是讓人少看一個失敗視窗。
        ServerStatus aNow = ServerHost.Probe(m_Model.RepoRoot, iServerId);
        m_Statuses[iServerId] = aNow;
        if (aNow.IsRunning)
            return "・[" + iServerId + "] 已經有一顆在跑（pid=" + aNow.Alive!.Pid + "）⇒ **沒有啟動第二顆**。";

        string? aExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(aExe))
            return "🔴 拿不到自己這顆 exe 的路徑 ⇒ **沒有啟動**（⛔ 不退而去 PATH 撈一顆，"
                   + "那可能是別的 build）。請用終端機跑 `senate server start`。";

        try
        {
            var aPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = aExe,
                // ⚠ `UseShellExecute = true` ＋ 不隱藏視窗：Server 是前景常駐，
                //   它需要一個自己的終端機掛著（也讓人看得見它、Ctrl+C 停得掉）。
                UseShellExecute = true,
                CreateNoWindow = false,
                WorkingDirectory = m_Model.RepoRoot,
            };
            aPsi.ArgumentList.Add("server");
            aPsi.ArgumentList.Add("start");
            // ⚠ 指名才不會起錯一顆 —— 不帶的話不管本頁現在看哪一顆，起來的都是 `main`。
            if (!string.Equals(iServerId, SCP_ServerIds.Default, StringComparison.Ordinal))
            {
                aPsi.ArgumentList.Add("--id");
                aPsi.ArgumentList.Add(iServerId);
            }
            System.Diagnostics.Process.Start(aPsi);
        }
        catch (Exception e)
        {
            return "🔴 啟動失敗（" + e.GetType().Name + "：" + e.Message + "）⇒ **沒有起來**。";
        }

        // ⛔ 這裡刻意**不回報「已啟動」** —— 我只知道「我送出了」。
        //   那兩件事在 2026-08 咬過我一次：回報字串會替自己說謊。
        return "・已送出啟動 `" + iServerId + "`（另一個視窗）。⚠ 這句話的意思是**我按下去了**，"
               + "⛔ 不是「它起來了」—— 按「重新探測全部」看心跳才算數。";
    }

    string StopNow(string iServerId)
    {
        var aLines = new List<string>();
        int aExit;
        try { aExit = ServerHost.Stop(m_Model.RepoRoot, iServerId, s => aLines.Add(s), s => aLines.Add(s)); }
        catch (Exception e) { return "🔴 停止時丟例外（" + e.GetType().Name + "：" + e.Message + "）"; }

        string aTail = aLines.Count > 0 ? "　—— " + aLines[aLines.Count - 1] : "";
        return aExit == 0
            ? "・`" + iServerId + "` 已停止（或本來就沒在跑）" + aTail
            : "🔴 `" + iServerId + "` 停不掉（exit " + aExit + "）" + aTail;
    }
}
