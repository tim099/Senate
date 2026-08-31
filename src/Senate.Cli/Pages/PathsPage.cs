// 區塊職責：**路徑管理頁** —— 所有動態路徑在一頁上，可編輯的可編輯、算出來的印算式。
// 物理意義：頁面**完全由 `SCP_PathRegistry` 生成**（foreach 描述表）。
//           ⇒ 之後擴充一條路徑＝加一個 enum 成員 ＋ 一筆 descriptor，**本檔一行都不用改**。
//           那正是 Tim 2026-08-31 要的形狀（「用 enum 來管理，之後擴充只要加 enum」）。
// 數值影響：編輯的是自己讀進來的 draft，按「儲存」才寫回（走 `SenateConfig.Save`——
//           與設定頁／專案頁同一支，未知欄位與 `"//"` 註解照樣保留）。**本頁不另立檔案。**
//
// ⚠ 本頁只寫 `Stored` 那幾格。`Derived` 是**唯讀**的，而那不是「還沒做編輯功能」——
//   🩸 現場（2026-08-31）：`sessionDir` 曾經是可填的（`auto` ＝ 從**信件庫根**往上找 `_session`），
//     於是「lock 在哪」跟著一個手填值漂。改了專案 root ⇒ 信件庫根靜默指著舊樹 ⇒
//     lock 也在舊樹上 ⇒「誰在線」跟真實脫鉤，而每一頁看起來都正常。
//   ⇒ 判準：**能被推導的路徑不准被儲存。** 存了就是給漂移一個住的地方。
//
// ⚠ 與「專案關聯」頁的分工：那頁管 projects[] 的**增刪**與逐列探測；
//   本頁管**路徑本身**（含全域那格 `lettersRoot`，那格不屬於任何專案）。
//   兩頁寫同一份檔、同一支 Save，改完互相看得到（都在 OnPush 重讀）。
using SCP.Core.Gui;
using SCP.Core.Paths;
using Senate.Core;

namespace Senate.Cli.Pages;

public sealed class PathsPage : SCP_GuiToolPage
{
    readonly SenateModel m_Model;
    readonly string m_ConfigPath;

    SenateConfig? m_Draft;
    string? m_LoadError;
    bool m_Dirty;
    string? m_Message;

    /// <summary>看哪個專案的每專案路徑。-1 ＝ 沒有專案可看。</summary>
    int m_ProjectIndex;

    public PathsPage(SenateModel iModel) : base()
    {
        m_Model = iModel;
        m_ConfigPath = SenateConfig.DefaultPath(iModel.RepoRoot);
    }

    public override string Key => PageKey;
    public const string PageKey = "paths";
    public override string Title => "路徑管理";
    public override string? MenuGroup => "設定";

    public override void OnPush() { base.OnPush(); Load(); }

    void Load()
    {
        m_LoadError = null;
        m_Dirty = false;
        try
        {
            m_Draft = SenateConfig.Load(m_ConfigPath);
            if (m_Draft == null) m_LoadError = $"還沒有 {Path.GetFileName(m_ConfigPath)} —— 先跑 `senate init`";
        }
        catch (InvalidDataException e)
        {
            // 壞檔不拿空白頂上 —— 「檔壞了」長得像「還沒設定」時，儲存就是不可逆的覆寫。
            m_Draft = null;
            m_LoadError = $"設定檔讀不了，本頁不提供編輯（檔案沒有被動過）：{e.Message}";
        }
        if (m_Draft != null && m_ProjectIndex >= m_Draft.Projects.Count) m_ProjectIndex = 0;
    }

    /// <summary>目前選中的專案（沒有專案回 null —— 那時每專案那幾格解不出來，而畫面要說出來）。</summary>
    SenateProject? CurrentProject()
        => m_Draft != null && m_ProjectIndex >= 0 && m_ProjectIndex < m_Draft.Projects.Count
            ? m_Draft.Projects[m_ProjectIndex]
            : null;

    /// <summary>
    /// 描述表要的「這個 Id 存起來的原始值」。
    /// <para>⚠ 這是**唯一**一處把 `SCP_PathId` 對映到設定檔欄位的地方 ——
    /// 描述表本身刻意不知道值住在哪個檔（那是呼叫端的事）。</para>
    /// </summary>
    string StoredOf(SCP_PathId iId)
    {
        SenateProject? aProj = CurrentProject();
        return iId switch
        {
            SCP_PathId.ProjectRoot => aProj?.Root ?? "",
            SCP_PathId.AgentCommandsRoot => aProj?.AgentCommandsRoot ?? "",
            SCP_PathId.LettersRoot => m_Draft?.Awakening.LettersRoot ?? "",
            // Derived 的格子不會走到這裡；真的走到＝描述表與本對映表不同步，要大聲。
            _ => "",
        };
    }

    void SetStored(SCP_PathId iId, string iValue)
    {
        SenateProject? aProj = CurrentProject();
        switch (iId)
        {
            case SCP_PathId.ProjectRoot: if (aProj != null) aProj.Root = iValue; break;
            case SCP_PathId.AgentCommandsRoot: if (aProj != null) aProj.AgentCommandsRoot = iValue; break;
            case SCP_PathId.LettersRoot: if (m_Draft != null) m_Draft.Awakening.LettersRoot = iValue; break;
            default: return;
        }
        m_Dirty = true;
    }

