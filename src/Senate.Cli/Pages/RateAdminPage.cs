// 區塊職責：**匯率與券互換市場後台頁**（TASK-0272）。
// 物理意義：Senate 後台檢視各券種相對於基準幣（USD）與任意 A 券之折算匯率，
//           支援手填維護 Bid/Ask 報價，二段確認（Pending 閘門）原子落盤 `Market/rates_cache.json`。
// 數值影響：讀取無 IO（已快取）或單次讀檔；寫入走原子儲存（tmp → replace）。
// 🩸 守衛：
//   ① **無緩存資訊時，視為無法兌換**（Tim 2026-09-22 拍板：並非所有券都能互相兌換）。
//   ② **二段確認**：動設定與手填匯率需確認兩次，避免誤點覆蓋報價。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Gui;
using SCP.Core.Market;
using Senate.Core;

namespace Senate.Cli.Pages;

public sealed class RateAdminPage : SCP_GuiToolPage
{
    public const string PageKey = "rates";
    public const string PendingId = "rates/pending";
    public const string BaseVoucherId = "rates/base_voucher";

    public const string EditSymbolId = "rates/edit/symbol";
    public const string EditBidId = "rates/edit/bid";
    public const string EditAskId = "rates/edit/ask";
    public const string EditEnabledId = "rates/edit/enabled";

    readonly SenateModel m_Model;

    string m_DataRoot = "";
    SCP_MarketRateConfig? m_Config;
    string? m_LoadError;
    string? m_Message;

    public RateAdminPage(SenateModel iModel) : base()
    {
        m_Model = iModel;
    }

    public override string Key => PageKey;
    public override string Title => "匯率與券市場";
    public override string? MenuGroup => "銀行";

    public override void OnPush()
    {
        base.OnPush();
        Load();
    }

