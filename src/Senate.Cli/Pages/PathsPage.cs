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
// ⚠ 與「設定」頁寫同一份檔、同一支 Save，改完互相看得到（都在 OnPush 重讀）。
// ⚠ 版面（Tim 2026-10-22）：可設定的路徑 → 同步遠端（sync.tasks）→ 唯讀路徑（推導／宿主，預設折起來）→ 寫回。
//   同步遠端那一格**不是路徑**（repo 網址＋branch），所以不進描述表；本機目錄照樣由資料根推導、不儲存。
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

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        base.TopBarButtons(iUi);

        if (m_Draft != null)
        {
            if (iUi.Button(m_Dirty ? "💾 儲存" : "儲存（沒有改動）", "paths/save") && m_Dirty)
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
        }

        if (iUi.Button("↩ 放棄改動並重新讀取", "paths/discard")) Load();
        iUi.Note(m_Dirty ? "有未儲存的改動。" : "沒有改動。");
        if (m_Message != null) iUi.Note(m_Message);
    }
    void Load()
    {
        m_LoadError = null;
        m_Dirty = false;
        try
        {
            m_Draft = SenateConfig.Load(m_ConfigPath);
            if (m_Draft == null) m_LoadError = $"還沒有 {Path.GetFileName(m_ConfigPath)} —— 先跑 `senate init`";
            LoadSyncStatus();
        }
        catch (InvalidDataException e)
        {
            // 壞檔不拿空白頂上 —— 「檔壞了」長得像「還沒設定」時，儲存就是不可逆的覆寫。
            m_Draft = null;
            m_LoadError = $"設定檔讀不了，本頁不提供編輯（檔案沒有被動過）：{e.Message}";
        }
    }

    /// <summary>
    /// 這個 Id 存起來的原始值。**對映表在 `SenatePathBinding`（唯一一處）** ——
    /// 頁面與 `senate cmd paths` 走同一支，兩邊不可能對同一格給出不同的值。
    /// </summary>
    SCP_PathStoredValue StoredOf(SCP_PathId iId)
        => m_Draft == null
            ? SCP_PathStoredValue.Unavailable("設定檔沒讀進來")
            : SenatePathBinding.StoredOf(m_Draft, iId);

    void SetStored(SCP_PathId iId, string iValue)
    {
        if (m_Draft == null) return;
        if (SenatePathBinding.SetStored(m_Draft, iId, iValue, out string? aErr)) m_Dirty = true;
        else m_Message = "✗ " + aErr;
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
               + $"寫的是 `{Path.GetFileName(m_ConfigPath)}`（與「設定」頁同一份檔），按「儲存」才寫回。");

        // ── 描述表逐條：可設定的在上面；推導／宿主（唯讀）收在最下面、預設折起來（Tim 2026-10-22：「把無法設定的欄位放到下方 且預設折疊」）──
        foreach (SCP_PathDescriptor aD in SCP_PathRegistry.All)
            if (aD.Kind == SCP_PathKind.Stored) DrawDescriptor(g, aD);

        DrawSync(g);

        var aReadOnly = SCP_PathRegistry.All.Where(d => d.Kind != SCP_PathKind.Stored).ToList();
        using (var aFold = g.Fold($"唯讀路徑（推導／宿主，共 {aReadOnly.Count} 條）—— 不儲存、看就好", "paths/readonly", iDefaultOpen: false))
        {
            if (aFold.Open)
                foreach (SCP_PathDescriptor aD in aReadOnly) DrawDescriptor(g, aD);
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

    void DrawDescriptor(SCP_Ui g, SCP_PathDescriptor iD)
    {
        string aId = "paths/" + iD.Id;
        SCP_PathResolution aRes = SCP_PathRegistry.Resolve(iD.Id, StoredOf);
        string aScope = iD.Scope == SCP_PathScope.Global ? "全域" : "專案";
        string aKind = iD.Kind == SCP_PathKind.Stored ? "可設定" : iD.Kind == SCP_PathKind.Host ? "宿主（唯讀）" : "推導（唯讀）";

        using (g.Box($"{iD.Label}　[{aScope}／{aKind}]"))
        {
            if (iD.Kind == SCP_PathKind.Stored)
            {
                string aRaw = StoredOf(iD.Id).Raw;
                string aNew = g.TextField(
                    iD.SupportsAuto ? $"值（`{SCP_PathRegistry.AutoLiteral}` ＝ 交給上游推導）" : "值",
                    aRaw, aId + "/value");
                if (aNew != aRaw) SetStored(iD.Id, aNew.Replace('\\', '/').Trim());
                if (iD.SupportsAuto
                    && !string.Equals(aRaw.Trim(), SCP_PathRegistry.AutoLiteral, StringComparison.OrdinalIgnoreCase)
                    && g.Button($"改用 {SCP_PathRegistry.AutoLiteral}", aId + "/auto"))
                    SetStored(iD.Id, SCP_PathRegistry.AutoLiteral);
                g.Note($"儲存鍵 `{iD.JsonKey}`　｜　算式：{SCP_PathRegistry.Formula(iD.Id)}");
            }
            else
            {
                g.Note($"算式：{SCP_PathRegistry.Formula(iD.Id)}　—— **不儲存**，所以不會跟上游脫鉤。");
            }

            // 值與「誰決定的」一起印 —— 看不出來源的路徑沒辦法被質疑。
            // 空白在這一格是合法的預設（TASK-0375）⇒ 說它代表什麼，⛔ 不印成「⚠ 解不出來」（那句在這裡是假的）。
            if (iD.IsBlankDefault(aRes)) g.Note($"⇒ 空白＝{iD.BlankMeans}");
            else if (aRes.Error != null) g.Note($"⚠ 解不出來（{aRes.Origin}）：{aRes.Error}");
            else g.Note($"⇒ `{aRes.Value}`　（{aRes.Origin}）{Existence(aRes.Value)}");
            g.Note(iD.Note);
        }
    }

    /// <summary>
    /// 同步遠端（`sync.tasks`）—— repo 網址不是路徑，所以不進描述表，在這裡另開一格（同一份檔、同一顆「儲存」）。
    /// <para>狀態讀數在 <see cref="Load"/> 算一次（只讀本機 git，不走網路）：⛔ 不在每一幀跑 git。</para>
    /// </summary>
    void DrawSync(SCP_Ui g)
    {
        if (m_Draft == null) return;
        SyncTargetSettings t = m_Draft.Sync.Tasks;
        using (g.Box("同步遠端：任務單（sync.tasks）"))
        {
            string aRepo = g.TextField("repo（clone 用的網址或路徑）", t.Repo, "paths/sync/tasks/repo");
            if (aRepo != t.Repo) { t.Repo = aRepo.Trim(); m_Dirty = true; }
            string aBranch = g.TextField("branch", t.Branch, "paths/sync/tasks/branch");
            if (aBranch != t.Branch) { t.Branch = aBranch.Trim(); m_Dirty = true; }
            g.Note(t.IsSet
                ? "兩格都填了 ⇒ 每次開單／改單自動 commit＋push；`<資料根>/Tasks` 不存在時第一次用到會自動 clone；"
                  + "兩邊同時開單撞號會自動改號，解不了的衝突擋下並發酒館通知。"
                : (t.Repo.Length > 0 || t.Branch.Length > 0) ? "⚠ 只填了一格 ⇒ **不同步**（兩格都填才算）" : "空白 ＝ 不同步（只寫本機）");
            if (m_SyncStatus != null) g.Note("⇒ " + m_SyncStatus);
            g.Note("手動：`senate cmd task-sync --arg op=status --arg fetch=1`（看差多少）／`--arg op=sync`（拉推，撞了會重放）");
        }
    }

    string? m_SyncStatus;

    void LoadSyncStatus()
    {
        m_SyncStatus = null;
        if (m_Draft == null) return;
        string? aRoot = SenatePathBinding.ResolveDataRoot(m_Draft, out string? aErr);
        if (aRoot == null) { m_SyncStatus = "資料根解不出來（" + aErr + "）"; return; }
        try
        {
            TaskRepoStatus s = SenateTaskRepoSync.Status(aRoot, TaskRepoTarget.From(m_Draft), iFetch: false);
            m_SyncStatus = (s.IsRepo ? $"本機 `{SenateTaskRepoSync.TasksDir(aRoot)}`：origin `{s.Origin}`／`{s.Branch}`，領先 {s.Ahead}／落後 {s.Behind}（上次抓遠端時）。" : "")
                         + s.Note;
        }
        catch (Exception e) { m_SyncStatus = "狀態讀不到（" + e.Message + "）"; }
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
