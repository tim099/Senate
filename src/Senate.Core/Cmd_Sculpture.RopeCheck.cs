// 區塊職責：`sculpture op=work sub=ropecheck`（TASK-0716）—— 繩端綁定的檢查、猜測、修正與報表（給遊戲用：遊戲照 ends 綁，寫成座標的端點只能照位置猜）。
// 物理意義：每條繩兩端照寫法分類 —— `model:`／`rope:`／`sail:`／固定點（含關聯路徑）＝ 作品寫明了綁在哪 ⇒ ok；
//          座標 `x,y,z` ⇒ 看它貼不貼實心格（RopeOnSolidM 內 ⇒ ok）、貼不貼模型（網格表面 RopeOnModelM 內、而且只有一個這麼近 ⇒ 猜「綁那個模型」）；
//          都不貼、離實心格 RopeSnapM 內 ⇒ 猜「挪到最近的實心格」；再遠 ⇒ 列出來給人看（不猜）。
//          「綁模型」改寫成 `model:<模型>/<x,y,z>`（模型自己的座標，反算回去跟原本同一點 ⇒ 畫出來一點都不動）；
//          「挪到實心格」改寫成那一格表面上最近的一點（照繩所在那一層的座標，最多挪 RopeSnapM）。
//          可疑的（索具 shroud／stay… 猜到砲、火藥桶、燈、槳、錨、小艇身上）⇒ 只列不改。
// 權限：Tim 2026-10-18「繩端問題是否可以在模型建造端修復（這有關遊戲，不用作者本人修改，維持外貌接近即可）」⇒ fix=1 不限作者，
//       每改一端在那件作品的 meta_log.jsonl 記一筆（誰、作者、哪條繩、哪一端、改之前、改之後、為什麼）—— 跟 GameLayer 同一份 log。
// 數值影響：不給 fix ⇒ 只讀（不拿鎖、不寫任何作品檔），寫報表；fix=1 ⇒ 只改那幾條繩的 from／to（ropes.json），格子、模型、帆一格都不動。
// 失敗處置：關聯樹、模型、繩讀不了 ⇒ exit 2；fix 時繩的端點跟分析時不一樣（別人剛改過）⇒ 那一端跳過、照實列出。
#nullable enable
using System.Globalization;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    const string RopeCheckRule = "TASK-0716：繩端綁定給遊戲用、端點不動或挪到最近的實心格（≤ 30 cm）不限作者（Tim 2026-10-18「這有關遊戲 不用作者本人修改 維持外貌接近即可」）";
    /// <summary>座標端離實心格多近算「綁在格子上」（公尺；遊戲的繩固定檢查也是 0.15 m）。</summary>
    const double RopeOnSolidM = 0.15;
    /// <summary>座標端離模型網格表面多近算「就在模型上」（公尺）。</summary>
    const double RopeOnModelM = 0.05;
    /// <summary>不在格子也不在模型上 ⇒ 最多挪多遠到最近的實心格（公尺；Tim：外貌接近）。</summary>
    const double RopeSnapM = 0.30;
    /// <summary>座標端離另一條繩的折線多近算「掛在那條繩上」（格；跟遊戲 10_ship.js linkRopes 找母繩同一個門檻：距離² ＜ 9）。</summary>
    const double RopeOnLineCells = 3;
    /// <summary>報表列「附近的模型」看多遠（公尺）。</summary>
    const double RopeNearM = 1.0;
    static readonly HashSet<string> s_RopeRigKinds = new(StringComparer.Ordinal) { "shroud", "stay", "backstay", "ratline", "sheet", "tack", "steering" };
    static readonly HashSet<string> s_RopeOddTargets = new(StringComparer.Ordinal) { "gun", "powder", "light", "oar", "anchor", "boat" };

    sealed class RopeEndCheck
    {
        public SCP_SculptRopeReading R = null!;
        public int End;                      // 0 ＝ from、1 ＝ to
        public string Frame = "", Holder = "", Text = "", Kind = "", Act = "", New = "", Why = "";
        public double[]? P;
        public double Dv = double.NaN, Dm = double.NaN, Dm2 = double.NaN;
        public string Model = "", ModelKind = "", Model2 = "";
    }

    static string? WorkRopeCheck(Ctx c, string iId)
    {
        SCP_SculptWork aCard = c.Works.Load(iId);
        bool aFix = c.Args.Get("fix").Trim() == "1";
        if (aFix && GameLayerPersona(c, "繩端綁定") is string aNoPersona) return aNoPersona;
        if (!TryOutPath(c, "ropecheck_" + aCard.id + ".md", out string aOut, out string aOutBad)) return Blocked(c, 2, aOutBad);
        double mpv = aCard.MetersPerVoxel;

        List<SCP_SculptLinkNode> aTree;
        try { aTree = SCP_SculptLinks.Tree(c.Works, aCard.id); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return Blocked(c, 2, "關聯樹讀不了：" + e.Message); }
        var aSel = new SCP_SculptLinkSelection();
        if (ResolveViewModels(c, aCard, aTree, aSel, true, out SCP_SculptModelSet aModels) is string aBad) return Blocked(c, 2, aBad);
        var aIndex = SCP_SculptRopes.IndexTree(aTree);
        List<SCP_SculptRopeReading> aRopes = SCP_SculptSails.Rig(c.Works, aCard.id, aTree, aSel, new SCP_SculptRopeSelection(), new SCP_SculptSailSelection(),
                                                                 new SCP_SculptSailEnv { MetersPerVoxel = mpv }, null, null, aModels).Ropes;
        SCP_SculptSpace aSpace = Flatten(c, aCard, aTree, new List<SCP_SculptCredit>());
        var aVox = aSpace.Voxels;

        // 每一端分類、猜
        var aAll = new List<RopeEndCheck>();
        int aSolidR = (int)Math.Ceiling(Math.Max(RopeSnapM, RopeNearM) / mpv);
        foreach (var r in aRopes)
        {
            string aFrame = r.Path.Length > r.Rope.name.Length ? r.Path.Substring(0, r.Path.Length - r.Rope.name.Length - 1) : "";
            string aHolder = aFrame.Length == 0 ? aCard.id : aIndex.TryGetValue(aFrame, out var aNode) ? aNode.Card.id : "";
            for (int e = 0; e < 2; e++)
            {
                var x = new RopeEndCheck { R = r, End = e, Frame = aFrame, Holder = aHolder, Text = e == 0 ? r.Rope.from : r.Rope.to, P = e == 0 ? r.From : r.To };
                aAll.Add(x);
                if (x.Text.StartsWith("model:", StringComparison.Ordinal)) x.Kind = "model";
                else if (x.Text.StartsWith("rope:", StringComparison.Ordinal)) x.Kind = "rope";
                else if (x.Text.StartsWith("sail:", StringComparison.Ordinal)) x.Kind = "sail";
                else x.Kind = SCP_SculptRopes.TryParseEndpoint(x.Text, out double[]? aXyz, out _, out _) && aXyz != null ? "xyz" : "anchor";
                if (r.State == SCP_SculptRopeReading.Broken) { x.Act = "broken"; x.Why = r.Reason; continue; }
                if (x.Kind != "xyz" || x.P == null) { x.Act = "ok"; x.Why = "作品寫明了綁在哪"; continue; }
                RopeNearestSolid(aVox, x.P, aSolidR, out x.Dv, out double[]? aCell);
                x.Dv *= mpv;
                RopeNearestModels(aModels, x.P, RopeNearM / mpv, x);
                x.Dm *= mpv; x.Dm2 *= mpv;
                string aRopeKind = RopeMetaKind(r.Rope.meta);
                if (x.Dv <= RopeOnSolidM) { x.Act = "ok"; x.Why = "貼著實心格（" + Cm(x.Dv) + "）"; continue; }
                string aOnRope = RopeNearestLine(aRopes, r, x.P, RopeOnLineCells);
                bool aOnModel = x.Dm <= RopeOnModelM && !(x.Dm2 <= 2 * x.Dm + mpv);
                if (aOnModel)
                {
                    if (s_RopeRigKinds.Contains(aRopeKind) && s_RopeOddTargets.Contains(x.ModelKind)) { x.Act = "odd"; x.Why = "索具（" + aRopeKind + "）的端點在 " + x.ModelKind + " 模型上 —— 多半是擺錯，不猜"; continue; }
                    string? aNew = RopeModelRef(aModels, x.Frame, x.Model, x.P, out string aWhy);
                    if (aNew == null) { x.Act = "manual"; x.Why = aWhy; continue; }
                    x.Act = "bind"; x.New = aNew; x.Why = "就在模型 `" + x.Model + "` 表面（" + Cm(x.Dm) + "）⇒ 綁它，端點不動"; continue;
                }
                if (aOnRope.Length > 0) { x.Act = "onrope"; x.Why = "端點就在繩 `" + aOnRope + "` 上（遊戲照母繩綁；寫成 rope: 會變成拉直的子繩、外貌會變 ⇒ 不改）"; continue; }
                if (aCell != null && x.Dv <= RopeSnapM)
                {
                    double[] aTo = new double[3];
                    for (int k = 0; k < 3; k++) aTo[k] = Math.Clamp(x.P[k], aCell[k] - 0.5, aCell[k] + 0.5);
                    string? aNew = RopeFrameXyz(x.Frame, aTo, aIndex, out string aWhy);
                    if (aNew == null) { x.Act = "manual"; x.Why = aWhy; continue; }
                    x.Act = "snap"; x.New = aNew; x.Why = "懸空；最近的實心格 " + Cm(x.Dv) + " ⇒ 挪過去" + (x.Model.Length > 0 ? "（附近的模型 `" + x.Model + "` " + Cm(x.Dm) + "，不夠近）" : "");
                    continue;
                }
                x.Act = "manual";
                x.Why = "懸空：最近的實心格 " + (double.IsNaN(x.Dv) ? "＞" + Cm(RopeNearM) : Cm(x.Dv)) + (x.Model.Length > 0 ? "、最近的模型 `" + x.Model + "` " + Cm(x.Dm) : "") + " —— 太遠，不猜";
            }
        }

        // fix=1：照猜到的改（bind／snap），一件作品一件作品來
        int aFixed = 0, aSkipped = 0;
        if (aFix)
        {
            var aByHolder = new Dictionary<string, List<RopeEndCheck>>(StringComparer.Ordinal);
            foreach (var x in aAll) if ((x.Act == "bind" || x.Act == "snap") && x.Holder.Length > 0) { if (!aByHolder.TryGetValue(x.Holder, out var L)) aByHolder[x.Holder] = L = new List<RopeEndCheck>(); L.Add(x); }
            foreach (var kv in aByHolder)
            {
                using var aLock = LockAll(c, new[] { kv.Key });
                SCP_SculptWork aHolderCard = c.Works.Load(kv.Key);
                var aPaths = c.Works.SpacePaths(kv.Key);
                SCP_SculptRopeFile aFile = SCP_SculptRopes.ReadRopes(aPaths);
                var aLog = new StringBuilder();
                int aHere = 0;
                foreach (var x in kv.Value)
                {
                    var rope = aFile.ropes.Find(q => q.name == x.R.Rope.name);
                    string? aCur = rope == null ? null : x.End == 0 ? rope.from : rope.to;
                    if (rope == null || aCur != x.Text) { x.Act += "-skip"; x.Why += "（⚠ 改的時候這一端已經不是 `" + x.Text + "` ⇒ 沒改）"; aSkipped++; continue; }
                    if (x.End == 0) rope.from = x.New; else rope.to = x.New;
                    aHere++; aFixed++; x.Act += "-done";
                    var o = SCP_JsonData.NewObject();
                    o.Set("at", DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
                    o.Set("persona", c.Persona); o.Set("owner", aHolderCard.owner); o.Set("part", "rope"); o.Set("op", "ropecheck"); o.Set("name", rope.name);
                    var a = SCP_JsonData.NewObject(); a.Set("end", x.End == 0 ? "from" : "to"); a.Set("before", x.Text); a.Set("after", x.New); a.Set("act", x.Act.Replace("-done", "")); a.Set("why", x.Why);
                    o.Set("args", a); o.Set("rule", RopeCheckRule);
                    aLog.Append(o.ToJson(false)).Append('\n');
                }
                if (aHere == 0) continue;
                SCP_SculptRopes.SaveRopes(aPaths, aFile);
                File.AppendAllText(aPaths.MetaLogFile, aLog.ToString(), new UTF8Encoding(false));
            }
        }

        // 報表
        var aCount = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var x in aAll) { string k = x.Act.Replace("-done", "").Replace("-skip", ""); aCount[k] = aCount.TryGetValue(k, out int n) ? n + 1 : 1; }
        var md = new StringBuilder();
        md.Append("# 繩端綁定檢查：").Append(aCard.id).Append("（").Append(aCard.title).Append("）\n\n");
        md.Append("TASK-0716；").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture)).Append("；一格 ").Append(mpv.ToString("0.###", CultureInfo.InvariantCulture)).Append(" m")
          .Append(aFix ? "；**fix=1：改了 " + aFixed + " 端" + (aSkipped > 0 ? "、跳過 " + aSkipped + " 端" : "") + "**" : "；只讀（沒改）").Append("\n\n");
        md.Append("規則：`model:`／`rope:`／`sail:`／固定點 ＝ 作品寫明了 ⇒ ok。座標端：貼實心格（≤ ").Append(Cm(RopeOnSolidM)).Append("）⇒ ok；就在另一條繩上（≤ ").Append(RopeOnLineCells.ToString(CultureInfo.InvariantCulture)).Append(" 格，同遊戲）⇒ **onrope**（遊戲照母繩綁，不改）；")
          .Append("在某個模型的表面上（≤ ").Append(Cm(RopeOnModelM)).Append("、沒有第二個一樣近）⇒ **bind**（改寫成 `model:<模型>/<模型座標>`，端點不動）；")
          .Append("都不貼、離實心格 ≤ ").Append(Cm(RopeSnapM)).Append(" ⇒ **snap**（挪到那一格表面）；索具猜到砲／火藥桶／燈／槳／錨／小艇 ⇒ **odd**（不改）；再遠 ⇒ **manual**（不改）。\n\n");
        md.Append("| 分類 | 端數 |\n|---|---|\n");
        foreach (var kv in aCount) md.Append("| ").Append(kv.Key).Append(" | ").Append(kv.Value).Append(" |\n");
        md.Append("\n## 要看的（不是 ok 的每一端）\n\n| 繩 | 端 | 現在 | 分類 | 最近實心格 | 最近模型 | 改成 | 為什麼 |\n|---|---|---|---|---|---|---|---|\n");
        int aRows = 0;
        foreach (var x in aAll)
        {
            if (x.Act == "ok") continue;
            aRows++;
            md.Append("| `").Append(x.R.Path).Append("` | ").Append(x.End == 0 ? "from" : "to").Append(" | `").Append(x.Text).Append("` | ").Append(x.Act).Append(" | ")
              .Append(double.IsNaN(x.Dv) ? "—" : Cm(x.Dv)).Append(" | ").Append(x.Model.Length == 0 ? "—" : "`" + x.Model + "`（" + x.ModelKind + "）" + Cm(x.Dm)).Append(" | ")
              .Append(x.New.Length == 0 ? "—" : "`" + x.New + "`").Append(" | ").Append(x.Why.Replace("|", "／")).Append(" |\n");
        }
        if (aRows == 0) md.Append("| （沒有）| | | | | | | |\n");
        md.Append("\n核對：每一端照它的座標近看，例 `senate cmd sculpture --arg op=view --arg work=").Append(aCard.id).Append(" --arg persona=<你> --arg focus=<x,y,z>`（看 bind 的端點是不是真的在那個模型上、snap 挪了之後外貌接不接近）。\n");
        string? aDir = Path.GetDirectoryName(aOut);
        if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
        File.WriteAllText(aOut, md.ToString(), new UTF8Encoding(false));

        foreach (var kv in aCount) c.Result.AddValue("ends_" + kv.Key, kv.Value.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("ropes", aRopes.Count.ToString(CultureInfo.InvariantCulture));
        if (aFix) c.Result.AddValue("fixed", aFixed.ToString(CultureInfo.InvariantCulture));
        if (aSkipped > 0) c.Result.AddValue("skipped", aSkipped.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("path", aOut);
        c.Result.AddOutput(aOut);
        int aGuess = (aCount.TryGetValue("bind", out int b) ? b : 0) + (aCount.TryGetValue("snap", out int s) ? s : 0);
        int aLook = (aCount.TryGetValue("odd", out int od) ? od : 0) + (aCount.TryGetValue("manual", out int mn) ? mn : 0) + (aCount.TryGetValue("broken", out int br) ? br : 0);
        string aHead = aFix ? "✓ ropecheck fix：改了 " + aFixed + " 端" + (aSkipped > 0 ? "、跳過 " + aSkipped : "") + (aLook > 0 ? "｜還要人看 " + aLook + " 端" : "")
                            : (aGuess + aLook == 0 ? "✓ ropecheck：繩 " + aRopes.Count + " 條，兩端都綁好了" : "⚠ ropecheck：繩 " + aRopes.Count + " 條｜猜得到 " + aGuess + " 端（fix=1 照報表改）｜要人看 " + aLook + " 端");
        return Finish(c, 0, aHead + " → " + aOut);
    }

    /// <summary>離這一點 R 格內的另一條繩（折線）；沒有 ⇒ 空。</summary>
    static string RopeNearestLine(List<SCP_SculptRopeReading> iRopes, SCP_SculptRopeReading iSelf, double[] iP, double iR)
    {
        string best = ""; double bd = iR * iR;
        foreach (var o in iRopes)
        {
            if (ReferenceEquals(o, iSelf) || o.Line == null) continue;
            var L = o.Line;
            for (int k = 0; k + 5 < L.Length; k += 3)
            {
                double ux = L[k + 3] - L[k], uy = L[k + 4] - L[k + 1], uz = L[k + 5] - L[k + 2], wx = iP[0] - L[k], wy = iP[1] - L[k + 1], wz = iP[2] - L[k + 2];
                double uu = ux * ux + uy * uy + uz * uz, t = uu > 0 ? Math.Clamp((ux * wx + uy * wy + uz * wz) / uu, 0, 1) : 0;
                double ex = wx - t * ux, ey = wy - t * uy, ez = wz - t * uz, d = ex * ex + ey * ey + ez * ez;
                if (d <= bd) { bd = d; best = o.Path; }
            }
        }
        return best;
    }

    static string Cm(double iM) => double.IsNaN(iM) ? "—" : (iM * 100).ToString("0", CultureInfo.InvariantCulture) + " cm";

    static string RopeMetaKind(SortedDictionary<string, SCP_JsonData>? iMeta)
        => iMeta != null && iMeta.TryGetValue("kind", out SCP_JsonData? v) ? v.AsString() : "";

    /// <summary>最近的實心格（格心）距離（格）；R 格內沒有 ⇒ NaN。</summary>
    static void RopeNearestSolid(SCP_SculptVoxelMap iVox, double[] iP, int iR, out double oD, out double[]? oCell)
    {
        oD = double.NaN; oCell = null;
        int x0 = (int)Math.Floor(iP[0]), y0 = (int)Math.Floor(iP[1]), z0 = (int)Math.Floor(iP[2]);
        double best = double.MaxValue;
        for (int dz = -iR; dz <= iR; dz++)
            for (int dy = -iR; dy <= iR; dy++)
                for (int dx = -iR; dx <= iR; dx++)
                {
                    int x = x0 + dx, y = y0 + dy, z = z0 + dz;
                    if (x < 0 || y < 0 || z < 0 || iVox.Get(x, y, z) == 0) continue;
                    double ex = x + 0.5 - iP[0], ey = y + 0.5 - iP[1], ez = z + 0.5 - iP[2], d = ex * ex + ey * ey + ez * ez;
                    if (d < best) { best = d; oCell = new double[] { x + 0.5, y + 0.5, z + 0.5 }; }
                }
        if (oCell != null) oD = Math.Sqrt(best);
    }

    /// <summary>R 格內最近、第二近的模型（網格表面距離，格）。</summary>
    static void RopeNearestModels(SCP_SculptModelSet iSet, double[] iP, double iR, RopeEndCheck ioX)
    {
        ioX.Dm = double.NaN; ioX.Dm2 = double.NaN;
        double b1 = double.MaxValue, b2 = double.MaxValue;
        foreach (var m in iSet.Models)
        {
            if (m.Geom == null || m.Min == null || m.Max == null) continue;
            if (iP[0] < m.Min[0] - iR || iP[0] > m.Max[0] + iR || iP[1] < m.Min[1] - iR || iP[1] > m.Max[1] + iR || iP[2] < m.Min[2] - iR || iP[2] > m.Max[2] + iR) continue;
            double d = MeshDistance(m.Geom, iP);
            if (d > iR) continue;
            if (d < b1) { b2 = b1; ioX.Model2 = ioX.Model; b1 = d; ioX.Model = m.Path; ioX.ModelKind = RopeMetaKind(m.Model.meta); }
            else if (d < b2) { b2 = d; ioX.Model2 = m.Path; }
        }
        if (b1 < double.MaxValue) ioX.Dm = b1;
        if (b2 < double.MaxValue) ioX.Dm2 = b2;
    }

    static double MeshDistance(SCP_SculptModelGeom iG, double[] iP)
    {
        double best = double.MaxValue;
        var P = iG.Points; var T = iG.Triangles;
        for (int t = 0; t + 2 < T.Length; t += 3)
        {
            int a = T[t] * 3, b = T[t + 1] * 3, c = T[t + 2] * 3;
            double d = PointTriangle2(iP, P[a], P[a + 1], P[a + 2], P[b], P[b + 1], P[b + 2], P[c], P[c + 1], P[c + 2]);
            if (d < best) best = d;
        }
        return Math.Sqrt(best);
    }

    /// <summary>點到三角形的距離平方（Ericson, Real-Time Collision Detection 5.1.5）。</summary>
    static double PointTriangle2(double[] p, double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
    {
        double abx = bx - ax, aby = by - ay, abz = bz - az, acx = cx - ax, acy = cy - ay, acz = cz - az;
        double apx = p[0] - ax, apy = p[1] - ay, apz = p[2] - az;
        double d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
        double qx, qy, qz;
        if (d1 <= 0 && d2 <= 0) { qx = ax; qy = ay; qz = az; }
        else
        {
            double bpx = p[0] - bx, bpy = p[1] - by, bpz = p[2] - bz;
            double d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
            if (d3 >= 0 && d4 <= d3) { qx = bx; qy = by; qz = bz; }
            else
            {
                double vc = d1 * d4 - d3 * d2;
                if (vc <= 0 && d1 >= 0 && d3 <= 0) { double v = d1 / (d1 - d3); qx = ax + v * abx; qy = ay + v * aby; qz = az + v * abz; }
                else
                {
                    double cpx = p[0] - cx, cpy = p[1] - cy, cpz = p[2] - cz;
                    double d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
                    if (d6 >= 0 && d5 <= d6) { qx = cx; qy = cy; qz = cz; }
                    else
                    {
                        double vb = d5 * d2 - d1 * d6;
                        if (vb <= 0 && d2 >= 0 && d6 <= 0) { double w = d2 / (d2 - d6); qx = ax + w * acx; qy = ay + w * acy; qz = az + w * acz; }
                        else
                        {
                            double va = d3 * d6 - d5 * d4;
                            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) { double w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); qx = bx + w * (cx - bx); qy = by + w * (cy - by); qz = bz + w * (cz - bz); }
                            else { double den = 1 / (va + vb + vc), v = vb * den, w = vc * den; qx = ax + abx * v + acx * w; qy = ay + aby * v + acy * w; qz = az + abz * v + acz * w; }
                        }
                    }
                }
            }
        }
        double ex = p[0] - qx, ey = p[1] - qy, ez = p[2] - qz;
        return ex * ex + ey * ey + ez * ez;
    }

    /// <summary>3×4 仿射（列優先）的反矩陣；奇異 ⇒ null。</summary>
    static double[]? AffineInverse(double[] A)
    {
        double a = A[0], b = A[1], cc = A[2], d = A[4], e = A[5], f = A[6], g = A[8], h = A[9], i = A[10];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + cc * (d * h - e * g);
        if (Math.Abs(det) < 1e-12) return null;
        double k = 1 / det;
        double[] M = { (e * i - f * h) * k, (cc * h - b * i) * k, (b * f - cc * e) * k, (f * g - d * i) * k, (a * i - cc * g) * k, (cc * d - a * f) * k, (d * h - e * g) * k, (b * g - a * h) * k, (a * e - b * d) * k };
        double tx = A[3], ty = A[7], tz = A[11];
        return new[] { M[0], M[1], M[2], -(M[0] * tx + M[1] * ty + M[2] * tz), M[3], M[4], M[5], -(M[3] * tx + M[4] * ty + M[5] * tz), M[6], M[7], M[8], -(M[6] * tx + M[7] * ty + M[8] * tz) };
    }

    static string Xyz(double[] p) => string.Join(",", p[0].ToString("0.0000", CultureInfo.InvariantCulture), p[1].ToString("0.0000", CultureInfo.InvariantCulture), p[2].ToString("0.0000", CultureInfo.InvariantCulture));

    /// <summary>組裝件座標的一點 ⇒ `model:<相對繩所在那一層的模型路徑>/<模型座標>`；反算一次確認落回同一點（差 &gt; 0.01 格 ⇒ null）。</summary>
    static string? RopeModelRef(SCP_SculptModelSet iSet, string iFrame, string iModel, double[] iP, out string oWhy)
    {
        oWhy = "";
        var m = iSet.Models.Find(q => q.Path == iModel);
        if (m == null || m.ToRoot == null) { oWhy = "模型 `" + iModel + "` 解不到座標 —— 不猜"; return null; }
        string aRel = iFrame.Length == 0 ? iModel : iModel.StartsWith(iFrame + "/", StringComparison.Ordinal) ? iModel.Substring(iFrame.Length + 1) : "";
        if (aRel.Length == 0) { oWhy = "模型 `" + iModel + "` 不在繩所在那一層（`" + iFrame + "`）底下 ⇒ model: 指不到 —— 不猜"; return null; }
        double[]? inv = AffineInverse(m.ToRoot);
        if (inv == null) { oWhy = "模型 `" + iModel + "` 的座標轉換是奇異的 —— 不猜"; return null; }
        double[] q = SCP_SculptModels.Apply(inv, iP);
        if (m.Model.source != SCP_SculptModels.SourceWork)
        {
            double[] X = m.Axes;   // 檔案座標 → 模型座標的 3×3（帶號排列）⇒ 轉置就是反
            q = new[] { X[0] * q[0] + X[3] * q[1] + X[6] * q[2], X[1] * q[0] + X[4] * q[1] + X[7] * q[2], X[2] * q[0] + X[5] * q[1] + X[8] * q[2] };
        }
        string aRef = aRel + "/" + Xyz(q);
        if (iSet.ResolvePoint(iFrame, aRef, out double[]? back, out _, out _) != null || back == null) { oWhy = "改寫成 `model:" + aRef + "` 反算解不了 —— 不猜"; return null; }
        double dd = Math.Sqrt((back[0] - iP[0]) * (back[0] - iP[0]) + (back[1] - iP[1]) * (back[1] - iP[1]) + (back[2] - iP[2]) * (back[2] - iP[2]));
        if (dd > 0.01) { oWhy = "改寫成 `model:" + aRef + "` 反算差了 " + dd.ToString("0.###", CultureInfo.InvariantCulture) + " 格 —— 不猜"; return null; }
        return "model:" + aRef;
    }

    /// <summary>組裝件座標的一點 ⇒ 繩所在那一層的座標字串（一定帶小數點：整數會被當成格心多 +0.5）；反算確認。</summary>
    static string? RopeFrameXyz(string iFrame, double[] iP, Dictionary<string, SCP_SculptLinkNode> iIndex, out string oWhy)
    {
        oWhy = "";
        double[] q = iP;
        if (iFrame.Length > 0)
        {
            double[] A = SCP_SculptModels.AffineOf(p => SCP_SculptRopes.ToRoot(iFrame, p, iIndex));
            double[]? inv = AffineInverse(A);
            if (inv == null) { oWhy = "繩所在那一層的座標轉換是奇異的 —— 不猜"; return null; }
            q = SCP_SculptModels.Apply(inv, iP);
        }
        string s = Xyz(q);
        if (!SCP_SculptRopes.TryParseEndpoint(s, out double[]? aPt, out _, out _) || aPt == null) { oWhy = "座標 `" + s + "` 解不回來 —— 不猜"; return null; }
        double[] back = iFrame.Length > 0 ? SCP_SculptRopes.ToRoot(iFrame, aPt, iIndex) : aPt;
        double dd = Math.Sqrt((back[0] - iP[0]) * (back[0] - iP[0]) + (back[1] - iP[1]) * (back[1] - iP[1]) + (back[2] - iP[2]) * (back[2] - iP[2]));
        if (dd > 0.01) { oWhy = "座標 `" + s + "` 反算差了 " + dd.ToString("0.###", CultureInfo.InvariantCulture) + " 格 —— 不猜"; return null; }
        return s;
    }
}
