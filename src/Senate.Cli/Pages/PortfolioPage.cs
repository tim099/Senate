// 區塊職責：**投資組合頁**（TASK-0371）—— 工具列選 persona，只顯示那個人的持倉、成本、現值與報酬率。
// 物理意義：數字全部來自 `SCP_Portfolio.Build`（與 `senate cmd portfolio op=show` 同一支）⇒ 頁面與指令同源。
//           本頁**純讀**：開帳（寫 opening.json）只走指令 `portfolio op=open --arg confirm=1` —— 那是只能做一次的事，
//           ⛔ 不放一顆按得到的鈕在每天會打開的頁面上。
// 數值影響：零寫入（除了 ui_session 的選單狀態）。
// 🩸 守衛：
//   ① **顯式 key 一律帶 `portfolio/` 前綴**：顯式 key 是全域的、IdScope 不會替它加前綴，
//      沒前綴就會跟別頁（例：銀行頁的 persona 選單）靜默共用同一格。
//   ② 工具列在 DrawContent **之前**跑 ⇒ 資料在 OnPush 與工具列裡載入，⛔ 不等 DrawContent（否則第一幀選單是空的）。
//   ③ 「帳上與紀錄不符」的差額、「尚未開帳」都要**看得見**，⛔ 不畫成 0。
// @doc-sync: <SCP_Core>/Docs~/Portfolio.md（頁面欄位與成本規則）
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Cmd;
using SCP.Core.Gui;
using SCP.Core.Letters;
using SCP.Core.Market;
using SCP.Core.Paths;
using SCP.Core.Voucher;

namespace Senate.Cli.Pages;

public sealed class PortfolioPage : SCP_GuiToolPage
{
    public const string PageKey = "portfolio";
    public const string PersonaKey = "portfolio/sel/persona";
    public const string CcyKey = "portfolio/sel/ccy";

    readonly SenateModel m_Model;
    string m_DataRoot = "";
    string m_LettersRoot = "";
    bool m_Loaded;
    string? m_LoadError;
    SCP_MarketRateConfig? m_Config;
    List<string> m_Personas = new List<string>();
    readonly List<string> m_PoolWarnings = new List<string>();

    string m_Sel = "";
    string m_Ccy = "USD";

    /// <summary>重算結果快取：頁面每幀重畫，⛔ 不每幀把事件簿整個讀一遍。鍵＝persona|ccy，重新讀取時清掉。</summary>
    string m_ViewKey = "";
    SCP_PortfolioView? m_View;

    public PortfolioPage(SenateModel iModel) : base()
    {
        m_Model = iModel;
    }

    public override string Key => PageKey;
    public override string Title => "投資組合";
    public override string? MenuGroup => "銀行";

    public override void OnPush()
    {
        base.OnPush();
        Load();
    }

    void Load()
    {
        m_Loaded = true;
        m_LoadError = null;
        m_ViewKey = "";
        m_View = null;
        m_PoolWarnings.Clear();

        // ⛔ 不寫死後備路徑：解析不到就明說（寫死的後備會在另一台機器上安靜地讀到別的專案）
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        m_LettersRoot = m_Model.LettersRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !System.IO.Directory.Exists(m_DataRoot))
        { m_LoadError = $"解析不到 AgentCommands 資料根（{m_Model.AgentCommandsRoot.Error ?? m_DataRoot}）—— 到「路徑管理」頁設定"; return; }
        if (string.IsNullOrEmpty(m_LettersRoot) || !System.IO.Directory.Exists(m_LettersRoot))
        { m_LoadError = $"解析不到 letters 根（{m_Model.LettersRoot.Error ?? m_LettersRoot}）—— 到「路徑管理」頁設定"; return; }

        m_Config = SCP_MarketRateCache.Load(m_DataRoot, out string? aErr);
        if (aErr != null) { m_LoadError = "匯率快取讀不了：" + aErr; m_Config = null; return; }

        m_Personas = SCP_PersonaProfile.PoolNames(m_LettersRoot, w => m_PoolWarnings.Add(w));
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (!m_Loaded) Load();
        OpenFolderButton(iUi, m_DataRoot.Length > 0 ? SCP_Portfolio.PortfolioDir(m_DataRoot) : null, "portfolio/open-dir");
        if (iUi.Button("重新讀取", "portfolio/reload")) Load();
        if (m_LoadError != null) return;

        string aPick = iUi.Dropdown("Persona", m_Personas, m_Sel, PersonaKey);
        if (aPick != m_Sel) m_Sel = aPick;

        var aCcys = new List<string> { "USD" };
        foreach (var kv in m_Config!.Quotes)
            if (kv.Key != "USD" && SCP_MarketRateCache.TryGetQuote(m_Config, kv.Key, out _)) aCcys.Add(kv.Key);
        aCcys.Sort(StringComparer.Ordinal);
        string aCcy = iUi.Dropdown("顯示幣別", aCcys, m_Ccy, CcyKey);
        if (aCcy.Length > 0 && aCcy != m_Ccy) m_Ccy = aCcy;

