// 區塊職責：TASK-0479 的淨室驗收 —— 個人作品尺寸上限參數化（設定 `sculpture.workMaxAxis`）＋渲染網格拿掉 64M 外框上限。
// 物理意義：① 上限只管建立／調尺寸：調小之後既有的大作品照樣能雕、能看、能算版本；超過上限的建立被擋且零寫入、不扣費。
//          ② 網格改成稀疏分塊之後，外框 20 億格的場景要畫得出來，配置的記憶體要跟 voxel 數成正比（不是跟外框成正比）；
//             超過打包範圍要**明說**（反向對照：⛔ 不讓 key 安靜地撞在一起）。
//          這台機器建不出 GL 3.3 ⇒ ② 是 Skipped（⛔ 不是通過）。
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;
using Senate.Core;
using Senate.Desktop;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureMaxAxisCleanRoom()
    {
        const string name = "雕刻作品尺寸上限參數化：預設 4096／設定調小只擋建立與調尺寸／既有大作品照常雕刻渲染算版本／超限零寫入不扣費／共用展區仍 256（淨室，TASK-0479）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            var gate = new SculptProbeGateway { Vouchers = 100 };
            SCP_CanvasGatewayHost.Factory = _ => gate;
            var renderer = new SculptProbeRenderer();
            SCP_SculptRenderers.Register(renderer);
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            string repo = room.Root + "/repo";
            Directory.CreateDirectory(repo);
            SCP_CmdResult Create(string id, string size, string? repoRoot) => repoRoot == null
                ? room.Run(("op", "work"), ("sub", "create"), ("id", id), ("title", "船"), ("persona", "p"), ("account", "acct"), ("size", size))
                : room.Run(("op", "work"), ("sub", "create"), ("id", id), ("title", "船"), ("persona", "p"), ("account", "acct"), ("size", size), ("repo_root", repoRoot));
            SCP_CmdResult Box(string work, int x) => room.Run(("op", "box"), ("persona", "p"), ("work", work), ("x1", x.ToString()), ("x2", x.ToString()),
                ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "19"));

            // ① 沒設過 ⇒ 預設 4096；超過 256 的作品建得起來、邊緣那一格雕得下去
            var big = Create("ship", "300,40,60", repo);
            Check(big.ExitCode == 0 && store.Load("ship").SizeX == 300, "預設上限下建 300 格寬的作品");
            Check(SculptRoom.V(big, "work_max_axis") == "4096" && SculptRoom.V(big, "work_max_axis_source").Contains("沒設過"), "沒設過 ⇒ 4096 且來源說「沒設過」");
            Check(Box("ship", 299).ExitCode == 0 && Box("ship", 300).ExitCode == 2, "x=299 雕得下、x=300 越界被擋");
            readings.Add("預設：" + SculptRoom.V(big, "work_max_axis") + "（" + SculptRoom.V(big, "work_max_axis_source") + "）");

            // ①b 座標 ≥256 的 workedit 事件要重播得回來（審查：重播端原本寫死 0..255 ⇒ move／undo／redo 或刪快取後整件打不開）
            SCP_SculptSpace Space() => store.Engine(store.Load("ship"), room.Data).LoadSpace();
            var move = room.Run(("op", "work"), ("sub", "move"), ("work", "ship"), ("persona", "p"), ("region", "299..299,0..0,0..0"), ("delta", "0,1,0"));
            Check(move.ExitCode == 0 && Space().Voxels.Get(299, 1, 0) != 0, "x=299 的 voxel 移得動");
            File.Delete(store.SpacePaths("ship").CacheFile);
            var rebuilt = room.Run(("op", "work"), ("sub", "show"), ("work", "ship"));
            Check(rebuilt.ExitCode == 0 && SculptRoom.V(rebuilt, "total_voxels") == "1" && Space().Voxels.Get(299, 1, 0) != 0, "刪快取後從事件重建（含 x≥256 的 workedit）照樣打得開");
            var undo = room.Run(("op", "work"), ("sub", "undo"), ("work", "ship"), ("persona", "p"));
            Check(undo.ExitCode == 0 && Space().Voxels.Get(299, 0, 0) != 0 && Space().Voxels.Get(299, 1, 0) == 0, "undo 回到 (299,0,0)");
            var redo = room.Run(("op", "work"), ("sub", "redo"), ("work", "ship"), ("persona", "p"));
            Check(redo.ExitCode == 0 && Space().Voxels.Get(299, 1, 0) != 0, "redo 重播 x≥256 的 workedit");

            // ② 設定調小到 100：只擋建立與調尺寸
            var (aSaved, aSaveMsg) = SculptureWorkPrefs.SaveMaxAxis(repo, 100);
            Check(aSaved, "保存上限 100：" + aSaveMsg);
            int consumed = gate.Consumed;
            var tooBig = Create("too-big", "200", repo);
            Check(tooBig.ExitCode == 2 && !store.Exists("too-big") && gate.Consumed == consumed, "超過上限 ⇒ 建立被擋、零寫入、不扣費");
            Check(SculptRoom.V(tooBig, "work_max_axis") == "100" && SculptRoom.V(tooBig, "work_max_axis_source").Contains("sculpture.workMaxAxis"), "被擋時印出上限與來源（設定那一格）");
            Check(Create("edge", "100", repo).ExitCode == 0 && store.Load("edge").size == 100, "剛好等於上限可以建");
            // 第二輪審查：size 格式錯的錯字要講**政策上限**（100），不是結構上限 1048576；調尺寸被擋也要印出上限
            var badCreate = Create("bad-size", "0", repo);
            string badCreateText = string.Join(" ", badCreate.Lines);
            Check(badCreate.ExitCode == 2 && !store.Exists("bad-size") && badCreateText.Contains("1–100") && !badCreateText.Contains("1048576"), "建立 size=0 ⇒ 錯字講 1–100（實得：" + badCreateText + "）");
            var badUpdate = room.Run(("op", "work"), ("sub", "update"), ("work", "ship"), ("persona", "p"), ("size", "abc"), ("repo_root", repo));
            Check(badUpdate.ExitCode == 2 && SculptRoom.V(badUpdate, "work_max_axis") == "100" && string.Join(" ", badUpdate.Lines).Contains("1–100") && store.Load("ship").SizeX == 300,
                "調尺寸 size=abc ⇒ exit 2、印上限 100、錯字講 1–100、書卡不動（實得：" + string.Join(" ", badUpdate.Lines) + "）");
            var grow = room.Run(("op", "work"), ("sub", "update"), ("work", "ship"), ("persona", "p"), ("size", "400,40,60"), ("repo_root", repo));
            Check(grow.ExitCode == 2 && store.Load("ship").SizeX == 300, "調尺寸超過上限被擋、書卡不動");
            var notes = room.Run(("op", "work"), ("sub", "update"), ("work", "ship"), ("persona", "p"), ("notes", "龍骨"), ("repo_root", repo));
            Check(notes.ExitCode == 0 && store.ReadText("ship", false) == "龍骨", "只改筆記不受上限影響");
            // 審查：GUI 存筆記時一定帶著原尺寸 ⇒ 維持原尺寸、或縮小某一軸，都不受上限影響
            var same = room.Run(("op", "work"), ("sub", "update"), ("work", "ship"), ("persona", "p"), ("size", "300,40,60"), ("notes", "龍骨二"), ("repo_root", repo));
            Check(same.ExitCode == 0 && store.ReadText("ship", false) == "龍骨二", "帶原尺寸存筆記不被擋（300 > 上限 100，但沒有變大）");
            var shrink = room.Run(("op", "work"), ("sub", "update"), ("work", "ship"), ("persona", "p"), ("size", "300,30,60"), ("repo_root", repo));
            Check(shrink.ExitCode == 0 && store.Load("ship").SizeY == 30 && store.Load("ship").SizeX == 300, "縮小某一軸放行（X 300 仍超過上限，但沒有變大）");
            Check(Box("ship", 298).ExitCode == 0, "上限調小後，既有 300 格作品照樣能雕");
            var view = room.Run(("op", "view"), ("work", "ship"), ("out", room.Root + "/ship.png"));
            Check(view.ExitCode == 0 && renderer.LastVoxels == 2 && renderer.Last?.SpaceSize == 300, "上限調小後，既有作品照樣渲染（SpaceSize 300）");
            string rev = "";
            try { rev = SCP_SculptWorks.Revision(store.Engine(store.Load("ship"), room.Data).LoadSpace(), store.Load("ship").Dimensions); }
            catch (Exception e) { failures.Add("x≥256 的作品算版本丟例外：" + e.Message); }
            Check(rev.Length == 64, "x≥256 的作品算得出版本（舊版在 256 丟例外）");

            // ③ 反向對照：壞值存不進去；沒給 repo_root ⇒ 預設且明說
            var (aBadOk, _) = SculptureWorkPrefs.SaveMaxAxis(repo, 0);
            Check(!aBadOk && SculptureWorkPrefs.ResolveMaxAxis(repo, out _) == 100, "0 存不進去、原值不變");
            var noRepo = Create("no-repo", "64", null);
            Check(noRepo.ExitCode == 0 && SculptRoom.V(noRepo, "work_max_axis_source").Contains("宿主沒給 repo_root"), "沒給 repo_root ⇒ 用預設且來源明說");
            // 共用展區仍是 256³：真的落一刀（x=300 整段在 0..255 之外 ⇒ 擋下、零事件）—— 只讀建構子預設值的那種 check 永遠過（審查 #22）
            int sharedEvents = room.EventCount();
            var shared = room.Run(("op", "box"), ("persona", "p"), ("x1", "300"), ("x2", "300"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "19"), ("repo_root", repo));
            Check(shared.ExitCode == 2 && room.EventCount() == sharedEvents, "共用展區 x=300 擋下、零事件（仍是 256³，不跟作品上限走）");
            // 上限小於 64 ⇒ 沒帶 size 的建立用上限當預設（不拿一句講 size 的錯擋一個沒給 size 的人）
            Check(SculptureWorkPrefs.SaveMaxAxis(repo, 32).Ok, "保存上限 32");
            var small = room.Run(("op", "work"), ("sub", "create"), ("id", "small"), ("title", "小"), ("persona", "p"), ("account", "acct"), ("repo_root", repo));
            Check(small.ExitCode == 0 && store.Load("small").size == 32, "上限 32、沒帶 size ⇒ 建成 32³（實得 exit " + small.ExitCode + "）");
            readings.Add("調成 100 後：建 200 → exit " + tooBig.ExitCode + "、既有 300 格作品雕刻／渲染／版本照常");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }

    static CheckRow SculptLargeBboxCleanRoom()
    {
        const string name = "雕刻網格拿掉 64M 外框上限：外框 20 億格的稀疏場景畫得出來、配置記憶體跟 voxel 數成正比／逐面與合併外露面數相同／超過打包範圍明說（淨室，TASK-0479）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        using var r = new SenateSculptRenderer();
        try
        {
            // 外框 2000×1000×1000 ＝ 20 億格（舊版整塊配置要 2 GB、在 64M 擋下）；實際只有三條線 ≈ 4,000 顆
            var vox = new List<SCP_SculptVoxel>();
            for (int x = 0; x < 2000; x++) vox.Add(new SCP_SculptVoxel(x, 0, 0, 228));
            for (int z = 1; z < 1000; z++) vox.Add(new SCP_SculptVoxel(1999, 999, z, 37));
            for (int y = 0; y < 1000; y++) vox.Add(new SCP_SculptVoxel(1000, y, 500, 146));
            var p = new SCP_SculptRenderParams { Width = 256, Height = 256, AmbientOcclusion = true, Shadow = false, Skybox = SCP_SculptRenderParams.SkyboxNone };
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            if (!r.TryRender(vox, p, out _, out string err))
                return err.Contains("建不出") ? new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped)
                                              : new CheckRow(name, "外框 20 億格的場景畫不出來：" + err, CheckResult.Fail);
            long aAlloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            var r0 = r.LastReading;
            // 三條互不相接的直線：每條 N 顆 ⇒ 4N＋2 個外露面
            int aExpectFaces = (4 * 2000 + 2) + (4 * 999 + 2) + (4 * 1000 + 2);
            Check(r0.Voxels == vox.Count && r0.Faces == aExpectFaces, $"外露面 {r0.Faces:N0}（期望 {aExpectFaces:N0}）、voxel {r0.Voxels:N0}");
            Check(aAlloc < 256L * 1024 * 1024, $"配置 {aAlloc / 1048576.0:0.0} MB（上限 256 MB；舊版整塊配置要 2,000 MB）");
            readings.Add($"外框 2000×1000×1000、{vox.Count:N0} voxel：外露面 {r0.Faces:N0}、配置 {aAlloc / 1048576.0:0.0} MB、網格 {r0.MeshMs:0} ms");

            var pm = p.Clone(); pm.MergeFaces = true;
            Check(r.TryRender(vox, pm, out _, out err), "合併畫法失敗：" + err);
            var r1 = r.LastReading;
            Check(r1.Faces == r0.Faces && r1.Quads < r0.Quads, $"合併：外露面 {r1.Faces:N0}（同逐面）併成 {r1.Quads:N0} 塊");
            readings.Add($"合併 {r1.Faces:N0} 面 → {r1.Quads:N0} 塊");

            // 反向對照：任一軸跨度超過打包範圍（2,097,152 格）⇒ 明說失敗，不出圖
            var far = new List<SCP_SculptVoxel> { new(0, 0, 0, 19), new(2_200_000, 0, 0, 19) };
            bool aFarOk = r.TryRender(far, p, out _, out string aFarErr);
            Check(!aFarOk && aFarErr.Contains("超過"), "跨度超過打包範圍要明說失敗（實得：" + (aFarOk ? "成功" : aFarErr) + "）");
            readings.Add("反向對照（x 跨 2,200,000）：" + (aFarOk ? "成功（✗）" : "擋下"));

            // 合併畫法（觀測頁用的那條）：同一層兩顆 voxel 對角相距 6000 格 ⇒ 那一層外露範圍 6001²（3,600 萬格）。
            // 審查：遮罩原本照整層範圍配一張 int ⇒ 144 MB，再大就溢位；現在每邊封頂 2048、切磚各自併
            var diag = new List<SCP_SculptVoxel> { new(0, 0, 0, 19), new(6000, 6000, 0, 37) };
            var pd = p.Clone(); pd.MergeFaces = true;
            long b0 = GC.GetAllocatedBytesForCurrentThread();
            bool aDiagOk = r.TryRender(diag, pd, out _, out string aDiagErr);
            long aDiagAlloc = GC.GetAllocatedBytesForCurrentThread() - b0;
            Check(aDiagOk, "6001² 的稀疏層合併畫法失敗：" + aDiagErr);
            Check(aDiagOk && r.LastReading.Faces == 12 && r.LastReading.Quads == 12, $"兩顆孤立 voxel ⇒ 12 面 12 塊（實得 {r.LastReading.Faces}/{r.LastReading.Quads}）");
            Check(aDiagAlloc < 64L * 1024 * 1024, $"合併畫法配置 {aDiagAlloc / 1048576.0:0.0} MB（上限 64 MB；整層一張遮罩要 144 MB）");
            readings.Add($"合併畫法 6001² 稀疏層：配置 {aDiagAlloc / 1048576.0:0.0} MB");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }
}
