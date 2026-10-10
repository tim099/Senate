// 區塊職責：TASK-0480 的淨室驗收 —— 剖面匯出（op=section）＋參考圖疊圖校準＋探針讀數。
// 物理意義：拿一張**比例已知**的合成參考圖（100 px／m，一條 10 公尺的龍骨、一條 10 公尺的比例尺、一塊黑色標記方塊）去疊一件 0.1 m／格的作品：
//          ① 兩種校準都要量出 100 px／m；② 錨點對 ⇒ 探針偏差 0；錨點故意偏 5 格 ⇒ 探針要報 5（反向對照：量得出偏差的尺）；
//          ③ 鏡像的參考圖：帶 ref_flip=1 對得上、不帶就對不上 —— 用**輸出圖的像素**驗（標記方塊有沒有畫在該畫的格子上），
//             不只看探針（探針只走 Apply，畫圖走 Invert 與取樣端的鏡像；審查指出前一版的反向對照是因為別的原因才過）；
//          ④ 視圖左右：y+ 與 y- 互為鏡像；鏡像視圖裡探針的方向字照世界軸的正向寫（審查：兩個負號疊在一起）；
//             垂直方向的偏差號、x±／z± 四個視圖的左右上下也各量一次（第二輪審查）；
//          ⑤ 作品比例存在書卡上、壞值擋下不改書卡；⑥ 壞參數 exit 2 不寫圖（兩點重合、grid_m、ref_flip、NaN、比例下溢、網格太密）；
//          ⑦ 探針 256 格內找不到 voxel ⇒ none（⛔ 不是 0）；⑧ 比例尺兩端反著給結果一樣；⑨ 透明背景合成在白底上。
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;
using StbImageSharp;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureSectionCleanRoom()
    {
        const string name = "雕刻剖面疊圖：比例存書卡／剖面投影格數／兩種校準 100 px／m／錨點偏 5 格探針報 5／ref_flip 鏡像（像素驗）／左右與方向字／找不到報 none／比例尺順序／透明背景／壞參數擋下（淨室，TASK-0480）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            var gate = new SculptProbeGateway { Vouchers = 100 };
            SCP_CanvasGatewayHost.Factory = _ => gate;
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            Check(room.Run(("op", "work"), ("sub", "create"), ("id", "keel"), ("title", "龍骨"), ("persona", "p"), ("account", "acct"), ("size", "120,10,10")).ExitCode == 0, "建立作品");

            // ⑤ 比例存書卡；壞值擋下
            SCP_CmdResult Mpv(string v) => room.Run(("op", "work"), ("sub", "update"), ("work", "keel"), ("persona", "p"), ("meters_per_voxel", v));
            Check(Mpv("0").ExitCode == 2 && Mpv("abc").ExitCode == 2 && Mpv("-1").ExitCode == 2 && store.Load("keel").meters_per_voxel == 0, "壞比例擋下、書卡不動");
            Check(Mpv("0.1").ExitCode == 0 && Math.Abs(store.Load("keel").MetersPerVoxel - 0.1) < 1e-12, "比例 0.1 存進書卡");

            // 龍骨：x 0..99、y 0..4、z 0..5（10 公尺 × 0.5 × 0.6）；x=0 上方一顆標記（判左右）
            Check(room.Run(("op", "box"), ("persona", "p"), ("work", "keel"), ("x1", "0"), ("x2", "99"), ("y1", "0"), ("y2", "4"), ("z1", "0"), ("z2", "5"), ("color", "19")).ExitCode == 0, "鋪龍骨");
            Check(room.Run(("op", "box"), ("persona", "p"), ("work", "keel"), ("x1", "0"), ("x2", "0"), ("y1", "0"), ("y2", "0"), ("z1", "6"), ("z2", "6"), ("color", "224")).ExitCode == 0, "放標記");

            // 剖面（不帶參考圖）：側視 y+ ⇒ 100×6 ＋ 1 顆標記 ＝ 601 格
            string plain = room.Root + "/plain.png";
            var sec = room.Run(("op", "section"), ("work", "keel"), ("axis", "y+"), ("region", "0..119,0..9,0..9"), ("px_per_voxel", "4"), ("out", plain));
            Check(sec.ExitCode == 0 && File.Exists(plain) && SculptRoom.V(sec, "section_cells") == "601" && SculptRoom.V(sec, "meters_per_voxel") == "0.1", "側視剖面 601 格、比例 0.1（實得 " + SculptRoom.V(sec, "section_cells") + "）");
            Check(SculptRoom.V(sec, "width") == "480" && SculptRoom.V(sec, "height") == (10 * 4 + 48).ToString(), "輸出尺寸 ＝ 格數 × 每格像素（＋比例尺區）");
            var defAxis = room.Run(("op", "section"), ("work", "keel"), ("region", "0..119,0..9,0..9"), ("out", room.Root + "/def.png"));
            Check(SculptRoom.V(defAxis, "view").StartsWith("視線 y+", StringComparison.Ordinal), "沒給 axis ⇒ view 印出解析後的 y+（實得 " + SculptRoom.V(defAxis, "view") + "）");

            ImageResult Png(string f) => ImageResult.FromMemory(File.ReadAllBytes(f), ColorComponents.RedGreenBlueAlpha);
            // ④ 左右：y+ 的標記在左半、y- 在右半
            int MarkerSide(string axis)
            {
                string f = room.Root + "/side_" + axis + ".png";
                room.Run(("op", "section"), ("work", "keel"), ("axis", axis), ("region", "0..119,0..9,0..9"), ("px_per_voxel", "4"), ("out", f));
                var img = Png(f);
                SCP_CanvasPalette.IndexToRgb(224, out byte mr, out byte mg, out byte mb);
                for (int y = 0; y < img.Height; y++)
                    for (int x = 0; x < img.Width; x++)
                    {
                        int o = (y * img.Width + x) * 4;
                        if (img.Data[o] == mr && img.Data[o + 1] == mg && img.Data[o + 2] == mb) return x < img.Width / 2 ? -1 : 1;
                    }
                return 0;
            }
            int aPlus = MarkerSide("y+"), aMinus = MarkerSide("y-");
            Check(aPlus == -1 && aMinus == 1, $"y+ 的 x=0 在左、y- 在右（實得 {aPlus} / {aMinus}）");

            // 合成參考圖：100 px／m ⇒ 一格 10 px。龍骨 x 100..1100、y 240..300；比例尺 (100,350)–(1100,350) ＝ 10 m；
            // 標記方塊 x 1200..1219、y 220..239 ⇒ 作品 X 110..111、Z 6..7（龍骨外、region 內）
            const int RW = 1400, RH = 400;
            string MakeRef(string file, bool mirror, bool transparentBg = false)
            {
                var px = new byte[RW * RH * 4];
                for (int k = 0; k < px.Length; k += 4)
                {
                    if (transparentBg) { px[k] = px[k + 1] = px[k + 2] = 0; px[k + 3] = 0; }
                    else { px[k] = px[k + 1] = px[k + 2] = 255; px[k + 3] = 255; }
                }
                void Dot(int x, int y) { if (mirror) x = RW - 1 - x; int o = (y * RW + x) * 4; px[o] = px[o + 1] = px[o + 2] = 0; px[o + 3] = 255; }
                for (int x = 100; x <= 1100; x++) { Dot(x, 240); Dot(x, 300); Dot(x, 350); }
                for (int y = 240; y <= 300; y++) { Dot(100, y); Dot(1100, y); }
                for (int x = 1200; x < 1220; x++) for (int y = 220; y < 240; y++) Dot(x, y);
                string path = room.Root + "/" + file;
                File.WriteAllBytes(path, SCP_CanvasPng.EncodeRgbaRows(px, RW, RH));
                return path;
            }
            string refPng = MakeRef("ref.png", false);
            SCP_CmdResult Overlay(string anchorWork, string refPath, string scale, string anchorRef, string probe, string axis = "y+", string? flip = null, string outName = "ov.png")
            {
                var a = new List<(string, string)> { ("op", "section"), ("work", "keel"), ("axis", axis), ("region", "0..119,0..9,0..9"), ("px_per_voxel", "4"),
                    ("out", room.Root + "/" + outName), ("ref", refPath), ("ref_scale", scale), ("ref_anchor", anchorRef), ("work_anchor", anchorWork), ("probe", probe) };
                if (flip != null) a.Add(("ref_flip", flip));
                return room.Run(a.ToArray());
            }
            // 標記方塊的中心格 (X 111, Z 7)：y+ 視圖 ⇒ 顯示 (111, 2)，每格 4 px ⇒ 像素 (446, 10)
            bool MarkerDark(string outName)
            {
                var img = Png(room.Root + "/" + outName);
                int o = (10 * img.Width + 446) * 4;
                return img.Data[o] < 80 && img.Data[o + 1] < 80 && img.Data[o + 2] < 80;
            }
            // ① 校準②：比例尺＋錨點
            var ok = Overlay("0,0", refPng, "100,350;1100,350;10", "100,300", "1095,295;105,295");
            Check(ok.ExitCode == 0 && SculptRoom.V(ok, "ref_px_per_m") == "100", "比例尺校準 ⇒ 100 px／m（實得 " + SculptRoom.V(ok, "ref_px_per_m") + "）");
            Check(SculptRoom.V(ok, "probe1_offset_cells") == "0" && SculptRoom.V(ok, "probe2_offset_cells") == "0", "錨點正確 ⇒ 龍骨兩端探針偏差 0（實得 " + SculptRoom.V(ok, "probe1_offset_cells") + "/" + SculptRoom.V(ok, "probe2_offset_cells") + "）");
            Check(MarkerDark("ov.png"), "疊圖像素：參考圖的標記方塊畫在作品 X 111、Z 7 那一格（Invert＋取樣）");
            readings.Add("校準② " + SculptRoom.V(ok, "ref_px_per_m") + " px／m；" + SculptRoom.V(ok, "probe1"));
            // ⑧ 比例尺兩端反著給 ⇒ 一樣（不轉 180°）
            var rev = Overlay("0,0", refPng, "1100,350;100,350;10", "100,300", "1095,295", outName: "ov_rev.png");
            Check(rev.ExitCode == 0 && SculptRoom.V(rev, "probe1_offset_cells") == "0" && Math.Abs(double.Parse(SculptRoom.V(rev, "ref_rotation_deg"), System.Globalization.CultureInfo.InvariantCulture)) < 0.01 && MarkerDark("ov_rev.png"),
                "比例尺兩端反著給 ⇒ 旋轉 0°、偏差 0、標記仍在原位（實得 " + SculptRoom.V(rev, "ref_rotation_deg") + "°）");
            // ② 反向對照：錨點故意偏 5 格 ⇒ 右端探針報 5，方向字是 X -5
            var off = Overlay("5,0", refPng, "100,350;1100,350;10", "100,300", "1095,295");
            Check(off.ExitCode == 0 && SculptRoom.V(off, "probe1_offset_cells") == "5" && SculptRoom.V(off, "probe1").Contains("X -5 格"), "錨點偏 5 格 ⇒ 探針報 5、方向 X -5（實得 " + SculptRoom.V(off, "probe1") + "）");
            readings.Add("錨點偏 5 格：" + SculptRoom.V(off, "probe1"));
            // ⑦ 錨點偏到 400 格外 ⇒ 找不到 ⇒ none（⛔ 不是 0）
            var far = Overlay("400,0", refPng, "100,350;1100,350;10", "100,300", "1095,295");
            Check(far.ExitCode == 0 && SculptRoom.V(far, "probe1_offset_cells") == "none", "256 格內沒有 voxel ⇒ none（實得 " + SculptRoom.V(far, "probe1_offset_cells") + "）");
            // ① 校準①：兩點對兩點
            var pts = room.Run(("op", "section"), ("work", "keel"), ("axis", "y+"), ("region", "0..119,0..9,0..9"), ("px_per_voxel", "4"), ("out", room.Root + "/ov2.png"),
                ("ref", refPng), ("ref_points", "100,300;1100,240"), ("work_points", "0,0;100,6"), ("probe", "1095,295"));
            Check(pts.ExitCode == 0 && SculptRoom.V(pts, "ref_px_per_m") == "100" && SculptRoom.V(pts, "probe1_offset_cells") == "0", "兩點校準 ⇒ 100 px／m、偏差 0（實得 " + SculptRoom.V(pts, "ref_px_per_m") + "）");
            // ③ 鏡像參考圖：座標照鏡像圖自己的像素給（比例尺從左往右）
            string mir = MakeRef("ref_mirror.png", true);
            var flipOk = Overlay("0,0", mir, "299,350;1299,350;10", "1299,300", "304,295", flip: "1", outName: "ov_flip.png");
            var flipNo = Overlay("0,0", mir, "299,350;1299,350;10", "1299,300", "304,295", outName: "ov_noflip.png");
            Check(flipOk.ExitCode == 0 && SculptRoom.V(flipOk, "probe1_offset_cells") == "0" && MarkerDark("ov_flip.png"), "鏡像圖帶 ref_flip ⇒ 偏差 0、標記畫在原位（實得 " + SculptRoom.V(flipOk, "probe1_offset_cells") + "）");
            Check(flipNo.ExitCode == 0 && SculptRoom.V(flipNo, "probe1_offset_cells") != "0" && !MarkerDark("ov_noflip.png"), "鏡像圖不帶 ref_flip ⇒ 探針對不上、標記不在原位（實得 " + SculptRoom.V(flipNo, "probe1_offset_cells") + "）");
            // ④ 鏡像視圖（y-）的方向字：同樣把錨點偏 +5 ⇒ 最近的 voxel 在世界 X -5（不因為畫面左右相反而變號）
            var mOff = Overlay("5,0", refPng, "100,350;1100,350;10", "100,300", "1095,295", axis: "y-", flip: "1", outName: "ov_m.png");
            Check(mOff.ExitCode == 0 && SculptRoom.V(mOff, "probe1_offset_cells") == "5" && SculptRoom.V(mOff, "probe1").Contains("X -5 格"), "y- 視圖錨點偏 5 ⇒ 方向仍寫 X -5（實得 " + SculptRoom.V(mOff, "probe1") + "）");
            // ④b 垂直方向字（第二輪審查：前面的探針偏差全在水平方向，db 的號從沒被量過）：
            //     參考圖 (605,205) ⇒ 作品 X 50.5、Z 9.5 ⇒ 落在 (50, 9) 那格，龍骨頂是 Z 5 ⇒ 最近的 voxel 在 Z -4
            var up = Overlay("0,0", refPng, "100,350;1100,350;10", "100,300", "605,205", outName: "ov_up.png");
            Check(up.ExitCode == 0 && SculptRoom.V(up, "probe1_offset_cells") == "4" && SculptRoom.V(up, "probe1").Contains("Z -4 格"), "龍骨上方 4 格的探針 ⇒ 報 4、方向 Z -4（實得 " + SculptRoom.V(up, "probe1") + "）");
            // ④c 其餘四個視圖的左右上下（第二輪審查：x±／z± 的對應表沒人驗）—— 每格 4 px，取格中心像素
            //     標記 (0,0,6)：x- 視圖右手是 +Y ⇒ 第 0 欄；x+ 右手是 -Y ⇒ 第 9 欄；兩者 Z 6 ⇒ 第 3 列
            //     z- 俯視右手 +X、上 +Y ⇒ 標記在 (0, 9)；龍骨 y 0..4 佔下半（第 5..9 列）；z+ 仰視右手 -X ⇒ 龍骨 x=0 在第 119 欄、x=119 那欄是空的
            bool Cell(string axis, int i, int j, byte color)
            {
                string f = room.Root + "/cell_" + axis + ".png";
                if (!File.Exists(f)) room.Run(("op", "section"), ("work", "keel"), ("axis", axis), ("region", "0..119,0..9,0..9"), ("px_per_voxel", "4"), ("out", f));
                var img = Png(f);
                SCP_CanvasPalette.IndexToRgb(color, out byte cr, out byte cg, out byte cb);
                int o = ((j * 4 + 2) * img.Width + i * 4 + 2) * 4;
                return img.Data[o] == cr && img.Data[o + 1] == cg && img.Data[o + 2] == cb;
            }
            Check(Cell("x-", 0, 3, 224) && !Cell("x-", 9, 3, 224), "x- 視圖：標記在左（右手 +Y）");
            Check(Cell("x+", 9, 3, 224) && !Cell("x+", 0, 3, 224), "x+ 視圖：標記在右（右手 -Y）");
            Check(Cell("z-", 0, 9, 224) && Cell("z-", 50, 7, 19) && !Cell("z-", 50, 2, 19), "z- 俯視：標記在左下、龍骨佔下半（上 ＝ +Y）");
            Check(Cell("z+", 119, 9, 19) && !Cell("z+", 0, 9, 19), "z+ 仰視：龍骨 x=0 在最右（右手 -X）");
            // ⑨ 透明背景：背景格（X 115、Z 9 ⇒ 像素 (462, 6)）要是白的，不是透明像素的 (0,0,0)
            string clear = MakeRef("ref_clear.png", false, transparentBg: true);
            var tr = Overlay("0,0", clear, "100,350;1100,350;10", "100,300", "1095,295", outName: "ov_clear.png");
            var trImg = Png(room.Root + "/ov_clear.png");
            int to = (6 * trImg.Width + 462) * 4;
            Check(tr.ExitCode == 0 && trImg.Data[to] > 200 && trImg.Data[to + 1] > 200 && trImg.Data[to + 2] > 200 && MarkerDark("ov_clear.png"), "透明背景合成在白底上、線條照畫（實得背景 " + trImg.Data[to] + "）");
            // ⑥ 參數壞 ⇒ 擋下、不寫圖
            string bad = room.Root + "/bad.png";
            var noCal = room.Run(("op", "section"), ("work", "keel"), ("ref", refPng), ("out", bad));
            var sameRef = room.Run(("op", "section"), ("work", "keel"), ("ref", refPng), ("ref_points", "1,1;1,1"), ("work_points", "0,0;1,1"), ("out", bad));
            var sameWork = room.Run(("op", "section"), ("work", "keel"), ("ref", refPng), ("ref_points", "100,300;1100,240"), ("work_points", "5,5;5,5"), ("out", bad));
            var nan = room.Run(("op", "section"), ("work", "keel"), ("ref", refPng), ("ref_points", "NaN,300;1100,240"), ("work_points", "0,0;100,6"), ("out", bad));
            var grid = room.Run(("op", "section"), ("work", "keel"), ("grid_m", "-1"), ("out", bad));
            var flipBad = room.Run(("op", "section"), ("work", "keel"), ("ref_flip", "maybe"), ("out", bad));
            var span = room.Run(("op", "section"), ("work", "keel"), ("region", "-2000000000..2000000000,0..9,0..9"), ("out", bad));
            // 第二輪審查補的：探針 NaN（只有 IsFinite 擋得住它 —— 上面 ref_points 的 NaN 另有比例檢查兜底，量不出 IsFinite 被拿掉）、
            // voxel_alpha=NaN、比例尺公尺數小到比例下溢、明給的網格太密、單次比例超過 1000
            SCP_CmdResult Cal(string scale, params (string, string)[] extra) => room.Run(new[] { ("op", "section"), ("work", "keel"), ("ref", refPng), ("ref_scale", scale),
                ("ref_anchor", "100,300"), ("work_anchor", "0,0"), ("out", bad) }.Concat(extra).ToArray());
            var probeNan = Cal("100,350;1100,350;10", ("probe", "NaN,1"));
            var alphaNan = room.Run(("op", "section"), ("work", "keel"), ("voxel_alpha", "NaN"), ("out", bad));
            var tiny = Cal("100,350;1100,350;1e-300");
            var dense = room.Run(("op", "section"), ("work", "keel"), ("px_per_voxel", "4"), ("grid_m", "0.001"), ("out", bad));
            var mpvBig = room.Run(("op", "section"), ("work", "keel"), ("meters_per_voxel", "5000"), ("out", bad));
            var all = new[] { noCal, sameRef, sameWork, nan, grid, flipBad, span, probeNan, alphaNan, tiny, dense, mpvBig };
            Check(all.All(r => r.ExitCode == 2) && !File.Exists(bad),
                "沒校準／ref 兩點重合／work 兩點重合／NaN／grid_m=-1／ref_flip=maybe／region 跨度溢位／probe NaN／voxel_alpha NaN／比例尺 1e-300 m／grid_m 太密／meters_per_voxel 5000 ⇒ 全部 exit 2 不寫圖（實得 "
                + string.Join(",", all.Select(r => r.ExitCode)) + "）");
            // 自動網格太密 ⇒ 不畫、grid_m 印 none（出圖照常）
            var autoDense = room.Run(("op", "section"), ("work", "keel"), ("px_per_voxel", "1"), ("region", "0..9,0..9,0..9"), ("out", room.Root + "/dense.png"));
            Check(autoDense.ExitCode == 0 && SculptRoom.V(autoDense, "grid_m") == "none", "自動網格太密 ⇒ grid_m=none（實得 " + SculptRoom.V(autoDense, "grid_m") + "）");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }
}
