// 淨室驗收：真CLI建立任務與子作品，用別人的零件組房間；驗原色、碰撞、Undo/Redo、Credit與重播。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureEditingCleanRoom()
    {
        const string name = "雕刻組裝：任務子作品免費／他人零件與自動Credit／區域移動／Undo+Redo／獨立重播";
        var failures = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            foreach (string p in new[] { "p", "q" }) Directory.CreateDirectory(Path.Combine(room.Letters, p, "profile"));
            var pay = new SculptProbeGateway { Vouchers = 30 };
            SCP_SculptRenderers.Register(new SculptProbeRenderer());
            var credit = new SculptCommissionProbe();
            SCP_CanvasGatewayHost.Factory = _ => pay; SenateSculptureRewards.Factory = _ => credit;
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            Check(room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", "room"), ("title", "三床房間"), ("size", "32"), ("commission", "建造三張床的房間"), ("commission_ref", "user:room"), ("account", "acct")).ExitCode == 0, "主任務建立");
            SCP_CmdResult Child(string id, string parent = "room", string owner = "p") => room.Run(("op", "work"), ("sub", "create"), ("persona", owner), ("id", id), ("title", id), ("size", "4,3,2"), ("parent_work", parent));
            Check(Child("bed").ExitCode == 0 && Child("leg", "bed").ExitCode == 0 && pay.Consumed == 0 && credit.Total == 10, "兩層子作品免費且主薪只發一次");
            Check(Child("stolen", owner: "q").ExitCode == 2 && Child("bed").ExitCode == 2 && !store.Exists("stolen"), "他人父作品與重複ID拒絕");
            Check(room.Run(("op", "work"), ("sub", "create"), ("persona", "q"), ("id", "pillow"), ("title", "朋友的枕頭"), ("size", "2"), ("account", "acct-q")).ExitCode == 0 && pay.Consumed == 10, "另一作者建立作品");
            Check(Child("not-task", "pillow", "q").ExitCode == 2, "付費自發作品不能冒用免費任務子作品");
            SCP_CmdResult Box(string id, string p, int x1, int x2, int color) => room.Run(("op", "box"), ("work", id), ("persona", p), ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", color.ToString()));
            Check(Box("bed", "p", 0, 1, 19).ExitCode == 0 && Box("pillow", "q", 0, 0, 77).ExitCode == 0, "床與枕頭雕刻");
            SCP_CmdResult Assemble(string target, string source, string at, string owner = "p", string turn = "0", string overwrite = "0") => room.Run(("op", "work"), ("sub", "assemble"), ("work", target), ("source_work", source), ("persona", owner), ("at", at), ("turn", turn), ("overwrite", overwrite));
            SCP_CmdResult Edit(string id, string sub, string owner = "p") => room.Run(("op", "work"), ("sub", sub), ("work", id), ("persona", owner));
            SCP_SculptSpace Space(string id) => store.Engine(store.Load(id), room.Data).LoadSpace();
            var firstAssembly = Assemble("bed", "pillow", "2,0,0");
            Check(firstAssembly.ExitCode == 0 && Space("bed").Voxels.Get(2, 0, 0) == 77 && Space("pillow").Voxels.Count == 1, "組裝他人零件保留來源與原色：" + SculptRoom.All(firstAssembly));
            foreach (int x in new[] { 0, 6, 12 }) Check(Assemble("room", "bed", x + ",0,0").ExitCode == 0, "三次放入床" + x);
            Check(Space("room").Voxels.Count == 9 && SculptRoom.V(Edit("room", "show"), "credit_count") == "2", "三床9格、床與上游枕頭Credit去重");
            string show = SculptRoom.V(Edit("room", "show"), "path");
            Check(show.Length > 0 && SCP_SculptHistory.Read(store.SpacePaths("room")).Credits().Any(c => c.work == "pillow" && c.author == "q" && c.title == "朋友的枕頭"), "自動Credit保存作者作品名稱與版本");
            int count = SCP_SculptStore.ListEvents(store.SpacePaths("room")).Count;
            Check(Assemble("room", "bed", "0,0,0").ExitCode == 2 && Assemble("room", "bed", "31,0,0").ExitCode == 2 && Assemble("room", "bed", "20,0,0", "q").ExitCode == 2 && Assemble("room", "bed", "20,0,0", turn: "45").ExitCode == 2 && SCP_SculptStore.ListEvents(store.SpacePaths("room")).Count == count, "碰撞越界權限旋轉錯誤均零事件");
            for (int i = 0; i < 3; i++) Check(Edit("room", "undo").ExitCode == 0, "連續Undo" + i);
            Check(Space("room").Voxels.Count == 0 && SculptRoom.V(Edit("room", "show"), "credit_count") == "0" && Edit("room", "undo").ExitCode == 2, "Undo空間與Credit且不可超退");
            for (int i = 0; i < 3; i++) Check(Edit("room", "redo").ExitCode == 0, "連續Redo" + i);
            Check(Space("room").Voxels.Count == 9 && Space("room").Voxels.Get(14, 0, 0) == 77 && SculptRoom.V(Edit("room", "show"), "credit_count") == "2", "Redo還原顏色與Credit");
            SCP_CmdResult Move(string delta, string overwrite = "0") => room.Run(("op", "work"), ("sub", "move"), ("work", "room"), ("persona", "p"), ("region", "0..2,0..0,0..0"), ("delta", delta), ("overwrite", overwrite));
            Check(Move("1,0,0").ExitCode == 0 && Space("room").Voxels.Get(0, 0, 0) == 0 && Space("room").Voxels.Get(3, 0, 0) == 77 && Space("room").Voxels.Count == 9, "重疊選區整體平移不丟色");
            Check(Edit("room", "undo").ExitCode == 0 && Space("room").Voxels.Get(2, 0, 0) == 77, "移動Undo原色");
            count = SCP_SculptStore.ListEvents(store.SpacePaths("room")).Count;
            Check(Move("6,0,0").ExitCode == 2 && Move("-1,0,0").ExitCode == 2 && Move("2147483647,0,0").ExitCode == 2 && SCP_SculptStore.ListEvents(store.SpacePaths("room")).Count == count, "移動碰撞越界溢位均不修改");
            Check(Move("6,0,0", "1").ExitCode == 0 && Space("room").Voxels.Count == 6 && Edit("room", "undo").ExitCode == 0 && Space("room").Voxels.Count == 9, "顯式覆蓋可完整Undo");
            Check(Assemble("room", "bed", "20,0,0", turn: "90").ExitCode == 0 && Space("room").Voxels.Get(22, 2, 0) == 77 && Edit("room", "redo").ExitCode == 2, "90度旋轉與新編輯清Redo分支");
            Check(Edit("room", "undo", "q").ExitCode == 2, "他人不可Undo");
            Check(Edit("bed", "undo").ExitCode == 0 && Edit("bed", "undo").ExitCode == 0 && Space("bed").Voxels.Count == 0 && Edit("bed", "redo").ExitCode == 0 && Edit("bed", "redo").ExitCode == 0, "舊box與新組裝都可Undo/Redo");
            Check(room.Run(("op", "carve"), ("work", "bed"), ("persona", "p"), ("x1", "2"), ("x2", "2"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0")).ExitCode == 0 && Edit("bed", "undo").ExitCode == 0 && Space("bed").Voxels.Get(2, 0, 0) == 77, "舊carve Undo恢復顏色");
            Check(room.Run(("op", "carve"), ("work", "bed"), ("persona", "p"), ("x1", "2"), ("x2", "2"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0")).ExitCode == 0, "縮小前挖除外圍");
            Check(room.Run(("op", "work"), ("sub", "update"), ("work", "bed"), ("persona", "p"), ("size", "2,3,2")).ExitCode == 0 && Edit("bed", "undo").ExitCode == 2 && Space("bed").Voxels.Count == 2, "縮小後Undo越界整筆拒絕");
            Check(room.Run(("op", "work"), ("sub", "update"), ("work", "bed"), ("persona", "p"), ("size", "4,3,2")).ExitCode == 0 && Edit("bed", "undo").ExitCode == 0 && Space("bed").Voxels.Get(2, 0, 0) == 77, "擴大後可Undo還原");
            string png = Path.Combine(room.Root, "editing-stamp.png");
            File.WriteAllBytes(png, SCP.Core.Canvas.SCP_CanvasPng.EncodeRgb(new byte[] { 19, 19 }, 0, 0, 2, 1, 2));
            Check(room.Run(("op", "stampimg"), ("work", "bed"), ("persona", "p"), ("png", png), ("at", "0,1,0")).ExitCode == 0 && Space("bed").Voxels.Count == 5 && Edit("bed", "undo").ExitCode == 0 && Space("bed").Voxels.Count == 3, "既有貼圖Undo");
            var importPreview = room.Run(("op", "work"), ("sub", "import"), ("work", "bed"), ("persona", "q"), ("at", "100,100,0"));
            var imported = room.Run(("op", "work"), ("sub", "import"), ("work", "bed"), ("persona", "q"), ("at", "100,100,0"), ("confirm", "1"), ("exhibit_id", "friends-bed"), ("expect_revision", SculptRoom.V(importPreview, "revision")), ("expect_placed", SculptRoom.V(importPreview, "would_place")));
            Check(importPreview.ExitCode == 0 && imported.ExitCode == 0 && pay.Consumed == 11 && Space("bed").Voxels.Count == 3, "可將他人作品副本匯入展區，匯入者支付1單位");
            var exhibit = SCP_JsonParser.Parse(File.ReadAllText(room.Engine().Paths.ExhibitJson("friends-bed")));
            Check(exhibit.GetString("author", "") == "p" && exhibit.GetString("description", "").Contains("Credit:") && exhibit.GetString("description", "").Contains("朋友的枕頭") && exhibit.GetString("description", "").Contains("by q"), "展品自動Credit原作者與上游來源");
            int finalCount = Space("room").Voxels.Count;
            File.Delete(store.SpacePaths("room").CacheFile);
            File.Move(Path.Combine(store.Folder("bed"), "work.json"), Path.Combine(store.Folder("bed"), "work.saved"));
            Check(Space("room").Voxels.Count == finalCount && SculptRoom.V(Edit("room", "show"), "credit_count") == "2" && Space("room").Voxels.Get(22, 2, 0) == 77, "刪快取及來源書卡後仍可獨立重播與Credit");
            Check(pay.Consumed == 11 && credit.Total == 10, "除自發建立10與展區匯入1外，組裝移動UndoRedo全免費、不另領薪");
            // 固定時鐘連續落子仍需嚴格排序，否則刪快取會交換同毫秒的填與挖。
            var engine = store.Engine(store.Load("leg"), room.Data); engine.Clock = () => new DateTime(2026, 1, 1);
            engine.Box(new SCP_SculptBoxArgs { X1 = 0, X2 = 0, Y1 = 0, Y2 = 0, Z1 = 0, Z2 = 0, Color = 19 });
            engine.Carve(new SCP_SculptCarveArgs { X1 = 0, X2 = 0, Y1 = 0, Y2 = 0, Z1 = 0, Z2 = 0 });
            File.Delete(engine.Paths.CacheFile);
            Check(engine.LoadSpace().Voxels.Count == 0, "同毫秒事件與重播順序一致");
            return new CheckRow(name, failures.Count == 0 ? "三床組裝、跨作者與上游Credit、子作品免付費不重領、旋轉、重疊平移、覆蓋還原、連續Undo/Redo、來源獨立重播通過" : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