    protected override void DrawContent(SCP_Ui g)
    {
        if (m_Draft == null)
        {
            g.Note($"⚠ {m_LoadError}");
            if (g.Button("重新讀取", "paths/reload")) Load();
            return;
        }

        g.Note($"本頁由 `SCP_PathRegistry` 描述表生成（共 {SCP_PathRegistry.All.Count} 條）——"
               + "**加一條路徑＝加一個 enum 成員 ＋ 一筆 descriptor，本頁不用改**。"
               + $"寫的是 `{Path.GetFileName(m_ConfigPath)}`（與「設定」／「專案關聯」頁同一份檔），按「儲存」才寫回。");

        // ── 每專案那幾格要先知道是哪個專案 ─────────────────────────
        if (m_Draft.Projects.Count == 0)
            g.Note("⚠ 還沒有任何專案 ⇒ 每專案的路徑**解不出來**（不是空的，是沒有起點）。先去「專案關聯」頁加一個。");
        else
        {
            using (g.Box("每專案路徑的對象"))
            {
                for (int i = 0; i < m_Draft.Projects.Count; i++)
                {
                    SenateProject p = m_Draft.Projects[i];
                    string aLabel = (p.Name.Length > 0 ? p.Name : "（未命名）")
                                    + (p.Enabled ? "" : "　（停用）");
                    if (g.Button((i == m_ProjectIndex ? "● " : "○ ") + aLabel, $"paths/proj/{i}"))
                        m_ProjectIndex = i;
                }
            }
        }

        // ── 描述表逐條 ─────────────────────────────────────────────
        foreach (SCP_PathDescriptor aD in SCP_PathRegistry.All)
        {
            string aId = "paths/" + aD.Id;
            SCP_PathResolution aRes = SCP_PathRegistry.Resolve(aD.Id, StoredOf);
            string aScope = aD.Scope == SCP_PathScope.Global ? "全域" : "每專案";
            string aKind = aD.Kind == SCP_PathKind.Stored ? "可設定" : "推導（唯讀）";

            using (g.Box($"{aD.Label}　[{aScope}／{aKind}]"))
            {
                if (aD.Kind == SCP_PathKind.Stored)
                {
                    string aRaw = StoredOf(aD.Id);
                    string aNew = g.TextField(
                        aD.SupportsAuto ? $"值（`{SCP_PathRegistry.AutoLiteral}` ＝ 交給上游推導）" : "值",
                        aRaw, aId + "/value");
                    if (aNew != aRaw) SetStored(aD.Id, aNew.Replace('\\', '/').Trim());
                    if (aD.SupportsAuto
                        && !string.Equals(aRaw.Trim(), SCP_PathRegistry.AutoLiteral, StringComparison.OrdinalIgnoreCase)
                        && g.Button($"改用 {SCP_PathRegistry.AutoLiteral}", aId + "/auto"))
                        SetStored(aD.Id, SCP_PathRegistry.AutoLiteral);
                    g.Note($"儲存鍵 `{aD.JsonKey}`　｜　算式：{SCP_PathRegistry.Formula(aD.Id)}");
                }
                else
                {
                    g.Note($"算式：{SCP_PathRegistry.Formula(aD.Id)}　—— **不儲存**，所以不會跟上游脫鉤。");
                }

                // 值與「誰決定的」一起印 —— 看不出來源的路徑沒辦法被質疑。
                if (aRes.Error != null) g.Note($"⚠ 解不出來（{aRes.Origin}）：{aRes.Error}");
                else g.Note($"⇒ `{aRes.Value}`　（{aRes.Origin}）{Existence(aRes.Value)}");
                g.Note(aD.Note);
            }
        }

        using (g.Box("寫回"))
        {
            g.Note(m_Dirty ? "有未儲存的改動。" : "沒有改動。");
            if (g.Button(m_Dirty ? "💾 儲存" : "儲存（沒有改動）", "paths/save") && m_Dirty)
            {
                try
                {
                    m_Draft.Save(m_ConfigPath);
                    m_Dirty = false;
                    m_Message = "已寫回 " + m_ConfigPath;
                    Load();     // 回讀 —— 印 ✓ 不算數
                }
                catch (Exception e) { m_Message = "✗ 寫回失敗：" + e.Message; }
            }
            if (g.Button("↩ 放棄改動並重新讀取", "paths/discard")) Load();
            if (m_Message != null) g.Note(m_Message);
        }
    }

    /// <summary>
    /// 存在性讀數。⚠ **「不存在」不等於錯** —— 有些路徑是第一次用才會被建出來。
    /// 但它跟「存在」必須看得出差別，否則路徑打錯的症狀是「一切正常，只是永遠 pending」。
    /// </summary>
    static string Existence(string iPath)
    {
        if (iPath.Length == 0) return "";
        if (Directory.Exists(iPath)) return "　✓ 存在";
        if (File.Exists(iPath)) return "　⚠ 是檔案不是目錄";
        return "　⚠ 不存在（可能還沒被建出來）";
    }
}
