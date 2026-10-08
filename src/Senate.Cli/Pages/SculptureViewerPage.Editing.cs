// 區塊職責：個人作品的零件、移動與歷史操作介面；只送CLI，不自行改voxel或Credit。
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed partial class SculptureViewerPage
{
    void WorkEdit(SCP_Ui g, string persona, string selected, string label, Dictionary<string, string> args)
    {
        args["op"] = "work"; args["work"] = selected; args["persona"] = persona;
        Start(g, label, () =>
        {
            var result = RunCli(args);
            return new Outcome { Log = result.Log(label), Reload = true };
        });
        RequestRender(label, iForce: true);
    }
    void DrawWorkEditing(SCP_Ui g, string persona, string selected)
    {
        using var fold = g.Fold("零件組裝、移動與Undo", P + "fold/work-editing", iDefaultOpen: true);
        if (!fold.Open) return;
        g.Note("作品內操作免費。可使用其他作者的零件，來源會自動Credit；組裝為副本，原作不變。碰撞預設拒絕。");
        var options = m_Works.Where(w => w.status == "ready").Select(w => new SCP_GuiOption(w.id, w.title + "｜by " + w.owner + "｜" + w.id)).ToList();
        string source = g.Dropdown("零件來源作品", options, options.Count > 0 ? options[0].Value : "", P + "work/edit-source");
        string at = g.TextField("放入座標x,y,z（來源原點）", "0,0,0", P + "work/edit-at");
        string turn = g.Dropdown("繞Z軸旋轉", new List<SCP_GuiOption> { new("0", "0°"), new("90", "90°"), new("180", "180°"), new("270", "270°") }, "0", P + "work/edit-turn");
        bool overwrite = g.Toggle("允許覆蓋目的地既有voxel（Undo可還原）", false, P + "work/edit-overwrite");
        if (g.Button("放入零件副本（免費）", P + "btn/work-assemble"))
            WorkEdit(g, persona, selected, "放入零件", new() { ["sub"] = "assemble", ["source_work"] = source, ["at"] = at, ["turn"] = turn, ["overwrite"] = overwrite ? "1" : "0" });
        string region = g.TextField("移動選區x1..x2,y1..y2,z1..z2", "0..0,0..0,0..0", P + "work/edit-region");
        string delta = g.TextField("平移量dx,dy,dz", "1,0,0", P + "work/edit-delta");
        if (g.Button("移動選區（免費）", P + "btn/work-move"))
            WorkEdit(g, persona, selected, "移動選區", new() { ["sub"] = "move", ["region"] = region, ["delta"] = delta, ["overwrite"] = overwrite ? "1" : "0" });
        if (g.Button("Undo上一筆voxel操作", P + "btn/work-undo")) WorkEdit(g, persona, selected, "Undo", new() { ["sub"] = "undo" });
        if (g.Button("Redo", P + "btn/work-redo")) WorkEdit(g, persona, selected, "Redo", new() { ["sub"] = "redo" });
        if (g.Button("查看編輯歷史", P + "btn/work-history")) WorkEdit(g, persona, selected, "編輯歷史", new() { ["sub"] = "history" });
        g.Note("Undo/Redo保留事件歷史；新增一刀會清除Redo分支。尺寸與筆記不屬於voxel Undo。");
    }
}
