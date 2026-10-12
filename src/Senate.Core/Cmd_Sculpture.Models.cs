// 區塊職責：`sculpture` 的外部模型（TASK-0507）—— work sub=model|unmodel|models，與觀測（view／export）時把模型讀好、換好座標、畫進去。
// 物理意義：資料、obj 解析、座標、快取鍵全在 SCP_Core `SCP_SculptModels`；本檔只做參數 → 驗 → 寫 → 讀數，外加「作品當模型」的快取產生器
//          （在行程內跑一次不帶 persona 的 `op=export format=obj`：拿來源作品自己的鎖、算它的繩與帆與模型 —— 跟 CLI 匯出同一條路）。
//          ⛔ 一格 voxel 都不動（不進 events、不收費）。改作品的資料 ⇒ 只有作品作者（同 rope）。
// 鎖：model／models 會產生快取（拿來源作品的鎖）⇒ ⛔ 不在 s_WorkLockedSubs 裡 —— 先在鎖外驗、產生快取，最後才拿 registry 鎖＋本作品鎖寫 models.json
//     （全系統只有一種鎖序：一次拿齊、不在拿著鎖的時候再去拿別的）。
// 數值影響：view 多一趟模型（voxel 數讀數不變）；export obj 多了模型的三角網格（list／vox 只有格子 ⇒ 明說沒匯出）。
#nullable enable
using System.Globalization;
using SCP.Core.Cmd;
using SCP.Core.Io;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    // ───────────────────────── 觀測：模型 ─────────────────────────

    /// <summary>
    /// 這次觀測的模型（view／export）：links=／hide_links= 的選擇、models=／hide_models= 的顯示開關；<paramref name="iGeometry"/> ⇒ 讀網格（work 來源缺快取就現產生）。
    /// 回 null ＝ 成功；否則是 exit 2 的原因。
    /// </summary>
    static string? ResolveViewModels(Ctx c, SCP_SculptWork iCard, List<SCP_SculptLinkNode>? iTree, SCP_SculptLinkSelection iSel, bool iGeometry, out SCP_SculptModelSet oSet)
    {
        string aModels = c.Args.Get("models").Trim(), aHide = c.Args.Get("hide_models").Trim();
        oSet = new SCP_SculptModelSet();
        if (aModels.Length > 0 && aModels != "0" && aModels != "1") return "models 要是 1 或 0（got '" + aModels + "'）";
        var aMSel = SCP_SculptModelSelection.Parse(aModels, aHide);
        try { oSet = SCP_SculptModels.Resolve(c.Works, iCard.id, iTree, iSel, aMSel, iGeometry, ModelCacheProducer(c)); }
        catch (InvalidOperationException e) { return "模型：" + e.Message + "（models=0 也救不了壞掉的檔 —— 先修檔）"; }
        var aKnown = new HashSet<string>(oSet.Models.ConvertAll(m => m.Path), StringComparer.Ordinal);
        foreach (string p in aMSel.Hide)
            if (!aKnown.Contains(p)) return "沒有模型 `" + p + "`（這次看得到的：" + (aKnown.Count == 0 ? "（沒有）" : string.Join("、", aKnown)) + "）";
        return null;
    }

    /// <summary>只要矩陣與狀態（繩／帆的寫入驗證、全庫掃）：不讀網格、不產生快取。讀不了 ⇒ 空的一份（model: 端點會 broken 並說原因）。</summary>
    static SCP_SculptModelSet ModelsForRig(Ctx c, string iWorkId, List<SCP_SculptLinkNode>? iTree, SCP_SculptLinkSelection iSel)
    {
        try { return SCP_SculptModels.Resolve(c.Works, iWorkId, iTree, iSel, new SCP_SculptModelSelection(), false); }
        catch (InvalidOperationException e) { c.Report.Append("- ⚠ 模型讀不了（綁在模型上的繩與帆角會列成 broken）：").Append(e.Message).Append('\n'); return new SCP_SculptModelSet(); }
    }

    /// <summary>
    /// 作品當模型的快取產生器：在行程內跑一次 `op=export format=obj work=<來源> out=<快取>`（不帶 persona ⇒ 不落回傳檔、不分享）。
    /// 來源作品自己的鎖由那一次 export 拿 —— ⚠ 呼叫端不可以正拿著作品鎖。
    /// </summary>
    static SCP_SculptModelCacheProducer ModelCacheProducer(Ctx c) => (string iWorkId, string iObjPath) =>
    {
        var aRaw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data_root"] = c.Roots.DataRoot,
            ["letters_root"] = c.Roots.LettersRoot,
            ["op"] = "export",
            ["work"] = iWorkId,
            ["format"] = "obj",
            ["out"] = iObjPath,
            ["share"] = "0",
        };
        var aCmd = new Cmd_Sculpture();
        (SCP_CmdArgs? aArgs, List<string> aErrors) = SCP_CmdArgs.Bind(aCmd.ArgSpecs, aRaw);
        if (aArgs == null) return "匯出參數不合：" + string.Join("；", aErrors);
        SCP_CmdResult r = aCmd.Execute(aArgs);
        if (r.ExitCode != 0)
        {
            string aWhy = r.Lines.Find(l => l.StartsWith("✗", StringComparison.Ordinal)) ?? (r.Lines.Count > 0 ? r.Lines[r.Lines.Count - 1] : "");
            return "匯出 " + iWorkId + " exit " + r.ExitCode + "：" + aWhy;
        }
        return null;
    };

    /// <summary>每個模型一行：狀態、來源、變換、三角形、外框、原因 ＋ 回傳值。</summary>
    static void AppendModelReport(Ctx c, SCP_SculptModelSet iSet, bool iAlways = false)
    {
        List<SCP_SculptModelReading> aList = iSet.Models;
        if (aList.Count == 0 && !iAlways) return;
        int aOk = 0, aHidden = 0, aBroken = 0, aDisabled = 0, aProduced = 0;
        c.Report.Append("\n## 外部模型（不是 voxel；座標單位：格）\n");
        if (aList.Count == 0) c.Report.Append("（沒有 —— `--arg op=work --arg sub=model --arg name=<名> --arg file=<obj>｜--arg source_work=<作品> [--arg at=] [--arg rotate=] [--arg scale=]`）\n");
        foreach (var m in aList)
        {
            switch (m.State)
            {
                case SCP_SculptModelReading.Ok: aOk++; break;
                case SCP_SculptModelReading.Hidden: aHidden++; break;
                case SCP_SculptModelReading.Broken: aBroken++; break;
                default: aDisabled++; break;
            }
            if (m.CacheProduced) aProduced++;
            string aIcon = m.State == SCP_SculptModelReading.Ok ? "✓" : m.State == SCP_SculptModelReading.Broken ? "🔴" : "◌";
            c.Report.Append("- ").Append(aIcon).Append(" `").Append(m.Path).Append("`［").Append(m.State).Append("］ ");
            if (m.Model.source == SCP_SculptModels.SourceFile)
                c.Report.Append("obj ").Append(m.Model.file).Append("（axes ").Append(m.Model.axes).Append(m.Model.sha256.Length >= 8 ? "、sha " + m.Model.sha256.Substring(0, 8) : "").Append("）");
            else
                c.Report.Append("作品 ").Append(m.Model.work).Append(m.CacheKey.Length > 0 ? "（快取 " + m.CacheKey.Substring(0, 8) + "：" + (m.CacheProduced ? "produced" : "reused") + "）" : "");
            c.Report.Append("｜").Append(SCP_SculptModels.DescribeTransform(m.Model));
            if (m.Geom != null) c.Report.Append("｜").Append((m.Geom.Triangles.Length / 3).ToString("N0", CultureInfo.InvariantCulture)).Append(" 三角形");
            if (m.Min != null && m.Max != null) c.Report.Append("｜外框 (").Append(SCP_SculptRopes.Fmt(m.Min)).Append(")–(").Append(SCP_SculptRopes.Fmt(m.Max)).Append(")");
            if (m.Model.color.Length > 0) c.Report.Append("｜色 ").Append(m.Model.color);
            if (m.Reason.Length > 0) c.Report.Append("｜").Append(m.Reason);
            c.Report.Append('\n');
        }
        c.Result.AddValue("models_drawn", aOk.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("models_hidden", aHidden.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("models_disabled", aDisabled.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("models_broken", aBroken.ToString(CultureInfo.InvariantCulture));
        if (aProduced > 0) c.Result.AddValue("model_caches_produced", aProduced.ToString(CultureInfo.InvariantCulture));
        if (aBroken > 0)
            c.Result.Lines.Add("🔴 模型：" + aBroken + " 個壞了（" + string.Join("、", aList.FindAll(m => m.State == SCP_SculptModelReading.Broken).ConvertAll(m => m.Path)) + "）—— 原因見回傳檔；沒畫");
    }

    // ───────────────────────── 參數 ─────────────────────────

    static bool TryVec3(string iText, string iName, out double[] oV, out string oBad)
    {
        oV = new double[3]; oBad = "";
        string[] p = iText.Split(',');
        if (p.Length != 3) { oBad = iName + " 要三個數 x,y,z（got '" + iText + "'）"; return false; }
        for (int i = 0; i < 3; i++)
            if (!TryFinite(p[i].Trim(), out oV[i])) { oBad = iName + " 的 `" + p[i] + "` 不是數"; return false; }
        return true;
    }

    // ───────────────────────── work sub=model／unmodel／models ─────────────────────────

    /// <summary>
    /// work sub=model name= (file=<obj> [axes=] | source_work=<作品>) [at=] [pivot=] [rotate=] [scale=] [mirror=] [color=] [enabled=] [note=]：加（或改）一個外部模型。
    /// 同名已在：變換、color、enabled、note 直接改；換來源（file=／source_work=）要 overwrite=1。
    /// 寫之前先讀一次 obj（work 來源先產生快取）：讀不了 ⇒ 擋、一個檔都沒寫。成環（含經過關聯、隔層）⇒ 擋。
    /// </summary>
    static string? WorkModel(Ctx c, string iId)
    {
        SCP_SculptWork aCard = c.Works.Load(iId);
        c.Result.AddValue("work", iId);
        if (c.Persona.Length == 0 || c.Persona != aCard.owner) return Blocked(c, 2, "只有作品作者 " + aCard.owner + " 可以改外部模型");
        string aName = c.Args.Get("name");
        if (SCP_SculptRopes.ValidName(aName, "模型") is string aBadName) return Blocked(c, 2, aBadName);
        var aPaths = c.Works.SpacePaths(iId);
        SCP_SculptModel? aOld = SCP_SculptModels.ReadModels(aPaths).models.Find(m => m.name == aName);
        bool aFileGiven = c.Args.IsExplicit("file"), aWorkGiven = c.Args.IsExplicit("source_work");
        if (aFileGiven && aWorkGiven) return Blocked(c, 2, "file= 與 source_work= 只能給一個（obj 檔，或另一件作品）");
        bool aSource = aFileGiven || aWorkGiven;
        string[] aPropKeys = { "axes", "at", "pivot", "rotate", "scale", "mirror", "color", "enabled", "note" };
        bool aProps = Array.Exists(aPropKeys, k => c.Args.IsExplicit(k));
        if (aOld != null && !aSource && !aProps) return Blocked(c, 2, "模型 `" + aName + "` 已經在了 —— 沒有要改什麼（at／pivot／rotate／scale／mirror／color／enabled／note 直接改；換來源要 file=／source_work= 加 overwrite=1）");
        if (aOld != null && aSource && !WorkOverwrite(c)) return Blocked(c, 2, "已經有模型 `" + aName + "`（" + (aOld.source == SCP_SculptModels.SourceFile ? aOld.file : "作品 " + aOld.work) + "）—— ⛔ 不默默換來源；要換請加 overwrite=1");
        if (aOld == null && !aSource) return Blocked(c, 2, "加模型要 name=<名> 加 file=<obj 絕對路徑> 或 source_work=<作品>");

        // ① 組出新的那一份（還沒寫）
        var m = new SCP_SculptModel
        {
            name = aName,
            source = aSource ? (aFileGiven ? SCP_SculptModels.SourceFile : SCP_SculptModels.SourceWork) : aOld!.source,
            file = aSource ? "" : aOld!.file, bytes = aSource ? 0 : aOld!.bytes, sha256 = aSource ? "" : aOld!.sha256,
            work = aSource ? (aWorkGiven ? c.Args.Get("source_work").Trim().ToLowerInvariant() : "") : aOld!.work,
            axes = aOld?.axes ?? SCP_SculptModels.AxesYUp,
            at = new List<double>(aOld?.at ?? new List<double> { 0, 0, 0 }),
            pivot = new List<double>(aOld?.pivot ?? new List<double> { 0, 0, 0 }),
            rotate = new List<double>(aOld?.rotate ?? new List<double> { 0, 0, 0 }),
            scale = new List<double>(aOld?.scale ?? new List<double> { 1, 1, 1 }),
            mirror = aOld?.mirror ?? "", color = aOld?.color ?? "", enabled = aOld?.enabled ?? true,
            note = c.Args.IsExplicit("note") ? c.Args.Get("note").Trim() : aOld?.note ?? "",
            persona = aOld?.persona ?? c.Persona,
            created_at = aOld?.created_at ?? DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        };
        string aSrcObj = "";
        SCP_SculptObjMesh? aMesh = null;
        if (m.source == SCP_SculptModels.SourceWork)
        {
            m.axes = SCP_SculptModels.AxesSculpt;
            if (c.Args.IsExplicit("axes") && c.Args.Get("axes").Trim() != SCP_SculptModels.AxesSculpt) return Blocked(c, 2, "作品來源的 axes 固定是 sculpt（快取是本引擎匯出的）；未寫入");
        }
        else if (c.Args.IsExplicit("axes")) m.axes = c.Args.Get("axes").Trim();
        foreach (var (aKey, aList) in new[] { ("at", m.at), ("pivot", m.pivot), ("rotate", m.rotate) })
        {
            if (!c.Args.IsExplicit(aKey)) continue;
            if (!TryVec3(c.Args.Get(aKey).Trim(), aKey, out double[] v, out string aBad)) return Blocked(c, 2, aBad + "；未寫入");
            aList.Clear(); aList.AddRange(v);
        }
        if (c.Args.IsExplicit("scale"))
        {
            string s = c.Args.Get("scale").Trim();
            if (s.IndexOf(',') < 0)
            {
                if (!TryFinite(s, out double k)) return Blocked(c, 2, "scale 要一個數或 x,y,z 三個數（got '" + s + "'）；未寫入");
                m.scale = new List<double> { k, k, k };
            }
            else
            {
                if (!TryVec3(s, "scale", out double[] v, out string aBad)) return Blocked(c, 2, aBad + "；未寫入");
                m.scale = new List<double>(v);
            }
        }
        if (c.Args.IsExplicit("mirror")) { string mi = c.Args.Get("mirror").Trim().ToLowerInvariant(); m.mirror = mi == "none" ? "" : mi; }
        if (c.Args.IsExplicit("color"))
        {
            string cl = c.Args.Get("color").Trim();
            if (cl.Length == 0 || cl == "none") m.color = "";
            else if (SCP_SculptRopes.TryParseColor(cl, out _, out _, out _, out string aNorm) is string aBadColor) return Blocked(c, 2, aBadColor + "；未寫入");
            else m.color = aNorm;
        }
        if (c.Args.IsExplicit("enabled"))
        {
            string v = c.Args.Get("enabled").Trim();
            if (v != "0" && v != "1") return Blocked(c, 2, "enabled 要是 1 或 0（got '" + v + "'）；未寫入");
            m.enabled = v == "1";
        }

        // ② 來源：file ⇒ 先讀一次（讀不了 ⇒ 擋）；work ⇒ 存在、不成環、產生快取、讀一次。比例：work 來源沒給 scale ⇒ 照兩件作品的一格幾公尺
        if (aFileGiven)
        {
            aSrcObj = c.Args.Get("file").Trim();
            if (!Path.IsPathRooted(aSrcObj)) return Blocked(c, 2, "file 要是 obj 的絕對路徑（got '" + aSrcObj + "'）；未寫入");
            try { aMesh = SCP_SculptModels.ParseObj(aSrcObj); }
            catch (Exception e) when (e is InvalidDataException || e is IOException) { return Blocked(c, 2, e.Message + "；未寫入"); }
            string aFileName = Path.GetFileName(aSrcObj);
            m.file = "models/" + aName + "/" + aFileName;
            m.bytes = new FileInfo(aSrcObj).Length;
            m.sha256 = SCP_SculptModels.FileSha256(aSrcObj);
        }
        else if (aWorkGiven)
        {
            if (m.work == iId) return Blocked(c, 2, "作品不能拿自己當模型；未寫入");
            SCP_SculptWork aSrcCard;
            try { aSrcCard = c.Works.Load(m.work); }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IOException) { return Blocked(c, 2, "來源作品 `" + m.work + "` 讀不了：" + e.Message + "；未寫入"); }
            List<string>? aLoop = SCP_SculptModels.PathTo(c.Works, m.work, iId);
            if (aLoop != null) return Blocked(c, 2, "模型來源成環：" + iId + " → " + string.Join(" → ", aLoop) + "（關聯與模型都算）；未寫入");
            if (!c.Args.IsExplicit("scale"))
            {
                double k = aSrcCard.MetersPerVoxel / aCard.MetersPerVoxel;
                m.scale = new List<double> { k, k, k };
                c.Report.Append("- scale 自動：").Append(aSrcCard.MetersPerVoxel.ToString("0.#####", CultureInfo.InvariantCulture)).Append(" m ÷ ")
                        .Append(aCard.MetersPerVoxel.ToString("0.#####", CultureInfo.InvariantCulture)).Append(" m ＝ ").Append(SCP_SculptModels.G(k)).Append('\n');
            }
            string? aCacheBad = SCP_SculptModels.EnsureCache(c.Works, m.work, ModelCacheProducer(c), out string aCache, out string aKey, out bool aProduced);
            if (aCacheBad != null) return Blocked(c, 2, "作品模型的快取：" + aCacheBad + "；未寫入");
            try { aMesh = SCP_SculptModels.LoadObj(aCache); }
            catch (Exception e) when (e is InvalidDataException || e is IOException) { return Blocked(c, 2, "快取讀不了：" + e.Message + "；未寫入"); }
            c.Report.Append("- 快取 ").Append(aKey.Substring(0, 8)).Append("：").Append(aProduced ? "produced" : "reused").Append("（").Append(aCache.Replace('\\', '/')).Append("）\n");
            c.Result.AddValue("model_cache", aProduced ? "produced" : "reused");
        }
        if (SCP_SculptModels.Check(m) is string aCheckBad) return Blocked(c, 2, aCheckBad + "；未寫入");

        // ③ 寫：registry 鎖（成環檢查看得到別人剛寫的那一筆）＋本作品鎖；鎖內再讀一次 models.json、再驗一次環
        int aCount;
        using (SCP_FileLock.Acquire(c.Works.RegistryLock, EngineLockTimeoutSec))
        using (LockAll(c, new SortedSet<string>(StringComparer.Ordinal) { iId }))
        {
            if (m.source == SCP_SculptModels.SourceWork && SCP_SculptModels.PathTo(c.Works, m.work, iId) is List<string> aLoop2)
                return Blocked(c, 2, "模型來源成環：" + iId + " → " + string.Join(" → ", aLoop2) + "；未寫入");
            SCP_SculptModelFile aFile = SCP_SculptModels.ReadModels(aPaths);
            int aIdx = aFile.models.FindIndex(x => x.name == aName);
            SCP_SculptModel? aNowOld = aIdx >= 0 ? aFile.models[aIdx] : null;
            if ((aNowOld == null) != (aOld == null)) return Blocked(c, 4, "models.json 在驗證之後被改了（`" + aName + "` " + (aNowOld == null ? "不見了" : "被加進來了") + "）—— 重跑一次；未寫入");
            if (aFileGiven)
            {
                // 複製進作品：models/<名字>/（先寫到暫存資料夾、整個換上去 ⇒ ⛔ 不會留下半套）
                string aDir = Path.Combine(aPaths.ModelsDir, aName), aTmp = aDir + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                Directory.CreateDirectory(aTmp);
                try
                {
                    File.Copy(aSrcObj, Path.Combine(aTmp, Path.GetFileName(aSrcObj)));
                    foreach (string lib in SCP_SculptModels.MtlLibs(aSrcObj))
                        File.Copy(Path.Combine(Path.GetDirectoryName(aSrcObj)!, lib), Path.Combine(aTmp, Path.GetFileName(lib)), true);
                    if (SCP_SculptModels.FileSha256(Path.Combine(aTmp, Path.GetFileName(aSrcObj))) != m.sha256) throw new IOException("複製後的 obj 雜湊對不上");
                    if (Directory.Exists(aDir)) Directory.Delete(aDir, true);
                    Directory.Move(aTmp, aDir);
                }
                finally { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); }
            }
            else if (aSource && aNowOld?.source == SCP_SculptModels.SourceFile)
            {
                // 換成作品來源 ⇒ 舊的 obj 資料夾拿掉
                string aDir = Path.Combine(aPaths.ModelsDir, aName);
                if (Directory.Exists(aDir)) Directory.Delete(aDir, true);
            }
            if (aIdx >= 0) aFile.models[aIdx] = m; else aFile.models.Add(m);
            SCP_SculptModels.SaveModels(aPaths, aFile);
            aCount = aFile.models.Count;
        }

        // ④ 讀數：寫了之後的樣子（關聯全開；只算這一個模型的網格）
        List<SCP_SculptLinkNode>? aTree = null;
        try { aTree = SCP_SculptLinks.Tree(c.Works, iId); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { c.Report.Append("- ⚠ 關聯樹讀不了：").Append(e.Message).Append('\n'); }
        SCP_SculptModelSet aSet = SCP_SculptModels.Resolve(c.Works, iId, aTree, SCP_SculptLinkSelection.Parse("all", ""), new SCP_SculptModelSelection(), true, ModelCacheProducer(c));
        SCP_SculptModelReading? aReading = aSet.Models.Find(x => x.Path == aName);
        var aOne = new SCP_SculptModelSet();
        if (aReading != null) aOne.Models.Add(aReading);
        AppendModelReport(c, aOne);
        int aTris = aMesh?.TriangleCount ?? aReading?.Mesh?.TriangleCount ?? 0;   // 只改變換 ⇒ 沒重讀來源，數字取這次讀數的網格
        c.Result.AddValue("model", m.name);
        c.Result.AddValue("model_source", m.source == SCP_SculptModels.SourceFile ? m.file : "work:" + m.work);
        c.Result.AddValue("model_triangles", aTris.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("model_count", aCount.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("models_file", aPaths.ModelsFile.Replace('\\', '/'));
        if (aMesh != null && aMesh.Warnings.Count > 0) c.Result.Lines.Add("⚠ " + string.Join("；", aMesh.Warnings));
        string aBox = aReading?.Min != null ? "｜外框 (" + SCP_SculptRopes.Fmt(aReading.Min) + ")–(" + SCP_SculptRopes.Fmt(aReading.Max) + ")" : "";
        return Finish(c, 0, "✓ 模型 `" + m.name + "` ← " + (m.source == SCP_SculptModels.SourceFile ? m.file : "作品 " + m.work) + "｜" + aTris.ToString("N0", CultureInfo.InvariantCulture) + " 三角形｜"
                            + SCP_SculptModels.DescribeTransform(m) + aBox + (m.enabled ? "" : "（關）") + "｜不是 voxel，一格都沒動");
    }

    /// <summary>work sub=unmodel name=：拿掉一個模型（file 來源連同 models/<名字>/）；綁在它上面的繩與帆角（全庫掃）⇒ 列出來。</summary>
    static string? WorkUnmodel(Ctx c, string iId)
    {
        SCP_SculptWork aCard = c.Works.Load(iId);
        c.Result.AddValue("work", iId);
        if (c.Persona.Length == 0 || c.Persona != aCard.owner) return Blocked(c, 2, "只有作品作者 " + aCard.owner + " 可以改外部模型");
        string aName = c.Args.Get("name").Trim();
        var aPaths = c.Works.SpacePaths(iId);
        int aCount;
        using (LockAll(c, new SortedSet<string>(StringComparer.Ordinal) { iId }))
        {
            SCP_SculptModelFile aFile = SCP_SculptModels.ReadModels(aPaths);
            int aIdx = aFile.models.FindIndex(x => x.name == aName);
            if (aIdx < 0) return Blocked(c, 2, "沒有模型 `" + aName + "`（現有：" + (aFile.models.Count == 0 ? "（沒有）" : string.Join("、", aFile.models.ConvertAll(x => x.name))) + "）");
            bool aWasFile = aFile.models[aIdx].source == SCP_SculptModels.SourceFile;
            aFile.models.RemoveAt(aIdx);
            SCP_SculptModels.SaveModels(aPaths, aFile);
            if (aWasFile)
            {
                string aDir = Path.Combine(aPaths.ModelsDir, aName);
                if (Directory.Exists(aDir)) Directory.Delete(aDir, true);
            }
            aCount = aFile.models.Count;
        }
        c.Result.AddValue("model_count", aCount.ToString(CultureInfo.InvariantCulture));
        List<string> aBroken = BrokenRiggingAcrossWorks(c, iId, out int aRopesBroken, out int aSailsBroken);
        c.Result.AddValue("ropes_now_broken", aRopesBroken.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("sails_now_broken", aSailsBroken.ToString(CultureInfo.InvariantCulture));
        if (aBroken.Count > 0)
        {
            c.Report.Append("\n## 🔴 現在綁定壞掉的繩與帆（全庫掃：所在作品 → 路徑｜原因）\n");
            foreach (string s in aBroken) c.Report.Append("- ").Append(s).Append('\n');
            c.Result.Lines.Add("🔴 拿掉之後有 " + aRopesBroken + " 條繩、" + aSailsBroken + " 面帆綁定壞了 —— 它們沒被刪，每次觀測都會列成 broken（清單見回傳檔）");
        }
        return Finish(c, 0, "✓ 拿掉模型 `" + aName + "`" + (aBroken.Count > 0 ? "｜🔴 " + aRopesBroken + " 條繩、" + aSailsBroken + " 面帆的綁定壞了" : "｜沒有繩或帆綁在上面"));
    }

    /// <summary>work sub=models：每個模型的讀數（本作品＋關聯樹上的；關聯照每條 enabled；work 來源缺快取就現產生）。</summary>
    static string? WorkModels(Ctx c, string iId)
    {
        SCP_SculptWork aCard = c.Works.Load(iId);
        c.Result.AddValue("work", iId);
        List<SCP_SculptLinkNode>? aTree = null;
        try { aTree = SCP_SculptLinks.Tree(c.Works, iId); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { c.Report.Append("- ⚠ 關聯樹讀不了（只列本作品）：").Append(e.Message).Append('\n'); }
        if (ResolveViewModels(c, aCard, aTree, new SCP_SculptLinkSelection(), true, out SCP_SculptModelSet aSet) is string aBad) return Blocked(c, 2, aBad);
        AppendModelReport(c, aSet, true);
        c.Result.AddValue("model_count", aSet.Models.Count.ToString(CultureInfo.InvariantCulture));
        int aBroken = aSet.Models.FindAll(m => m.State == SCP_SculptModelReading.Broken).Count;
        return Finish(c, 0, "✓ 模型 " + aSet.Models.Count + " 個（含關聯）" + (aBroken > 0 ? "｜🔴 " + aBroken + " 個壞了" : ""));
    }
}
