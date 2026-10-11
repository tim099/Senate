// 區塊職責：作品的時間標記 CLI（TASK-0491）—— work sub=mark／marks，以及觀測類 op 的 mark／hide／only。
// 物理意義：標記只記「重播到哪一個事件」；觀測時引擎從頭重播到那裡（不碰快取）—— 作品、事件、快取一格都不動。
// 失敗處置：寫入類 op 帶了 mark／hide／only ⇒ exit 2（落子永遠落在現在的作品上）；名字／事件解不出來 ⇒ exit 2、零寫入。
using System.Globalization;
using SCP.Core.Io;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    static readonly string[] ViewTimeOps = { "view", "slice", "section", "stats", "export" };

    /// <summary>觀測的時間條件：沒給 mark／hide／only ⇒ 不動（true）；給了 ⇒ 解成 <see cref="Ctx.View"/>，解不出來 ⇒ false＋原因。</summary>
    static bool ConfigureView(Ctx c, out string oError)
    {
        oError = "";
        string aMark = c.Args.Get("mark").Trim(), aHide = c.Args.Get("hide").Trim(), aOnly = c.Args.Get("only").Trim();
        if (aMark.Length == 0 && aHide.Length == 0 && aOnly.Length == 0) return true;
        if (Array.IndexOf(ViewTimeOps, c.Op) < 0)
        {
            oError = "mark／hide／only 只給觀測類 op（view／slice／section／stats／export）—— 落子、刻、編輯一律落在現在的作品上";
            return false;
        }
        if (c.Work == null) { oError = "時間標記是作品的 —— 要帶 work="; return false; }
        try
        {
            var aFile = SCP_SculptMarks.Read(c.Works.SpacePaths(c.Work.id));
            var aFilter = new SCP_SculptViewFilter();
            if (aMark.Length > 0)
            {
                string aRel = SCP_SculptMarks.ResolvePoint(aFile, aMark);
                if (aRel.Length == 0) aFilter.UptoStart = true;
                else aFilter.UptoEvent = aRel;
            }
            foreach (string h in aHide.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var s = SCP_SculptMarks.ResolveInterval(aFile, h);
                aFilter.Hide.Add((s.Begin, s.End, h));
            }
            if (aOnly.Length > 0)
            {
                if (aOnly.Contains(',')) throw new ArgumentException("only 一次只能給一個區間");
                var s = SCP_SculptMarks.ResolveInterval(aFile, aOnly);
                aFilter.Only = (s.Begin, s.End, aOnly);
            }
            c.View = aFilter;
            string aWhen = aMark.Length == 0 ? "現在" : aMark + "（" + (aFilter.UptoStart ? "第一個事件之前" : aFilter.UptoEvent) + "）";
            c.Report.Append("- ⏪ 觀測的時間：").Append(aWhen)
                    .Append(aHide.Length > 0 ? "｜撤掉區間：" + aHide : "").Append(aOnly.Length > 0 ? "｜只看區間：" + aOnly : "")
                    .Append("（作品、事件、快取都不動）\n");
            c.Result.AddValue("view_mark", aMark.Length == 0 ? "now" : aMark);
            c.Result.AddValue("view_upto_event", aFilter.UptoStart ? "start" : aFilter.UptoEvent.Length == 0 ? "latest" : aFilter.UptoEvent);
            if (aHide.Length > 0) c.Result.AddValue("view_hide", aHide);
            if (aOnly.Length > 0) c.Result.AddValue("view_only", aOnly);
            return true;
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { oError = e.Message; return false; }
    }

    /// <summary>work sub=mark：作者在作品上加一個時間標記（預設標在現在；upto= 補標過去的事件或 start）。</summary>
    static string? WorkMark(Ctx c, SCP_SculptWork card)
    {
        if (c.Persona.Length == 0 || c.Persona != card.owner) return Blocked(c, 2, "只有作品作者 " + card.owner + " 可以加標記");
        try
        {
            var aMark = SCP_SculptMarks.Add(c.Works.SpacePaths(card.id), c.Args.Get("name"), c.Args.Get("phase"), c.Args.Get("upto"),
                                            c.Persona, c.Args.Get("note"), WorkOverwrite(c), DateTime.Now);
            string aAt = aMark.@event.Length == 0 ? "start（第一個事件之前）" : aMark.@event + "（第 " + aMark.event_index + " 個事件）";
            c.Result.AddValue("mark", aMark.name);
            c.Result.AddValue("phase", aMark.phase);
            c.Result.AddValue("event", aMark.@event.Length == 0 ? "start" : aMark.@event);
            c.Result.AddValue("event_index", aMark.event_index.ToString(CultureInfo.InvariantCulture));
            c.Result.AddValue("marks_file", c.Works.SpacePaths(card.id).MarksFile.Replace('\\', '/'));
            c.Report.Append("- 標記 `").Append(aMark.name).Append("`（").Append(aMark.phase).Append("）＝ 重播到 ").Append(aAt).Append(" 為止的樣子\n");
            c.Report.Append("- 看那一刻：`--arg op=view --arg work=").Append(card.id).Append(" --arg mark=").Append(aMark.name)
                    .Append(aMark.phase == SCP_SculptMarks.PhasePoint ? "" : ":" + aMark.phase).Append("`\n");
            return Finish(c, 0, "✓ 標記 " + aMark.name + "（" + aMark.phase + "）→ " + aAt);
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return Blocked(c, 2, e.Message + "；未寫入"); }
    }

    /// <summary>work sub=marks：列出作品的時間標記（事件已不在的會標出來）。</summary>
    static string? WorkMarks(Ctx c, string id)
    {
        try
        {
            int n = AppendMarks(c, id);
            c.Result.AddValue("count", n.ToString(CultureInfo.InvariantCulture));
            return Finish(c, 0, "✓ 標記 " + n + " 個");
        }
        catch (InvalidOperationException e) { return Blocked(c, 2, e.Message); }
    }

    /// <summary>把標記表接到回報（history 也用）；回傳標記數。</summary>
    static int AppendMarks(Ctx c, string id)
    {
        var aPaths = c.Works.SpacePaths(id);
        var aFile = SCP_SculptMarks.Read(aPaths);
        var aEvents = SCP_SculptStore.ListEvents(aPaths);
        c.Report.Append("\n## 時間標記（").Append(aFile.marks.Count).Append(" 個；觀測時給 mark=／hide=／only=）\n");
        if (aFile.marks.Count == 0) c.Report.Append("（還沒有 —— `--arg op=work --arg sub=mark --arg name=<名字> [--arg phase=begin|end] [--arg upto=<事件>]`）\n");
        foreach (var m in aFile.marks)
        {
            string aWhere;
            try
            {
                int i = SCP_SculptMarks.IndexOf(aEvents, m.@event, "標記");
                aWhere = i < 0 ? "start" : m.@event + "（第 " + (i + 1) + "／" + aEvents.Count + " 個事件）";
            }
            catch (ArgumentException) { aWhere = "⚠ 事件 `" + m.@event + "` 已經不在 events/"; }
            c.Report.Append("- `").Append(m.name).Append(m.phase == SCP_SculptMarks.PhasePoint ? "" : ":" + m.phase).Append("` → ").Append(aWhere)
                    .Append("｜").Append(m.created_at).Append(" by ").Append(m.persona)
                    .Append(m.note.Length > 0 ? "｜" + m.note : "").Append('\n');
        }
        return aFile.marks.Count;
    }
}
