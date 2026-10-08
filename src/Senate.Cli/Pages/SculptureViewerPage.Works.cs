// 區塊職責：雕刻觀測頁的個人作品區；建立／筆記／匯入都派送同一支 sculpture CLI。
// 物理意義：模式與 ID 明確決定觀測空間；作品選取失效不會退回共用展區。
using System.Text;
using SCP.Core.Gui;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli.Pages;

public sealed partial class SculptureViewerPage
{
    public const string SpaceSel = P + "sel/space", WorkSel = P + "sel/work";
    const string WorkCreateId = P + "work/create-id", WorkCreateTitle = P + "work/create-title";
    const string WorkAt = P + "work/at", WorkExhibit = P + "work/exhibit";
    const string WorkSeen = P + "state/work-seen", ImportSig = P + "state/import-sig", ImportRevision = P + "state/import-revision", ImportCount = P + "state/import-count";
    const string SubjectWork = "work:";
    List<SCP_SculptWork> m_Works = new();
    readonly Dictionary<string, (string Notes, string Todo)> m_WorkTexts = new();
    readonly Dictionary<string, List<SCP_SculptCredit>> m_WorkCredits = new();
    string m_WorksError = "";

    void ReloadWorks(SCP_DataRoot data)
    {
        m_Works = new(); m_WorkTexts.Clear(); m_WorkCredits.Clear(); m_WorksError = "";
        try
        {
            var store = new SCP_SculptWorks(data);
            m_Works = store.List();
            foreach (var card in m_Works)
            {
                m_WorkTexts[card.id] = (store.ReadText(card.id, false), store.ReadText(card.id, true));
                m_WorkCredits[card.id] = SCP_SculptHistory.Read(store.SpacePaths(card.id)).Credits();
            }
        }
        catch (Exception e) { m_WorksError = "作品讀取失敗：" + e.Message; }
    }
    static bool PersonalSpace(SCP_Ui g) => g.FieldValue(SpaceSel + "/value", "shared") == "work";
    string SelectedWork(SCP_Ui g)
    {
        string id = g.FieldValue(WorkSel + "/value", "");
        return m_Works.Exists(w => w.id == id && w.status == "ready") ? id : "";
    }
    void AddWorkTarget(SCP_Ui g, Dictionary<string, string> args)
    {
        if (!PersonalSpace(g)) return;
        string id = SelectedWork(g);
        args["work"] = id.Length > 0 ? id : "__missing-work__"; // 不准失效選取靜默變成展區操作。
        args.Remove("exhibit");
    }
    void DrawWorkSelector(SCP_Ui g, string persona)
    {
        g.Dropdown("雕刻空間", new List<SCP_GuiOption> { new("shared", "共用展區（256³）"), new("work", "個人作品（可調尺寸）") }, "shared", SpaceSel);
        if (PersonalSpace(g))
        {
            var options = m_Works.Select(w => new SCP_GuiOption(w.id, w.title + "｜" + w.owner + "｜" + w.status)).ToList();
            string first = m_Works.Find(w => w.owner == persona && w.status == "ready")?.id ?? m_Works.Find(w => w.status == "ready")?.id ?? "";
            if (g.FieldValue(WorkSel + "/value", "").Length == 0 && first.Length > 0)
                g.SetField(WorkSel + "/value", first);
            g.Dropdown("作品", options, first, WorkSel);
        }
        string selection = PersonalSpace(g) ? SubjectWork + SelectedWork(g) : SubjectFull;
        string seen = g.FieldValue(WorkSeen, "");
        if (seen != selection)
        {
            g.SetField(WorkSeen, selection);
            if (seen.Length > 0 || PersonalSpace(g))
            {
                g.SetField(SSubject, selection); g.SetField(FRegion, ""); g.SetField(FExclude, "");
                g.SetField(SViewPath, ""); g.SetField(SSlicePath, ""); g.SetField(SExportPath, "");
                RequestRender("切換雕刻空間", iForce: true);
            }
        }
    }
    void DrawWorks(SCP_Ui g, string persona)
    {
        if (m_WorksError.Length > 0) g.Note(m_WorksError);
        g.Note("作品預設64³，各軸可設1–256；建立收10單位，作品內雕刻與調尺寸免費。匯入展區另按實際落地收費，原稿保留。");
        using (var create = g.Fold("建立作品", P + "fold/work-create", iDefaultOpen: false))
        {
            if (create.Open)
            {
                string id = g.TextField("全庫唯一 ID（英數 _ -）", "", WorkCreateId);
                string title = g.TextField("作品名稱", "", WorkCreateTitle);
                string dimensions = g.TextField("尺寸（邊長或X,Y,Z，各軸1–256）", "64", P + "work/create-size");
                string parent = g.TextField("任務父作品ID（子作品才填）", "", P + "work/create-parent").Trim();
                string pending = g.FieldValue(Pending, "");
                Armed(g, pending, "work-create", parent.Length > 0 ? "建立任務子作品（免費）" : "建立作品（10單位）", () =>
                {
                    var args = new Dictionary<string, string> { ["op"] = "work", ["sub"] = "create", ["persona"] = persona, ["id"] = id, ["title"] = title, ["size"] = dimensions };
                    if (parent.Length > 0) args["parent_work"] = parent;
                    Start(g, "建立作品", () =>
                    {
                        var result = RunCli(args);
                        var outcome = new Outcome { Log = result.Log("建立作品"), Reload = true };
                        if (result.Exit == 0) outcome.Fields[WorkSel + "/value"] = result.Value("work", "");
                        return outcome;
                    });
                });
            }
        }
        string selected = SelectedWork(g);
        if (selected.Length == 0) { g.Note("請建立或選擇付款完成的作品；待付款作品可用同一 ID 再按建立對帳。"); return; }
        var card = m_Works.Find(w => w.id == selected)!;
        g.Label(card.title + "｜" + selected + "｜作者 " + card.owner + "｜" + card.Dimensions);
        if (card.commission.Length > 0) g.Note("委託：" + card.commission + "｜來源 " + card.commission_ref + "｜建立免費、報酬10 token（" + card.status + "）");
        if (card.parent_work.Length > 0) g.Note("任務子作品｜父作品 " + card.parent_work + "｜建立免費，不另領薪");
        if (m_WorkCredits.TryGetValue(selected, out var credits) && credits.Count > 0)
        {
            g.Label("Credit（自動）");
            foreach (var credit in credits) g.Note(credit.title + "（" + credit.work + "）｜by " + credit.author + "｜版本 " + credit.revision);
        }
        if (g.Button("渲染作品", P + "btn/work-render")) RunView(g, "作品 " + selected, new() { ["work"] = selected }, persona, SubjectWork + selected);
        var text = m_WorkTexts.TryGetValue(selected, out var saved) ? saved : (Notes: "", Todo: "");
        string titleKey = P + "work/" + selected + "/title", notesKey = P + "work/" + selected + "/notes", todoKey = P + "work/" + selected + "/todo";
        using (var notesFold = g.Fold("心得與續作 TODO", P + "fold/work-notes", iDefaultOpen: true))
        {
            if (notesFold.Open)
            {
                string title = g.TextField("名稱", card.title, titleKey);
                string dimensions = g.TextField("空間尺寸（邊長或X,Y,Z）", card.Dimensions, P + "work/" + selected + "/size");
                g.Note("調整空間不移動或縮放作品；縮小若會切掉內容，整次保存會拒絕。");
                string notes = g.TextArea("心得／進度／下次從哪開始", text.Notes, notesKey, 6);
                string todo = g.TextArea("TODO", text.Todo, todoKey, 4);
                if (persona == card.owner)
                {
                    if (g.Button("保存尺寸與筆記（免費）", P + "btn/work-save"))
                        Start(g, "保存尺寸與筆記", () => new Outcome { Log = RunCli(new() { ["op"] = "work", ["sub"] = "update", ["work"] = selected, ["persona"] = persona, ["title"] = title, ["size"] = dimensions, ["notes"] = notes, ["todo"] = todo }).Log("保存尺寸與筆記"), Reload = true });
                }
                else g.Note("目前 persona 不是作者；可觀測，不能修改。");
            }
        }
        if (persona == card.owner) DrawWorkEditing(g, persona, selected);
        using var importFold = g.Fold("匯入共用展區", P + "fold/work-import", iDefaultOpen: false);
        if (!importFold.Open) return;
        string at = g.TextField("展區座標（作品原點0,0,0平移到這裡）", "0,0,0", WorkAt);
        string exhibit = g.TextField("新的展品 ID", selected + "-exhibit", WorkExhibit);
        string signature = selected + "|" + persona + "|" + at + "|" + exhibit;
        var importArgs = new Dictionary<string, string> { ["op"] = "work", ["sub"] = "import", ["work"] = selected, ["persona"] = persona, ["at"] = at, ["exhibit_id"] = exhibit };
        if (g.Button("預覽匯入數量與費用", P + "btn/work-import-preview"))
        {
            Start(g, "匯入預覽", () =>
            {
                var result = RunCli(importArgs);
                var outcome = new Outcome { Log = result.Log("匯入預覽") };
                outcome.Fields[ImportSig] = result.Exit == 0 ? signature : "";
                outcome.Fields[ImportRevision] = result.Value("revision", "");
                outcome.Fields[ImportCount] = result.Value("would_place", "");
                return outcome;
            });
        }
        if (g.FieldValue(ImportSig, "") != signature) { g.Note("先預覽，再確認匯入。來源版本或展區落地數改變會拒絕提交。"); return; }
        g.Note("預覽版本 " + g.FieldValue(ImportRevision, "") + "；落地 " + g.FieldValue(ImportCount, "") + " voxels。既有格子會跳過；越界拒絕。");
        Armed(g, g.FieldValue(Pending, ""), "work-import", "匯入並支付落地費用", () =>
        {
            importArgs["confirm"] = "1";
            importArgs["expect_revision"] = g.FieldValue(ImportRevision, "");
            importArgs["expect_placed"] = g.FieldValue(ImportCount, "");
            Start(g, "匯入作品", () =>
            {
                var outcome = new Outcome { Log = RunCli(importArgs).Log("匯入作品"), Reload = true };
                outcome.Fields[ImportSig] = "";
                return outcome;
            });
        });
    }

    /// <summary>長筆記走 UTF-8 arg-file；每次呼叫獨占隨機檔，子行程結束才清掉。</summary>
    sealed class WorkArgumentFiles : IDisposable
    {
        readonly List<string> m_Files = new();
        public string Add(string text)
        {
            string path = Path.Combine(Path.GetTempPath(), "senate-sculpt-work-" + Guid.NewGuid().ToString("N") + ".md");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
            m_Files.Add(path);
            return path;
        }
        public void Dispose() { foreach (string path in m_Files) File.Delete(path); }
    }
}
