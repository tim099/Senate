// 區塊職責：op=section —— 剖面匯出＋參考圖疊圖（TASK-0480）。
// 物理意義：Tim 2026-10-10：「是否可以加一個工具，可以把雕刻剖面圖匯出並跟參考圖疊圖去對比」——
//           照圖紙雕風帆戰艦（TASK-0478）時，「量到的比例對不對」要有一張圖、一組數字可以對。
//   · 視圖：axis ＝ 視線方向（同 slice：y+ ＝ 從 -y 那側往 +y 看）。垂直軸一律朝上（側視、正視是 Z；俯視、仰視是 Y），
//     水平軸是「站在那一側看過去的右手方向」—— ⚠ 跟 slice 不同：slice 的軸對應是為了貼圖往返，側視時 Z 沒有朝上。
//   · 比例：作品書卡的 meters_per_voxel（沒記 ⇒ 1 公尺＝32 格）；網格線落在世界座標的整數公尺上，比例尺標公尺數。
//   · 校準：ref 像素 → 顯示座標（以 voxel 為單位）是一個相似變換（縮放＋旋轉＋平移）；兩種給法——
//       ① ref_points＋work_points：兩個參考圖像素各對應作品平面上哪個座標；
//       ② ref_scale（比例尺上兩點＋實際公尺數，定縮放與旋轉）＋ref_anchor＋work_anchor（定平移）。
//     ref_flip=1 ⇒ 參考圖先左右鏡像（圖紙的船頭方向跟視圖相反時用）。
//   · 探針：probe 的每個參考圖像素 ⇒ 印出它落在作品的哪一格、離最近的 voxel 差幾格 —— 「疊得準不準」的讀數，
//     不是只有一張要靠眼睛看的圖。
// 數值影響：剖面是逐 voxel 投影（保留最靠近視點的那顆）⇒ 成本 ∝ voxel 數，不 ∝ 範圍體積。
//           參考圖整張解碼成 RGBA（14573×4736 的掃描約 276 MB，另加原檔位元組），只在這一次呼叫裡存在。
// 失敗處置：參數錯、參考圖讀不了、校準退化（兩點重合、比例算出 0 或非有限數）、明給的網格太密 ⇒ exit 2，不寫圖。
#nullable enable
using System.Globalization;
using SCP.Core.Canvas;
using SCP.Core.Sculpture;
using StbImageSharp;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    public const string SectionPngName = "sculpture_section.png";
    /// <summary>輸出圖的像素上限（寬×高）。</summary>
    const long SectionMaxPixels = 64L * 1024 * 1024;

    /// <summary>視圖：世界座標 ↔ 顯示座標（以 voxel 為單位，x 往右、y 往下）。</summary>
    sealed class SectionView
    {
        public string Axis = "";       // 解析後的視線方向（y+ …）
        public int N, R, U;            // 法線、右手、朝上 三個軸的索引（0=x 1=y 2=z）
        public int NSign, RSign;       // 視線方向的正負；右手方向是世界軸的正向還是負向
        public int[] Min = new int[3], Max = new int[3];
        public int W => Max[R] - Min[R] + 1;
        public int H => Max[U] - Min[U] + 1;
        static readonly string[] s_Axis = { "X", "Y", "Z" };
        public string RightName => (RSign > 0 ? "+" : "-") + s_Axis[R];
        public string UpName => "+" + s_Axis[U];
        public string RAxis => s_Axis[R];
        public string UAxis => s_Axis[U];

        /// <summary>世界平面座標（右手軸、朝上軸；可以是小數，格的左下角 ＝ 整數）→ 顯示座標。</summary>
        public (double X, double Y) ToDisplay(double a, double b)
            => (RSign > 0 ? a - Min[R] : Max[R] + 1 - a, Max[U] + 1 - b);
        public (double A, double B) ToWorld(double x, double y)
            => (RSign > 0 ? x + Min[R] : Max[R] + 1 - x, Max[U] + 1 - y);
    }

    static bool TryParseSectionView(string iAxis, int[] iRegion, out SectionView oView, out string oBad)
    {
        oView = new SectionView(); oBad = "";
        string a = iAxis.Length == 0 ? "y+" : iAxis.ToLowerInvariant().Replace("+y", "y+").Replace("-y", "y-").Replace("+x", "x+").Replace("-x", "x-").Replace("+z", "z+").Replace("-z", "z-");
        // 視線 d、朝上 up、右手 ＝ d × up（右手系）
        switch (a)
        {
            case "y+": oView.N = 1; oView.NSign = 1;  oView.R = 0; oView.RSign = 1;  oView.U = 2; break;   // (0,1,0)×(0,0,1) ＝ +X
            case "y-": oView.N = 1; oView.NSign = -1; oView.R = 0; oView.RSign = -1; oView.U = 2; break;   // ＝ -X
            case "x+": oView.N = 0; oView.NSign = 1;  oView.R = 1; oView.RSign = -1; oView.U = 2; break;   // (1,0,0)×(0,0,1) ＝ -Y
            case "x-": oView.N = 0; oView.NSign = -1; oView.R = 1; oView.RSign = 1;  oView.U = 2; break;   // ＝ +Y
            case "z-": oView.N = 2; oView.NSign = -1; oView.R = 0; oView.RSign = 1;  oView.U = 1; break;   // 俯視：(0,0,-1)×(0,1,0) ＝ +X
            case "z+": oView.N = 2; oView.NSign = 1;  oView.R = 0; oView.RSign = -1; oView.U = 1; break;   // 仰視：＝ -X
            default: oBad = "axis 要是 x+ x- y+ y- z+ z-（視線方向；y+ ＝ 從 -y 那側往 +y 看）：'" + iAxis + "'"; return false;
        }
        oView.Axis = a;
        for (int k = 0; k < 3; k++)
        {
            oView.Min[k] = iRegion[k * 2]; oView.Max[k] = iRegion[k * 2 + 1];
            // 範圍跨度要在結構上限內：W／H 是 int，跨度到 2^31 會溢位成負數（審查 #9）
            if ((long)oView.Max[k] - oView.Min[k] + 1 > SCP_SculptWorks.MaxAxisHard)
            { oBad = "region 每軸跨度最多 " + SCP_SculptWorks.MaxAxisHard + " 格"; return false; }
        }
        return true;
    }

    static bool TryParseRegion6(string iText, out int[] oRegion)
    {
        oRegion = new int[6];
        string[] parts = iText.Split(',');
        if (parts.Length != 3) return false;
        for (int k = 0; k < 3; k++)
        {
            string[] ab = parts[k].Split(new[] { ".." }, StringSplitOptions.None);
            if (ab.Length != 2 || !int.TryParse(ab[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lo)
                || !int.TryParse(ab[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hi)) return false;
            oRegion[k * 2] = Math.Min(lo, hi); oRegion[k * 2 + 1] = Math.Max(lo, hi);
        }
        return true;
    }

    /// <summary>"x,y;x,y;…" → 點列（小數可）。</summary>
    static bool TryParsePoints(string iText, int iCount, out (double X, double Y)[] oPts)
    {
        var list = new List<(double, double)>();
        foreach (string p in iText.Split(';'))
        {
            if (p.Trim().Length == 0) continue;
            string[] xy = p.Split(',');
            if (xy.Length != 2 || !double.TryParse(xy[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(xy[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                || !double.IsFinite(x) || !double.IsFinite(y)) { oPts = Array.Empty<(double, double)>(); return false; }
            list.Add((x, y));
        }
        oPts = list.ToArray();
        return iCount <= 0 ? oPts.Length > 0 : oPts.Length == iCount;
    }

    /// <summary>複數形式的相似變換 d ＝ A·r ＋ B（A 帶縮放與旋轉）。</summary>
    readonly struct Similarity
    {
        public readonly double Ar, Ai, Br, Bi;
        public Similarity(double ar, double ai, double br, double bi) { Ar = ar; Ai = ai; Br = br; Bi = bi; }
        public (double X, double Y) Apply(double x, double y) => (Ar * x - Ai * y + Br, Ai * x + Ar * y + Bi);
        public (double X, double Y) Invert(double x, double y)
        {
            double dx = x - Br, dy = y - Bi, n = Ar * Ar + Ai * Ai;
            return ((dx * Ar + dy * Ai) / n, (dy * Ar - dx * Ai) / n);
        }
        public double Scale => Math.Sqrt(Ar * Ar + Ai * Ai);
        public double AngleDeg => Math.Atan2(Ai, Ar) * 180 / Math.PI;
        /// <summary>兩對點：r1→d1、r2→d2。</summary>
        public static bool FromPairs((double X, double Y) r1, (double X, double Y) r2, (double X, double Y) d1, (double X, double Y) d2, out Similarity oS)
        {
            double rx = r2.X - r1.X, ry = r2.Y - r1.Y, dx = d2.X - d1.X, dy = d2.Y - d1.Y, n = rx * rx + ry * ry;
            oS = default;
            if (n < 1e-9) return false;
            double ar = (dx * rx + dy * ry) / n, ai = (dy * rx - dx * ry) / n;   // A ＝ (d2-d1)/(r2-r1)
            oS = new Similarity(ar, ai, d1.X - (ar * r1.X - ai * r1.Y), d1.Y - (ai * r1.X + ar * r1.Y));
            return true;
        }
    }

    static string? OpSection(Ctx c)
    {
        // ── 範圍、視圖、比例 ──
        string aRegionText = c.Args.Get("region").Trim();
        int[] aRegion;
        if (aRegionText.Length == 0)
            aRegion = c.Work == null ? new[] { 0, 255, 0, 255, 0, 255 } : new[] { 0, c.Work.SizeX - 1, 0, c.Work.SizeY - 1, 0, c.Work.SizeZ - 1 };
        else if (!TryParseRegion6(aRegionText, out aRegion))
            return Blocked(c, 2, "region 要是 x1..x2,y1..y2,z1..z2：'" + aRegionText + "'");
        if (!TryParseSectionView(c.Args.Get("axis").Trim(), aRegion, out SectionView v, out string aBad)) return Blocked(c, 2, aBad);
        if (!TryOutPath(c, SectionPngName, out string aOut, out string aOutBad)) return Blocked(c, 2, aOutBad);

        double aMpv = c.Work?.MetersPerVoxel ?? SCP_SculptWork.DefaultMetersPerVoxel;
        string aMpvSource = c.Work == null ? "共用展區預設（1 公尺＝32 格）" : c.Work.meters_per_voxel > 0 ? "作品書卡" : "作品沒記 ⇒ 預設 1 公尺＝32 格";
        if (c.Args.IsExplicit("meters_per_voxel"))
        {
            if (!TryPositive(c.Args.Get("meters_per_voxel"), out aMpv) || aMpv > 1000) return Blocked(c, 2, "meters_per_voxel 要是 0–1000 的正數（與 work update 同一條）");
            aMpvSource = "單次指定";
        }

        int W = v.W, H = v.H;
        int aPpv;
        if (c.Args.IsExplicit("px_per_voxel"))
        {
            if (!int.TryParse(c.Args.Get("px_per_voxel"), NumberStyles.Integer, CultureInfo.InvariantCulture, out aPpv) || aPpv < 1 || aPpv > 64)
                return Blocked(c, 2, "px_per_voxel 要是 1–64 的整數");
        }
        else aPpv = Math.Max(1, Math.Min(32, 2400 / Math.Max(W, H)));
        double aGridArg = 0;
        if (c.Args.IsExplicit("grid_m") && !TryPositive(c.Args.Get("grid_m"), out aGridArg))
            return Blocked(c, 2, "grid_m 要是正數（公尺）");
        string aFlipText = c.Args.Get("ref_flip").Trim().ToLowerInvariant();
        bool aFlip;
        if (aFlipText == "1" || aFlipText == "true" || aFlipText == "yes") aFlip = true;
        else if (aFlipText.Length == 0 || aFlipText == "0" || aFlipText == "false" || aFlipText == "no") aFlip = false;
        else return Blocked(c, 2, "ref_flip 要是 1／0（或 true／false）：'" + c.Args.Get("ref_flip") + "'");
        // NaN 過得了 TryParse、也過得了「< 0 或 > 1」⇒ 用「不在 [0,1]」的寫法擋（第二輪審查）
        double aAlphaArg = -1;
        if (c.Args.IsExplicit("voxel_alpha") && (!double.TryParse(c.Args.Get("voxel_alpha"), NumberStyles.Float, CultureInfo.InvariantCulture, out aAlphaArg) || !(aAlphaArg >= 0 && aAlphaArg <= 1)))
            return Blocked(c, 2, "voxel_alpha 要是 0–1");
        // 網格：明給的間距畫不出來（每條 < 4 px）就明說，不安靜地省掉還把 grid_m 印成「有畫」（第二輪審查）
        double aPxPerM = aPpv / aMpv;
        double aGridM = aGridArg > 0 ? aGridArg : NiceStep(Math.Max(W, H) * aMpv / 12);
        bool aGridDrawn = aGridM * aPxPerM >= 4;
        if (aGridArg > 0 && !aGridDrawn)
            return Blocked(c, 2, $"grid_m={aGridArg} 太密：每條只有 {aGridM * aPxPerM:0.##} px（至少 4）—— 加大 px_per_voxel 或 grid_m");
        const int Margin = 48;   // 底部比例尺區
        long aOw = (long)W * aPpv, aOh = (long)H * aPpv + Margin;
        if (aOw * aOh > SectionMaxPixels) return Blocked(c, 2, $"輸出 {aOw}×{aOh} 超過 {SectionMaxPixels:N0} 像素 —— 縮小 region 或 px_per_voxel");

        // ── 剖面：逐 voxel 投影，保留最靠近視點的那顆 ──
        SCP_SculptSpace? aSpace = null;
        string aLoadErr = "";
        if (!TryLocked(c, e =>
            {
                try { aSpace = e.LoadSpace(); }
                catch (Exception ex) { aLoadErr = ex.Message; }
                return SCP_SculptResult.Text(0);
            }, out _, out string? aBlocked)) return aBlocked;
        if (aSpace == null) return Blocked(c, 1, "雕刻狀態讀不了：" + aLoadErr);
        var aColor = new byte[(long)W * H];
        var aDepth = new int[(long)W * H];
        int aHits = 0;
        var p = new int[3];
        foreach (var vx in aSpace.Voxels.Entries())
        {
            if (vx.Color <= 0) continue;
            p[0] = vx.X; p[1] = vx.Y; p[2] = vx.Z;
            bool aIn = true;
            for (int k = 0; k < 3; k++) if (p[k] < v.Min[k] || p[k] > v.Max[k]) { aIn = false; break; }
            if (!aIn) continue;
            int i = v.RSign > 0 ? p[v.R] - v.Min[v.R] : v.Max[v.R] - p[v.R];
            int j = v.Max[v.U] - p[v.U];
            long o = (long)j * W + i;
            int d = (v.NSign > 0 ? p[v.N] - v.Min[v.N] : v.Max[v.N] - p[v.N]) + 1;   // 1 ＝ 最靠近視點
            if (aColor[o] == 0) aHits++;
            if (aColor[o] == 0 || d < aDepth[o]) { aColor[o] = (byte)Math.Min(255, vx.Color); aDepth[o] = d; }
        }

        // ── 參考圖與校準 ──
        string aRef = c.Args.Get("ref").Trim();
        ImageResult? aImg = null;
        Similarity aSim = default;
        double aRefPxPerM = 0;
        string aCalib = "";
        if (aRef.Length > 0)
        {
            if (!Path.IsPathRooted(aRef) || !File.Exists(aRef)) return Blocked(c, 2, "ref 要是存在的絕對路徑：'" + aRef + "'");
            try { aImg = ImageResult.FromMemory(File.ReadAllBytes(aRef), ColorComponents.RedGreenBlueAlpha); }   // 帶 alpha：透明背景合成在白底上（審查 #10）
            catch (Exception e) { return Blocked(c, 2, "參考圖讀不了（PNG／JPEG）：" + e.Message); }
            int aRefW = aImg.Width;
            (double X, double Y) Flip((double X, double Y) r) => aFlip ? (aRefW - r.X, r.Y) : r;
            string aRefPts = c.Args.Get("ref_points").Trim(), aWorkPts = c.Args.Get("work_points").Trim();
            string aScale = c.Args.Get("ref_scale").Trim();
            if (aRefPts.Length > 0 || aWorkPts.Length > 0)
            {
                if (!TryParsePoints(aRefPts, 2, out var r) || !TryParsePoints(aWorkPts, 2, out var w))
                    return Blocked(c, 2, "ref_points 與 work_points 各要兩個點：x,y;x,y（work_points 是作品平面座標：右手軸, 朝上軸，單位格）");
                var d1 = v.ToDisplay(w[0].X, w[0].Y); var d2 = v.ToDisplay(w[1].X, w[1].Y);
                if (!Similarity.FromPairs(Flip(r[0]), Flip(r[1]), d1, d2, out aSim)) return Blocked(c, 2, "ref_points 兩點重合，定不出比例");
                if (!(aSim.Scale > 1e-12) || !double.IsFinite(aSim.Scale)) return Blocked(c, 2, "work_points 兩點重合，定不出比例");
                aCalib = "兩點對兩點";
                aRefPxPerM = 1 / (aSim.Scale * aMpv);
            }
            else if (aScale.Length > 0)
            {
                string[] sp = aScale.Split(';');
                if (sp.Length != 3 || !TryParsePoints(sp[0] + ";" + sp[1], 2, out var sr) || !TryPositive(sp[2], out double aMeters))
                    return Blocked(c, 2, "ref_scale 要是 x1,y1;x2,y2;公尺數（比例尺上兩點與它們之間的實際長度）");
                if (!TryParsePoints(c.Args.Get("ref_anchor").Trim(), 1, out var ra) || !TryParsePoints(c.Args.Get("work_anchor").Trim(), 1, out var wa))
                    return Blocked(c, 2, "ref_scale 要搭配 ref_anchor=x,y（參考圖像素）與 work_anchor=a,b（作品平面座標，單位格）");
                var s1 = Flip(sr[0]); var s2 = Flip(sr[1]);
                double aPx = Math.Sqrt((s2.X - s1.X) * (s2.X - s1.X) + (s2.Y - s1.Y) * (s2.Y - s1.Y));
                if (aPx < 1e-6) return Blocked(c, 2, "ref_scale 兩點重合，定不出比例");
                aRefPxPerM = aPx / aMeters;
                double aK = 1 / (aRefPxPerM * aMpv);                         // 顯示單位（格）／參考圖像素
                // 把比例尺轉成水平；兩端順序不拘（審查 #5）：角度收進 (-90°, 90°]，不讓「從右往左給」把整張圖轉 180°
                double aAng = Math.Atan2(s2.Y - s1.Y, s2.X - s1.X);
                if (aAng > Math.PI / 2) aAng -= Math.PI; else if (aAng <= -Math.PI / 2) aAng += Math.PI;
                double aTheta = -aAng;
                double ar = aK * Math.Cos(aTheta), ai = aK * Math.Sin(aTheta);
                var ra0 = Flip(ra[0]); var da = v.ToDisplay(wa[0].X, wa[0].Y);
                aSim = new Similarity(ar, ai, da.X - (ar * ra0.X - ai * ra0.Y), da.Y - (ai * ra0.X + ar * ra0.Y));
                aCalib = $"比例尺 {aMeters} 公尺＋錨點";
            }
            else return Blocked(c, 2, "有 ref 就要校準：ref_points＋work_points，或 ref_scale＋ref_anchor＋work_anchor");
            // 兩種校準共用的退化檢查（第二輪審查：原本只有兩點對兩點那條有）—— 比例 0 ⇒ Invert 除以 0、探針全塌在錨點上
            if (!(aSim.Scale > 1e-12) || !double.IsFinite(aSim.Scale) || !double.IsFinite(aSim.Br) || !double.IsFinite(aSim.Bi) || !double.IsFinite(aRefPxPerM))
                return Blocked(c, 2, "校準算出的比例退化或不是有限數（" + aCalib + "：每參考圖像素 " + aSim.Scale.ToString("G4", CultureInfo.InvariantCulture) + " 格）—— 檢查兩點與公尺數");
        }

        // ── 合成 ──
        int OW = (int)aOw, OH = (int)aOh;
        var rgba = new byte[(long)OW * OH * 4];
        for (long k = 0; k < rgba.Length; k += 4) { rgba[k] = 255; rgba[k + 1] = 255; rgba[k + 2] = 255; rgba[k + 3] = 255; }
        if (aImg != null)
        {
            // 每個輸出像素在參考圖上的足跡可能是好幾個像素（縮小）⇒ 足跡內 n×n 取樣平均，細線才不會消失
            double aFoot = 1 / (aSim.Scale * aPpv);
            int aSub = Math.Max(1, Math.Min(6, (int)Math.Ceiling(aFoot)));
            int rw = aImg.Width, rh = aImg.Height;
            byte[] src = aImg.Data;
            Parallel.For(0, OH - Margin, y =>
            {
                for (int x = 0; x < OW; x++)
                {
                    int sr = 0, sg = 0, sb = 0, n = 0;
                    for (int sy = 0; sy < aSub; sy++)
                        for (int sx = 0; sx < aSub; sx++)
                        {
                            double dx = (x + (sx + 0.5) / aSub) / aPpv, dy = (y + (sy + 0.5) / aSub) / aPpv;
                            var r = aSim.Invert(dx, dy);
                            double rx = aFlip ? rw - r.X : r.X, ry = r.Y;
                            int ix = (int)Math.Floor(rx), iy = (int)Math.Floor(ry);
                            if (ix < 0 || iy < 0 || ix >= rw || iy >= rh) continue;
                            long so = ((long)iy * rw + ix) * 4;
                            int sa = src[so + 3];
                            sr += (src[so] * sa + 255 * (255 - sa)) / 255;
                            sg += (src[so + 1] * sa + 255 * (255 - sa)) / 255;
                            sb += (src[so + 2] * sa + 255 * (255 - sa)) / 255;
                            n++;
                        }
                    if (n == 0) continue;
                    long o = ((long)y * OW + x) * 4;
                    rgba[o] = (byte)(sr / n); rgba[o + 1] = (byte)(sg / n); rgba[o + 2] = (byte)(sb / n);
                }
            });
        }
        double aAlpha = aAlphaArg >= 0 ? aAlphaArg : aImg != null ? 0.55 : 1.0;
        for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++)
            {
                byte ci = aColor[(long)j * W + i];
                if (ci == 0) continue;
                SCP_CanvasPalette.IndexToRgb(ci, out byte cr, out byte cg, out byte cb);
                for (int yy = 0; yy < aPpv; yy++)
                    for (int xx = 0; xx < aPpv; xx++)
                    {
                        long o = ((long)(j * aPpv + yy) * OW + i * aPpv + xx) * 4;
                        rgba[o] = (byte)(rgba[o] * (1 - aAlpha) + cr * aAlpha);
                        rgba[o + 1] = (byte)(rgba[o + 1] * (1 - aAlpha) + cg * aAlpha);
                        rgba[o + 2] = (byte)(rgba[o + 2] * (1 - aAlpha) + cb * aAlpha);
                    }
            }
        // 有參考圖時描 voxel 輪廓（深紅），疊在圖紙線上才看得出誰偏
        if (aImg != null && aPpv >= 2)
            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                {
                    bool aOcc = aColor[(long)j * W + i] != 0;
                    if (!aOcc) continue;
                    bool L = i > 0 && aColor[(long)j * W + i - 1] != 0, Rr = i < W - 1 && aColor[(long)j * W + i + 1] != 0;
                    bool T = j > 0 && aColor[(long)(j - 1) * W + i] != 0, Bt = j < H - 1 && aColor[(long)(j + 1) * W + i] != 0;
                    for (int t = 0; t < aPpv; t++)
                    {
                        if (!L) Put(rgba, OW, i * aPpv, j * aPpv + t, 170, 0, 0);
                        if (!Rr) Put(rgba, OW, i * aPpv + aPpv - 1, j * aPpv + t, 170, 0, 0);
                        if (!T) Put(rgba, OW, i * aPpv + t, j * aPpv, 170, 0, 0);
                        if (!Bt) Put(rgba, OW, i * aPpv + t, j * aPpv + aPpv - 1, 170, 0, 0);
                    }
                }

        // ── 網格（世界座標的整數公尺）與比例尺 ──
        if (aGridDrawn)
        {
            void GridLine(bool iVertical, double iDisplay, bool iMajor)
            {
                int pos = (int)Math.Round(iDisplay * aPpv);
                int len = iVertical ? OH - Margin : OW;
                for (int t = 0; t < len; t++)
                    if (iVertical) Blend(rgba, OW, pos, t, 40, 90, 200, iMajor ? 0.55 : 0.25);
                    else Blend(rgba, OW, t, pos, 40, 90, 200, iMajor ? 0.55 : 0.25);
            }
            double aLoA = v.Min[v.R] * aMpv, aHiA = (v.Max[v.R] + 1) * aMpv;
            for (long m = (long)Math.Ceiling(aLoA / aGridM); m * aGridM <= aHiA; m++)
            {
                var d = v.ToDisplay(m * aGridM / aMpv, v.Min[v.U]);
                if (d.X >= 0 && d.X * aPpv < OW) GridLine(true, d.X, m % 10 == 0);
            }
            double aLoB = v.Min[v.U] * aMpv, aHiB = (v.Max[v.U] + 1) * aMpv;
            for (long m = (long)Math.Ceiling(aLoB / aGridM); m * aGridM <= aHiB; m++)
            {
                var d = v.ToDisplay(v.Min[v.R], m * aGridM / aMpv);
                if (d.Y >= 0 && d.Y * aPpv < OH - Margin) GridLine(false, d.Y, m % 10 == 0);
            }
        }
        double aBarM = NiceStep(OW / aPxPerM / 5);
        int aBarPx = (int)Math.Round(aBarM * aPxPerM);
        int by0 = OH - Margin + 14;
        for (int x = 0; x < aBarPx && 12 + x < OW; x++)   // 五段黑灰交錯
        {
            byte aShade = (x * 5 / Math.Max(1, aBarPx)) % 2 == 0 ? (byte)0 : (byte)170;
            for (int t = 0; t < 6; t++) Put(rgba, OW, 12 + x, by0 + t, aShade, aShade, aShade);
        }
        for (int t = -4; t < 10; t++) { Put(rgba, OW, 12, by0 + t, 0, 0, 0); Put(rgba, OW, Math.Min(OW - 1, 12 + aBarPx), by0 + t, 0, 0, 0); }
        DrawText(rgba, OW, OH, Math.Min(OW - 60, 18 + aBarPx), by0 - 2, FormatM(aBarM) + "m", 3);

        // ── 探針 ──
        var aProbeLines = new List<string>();
        string aProbe = c.Args.Get("probe").Trim();
        if (aProbe.Length > 0)
        {
            if (aImg == null) return Blocked(c, 2, "probe 要搭配 ref（探針的座標是參考圖像素）");
            if (!TryParsePoints(aProbe, 0, out var pts)) return Blocked(c, 2, "probe 要是 x,y;x,y;…（參考圖像素）");
            int rw = aImg.Width;
            for (int k = 0; k < pts.Length; k++)
            {
                var r = aFlip ? (rw - pts[k].X, pts[k].Y) : pts[k];
                var d = aSim.Apply(r.Item1, r.Item2);
                var wpt = v.ToWorld(d.X, d.Y);
                int ci = (int)Math.Floor(d.X), cj = (int)Math.Floor(d.Y);
                bool aFound = NearestOccupied(aColor, W, H, ci, cj, ProbeSearchCells, out int di, out int dj);
                // 顯示座標的偏差換回**世界軸的正向**（右手軸是 -X 的視圖要反號），標籤只寫軸名不帶號 —— 不讓兩個負號疊在一起（審查 #4）
                int da = v.RSign > 0 ? di : -di, db = -dj;
                string line = $"probe{k + 1} 參考圖 ({pts[k].X:0.#},{pts[k].Y:0.#}) → 作品 {v.RAxis}={wpt.A:0.0}、{v.UAxis}={wpt.B:0.0} 格 ＝ ({wpt.A * aMpv:0.000},{wpt.B * aMpv:0.000}) m；"
                              + (!aFound ? $"{ProbeSearchCells} 格內沒有 voxel（偏差未知）"
                                 : di == 0 && dj == 0 ? "那一格有 voxel（偏差 0）"
                                 : $"最近的 voxel 在 {v.RAxis} {da:+#;-#;0} 格、{v.UAxis} {db:+#;-#;0} 格（{Math.Max(Math.Abs(da), Math.Abs(db))} 格）");
                aProbeLines.Add(line);
                c.Result.AddValue("probe" + (k + 1), line);
                // ⚠ 找不到 ≠ 0：印 none（審查 #2）—— 「校準歪到 256 格外」與「完全對準」不能同形
                c.Result.AddValue("probe" + (k + 1) + "_offset_cells", aFound ? Math.Max(Math.Abs(da), Math.Abs(db)).ToString(CultureInfo.InvariantCulture) : "none");
                // 綠色十字
                int px = (int)Math.Round(d.X * aPpv), py = (int)Math.Round(d.Y * aPpv);
                for (int t = -8; t <= 8; t++) { Put(rgba, OW, px + t, py, 0, 170, 0); Put(rgba, OW, px, py + t, 0, 170, 0); }
            }
        }

        string? aDir = Path.GetDirectoryName(aOut);
        if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
        File.WriteAllBytes(aOut, SCP_CanvasPng.EncodeRgbaRows(rgba, OW, OH));
        string aSha = SCP.Core.Sculpture.SCP_SculptPy.Sha256Hex(File.ReadAllBytes(aOut));

        c.Result.AddValue("path", aOut);
        c.Result.AddValue("sha256", aSha);
        c.Result.AddValue("width", OW.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("height", OH.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("px_per_voxel", aPpv.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("meters_per_voxel", aMpv.ToString("0.######", CultureInfo.InvariantCulture));
        c.Result.AddValue("meters_per_voxel_source", aMpvSource);
        c.Result.AddValue("view", $"視線 {v.Axis}｜右 {v.RightName}｜上 {v.UpName}");
        c.Result.AddValue("section_cells", aHits.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("grid_m", aGridDrawn ? aGridM.ToString("0.###", CultureInfo.InvariantCulture) : "none");   // 自動間距太密 ⇒ 沒畫就印 none
        c.Result.AddValue("scale_bar_m", aBarM.ToString("0.###", CultureInfo.InvariantCulture));
        if (aImg != null)
        {
            c.Result.AddValue("ref_px_per_m", aRefPxPerM.ToString("0.###", CultureInfo.InvariantCulture));
            c.Result.AddValue("ref_rotation_deg", aSim.AngleDeg.ToString("0.###", CultureInfo.InvariantCulture));
            c.Result.AddValue("calibration", aCalib + (aFlip ? "（參考圖左右鏡像）" : ""));
        }
        c.Result.AddOutput(aOut);
        c.Report.Append("## section\n")
            .Append($"- 範圍 {v.Min[0]}..{v.Max[0]},{v.Min[1]}..{v.Max[1]},{v.Min[2]}..{v.Max[2]}｜視線 {v.Axis}（右 {v.RightName}、上 {v.UpName}）｜剖面有 voxel 的格 {aHits:N0}\n")
            .Append($"- 比例 {aMpv:0.######} m／格（{aMpvSource}）｜每格 {aPpv} px ⇒ {aPxPerM:0.##} px／m｜{(aGridDrawn ? $"網格 {aGridM:0.###} m（每 10 條加深）" : $"網格太密沒畫（{aGridM:0.###} m 只有 {aGridM * aPxPerM:0.##} px）")}｜比例尺 {FormatM(aBarM)} m\n");
        if (aImg != null)
            c.Report.Append($"- 參考圖 `{aRef}`（{aImg.Width}×{aImg.Height}）｜校準：{aCalib}{(aFlip ? "（左右鏡像）" : "")}｜{aRefPxPerM:0.###} px／m｜旋轉 {aSim.AngleDeg:0.###}°\n");
        foreach (string l in aProbeLines) c.Report.Append("- ").Append(l).Append('\n');
        return Finish(c, 0, $"✓ section {OW}×{OH} → {aOut}");
    }

    static bool TryPositive(string iText, out double oVal)
        => double.TryParse(iText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out oVal) && oVal > 0 && !double.IsInfinity(oVal);

    /// <summary>1、2、5 × 10^n 裡不小於目標的最小值。</summary>
    static double NiceStep(double iTarget)
    {
        if (iTarget <= 0) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(iTarget)));
        foreach (double m in new[] { 1.0, 2.0, 5.0, 10.0 }) if (m * p >= iTarget) return m * p;
        return 10 * p;
    }

    static string FormatM(double iM) => iM.ToString(iM >= 1 ? "0.##" : "0.###", CultureInfo.InvariantCulture);

    /// <summary>探針往外找 voxel 的半徑（格）。</summary>
    const int ProbeSearchCells = 256;

    /// <summary>從 (i,j) 往外一圈一圈找最近的有 voxel 的格（Chebyshev 距離）；找不到回 false（⛔ 不回偏差 0）。</summary>
    static bool NearestOccupied(byte[] iColor, int W, int H, int i, int j, int iMaxR, out int oDi, out int oDj)
    {
        oDi = oDj = 0;
        for (int r = 0; r <= iMaxR; r++)
        {
            int best = int.MaxValue;
            for (int dj = -r; dj <= r; dj++)
                for (int di = -r; di <= r; di++)
                {
                    if (Math.Max(Math.Abs(di), Math.Abs(dj)) != r) continue;
                    int x = i + di, y = j + dj;
                    if (x < 0 || y < 0 || x >= W || y >= H || iColor[(long)y * W + x] == 0) continue;
                    int e = di * di + dj * dj;
                    if (e < best) { best = e; oDi = di; oDj = dj; }
                }
            if (best != int.MaxValue) return true;
        }
        oDi = oDj = 0;
        return false;
    }

    static void Put(byte[] iRgba, int W, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= W) return;
        long o = ((long)y * W + x) * 4;
        if (o + 3 >= iRgba.Length) return;
        iRgba[o] = r; iRgba[o + 1] = g; iRgba[o + 2] = b; iRgba[o + 3] = 255;
    }

    static void Blend(byte[] iRgba, int W, int x, int y, byte r, byte g, byte b, double a)
    {
        if (x < 0 || y < 0 || x >= W) return;
        long o = ((long)y * W + x) * 4;
        if (o + 3 >= iRgba.Length) return;
        iRgba[o] = (byte)(iRgba[o] * (1 - a) + r * a);
        iRgba[o + 1] = (byte)(iRgba[o + 1] * (1 - a) + g * a);
        iRgba[o + 2] = (byte)(iRgba[o + 2] * (1 - a) + b * a);
    }

    // 3×5 點陣字：比例尺的數字（沒有字型檔可用 ⇒ 自帶最小的那幾個字）
    static readonly Dictionary<char, string> s_Glyph = new()
    {
        ['0'] = "111101101101111", ['1'] = "010110010010111", ['2'] = "111001111100111", ['3'] = "111001111001111",
        ['4'] = "101101111001001", ['5'] = "111100111001111", ['6'] = "111100111101111", ['7'] = "111001001001001",
        ['8'] = "111101111101111", ['9'] = "111101111001111", ['.'] = "000000000000010", ['m'] = "000000110111101",
    };

    static void DrawText(byte[] iRgba, int W, int H, int x0, int y0, string iText, int iScale)
    {
        int x = x0;
        foreach (char ch in iText)
        {
            if (s_Glyph.TryGetValue(ch, out string? g))
                for (int r = 0; r < 5; r++)
                    for (int col = 0; col < 3; col++)
                        if (g[r * 3 + col] == '1')
                            for (int sy = 0; sy < iScale; sy++)
                                for (int sx = 0; sx < iScale; sx++)
                                    if (y0 + r * iScale + sy < H) Put(iRgba, W, x + col * iScale + sx, y0 + r * iScale + sy, 0, 0, 0);
            x += 4 * iScale;
        }
    }
}
