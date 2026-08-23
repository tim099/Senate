// 區塊職責：設定頁 —— **完全不寫欄位碼**，把整份 senate.local.json 丟給自動繪製。
// 物理意義：⭐ 這一頁是反射三層的第一個真消費者，也是它們的驗收：
//           畫面上的欄位是 SCP_TypeSchema 掃出來的，而 SCP_JsonMapper 吃的是同一份
//           ⇒ 「畫得出來」與「存得進去」不會分岔（分岔的症狀是某個欄位改了之後回不來，不報錯）。
//           以後 SenateConfig 加一個欄位，這一頁**一行都不用改**就會出現在畫面上。
// 數值影響：編輯的是**自己讀進來的一份**（不是 model 手上那份）；按「儲存」才寫回檔案，
//           而且走 SenateConfig.Save ⇒ 未知欄位與 "//" 註解照樣保留（D12）。
// ⚠ 這一頁刻意**不自動存**：自動存會讓「打字打到一半」變成落地的設定值，
//   而字級這種東西改壞了會讓人看不見畫面上的還原按鈕。
// ⚠ 多層巢狀在這裡是真的（config → projects[] → 每個專案的欄位），
//   所以每一層都用 Fold 收得起來（`--fold settings/Projects` 之類）。
using SCP.Core.Gui;
using Senate.Core;

namespace Senate.Cli.Pages;

public sealed class SettingsPage : SCP_GuiToolPage
{
    readonly SenateModel m_Model;
    readonly string m_RepoRoot;
    readonly string m_ConfigPath;

    /// <summary>編輯中的那一份（按儲存才寫回檔案）。null ＝ 讀不到／壞掉，畫面要說出來。</summary>
    SenateConfig? m_Draft;
    string? m_LoadError;

    bool m_Dirty;
    string? m_Message;

    /// <summary>`: base()` 讓 [CallerFilePath] 填 SourceFilePath（隱式 base() 會是 null）。</summary>
    public SettingsPage(SenateModel iModel) : base()
    {
        m_Model = iModel;
        m_RepoRoot = iModel.RepoRoot;
        m_ConfigPath = SenateConfig.DefaultPath(m_RepoRoot);
    }

    /// <summary>
    /// 讀檔在 <c>OnPush</c> 不在建構子。
    /// <para>⚠ 這不是風格問題：頁面目錄（<see cref="SCP.Core.Gui.SCP_GuiPageCatalog"/>）為了讀
    /// 標題與分組會**建一次實例然後丟掉** —— 建構子碰磁碟的話，光是「列出有哪些頁」
    /// 就會去讀一次設定檔，而那筆 IO 不屬於任何人按下的動作。</para>
    /// </summary>
    public override void OnPush() { base.OnPush(); Load(); }

    public override string Key => PageKey;
    public const string PageKey = "settings";

    public override string Title => "設定（自動繪製）";

    /// <summary>列進入口頁的「設定」組。</summary>
    public override string? MenuGroup => "設定";

    void Load()
    {
        m_LoadError = null;
        m_Dirty = false;
        try
        {
            m_Draft = SenateConfig.Load(m_ConfigPath);
            if (m_Draft == null) m_LoadError = $"還沒有 {System.IO.Path.GetFileName(m_ConfigPath)} —— 先跑 `senate init`";
        }
        catch (System.IO.InvalidDataException e)
        {
            // 壞掉的設定檔**不要用空白的頂上去** —— 那會讓「檔壞了」長得像「還沒設定」，
            // 而按下儲存就把壞掉的內容換成一份空的（不可逆）
            m_Draft = null;
            m_LoadError = $"設定檔讀不了，本頁不提供編輯（檔案沒有被動過）：{e.Message}";
        }
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Note("這一頁的欄位沒有一行是手寫的 —— 全部由 SCP_GuiInspector 從型別反射出來。");

        if (m_Draft == null)
        {
            g.Note($"⚠ {m_LoadError}");
            if (g.Button("重新讀取", "settings/reload")) Load();
            return;
        }

        // id 前綴是契約：agent 可以直接 `--set settings/Ui/Scale=1.5`、`--fold settings/Projects`
        SCP_InspectorResult aResult = SCP_GuiInspector.Draw(g, m_Draft, "settings");
        if (aResult.Changed) m_Dirty = true;

        g.Space();
        using (g.Row())
        {
            if (g.Button(m_Dirty ? "儲存（有未存的改動）" : "儲存", "settings/save")) Save();
            if (g.Button("放棄改動", "settings/revert"))
            {
                Load();
                m_Message = "・已重新讀取檔案（未儲存的改動丟掉了，檔案沒有被動過）";
            }
        }

        if (m_Dirty) g.Note("⚠ 有改動還沒儲存 —— 離開這一頁就會丟掉（本頁刻意不自動存）");
        if (m_Message != null) g.Note(m_Message);
        foreach (string n in aResult.Notes) g.Note($"（自動繪製）{n}");
    }

    void Save()
    {
        if (m_Draft == null) return;

        // 設定本身先驗一遍 —— 存一份自己知道有問題的設定，等於把問題延後到下次啟動才爆
        var aErrors = m_Draft.Validate();
        if (aErrors.Count > 0)
        {
            m_Message = "⚠ 沒有儲存 —— 設定有問題：" + string.Join("；", aErrors);
            return;
        }

        try
        {
            m_Draft.Save(m_ConfigPath);
        }
        catch (System.Exception e)
        {
            m_Message = $"⚠ 寫檔失敗：{e.GetType().Name}: {e.Message}";
            return;
        }

        // 回讀確認（寫入端會替自己說謊）—— 順手把活著的 style 對上新值，這一輪就生效
        try
        {
            SenateConfig? aBack = SenateConfig.Load(m_ConfigPath);
            float aGot = aBack?.Ui.Scale ?? float.NaN;
            m_Model.Style.SetScale(m_Draft.Ui.Scale);
            m_Model.Style.TextWidth = m_Draft.Ui.TextWidth;

            m_Message = System.Math.Abs(aGot - m_Draft.Ui.Scale) < 0.001f
                ? $"✓ 已存進 {System.IO.Path.GetFileName(m_ConfigPath)}（回讀確認 ui.scale={aGot:0.##}）"
                : $"⚠ 寫進去了但回讀是 {aGot:0.##}（期望 {m_Draft.Ui.Scale:0.##}）—— 有第二個寫入者？";
            m_Dirty = false;
        }
        catch (System.IO.InvalidDataException e)
        {
            m_Message = $"⚠ 寫完之後回讀失敗（檔案可能壞了）：{e.Message}";
        }
    }
}
