// 區塊職責：**跨主機連線後台頁**（TASK-0011）—— 啟動主機／當另一端／關閉、現況、手動交換公告代碼。
// 物理意義：真正連線的是酒館 Server 裡的 SenatePeerJob（每一輪重讀設定檔）⇒ 本頁按下去只是改設定檔，**不必重啟 Server**。
//           按鈕走的是 `senate cmd peer` 同一支（Tim 2026-11-11：一端當主機，從後台介面啟動）。
//           「服務放在哪一台」那一格（FAR-53）：Task 寫在哪一台跟誰當主機分開選；這台當 Task Server 時勾允許哪些節點。
//           ⚠ 沒有下拉選單元件 ⇒ 用一排按鈕選（這台／不用連線／每個在線的節點各一顆）。
// 數值影響：按鈕才寫檔（`<機密資料夾>/peer/`）；畫面每次重畫都重讀狀態檔（很小）。
// ⚠ 視窗文字不放 emoji（ImWchar 16 位元，U+FFFF 以上畫成方框 —— TASK-0356）。
#nullable enable
using Senate.Core;
using SCP.Core.Cmd;
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed class PeerLinkPage : SCP_GuiToolPage
{
    public const string PageKey = "peer-link";

    readonly SenateModel m_Model;
    int m_Gen;
    PeerConfig m_Saved = new();
    string? m_Message;
    string? m_Code;
    Task<SCP_CmdResult>? m_ProbeJob;     // 量網路要幾秒（等路由器與 STUN 回應）⇒ 視窗模式放背景，不卡畫面
    List<string>? m_Probe;               // 最近一次量到的（null ＝ 這次開頁還沒量）
    string m_ProbeAt = "";

    public PeerLinkPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "跨主機連線";
    public override string? MenuGroup => "酒館";

    string DataRoot => m_Model.AgentCommandsRoot.Value;
    bool HasRoot => DataRoot.Length > 0 && Directory.Exists(DataRoot);
    string Id(string iKey) => $"peer-link/{m_Gen}/{iKey}";

    public override void OnPush()
    {
        base.OnPush();
        Reload();
        // 開頁就自動量一次（Tim 2026-11-11：不要叫人去打指令）。文字模式每一次呼叫都是新行程 ⇒ 不自動量，留按鈕。
        if (HasRoot && SCP_GuiHost.RedrawsContinuously) StartProbe();
    }

    /// <summary>量這台的對外網路（`senate cmd peer --arg op=probe`，唯讀：不開埠、不改路由器）。</summary>
    void StartProbe()
    {
        if (m_ProbeJob != null) return;
        var aArgs = new Dictionary<string, string> { ["op"] = "probe", ["data_root"] = DataRoot };
        if (SCP_GuiHost.RedrawsContinuously) { m_ProbeJob = Task.Run(() => SCP_CmdRegistry.Dispatch("peer", aArgs)); return; }
        try { HarvestProbe(SCP_CmdRegistry.Dispatch("peer", aArgs)); }
        catch (Exception e) { m_Probe = new List<string> { "量的時候出錯了：" + e.GetType().Name + ": " + e.Message }; }
    }

    void PumpProbe()
    {
        if (m_ProbeJob == null || !m_ProbeJob.IsCompleted) return;
        Task<SCP_CmdResult> j = m_ProbeJob;
        m_ProbeJob = null;
        try { HarvestProbe(j.Result); }
        catch (Exception e) { m_Probe = new List<string> { "量的時候出錯了：" + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message }; }
    }

    void HarvestProbe(SCP_CmdResult r)
    {
        m_Probe = r.Lines.Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)).Select(Plain).ToList();
        m_ProbeAt = DateTime.Now.ToString("HH:mm:ss");
    }

    void Reload()
    {
        m_Gen++;
        m_Code = null;
        if (HasRoot) m_Saved = SenatePeerStore.LoadConfig(DataRoot);
    }

    protected override void TopBarButtons(SCP_Ui g)
    {
        if (g.Button("重新讀", "peer-link/reload")) { m_Message = null; Reload(); }
    }

    /// <summary>跑一次 `senate cmd peer`，把結果的文字留在畫面上。</summary>
    void Run(Dictionary<string, string> iArgs)
    {
        iArgs["data_root"] = DataRoot;
        try
        {
            SCP_CmdResult r = SCP_CmdRegistry.Dispatch("peer", iArgs);
            m_Message = string.Join("\n", r.Lines.Where(l => l.Length > 0).Select(Plain));
        }
        catch (Exception e) { m_Message = "那一步出錯了：" + e.GetType().Name + ": " + e.Message; }
        Reload();
    }

    // 指令輸出裡的 emoji 換成文字（本頁畫不出來）
    static string Plain(string s)
        => s.Replace("✓", "[好]").Replace("✗", "[不行]").Replace("⚠", "[注意]").Replace("⛔", "[不要]").Replace("📡", "").Replace("📴", "");

    Dictionary<string, string> Fields(SCP_Ui g, string iOp)
    {
        var a = new Dictionary<string, string> { ["op"] = iOp };
        string aName = g.FieldValue(Id("name"), m_Saved.Name).Trim();
        string aPort = g.FieldValue(Id("port"), m_Saved.Port.ToString()).Trim();
        if (aName.Length > 0) a["name"] = aName;
        if (aPort.Length > 0) a["port"] = aPort;
        a["tunnel"] = g.ToggleValue(Id("tunnel"), m_Saved.Tunnel == "auto") ? "auto" : "off";
        a["port_map"] = g.ToggleValue(Id("portmap"), m_Saved.PortMap == "auto") ? "auto" : "off";
        return a;
    }

    protected override void DrawContent(SCP_Ui g)
    {
        if (!HasRoot) { g.Note("[注意] 解不出資料根 —— 先到「路徑」頁確認。"); return; }

        g.Paragraph("一台當主機，另一台連上去：兩邊在線的人互相看得到，酒館的訊息經主機轉發到每一台，不必再靠 Discord 同步。"
                    + "主機啟動後會把連線位址貼到 Discord（主頻道的分類綁的 webhook），另一台的 Discord Inbound 收到就自動連上。");
        g.Note("[注意] 看得到那則公告的人就連得上主機 —— 公告要貼在只有自己人的頻道。");

        using (g.Box("現況", "peer-link/box/status"))
        {
            foreach (string l in Cmd_Peer.StatusLines(DataRoot, out _).Skip(1)) g.Label(Plain(l));
        }

        PumpProbe();
        using (g.Box("這台的網路（適不適合當主機）", "peer-link/box/probe"))
        {
            if (m_ProbeJob != null) g.Note("量測中…（等路由器與 STUN 回應，約 5 秒；只讀，不開埠）");
            else if (m_Probe == null) g.Note("還沒量。按下面的按鈕量一次（只讀：不開埠、不改路由器）。");
            else
            {
                foreach (string l in m_Probe) g.Label(l);
                g.Note("量的時間：" + m_ProbeAt + "。最後一行是判語；不適合當主機的那一台就按「當另一端」。");
            }
            if (m_ProbeJob == null && g.Button(m_Probe == null ? "量這台的網路" : "重新量", "peer-link/btn/probe")) StartProbe();
        }

        using (g.Box("這一台", "peer-link/box/config"))
        {
            g.TextField("節點名（英數與 -_.；空白 = 電腦名稱）", m_Saved.Name, Id("name"));
            g.TextField("主機收連線的埠", m_Saved.Port.ToString(), Id("port"));
            g.Toggle("當主機時請路由器開對外的埠（UPnP；公網位址會寫進公告，關主機時收回）", m_Saved.PortMap == "auto", Id("portmap"));
            g.Toggle("主機連不進來時用 Cloudflare 臨時通道（要先裝 cloudflared）", m_Saved.Tunnel == "auto", Id("tunnel"));
            if (SenatePeerTunnel.Find(m_Saved.CloudflaredPath).Length == 0)
                g.Note("這台找不到 cloudflared：路由器的對外埠也開不成的話，不在同一個區網的另一台連不進來。");
            using (g.Row())
            {
                if (g.Button(m_Saved.Mode == "host" ? "重新啟動主機（換一場新的公告）" : "啟動主機", "peer-link/btn/host"))
                {
                    Run(Fields(g, "host"));
                    StartProbe();   // 啟動主機時順便再量一次：判語就在上面那一格
                }
                if (g.Button("當另一端（等主機的公告）", "peer-link/btn/client")) Run(Fields(g, "client"));
                if (m_Saved.Mode != "off" && g.Button("關閉", "peer-link/btn/off")) Run(new Dictionary<string, string> { ["op"] = "off" });
            }
        }

        using (g.Box("沒有 Discord 時：手動交換公告代碼", "peer-link/box/manual"))
        {
            if (m_Saved.Mode == "host")
            {
                if (g.Button("顯示這台主機的公告代碼", "peer-link/btn/code"))
                {
                    string aCode = SenatePeerStore.HostGet(DataRoot, "code");
                    m_Code = aCode.Length > 0 ? aCode : "（還沒產生：酒館 Server 還沒跑到那一輪，或沒在跑）";
                }
                if (m_Code != null)
                {
                    g.Note("把下面整段交給另一台（[注意] 拿到的人就連得上，只交給自己人）：");
                    g.TextArea("公告代碼", m_Code, Id("code-out"), 4);
                }
            }
            else
            {
                g.TextArea("貼上主機的公告代碼（SENATE-HOST1 …）", "", Id("code-in"), 4);
                if (g.Button("收下代碼並連線", "peer-link/btn/join"))
                {
                    string aIn = g.FieldValue(Id("code-in"), "").Trim();
                    if (aIn.Length == 0) m_Message = "還沒有貼代碼";
                    else Run(new Dictionary<string, string> { ["op"] = "join", ["code"] = aIn });
                }
            }
        }

        DrawServices(g);

        if (m_Message != null)
            using (g.Box("上一步的結果", "peer-link/box/result"))
                foreach (string l in m_Message.Split('\n')) g.Label(l);
    }

    /// <summary>「服務放在哪一台」：Task Server 選哪一台；這台是 Task Server 時，允許哪些節點送 Task 指令過來。</summary>
    void DrawServices(SCP_Ui g)
    {
        using (g.Box("服務放在哪一台", "peer-link/box/services"))
        {
            g.Paragraph("主機只是被連的那一端；Task 寫在哪一台另外選。選了別台之後，這台的 task／tasks 指令（含任務頁的按鈕、commit 的 Fixes TASK-n）"
                        + "都會經連線送到那一台執行；那一台不在線就直接說，不會改寫在這台。");
            g.Label(Plain(Cmd_Peer.TaskServiceText(DataRoot, m_Saved)).Replace("**", ""));
            g.Note("[注意] 切換 Task Server 不會搬單子。要換台，先用 sync.tasks 或複製 Tasks/ 把單子搬過去。");

            var aView = SCP.Core.Tavern.SCP_PeerPresence.Read(DataRoot);
            Dictionary<string, string> aServers = SenatePeerStore.PresenceServers(DataRoot, SenatePeerLink.SvcTask);
            using (g.Row())
            {
                if (m_Saved.TaskServer.Length > 0 && g.Button("Task 寫在這台（不用連線）", "peer-link/btn/task-local"))
                    Run(new Dictionary<string, string> { ["op"] = "set", ["task_server"] = "-" });
                if (!m_Saved.ServesTask && g.Button("這台當 Task Server", "peer-link/btn/task-self"))
                    Run(new Dictionary<string, string> { ["op"] = "set", ["task_server"] = PeerConfig.Self });
            }
            if (aView.Live)
                foreach (var n in aView.Nodes)
                {
                    if (n.Id == m_Saved.TaskServer) continue;
                    string aTag = aServers.ContainsKey(n.Id) ? "（它說自己是 Task Server）" : "（它沒有說自己是 Task Server）";
                    if (g.Button($"改用 {n.Name} 當 Task Server {aTag}", "peer-link/btn/task-node/" + n.Id))
                        Run(new Dictionary<string, string> { ["op"] = "set", ["task_server"] = n.Id });
                }
            else g.Note("在線名單不是新的（酒館 Server 沒在跑，或線沒接上）⇒ 列不出別台可以選。");

            if (m_Saved.ServesTask)
            {
                g.Label("允許哪些節點送 Task 指令過來（沒勾的一律擋下）：");
                var aIds = new List<(string Id, string Name)>();
                foreach (var n in aView.Nodes) aIds.Add((n.Id, n.Name));
                foreach (string id in m_Saved.TaskClients)
                    if (!aIds.Any(x => x.Id == id)) aIds.Add((id, SenateTaskServer.KnownName(DataRoot, id) + "（現在不在線）"));
                if (aIds.Count == 0) g.Note("現在沒有在線的節點可以勾。");
                foreach (var (id, name) in aIds)
                    g.Toggle($"{name}（{id}）", m_Saved.TaskClients.Contains(id), Id("client/" + id));
                if (aIds.Count > 0 && g.Button("儲存允許名單", "peer-link/btn/task-clients"))
                {
                    List<string> aPick = aIds.Where(x => g.ToggleValue(Id("client/" + x.Id), m_Saved.TaskClients.Contains(x.Id))).Select(x => x.Id).ToList();
                    Run(new Dictionary<string, string> { ["op"] = "set", ["task_clients"] = aPick.Count == 0 ? "-" : string.Join(",", aPick) });
                }
            }
        }
    }
}
