// 區塊職責：任務子作品、作品組裝、移動與Undo/Redo的CLI；所有幾何走共用引擎。
// 鎖順序：跨作品依ID排序拿鎖，避免房間與床互相匯入時死鎖；不碰展區或付款閘。
using System.Globalization;
using SCP.Core.Io;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    static string? CreateChildWork(Ctx c, string id, int[] dimensions, string commission)
    {
        if (commission.Length > 0) return Blocked(c, 2, "子作品不另領委託薪水；不要帶commission");
        if (c.Works.Exists(id)) return Blocked(c, 2, "作品ID已存在：" + id);
        string parent = SCP_SculptWorks.NormalizeId(c.Args.Get("parent_work"));
        var root = c.Works.Load(parent);
        var visited = new HashSet<string>(StringComparer.Ordinal) { id };
        while (true)
        {
            if (!visited.Add(root.id)) return Blocked(c, 2, "子作品父層循環");
            if (root.owner != c.Persona) return Blocked(c, 2, "只有任務作品作者可以建立其子作品；合作請用assemble匯入他人作品");
            if (root.parent_work.Length == 0) break;
            root = c.Works.Load(root.parent_work);
        }
        if (root.commission.Length == 0) return Blocked(c, 2, "免費子作品必須屬於使用者委託任務");
        string title = c.Args.Get("title").Trim();
        if (title.Length == 0) return Blocked(c, 2, "建立子作品需要title");
        var card = new SCP_SculptWork { schema = 1, id = id, owner = c.Persona, title = title, parent_work = parent,
            status = "ready", payment_ref = "sculpture-child:" + Guid.NewGuid().ToString("N"),
            created_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
        SCP_SculptWorks.SetSize(card, dimensions); c.Works.Save(card); c.Works.SpacePaths(id).EnsureBase();
        c.Result.AddValue("work", id); c.Result.AddValue("parent_work", parent); c.Result.AddValue("root_work", root.id);
        c.Result.AddValue("space_size", card.Dimensions); c.Result.AddValue("charged", "0"); c.Result.AddValue("reward", "0");
        WorkNextSteps(c, id);
        return Finish(c, 0, "✓ 任務子作品 " + id + "｜免費建立，不重複領薪");
    }
    static int[] WorkVector(string text, string name)
    {
        string[] parts = text.Split(','); var result = new int[3];
        if (parts.Length != 3) throw new ArgumentException(name + "需要x,y,z三個整數");
        for (int i = 0; i < 3; i++) if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]))
            throw new ArgumentException(name + "需要x,y,z三個整數");
        return result;
    }
    static bool WorkOverwrite(Ctx c)
    {
        string value = c.Args.Get("overwrite").Trim();
        if (value != "" && value != "0" && value != "1") throw new ArgumentException("overwrite需為0或1");
        return value == "1";
    }
    static string? AssembleWork(Ctx c, string id)
    {
        string sourceId = SCP_SculptWorks.NormalizeId(c.Args.Get("source_work"));
        c.Works.Load(id); c.Works.Load(sourceId); // 不為不存在的作品建立鎖目錄。
        string first = string.CompareOrdinal(id, sourceId) <= 0 ? id : sourceId;
        string second = first == id ? sourceId : id;
        using var firstLock = SCP_FileLock.Acquire(c.Works.EngineLock(first), EngineLockTimeoutSec);
        using var secondLock = first == second ? null : SCP_FileLock.Acquire(c.Works.EngineLock(second), EngineLockTimeoutSec);
        var card = c.Works.Load(id); var source = c.Works.Load(sourceId);
        if (c.Persona.Length == 0 || c.Persona != card.owner) return Blocked(c, 2, "只有目標作品作者可以放入零件；來源作者不限");
        int[] at = WorkVector(c.Args.Get("at"), "at");
        if (!TryInt(c, "turn", out int turn)) return Blocked(c, 2, "turn需為0/90/180/270");
        var engine = c.Works.Engine(card, c.Roots.DataRoot);
        var sourceSpace = c.Works.Engine(source, c.Roots.DataRoot).LoadSpace();
        var edit = engine.Assemble(sourceSpace, source.SizeX, source.SizeY, at, turn, WorkOverwrite(c));
        edit.credits.Add(new SCP_SculptCredit { work = source.id, author = source.owner, title = source.title,
            revision = SCP_SculptWorks.Revision(sourceSpace, source.Dimensions) });
        edit.credits.AddRange(SCP_SculptHistory.Read(c.Works.SpacePaths(source.id)).Credits());
        return FinishEdit(c, card, engine, edit);
    }
    static string? EditWork(Ctx c, SCP_SculptWork card, string sub)
    {
        var engine = c.Works.Engine(card, c.Roots.DataRoot);
        SCP_SculptEdit edit;
        if (sub == "move")
        {
            if (!SCP_SculptEngine.TryParseRegion(c.Args.Get("region"), out int[] region)) return Blocked(c, 2, "move需要region=x1..x2,y1..y2,z1..z2");
            edit = engine.Move(region, WorkVector(c.Args.Get("delta"), "delta"), WorkOverwrite(c));
        }
        else edit = engine.UndoRedo(sub == "redo");
        return FinishEdit(c, card, engine, edit);
    }
    static string? FinishEdit(Ctx c, SCP_SculptWork card, SCP_SculptEngine engine, SCP_SculptEdit edit)
    {
        string file = engine.CommitEdit(edit, c.Persona);
        c.Result.AddValue("work", card.id); c.Result.AddValue("changed", edit.after.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("charged", "0"); c.Result.AddValue("event_file", file);
        c.Result.AddValue("revision", SCP_SculptWorks.Revision(engine.LoadSpace(), card.Dimensions));
        AppendCredits(c, card.id);
        return Finish(c, 0, "✓ " + edit.action + "｜" + edit.after.Count + "格變動｜免費；可用work sub=undo/redo");
    }
    static void AppendCredits(Ctx c, string id)
    {
        c.Report.Append("\n\n## Credit（自動依目前編輯歷史產生）\n");
        var credits = SCP_SculptHistory.Read(c.Works.SpacePaths(id)).Credits();
        foreach (var credit in credits) c.Report.Append("- ").Append(credit.title).Append("（").Append(credit.work)
            .Append("）｜by ").Append(credit.author).Append("｜版本 ").Append(credit.revision).Append('\n');
        if (credits.Count == 0) c.Report.Append("（尚未匯入零件）\n");
        c.Result.AddValue("credit_count", credits.Count.ToString(CultureInfo.InvariantCulture));
    }
    static string? WorkHistory(Ctx c, string id)
    {
        var history = SCP_SculptHistory.Read(c.Works.SpacePaths(id));
        c.Result.AddValue("undo_count", history.Active.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("redo_count", history.Redo.Count.ToString(CultureInfo.InvariantCulture));
        c.Report.Append("## 可Undo的編輯（最近20筆）\n");
        for (int i = Math.Max(0, history.Active.Count - 20); i < history.Active.Count; i++)
            c.Report.Append("- ").Append(history.Active[i].Rel).Append('\n');
        AppendCredits(c, id);
        return Finish(c, 0, "✓ 可Undo " + history.Active.Count + " 筆；可Redo " + history.Redo.Count + " 筆");
    }
}
