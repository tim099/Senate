// 區塊職責：噴漆 CLI（TASK-0495）—— op=spray：預設預覽（零事件），confirm=1＋expect_plan 才落刀；draft=1 落在草稿。
// 物理意義：幾何與規則在 SCP_Core（SCP_SculptSpray.cs）；本檔只做「只在作品裡、只有作者、握作品鎖、草稿開不開」與回傳值。
//          噴漆只換色、不放不刻 ⇒ 作品內免費；共用展區不收（那裡每格都要收費，換色沒有對應的帳）。
// 失敗處置：共用展區／不是作者／沒明寫 color ⇒ exit 2；引擎的 2（參數）／3（沒有要換的格）／4（expect 對不上）照原碼回，零事件。
using System.Globalization;
using SCP.Core.Io;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    static string? OpSpray(Ctx c)
    {
        if (c.Work == null) return Blocked(c, 2, "噴漆只在作品裡 —— 要帶 work=（共用展區不收）");
        var work = c.Work;
        if (c.Persona.Length == 0 || c.Persona != work.owner) return Blocked(c, 2, "只有作品作者 " + work.owner + " 可以噴漆");
        if (!c.Args.IsExplicit("color")) return Blocked(c, 2, "spray 要明寫 color=1..255（⛔ 不吃 box 的預設 19 —— 噴錯色要再噴一次才救得回來）");
        if (!TryInt(c, "color", out int aColor)) return Blocked(c, 2, "color 要是整數：" + c.Args.Get("color"));
        int aDepth = 1;
        if (c.Args.Get("depth").Trim().Length > 0 && !TryInt(c, "depth", out aDepth)) return Blocked(c, 2, "depth 要是整數：" + c.Args.Get("depth"));
        string aOut = c.Args.Get("out").Trim();
        if (aOut.Length > 0 && !Path.IsPathRooted(aOut)) return Blocked(c, 2, "spray 的 out 要絕對路徑（got '" + aOut + "'）");
        int? aExpectPlaced = null;
        if (c.Args.Get("expect_placed").Trim().Length > 0)
        {
            if (!TryInt(c, "expect_placed", out int v)) return Blocked(c, 2, "expect_placed 要是整數：" + c.Args.Get("expect_placed"));
            aExpectPlaced = v;
        }
        bool aConfirm = c.Args.Get("confirm").Trim() == "1";
        string aCurve = c.Args.Get("curve").Trim();
        var aArgs = new SCP_SculptSprayArgs
        {
            Persona = c.Persona,
            Color = aColor,
            From = c.Args.Get("from").Trim(),
            Dir = c.Args.Get("dir").Trim(),
            To = c.Args.Get("to").Trim(),
            Path = c.Args.Get("path").Trim(),
            Curve = aCurve.Length > 0 ? aCurve : SCP_SculptSprayArgs.CurveLine,
            Sweep = c.Args.Get("sweep").Trim(),
            SweepPath = c.Args.Get("sweep_path").Trim(),
            Sweep2 = c.Args.Get("sweep2").Trim(),
            OnlyColor = c.Args.Get("only_color").Trim(),
            Depth = aDepth,
            Confirm = aConfirm,
            ExpectPlan = c.Args.Get("expect_plan").Trim(),
            ExpectChanged = aExpectPlaced,
            Out = aOut,
        };
        try
        {
            using var workLock = LockWorks(c);
            c.Work = c.Works.Load(work.id);   // 在鎖內重讀（尺寸可能剛被改過）
            if (c.Draft)
            {
                if (aConfirm)
                {
                    try { OpenDraft(c, c.Work); }   // 草稿沒開就開（底 ＝ 現在的作品）
                    catch (InvalidOperationException e) { return Blocked(c, 2, e.Message); }
                }
                else c.Result.AddValue("draft", "1");
            }
            var aEngine = NewEngine(c);
            SCP_SculptResult aRes = aEngine.Spray(aArgs);
            c.Report.Append("```\n").Append(aRes.Render()).Append("\n```\n");
            c.Result.Lines.AddRange(aRes.Lines);
            if (aRes is SCP_SculptSprayResult r)
            {
                c.Result.AddValue("applied", r.Applied ? "1" : "0");
                c.Result.AddValue("rays", r.Rays.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("rays_hit", r.RaysHit.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("rays_missed", r.RaysMissed.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("rays_blocked", r.RaysBlocked.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("changed", r.Changed.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("already", r.Already.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("plan_sha256", r.PlanSha256);
                if (r.OutputPath.Length > 0) { c.Result.AddValue("path", r.OutputPath.Replace('\\', '/')); c.Result.AddOutput(r.OutputPath.Replace('\\', '/')); }
                if (r.Applied)
                {
                    c.Result.AddValue("charged", "0");
                    c.Result.AddValue("event_file", r.EventFile);
                    c.Result.AddValue("revision", SCP_SculptWorks.Revision(NewEngine(c).LoadSpace(), c.Work.Dimensions));
                }
            }
            if (!aRes.Ok)
            {
                string aFirst = aRes.Lines.Count > 0 ? aRes.Lines[aRes.Lines.Count - 1] : "";
                return Finish(c, aRes.ExitCode, "✗ spray 沒有落刀（engine exit " + aRes.ExitCode + "）" + (aFirst.Length > 0 ? "：" + aFirst : ""));
            }
            var s = (SCP_SculptSprayResult)aRes;
            if (!s.Applied)
                return Finish(c, 0, "✓ 噴漆預覽：" + s.Rays + " 道射線、換色 " + s.Changed + " 格（零事件）" + (s.Changed > 0 ? " —— 落刀帶 confirm=1 expect_plan=" + s.PlanSha256 : ""));
            return Finish(c, 0, "✓ 作品 " + work.id + (c.Draft ? " 的草稿" : "") + " 噴漆 " + s.Changed + " 格 → c" + aColor + "，免費；可用 work sub=undo" + (c.Draft ? " draft=1" : ""));
        }
        catch (SCP_FileLockTimeoutException e) { return Blocked(c, 4, e.Message); }
        catch (InvalidOperationException e) { return Blocked(c, 1, e.Message); }   // CommitEdit：編輯前的內容已改變
    }
}
