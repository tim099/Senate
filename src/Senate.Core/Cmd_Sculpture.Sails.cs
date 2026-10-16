// 區塊職責：`sculpture` 的帆（TASK-0503）—— work sub=sail|unsail|sails|wind，與觀測（view／export）時把帆與繩一起算好（布料模擬）、畫進去。
// 物理意義：資料、角的解法、模擬全在 SCP_Core `SCP_SculptSails`（sails.json、PBD 布料、帆角當繩的端點）；本檔只做參數 → 驗 → 寫 → 讀數。
//          ⛔ 一格 voxel 都不動（不進 events、不收費）。改作品的資料 ⇒ 只有作品作者（同 rope）。
//          觀測：帆跟著關聯走（綁的那一層沒組進來 ⇒ hidden、不模擬）；sails=0／hide_sails= ⇒ 這次不畫（照樣模擬，綁在帆角的繩照樣拉）。
//                風 ＝ CLI `wind=`（m/s）＞ 組裝件 sails.json 的 wind；秒數 ＝ `sail_seconds=` ＞ sails.json 的 seconds（預設 3）。
//                綁定壞掉 ⇒ state=broken，**每次觀測都列出來**（⛔ 不靜默消失）。
// 數值影響：view 多一趟帆（voxel 數讀數不變）；export obj 多了帆的雙面網格（list／vox 只有格子 ⇒ 明說沒匯出）。
#nullable enable
using System.Globalization;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Sculpture;
using SCP.Core.Json;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    // ───────────────────────── 觀測：帆＋繩（一次建好、模擬到那一刻）─────────────────────────

    /// <summary>
    /// 觀測時的帆與繩（取代只算繩的那一支 —— 綁帆角的繩要等帆模擬完才知道形狀）。
    /// 回 null ＝ 成功（<paramref name="oRig"/> null ＝ 這個 op／這個對象沒有帆與繩可算）；否則是 exit 2 的原因。
    /// </summary>
    static string? ResolveViewRig(Ctx c, out SCP_SculptSailRig? oRig)
    {
        oRig = null;
        string aRopes = c.Args.Get("ropes").Trim(), aHide = c.Args.Get("hide_ropes").Trim();
        string aRopeGroups = c.Args.Get("rope_groups").Trim(), aHideRopeGroups = c.Args.Get("hide_rope_groups").Trim();   // TASK-0512
        string aSails = c.Args.Get("sails").Trim(), aHideSails = c.Args.Get("hide_sails").Trim();
        string aWind = c.Args.Get("wind").Trim(), aSec = c.Args.Get("sail_seconds").Trim();
        string aModels = c.Args.Get("models").Trim(), aHideModels = c.Args.Get("hide_models").Trim();
        bool aGiven = aRopes.Length > 0 || aHide.Length > 0 || aRopeGroups.Length > 0 || aHideRopeGroups.Length > 0 || aSails.Length > 0 || aHideSails.Length > 0 || aWind.Length > 0 || aSec.Length > 0 || aModels.Length > 0 || aHideModels.Length > 0;
        if (aRopes.Length > 0 && aRopes != "0" && aRopes != "1") return "ropes 要是 1 或 0（got '" + aRopes + "'）";
        if (aSails.Length > 0 && aSails != "0" && aSails != "1") return "sails 要是 1 或 0（got '" + aSails + "'）";
        if (aGiven && Array.IndexOf(RopeViewOps, c.Op) < 0) return "ropes／hide_ropes／rope_groups／hide_rope_groups／sails／hide_sails／wind／sail_seconds／models／hide_models 只給 view／export（繩、帆、模型不是格子：slice／section／stats 量的是 voxel）";
        if (c.Work == null) return aGiven ? "繩、帆、模型是作品的 —— 要帶 work=" : null;
        if (Array.IndexOf(RopeViewOps, c.Op) < 0) return null;

        List<SCP_SculptLinkNode>? aTree = c.LinkTree;
        if (aTree == null)
        {
            try { aTree = SCP_SculptLinks.Tree(c.Works, c.Work.id); }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { aTree = null; c.Report.Append("- ⚠ 繩與帆：關聯樹讀不了（綁在關聯上的會列成 broken）：").Append(e.Message).Append('\n'); }
        }
        SCP_SculptLinkSelection aSel = c.Links?.Selection ?? SCP_SculptLinkSelection.Parse(c.Args.Get("links"), c.Args.Get("hide_links"));
        if (ViewEnv(c, c.Work, out SCP_SculptSailEnv aEnv) is string aEnvBad) return aEnvBad;
        var aRopeSel = SCP_SculptRopeSelection.Parse(aRopes, aHide, aRopeGroups, aHideRopeGroups);
        var aSailSel = SCP_SculptSailSelection.Parse(aSails, aHideSails);
        // 外部模型（TASK-0507）先解：繩的端點與帆角可以綁在模型上；要畫 ⇒ 讀網格（作品來源缺快取就現產生 —— 這裡在作品鎖外）
        if (ResolveViewModels(c, c.Work, aTree, aSel, !c.Parts, out SCP_SculptModelSet aModelSet) is string aModelBad) return aModelBad;   // parts=1：網格由子模型那一份讀（TASK-0509）
        SCP_SculptSailRig aRig;
        try { aRig = SCP_SculptSails.Rig(c.Works, c.Work.id, aTree, aSel, aRopeSel, aSailSel, aEnv, null, null, aModelSet); }
        catch (InvalidOperationException e) { return "繩與帆：" + e.Message + "（只想看格子：ropes=0／sails=0 也救不了壞掉的檔 —— 先修檔）"; }
        // hide_ropes／hide_sails 點名的路徑要存在（⛔ 打錯字不會變成「什麼都沒關」）
        var aKnown = new HashSet<string>(aRig.Ropes.ConvertAll(r => r.Path), StringComparer.Ordinal);
        foreach (string p in aRopeSel.Hide)
            if (!aKnown.Contains(p)) return "沒有繩 `" + p + "`（這次看得到的：" + (aKnown.Count == 0 ? "（沒有）" : string.Join("、", aKnown)) + "）";
        // rope_groups／hide_rope_groups（TASK-0512）：點名的組要有繩（那一組或它底下的子組）—— 打錯字不會變成「什麼都沒關／全關」
        var aKnownGroups = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var r in aRig.Ropes) if (r.Group.Length > 0) aKnownGroups.Add(r.Group);
        foreach (string g in new List<string>(aRopeSel.OnlyGroups).Concat(aRopeSel.HideGroups))
            if (!aKnownGroups.Any(k => k == g || k.StartsWith(g + "/", StringComparison.Ordinal)))
                return "沒有繩的組 `" + g + "`（這次看得到的組：" + (aKnownGroups.Count == 0 ? "（沒有 —— work sub=rope group= 或 sub=ropegroup assign= 分組）" : string.Join("、", aKnownGroups)) + "）";
        var aKnownSails = new HashSet<string>(aRig.Sails.ConvertAll(s => s.Path), StringComparer.Ordinal);
        foreach (string p in aSailSel.Hide)
            if (!aKnownSails.Contains(p)) return "沒有帆 `" + p + "`（這次看得到的：" + (aKnownSails.Count == 0 ? "（沒有）" : string.Join("、", aKnownSails)) + "）";
        if (aRig.Animated) aRig.Simulate(aEnv.Seconds);
        oRig = aRig;
        return null;
    }

    /// <summary>這次觀測的環境：一格幾公尺 ＝ 組裝件的比例；風／秒數 ＝ CLI ＞ 組裝件 sails.json ＞ 預設。</summary>
    static string? ViewEnv(Ctx c, SCP_SculptWork iRoot, out SCP_SculptSailEnv oEnv)
    {
        oEnv = new SCP_SculptSailEnv { MetersPerVoxel = iRoot.MetersPerVoxel };
        SCP_SculptSailFile aFile;
        try { aFile = SCP_SculptSails.ReadSails(c.Works.SpacePaths(iRoot.id)); }
        catch (InvalidOperationException e) { return "帆：" + e.Message; }
        var aSrc = new List<string>();
        if (c.Args.IsExplicit("wind"))
        {
            if (!SCP_SculptSails.TryParseWind(c.Args.Get("wind"), out double[] w, out string aBad)) return aBad;
            oEnv.Wind = w; aSrc.Add("wind＝CLI");
        }
        else { oEnv.Wind = aFile.wind.ToArray(); aSrc.Add("wind＝作品預設"); }
        if (c.Args.IsExplicit("sail_seconds"))
        {
            if (!TryFinite(c.Args.Get("sail_seconds").Trim(), out double s) || SCP_SculptSails.CheckSeconds(s) is string) return "sail_seconds（模擬幾秒）要在 0.." + SCP_SculptSails.MaxSeconds + "：'" + c.Args.Get("sail_seconds") + "'";
            oEnv.Seconds = s; aSrc.Add("秒數＝CLI");
        }
        else { oEnv.Seconds = aFile.seconds; aSrc.Add("秒數＝作品預設"); }
        oEnv.Source = string.Join("、", aSrc);
        return null;
    }

    /// <summary>帆的讀數：環境一行＋每面帆一行 ＋ 回傳值。</summary>
    static void AppendSailReport(Ctx c, SCP_SculptSailRig iRig, bool iAlways = false, List<SCP_SculptSailReading>? iOnly = null)
    {
        List<SCP_SculptSailReading> aList = iOnly ?? iRig.Sails;
        if (aList.Count == 0 && !iAlways) return;
        int aOk = 0, aHidden = 0, aBroken = 0, aDisabled = 0, aTexBad = 0;
        double aMax = 0;
        c.Report.Append("\n## 帆（不是 voxel；布料模擬 —— 長度單位：格）\n");
        SCP_SculptSailEnv e = iRig.Env;
        double aSpeed = Math.Sqrt(e.Wind[0] * e.Wind[0] + e.Wind[1] * e.Wind[1] + e.Wind[2] * e.Wind[2]);
        c.Report.Append("- 環境：風 (").Append(SCP_SculptSails.Fmt2(e.Wind)).Append(") m/s（").Append(F2(aSpeed)).Append(" m/s）｜一格 ").Append(e.MetersPerVoxel.ToString("0.#####", CultureInfo.InvariantCulture))
                .Append(" m｜模擬 ").Append(F2(iRig.Time)).Append(" 秒 ／ ").Append(iRig.Steps).Append(" 步（dt 1/60、每步 ").Append(SCP_SculptSails.Iterations).Append(" 次迭代")
                .Append(iRig.Steps > 0 ? "、平均每步 " + iRig.LastStepMs.ToString("0.###", CultureInfo.InvariantCulture) + " ms" : "").Append("）｜").Append(e.Source).Append('\n');
        if (aList.Count == 0) c.Report.Append("（沒有 —— `--arg op=work --arg sub=sail --arg name=<名> --arg corners=\"<角0>|<角1>|<角2>[|<角3>]\" [--arg bind=0] [--arg free=2,3] [--arg color=]`）\n");
        foreach (var r in aList)
        {
            switch (r.State)
            {
                case SCP_SculptSailReading.Ok: aOk++; break;
                case SCP_SculptSailReading.Hidden: aHidden++; break;
                case SCP_SculptSailReading.Broken: aBroken++; break;
                default: aDisabled++; break;
            }
            string aIcon = r.State == SCP_SculptSailReading.Ok ? "✓" : r.State == SCP_SculptSailReading.Broken ? "🔴" : "◌";
            c.Report.Append("- ").Append(aIcon).Append(" `").Append(r.Path).Append("`［").Append(r.State).Append("］ ")
                    .Append(r.Sail.corners.Count == 3 ? "三角帆" : "四邊形").Append("｜角 ");
            for (int k = 0; k < r.Sail.corners.Count; k++)
            {
                SCP_SculptSailCorner co = r.Sail.corners[k];
                c.Report.Append(k == 0 ? "" : "、").Append(k).Append(co.name.Length > 0 ? "=" + co.name : "").Append(' ').Append(co.at)
                        .Append(" (").Append(SCP_SculptRopes.Fmt(k < r.Corners.Length ? r.Corners[k] : null)).Append(')').Append(SCP_SculptSails.CornerPinned(r.Sail, k) ? "" : "〔free〕");
            }
            if (r.Sail.bind.Count > 0) c.Report.Append("｜整條釘住的邊 ").Append(string.Join(",", r.Sail.bind));
            if (r.Simulated)
            {
                c.Report.Append("｜").Append(r.Resolution).Append(" 格／邊、").Append(r.Particles).Append(" 布點、").Append(r.Triangles).Append(" 三角形")
                        .Append("｜最鼓 ").Append(F2(r.Billow)).Append("（往法線 ").Append(SCP_SculptSails.Fmt2(r.PlaneNormal)).Append(r.Billow >= 0 ? " 那側" : " 的反側").Append("）")
                        .Append("｜平均位移 (").Append(SCP_SculptSails.Fmt2(r.MeanShift)).Append(")");
                if (r.Tethers > 0) c.Report.Append("｜牽繩 ").Append(r.Tethers).Append(" 條");
                if (Math.Abs(r.Billow) > Math.Abs(aMax)) aMax = r.Billow;
            }
            c.Report.Append("｜色 ").Append(r.Sail.color).Append("、fullness ").Append(F2(r.Sail.fullness));
            if (r.Sail.meta.Count > 0) c.Report.Append("｜meta ").Append(SCP_SculptMeta.Describe(r.Sail.meta));   // TASK-0539（沒 meta 的帆這一行一個字都不差）
            if (r.Sail.texture.Length > 0)
            {
                // 貼圖（TASK-0530）：讀得了 ⇒ 尺寸與色數；讀不了 ⇒ ⚠（照底色畫，⛔ 不靜默）
                if (r.Texture != null)
                    c.Report.Append("｜貼圖 ").Append(r.Sail.texture).Append(' ').Append(r.Texture.Width).Append('×').Append(r.Texture.Height)
                            .Append("、").Append(r.Texture.Colors).Append(" 色、畫 ").Append(r.Texture.Opaque).Append(" 像素");
                else { aTexBad++; c.Report.Append("｜⚠ 貼圖 ").Append(r.Sail.texture).Append(" 讀不了（照底色畫）：").Append(r.TextureError); }
            }
            if (r.Reason.Length > 0) c.Report.Append("｜").Append(r.Reason);
            c.Report.Append('\n');
        }
        if (aTexBad > 0)
        {
            c.Result.AddValue("sail_texture_broken", aTexBad.ToString(CultureInfo.InvariantCulture));
            c.Result.Lines.Add("⚠ 帆：" + aTexBad + " 面的貼圖讀不了（照底色畫）—— 原因見回傳檔");
        }
        c.Result.AddValue("sails_drawn", aOk.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("sails_hidden", aHidden.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("sails_disabled", aDisabled.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("sails_broken", aBroken.ToString(CultureInfo.InvariantCulture));
        if (iRig.Animated)
        {
            c.Result.AddValue("sail_seconds", F2(iRig.Time));
            c.Result.AddValue("sail_steps", iRig.Steps.ToString(CultureInfo.InvariantCulture));
            c.Result.AddValue("sail_step_ms", iRig.LastStepMs.ToString("0.###", CultureInfo.InvariantCulture));
            c.Result.AddValue("sail_max_billow", F2(aMax));
        }
        if (aBroken > 0)
            c.Result.Lines.Add("🔴 帆：" + aBroken + " 面綁定壞了（" + string.Join("、", aList.FindAll(r => r.State == SCP_SculptSailReading.Broken).ConvertAll(r => r.Path)) + "）—— 原因見回傳檔；沒畫");
    }

    // ───────────────────────── work sub=sail／unsail／sails／wind ─────────────────────────

    /// <summary>
    /// work sub=sail name= corners=角0|角1|角2[|角3] [corner_names=名|名|…] [free=角,…] [bind=邊,…] [color=] [res=] [fullness=] [enabled=] [note=]：加（或改）一面帆。
    /// 同名已在：改 free／bind／color／res／fullness／enabled／note／corner_names 直接改；換角的位置（corners=）要 overwrite=1（⛔ 不默默把帆換到別的地方）。
    /// 寫之前解一次：角解不到 ⇒ 擋、未寫入（⛔ 打錯字不會變成一面壞帆）。
    /// </summary>
    static string? WorkSail(Ctx c, SCP_SculptWork iCard)
    {
        if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "只有作品作者 " + iCard.owner + " 可以改帆");
        string aName = c.Args.Get("name");
        if (SCP_SculptRopes.ValidName(aName, "帆") is string aBadName) return Blocked(c, 2, aBadName);
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_SculptSailFile aFile = SCP_SculptSails.ReadSails(aPaths);
        int aIdx = aFile.sails.FindIndex(s => s.name == aName);
        SCP_SculptSail? aOld = aIdx >= 0 ? aFile.sails[aIdx] : null;
        string[] aPropKeys = { "corner_names", "free", "bind", "color", "res", "fullness", "enabled", "note", "texture" };
        bool aProps = Array.Exists(aPropKeys, k => c.Args.IsExplicit(k));
        bool aCorners = c.Args.IsExplicit("corners");
        if (aOld != null && !aCorners && !aProps) return Blocked(c, 2, "帆 `" + aName + "` 已經在了 —— 沒有要改什麼（free／bind／color／res／fullness／enabled／note／corner_names／texture 直接改；換角的位置要 corners= 加 overwrite=1）");
        if (aOld != null && aCorners && !WorkOverwrite(c)) return Blocked(c, 2, "已經有帆 `" + aName + "`（" + string.Join(" | ", aOld.corners.ConvertAll(k => k.at)) + "）—— ⛔ 不默默換角；要換請加 overwrite=1");
        if (aOld == null && !aCorners) return Blocked(c, 2, "加帆要 name=<名> corners=\"<角0>|<角1>|<角2>[|<角3>]\"（3 個 ＝ 三角帆、4 個 ＝ 四邊形，依序繞一圈；角：固定點名｜關聯/…/固定點名｜x,y,z）");

        var aSail = new SCP_SculptSail
        {
            name = aName,
            color = aOld?.color ?? SCP_SculptSails.DefaultColor,
            res = aOld?.res ?? 0,
            fullness = aOld?.fullness ?? SCP_SculptSails.DefaultFullness,
            enabled = aOld?.enabled ?? true,
            note = c.Args.IsExplicit("note") ? c.Args.Get("note").Trim() : aOld?.note ?? "",
            persona = aOld?.persona ?? c.Persona,
            created_at = aOld?.created_at ?? DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            texture = aOld?.texture ?? "",
            meta = aOld?.meta ?? SCP_SculptMeta.New(),   // TASK-0539：改屬性、換角都不動 meta（⛔ 沒帶過去 ⇒ 作者一改就默默清掉）；meta 走 sub=meta
        };
        // 貼圖（TASK-0530）：先解一次（壞圖、太大 ⇒ 擋、未寫入）；檔要等角驗過才複製進作品（⛔ 被擋的那一刀不留檔）
        byte[]? aTexBytes = null;
        SCP_SculptSailTexture? aTexNew = null;
        if (c.Args.IsExplicit("texture"))
        {
            string aSrc = c.Args.Get("texture").Trim();
            if (aSrc.Length == 0 || aSrc == "none") aSail.texture = "";
            else
            {
                string aSrcFull = Path.GetFullPath(aSrc);
                if (!File.Exists(aSrcFull)) return Blocked(c, 2, "texture 的檔不在：" + aSrcFull.Replace('\\', '/') + "；未寫入");
                aSail.texture = SCP_SculptSails.TextureDir + "/" + TextureFileName(aName);
                try { aTexBytes = File.ReadAllBytes(aSrcFull); aTexNew = SCP_SculptSails.DecodeTexture(aTexBytes, aSail.texture); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException) { return Blocked(c, 2, "texture：" + e.Message + "；未寫入"); }
            }
        }
        if (aCorners)
            foreach (string at in c.Args.Get("corners").Split('|')) aSail.corners.Add(new SCP_SculptSailCorner { at = at.Trim() });
        else
            foreach (var k in aOld!.corners) aSail.corners.Add(new SCP_SculptSailCorner { name = k.name, at = k.at, pin = k.pin });
        int n = aSail.corners.Count;
        if (n != 3 && n != 4) return Blocked(c, 2, "corners 要 3 個（三角帆）或 4 個（四邊形），用 | 分隔：got " + n + "；未寫入");
        // 角的名字：給了就照給的（個數要對）；換角但沒給 ⇒ 舊名字照序號沿用（個數一樣才沿用）
        if (c.Args.IsExplicit("corner_names"))
        {
            string[] aNames = c.Args.Get("corner_names").Split('|');
            if (aNames.Length != n) return Blocked(c, 2, "corner_names 要跟角一樣多（" + n + " 個，用 | 分隔；不要名字的留空）：got " + aNames.Length + "；未寫入");
            for (int k = 0; k < n; k++) aSail.corners[k].name = aNames[k].Trim();
        }
        else if (aCorners && aOld != null && aOld.corners.Count == n)
            for (int k = 0; k < n; k++) aSail.corners[k].name = aOld.corners[k].name;
        // free：哪幾個角不釘（名字或序號）；沒給 ⇒ 換角時全釘、只改屬性時沿用
        if (c.Args.IsExplicit("free"))
        {
            foreach (var k in aSail.corners) k.pin = true;
            foreach (string t in c.Args.Get("free").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int k = SCP_SculptSails.CornerIndex(aSail, t.Trim());
                if (k < 0) return Blocked(c, 2, "free 的 `" + t.Trim() + "` 不是這面帆的角（名字或序號 0.." + (n - 1) + "）；未寫入");
                aSail.corners[k].pin = false;
            }
        }
        else if (!aCorners && aOld != null)
            for (int k = 0; k < n; k++) aSail.corners[k].pin = aOld.corners[k].pin;
        if (c.Args.IsExplicit("bind"))
        {
            foreach (string t in c.Args.Get("bind").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(t.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int e)) return Blocked(c, 2, "bind 要是邊的序號（邊 k ＝ 角 k → 角 k+1），逗號分隔：'" + t + "'；未寫入");
                aSail.bind.Add(e);
            }
        }
        else if (aOld != null && aOld.corners.Count == n) aSail.bind.AddRange(aOld.bind);
        if (c.Args.IsExplicit("color"))
        {
            if (SCP_SculptRopes.TryParseColor(c.Args.Get("color"), out _, out _, out _, out string aNorm) is string aBadColor) return Blocked(c, 2, aBadColor + "；未寫入");
            aSail.color = aNorm;
        }
        if (c.Args.IsExplicit("res"))
        {
            if (!TryInt(c, "res", out int r)) return Blocked(c, 2, "res 要是整數（0 ＝ 自動）；未寫入");
            aSail.res = r;
        }
        if (c.Args.IsExplicit("fullness"))
        {
            if (!TryFinite(c.Args.Get("fullness").Trim(), out double f)) return Blocked(c, 2, "fullness 不是數字；未寫入");
            aSail.fullness = f;
        }
        if (c.Args.IsExplicit("enabled"))
        {
            string v = c.Args.Get("enabled").Trim();
            if (v != "0" && v != "1") return Blocked(c, 2, "enabled 要是 1 或 0（got '" + v + "'）；未寫入");
            aSail.enabled = v == "1";
        }
        if (SCP_SculptSails.Check(aSail) is string aBad) return Blocked(c, 2, aBad + "；未寫入");
        if (aIdx >= 0) aFile.sails[aIdx] = aSail; else aFile.sails.Add(aSail);

        // 寫之前用「寫了之後的樣子」解一次（同一支 Rig，本作品那份換成還沒存的 aFile；關聯全開 ⇒ 只判綁不綁得到）：角解不到 ⇒ 擋
        List<SCP_SculptLinkNode>? aTree;
        try { aTree = SCP_SculptLinks.Tree(c.Works, iCard.id); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return Blocked(c, 2, "關聯樹讀不了（角驗不了）：" + e.Message + "；未寫入"); }
        SCP_SculptSailRig aRig = SCP_SculptSails.Rig(c.Works, iCard.id, aTree, SCP_SculptLinkSelection.Parse("all", ""), new SCP_SculptRopeSelection(), new SCP_SculptSailSelection(),
                                                     new SCP_SculptSailEnv { MetersPerVoxel = iCard.MetersPerVoxel, Wind = aFile.wind.ToArray() }, aFile, null,
                                                     ModelsForRig(c, iCard.id, aTree, SCP_SculptLinkSelection.Parse("all", "")));
        SCP_SculptSailReading? aReading = aRig.Sails.Find(r => r.Path == aSail.name);
        if (aReading == null) return Blocked(c, 1, "寫之前的回讀沒找到這面帆（不該發生）；未寫入");
        if (aReading.State == SCP_SculptSailReading.Broken) return Blocked(c, 2, "角解不到：" + aReading.Reason + "；未寫入（先 sub=anchor 加固定點，或用 sub=anchors 看有哪些）");
        if (aTexBytes != null && aTexNew != null)
        {
            // 圖先落檔、再寫 sails.json ⇒ sails.json 永遠不會指到一張不在的圖
            string aDst = SCP_SculptSails.TextureFile(aPaths, aSail.texture), aTmp = aDst + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aDst)!);
                File.WriteAllBytes(aTmp, aTexBytes);
                if (File.Exists(aDst)) File.Replace(aTmp, aDst, null); else File.Move(aTmp, aDst);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return Blocked(c, 1, "貼圖複製進作品失敗（" + aDst.Replace('\\', '/') + "）：" + e.Message + "；sails.json 未寫入"); }
            finally { try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch (Exception) { } }
            aReading.Texture = aTexNew; aReading.TextureError = "";   // 寫之前那次解的時候檔還沒在 —— 讀數換成剛落檔的那一份（同一串位元組）
            c.Result.AddValue("texture_file", aDst.Replace('\\', '/'));
        }
        SCP_SculptSails.SaveSails(aPaths, aFile);
        if (aOld != null && aOld.texture.Length > 0 && aOld.texture != aSail.texture) DropTextureIfUnused(c, aPaths, aFile, aOld.texture);
        c.Result.AddValue("sail", aSail.name);
        c.Result.AddValue("sail_corners", n.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("sail_count", aFile.sails.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("sails_file", aPaths.SailsFile.Replace('\\', '/'));
        AppendSailReport(c, aRig, false, new List<SCP_SculptSailReading> { aReading });
        c.Report.Append("\n形狀要看模擬 ⇒ `--arg op=work --arg sub=sails --arg work=").Append(iCard.id).Append("`（讀數）或 `--arg op=view --arg work=").Append(iCard.id).Append(" [--arg wind=x,y,z]`（圖）\n");
        return Finish(c, 0, "✓ 帆 `" + aSail.name + "`（" + (n == 3 ? "三角帆" : "四邊形") + "、" + aReading.Particles + " 布點" + (aSail.enabled ? "" : "、關") + "）｜不是 voxel，一格都沒動");
    }

    /// <summary>
    /// 帆的貼圖在作品裡的檔名（TASK-0530）：帆名本身是英數＿- ⇒ <c>&lt;帆名&gt;.png</c>；
    /// 帆名用了別的字（中文…）⇒ <c>sail_&lt;帆名 UTF-8 的 SHA-256 前 8 碼&gt;.png</c>（檔名要過 <see cref="SCP_SculptSails.CheckTexturePath"/>）。
    /// </summary>
    static string TextureFileName(string iSailName)
    {
        bool aPlain = iSailName.Length > 0 && !iSailName.StartsWith(".", StringComparison.Ordinal);
        foreach (char ch in iSailName) aPlain &= ch < 128 && (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-');
        if (aPlain) return iSailName + ".png";
        using var aSha = System.Security.Cryptography.SHA256.Create();
        byte[] h = aSha.ComputeHash(Encoding.UTF8.GetBytes(iSailName));
        return "sail_" + BitConverter.ToString(h, 0, 4).Replace("-", "").ToLowerInvariant() + ".png";
    }

    /// <summary>換掉／拿掉貼圖之後：這件作品沒有別的帆用那張圖 ⇒ 刪檔（版控裡還有）；有人用 ⇒ 留著。結果寫進回傳值。</summary>
    static void DropTextureIfUnused(Ctx c, SCP_SculptPaths iPaths, SCP_SculptSailFile iFile, string iRel)
    {
        if (iFile.sails.Exists(s => s.texture == iRel)) { c.Result.AddValue("texture_kept", iRel); return; }
        string aFull = SCP_SculptSails.TextureFile(iPaths, iRel);
        try { if (File.Exists(aFull)) { File.Delete(aFull); c.Result.AddValue("texture_removed", iRel); } }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { c.Result.Lines.Add("⚠ 舊貼圖沒刪掉（" + aFull.Replace('\\', '/') + "：" + e.Message + "）—— 沒有帆用它了，可以手動刪"); }
    }

    /// <summary>work sub=unsail name=：拿掉一面帆；綁在它角上的繩（全庫掃）⇒ 列出來（它們之後每次觀測都會是 broken）。</summary>
    static string? WorkUnsail(Ctx c, SCP_SculptWork iCard)
    {
        if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "只有作品作者 " + iCard.owner + " 可以改帆");
        string aName = c.Args.Get("name").Trim();
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_SculptSailFile aFile = SCP_SculptSails.ReadSails(aPaths);
        int aIdx = aFile.sails.FindIndex(s => s.name == aName);
        if (aIdx < 0) return Blocked(c, 2, "沒有帆 `" + aName + "`（現有：" + (aFile.sails.Count == 0 ? "（沒有）" : string.Join("、", aFile.sails.ConvertAll(s => s.name))) + "）");
        string aOldTexture = aFile.sails[aIdx].texture;
        aFile.sails.RemoveAt(aIdx);
        SCP_SculptSails.SaveSails(aPaths, aFile);
        if (aOldTexture.Length > 0) DropTextureIfUnused(c, aPaths, aFile, aOldTexture);
        c.Result.AddValue("sail_count", aFile.sails.Count.ToString(CultureInfo.InvariantCulture));
        List<string> aBroken = BrokenRiggingAcrossWorks(c, iCard.id, out int aRopesBroken, out _);
        c.Result.AddValue("ropes_now_broken", aRopesBroken.ToString(CultureInfo.InvariantCulture));
        if (aBroken.Count > 0)
        {
            c.Report.Append("\n## 🔴 現在綁定壞掉的繩與帆（全庫掃：所在作品 → 路徑｜原因）\n");
            foreach (string s in aBroken) c.Report.Append("- ").Append(s).Append('\n');
            c.Result.Lines.Add("🔴 拿掉之後有 " + aBroken.Count + " 條繩／面帆綁定壞了 —— 它們沒被刪，每次觀測都會列成 broken（清單見回傳檔）");
        }
        return Finish(c, 0, "✓ 拿掉帆 `" + aName + "`" + (aBroken.Count > 0 ? "｜🔴 " + aBroken.Count + " 條繩／面帆的綁定壞了" : "｜沒有繩綁在它的角上"));
    }

    /// <summary>work sub=sails [wind=] [sail_seconds=]：每面帆模擬到那一刻的讀數（本作品＋關聯樹上的；關聯照每條 enabled）＋綁帆角的繩。</summary>
    static string? WorkSails(Ctx c, SCP_SculptWork iCard)
    {
        List<SCP_SculptLinkNode>? aTree = null;
        try { aTree = SCP_SculptLinks.Tree(c.Works, iCard.id); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { c.Report.Append("- ⚠ 關聯樹讀不了（綁在關聯上的會列成 broken）：").Append(e.Message).Append('\n'); }
        if (ViewEnv(c, iCard, out SCP_SculptSailEnv aEnv) is string aEnvBad) return Blocked(c, 2, aEnvBad);
        SCP_SculptSailRig aRig = SCP_SculptSails.Rig(c.Works, iCard.id, aTree, new SCP_SculptLinkSelection(), new SCP_SculptRopeSelection(), new SCP_SculptSailSelection(), aEnv,
                                                     null, null, ModelsForRig(c, iCard.id, aTree, new SCP_SculptLinkSelection()));
        if (aRig.Animated) aRig.Simulate(aEnv.Seconds);
        AppendSailReport(c, aRig, true);
        var aOnSails = aRig.Ropes.FindAll(r => r.FromSail.Length > 0 || r.ToSail.Length > 0);
        if (aOnSails.Count > 0) AppendRopeReport(c, aOnSails);
        c.Result.AddValue("sail_count", aRig.Sails.Count.ToString(CultureInfo.InvariantCulture));
        int aBroken = aRig.Sails.FindAll(r => r.State == SCP_SculptSailReading.Broken).Count;
        return Finish(c, 0, "✓ 帆 " + aRig.Sails.Count + " 面（含關聯）｜模擬 " + F2(aRig.Time) + " 秒" + (aBroken > 0 ? "｜🔴 " + aBroken + " 面綁定壞了" : ""));
    }

    /// <summary>work sub=wind [wind=x,y,z] [sail_seconds=]：這件作品當組裝件觀測時的預設風（m/s）與模擬秒數（寫進 sails.json）；都沒給 ⇒ 印現在的值。</summary>
    static string? WorkWind(Ctx c, SCP_SculptWork iCard)
    {
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_SculptSailFile aFile = SCP_SculptSails.ReadSails(aPaths);
        bool aSet = c.Args.IsExplicit("wind") || c.Args.IsExplicit("sail_seconds");
        if (aSet)
        {
            if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "只有作品作者 " + iCard.owner + " 可以改預設風");
            if (c.Args.IsExplicit("wind"))
            {
                if (!SCP_SculptSails.TryParseWind(c.Args.Get("wind"), out double[] w, out string aBad)) return Blocked(c, 2, aBad + "；未寫入");
                aFile.wind = new List<double>(w);
            }
            if (c.Args.IsExplicit("sail_seconds"))
            {
                if (!TryFinite(c.Args.Get("sail_seconds").Trim(), out double s) || SCP_SculptSails.CheckSeconds(s) is string aSecBad) return Blocked(c, 2, "sail_seconds 要在 0.." + SCP_SculptSails.MaxSeconds + "；未寫入");
                aFile.seconds = s;
            }
            SCP_SculptSails.SaveSails(aPaths, aFile);
        }
        string aWind = SCP_SculptSails.Fmt2(aFile.wind.ToArray());
        c.Result.AddValue("wind", aWind);
        c.Result.AddValue("sail_seconds", F2(aFile.seconds));
        c.Result.AddValue("meters_per_voxel", iCard.MetersPerVoxel.ToString("0.#####", CultureInfo.InvariantCulture));
        c.Report.Append("- 預設風 (").Append(aWind).Append(") m/s（世界座標）｜模擬 ").Append(F2(aFile.seconds)).Append(" 秒｜一格 ")
                .Append(iCard.MetersPerVoxel.ToString("0.#####", CultureInfo.InvariantCulture)).Append(" m（重力與風照它換成格）\n");
        return Finish(c, 0, (aSet ? "✓ 預設風改成 (" : "✓ 預設風 (") + aWind + ") m/s、模擬 " + F2(aFile.seconds) + " 秒（觀測時 wind=／sail_seconds= 可以一次性蓋過）");
    }

    /// <summary>
    /// work sub=sea [sea=on|off] [sea_z=] [sea_color=] [sea_opacity=]：這件作品觀測時的海面預設（TASK-0585；寫進 sea.json，渲染用、⛔ 不是 voxel）。
    /// 只給一部分 ⇒ 其他欄位沿用檔裡的；都沒給 ⇒ 印現在的值。改的只有作者。觀測時 view 的 sea=／sea_z=… 可以一次性蓋過。
    /// </summary>
    static string? WorkSea(Ctx c, SCP_SculptWork iCard)
    {
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_JsonData? aOld;
        try { aOld = SCP_SculptRenderProfiles.ReadWorkSea(aPaths); }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is SCP.Core.Json.SCP_JsonParseException)
        { return Blocked(c, 2, "sea.json 讀不了（" + e.Message + "）⇒ 先修好或刪掉它；未寫入"); }
        var aEdit = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string k in SCP_SculptRenderProfiles.SeaFlatKeys)
            if (c.Args.IsExplicit(k)) aEdit[k] = c.Args.Get(k).Trim();
        bool aSet = aEdit.Count > 0;
        var aProfile = SCP_JsonData.NewObject();
        if (aOld != null) aProfile.Set("sea", aOld);
        if (aSet)
        {
            if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "只有作品作者 " + iCard.owner + " 可以改海面預設");
            if (!SCP_SculptRenderProfiles.TryEdit(aProfile, aEdit, new List<string>(), out string aBad)) return Blocked(c, 2, aBad + "；未寫入");
        }
        // 驗：照 view 的同一支疊一次（⛔ 不合法的值不落盤）
        var aCheck = new SCP_SculptRenderParams();
        string aInvalid = "";
        if (aProfile["sea"].Exists && !SCP_SculptRenderProfiles.TryApplySeaObject(aProfile["sea"], aCheck, out aInvalid)) return Blocked(c, 2, "海面：" + aInvalid + "；未寫入");
        if (aSet)
        {
            if (!aProfile["sea"]["enabled"].Exists) aProfile["sea"].Set("enabled", true);   // 第一次只給高度 ⇒ 當成要開（設了高度卻關著，沒人會這樣想）
            if (!SCP_SculptRenderProfiles.TryApplySeaObject(aProfile["sea"], aCheck = new SCP_SculptRenderParams(), out aInvalid)) return Blocked(c, 2, "海面：" + aInvalid + "；未寫入");
            SCP_SculptRenderProfiles.SaveWorkSea(aPaths, aProfile["sea"]);
        }
        SCP_SculptSea s = aCheck.Sea ?? new SCP_SculptSea();
        string aColor = "#" + s.R.ToString("x2") + s.G.ToString("x2") + s.B.ToString("x2");
        c.Result.AddValue("sea", aCheck.Sea != null && s.Enabled ? "on" : "off");
        c.Result.AddValue("sea_z", F2(s.Z));
        c.Result.AddValue("sea_color", aColor);
        c.Result.AddValue("sea_opacity", F2(s.Opacity));
        c.Result.AddValue("sea_file", aPaths.SeaFile.Replace('\\', '/'));
        if (aCheck.Sea == null) return Finish(c, 0, "✓ 這件作品沒有海面預設（設：--arg sea_z=<世界 z> [--arg sea_color=#RRGGBB] [--arg sea_opacity=0..1]）");
        string aWhat = (s.Enabled ? "開" : "關") + "｜海面 z " + F2(s.Z) + "（" + (s.Z * iCard.MetersPerVoxel).ToString("0.##", CultureInfo.InvariantCulture) + " m）｜" + aColor + "｜不透明度 " + F2(s.Opacity);
        c.Report.Append("- 海面預設：").Append(aWhat).Append("（渲染的一片平面，⛔ 不是 voxel；觀測時 sea=／sea_z=… 可以一次性蓋過）\n");
        return Finish(c, 0, (aSet ? "✓ 海面預設改成 " : "✓ 海面預設 ") + aWhat);
    }

    /// <summary>
    /// work sub=lamps [lamps=on|off] [lamp_intensity=] [lamp_radius=] [lamp_color=]：這件作品觀測時的點光源預設（TASK-0650；寫進 lamps.json，渲染用）。
    /// 跟 sub=sea 同一套：只給一部分 ⇒ 其他欄位沿用檔裡的；都沒給 ⇒ 印現在的值；改的只有作者；第一次只給強度之類 ⇒ 當成要開。
    /// 燈在哪不在這裡設：外部模型與關聯的 meta kind=light（＋來源作品的固定點 wick）。觀測時 view 的 lamps=／lamp_*= 可以一次性蓋過。
    /// </summary>
    static string? WorkLamps(Ctx c, SCP_SculptWork iCard)
    {
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_JsonData? aOld;
        try { aOld = SCP_SculptRenderProfiles.ReadWorkLamps(aPaths); }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is SCP.Core.Json.SCP_JsonParseException)
        { return Blocked(c, 2, "lamps.json 讀不了（" + e.Message + "）⇒ 先修好或刪掉它；未寫入"); }
        var aEdit = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string k in SCP_SculptRenderProfiles.LampFlatKeys)
            if (c.Args.IsExplicit(k)) aEdit[k] = c.Args.Get(k).Trim();
        bool aSet = aEdit.Count > 0;
        var aProfile = SCP_JsonData.NewObject();
        if (aOld != null) aProfile.Set("lamps", aOld);
        if (aSet)
        {
            if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "只有作品作者 " + iCard.owner + " 可以改點光源預設");
            if (!SCP_SculptRenderProfiles.TryEdit(aProfile, aEdit, new List<string>(), out string aBad)) return Blocked(c, 2, aBad + "；未寫入");
        }
        var aCheck = new SCP_SculptRenderParams();
        string aInvalid = "";
        if (aProfile["lamps"].Exists && !SCP_SculptRenderProfiles.TryApplyLampsObject(aProfile["lamps"], aCheck, out aInvalid)) return Blocked(c, 2, "點光源：" + aInvalid + "；未寫入");
        if (aSet)
        {
            if (!aProfile["lamps"]["enabled"].Exists) aProfile["lamps"].Set("enabled", true);   // 第一次只給強度、半徑或顏色 ⇒ 當成要開（同 sub=sea）
            if (!SCP_SculptRenderProfiles.TryApplyLampsObject(aProfile["lamps"], aCheck = new SCP_SculptRenderParams(), out aInvalid)) return Blocked(c, 2, "點光源：" + aInvalid + "；未寫入");
            SCP_SculptRenderProfiles.SaveWorkLamps(aPaths, aProfile["lamps"]);
        }
        SCP_SculptLamps l = aCheck.Lamps ?? new SCP_SculptLamps();
        string aColor = "#" + l.R.ToString("x2") + l.G.ToString("x2") + l.B.ToString("x2");
        c.Result.AddValue("lamps", aCheck.Lamps != null && l.Enabled ? "on" : "off");
        c.Result.AddValue("lamp_intensity", F2(l.Intensity));
        c.Result.AddValue("lamp_radius", F2(l.Radius));
        c.Result.AddValue("lamp_color", aColor);
        c.Result.AddValue("lamps_file", aPaths.LampsFile.Replace('\\', '/'));
        if (aCheck.Lamps == null) return Finish(c, 0, "✓ 這件作品沒有點光源預設（設：--arg lamps=on [--arg lamp_intensity=0..8] [--arg lamp_radius=<公尺>] [--arg lamp_color=#RRGGBB]）");
        string aWhat = (l.Enabled ? "開" : "關") + "｜強度 " + F2(l.Intensity) + "｜半徑 " + F2(l.Radius) + " m（"
                       + (l.Radius / iCard.MetersPerVoxel).ToString("0.#", CultureInfo.InvariantCulture) + " 格）｜" + aColor;
        c.Report.Append("- 點光源預設：").Append(aWhat).Append("（燈 ＝ meta kind=light 的模型與關聯；觀測時 lamps=／lamp_*= 可以一次性蓋過）\n");
        return Finish(c, 0, (aSet ? "✓ 點光源預設改成 " : "✓ 點光源預設 ") + aWhat);
    }

    /// <summary>
    /// 全庫掃：有 ropes.json 或 sails.json 的作品逐件解一次（關聯全開 links=all），收集 broken 而且跟 <paramref name="iWorkId"/> 有關的
    /// （就在這件作品上、或它的關聯樹裡有這件作品）。讀不了的作品記一行原因（⛔ 不當成沒有）。
    /// </summary>
    static List<string> BrokenRiggingAcrossWorks(Ctx c, string iWorkId, out int oRopes, out int oSails)
    {
        var aOut = new List<string>();
        oRopes = 0; oSails = 0;
        foreach (SCP_SculptWork w in c.Works.List(""))
        {
            var p = c.Works.SpacePaths(w.id);
            if (!File.Exists(p.RopesFile) && !File.Exists(p.SailsFile)) continue;
            try
            {
                List<SCP_SculptLinkNode> aTree = SCP_SculptLinks.Tree(c.Works, w.id);
                var aIds = new SortedSet<string>(StringComparer.Ordinal) { w.id };
                SCP_SculptLinks.Closure(aTree, aIds);
                // TASK-0507：作品來源的模型（繩可以綁那件作品的固定點）也算有關
                foreach (string id in new List<string>(aIds))
                    foreach (var m in SCP_SculptModels.ReadModels(c.Works.SpacePaths(id)).models)
                        if (m.source == SCP_SculptModels.SourceWork) aIds.Add(m.work);
                if (!aIds.Contains(iWorkId)) continue;
                SCP_SculptSailRig aRig = SCP_SculptSails.Rig(c.Works, w.id, aTree, SCP_SculptLinkSelection.Parse("all", ""), new SCP_SculptRopeSelection(), new SCP_SculptSailSelection(),
                                                             new SCP_SculptSailEnv { MetersPerVoxel = w.MetersPerVoxel }, null, null,
                                                             ModelsForRig(c, w.id, aTree, SCP_SculptLinkSelection.Parse("all", "")));
                foreach (var r in aRig.Ropes) if (r.State == SCP_SculptRopeReading.Broken) { oRopes++; aOut.Add(w.id + " → 繩 `" + r.Path + "`｜" + r.Reason); }
                foreach (var s in aRig.Sails) if (s.State == SCP_SculptSailReading.Broken) { oSails++; aOut.Add(w.id + " → 帆 `" + s.Path + "`｜" + s.Reason); }
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { aOut.Add(w.id + " →（讀不了，沒辦法判）" + e.Message); }
        }
        return aOut;
    }
}
