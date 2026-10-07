// 區塊職責：個人雕刻作品完整 CLI 正反向驗收；所有空間、付款閘與渲染器都是淨室。
// 物理意義：檢查真正資料與付款次數，不以回傳 success 當唯一憑據。
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureWorksCleanRoom()
    {
        const string name = "個人雕刻作品：唯一ID／10單位／作者權限／64³／免費續作／原色副本匯入與版本閘／事件重播（TASK-0460）";
        var failures = new List<string>();
        void Check(bool condition, string what) { if (!condition) failures.Add(what); }
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            Directory.CreateDirectory(Path.Combine(room.Letters, "q", "profile"));
            var gate = new SculptProbeGateway { Vouchers = 9 };
            SCP_CanvasGatewayHost.Factory = _ => gate;
            var renderer = new SculptProbeRenderer();
            SCP_SculptRenderers.Register(renderer);
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            SCP_CmdResult Create(string id, string persona = "p") => room.Run(("op", "work"), ("sub", "create"), ("id", id), ("title", "椅子"), ("persona", persona), ("account", "acct"));
            Check(Create("chair").ExitCode == 3 && !store.Exists("chair") && gate.Consumed == 0, "餘額不足不建立不扣費");
            gate.Vouchers = 40;
            var created = Create("chair");
            Check(created.ExitCode == 0 && gate.Consumed == 10 && store.Load("chair").owner == "p" && store.Load("chair").size == 64, "建立費／書卡");
            Check(Create("CHAIR", "q").ExitCode == 2 && gate.Consumed == 10, "大小寫重複ID不扣費");
            var racers = new[] { Task.Run(() => Create("concurrent")), Task.Run(() => Create("CONCURRENT")) };
            Task.WaitAll(racers);
            Check(racers.Count(t => t.Result.ExitCode == 0) == 1 && racers.Count(t => t.Result.ExitCode == 2) == 1 && gate.Consumed == 20, "競爭建立只有一件且只扣一次");
            int creationConsumed = gate.Consumed;
            Check(Create("../escape").ExitCode == 2 && Create("con").ExitCode == 2 && gate.Consumed == creationConsumed, "路徑與保留ID拒絕");
            Check(Create("fake-owner", "nonexistent").ExitCode == 2 && !store.Exists("fake-owner"), "作者必須存在");
            Check(room.Run(("op", "work"), ("sub", "show"), ("work", "not-yet-created")).ExitCode == 2 && !store.Exists("not-yet-created"), "查不存在ID不建立幽靈目錄");
            SCP_CmdResult Box(string persona, string work, int x1, int x2, int color = 19) => room.Run(("op", "box"), ("persona", persona), ("work", work), ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", "0"), ("y2", "1"), ("z1", "0"), ("z2", "0"), ("color", color.ToString()));
            Check(Box("q", "chair", 0, 1).ExitCode == 2 && Box("p", "missing", 0, 1).ExitCode == 2 && room.EventCount() == 0, "非作者與不存在作品不落到展區");
            gate.Vouchers = 0;
            var box = Box("p", "chair", 0, 1);
            var workEngine = new SCP_SculptEngine(store.SpacePaths("chair"), room.Data, 64);
            Check(box.ExitCode == 0 && SculptRoom.V(box, "charged") == "0" && gate.Consumed == creationConsumed && workEngine.LoadSpace().Voxels.Count == 4 && room.Engine().LoadSpace().Voxels.Count == 0, "沒餘額也能免費雕刻且空間隔離");
            Check(Box("p", "chair", 63, 64).ExitCode == 2 && workEngine.LoadSpace().Voxels.Count == 4, "64邊界拒絕不裁切");
            var carve = room.Run(("op", "carve"), ("work", "chair"), ("persona", "p"), ("x1", "0"), ("x2", "0"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"));
            Check(carve.ExitCode == 0 && workEngine.LoadSpace().Voxels.Count == 3 && gate.Consumed == creationConsumed, "免費挖除");
            Box("p", "chair", 0, 0, 77);
            Check(room.Run(("op", "work"), ("sub", "update"), ("work", "chair"), ("persona", "q"), ("notes", "不該保存")).ExitCode == 2 && store.ReadText("chair", false) == "", "非作者不能改筆記");
            var notes = room.Run(("op", "work"), ("sub", "update"), ("work", "chair"), ("persona", "p"), ("notes", "四腳已完成\n下次修椅背"), ("todo", "- [ ] 椅背"));
            Check(notes.ExitCode == 0 && store.ReadText("chair", false).Contains("下次修椅背") && store.ReadText("chair", true).Contains("椅背"), "續作文字回讀");
            string picture = Path.Combine(room.Root, "chair.png");
            var view = room.Run(("op", "view"), ("work", "chair"), ("out", picture));
            Check(view.ExitCode == 0 && File.Exists(picture) && renderer.LastVoxels == 4 && renderer.Last?.SpaceSize == 64, "作品渲染同引擎且64格");
            var export = room.Run(("op", "export"), ("work", "chair"), ("format", "vox"), ("out_dir", room.Root));
            Check(export.ExitCode == 0, "作品匯出");
            var invalidPreset = room.Run(("op", "exhibit"), ("sub", "register"), ("work", "chair"), ("persona", "q"), ("id", "hack"), ("title", "hack"), ("region", "0..1,0..1,0..0"));
            Check(invalidPreset.ExitCode == 2, "作品展品設定也要作者");
            // PNG stamp 指定作品，投影在64之外不能落地；不收任何費用。
            string png = Path.Combine(room.Root, "stamp.png");
            File.WriteAllBytes(png, SCP.Core.Canvas.SCP_CanvasPng.EncodeRgb(new byte[] { 19, 19 }, 0, 0, 2, 1, 2));
            var stamp = room.Run(("op", "stampimg"), ("work", "chair"), ("persona", "p"), ("png", png), ("at", "63,2,0"));
            Check(stamp.ExitCode == 5 && workEngine.LoadSpace().Voxels.Count == 4, "作品貼圖64界線");
            // 先在展區放一顆原色，預览应跳過而且不收费。
            room.Engine().Box(new SCP_SculptBoxArgs { X1 = 11, X2 = 11, Y1 = 10, Y2 = 10, Z1 = 10, Z2 = 10, Color = 88 });
            SCP_CmdResult Preview(string at = "10,10,10") => room.Run(("op", "work"), ("sub", "import"), ("work", "chair"), ("persona", "p"), ("at", at), ("exhibit_id", "chair-show"));
            var preview = Preview();
            Check(preview.ExitCode == 0 && SculptRoom.V(preview, "would_place") == "3" && SculptRoom.V(preview, "skipped_occupied") == "1" && gate.Consumed == creationConsumed && room.Engine().LoadSpace().Voxels.Count == 1, "匯入預覽數量、衝突、不落地不扣費");
            Check(Preview("255,255,255").ExitCode == 5 && room.Engine().LoadSpace().Voxels.Count == 1, "匯入越界拒絕");
            SCP_CmdResult Import(SCP_CmdResult p) => room.Run(("op", "work"), ("sub", "import"), ("work", "chair"), ("persona", "p"), ("at", "10,10,10"), ("exhibit_id", "chair-show"), ("confirm", "1"), ("expect_revision", SculptRoom.V(p, "revision")), ("expect_placed", SculptRoom.V(p, "would_place")));
            Check(Import(preview).ExitCode == 3 && room.Engine().LoadSpace().Voxels.Count == 1, "匯入餘額不足拒絕");
            Box("p", "chair", 3, 3);
            gate.Vouchers = 10;
            Check(Import(preview).ExitCode == 5 && room.Engine().LoadSpace().Voxels.Count == 1 && gate.Consumed == creationConsumed, "來源版本更動拒絕");
            preview = Preview();
            var imported = Import(preview);
            var shared = room.Engine().LoadSpace();
            Check(imported.ExitCode == 0 && SculptRoom.V(imported, "placed") == "5" && gate.Consumed == creationConsumed + 1 && shared.Voxels.Count == 6 && shared.Voxels.Get(10, 10, 10) == 77 && shared.Voxels.Get(11, 10, 10) == 88, "實際落地計費及原色保留");
            Check(File.Exists(room.Engine().Paths.ExhibitJson("chair-show")) && File.ReadAllText(SculptRoom.V(imported, "event_file")).Contains(SculptRoom.V(preview, "revision")), "展品及來源版本保存");
            Box("p", "chair", 5, 5);
            Check(room.Engine().LoadSpace().Voxels.Count == 6, "原稿續雕不改展品副本");
            // 丟棄衍生快取，重播仍保留匯入副本，不依賴原稿當下版本。
            File.Delete(room.Engine().Paths.CacheFile);
            Check(room.Engine().LoadSpace().Voxels.Count == 6 && workEngine.LoadSpace().Voxels.Count == 8, "匯入事件可獨立重播");
            Check(Preview().ExitCode == 2 && gate.Consumed == creationConsumed + 1, "展品ID重複拒絕不扣款");
            var list = room.Run(("op", "work"), ("sub", "list"), ("persona", "p"));
            Check(list.ExitCode == 0 && SculptRoom.V(list, "count") == "2", "作品清單");
            // 跨付款渠道的未知／失敗不能啟用作品；同ID重試沿用原計畫與冪等ref。
            var mixedGate = new SculptWorkRecoveryGateway();
            SCP_CanvasGatewayHost.Factory = _ => mixedGate;
            Check(Create("recover").ExitCode == 1 && store.Load("recover", false).status == "pending" && mixedGate.VoucherPaid == 7 && mixedGate.TokenPaid == 0, "部分付款保存pending");
            Check(Box("p", "recover", 0, 1).ExitCode == 2 && !Directory.Exists(store.SpacePaths("recover").Events), "pending不能免費雕刻");
            mixedGate.FailToken = false;
            Check(Create("recover").ExitCode == 0 && store.Load("recover").status == "ready" && mixedGate.VoucherPaid == 7 && mixedGate.TokenPaid == 3, "重試只扣剩下渠道；限時永久券合併且不重扣");
            Check(Create("recover").ExitCode == 2 && mixedGate.VoucherPaid + mixedGate.TokenPaid == 10, "完成後重建拒絕");
            return new CheckRow(name, failures.Count == 0 ? "每件建立10、匯入1；競爭與部分付款恢復不重扣；免費續雕；權限／越界／版本／原色／重播／觀測通過" : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, "例外：" + e, CheckResult.Fail); }
    }

    sealed class SculptWorkRecoveryGateway : SCP_ICanvasGateway
    {
        public bool FailToken = true;
        public int VoucherPaid, TokenPaid;
        readonly HashSet<string> m_Refs = new();
        public string HostQualifier => "作品建立恢復探針";
        public SCP_CanvasTriState QueryInFreeTime(string p, out string detail) { detail = "probe"; return SCP_CanvasTriState.Yes; }
        public int QueryExpiringVouchers(string p, out string detail) { detail = "probe"; return 3; }
        public int QueryPermanentVouchers(string p, out string detail) { detail = "probe"; return 4; }
        public int QueryTavernVouchers(string p, out string detail) { detail = "probe"; return 0; }
        public long QueryTokenBalance(string a, out string detail) { detail = "probe"; return 3; }
        public SCP_CanvasGateResult ConsumeVouchers(string p, int n, string reference, string description)
        {
            if (m_Refs.Add("v:" + reference)) VoucherPaid += n;
            return SCP_CanvasGateResult.Good("receipt");
        }
        public SCP_CanvasGateResult ConsumeTavernVouchers(string p, int n, string reference, string description) => SCP_CanvasGateResult.Bad("no tavern vouchers");
        public SCP_CanvasGateResult DebitTokens(string a, int n, string kind, string reference, string description)
        {
            if (FailToken) return SCP_CanvasGateResult.Bad("probe partial payment failure");
            if (m_Refs.Add("t:" + reference)) TokenPaid += n;
            return SCP_CanvasGateResult.Good("receipt");
        }
        public SCP_CanvasGateResult Share(string p, string room, string body, string? attachment = null, string? tag = null) => SCP_CanvasGateResult.Bad("not used");
    }
}
