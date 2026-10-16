// 區塊職責：雕刻觀測的點光源（TASK-0650）三組驗收 ——
//   ① 淨室（不用 GPU）：設定疊層（只換強度 ⇒ 開關與顏色沿用下層、unset 一格、不合法的值整層不寫回、不認得的鍵擋）、
//      作品的 lamps.json 讀寫一圈不變、壞掉的檔丟例外（⛔ 不默默當成不點燈）。
//   ② 真的作品（暫存）走觀測頁同一條路（Cmd_Sculpture.TryPrepareView）：meta kind=light 的模型（作品來源、有 wick）、obj 模型（沒有 wick ⇒ 外框中心）、
//      關聯（有 wick）各一盞，kind=gun 的不算；燈心座標逐格對上手算；wick 搬走 ⇒ 燈跟著走；parts=1 每盞指到對的實例、藏掉那個實例 ⇒ 少一盞；
//      沒開 ⇒ 一盞都不收；`work sub=lamps` 只有作者能改、壞值不寫入、存了之後 view 不帶參數也開。
//   ③ GPU：沒開（Lamps＝null）＝ 關著 ＝ 開著但強度 0 ⇒ 圖逐位元相同；燈放左邊 ⇒ 變亮的像素在左邊，搬到右邊 ⇒ 亮處跟著走。
//      這台機器建不出 GL 3.3 ⇒ Skipped（⛔ 不是通過）。
using Senate.Core;
using Senate.Desktop;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptLampProfileCleanRoom()
    {
        const string name = "雕刻點光源：設定疊層（只換強度 ⇒ 開關、顏色沿用下層；unset；不合法整層不寫回；不認得的鍵擋）＋作品 lamps.json 讀寫一圈、壞檔丟例外（淨室，TASK-0650）";
        var failures = new List<string>();
        void Check(bool v, string why) { if (!v) failures.Add(why); }
        string tmp = Path.Combine(Path.GetTempPath(), "senate_lamps_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var p = new SCP_SculptRenderParams();
            Check(p.Lamps == null && p.LampPoints.Count == 0, "內建預設不該點燈");
            var lo = SCP_JsonData.NewObject();
            Check(SCP_SculptRenderProfiles.TryEdit(lo, new Dictionary<string, string> { ["lamps"] = "on", ["lamp_radius"] = "4", ["lamp_color"] = "#102030" }, new List<string>(), out string e1), "下層 TryEdit：" + e1);
            Check(SCP_SculptRenderProfiles.TryApply(lo, p, tmp, out e1), "下層 TryApply：" + e1);
            Check(p.Lamps is { Enabled: true, Radius: 4, Intensity: 0.6, R: 0x10, G: 0x20, B: 0x30 }, "下層疊完應是 開、半徑 4、強度預設 0.6、#102030：" + Describe(p.Lamps));
            var hi = SCP_JsonData.NewObject();
            SCP_SculptRenderProfiles.TryEdit(hi, new Dictionary<string, string> { ["lamp_intensity"] = "1.5" }, new List<string>(), out _);
            Check(SCP_SculptRenderProfiles.TryApply(hi, p, tmp, out e1) && p.Lamps is { Enabled: true, Intensity: 1.5, Radius: 4, R: 0x10 }, "只換強度 ⇒ 開關、半徑、顏色要沿用：" + Describe(p.Lamps) + " " + e1);
            var bad = SCP_JsonData.NewObject();
            SCP_SculptRenderProfiles.TryEdit(bad, new Dictionary<string, string> { ["lamp_intensity"] = "2", ["lamp_radius"] = "0" }, new List<string>(), out _);
            Check(!SCP_SculptRenderProfiles.TryApply(bad, p, tmp, out string e2) && e2.Contains("radius"), "半徑 0 應擋：" + e2);
            Check(p.Lamps is { Intensity: 1.5 }, "🔴 擋下的那一層不該寫回任何一格（強度應還是 1.5）：" + Describe(p.Lamps));
            var typo = SCP_JsonData.Parse("{\"lamps\":{\"radious\":3}}");
            Check(!SCP_SculptRenderProfiles.TryApply(typo, p, tmp, out string e3), "lamps 物件裡打錯的鍵應擋：" + e3);
            var off = SCP_JsonData.NewObject();
            SCP_SculptRenderProfiles.TryEdit(off, new Dictionary<string, string> { ["lamps"] = "off" }, new List<string>(), out _);
            Check(SCP_SculptRenderProfiles.TryApply(off, p, tmp, out _) && p.Lamps is { Enabled: false, Intensity: 1.5 }, "lamps=off 應只關開關、值記得：" + Describe(p.Lamps));
            var un = SCP_JsonData.Parse("{\"lamps\":{\"radius\":1,\"intensity\":0.5}}");
            Check(SCP_SculptRenderProfiles.TryEdit(un, new Dictionary<string, string> { ["unset"] = "lamp_radius" }, new List<string>(), out string e4)
                  && !un["lamps"]["radius"].Exists && un["lamps"]["intensity"].Exists, "unset lamp_radius 應只拿掉 radius：" + un.ToJson(false) + " " + e4);

            // 太陽（夜景）：頂層 sun 0..1；越界擋；unset 拿掉
            var sun = SCP_JsonData.NewObject();
            Check(SCP_SculptRenderProfiles.TryEdit(sun, new Dictionary<string, string> { ["sun"] = "0.2" }, new List<string>(), out string e5)
                  && SCP_SculptRenderProfiles.TryApply(sun, p, tmp, out e5) && p.Sun == 0.2 && Math.Abs(p.SkyLight - 0.4) < 1e-12, "sun 0.2 ⇒ Sun 0.2、SkyLight 0.25＋0.75×0.2 ＝ 0.4：" + p.Sun + "／" + p.SkyLight + " " + e5);
            Check(!SCP_SculptRenderProfiles.TryApply(SCP_JsonData.Parse("{\"sun\":1.5}"), p, tmp, out string e6) && p.Sun == 0.2, "sun 1.5 應擋、不寫回：" + e6);
            Check(SCP_SculptRenderProfiles.TryEdit(sun, new Dictionary<string, string> { ["unset"] = "sun" }, new List<string>(), out _) && !sun["sun"].Exists, "unset sun 應拿掉");
            Check(new SCP_SculptRenderParams().SkyLight == 1, "白天（Sun 1）SkyLight 要正好 1（不乘 ⇒ 逐位元不變）");

            var paths = new SCP_SculptPaths(tmp);
            Check(SCP_SculptRenderProfiles.ReadWorkLamps(paths) == null, "沒有 lamps.json 應回 null");
            SCP_SculptRenderProfiles.SaveWorkLamps(paths, SCP_JsonData.Parse("{\"enabled\":true,\"intensity\":0.9,\"radius\":3}"));
            SCP_JsonData? back = SCP_SculptRenderProfiles.ReadWorkLamps(paths);
            var q = new SCP_SculptRenderParams();
            Check(back != null && SCP_SculptRenderProfiles.TryApplyLampsObject(back, q, out _) && q.Lamps is { Enabled: true, Intensity: 0.9, Radius: 3 }, "lamps.json 讀回不一致：" + back?.ToJson(false));
            File.WriteAllText(paths.LampsFile, "{\"schema\":1}");
            bool threw = false;
            try { SCP_SculptRenderProfiles.ReadWorkLamps(paths); } catch (InvalidDataException) { threw = true; }
            Check(threw, "🔴 沒有 lamps 物件的檔應丟例外（不默默當成不點燈）");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { try { Directory.Delete(tmp, true); } catch { } }
        return new(name, failures.Count == 0 ? "疊層 7 格＋太陽 4 格＋lamps.json 3 格 全過" : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);

        static string Describe(SCP_SculptLamps? l) => l == null ? "null" : $"{(l.Enabled ? "開" : "關")} 強度 {l.Intensity} 半徑 {l.Radius} #{l.R:x2}{l.G:x2}{l.B:x2}";
    }

    static CheckRow SculptLampSceneCleanRoom()
    {
        const string name = "雕刻點光源：場景裡的燈 —— kind=light 的作品模型在 wick、obj 模型在外框中心、關聯在 wick，kind=gun 不算；wick 搬走燈跟著走；parts=1 指到對的實例、藏掉少一盞；沒開一盞都不收；單盞光色；night=on／sun=；work sub=lamps 只有作者能改（淨室，TASK-0650）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string why) { if (!c) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(null!);
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "點光源 " + id), ("commission_ref", "user:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Box(string id, int x1, int x2, int y1, int y2, int z1, int z2, int color)
                => room.Run(("op", "box"), ("work", id), ("persona", "p"), ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", y1.ToString()), ("y2", y2.ToString()),
                            ("z1", z1.ToString()), ("z2", z2.ToString()), ("color", color.ToString()));
            SCP_CmdResult Anchor(string at) => room.Run(("op", "work"), ("sub", "anchor"), ("work", "lan"), ("persona", "p"), ("name", "wick"), ("at", at), ("overwrite", "1"));
            Check(Create("h", "24") && Create("lan", "4"), "兩件作品建立");
            Check(Box("h", 0, 23, 0, 23, 0, 0, 255).ExitCode == 0 && Box("lan", 1, 2, 1, 2, 0, 2, 245).ExitCode == 0, "主作品的地、燈籠的格");
            Check(Anchor("1,1,3").ExitCode == 0, "燈籠的固定點 wick");
            string aTet = Path.Combine(room.Root, "tet.obj").Replace('\\', '/');
            File.WriteAllText(aTet, "v 0 0 0\nv 2 0 0\nv 0 1 0\nv 0 0 3\nf 1 3 2\nf 1 2 4\nf 1 4 3\nf 2 3 4\n");
            SCP_CmdResult Model(string nm, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", "model"), ("work", "h"), ("persona", "p"), ("name", nm) };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            var aM1 = Model("L1", ("source_work", "lan"), ("at", "10,4,1"), ("meta", "{\"kind\":\"light\"}"));
            var aM2 = Model("F", ("file", aTet), ("axes", "zup"), ("at", "4,16,1"), ("meta", "{\"kind\":\"light\",\"light_color\":\"green\"}"));
            var aM3 = Model("G", ("file", aTet), ("axes", "zup"), ("at", "20,2,1"), ("meta", "{\"kind\":\"gun\"}"));
            var aLk = room.Run(("op", "work"), ("sub", "link"), ("work", "h"), ("persona", "p"), ("name", "LL"), ("source_work", "lan"), ("at", "16,16,1"), ("meta", "{\"kind\":\"lantern\",\"light_color\":\"#59ff8c\"}"));
            Check(aM1.ExitCode == 0 && aM2.ExitCode == 0 && aM3.ExitCode == 0 && aLk.ExitCode == 0, "三個模型、一條關聯：" + SculptRoom.All(aM1) + SculptRoom.All(aM2) + SculptRoom.All(aM3) + SculptRoom.All(aLk));

            var aRaw = new Dictionary<string, string> { ["data_root"] = room.Data, ["letters_root"] = room.Letters, ["work"] = "h", ["ao"] = "0" };
            SCP_SculptViewPlan Plan(params (string K, string V)[] iMore)
            {
                var a = new Dictionary<string, string>(aRaw);
                foreach (var (k, v) in iMore) a[k] = v;
                Check(Cmd_Sculpture.TryPrepareView(a, out SCP_SculptViewPlan aPlan, out string aLayers, out string aErr), "場景（" + string.Join(",", iMore.Select(x => x.K + "=" + x.V)) + "）：" + aErr);
                aPlan.Lines.Add("layers: " + aLayers);
                return aPlan;
            }
            string Pt(SCP_SculptViewPlan iPlan, string iPath)
            {
                var l = iPlan.Params.LampPoints.Find(x => x.Path == iPath);
                return l == null ? "（沒有）" : FormattableString.Invariant($"{l.X:0.##},{l.Y:0.##},{l.Z:0.##} {l.From}");
            }

            // 反向對照：沒開 ⇒ 一盞都不收
            var aOff = Plan();
            Check(aOff.Params.LampPoints.Count == 0 && (aOff.Params.Lamps == null || !aOff.Params.Lamps.Enabled), "沒開點光源 ⇒ 一盞都不收：" + aOff.Params.LampPoints.Count);

            var aOn = Plan(("lamps", "on"));
            string aGot = string.Join("；", aOn.Params.LampPoints.Select(l => l.Path + "@" + Pt(aOn, l.Path)));
            readings.Add("開燈：" + aGot);
            // 手算：L1 ＝ at＋(wick 格心 − pivot 0) ＝ (10,4,1)＋(1.5,1.5,3.5)；F ＝ (4,16,1)＋obj 外框中心 (1,0.5,1.5)；LL ＝ (16,16,1)＋(1.5,1.5,3.5)
            Check(aOn.Params.LampPoints.Count == 3, "應有 3 盞（kind=gun 不算）：" + aGot);
            Check(Pt(aOn, "L1") == "11.5,5.5,4.5 wick", "L1 應在 wick 11.5,5.5,4.5：" + Pt(aOn, "L1"));
            Check(Pt(aOn, "F") == "5,16.5,2.5 center", "F（obj、沒有 wick）應在外框中心 5,16.5,2.5：" + Pt(aOn, "F"));
            Check(Pt(aOn, "LL") == "17.5,17.5,4.5 wick", "關聯 LL（kind=lantern）應在 wick 17.5,17.5,4.5：" + Pt(aOn, "LL"));
            Check(aOn.Params.LampPoints.All(l => Math.Abs(l.Radius - 6 * 32) < 1e-9), "半徑 6 m ÷ 預設一格 1/32 m ＝ 192 格：" + string.Join(",", aOn.Params.LampPoints.Select(l => l.Radius)));
            // 單盞的光色（meta light_color）：LL 綠；F 寫壞 ⇒ 照預設（null）＋回傳檔記一行；L1 沒寫 ⇒ null
            string Col(string iPath) { var c = aOn.Params.LampPoints.Find(x => x.Path == iPath)?.Color; return c == null ? "null" : $"{c[0]},{c[1]},{c[2]}"; }
            Check(Col("LL") == "89,255,140" && Col("F") == "null" && Col("L1") == "null", "光色：LL #59ff8c、F 壞值照預設、L1 沒寫照預設：" + Col("LL") + "／" + Col("F") + "／" + Col("L1"));

            // 夜景：night=on ⇒ 太陽 0.1＋燈開；night=on lamps=off ⇒ 燈關、還是夜；sun= 顯式蓋過；sun 越界擋
            var aNight = Plan(("night", "on"));
            Check(aNight.Params.Sun == Cmd_Sculpture.NightSun && aNight.Params.Lamps is { Enabled: true } && aNight.Params.LampPoints.Count == 3, "night=on ⇒ 太陽 0.1、燈開 3 盞：" + aNight.Params.Sun + "／" + aNight.Params.LampPoints.Count);
            var aNightDark = Plan(("night", "on"), ("lamps", "off"));
            Check(aNightDark.Params.Sun == Cmd_Sculpture.NightSun && aNightDark.Params.LampPoints.Count == 0, "night=on lamps=off ⇒ 夜、不點燈：" + aNightDark.Params.LampPoints.Count);
            var aSun = Plan(("night", "on"), ("sun", "0.3"));
            Check(aSun.Params.Sun == 0.3, "sun=0.3 要蓋過夜景的 0.1：" + aSun.Params.Sun);
            Check(aOff.Params.Sun == 1, "沒給 ⇒ 白天（Sun 1）：" + aOff.Params.Sun);
            var aBadSun = new Dictionary<string, string>(aRaw) { ["sun"] = "2" };
            Check(!Cmd_Sculpture.TryPrepareView(aBadSun, out _, out _, out string aSunErr) && aSunErr.Contains("sun"), "sun=2 要擋：" + aSunErr);
            readings.Add("夜景：太陽 " + aNight.Params.Sun + "、" + aNight.Params.LampPoints.Count + " 盞；LL 光色 " + Col("LL"));

            // hide_models 拿掉 L1 ⇒ 合併的場景少一盞
            var aHide = Plan(("lamps", "on"), ("hide_models", "L1"));
            Check(aHide.Params.LampPoints.Count == 2 && Pt(aHide, "L1") == "（沒有）", "hide_models=L1 ⇒ 那盞不點：" + aHide.Params.LampPoints.Count);

            // wick 搬走 ⇒ 燈跟著走（模型與關聯都是）
            Check(Anchor("3,3,0").ExitCode == 0, "改 wick");
            var aMoved = Plan(("lamps", "on"));
            readings.Add("wick 搬到 3,3,0：L1 " + Pt(aMoved, "L1") + "、LL " + Pt(aMoved, "LL"));
            Check(Pt(aMoved, "L1") == "13.5,7.5,1.5 wick" && Pt(aMoved, "LL") == "19.5,19.5,1.5 wick", "wick 搬走燈要跟著走：" + Pt(aMoved, "L1") + "／" + Pt(aMoved, "LL"));

            // parts=1：每盞指到同一條路徑、同一種的實例
            var aSplit = Plan(("lamps", "on"), ("parts", "1"));
            SCP_SculptParts? aP = aSplit.Params.Parts;
            bool aPartsOk = aP != null && aSplit.Params.LampPoints.Count == 3 && aSplit.Params.LampPoints.All(l =>
                l.Part >= 0 && l.Part < aP.Instances.Count && aP.Instances[l.Part].Path == l.Path
                && aP.Instances[l.Part].Kind == (l.Path == "LL" ? SCP_SculptPartInstance.KindLink : SCP_SculptPartInstance.KindModel));
            Check(aPartsOk, "parts=1 每盞要指到自己的實例：" + string.Join("；", aSplit.Params.LampPoints.Select(l => l.Path + "→" + l.Part)));
            Check(Pt(aSplit, "L1") == Pt(aMoved, "L1") && Pt(aSplit, "F") == Pt(aMoved, "F") && Pt(aSplit, "LL") == Pt(aMoved, "LL"), "合併與分開的燈心要一樣");

            // work sub=lamps：別人擋、壞值擋、作者存了之後 view 不帶參數也開
            var aNotOwner = room.Run(("op", "work"), ("sub", "lamps"), ("work", "h"), ("persona", "q"), ("lamp_intensity", "1"));
            Check(aNotOwner.ExitCode != 0 && !File.Exists(room.Sculpt + "/works/h/lamps.json"), "不是作者 ⇒ 擋、沒寫檔：" + SculptRoom.All(aNotOwner));
            var aBadSet = room.Run(("op", "work"), ("sub", "lamps"), ("work", "h"), ("persona", "p"), ("lamp_radius", "-1"));
            Check(aBadSet.ExitCode != 0 && !File.Exists(room.Sculpt + "/works/h/lamps.json"), "半徑 −1 ⇒ 擋、沒寫檔：" + SculptRoom.All(aBadSet));
            var aSet = room.Run(("op", "work"), ("sub", "lamps"), ("work", "h"), ("persona", "p"), ("lamp_intensity", "1.2"));
            Check(aSet.ExitCode == 0 && SculptRoom.V(aSet, "lamps") == "on", "作者只給強度 ⇒ 存檔並當成開：" + SculptRoom.All(aSet));
            var aDefault = Plan();
            readings.Add("存了作品預設後不帶參數：" + aDefault.Params.LampPoints.Count + " 盞、強度 " + aDefault.Params.Lamps?.Intensity + "、" + aDefault.Lines.Last());
            Check(aDefault.Params.LampPoints.Count == 3 && aDefault.Params.Lamps is { Intensity: 1.2 } && aDefault.Lines.Last().Contains("作品點光源"), "作品預設要疊進來（3 盞、強度 1.2、layers 有作品點光源）");
            var aCliOff = Plan(("lamps", "off"));
            Check(aCliOff.Params.LampPoints.Count == 0, "CLI lamps=off 要蓋過作品預設：" + aCliOff.Params.LampPoints.Count);

            // GPU：藏掉關聯那個實例 ⇒ 少點一盞（讀數）
            using var r = new SenateSculptRenderer();
            var q1 = aSplit.Params.Clone(); q1.Width = 128; q1.Height = 128; q1.Skybox = SCP_SculptRenderParams.SkyboxNone; q1.Shadow = false;
            if (!r.TryRender(aSplit.Voxels, q1, out _, out string aGpuErr))
            {
                if (aGpuErr.Contains("建不出")) readings.Add("這台機器沒有可用的 GPU ⇒ 實例開關那一格跳過：" + aGpuErr);
                else failures.Add("parts=1 開燈畫不出來：" + aGpuErr);
            }
            else if (aP != null)
            {
                int aAll = r.LastReading.Lamps;
                var q2 = q1.Clone(); q2.PartVisible = aP.Instances.Select(i => i.Path != "LL").ToArray();
                Check(r.TryRender(aSplit.Voxels, q2, out _, out aGpuErr), "藏關聯畫不出來：" + aGpuErr);
                readings.Add($"GPU 點了 {aAll} 盞；藏關聯 LL ⇒ {r.LastReading.Lamps} 盞");
                Check(aAll == 3 && r.LastReading.Lamps == 2, "藏掉關聯 LL 那個實例 ⇒ 那盞燈也熄（3 → 2）：" + aAll + " → " + r.LastReading.Lamps);
            }
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { SenateSculptureRewards.Factory = original; }
        string aRead = string.Join("；", readings);
        return new(name, failures.Count == 0 ? aRead + " 全過" : string.Join("；", failures) + (aRead.Length > 0 ? "｜" + aRead : ""),
                   failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }

    static CheckRow SculptLampGpuCleanRoom()
    {
        const string name = "雕刻點光源（GPU）：沒開 ＝ 關著 ＝ 強度 0 ＝ Sun 1 ⇒ 圖逐位元相同；燈在左 ⇒ 亮處在左、搬到右 ⇒ 亮處跟著走；Sun 0 整張暗、夜裡燈照得更廣；單盞綠燈（淨室，TASK-0650）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool v, string why) { if (!v) failures.Add(why); }
        using var r = new SenateSculptRenderer();
        try
        {
            var vox = new List<SCP_SculptVoxel>();
            for (int x = 0; x < 64; x++) for (int y = 0; y < 64; y++) vox.Add(new SCP_SculptVoxel(x, y, 0, 182));   // 64×64 一層灰地
            var p = new SCP_SculptRenderParams { Width = 256, Height = 256, Skybox = SCP_SculptRenderParams.SkyboxNone, Shadow = false, AmbientOcclusion = false, PitchDeg = 60 };
            if (!r.TryRender(vox, p, out byte[] aNone, out string err))
                return err.Contains("建不出") ? new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped)
                                              : new CheckRow(name, "沒開燈就畫不出來：" + err, CheckResult.Fail);
            SCP_SculptRenderParams With(bool iOn, double iIntensity, double iX)
            {
                var q = p.Clone();
                q.Lamps = new SCP_SculptLamps { Enabled = iOn, Intensity = iIntensity, Radius = 12 };
                q.LampPoints = new List<SCP_SculptLampPoint> { new SCP_SculptLampPoint { Path = "L", X = iX, Y = 32, Z = 3, Radius = 12, From = SCP_SculptLampFinder.AtWick } };
                return q;
            }
            Check(r.TryRender(vox, With(false, 1, 12), out byte[] aOff, out err), "關著畫不出來：" + err);
            Check(aNone.AsSpan().SequenceEqual(aOff), "🔴 Lamps.Enabled＝false（場景裡有燈）的圖應跟沒有燈逐位元相同");
            Check(r.TryRender(vox, With(true, 0, 12), out byte[] aZero, out err), "強度 0 畫不出來：" + err);
            Check(aNone.AsSpan().SequenceEqual(aZero), "🔴 開著但強度 0 ⇒ 應跟沒有燈逐位元相同（加上去的是 0）");
            Check(r.TryRender(vox, With(true, 1, 12), out byte[] aLeft, out err), "燈在 x 12 畫不出來：" + err);
            int aLeftLamps = r.LastReading.Lamps;
            Check(r.TryRender(vox, With(true, 1, 52), out byte[] aRight, out err), "燈在 x 52 畫不出來：" + err);
            (int N, double X, double Y, int Max) Bright(byte[] a)
            {
                int n = 0, mx = 0; double sx = 0, sy = 0;
                for (int k = 0; k < a.Length; k += 4)
                {
                    int d = a[k] + a[k + 1] + a[k + 2] - aNone[k] - aNone[k + 1] - aNone[k + 2];
                    if (d > mx) mx = d;
                    if (d <= 15) continue;
                    int i = k / 4; n++; sx += i % p.Width; sy += i / p.Width;
                }
                return n == 0 ? (0, double.NaN, double.NaN, mx) : (n, sx / n, sy / n, mx);
            }
            var bl = Bright(aLeft); var br = Bright(aRight);
            double aShift = Math.Sqrt((bl.X - br.X) * (bl.X - br.X) + (bl.Y - br.Y) * (bl.Y - br.Y));
            readings.Add(FormattableString.Invariant($"燈在 x 12：變亮 {bl.N} 像素（最多 +{bl.Max}）、重心 ({bl.X:0},{bl.Y:0})；燈在 x 52：{br.N} 像素、重心 ({br.X:0},{br.Y:0})；重心移了 {aShift:0} px；點了 {aLeftLamps} 盞"));
            Check(aLeftLamps == 1, "讀數：應點 1 盞：" + aLeftLamps);
            Check(bl.N > 500 && br.N > 500, "開燈 ⇒ 燈旁的地要變亮（> 500 像素）：" + bl.N + "／" + br.N);
            Check(aShift > 60, "燈搬 40 格 ⇒ 亮處的重心要跟著移（> 60 px）：" + aShift.ToString("0"));
            // 反向對照：兩張只差燈的位置 ⇒ 不該一樣（不然上面量的是別的東西）
            Check(!aLeft.AsSpan().SequenceEqual(aRight), "燈換位置畫面要變");

            // 夜景：Sun 1 顯式 ＝ 預設；Sun 0 沒燈 ⇒ 整張暗下來；夜裡同一盞燈照亮的像素比白天多（天越暗燈越亮）
            double Mean(byte[] a) { double t = 0; for (int k = 0; k < a.Length; k += 4) t += a[k] + a[k + 1] + a[k + 2]; return t / (a.Length / 4) / 3; }
            var pDay = p.Clone(); pDay.Sun = 1;
            Check(r.TryRender(vox, pDay, out byte[] aDay1, out err) && aNone.AsSpan().SequenceEqual(aDay1), "🔴 Sun 1 要跟預設逐位元相同：" + err);
            var pDark = p.Clone(); pDark.Sun = 0;
            Check(r.TryRender(vox, pDark, out byte[] aDark, out err), "Sun 0 畫不出來：" + err);
            SCP_SculptRenderParams Night(SCP_SculptRenderParams q) { q.Sun = 0.1; return q; }
            var pNightNone = Night(p.Clone());
            Check(r.TryRender(vox, pNightNone, out byte[] aNightNone, out err), "夜、沒燈畫不出來：" + err);
            Check(r.TryRender(vox, Night(With(true, 1, 12)), out byte[] aNightLamp, out err), "夜、燈在 x 12 畫不出來：" + err);
            // 燈加上去的總亮度（Σ 變亮量）：夜裡 × (1.6 − 0.1)／0.6 ＝ 2.5 倍，扣掉飽和與底色變暗，要 > 1.8 倍
            long Added(byte[] a, byte[] iBase) { long t = 0; for (int k = 0; k < a.Length; k += 4) t += Math.Max(0, a[k] + a[k + 1] + a[k + 2] - iBase[k] - iBase[k + 1] - iBase[k + 2]); return t; }
            long aDayAdded = Added(aLeft, aNone), aNightAdded = Added(aNightLamp, aNightNone);
            int aNightLit = 0; double aNightMax = 0;
            for (int k = 0; k < aNightLamp.Length; k += 4)
            {
                int d = aNightLamp[k] + aNightLamp[k + 1] + aNightLamp[k + 2] - aNightNone[k] - aNightNone[k + 1] - aNightNone[k + 2];
                if (d > 15) aNightLit++;
                aNightMax = Math.Max(aNightMax, d);
            }
            // 單盞光色：綠燈 ⇒ 變亮的地方綠比紅多
            var pGreen = With(true, 1, 12); pGreen.LampPoints[0].Color = new byte[] { 0, 255, 0 };
            Check(r.TryRender(vox, pGreen, out byte[] aGreen, out err), "綠燈畫不出來：" + err);
            long dR = 0, dG = 0;
            for (int k = 0; k < aGreen.Length; k += 4) { dR += aGreen[k] - aNone[k]; dG += aGreen[k + 1] - aNone[k + 1]; }
            readings.Add(FormattableString.Invariant($"平均亮度：白天 {Mean(aNone):0.0}、Sun 0 {Mean(aDark):0.0}；同一盞燈變亮的像素：白天 {bl.N}、夜（Sun 0.1）{aNightLit}（最多 +{aNightMax}）；Σ 變亮量 白天 {aDayAdded}／夜 {aNightAdded}（{(double)aNightAdded / Math.Max(1, aDayAdded):0.00} 倍）；綠燈 ΣΔG {dG}／ΣΔR {dR}"));
            Check(Mean(aDark) < Mean(aNone) * 0.4, "Sun 0 ⇒ 整張要暗到白天的 40% 以下");
            Check(aNightLit > bl.N && aNightAdded > aDayAdded * 1.8, "夜裡同一盞燈要照得更廣（像素多）、更亮（Σ 變亮量 > 1.8 倍）：" + aNightLit + " vs " + bl.N + "、" + aNightAdded + " vs " + aDayAdded);
            Check(dG > dR * 3, "綠燈 ⇒ 綠的增量要遠大於紅的：" + dG + " vs " + dR);
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        string aRead = string.Join("；", readings);
        return new(name, failures.Count == 0 ? aRead + " 全過" : string.Join("；", failures) + (aRead.Length > 0 ? "｜" + aRead : ""),
                   failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
