// 區塊職責：給遊戲用的組裝層不限作者（TASK-0646）—— 外部模型（model／unmodel）、關聯（link／unlink）、固定點（anchor／unanchor）、橫繩組（ratlines／unratlines，TASK-0678）。
// 物理意義：規則由 Tim 定（2026-10-16）：「不影響模型外觀的修改 ok」——「規則可以改」「因為是為了進遊戲做的調整（meta 層）」
//          「可以在不調整模型外觀下 修改（meta 或拆件符合遊戲規格）」。之前只有 work sub=meta 不限作者（Tim 2026-10-13，TASK-0539）。
//          ⇒ 這三種跟 meta 同一級；格子（stamp／carve／assemble／spray／草稿）、繩、帆、preset、海面、風照舊只有作者能改。
//          橫繩組（TASK-0678）：Tim 2026-10-17「遊戲meta相關可以調整」⇒ 橫繩組（ratlines 的間距、繩梯 climb／foot／head）也不限作者；一條一條的繩照舊只有作者。
//          梯子的格子：Tim 2026-10-23「梯子格子只有作者能改 這個規定改掉 因為梯子跟遊戲有關(算是遊戲meta)」⇒ 梯子作品（被關聯照 meta kind=ladders
//          引用的；每艘船的梯子都這樣掛進甲板，造船規格 §9.3）的落子、move、undo／redo、草稿 apply／discard 不限作者。換色（spray）、別的作品的格子照舊只有作者。
// 留痕：不是作者改的、而且成功了 ⇒ 那件作品的 meta_log.jsonl 記一筆（誰、作者是誰、哪一種、哪一個名字、給了哪些參數）。
//       作者自己改 ⇒ 不記（跟以前一樣，models.json／links.json 的 persona 欄照舊）。
using System.Globalization;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    /// <summary>給遊戲用的組裝層：只要有 persona（不是作者也行 —— 成功之後記進 meta_log.jsonl）。</summary>
    static string? GameLayerPersona(Ctx c, string iWhat)
        => c.Persona.Length == 0 ? Blocked(c, 2, "改" + iWhat + "要給 persona（不是作者改的會記在那件作品的 meta_log.jsonl，查得到是誰改的）") : null;

    static readonly string[] s_GameLayerLogArgs = { "name", "source_work", "file", "at", "pivot", "rotate", "scale", "mirror", "turn", "axes", "color", "enabled", "note", "meta", "recolor", "link_hide", "link_carve", "overwrite",
                                                    "rope_a", "rope_b", "step", "start", "stop", "min_span", "thickness", "group", "climb", "foot", "head",   // 這排：橫繩組（TASK-0678）
                                                    "voxels", "region", "delta", "expect_pixels", "draft" };   // 梯子的格子（落子、move）

    const string LadderRule = "Tim 2026-10-23：梯子跟遊戲有關（算遊戲 meta）⇒ 梯子作品（被 kind=ladders 關聯引用的）的格子不限作者";

    /// <summary>梯子作品：有任何一條關聯照 meta kind=ladders 引用它。讀不了的 links.json 不算（⛔ 不因此放行）。</summary>
    static bool IsLadderWork(Ctx c, string iId)
    {
        foreach (SCP_SculptWork w in c.Works.List())
        {
            try { foreach (var l in SCP_SculptLinks.Read(c.Works.SpacePaths(w.id)).links) if (l.work == iId && RopeMetaKind(l.meta) == "ladders") return true; }
            catch (Exception e) when (e is InvalidOperationException || e is IOException) { }
        }
        return false;
    }

    /// <summary>動格子的門：作者 ⇒ 照舊跑；不是作者而且是梯子作品 ⇒ 跑、成功就記 meta_log；其他 ⇒ 擋（iDeny 是擋下的說明）。</summary>
    static string? VoxelGate(Ctx c, SCP_SculptWork iCard, string iSub, string iDeny, System.Func<string?> iRun)
    {
        if (c.Persona.Length > 0 && c.Persona == iCard.owner) return iRun();
        if (c.Persona.Length == 0 || !IsLadderWork(c, iCard.id)) return Blocked(c, 2, iDeny + "（例外：梯子作品 —— 被 kind=ladders 關聯引用的 —— 不限作者）");
        return GameLayer(c, iCard, "ladders", iSub, iRun, LadderRule);
    }

    /// <summary>跑一支組裝層的 sub；成功、而且動手的不是作者 ⇒ 記一筆、回傳多一行說明。</summary>
    /// <param name="iRule">記進 meta_log 的規則出處（沒給 ⇒ TASK-0646 那一條）。</param>
    static string? GameLayer(Ctx c, SCP_SculptWork iCard, string iPart, string iSub, System.Func<string?> iRun, string? iRule = null)
    {
        string? aRet = iRun();
        if (c.Result.ExitCode != 0 || c.Persona.Length == 0 || c.Persona == iCard.owner) return aRet;
        var o = SCP_JsonData.NewObject();
        o.Set("at", System.DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        o.Set("persona", c.Persona); o.Set("owner", iCard.owner); o.Set("part", iPart); o.Set("op", iSub);
        o.Set("name", c.Args.Get("name").Trim());
        var aArgs = SCP_JsonData.NewObject();
        foreach (string k in s_GameLayerLogArgs) { string v = c.Args.Get(k); if (k != "name" && c.Args.IsExplicit(k) && v.Length > 0) aArgs.Set(k, v); }   // ⛔ 只記有給的（thickness 有預設值 1 ⇒ 沒給也讀得到）
        o.Set("args", aArgs);
        string aRule = iRule ?? "TASK-0646：不影響模型外觀的修改不限作者（Tim 2026-10-16）";
        o.Set("rule", aRule);
        var aPaths = c.Works.SpacePaths(iCard.id);
        File.AppendAllText(aPaths.MetaLogFile, o.ToJson(false) + "\n", new UTF8Encoding(false));
        c.Result.Lines.Add("· 作者是 " + iCard.owner + "：" + iPart + " 不限作者（" + aRule + "）｜記在 meta_log.jsonl");
        return aRet;
    }
}