    void Load()
    {
        m_LoadError = null;
        m_Message = null;

        // 推導資料根
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot))
        {
            string aCandidate = Path.Combine(m_Model.RepoRoot, "AgentCommands");
            if (Directory.Exists(aCandidate)) m_DataRoot = aCandidate;
            else m_DataRoot = "D:/Unity/Bar/AgentCommands";
        }

        if (!Directory.Exists(m_DataRoot))
        {
            m_LoadError = $"找不到 AgentCommands 資料根（{m_DataRoot}）";
            return;
        }

        m_Config = SCP_MarketRateCache.Load(m_DataRoot, out string? aErr);
        if (aErr != null) m_LoadError = aErr;
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Title("【匯率與券互換市場後台】");
        g.Note("查閱與管理各券種相對於基準幣（USD）與任意指定基準券（A 券）之雙向折算比率。");
        g.Note("・規則：無緩存資訊之券種視為無法兌換（並非所有券都能互相兌換）。");

        if (m_LoadError != null)
        {
            g.Note($"[錯誤] 載入失敗：{m_LoadError}");
            if (g.Button("重新載入", "rates/reload")) Load();
            return;
        }

        if (m_Config == null)
        {
            g.Note("（尚未載入設定）");
            return;
        }

        if (m_Message != null)
        {
            g.Note(m_Message);
        }

        DrawTopControls(g);
        g.Separator();

        DrawRateMatrix(g);
        g.Separator();

        DrawManualEditPanel(g);
    }

    void DrawTopControls(SCP_Ui g)
    {
        using (g.Row())
        {
            g.Label($"基準貨幣：**{m_Config!.BaseCurrency}**");
            g.Label($"系統狀態：{(m_Config.FxSystemEnabled ? "【互換已啟用】" : "【互換已停用】")}");
            g.Label($"快取更新時間：`{m_Config.UpdatedAtUtc}`");
        }

        string aPending = g.FieldValue(PendingId, "");

        using (g.Row())
        {
            if (g.Button("重新讀取", "rates/btn/refresh"))
            {
                Load();
                m_Message = "・已重新讀取快取";
            }

            if (aPending == "toggle_fx")
            {
                if (g.Button(m_Config.FxSystemEnabled ? "確認停用系統？" : "確認啟用系統？", "rates/btn/confirm_toggle"))
                {
                    m_Config.FxSystemEnabled = !m_Config.FxSystemEnabled;
                    if (SCP_MarketRateCache.Save(m_DataRoot, m_Config, out string? aSaveErr))
                    {
                        m_Message = $"・匯率互換系統已設為：{(m_Config.FxSystemEnabled ? "啟用" : "停用")}";
                    }
                    else
                    {
                        m_Message = $"[錯誤] 儲存失敗：{aSaveErr}";
                    }
                    g.SetField(PendingId, "");
                }
                if (g.Button("取消", "rates/btn/cancel_toggle"))
                {
                    g.SetField(PendingId, "");
                }
            }
            else
            {
                if (g.Button(m_Config.FxSystemEnabled ? "停用互換系統" : "啟用互換系統", "rates/btn/req_toggle"))
                {
                    g.SetField(PendingId, "toggle_fx");
                }
            }
        }
    }

    void DrawRateMatrix(SCP_Ui g)
    {
        g.Label("全券種折算匯率表（以 A 券為基準）");

        // 收集所有已知券種代號
        var aAllSymbols = new List<string>(m_Config!.Quotes.Keys);
        if (!aAllSymbols.Contains("USD")) aAllSymbols.Insert(0, "USD");
        aAllSymbols.Sort(StringComparer.OrdinalIgnoreCase);

        // 讀取當前選擇之基準券
        string aCurrentBase = g.FieldValue(BaseVoucherId, "").ToUpperInvariant();
        if (string.IsNullOrEmpty(aCurrentBase) || !aAllSymbols.Contains(aCurrentBase))
        {
            aCurrentBase = aAllSymbols.Contains("BTC") ? "BTC" : aAllSymbols[0];
            g.SetField(BaseVoucherId, aCurrentBase);
        }

        using (g.Row())
        {
            g.Label("選取基準券 (A 券)：");
            foreach (string s in aAllSymbols)
            {
                bool isSelected = string.Equals(s, aCurrentBase, StringComparison.OrdinalIgnoreCase);
                string aLabel = isSelected ? $"【 {s} 】" : s;
                if (g.Button(aLabel, "rates/sel/" + s.ToLowerInvariant()))
                {
                    aCurrentBase = s;
                    g.SetField(BaseVoucherId, s);
                }
            }
        }

        using (g.Table("券種", "買入價 (Bid USD)", "賣出價 (Ask USD)", $"折算率 (賣 1 {aCurrentBase} 可換)", $"買入價 (買 1 該券需付 {aCurrentBase})", "狀態", "資料來源"))
        {
            foreach (string s in aAllSymbols)
            {
                if (!m_Config.Quotes.TryGetValue(s, out var q))
                {
                    if (s == "USD") q = new SCP_RateQuote { Symbol = "USD", Bid = 1, Ask = 1, IsEnabled = true, Source = "builtin" };
                    else continue;
                }

                string aSellRateStr = "—";
                string aBuyRateStr = "—";
                string aStatusStr = "無法兌換";

                if (SCP_MarketRateCache.TryGetPairRate(m_Config, aCurrentBase, s, out decimal aSellRate, out decimal aBuyRate, out _))
                {
                    aSellRateStr = aSellRate.ToString("0.########", CultureInfo.InvariantCulture);
                    aBuyRateStr = aBuyRate.ToString("0.########", CultureInfo.InvariantCulture);
                    aStatusStr = "可兌換";
                }
                else
                {
                    if (!q.IsEnabled) aStatusStr = "已停用";
                    else aStatusStr = "無法兌換";
                }

                g.TableRow(
                    q.Symbol,
                    q.Bid.ToString("0.########", CultureInfo.InvariantCulture),
                    q.Ask.ToString("0.########", CultureInfo.InvariantCulture),
                    aSellRateStr,
                    aBuyRateStr,
                    aStatusStr,
                    q.Source
                );
            }
        }
    }

    void DrawManualEditPanel(SCP_Ui g)
    {
        g.Label("手填維護券種報價（相對於 USD）");

        string aSymbol = g.TextField("券種代碼 (Symbol，如 BTC/GOLD)", g.FieldValue(EditSymbolId, ""), EditSymbolId).Trim().ToUpperInvariant();
        string aBidStr = g.TextField("買入價 Bid USD (賣券換 USD 單價)", g.FieldValue(EditBidId, ""), EditBidId).Trim();
        string aAskStr = g.TextField("賣出價 Ask USD (支付 USD 買券單價)", g.FieldValue(EditAskId, ""), EditAskId).Trim();
        bool aEnabled = g.Toggle("開放兌換 (IsEnabled)", g.ToggleValue(EditEnabledId, true), EditEnabledId);

        string aPending = g.FieldValue(PendingId, "");

        if (aPending == "save_rate")
        {
            using (g.Row())
            {
                if (g.Button($"確認將 `{aSymbol}` (Bid:{aBidStr}, Ask:{aAskStr}) 寫入快取？", "rates/btn/confirm_save"))
                {
                    ExecuteSaveQuote(aSymbol, aBidStr, aAskStr, aEnabled);
                    g.SetField(PendingId, "");
                }
                if (g.Button("取消", "rates/btn/cancel_save"))
                {
                    g.SetField(PendingId, "");
                }
            }
        }
        else
        {
            if (g.Button("儲存 / 更新報價 (二段確認)", "rates/btn/req_save"))
            {
                if (string.IsNullOrEmpty(aSymbol))
                {
                    m_Message = "・請先填寫券種代碼";
                }
                else if (!decimal.TryParse(aBidStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal bid) || bid <= 0)
                {
                    m_Message = "・買入價 Bid 必須為大於 0 之數字";
                }
                else if (!decimal.TryParse(aAskStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal ask) || ask <= 0)
                {
                    m_Message = "・賣出價 Ask 必須為大於 0 之數字";
                }
                else if (ask < bid)
                {
                    m_Message = "・賣出價 Ask 不得低於買入價 Bid（避免倒掛）";
                }
                else
                {
                    g.SetField(PendingId, "save_rate");
                }
            }
        }
    }

    void ExecuteSaveQuote(string iSymbol, string iBidStr, string iAskStr, bool iEnabled)
    {
        if (!decimal.TryParse(iBidStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal aBid)) return;
        if (!decimal.TryParse(iAskStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal aAsk)) return;

        var aQuote = new SCP_RateQuote
        {
            Symbol = iSymbol,
            BaseCurrency = "USD",
            Bid = aBid,
            Ask = aAsk,
            IsEnabled = iEnabled,
            Source = "admin_ui",
            UpdatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };

        m_Config!.Quotes[iSymbol] = aQuote;

        if (SCP_MarketRateCache.Save(m_DataRoot, m_Config, out string? aSaveErr))
        {
            m_Message = $"✅ 券種 `{iSymbol}` 報價已成功更新並落盤！";
        }
        else
        {
            m_Message = $"🔴 儲存失敗：{aSaveErr}";
        }
    }
}
