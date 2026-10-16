// 區塊職責：拆件（TASK-0646；work sub=split）—— 把一件作品的一部分格子搬到新的子作品，同一刀在每個引用它的地方補上擺放完全相同的引用。
// 物理意義：規則由 Tim 定（2026-10-16）：「不影響模型外觀的修改 ok」「可以在不調整模型外觀下 修改（meta 或拆件符合遊戲規格）」。
//          ⇒ 不限作者；外觀不變由引擎自己證明：寫檔之前在記憶體裡組 A（含它底下的關聯）拆前、拆後＋B，逐格比，對不上整筆不寫。
//          起因（TASK-0640）：遊戲只轉外部模型 —— 雙舵輪的支柱跟輪雕在同一件、舵在船殼格子裡 ⇒ 要拆出來才轉得對。
// 做法：
//   ① 選格：A 自己的格（不含關聯），照 colors（色號）或 voxels（清單檔 x,y,z[,c]；格要在、色要對）。
//   ② 引用：全庫掃 —— 關聯到 A 的（links.json）、拿 A 當外部模型的（models.json）。每一個都補一個雙生：名字照 twin 樣板（{name} ＝ 原名），
//      指 B，其餘欄位（at／turn／mirror／pivot／rotate／scale／axes／color／enabled／note／recolor）照抄；meta ＝ twin_meta（不抄原本的：
//      原本的 kind 那些是在講 A，例如舵輪 spin 不能讓支柱也跟著轉）。雙生關聯插在原關聯的**下一個**（組裝器「先到的格贏」的順序不變）。
//   ③ 驗：A 拆前組好 ＝ A 拆後組好 ⊎ B（不重疊、逐格同色）。A 的格原本蓋住它自己關聯的格 ⇒ 拆掉會露出來 ⇒ 對不上 ⇒ 擋。
//   ④ 寫（confirm=1；不給 ＝ 只預覽）：registry 鎖 → A＋引用它的作品＋A 的關聯樹一次拿齊 → 建 B 的書卡（作者 ＝ A 的作者、parent ＝ A 的 parent，沒有就 A）
//      → B 落一刀 workedit（action split）→ A 落一刀 → 各處補雙生 → meta_log.jsonl 記一筆（誰、從哪拆到哪）。
// ⚠ undo：A 那一刀單獨 undo 會讓格子在 A、B 各一份（外觀變了）⇒ 回傳說明；要復原請兩件都 undo 並拿掉雙生。
using System.Globalization;
using System.Text;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    static string? WorkSplit(Ctx c, string iA)
    {
        if (c.Persona.Length == 0) return Blocked(c, 2, "拆件要給 persona（記在 meta_log.jsonl 與事件上，查得到是誰拆的）");
        SCP_SculptWork aCard = c.Works.Load(iA);
        c.Result.AddValue("work", iA);
        string aB = SCP_SculptWorks.NormalizeId(c.Args.Get("into").Trim());
        if (aB.Length == 0) return Blocked(c, 2, "要給 into＝新子作品的 ID（例 " + iA + "-stand）");
        if (aB == iA) return Blocked(c, 2, "into 不能是自己");
        if (c.Works.Exists(aB)) return Blocked(c, 2, "作品 ID 已存在：" + aB + "（拆件一定建新的子作品）");
        string aTwin = c.Args.Get("twin").Trim();
        if (aTwin.Length == 0) return Blocked(c, 2, "要給 twin＝雙生引用的名字樣板（{name} ＝ 原本的名字，例 {name}座；沒有 {name} ⇒ 接在原名後面）");
        string TwinName(string n) => aTwin.Contains("{name}") ? aTwin.Replace("{name}", n) : n + aTwin;
        var aTwinMeta = SCP_SculptMeta.New();
        string aTwinMetaArg = c.Args.Get("twin_meta").Trim();
        if (aTwinMetaArg.Length > 0 && SCP_SculptMeta.Merge(aTwinMeta, aTwinMetaArg) is string aBadMeta) return Blocked(c, 2, "twin_meta：" + aBadMeta);
        var aColors = new HashSet<int>();
        foreach (string s in c.Args.Get("colors").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int k) || k < 1 || k > 255) return Blocked(c, 2, "colors 要是 1..255 的色號，逗號分隔：" + s);
            aColors.Add(k);
        }
        var aList = new Dictionary<long, int>();   // 清單檔：座標 ⇒ 色（-1 ＝ 不看色）
        string aListFile = c.Args.Get("voxels").Trim();
        if (aListFile.Length > 0)
        {
            if (!File.Exists(aListFile)) return Blocked(c, 2, "voxels 清單檔不存在：" + aListFile);
            int ln = 0;
            foreach (string raw in File.ReadLines(aListFile))
            {
                ln++;
                string l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] p = l.Split(',');
                if (p.Length < 3 || p.Length > 4) return Blocked(c, 2, "voxels 第 " + ln + " 行要是 x,y,z 或 x,y,z,color：" + l);
                var v = new int[4]; v[3] = -1;
                for (int i = 0; i < p.Length; i++)
                    if (!int.TryParse(p[i].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v[i])) return Blocked(c, 2, "voxels 第 " + ln + " 行不是整數：" + l);
                aList[SCP_SculptSurface.Key(v[0], v[1], v[2])] = v[3];
                if (aList.Count > MaxVolume) return Blocked(c, 2, "voxels 超過 " + MaxVolume.ToString("N0", CultureInfo.InvariantCulture) + " 格");
            }
        }
        if (aColors.Count == 0 && aList.Count == 0) return Blocked(c, 2, "要給 colors（色號）或 voxels（清單檔）選要搬的格");
        var aPathsA = c.Works.SpacePaths(iA);
        if (SCP_SculptDraft.IsOpen(aPathsA)) return Blocked(c, 2, iA + " 開著草稿 ⇒ 先 apply 或 discard 再拆");
        bool aWrite = c.Args.Get("confirm").Trim() == "1";

        // ② 引用（先掃一次決定要鎖誰；鎖到之後再讀一次）
        var aRefWorks = new SortedSet<string>(StringComparer.Ordinal);
        var aUnread = new List<string>();
        foreach (SCP_SculptWork w in c.Works.List())
        {
            var p = c.Works.SpacePaths(w.id);
            try { foreach (var l in SCP_SculptLinks.Read(p).links) if (l.work == iA) aRefWorks.Add(w.id); }
            catch (Exception e) when (e is InvalidOperationException || e is IOException) { aUnread.Add(w.id + "（links：" + e.Message + "）"); }
            try { foreach (var m in SCP_SculptModels.ReadModels(p).models) if (m.source == SCP_SculptModels.SourceWork && m.work == iA) aRefWorks.Add(w.id); }
            catch (Exception e) when (e is InvalidOperationException || e is IOException) { aUnread.Add(w.id + "（models：" + e.Message + "）"); }
        }
        if (aUnread.Count > 0) return Blocked(c, 2, "有作品的引用讀不了 ⇒ 不知道會不會漏掉雙生（⛔ 不默默略過）：" + string.Join("、", aUnread));

        List<SCP_SculptLinkNode> aTree = SCP_SculptLinks.Tree(c.Works, iA);
        var aLockIds = new SortedSet<string>(StringComparer.Ordinal) { iA };
        foreach (string w in aRefWorks) aLockIds.Add(w);
        SCP_SculptLinks.Closure(aTree, aLockIds);

        using var aRegistry = SCP_FileLock.Acquire(c.Works.RegistryLock, EngineLockTimeoutSec);
        using var aLocks = LockAll(c, aLockIds);
        aCard = c.Works.Load(iA);
        if (c.Works.Exists(aB)) return Blocked(c, 2, "作品 ID 已存在：" + aB);

        // ① 選格（鎖到之後才讀）
        SCP_SculptEngine aEngA = c.Works.Engine(aCard, c.Roots.DataRoot);
        SCP_SculptSpace aOwn = aEngA.LoadSpace();
        var aSel = new List<(int X, int Y, int Z, int C)>();
        foreach (var v in aOwn.Voxels.Entries())
        {
            long k = SCP_SculptSurface.Key(v.X, v.Y, v.Z);
            bool byList = aList.TryGetValue(k, out int want);
            if (byList && want >= 0 && want != v.Color) return Blocked(c, 2, "voxels 清單的 (" + v.X + "," + v.Y + "," + v.Z + ") 色是 " + want + "，作品裡是 " + v.Color);
            if (byList || aColors.Contains(v.Color)) aSel.Add((v.X, v.Y, v.Z, v.Color));
        }
        if (aList.Count > 0)
        {
            int aHit = 0; foreach (var s in aSel) if (aList.ContainsKey(SCP_SculptSurface.Key(s.X, s.Y, s.Z))) aHit++;
            if (aHit != aList.Count) return Blocked(c, 2, "voxels 清單有 " + (aList.Count - aHit) + " 格在 " + iA + " 裡是空的（⛔ 不默默略過）");
        }
        if (aSel.Count == 0) return Blocked(c, 2, iA + " 裡沒有符合的格");
        if (aSel.Count >= MaxVolume) return Blocked(c, 2, "一次拆超過 " + MaxVolume.ToString("N0", CultureInfo.InvariantCulture) + " 格；請分批");
        if (aSel.Count == aOwn.Voxels.Count) return Blocked(c, 2, "選到了 " + iA + " 的全部格 ⇒ 那不是拆件（改用關聯／模型換來源）");

        // ③ 驗：A 拆前組好 ＝ A 拆後組好 ⊎ B
        var aSelKeys = new HashSet<long>();
        foreach (var s in aSel) aSelKeys.Add(SCP_SculptSurface.Key(s.X, s.Y, s.Z));
        var aSpaceBefore = new SCP_SculptSpace();
        var aSpaceAfter = new SCP_SculptSpace();
        foreach (var v in aOwn.Voxels.Entries())
        {
            aSpaceBefore.Voxels.Set(v.X, v.Y, v.Z, v.Color);
            if (!aSelKeys.Contains(SCP_SculptSurface.Key(v.X, v.Y, v.Z))) aSpaceAfter.Voxels.Set(v.X, v.Y, v.Z, v.Color);
        }
        if (aTree.Count > 0)
        {
            var aSelAll = new SCP_SculptLinkSelection { All = true };
            aSpaceBefore = new SCP_SculptLinkComposer(c.Works, c.Roots.DataRoot, aTree, aSelAll).Compose(aSpaceBefore, aCard.SizeX, aCard.SizeY, aCard.SizeZ);
            aSpaceAfter = new SCP_SculptLinkComposer(c.Works, c.Roots.DataRoot, aTree, aSelAll).Compose(aSpaceAfter, aCard.SizeX, aCard.SizeY, aCard.SizeZ);
        }
        int aOverlap = 0;
        var aSamples = new List<string>();
        foreach (var s in aSel)
        {
            int had = aSpaceAfter.Voxels.Get(s.X, s.Y, s.Z);
            if (had != 0) { aOverlap++; if (aSamples.Count < 5) aSamples.Add("(" + s.X + "," + s.Y + "," + s.Z + ") 露出關聯的色 " + had); continue; }
            aSpaceAfter.Voxels.Set(s.X, s.Y, s.Z, s.C);
        }
        int aDiff = 0;
        foreach (var v in aSpaceBefore.Voxels.Entries()) if (aSpaceAfter.Voxels.Get(v.X, v.Y, v.Z) != v.Color) { aDiff++; if (aSamples.Count < 5) aSamples.Add("(" + v.X + "," + v.Y + "," + v.Z + ") 拆前 " + v.Color + "、拆後 " + aSpaceAfter.Voxels.Get(v.X, v.Y, v.Z)); }
        if (aSpaceAfter.Voxels.Count != aSpaceBefore.Voxels.Count) aDiff += Math.Abs(aSpaceAfter.Voxels.Count - aSpaceBefore.Voxels.Count);
        c.Result.AddValue("split_voxels", aSel.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("split_keep", (aOwn.Voxels.Count - aSel.Count).ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("split_composed", aSpaceBefore.Voxels.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("split_mismatch", (aOverlap + aDiff).ToString(CultureInfo.InvariantCulture));
        if (aOverlap + aDiff > 0)
            return Blocked(c, 2, "拆了外觀會變（" + (aOverlap + aDiff) + " 格對不上：" + string.Join("；", aSamples) + "）⇒ 一個檔都沒寫");

        // ② 雙生（鎖到之後再讀一次；名字先驗完才寫）
        var aLinkPlans = new List<(string Work, SCP_SculptLinkFile File, List<(int Index, SCP_SculptLink Twin)> Twins)>();
        var aModelPlans = new List<(string Work, SCP_SculptModelFile File, List<(int Index, SCP_SculptModel Twin)> Twins)>();
        string aNow = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        foreach (string w in aRefWorks)
        {
            var p = c.Works.SpacePaths(w);
            var lf = SCP_SculptLinks.Read(p);
            var lt = new List<(int, SCP_SculptLink)>();
            for (int i = 0; i < lf.links.Count; i++)
            {
                var l = lf.links[i];
                if (l.work != iA) continue;
                string n = TwinName(l.name);
                if (SCP_SculptRopes.ValidName(n, "關聯") is string bad) return Blocked(c, 2, w + "：" + bad);
                if (lf.links.Exists(x => x.name == n)) return Blocked(c, 2, w + " 已經有關聯 `" + n + "` ⇒ 換一個 twin 樣板");
                var t = new SCP_SculptLink
                {
                    name = n, work = aB, at = new List<int>(l.at), turn = l.turn, mirror = l.mirror, pivot = new List<int>(l.pivot), enabled = l.enabled,
                    recolor = l.recolor, created_at = aNow, persona = c.Persona, note = "拆件：從 `" + l.name + "`（" + iA + "）拆出（TASK-0646）", meta = new SortedDictionary<string, SCP_JsonData>(aTwinMeta, StringComparer.Ordinal),
                };
                lt.Add((i, t));
            }
            if (lt.Count > 0) aLinkPlans.Add((w, lf, lt));
            var mf = SCP_SculptModels.ReadModels(p);
            var mt = new List<(int, SCP_SculptModel)>();
            for (int i = 0; i < mf.models.Count; i++)
            {
                var m = mf.models[i];
                if (m.source != SCP_SculptModels.SourceWork || m.work != iA) continue;
                string n = TwinName(m.name);
                if (SCP_SculptRopes.ValidName(n, "模型") is string bad) return Blocked(c, 2, w + "：" + bad);
                if (mf.models.Exists(x => x.name == n)) return Blocked(c, 2, w + " 已經有模型 `" + n + "` ⇒ 換一個 twin 樣板");
                var t = new SCP_SculptModel
                {
                    name = n, source = SCP_SculptModels.SourceWork, work = aB, axes = m.axes, at = new List<double>(m.at), pivot = new List<double>(m.pivot),
                    rotate = new List<double>(m.rotate), scale = new List<double>(m.scale), mirror = m.mirror, color = m.color, enabled = m.enabled,
                    note = "拆件：從 `" + m.name + "`（" + iA + "）拆出（TASK-0646）", persona = c.Persona, created_at = aNow,
                    meta = new SortedDictionary<string, SCP_JsonData>(aTwinMeta, StringComparer.Ordinal),
                };
                mt.Add((i, t));
            }
            if (mt.Count > 0) aModelPlans.Add((w, mf, mt));
        }
        int aTwinLinks = 0, aTwinModels = 0;
        c.Report.Append("\n## 拆件：").Append(iA).Append(" → ").Append(aB).Append('\n');
        c.Report.Append("- 搬 ").Append(aSel.Count.ToString("N0", CultureInfo.InvariantCulture)).Append(" 格、留 ").Append((aOwn.Voxels.Count - aSel.Count).ToString("N0", CultureInfo.InvariantCulture)).Append(" 格；A 組起來 ").Append(aSpaceBefore.Voxels.Count.ToString("N0", CultureInfo.InvariantCulture)).Append(" 格，拆前拆後逐格相同\n");
        foreach (var lp in aLinkPlans) foreach (var t in lp.Twins) { aTwinLinks++; c.Report.Append("- 關聯 ").Append(lp.Work).Append(" / `").Append(t.Twin.name).Append("`（照 `").Append(lp.File.links[t.Index].name).Append("`）\n"); }
        foreach (var mp in aModelPlans) foreach (var t in mp.Twins) { aTwinModels++; c.Report.Append("- 模型 ").Append(mp.Work).Append(" / `").Append(t.Twin.name).Append("`（照 `").Append(mp.File.models[t.Index].name).Append("`）\n"); }
        c.Result.AddValue("split_twin_links", aTwinLinks.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("split_twin_models", aTwinModels.ToString(CultureInfo.InvariantCulture));
        if (!aWrite)
            return Finish(c, 0, "預覽：" + iA + " 拆 " + aSel.Count + " 格到 " + aB + "、雙生關聯 " + aTwinLinks + "、雙生模型 " + aTwinModels + "｜外觀不變（驗過）｜一個檔都沒寫 —— 加 confirm=1 才寫");

        // ④ 寫：B 的書卡 → B 一刀 → A 一刀 → 雙生 → meta_log
        string aParent = aCard.parent_work.Length > 0 ? aCard.parent_work : aCard.id;
        string aTitle = c.Args.Get("title").Trim();
        var aCardB = new SCP_SculptWork
        {
            schema = 1, id = aB, owner = aCard.owner, title = aTitle.Length > 0 ? aTitle : aCard.title + "（拆件）", parent_work = aParent, status = "ready",
            payment_ref = "sculpture-split:" + Guid.NewGuid().ToString("N"), created_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            tags = new List<string> { SCP_SculptWorks.TagSub }, meters_per_voxel = aCard.meters_per_voxel,
        };
        SCP_SculptWorks.SetSize(aCardB, new[] { aCard.SizeX, aCard.SizeY, aCard.SizeZ });
        c.Works.Save(aCardB);
        c.Works.SpacePaths(aB).EnsureBase();
        var aSrc = new SCP_SculptPyObj().Put("kind", "split").Put("from", iA).Put("to", aB).Put("by", c.Persona).Put("rule", "TASK-0646：不影響模型外觀的修改不限作者（Tim 2026-10-16）");
        var aEditB = new SCP_SculptEdit { action = "split" };
        var aEditA = new SCP_SculptEdit { action = "split" };
        foreach (var s in aSel)
        {
            aEditB.before.Add(new SCP_SculptChange(s.X, s.Y, s.Z, 0)); aEditB.after.Add(new SCP_SculptChange(s.X, s.Y, s.Z, s.C));
            aEditA.before.Add(new SCP_SculptChange(s.X, s.Y, s.Z, s.C)); aEditA.after.Add(new SCP_SculptChange(s.X, s.Y, s.Z, 0));
        }
        string aFileB, aFileA;
        try { aFileB = c.Works.Engine(aCardB, c.Roots.DataRoot).CommitEdit(aEditB, c.Persona, aSrc); }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IOException)
        {
            try { Directory.Delete(c.Works.Folder(aB), true); } catch (IOException) { }
            return Blocked(c, 1, "B 寫不進去（" + e.Message + "）⇒ 已刪掉 B，A 沒動");
        }
        try { aFileA = aEngA.CommitEdit(aEditA, c.Persona, aSrc); }
        catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IOException)
        {
            try { Directory.Delete(c.Works.Folder(aB), true); } catch (IOException) { }
            return Blocked(c, 1, "A 寫不進去（" + e.Message + "）⇒ 已刪掉 B，A 沒動");
        }
        foreach (var lp in aLinkPlans)
        {
            for (int k = lp.Twins.Count - 1; k >= 0; k--) lp.File.links.Insert(lp.Twins[k].Index + 1, lp.Twins[k].Twin);   // 從後面插 ⇒ 前面的 index 不位移
            SCP_SculptLinks.Save(c.Works.SpacePaths(lp.Work), lp.File);
        }
        foreach (var mp in aModelPlans)
        {
            for (int k = mp.Twins.Count - 1; k >= 0; k--) mp.File.models.Insert(mp.Twins[k].Index + 1, mp.Twins[k].Twin);
            SCP_SculptModels.SaveModels(c.Works.SpacePaths(mp.Work), mp.File);
        }
        var aTouched = new SortedSet<string>(StringComparer.Ordinal) { iA };
        foreach (var lp in aLinkPlans) aTouched.Add(lp.Work);
        foreach (var mp in aModelPlans) aTouched.Add(mp.Work);
        string aStamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        foreach (string w in aTouched)
        {
            var o = SCP_JsonData.NewObject();
            o.Set("at", aStamp); o.Set("persona", c.Persona); o.Set("owner", c.Works.Load(w).owner); o.Set("part", "split"); o.Set("op", "split");
            o.Set("name", aB); o.Set("from", iA); o.Set("voxels", aSel.Count); o.Set("twin", aTwin);
            o.Set("rule", "TASK-0646：不影響模型外觀的修改不限作者（Tim 2026-10-16）");
            File.AppendAllText(c.Works.SpacePaths(w).MetaLogFile, o.ToJson(false) + "\n", new UTF8Encoding(false));
        }
        c.Result.AddValue("split_into", aB);
        c.Result.AddValue("event_a", aFileA);
        c.Result.AddValue("event_b", aFileB);
        c.Result.Lines.Add("⚠ undo：A 那一刀單獨 undo 會讓格子在 A、B 各一份（外觀變了）—— 要復原請兩件都 undo、並拿掉雙生引用");
        if (c.Persona != aCard.owner) c.Result.Lines.Add("· 作者是 " + aCard.owner + "：拆件不限作者（Tim 定：不影響模型外觀的修改 ok，TASK-0646）｜B 的作者也是 " + aCard.owner);
        return Finish(c, 0, "✓ 拆件：" + iA + " → " + aB + "｜搬 " + aSel.Count + " 格｜雙生關聯 " + aTwinLinks + "、雙生模型 " + aTwinModels + "｜外觀不變（寫之前驗過）");
    }
}
