// 區塊職責：給遊戲用的組裝層不限作者（TASK-0646）—— 外部模型（model／unmodel）、關聯（link／unlink）、固定點（anchor／unanchor）。
// 物理意義：規則由 Tim 定（2026-10-16）：「不影響模型外觀的修改 ok」——「規則可以改」「因為是為了進遊戲做的調整（meta 層）」
//          「可以在不調整模型外觀下 修改（meta 或拆件符合遊戲規格）」。之前只有 work sub=meta 不限作者（Tim 2026-10-13，TASK-0539）。
//          ⇒ 這三種跟 meta 同一級；格子（stamp／carve／assemble／spray／草稿）、繩、帆、preset、海面、風照舊只有作者能改。
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

    static readonly string[] s_GameLayerLogArgs = { "name", "source_work", "file", "at", "pivot", "rotate", "scale", "mirror", "turn", "axes", "color", "enabled", "note", "meta", "recolor", "link_hide", "link_carve", "overwrite" };

    /// <summary>跑一支組裝層的 sub；成功、而且動手的不是作者 ⇒ 記一筆、回傳多一行說明。</summary>
    static string? GameLayer(Ctx c, SCP_SculptWork iCard, string iPart, string iSub, System.Func<string?> iRun)
    {
        string? aRet = iRun();
        if (c.Result.ExitCode != 0 || c.Persona.Length == 0 || c.Persona == iCard.owner) return aRet;
        var o = SCP_JsonData.NewObject();
        o.Set("at", System.DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        o.Set("persona", c.Persona); o.Set("owner", iCard.owner); o.Set("part", iPart); o.Set("op", iSub);
        o.Set("name", c.Args.Get("name").Trim());
        var aArgs = SCP_JsonData.NewObject();
        foreach (string k in s_GameLayerLogArgs) { string v = c.Args.Get(k); if (k != "name" && v.Length > 0) aArgs.Set(k, v); }
        o.Set("args", aArgs);
        o.Set("rule", "TASK-0646：不影響模型外觀的修改不限作者（Tim 2026-10-16）");
        var aPaths = c.Works.SpacePaths(iCard.id);
        File.AppendAllText(aPaths.MetaLogFile, o.ToJson(false) + "\n", new UTF8Encoding(false));
        c.Result.Lines.Add("· 作者是 " + iCard.owner + "：" + iPart + " 不限作者（Tim 定：不影響模型外觀的修改 ok，TASK-0646）｜記在 meta_log.jsonl");
        return aRet;
    }
}
