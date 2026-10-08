// 委託付款驗收：真正走作品CLI，銀行僅在淨室替換；檢查未知回執重试的冪等發薪。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureCommissionCleanRoom()
    {
        const string name = "雕刻委託：免費建立／即發10token／来源唯一／未知回執重試不重發（TASK-0461）";
        var failures = new List<string>();
        void Check(bool ok, string why) { if (!ok) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            Directory.CreateDirectory(Path.Combine(room.Letters, "q", "profile"));
            var pay = new SculptProbeGateway { Vouchers = 0 };
            SCP_CanvasGatewayHost.Factory = _ => pay;
            var credit = new SculptCommissionProbe();
            SenateSculptureRewards.Factory = _ => credit;
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            SCP.Core.Cmd.SCP_CmdResult Create(string id, string source = "user:table", string owner = "p", string account = "acct") => room.Run(
                ("op", "work"), ("sub", "create"), ("persona", owner), ("id", id), ("title", "桌子"),
                ("commission", "使用者指定雕刻一張桌子"), ("commission_ref", source), ("account", account), ("size", "96,48,32"));
            Check(room.Run(("op", "work"), ("sub", "create"), ("id", "incomplete"), ("persona", "p"), ("commission", "桌子")).ExitCode == 2 && !store.Exists("incomplete"), "缺來源拒絕");
            Check(Create("table", owner: "missing").ExitCode == 2 && credit.Total == 0, "不存在persona拒絕");
            var made = Create("table");
            Check(made.ExitCode == 0 && SculptRoom.V(made, "charged") == "0" && SculptRoom.V(made, "reward") == "10" && credit.Total == 10 && pay.Consumed == 0, "免費並立即發10");
            var card = store.Load("table");
            Check(card.owner == "p" && card.reward == 10 && card.account == "acct" && card.commission_ref == "user:table", "委託身分與帳戶保存");
            Check(card.Dimensions == "96,48,32", "委託可指定長方體尺寸");
            Check(Create("table").ExitCode == 2 && Create("table2").ExitCode == 2 && Create("other-owner", owner: "q").ExitCode == 2 && credit.Total == 10, "ID及委託來源全庫不重領");
            var box = room.Run(("op", "box"), ("work", "table"), ("persona", "p"), ("x1", "0"), ("x2", "1"), ("y1", "0"), ("y2", "1"), ("z1", "0"), ("z2", "0"));
            Check(box.ExitCode == 0 && pay.Consumed == 0 && room.Engine().LoadSpace().Voxels.Count == 0 && new SCP_SculptEngine(store.SpacePaths("table"), room.Data, 64).LoadSpace().Voxels.Count == 4, "委託續雕免費且隔離");
            credit.UnknownOnce = true;
            Check(Create("retry", "user:retry").ExitCode == 1 && store.Load("retry", false).status == "pending" && credit.Total == 20, "已入帳但回執未知保留pending");
            Check(Create("retry", "changed").ExitCode == 2 && Create("alias", "user:retry").ExitCode == 2, "pending不換來源不換ID領");
            Check(room.Run(("op", "work"), ("sub", "create"), ("id", "retry"), ("persona", "p"), ("size", "128")).ExitCode == 2 && credit.Total == 20, "pending改尺寸拒絕不重領");
            Check(room.Run(("op", "work"), ("sub", "create"), ("id", "retry"), ("persona", "q")).ExitCode == 2, "不可冒領pending");
            var retry = room.Run(("op", "work"), ("sub", "create"), ("id", "retry"), ("persona", "p"), ("account", "wrong-account"));
            Check(retry.ExitCode == 0 && store.Load("retry").account == "acct" && store.Load("retry").Dimensions == "96,48,32" && credit.Total == 20 && credit.LastAccount == "acct", "同ref重試不重發不改收款戶與尺寸");
            pay.Vouchers = 10;
            var paid = room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", "personal"), ("title", "自發"), ("account", "acct"));
            Check(paid.ExitCode == 0 && pay.Consumed == 10 && Create("personal", "user:old").ExitCode == 2 && credit.Total == 20, "自發費用不變、舊作品不可轉委託");
            return new CheckRow(name, failures.Count == 0 ? "建立0＋報酬10；券不動；來源與作者拒絕；未知回執同ref恢復；付費舊作品不轉委託" : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
    sealed class SculptCommissionProbe : ISculptureRewardGateway
    {
        readonly HashSet<string> m_Refs = new();
        public int Total;
        public bool UnknownOnce;
        public string LastAccount = "";
        public SCP_CanvasGateResult Credit(string account, string persona, string reference, string description)
        {
            LastAccount = account;
            if (m_Refs.Add(reference)) Total += 10;
            if (UnknownOnce) { UnknownOnce = false; return SCP_CanvasGateResult.Bad("未知回執"); }
            return SCP_CanvasGateResult.Good("收據");
        }
    }
}
