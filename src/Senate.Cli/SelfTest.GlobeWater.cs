// 區塊職責：球面水體層（TASK-0765）的淨室驗收 —— 湖（含湖中島、低於海平面）、湖的出口、支流、瀑布四種、拒絕的幾種情形、
//          水體 Undo 與格子 Undo 互不干擾、門檻改了瀑布跟著變、regrid 之後照新 N 柵格化、沒有水體時輸出逐位元組相同。
// 物理意義：每一格都拿「跟工具無關的量」對：查格子回的水體 id、顯示層與格子層各自的值、事件檔張數 —— 不以「回 success」當憑據。
//          拒絕的每一種都量「水體事件檔張數沒變」（零寫入），不只看 exit code。
using SCP.Core.Cmd;
using SCP.Core.Globe;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow GlobeWaterCleanRoom()
    {
        const string name = "球面水體層：湖與湖中島／低於海平面／湖的出口／支流／瀑布（河身、匯入、入湖、入海）／往上游、接不存在、太遠、繞環、刪被接著的 ⇒ 零寫入／水體與格子 Undo 分開／門檻／regrid／空水體輸出不變（淨室，TASK-0765）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        string aData = Path.Combine(Path.GetTempPath(), "senate_globe_water_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(aData);
            SCP_CmdResult Run(params (string K, string V)[] a)
            {
                var d = new Dictionary<string, string> { ["data_root"] = aData };
                foreach (var (k, v) in a) d[k] = v;
                return SCP_CmdRegistry.Dispatch("globe", d);
            }
            string Val(SCP_CmdResult r, string k) => r.Values.FirstOrDefault(kv => kv.Key == k).Value ?? "";
            var store = new SCP_GlobeStore(new SCP_GlobePaths(Path.Combine(aData, SCP_GlobePaths.DirName)));
            var ws = new SCP_GlobeWaterStore(store);
            int WaterEvents() => Directory.Exists(ws.EventsDir) ? Directory.GetFiles(ws.EventsDir, "*.json").Length : 0;
            string WaterAt(double la, double lo) => Val(Run(("op", "cell"), ("lat", la.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("lon", lo.ToString(System.Globalization.CultureInfo.InvariantCulture))), "water");

            Check(Run(("op", "init"), ("n", "512")).ExitCode == 0, "init");
            // 空水體：顯示層就是格子層本身；有過水體又全部刪掉 ⇒ 輸出跟從沒有水體時逐位元組相同
            Run(("op", "point"), ("persona", "a"), ("lat", "10"), ("lon", "20"), ("radius", "40"), ("color", "#336633"));
            string p0 = Path.Combine(aData, "w0.png"), p1 = Path.Combine(aData, "w1.png"), p2 = Path.Combine(aData, "w2.png");
            Run(("op", "render"), ("center", "11,21"), ("zoom", "6"), ("size", "200"), ("graticule", "0"), ("out", p0));
            SCP_GlobeState s0 = store.Load();
            Check(ReferenceEquals(s0.Display, s0.Cells), "沒有水體 ⇒ 顯示層就是格子層");

            // ① 湖：外環＋一座湖中島，水面 120 m
            var lake = Run(("op", "water"), ("sub", "lake"), ("persona", "a"), ("name", "測試湖"), ("surface", "120"),
                           ("points", "10,20;10,22;12,22;12,20"), ("holes", "10.8,20.8;10.8,21.2;11.2,21.2;11.2,20.8"));
            Check(lake.ExitCode == 0 && Val(lake, "id") == "lake-1", "新增湖（自動編號 lake-1）：" + string.Join(" ", lake.Lines));
            Check(WaterAt(10.4, 20.4) == "lake-1" && WaterAt(11, 21) == "" && WaterAt(13, 21) == "", "湖裡是 lake-1、湖中島與湖外不是");
            SCP_GlobeState s1 = store.Load();
            int iLake = s1.Grid.LatLonToCell(10.4, 20.4);
            Check(s1.Display.Get(iLake) == SCP_GlobeWaterRaster.LakeColor && s1.Cells.Get(iLake) == 0x336633, "顯示層是湖色、格子層的顏色沒被動");
            Check(s1.LastSeq == 1, "水體不寫格子事件（格子事件仍 1 筆）：" + s1.LastSeq);

            // ② 低於海平面的湖
            Check(Run(("op", "water"), ("sub", "lake"), ("persona", "a"), ("id", "deep"), ("surface", "-40"), ("points", "-5,30;-5,31;-4,31;-4,30")).ExitCode == 0
                  && WaterAt(-4.5, 30.5) == "deep", "低於海平面的湖（-40 m）");

            // ③ 湖的出口 river-1：120 → 100（第 2 段 100 → 40 落差 60 m、約 220 m 坡度 0.27 ⇒ 河身瀑布）→ 入海（最後 30 m ⇒ 入海瀑布）
            //    第 1 段 120 → 100 落差 20 m 但攤在 111 km 上 ⇒ 不是瀑布（反向對照：只看落差的話會多一座）
            var r1 = Run(("op", "water"), ("sub", "river"), ("persona", "a"), ("name", "主流"), ("from", "lake:lake-1"), ("to", "sea"),
                         ("points", "12,21,120,200;13,21,100,300;13.002,21,40,300;14,21,30,500"));
            Check(r1.ExitCode == 0 && Val(r1, "id") == "river-1", "湖的出口 river-1：" + string.Join(" ", r1.Lines));
            // ④ 支流 river-2：從 (13.5,22.5) 流到主流第 3→4 點之間（主流那裡約 35 m；支流 80 m ⇒ 匯入瀑布 45 m）
            var r2 = Run(("op", "water"), ("sub", "river"), ("persona", "b"), ("name", "支流"), ("to", "river:river-1"),
                         ("points", "13.5,22.5,200,50;13.5,21,80,80"));
            Check(r2.ExitCode == 0, "支流 river-2：" + string.Join(" ", r2.Lines));
            // ⑤ 入湖 river-3：50 m 流進 deep（-40）⇒ 入湖瀑布 90 m
            Check(Run(("op", "water"), ("sub", "river"), ("persona", "b"), ("to", "lake:deep"), ("points", "-3,30.5,60,30;-4.2,30.5,50,30")).ExitCode == 0, "入湖 river-3");

            var ck = Run(("op", "water"), ("sub", "check"));
            readings.Add($"check：湖 {Val(ck, "lakes")}、河 {Val(ck, "rivers")}、支流 {Val(ck, "tributaries")}、瀑布 {Val(ck, "falls")}、問題 {Val(ck, "problems")}");
            Check(Val(ck, "lakes") == "2" && Val(ck, "rivers") == "3" && Val(ck, "tributaries") == "1" && Val(ck, "problems") == "0", "水系讀數");
            SCP_GlobeState s2 = store.Load();
            var aKinds = string.Join(",", s2.WaterReport.Falls.Select(f => f.River + ":" + f.Kind).OrderBy(x => x, StringComparer.Ordinal));
            readings.Add("瀑布 " + aKinds);
            Check(aKinds == "river-1:sea,river-1:segment,river-2:junction,river-3:lake", "四種瀑布各一座");
            var jf = s2.WaterReport.Falls.FirstOrDefault(f => f.Kind == "junction");
            Check(jf != null && jf.Drop > 40 && jf.Drop < 50, "匯入瀑布落差照主流在接點的內插水面算（約 45 m）：" + jf?.Drop);
            Check(s2.Display.Get(s2.Grid.LatLonToCell(13, 21)) == SCP_GlobeWaterRaster.FallColor, "河身瀑布崖口那一格有瀑布記號");
            Check(WaterAt(13.5, 22) == "river-2" && WaterAt(13.5, 23.5) == "", "支流在河道上、旁邊不是");

            // ⑥ 拒絕：每一種都零寫入
            int aBefore = WaterEvents();
            foreach (var (what, args) in new (string, (string, string)[])[]
            {
                ("往上游流", new[] { ("sub", "river"), ("persona", "c"), ("points", "20,20,10,50;20,21,30,50") }),
                ("接到不存在的湖", new[] { ("sub", "river"), ("persona", "c"), ("to", "lake:nope"), ("points", "20,20,30,50;20,21,10,50") }),
                ("出口水面不等於湖面", new[] { ("sub", "river"), ("persona", "c"), ("from", "lake:lake-1"), ("points", "12,20.5,90,50;14,20.5,10,50") }),
                ("支流離主流太遠", new[] { ("sub", "river"), ("persona", "c"), ("to", "river:river-1"), ("points", "20,30,100,50;20,31,90,50") }),
                ("比主流低流不進去", new[] { ("sub", "river"), ("persona", "c"), ("to", "river:river-1"), ("points", "13.5,23,20,50;13.5,21,10,50") }),
                ("源頭格式壞", new[] { ("sub", "river"), ("persona", "c"), ("from", "lake"), ("points", "20,20,30,50;20,21,10,50") }),
                ("刪掉被河接著的湖", new[] { ("sub", "delete"), ("persona", "c"), ("id", "lake-1") }),
                ("湖少給海拔", new[] { ("sub", "lake"), ("persona", "c"), ("points", "1,1;1,2;2,2") }),
                ("湖改成河", new[] { ("sub", "river"), ("persona", "c"), ("id", "lake-1"), ("points", "1,1,1,1;1,2,0,1") }),
            })
            {
                var d = new List<(string, string)> { ("op", "water") };
                d.AddRange(args);
                var rr = Run(d.ToArray());
                Check(rr.ExitCode == 2, $"{what} ⇒ exit 2（實際 {rr.ExitCode}：{string.Join(" ", rr.Lines)}）");
            }
            Check(WaterEvents() == aBefore, $"拒絕的每一筆都零寫入（{aBefore} → {WaterEvents()}）");

            // ⑦ 繞成環：river-4、river-5 互為支流（平的，高度一樣）——第二步要拒絕
            Check(Run(("op", "water"), ("sub", "river"), ("persona", "c"), ("id", "r4"), ("points", "0,0,50,50;0,2,50,50")).ExitCode == 0
                  && Run(("op", "water"), ("sub", "river"), ("persona", "c"), ("id", "r5"), ("to", "river:r4"), ("points", "0.1,2,50,50;0,1,50,50")).ExitCode == 0, "r4 入海、r5 匯入 r4");
            int aBeforeCycle = WaterEvents();
            var cyc = Run(("op", "water"), ("sub", "river"), ("persona", "c"), ("id", "r4"), ("to", "river:r5"));
            Check(cyc.ExitCode == 2 && cyc.Lines.Exists(l => l.Contains("繞成環")) && WaterEvents() == aBeforeCycle, "r4 改成匯入 r5 ⇒ 繞成環、拒絕、零寫入：" + string.Join(" ", cyc.Lines));

            // ⑧ 水體 Undo 與格子 Undo 分開
            Run(("op", "point"), ("persona", "a"), ("lat", "-30"), ("lon", "-30"), ("color", "#AA0000"));
            var wu = Run(("op", "water"), ("sub", "undo"), ("persona", "c"));
            SCP_GlobeState s3 = store.Load();
            Check(wu.ExitCode == 0 && !s3.Water.Features.ContainsKey("r5") && s3.Cells.Get(s3.Grid.LatLonToCell(-30, -30)) == 0xAA0000, "水體 undo 退掉 r5、格子那一筆還在");
            Run(("op", "undo"), ("persona", "a"));
            SCP_GlobeState s4 = store.Load();
            Check(s4.Water.Features.ContainsKey("r4") && s4.Cells.Get(s4.Grid.LatLonToCell(-30, -30)) == 0 && s4.Water.Count == 6, "格子 undo 退掉那一點、水體不動：" + s4.Water.Count);
            Check(Run(("op", "water"), ("sub", "river"), ("persona", "c"), ("id", "r5"), ("to", "river:r4"), ("points", "0.1,2,50,50;0,1,50,50")).ExitCode == 0
                  && store.Load().Water.UsedIds.Contains("r5"), "退掉的 id 可以再用");

            // ⑨ 門檻：落差門檻拉到 100 m ⇒ 只剩落差 ≥ 100 的（沒有）
            var cf = Run(("op", "water"), ("sub", "config"), ("persona", "a"), ("fall_drop", "100"));
            Check(cf.ExitCode == 0 && Val(cf, "falls") == "0", "落差門檻 100 m ⇒ 瀑布 0：" + Val(cf, "falls"));
            Run(("op", "water"), ("sub", "config"), ("persona", "a"), ("fall_drop", "10"));
            Check(store.Load().WaterReport.Falls.Count == 5, "門檻改回 10 m ⇒ 五座（r4 入海那一座 50 m 也算；r5 匯入落差 0 不算）：" + store.Load().WaterReport.Falls.Count);

            // ⑩ render：有水體與沒有不一樣；刪光水體之後跟最初逐位元組相同
            Run(("op", "render"), ("center", "11,21"), ("zoom", "6"), ("size", "200"), ("graticule", "0"), ("out", p1));
            Check(!File.ReadAllBytes(p0).SequenceEqual(File.ReadAllBytes(p1)), "有水體 ⇒ 輸出變了");
            foreach (string id in new[] { "r5", "r4", "river-3", "river-2", "river-1", "deep", "lake-1" })
                Check(Run(("op", "water"), ("sub", "delete"), ("persona", "a"), ("id", id)).ExitCode == 0, "刪 " + id);
            Run(("op", "render"), ("center", "11,21"), ("zoom", "6"), ("size", "200"), ("graticule", "0"), ("out", p2));
            Check(File.ReadAllBytes(p0).SequenceEqual(File.ReadAllBytes(p2)), "水體刪光 ⇒ 輸出跟最初逐位元組相同");

            // ⑪ regrid：水體是向量，照新的 N 柵格化（湖裡那一點仍是湖）
            Check(Run(("op", "water"), ("sub", "undo"), ("persona", "a")).ExitCode == 0, "undo 把 lake-1 拿回來");
            Run(("op", "regrid"), ("persona", "a"), ("confirm", "1"));
            SCP_GlobeState s5 = store.Load();
            Check(s5.Grid.N == 1024 && WaterAt(10.4, 20.4) == "lake-1", "regrid 之後湖照新 N 畫");

            var st = Run(("op", "status"));
            Check(Val(st, "water") == "1" && st.Lines.Exists(l => l.Contains("水體：湖 1")), "status 印水體讀數");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { try { Directory.Delete(aData, true); } catch { } }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }
}
