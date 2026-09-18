// 區塊職責：**Server 狀態管理頁** —— 看它活著沒、心跳新不新鮮、build 對不對，並且能啟動／停止。
// 物理意義：Tim 2026-09-18 交辦（「新增一個 Page 用來管理 Server 狀態，至少包含啟動＆關閉」）。
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

namespace Senate.Cli.Pages;

public sealed class ServerAdminPage : SCP_GuiToolPage
{
    public const string PageKey = "server";

    /// <summary>待確認的動作（session 欄位；空 ＝ 沒有待確認）。</summary>
    public const string PendingId = "server/pending";

    readonly SenateModel m_Model;

    ServerStatus? m_Status;
    string? m_Message;

    public ServerAdminPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key { get { return PageKey; } }
    public override string Title { get { return "Server 狀態"; } }
    public override string? MenuGroup { get { return "管理"; } }

    public override void OnPush()
    {
        base.OnPush();
        Reload();
    }

    void Reload()
    {
        try { m_Status = ServerHost.Probe(m_Model.RepoRoot); }
        catch (Exception e)
        {
            m_Status = null;
            // ⛔ 探不到**不等於**沒在跑：前者是「我的量具壞了」，後者是一個讀數。
            //   兩者壓成同一句話的話，這一頁會在自己壞掉的時候叫別人去啟動一顆已經在跑的 Server。
            m_Message = "🔴 探針自己失敗了（" + e.GetType().Name + "：" + e.Message
                        + "）—— ⛔ 這**不是**「Server 沒在跑」，是本頁量不到。";
        }
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (iUi.Button("重新探測", "server/reload")) { Reload(); m_Message = "・已重新探測"; }
        iUi.Label("｜" + StateLabel());
    }

    /// <summary>一句話狀態 —— 與 `senate server status` 的 `🔢 server_state` 同一組語彙（⛔ 不另造措辭）。</summary>
    string StateLabel()
    {
        if (m_Status == null) return "**量不到**（探針失敗）";
        if (!m_Status.IsRunning) return "・**沒在跑**";
        if (m_Status.Heartbeat == null) return "⚠ **活著但沒心跳**（卡住）";
        if (!m_Status.HeartbeatFresh) return "⚠ **心跳停了**（卡住）";
        if (!m_Status.BuildMatches) return "⚠ **在跑，但版本不符**";
        return "● **在跑**";
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Note("Senate Server 的狀態與生命週期。讀數來自 `ServerHost.Probe`，"
               + "與 `senate server status` **同一份事實**（⛔ 本頁不自己判活著）。");
        g.Note("⚠ **2026-09-18 起它是金流的必要條件**：權威在新銀行，而寫入端只有 Server"
               + "（TASK-0216 ⑨）⇒ 停掉它，全體的領薪／扣款會**當場失敗並出聲**（⛔ 不會靜默降級）。");

        if (m_Message != null) g.Note(m_Message);

        if (m_Status == null)
        {
            g.Note("⛔ 沒有讀數 —— 上面那行說了為什麼。修好探針之前，⛔ 不要拿這一頁的空白當「沒在跑」。");
            DrawButtons(g, iAllowStop: false);
            return;
        }

        DrawReadings(g);
        DrawButtons(g, iAllowStop: m_Status.IsRunning);
    }

    void DrawReadings(SCP_Ui g)
    {
        ServerStatus s = m_Status!;
        g.Label("· 本 CLI build＝`" + s.MyBuildId + "`");

        if (!s.IsRunning)
        {
            // 「沒在跑」與「認不出來」不同形：後者有東西，只是沒辦法說它是不是 Server。
            if (s.Unverifiable.Count > 0)
            {
                g.Label("？ 沒有 Alive 的 Server，而 registry 裡有 **" + s.Unverifiable.Count
                        + "** 筆 `" + ServerHost.Tag + "` 身分**驗不出來**"
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

    void DrawButtons(SCP_Ui g, bool iAllowStop)
    {
        // ── 啟動 ───────────────────────────────────────────
        if (g.Button("啟動 Server（另開視窗）", "server/start"))
            m_Message = StartDetached();

        // ── 停止（二段確認）────────────────────────────────
        if (!iAllowStop)
        {
            g.Note("・沒有在跑的 Server ⇒ 沒有東西可以停。");
            g.SetField(PendingId, "");
            return;
        }

        bool aArmed = g.FieldValue(PendingId, "") == "stop";
        if (g.Button(aArmed ? "⚠ 再按一次＝真的停掉" : "停止 Server", "server/stop"))
        {
            if (!aArmed)
            {
                g.SetField(PendingId, "stop");
                m_Message = "⚠ 待確認：**停掉之後全體的金流會當場失敗**（權威在新銀行，寫入端只有它）。"
                            + "再按一次才會真的停。";
            }
            else
            {
                g.SetField(PendingId, "");
                m_Message = StopNow();
                Reload();
            }
        }
        if (aArmed && g.Button("取消", "server/stop/cancel"))
        { g.SetField(PendingId, ""); m_Message = "・已取消（Server 一格都沒動）"; }
    }

    // ===========================================================
    // 區塊職責：啟動 —— **另開一個行程**，⛔ 不在本頁 inline 跑。
    // 🩸 `ServerHost.RunForeground` 是前景常駐迴圈：在這裡叫它，GUI 會停在那一行不再畫面更新，
    //   而畫面上的樣子是「按了沒反應」——跟當掉一模一樣。
    // ⚠ 用**自己這顆 exe**（`Environment.ProcessPath`）：版本天生一致。
    //   寫死 "senate" 走 PATH 的話，可能拉起**另一顆 build 的 exe**，
    //   而那正是本頁上面那行「版本不符」在抱怨的東西（我不想自己造出它）。
    // ===========================================================
    string StartDetached()
    {
        if (m_Status != null && m_Status.IsRunning)
            return "・已經有一顆在跑（pid=" + m_Status.Alive!.Pid + "）⇒ **沒有啟動第二顆**。";

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
            System.Diagnostics.Process.Start(aPsi);
        }
        catch (Exception e)
        {
            return "🔴 啟動失敗（" + e.GetType().Name + "：" + e.Message + "）⇒ **沒有起來**。";
        }

        // ⛔ 這裡刻意**不回報「已啟動」** —— 我只知道「我送出了」。
        //   那兩件事在 2026-08 咬過我一次：回報字串會替自己說謊。
        return "・已送出啟動（另一個視窗）。⚠ 這句話的意思是**我按下去了**，"
               + "⛔ 不是「它起來了」—— 按「重新探測」看心跳才算數。";
    }

    string StopNow()
    {
        var aLines = new List<string>();
        int aExit;
        try { aExit = ServerHost.Stop(m_Model.RepoRoot, s => aLines.Add(s), s => aLines.Add(s)); }
        catch (Exception e) { return "🔴 停止時丟例外（" + e.GetType().Name + "：" + e.Message + "）"; }

        string aTail = aLines.Count > 0 ? "　—— " + aLines[aLines.Count - 1] : "";
        return aExit == 0
            ? "・已停止（或本來就沒在跑）" + aTail
            : "🔴 停不掉（exit " + aExit + "）" + aTail;
    }
}
