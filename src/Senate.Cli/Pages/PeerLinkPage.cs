// 區塊職責：**跨主機連線後台頁**（TASK-0011）—— 啟動主機／當另一端／關閉、現況、手動交換公告代碼。
// 物理意義：真正連線的是酒館 Server 裡的 SenatePeerJob（每一輪重讀設定檔）⇒ 本頁按下去只是改設定檔，**不必重啟 Server**。
//           按鈕走的是 `senate cmd peer` 同一支（Tim 2026-11-11：一端當主機，從後台介面啟動）。
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

    public PeerLinkPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "跨主機連線";
    public override string? MenuGroup => "酒館";

    string DataRoot => m_Model.AgentCommandsRoot.Value;
    bool HasRoot => DataRoot.Length > 0 && Directory.Exists(DataRoot);
    string Id(string iKey) => $"peer-link/{m_Gen}/{iKey}";

    public override void OnPush() { base.OnPush(); Reload(); }

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

        using (g.Box("這一台", "peer-link/box/config"))
        {
            g.TextField("節點名（英數與 -_.；空白 = 電腦名稱）", m_Saved.Name, Id("name"));
            g.TextField("主機收連線的埠", m_Saved.Port.ToString(), Id("port"));
            g.Toggle("主機連不進來時用 Cloudflare 臨時通道（要先裝 cloudflared）", m_Saved.Tunnel == "auto", Id("tunnel"));
            if (SenatePeerTunnel.Find(m_Saved.CloudflaredPath).Length == 0)
                g.Note("這台找不到 cloudflared：當主機時只會公告區網位址，不在同一個區網的另一台連不進來。");
            using (g.Row())
            {
                if (g.Button(m_Saved.Mode == "host" ? "重新啟動主機（換一場新的公告）" : "啟動主機", "peer-link/btn/host")) Run(Fields(g, "host"));
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

        if (m_Message != null)
            using (g.Box("上一步的結果", "peer-link/box/result"))
                foreach (string l in m_Message.Split('\n')) g.Label(l);
    }
}
