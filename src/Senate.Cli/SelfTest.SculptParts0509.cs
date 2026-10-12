// 淨室驗收（TASK-0509）：觀測頁 GPU 預覽的子模型分開畫 ——
// ① 關聯節點的矩陣（連續）跟組裝器的格子落點（離散，SCP_SculptLinks.Transform）逐格對上：鏡像、轉向、巢狀；行列式的正負跟著鏡像次數；
// ② 顯示判斷跟繩的 PathDrawn 同一個判準（預設／all／none／點名／拿掉／self）；模型跟著所在那一層、models=0、hide_models；點名不存在的要說；
// ③ 真的作品（暫存）走觀測頁同一條路（Cmd_Sculpture.TryPrepareView parts=1）：主作品只帶自己的格、同一件關聯兩次只一份來源；
// ④ GPU：分開畫跟合併畫（關聯沒貼著主作品、AO 關）逐像素對拍；切開關 ⇒ 新建網格 0、畫面有變；切回來 ⇒ 畫面逐位元回到原樣。
using Senate.Core;
using Senate.Desktop;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptPartsCleanRoom()
    {
        const string name = "雕刻子模型分開畫：關聯矩陣對格子落點（鏡像／轉向／巢狀）／顯示跟 PathDrawn 同判準／parts=1 主作品只帶自己的格＋同件一份來源／GPU 分開與合併對拍＋開關只切顯示（淨室，TASK-0509）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string why) { if (!c) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        bool aSkippedGpu = false;
        try
        {
            // ── ① 矩陣 vs 格子落點 ──
            var aCardA = new SCP_SculptWork { id = "a", size_x = 3, size_y = 2, size_z = 2 };
            var aCardB = new SCP_SculptWork { id = "b", size_x = 2, size_y = 1, size_z = 1 };
            var aLinkA = new SCP_SculptLink { name = "a", work = "a", at = new List<int> { 5, 1, 0 }, turn = 90, mirror = "x", pivot = new List<int> { 3, 2, 2 } };
            var aLinkB = new SCP_SculptLink { name = "b", work = "b", at = new List<int> { 1, 0, 1 }, turn = 270, mirror = "yz", pivot = new List<int> { 2, 1, 1 } };
            var aNodeB = new SCP_SculptLinkNode { Path = "a/b", Link = aLinkB, Card = aCardB };
            var aNodeA = new SCP_SculptLinkNode { Path = "a", Link = aLinkA, Card = aCardA, Children = new List<SCP_SculptLinkNode> { aNodeB } };
            var aTree = new List<SCP_SculptLinkNode> { aNodeA };
            var aIndex = SCP_SculptRopes.IndexTree(aTree);
            double[] aMa = SCP_SculptParts.LinkMatrix("a", aIndex), aMb = SCP_SculptParts.LinkMatrix("a/b", aIndex);
            int aCells = 0, aBad = 0;
            void Place(SCP_SculptLink l, int x, int y, int z, out int ox, out int oy, out int oz)
            {
                SCP_SculptLinks.Transform(l.mirror, l.turn, l.pivot[0], l.pivot[1], l.pivot[2], x, y, z, out ox, out oy, out oz);
                ox += l.at[0]; oy += l.at[1]; oz += l.at[2];
            }
            bool Same(double[] m, int x, int y, int z, int ex, int ey, int ez)
            {
                double[] c = SCP_SculptModels.Apply(m, new double[] { x + 0.5, y + 0.5, z + 0.5 });
                return Math.Abs(c[0] - (ex + 0.5)) < 1e-9 && Math.Abs(c[1] - (ey + 0.5)) < 1e-9 && Math.Abs(c[2] - (ez + 0.5)) < 1e-9;
            }
            for (int x = 0; x < 3; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++)
            {
                Place(aLinkA, x, y, z, out int ax, out int ay, out int az);
                aCells++; if (!Same(aMa, x, y, z, ax, ay, az)) aBad++;
            }
            for (int x = 0; x < 2; x++)
            {
                Place(aLinkB, x, 0, 0, out int bx, out int by, out int bz);
                Place(aLinkA, bx, by, bz, out int ax, out int ay, out int az);   // 巢狀：先過 b 再過 a
                aCells++; if (!Same(aMb, x, 0, 0, ax, ay, az)) aBad++;
            }
            readings.Add($"矩陣 vs 格子落點 {aCells} 格、對不上 {aBad}；det(a)={SCP_SculptParts.Det3(aMa):0}、det(a/b)={SCP_SculptParts.Det3(aMb):0}");
            Check(aBad == 0, "關聯矩陣跟格子落點對不上 " + aBad + " 格");
            Check(SCP_SculptParts.Det3(aMa) < 0 && SCP_SculptParts.Det3(aMb) < 0, "鏡像次數：a 翻一軸（det<0）、a/b 再翻兩軸共三軸（det<0）");

            // ── ② 顯示 ──
            aLinkB.enabled = false;
            var aParts = new SCP_SculptParts();
            aParts.Instances.Add(new SCP_SculptPartInstance { Kind = SCP_SculptPartInstance.KindLink, Path = "a", Frame = "a", Enabled = true });
            aParts.Instances.Add(new SCP_SculptPartInstance { Kind = SCP_SculptPartInstance.KindLink, Path = "a/b", Frame = "a/b", Enabled = false });
            aParts.Instances.Add(new SCP_SculptPartInstance { Kind = SCP_SculptPartInstance.KindModel, Path = "a/b/m", Frame = "a/b" });
            aParts.Instances.Add(new SCP_SculptPartInstance { Kind = SCP_SculptPartInstance.KindModel, Path = "m0", Frame = "" });
            string Vis(string iLinks, string iHide, string iModels = "", string iHideModels = "")
            {
                var aSel = SCP_SculptLinkSelection.Parse(iLinks, iHide);
                var s = SCP_SculptParts.Visibility(aParts, aSel, SCP_SculptModelSelection.Parse(iModels, iHideModels));
                // 反向對照：關聯那兩格跟繩的 PathDrawn（同一棵樹、同一個選擇）一致
                Check(s.Visible[0] == SCP_SculptRopes.PathDrawn("a", aIndex, aSel) && s.Visible[1] == SCP_SculptRopes.PathDrawn("a/b", aIndex, aSel),
                      "links=" + iLinks + " hide=" + iHide + " 跟 PathDrawn 不一致");
                return (s.Own ? "O" : "o") + new string(s.Visible.Select(b => b ? '1' : '0').ToArray());
            }
            Check(Vis("", "") == "O1001", "預設：a 畫、a/b 設為關不畫、它上面的模型跟著不畫、根上的模型畫：" + Vis("", ""));
            Check(Vis("all", "") == "O1111", "all：連設為關的都畫：" + Vis("all", ""));
            Check(Vis("none", "") == "O0001", "none：關聯都不畫、根上的模型照畫：" + Vis("none", ""));
            Check(Vis("a/b", "") == "O1111", "點名 a/b：連同祖先 a：" + Vis("a/b", ""));
            Check(Vis("all", "a") == "O0001", "拿掉 a：連同底下：" + Vis("all", "a"));
            Check(Vis("", "self") == "o1001", "hide_links=self：只藏主作品自己的格：" + Vis("", "self"));
            Check(Vis("all", "", "0") == "O1100", "models=0：模型都不畫：" + Vis("all", "", "0"));
            Check(Vis("all", "", "", "m0") == "O1110", "hide_models=m0：" + Vis("all", "", "", "m0"));
            var aUnk = SCP_SculptParts.Visibility(aParts, SCP_SculptLinkSelection.Parse("zz", ""), SCP_SculptModelSelection.Parse("", "nope"));
            Check(aUnk.Unknown.Count == 2, "點名不存在的關聯與模型要說出來：" + string.Join("、", aUnk.Unknown));

            // ── ③ 真的作品：parts=1 ──
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(null!);
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "子模型 " + id), ("commission_ref", "user:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Box(string id, int x1, int x2, int y1, int y2, int z1, int z2, int color)
                => room.Run(("op", "box"), ("work", id), ("persona", "p"), ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", y1.ToString()), ("y2", y2.ToString()),
                            ("z1", z1.ToString()), ("z2", z2.ToString()), ("color", color.ToString()));
            SCP_CmdResult Link(string nm, string at, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", "link"), ("work", "h"), ("persona", "p"), ("name", nm), ("source_work", "gun"), ("at", at) };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            Check(Create("h", "24") && Create("gun", "3,2,2"), "兩件作品建立");
            Check(Box("h", 2, 5, 2, 5, 0, 1, 200).ExitCode == 0, "主作品的格");
            // 砲：L 形兩色（不對稱 ⇒ 轉錯、翻錯都看得出來）
            Check(Box("gun", 0, 2, 0, 0, 0, 0, 28).ExitCode == 0 && Box("gun", 0, 0, 1, 1, 0, 1, 224).ExitCode == 0, "砲的格");
            Check(Link("g1", "12,3,0").ExitCode == 0 && Link("g2", "12,12,2", ("turn", "90"), ("mirror", "x")).ExitCode == 0
                  && Link("g3", "3,14,0", ("turn", "180")).ExitCode == 0, "同一件關聯三次（轉向、鏡像）");
            // 外部模型：不對稱的四面體（朝外逆時針），同一個 obj 用兩個名字各放一份（縮放＋旋轉＋鏡像、旋轉＋縮放）⇒ 兩份檔、內容一樣 ⇒ 一份來源
            string aTet = Path.Combine(room.Root, "tet.obj").Replace('\\', '/');
            File.WriteAllText(aTet, "v 0 0 0\nv 2 0 0\nv 0 1 0\nv 0 0 3\nf 1 3 2\nf 1 2 4\nf 1 4 3\nf 2 3 4\n");
            SCP_CmdResult Model(string nm, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", "model"), ("work", "h"), ("persona", "p"), ("name", nm), ("file", aTet), ("axes", "zup") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            var aT1 = Model("t1", ("at", "4,16,1"), ("scale", "2,1,1.5"), ("rotate", "0,0,30"), ("mirror", "x"));
            var aT2 = Model("t2", ("at", "18,18,0"), ("rotate", "20,0,-60"), ("scale", "1.5"));
            Check(aT1.ExitCode == 0 && aT2.ExitCode == 0, "兩個模型加得進去：" + SculptRoom.All(aT1) + SculptRoom.All(aT2));
            var aRaw = new Dictionary<string, string> { ["data_root"] = room.Data, ["letters_root"] = room.Letters, ["work"] = "h", ["ao"] = "0" };
            Check(Cmd_Sculpture.TryPrepareView(aRaw, out SCP_SculptViewPlan aMerged, out _, out string aErr0), "合併的場景：" + aErr0);
            var aRawP = new Dictionary<string, string>(aRaw) { ["parts"] = "1" };
            Check(Cmd_Sculpture.TryPrepareView(aRawP, out SCP_SculptViewPlan aSplit, out _, out string aErr1), "parts=1 的場景：" + aErr1);
            SCP_SculptParts? aP = aSplit.Params.Parts;
            int aGunVox = 3 + 2;   // 一橫三格＋一直兩格
            readings.Add($"合併 {aMerged.Voxels.Count} 格；parts=1 主作品 {aSplit.Voxels.Count} 格＋{aP?.Sources.Count} 份來源／{aP?.Instances.Count} 個實例");
            Check(aP != null && aP.Sources.Count == 2 && aP.Instances.Count == 5, "同一件關聯三次＋同一個 obj 兩個名字 ⇒ 兩份來源、五個實例：" + aP?.Sources.Count + "／" + aP?.Instances.Count);
            Check(aMerged.Voxels.Count == aSplit.Voxels.Count + 3 * aGunVox, $"合併 ＝ 主作品＋三份砲（{aMerged.Voxels.Count} vs {aSplit.Voxels.Count}+{3 * aGunVox}）");
            Check(aSplit.Params.Parts == null || aSplit.Params.Models.Count == 0, "parts=1 時模型不走合併那一趟");
            var aBadRaw = new Dictionary<string, string>(aRawP) { ["region"] = "0..5,0..5,0..2" };
            Check(!Cmd_Sculpture.TryPrepareView(aBadRaw, out _, out _, out string aErr2) && aErr2.Contains("parts"), "parts=1＋region 要擋：" + aErr2);

            // ── ④ GPU ──
            using var r = new SenateSculptRenderer();
            SCP_SculptRenderParams Base(SCP_SculptViewPlan iPlan)
            {
                var q = iPlan.Params.Clone();
                q.Width = 384; q.Height = 384; q.AmbientOcclusion = false; q.Skybox = SCP_SculptRenderParams.SkyboxNone; q.Floor = null;
                // 只比幾何：鏡頭釘死（自動框景兩邊拿的點不同 —— 合併拿 voxel 中心、分開拿實例外框八角）、陰影關（陰影圖的範圍也跟著框景點）
                q.Projection = SCP_SculptProjection.Orthographic; q.Zoom = 1.2; q.TargetX = 12; q.TargetY = 12; q.TargetZ = 1; q.Shadow = false;
                return q;
            }
            if (!r.TryRender(aMerged.Voxels, Base(aMerged), out byte[] aImgMerged, out string aGpuErr))
            {
                if (aGpuErr.Contains("建不出")) { aSkippedGpu = true; readings.Add("這台機器沒有可用的 GPU ⇒ ④ 跳過：" + aGpuErr); }
                else failures.Add("合併畫法失敗：" + aGpuErr);
            }
            else if (aP != null)
            {
                var q1 = Base(aSplit);
                Check(r.TryRender(aSplit.Voxels, q1, out byte[] aImgSplit, out aGpuErr), "分開畫法失敗：" + aGpuErr);
                var r1 = r.LastReading;
                double aFrac = PixelDiffFraction(aImgMerged, aImgSplit, out int aDiff);
                readings.Add($"分開 vs 合併：差異 {aDiff} 像素（{aFrac:P3}）；第一張新建 {r1.PartBuilds} 份網格");
                Check(aFrac <= SculptMergeDiffLimit, $"分開畫跟合併畫差太多 {aFrac:P3}（> {SculptMergeDiffLimit:P1}）—— 矩陣、鏡像繞序或法線錯了");
                Check(r1.PartBuilds == 2 && r1.PartShown == 5, $"第一張：兩份網格、畫五個實例（新建 {r1.PartBuilds}、畫 {r1.PartShown}）");
                long aTotal = r.PartBuildsTotal;
                var q2 = Base(aSplit); q2.PartVisible = new[] { true, false, true, true, true };
                Check(r.TryRender(aSplit.Voxels, q2, out byte[] aImgHide, out aGpuErr), "藏一個實例：" + aGpuErr);
                var r2 = r.LastReading;
                double aHideFrac = PixelDiffFraction(aImgSplit, aImgHide, out int aHideDiff);
                var q3 = Base(aSplit);
                Check(r.TryRender(aSplit.Voxels, q3, out byte[] aImgBack, out aGpuErr), "切回來：" + aGpuErr);
                readings.Add($"藏 g2：新建 {r2.PartBuilds}、畫 {r2.PartShown}、畫面變了 {aHideDiff} 像素；切回來逐位元相同 {aImgBack.AsSpan().SequenceEqual(aImgSplit)}");
                Check(r2.PartBuilds == 0 && r.PartBuildsTotal == aTotal && r2.PartShown == 4, "切開關不該新建網格");
                Check(aHideDiff > 0, "反向對照：藏一個實例畫面要變（不然這把尺量不到顯示）");
                Check(aImgBack.AsSpan().SequenceEqual(aImgSplit) && r.LastReading.PartBuilds == 0, "切回來畫面要逐位元回到原樣、也不新建");
                // 反向對照：兩個模型真的在畫面裡（藏掉 ⇒ 畫面要變；不然上面的「差 0 像素」量的是空氣）
                var q4 = Base(aSplit); q4.PartVisible = new[] { true, true, true, false, false };
                Check(r.TryRender(aSplit.Voxels, q4, out byte[] aImgNoModel, out aGpuErr), "藏模型：" + aGpuErr);
                double aNoModelFrac = PixelDiffFraction(aImgSplit, aImgNoModel, out int aNoModelDiff);
                Check(aNoModelDiff > 0, "藏掉兩個模型畫面沒變 ⇒ 模型不在畫面裡，對拍量不到模型");
                // 讀數（不判）：開陰影時兩種畫法差多少 —— 陰影圖的範圍跟著框景點（合併拿 voxel 中心、分開拿實例外框）
                var qs1 = Base(aMerged); qs1.Shadow = true; var qs2 = Base(aSplit); qs2.Shadow = true;
                r.TryRender(aMerged.Voxels, qs1, out byte[] aSh1, out _);
                r.TryRender(aSplit.Voxels, qs2, out byte[] aSh2, out _);
                double aShFrac = PixelDiffFraction(aSh1, aSh2, out int aShDiff);
                readings.Add($"藏兩個模型：畫面變了 {aNoModelDiff} 像素；開陰影時分開 vs 合併差 {aShDiff} 像素（{aShFrac:P3}，讀數不判）");
            }
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { SenateSculptureRewards.Factory = original; }
        string aRead = string.Join("；", readings);
        if (failures.Count > 0) return new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
        return new CheckRow(name, (aSkippedGpu ? "①～③ 過、④ 跳過（沒有 GPU）：" : "①～④ 全過：") + aRead, CheckResult.Pass);
    }
}