        iUi.Label(m_Sel.Length == 0 ? "｜（未選 persona）" : $"｜{m_Sel}　以 {m_Ccy} 顯示");
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Note("成本＝加權平均成本：買進／獲配時以當下市值計入；賣出時依平均成本扣除並計已實現損益；花用依平均成本扣除、不算已實現。");
        g.Note("上線時估值＝開帳那一刻的現值（之前沒有任何買入紀錄，Tim 2026-10-01 拍板以此為成本）。現價取 Bid（賣出可得）。");
        g.Note("只追蹤有報價的券；沒有報價的券列在下方、不估值。");
        foreach (string w in m_PoolWarnings) g.Note("⚠ " + w);
        if (m_LoadError != null) { g.Label("⚠ " + m_LoadError); return; }
        if (m_Sel.Length == 0) { g.Label("從上方工具列選一位 persona。"); return; }

        if (!SCP_Cmd_Portfolio.TryCcy(m_Config!, m_Ccy, out string aCcy, out decimal aUsdPerCcy, out string? aCcyErr))
        { g.Label("⚠ " + aCcyErr); return; }

        string aKey = m_Sel + "|" + aCcy;
        if (m_View == null || m_ViewKey != aKey)
        {
            m_View = SCP_Portfolio.Build(m_DataRoot, new SCP_LettersRoot(m_LettersRoot), m_Sel, m_Config!, DateTime.UtcNow);
            m_ViewKey = aKey;
        }
        var v = m_View;

        g.Separator();
        g.Label(v.OpeningExists
            ? $"開帳快照：{v.OpeningAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}（之前的持倉以這一刻的現值為成本）"
            : "⚠ 尚未開帳 ⇒ 既有持倉沒有成本，只有之後的交易算得出報酬。開帳：`senate cmd portfolio --arg op=open --arg confirm=1`（只能一次）");

        decimal aCost = v.TotalCostHeldUsd, aVal = v.TotalValueHeldUsd;
        decimal? aRoi = aCost > 0m ? (aVal - aCost) / aCost : (decimal?)null;
        g.Label($"合計（只算有紀錄的部分）：成本 {M(aCost, aUsdPerCcy)}　現值 {M(aVal, aUsdPerCcy)}　"
                + $"未實現 {S(aVal - aCost, aUsdPerCcy)}（{SCP_Cmd_Portfolio.Pct(aRoi)}）　已實現 {S(v.TotalRealizedUsd, aUsdPerCcy)}　（{aCcy}）");

        g.Separator();
        g.Label($"可估值的持倉（{v.Positions.Count}）");
        using (g.Table("券", "持有", $"成本 ({aCcy})", $"均價 ({aCcy})", $"現價 ({aCcy})", $"現值 ({aCcy})",
                       "未實現", "報酬率", "已實現", "成本來源"))
        {
            foreach (var p in v.Positions)
            {
                string aAvg = p.HeldTrackedE8 > 0
                    ? (p.CostHeldUsd / ((decimal)p.HeldTrackedE8 / SCP_VoucherBook.FractionScale) / aUsdPerCcy)
                        .ToString("#,0.########", CultureInfo.InvariantCulture)
                    : "—";
                g.TableRow(p.Symbol, SCP_Cmd_Portfolio.Units(p.ActualE8), M(p.CostHeldUsd, aUsdPerCcy), aAvg,
                           (p.BidUsd / aUsdPerCcy).ToString("#,0.########", CultureInfo.InvariantCulture),
                           M(p.ActualValueUsd, aUsdPerCcy), S(p.UnrealizedUsd, aUsdPerCcy),
                           SCP_Cmd_Portfolio.Pct(p.Roi), S(p.RealizedUsd, aUsdPerCcy), p.BasisLabel);
                if (p.DriftE8 != 0)
                    g.TableRow("  ⚠ 與紀錄不符", (p.DriftE8 > 0 ? "+" : "") + SCP_Cmd_Portfolio.Units(p.DriftE8),
                               "來源不明", "", "", "", "不計入", "", "", "");
                if (p.UntrackedDisposedE8 > 0)
                    g.TableRow("  ⚠ 賣出超出紀錄", SCP_Cmd_Portfolio.Units(p.UntrackedDisposedE8),
                               "成本不明", "", "", "", "未計已實現", "", "", "");
            }
        }
        if (v.Positions.Count == 0) g.Note("（沒有可估值的持倉）");

        // ⚠ Fold 只畫外框；內容要自己看 `.Open` 才跳過（不看的話收起來也照樣建內容）
        using (var aFold = g.Fold($"沒有報價的券（{v.Unquoted.Count}，不估值）", "portfolio/fold/unquoted", iDefaultOpen: false))
        {
            if (aFold.Open)
            using (g.Table("券", "持有"))
                foreach (var kv in v.Unquoted) g.TableRow(kv.Key, SCP_Cmd_Portfolio.Units(kv.Value));
        }

        using (var aFold = g.Fold($"交易紀錄（{v.Events.Count}，新→舊，最多 50 筆）", "portfolio/fold/events", iDefaultOpen: true))
        if (aFold.Open)
        {
            if (v.Events.Count == 0) g.Note("（還沒有交易紀錄 —— 紀錄從本功能上線才開始記）");
            for (int i = 0; i < v.Events.Count && i < 50; i++) g.Label(SCP_Cmd_Portfolio.Describe(v.Events[i]));
        }

        foreach (string s in v.Problems) g.Note("⚠ " + s);
    }

    static string M(decimal iUsd, decimal iUsdPerCcy) => SCP_Cmd_Portfolio.Money(iUsd / iUsdPerCcy);
    static string S(decimal iUsd, decimal iUsdPerCcy) => SCP_Cmd_Portfolio.Signed(iUsd / iUsdPerCcy);
}
