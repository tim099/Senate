// 區塊職責：**對拍設定頁**（TASK-0397）—— 勾選 `senate selftest` 預設要跑哪些。
// 物理意義：跟 `senate selftest --enable／--disable` 是**同一份檔**（`SenateData/config/selftest.json`）、同一組判準
//           （SelfTestConfig／SelfTest.ListWithStatus），本頁只排版。語意與「新增測試的規矩」見 Docs/API/Cli_Reference.md › selftest。
// 數值影響：畫面不寫檔；只有按「存到設定檔」才寫，而且**只寫跟目前狀態不同的那幾項**
//           （沒動過的「新」項不會被這一按變成常駐或關閉）。
// ⚠ 勾＝常駐（預設會跑）、不勾＝關閉（預設不跑；`--all`／`--only` 照跑）。「新」＝設定檔沒列過：預設顯示成勾，
//   它會跑一次、通過就自動關閉 ⇒ 不動它就維持「新」。
// ⚠ 設定檔讀不懂 ⇒ 整頁只說這件事，⛔ 不退回預設、也不覆蓋它。
using Senate.Core;
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed class SelfTestConfigPage : SCP_GuiToolPage
{
    readonly SenateModel m_Model;

    SelfTestConfig? m_Cfg;
    string? m_Error;
    string? m_Message;
    List<(string Key, string Group, SelfTestStatus Status, bool Important)> m_Items = new();

    /// <summary>每次重新讀就換一個，讓勾選框的 id 跟著換 ⇒ 之前沒存的勾選覆寫自然作廢（同 AutoCommitPage）。</summary>
    int m_Stamp;

    public SelfTestConfigPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public const string PageKey = "selftest-config";
    public override string Title => "對拍設定";
    public override string? MenuGroup => "工具";

    public override void OnPush() { base.OnPush(); Reload(); }

    void Reload()
    {
        ++m_Stamp;
        m_Cfg = null;
        m_Error = null;
        try
        {
            m_Cfg = SelfTestConfig.Load(SelfTestConfig.PathFor(SenatePaths.ConfigDir(m_Model.RepoRoot)),
                SelfTest.CatalogKeys(), SelfTest.CoreKeys, out _);
            m_Items = SelfTest.ListWithStatus(m_Cfg);
        }
        catch (InvalidOperationException e) { m_Error = e.Message; m_Items = new(); }
    }

    string ToggleId(string iKey) => $"selftest-config/{m_Stamp}/{iKey}";

    protected override void TopBarButtons(SCP_Ui g)
    {
        if (g.Button("重新讀", "selftest-config/reload")) { m_Message = null; Reload(); }
    }

    protected override void DrawContent(SCP_Ui g)
    {
        if (m_Error != null) { g.Note("⚠ " + m_Error); return; }
        if (m_Cfg == null) { g.Note("（還沒讀）"); return; }

        int aStanding = m_Items.Count(x => x.Status == SelfTestStatus.Standing);
        int aNew = m_Items.Count(x => x.Status == SelfTestStatus.New);
        int aClosed = m_Items.Count(x => x.Status == SelfTestStatus.Closed);
        g.Label($"共 {m_Items.Count} 筆；預設會跑 {aStanding + aNew} 筆（常駐 {aStanding}＋新 {aNew}），關閉 {aClosed} 筆。");
        g.Note("勾＝常駐（預設會跑）、不勾＝關閉（預設不跑；`senate selftest --all` 或 `--only` 照跑）。「新」＝設定檔沒列過：跑一次、通過就自動關閉。");
        g.Note("設定檔：" + m_Cfg.Path + "　（CLI：`senate selftest --enable／--disable`，是同一份）");
        g.Note("新增測試的規矩（只有共用層＋會安靜壞＋容易被連帶弄壞才標 important）見 Docs/API/Cli_Reference.md › selftest。");

        // 先算出「想要的狀態」與「跟目前不同的」—— 存檔只寫不同的那幾項
        var aChanges = new List<(string Key, bool On)>();
        foreach (var it in m_Items)
        {
            bool aCur = it.Status != SelfTestStatus.Closed;
            bool aWant = g.ToggleValue(ToggleId(it.Key), aCur);
            if (aWant != aCur) aChanges.Add((it.Key, aWant));
        }

        using (g.Row())
        {
            if (aChanges.Count > 0)
            {
                if (g.Button($"存到設定檔（{aChanges.Count} 項有改）", "selftest-config/save")) Save(aChanges);
            }
            else g.Note("（沒有未存的改動）");
        }
        if (m_Message != null) g.Note(m_Message);

        foreach (var aGroup in m_Items.GroupBy(x => x.Group))
        {
            int aOnInGroup = aGroup.Count(x => x.Status != SelfTestStatus.Closed);
            using (g.Box($"{aGroup.Key}（預設跑 {aOnInGroup}／{aGroup.Count()}）", "selftest-config/box/" + aGroup.Key))
            {
                foreach (var it in aGroup)
                {
                    string aTag = it.Status switch
                    {
                        SelfTestStatus.New => "　〔新：跑一次後自動關閉〕",
                        _ => it.Important ? "　〔important〕" : "",
                    };
                    g.Toggle(it.Key + aTag, it.Status != SelfTestStatus.Closed, ToggleId(it.Key));
                }
            }
        }
    }

    void Save(List<(string Key, bool On)> iChanges)
    {
        if (m_Cfg == null) return;
        foreach (var (k, on) in iChanges)
        {
            if (on) { m_Cfg.Disabled.Remove(k); m_Cfg.Enabled.Add(k); }
            else { m_Cfg.Enabled.Remove(k); m_Cfg.Disabled.Add(k); }
        }
        try
        {
            m_Cfg.Save();
            int aOn = iChanges.Count(c => c.On), aOff = iChanges.Count - aOn;
            Reload();   // 存完一律重新讀 —— 畫面上的狀態要是「磁碟上的」，不是「以為寫進去的」
            m_Message = $"✓ 已寫 {m_Cfg?.Path}：常駐 +{aOn}／關閉 +{aOff}（已重新讀回）";
        }
        catch (IOException e) { m_Message = "✗ 寫入失敗：" + e.Message; }
    }
}
