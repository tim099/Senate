// 區塊職責：**安裝管理頁**（TASK-0375）—— 看每個模型／套件在選定環境裡的狀態，安裝、解除安裝、建 venv。
// 物理意義：項目來自清單、動作是通用的：
//           跟 `senate cmd install` 是**同一套實作**（InstallProbe／InstallPlanner／InstallRunner），本頁只排版。
// 數值影響：量狀態純讀；安裝／解除安裝／建 venv 都是**兩段式**：第一顆鈕只「上膛」並把計畫攤開，第二顆才動手。
//           動手前在背景**重新量一次、重新判一次能不能做**（上膛到確認之間，環境可能被別人改了）。
// ⚠ 路徑（Python 環境／模型位置）**本頁不存**：一律讀「路徑管理」頁那兩格（PythonEnvRoot／ModelsRoot）。
// ⚠ 量一次要 ~40 秒（要 import torch）⇒ 量與動手都跑背景 job；不會重畫的宿主（文字模式）同步等它。
using Senate.Core;
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed class InstallPage : SCP_GuiToolPage
{
    readonly SenateModel m_Model;

    InstallCatalog? m_Catalog;
    string? m_CatalogError;
    InstallEnv? m_Env;
    List<InstallItemStatus>? m_Status;

    /// <summary>每個模型的下載暫存檔（量狀態時一起算好；不在每一幀列目錄）。</summary>
    Dictionary<string, (int Count, long Bytes)> m_Partial = new();

    InstallJob<List<InstallItemStatus>>? m_ProbeJob;
    InstallJob<string?>? m_ActionJob;
    string? m_LastActionResult;

    /// <summary>上膛中的動作（null ＝ 沒有）：種類＋項目＋要攤給人看的計畫。</summary>
    (string Kind, string Id, List<string> Plan)? m_Armed;

    public InstallPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public const string PageKey = "install";
    public override string Title => "安裝管理";
    public override string? MenuGroup => "工具";

    public override void OnPush() { base.OnPush(); Reload(); }

    void Reload()
    {
        m_Catalog = InstallCatalog.Load(InstallCatalog.DefaultPath(m_Model.RepoRoot), out m_CatalogError);
        SenateConfig? aCfg = null;
        try { aCfg = SenateConfig.Load(SenateConfig.DefaultPath(m_Model.RepoRoot)); }
        catch (InvalidDataException e) { m_CatalogError = (m_CatalogError ?? "") + "\n設定檔讀不了：" + e.Message; }
        m_Env = InstallEnv.Resolve(aCfg);
        m_Status = null;
        m_Armed = null;
        if (m_Catalog == null) return;
        InstallCatalog aCat = m_Catalog;
        InstallEnv aEnv = m_Env;
        m_ProbeJob = new InstallJob<List<InstallItemStatus>>("量狀態", _ => InstallProbe.ProbeAll(aEnv, aCat));
        m_ProbeJob.Start();
        if (!SCP_GuiHost.RedrawsContinuously) { m_ProbeJob.WaitForExit(); Harvest(); }
    }

    /// <summary>UI 執行緒把背景結果搬進頁面（契約③）。</summary>
    void Harvest()
    {
        if (m_ProbeJob != null)
        {
            var s = m_ProbeJob.Snapshot();
            if (s.Finished)
            {
                m_Status = s.Result;
                m_Partial = new();
                if (m_Status != null && m_Env != null)
                    foreach (InstallItemStatus st in m_Status)
                    {
                        var aFiles = InstallRunner.PartialFiles(m_Env, st.Item);
                        if (aFiles.Count > 0) m_Partial[st.Item.Id] = (aFiles.Count, aFiles.Sum(f => f.Length));
                    }
                if (s.Error != null) m_LastActionResult = "⚠ 量狀態失敗：" + s.Error;
                m_ProbeJob = null;
            }
        }
        if (m_ActionJob != null)
        {
            var s = m_ActionJob.Snapshot();
            if (s.Finished)
            {
                m_LastActionResult = s.Error != null ? "✗ " + s.Error : (s.Result ?? $"✓ {s.Label} 完成");
                m_ActionJob = null;
                Reload();   // 動完一律重新量 —— 畫面上的狀態要是「動完之後量到的」，不是「以為會變成的」
            }
        }
    }

    bool Busy => m_ProbeJob != null || m_ActionJob != null;

    protected override void TopBarButtons(SCP_Ui g)
    {
        if (!Busy && g.Button("重新量", "install/reload")) { m_LastActionResult = null; Reload(); }
    }

    protected override void DrawContent(SCP_Ui g)
    {
        Harvest();
        if (m_Catalog == null) { g.Note("⚠ " + (m_CatalogError ?? "清單讀不了")); return; }
        InstallEnv aEnv = m_Env!;

        using (g.Box("環境", "install/env"))
        {
            g.Label("Python：" + (aEnv.PythonExe ?? "（找不到）") + "　（" + aEnv.PythonOrigin + (aEnv.IsVenv ? "，venv" : "") + "）");
            if (aEnv.PythonError != null) g.Note("⚠ " + aEnv.PythonError);
            g.Label("模型位置（HF_HOME）：" + aEnv.ModelsRoot + "　（" + aEnv.ModelsOrigin + "）");
            g.Note("這兩格在「路徑管理」頁改（PythonEnvRoot／ModelsRoot）—— 空白＝用預設；本頁不存路徑。");
            if (aEnv.CreatableEnvDir != null && !Busy && m_Armed == null
                && g.Button("在這裡建一份 venv", "install/create-env"))
                m_Armed = ("create_env", "", new List<string> { $"在 {aEnv.CreatableEnvDir} 建一份 venv（用系統 Python 當基底；建完是空的，套件要另外裝）" });
        }

        if (m_LastActionResult != null) g.Note(m_LastActionResult);
        if (m_Armed != null) DrawConfirm(g, aEnv);
        if (m_ActionJob != null) DrawProgress(g, m_ActionJob.Snapshot());

        if (m_Status == null)
        {
            g.Note(m_ProbeJob != null ? "⏳ 量狀態中（要 import torch，大約 40 秒）…" : "（還沒量）");
            return;
        }

        using (g.Table("動作", "暫存檔", "項目", "名稱", "狀態", "版本", "大小", "說明"))
        {
            foreach (InstallItemStatus s in m_Status)
            {
                using (g.TableRowScope())
                {
                    DrawActionCell(g, s);
                    DrawPartialCell(g, s);
                    // ⚠ 格子裡不放換行：文字版表格以「一列一行」對齊，換行會把後面幾格推到下一行去。
                    g.TableCell(s.Item.Id);
                    g.TableCell(s.Item.Name);
                    g.TableCell(InstallItemStatus.StateText(s.State));
                    g.TableCell(s.Version.Length > 0 ? s.Version : "—");
                    g.TableCell(s.SizeBytes > 0 ? InstallProbe.FormatSize(s.SizeBytes) : $"約 {s.Item.SizeMb} MB");
                    g.TableCell(s.Detail.Length > 0 ? s.Detail : s.Item.Desc);
                }
            }
        }
        g.Note($"清單：{m_Catalog.Path}（新增項目只改清單）　CLI：`senate cmd install`（同一套實作）");
    }

    void DrawActionCell(SCP_Ui g, InstallItemStatus s)
    {
        // ⚠ 一格只能放一個節點 ⇒ 依狀態只給一顆鈕；忙的時候畫成字（不給按）。
        if (Busy || m_Armed != null) { g.TableCell("—"); return; }
        string aId = s.Item.Id;
        switch (s.State)
        {
            case InstallState.Installed:
                if (g.Button("解除安裝", $"install/uninstall/{aId}")) ArmUninstall(aId);
                break;
            case InstallState.Missing:
                if (g.Button("安裝", $"install/install/{aId}")) ArmInstall(aId);
                break;
            case InstallState.Broken:
                if (g.Button("重裝", $"install/install/{aId}")) ArmInstall(aId);
                break;
            case InstallState.Partial:
                // 先從斷點接續暫存檔、再補齊其他檔（InstallRunner.Install 會先做 ResumePartial）
                if (g.Button("接續下載", $"install/install/{aId}")) ArmInstall(aId);
                break;
            default:
                g.TableCell("量不到");   // ⛔ 不知道有沒有，就不給裝也不給拆
                break;
        }
    }

    void ArmInstall(string iId)
    {
        InstallPlan aPlan = InstallPlanner.PlanInstall(m_Catalog!, m_Status!, new[] { iId });
        var aLines = new List<string>();
        if (aPlan.Blocked.Count > 0) { m_LastActionResult = "⛔ 不能裝：" + string.Join("；", aPlan.Blocked); return; }
        aLines.Add($"會照順序裝 {aPlan.Steps.Count} 項（含相依），合計約 {aPlan.SizeMb} MB：");
        foreach (InstallItemStatus s in aPlan.Steps)
            aLines.Add($"  · {s.Item.Name}（{s.Item.Id}）約 {s.Item.SizeMb} MB，來源 {s.Item.Source}，裝到 "
                       + (s.Item.Kind == InstallKind.HfModel ? InstallProbe.ModelDir(m_Env!, s.Item) : m_Env!.PythonExe));
        m_Armed = ("install", iId, aLines);
        m_LastActionResult = null;
    }

    void ArmUninstall(string iId)
    {
        string? aWhy = InstallPlanner.CheckUninstall(m_Catalog!, m_Env!, m_Status!, iId);
        if (aWhy != null) { m_LastActionResult = "⛔ 不能拆：" + aWhy; return; }
        InstallItem aItem = m_Catalog!.Find(iId)!;
        var aLines = new List<string>
        {
            aItem.Kind == InstallKind.HfModel
                ? $"刪除模型目錄 {InstallProbe.ModelDir(m_Env!, aItem)}（⚠ 不可復原，重裝要重新下載約 {aItem.SizeMb} MB）"
                : $"從 {m_Env!.PythonExe} 移除 {string.Join("、", aItem.Dists)}",
        };
        if (aItem.Shared) aLines.Add("⚠ 這是共用的地基：清單上沒有已安裝的項目需要它，但清單外的程式可能會用。");
        m_Armed = ("uninstall", iId, aLines);
        m_LastActionResult = null;
    }

    // 下載中斷留下的 .incomplete（Tim 2026-10-02：殘留要能刪除或接續下載）。
    // 接續下載走「動作」那一格（不完整時）；這一格只負責清掉 —— 裝好之後留下的孤兒暫存檔只會佔空間。
    void DrawPartialCell(SCP_Ui g, InstallItemStatus s)
    {
        if (!m_Partial.TryGetValue(s.Item.Id, out var p)) { g.TableCell("—"); return; }
        if (Busy || m_Armed != null) { g.TableCell($"{p.Count} 個（{InstallProbe.FormatSize(p.Bytes)}）"); return; }
        if (g.Button($"清掉暫存檔（{InstallProbe.FormatSize(p.Bytes)}）", $"install/clean-partial/{s.Item.Id}"))
        {
            var aLines = new List<string> { $"刪掉 {s.Item.Id} 的 {p.Count} 個下載暫存檔（{InstallProbe.FormatSize(p.Bytes)}）—— 正在被寫入的會跳過" };
            aLines.Add(s.State == InstallState.Installed
                ? "模型已經裝好了，這些是上次中斷留下的孤兒，刪了不影響使用。"
                : "⚠ 模型還沒裝好：刪了之後要從頭下載（不刪的話，按「接續下載」會從斷點接著下）。");
            m_Armed = ("clean_partial", s.Item.Id, aLines);
            m_LastActionResult = null;
        }
    }

    void DrawConfirm(SCP_Ui g, InstallEnv iEnv)
    {
        var (aKind, aId, aPlan) = m_Armed!.Value;
        string aTitle = aKind switch
        {
            "install" => $"確認安裝 {aId}",
            "uninstall" => $"確認解除安裝 {aId}",
            "clean_partial" => $"確認清掉 {aId} 的暫存檔",
            _ => "確認建 venv",
        };
        using (g.Box(aTitle, "install/confirm"))
        {
            foreach (string l in aPlan) g.Note(l);
            g.Note("按下之後會先重新量一次、重新判一次能不能做；條件變了就不動。");
            bool aGo = false, aCancel = false;
            using (g.Row())
            {
                if (g.Button(aKind == "uninstall" ? "確認解除安裝" : "確認", "install/confirm/yes")) aGo = true;
                if (g.Button("取消", "install/confirm/no")) aCancel = true;
            }
            if (aCancel) { m_Armed = null; m_LastActionResult = "・已取消（沒有動任何東西）"; return; }
            if (!aGo) return;
        }
        m_Armed = null;
        StartAction(aKind, aId, iEnv);
    }

    void StartAction(string iKind, string iId, InstallEnv iEnv)
    {
        InstallCatalog aCat = m_Catalog!;
        Func<Action<string>, string?> aWork = iKind switch
        {
            "install" => log =>
            {
                // 動手前重新量、重新排 —— 上膛之後別人可能已經裝好或拆掉了相依。
                InstallPlan aPlan = InstallPlanner.PlanInstall(aCat, InstallProbe.ProbeAll(iEnv, aCat), new[] { iId });
                if (aPlan.Blocked.Count > 0) return "⛔ 重新量之後擋下（零變動）：" + string.Join("；", aPlan.Blocked);
                if (aPlan.Steps.Count == 0) return "・重新量之後發現已經裝好了，沒有要做的";
                int aOk = 0;
                foreach (InstallItemStatus s in aPlan.Steps)
                {
                    log($"── 安裝 {s.Item.Id} ──");
                    string? e = InstallRunner.Install(iEnv, s.Item, log);
                    if (e != null) return $"✗ {s.Item.Id} 沒裝成功（已完成 {aOk}／{aPlan.Steps.Count}）：{e}";
                    aOk++;
                }
                return $"✓ 裝好了 {aOk} 項（每一項都重新量過）";
            },
            "uninstall" => log =>
            {
                string? aWhy = InstallPlanner.CheckUninstall(aCat, iEnv, InstallProbe.ProbeAll(iEnv, aCat), iId);
                if (aWhy != null) return "⛔ 重新量之後擋下（零變動）：" + aWhy;
                string? e = InstallRunner.Uninstall(iEnv, aCat.Find(iId)!, log);
                return e != null ? "✗ " + e : $"✓ {iId} 已解除安裝（重新量過）";
            },
            "clean_partial" => log =>
            {
                long aFreed = InstallRunner.CleanPartial(iEnv, aCat.Find(iId)!, log);
                return $"✓ 清掉 {InstallProbe.FormatSize(aFreed)}（正在被寫入的會跳過，見上方紀錄）";
            },
            _ => log =>
            {
                if (iEnv.CreatableEnvDir == null) return "⛔ 沒有可以建 venv 的位置";
                string? e = InstallRunner.CreateVenv(iEnv.CreatableEnvDir, log);
                return e != null ? "✗ " + e : "✓ venv 建好了";
            },
        };
        string aLabel = iKind switch { "install" => "安裝 " + iId, "uninstall" => "解除安裝 " + iId, "clean_partial" => "清暫存檔 " + iId, _ => "建 venv" };
        m_ActionJob = new InstallJob<string?>(aLabel, aWork);
        m_ActionJob.Start();
        if (!SCP_GuiHost.RedrawsContinuously) { m_ActionJob.WaitForExit(); Harvest(); }
    }

    static void DrawProgress(SCP_Ui g, InstallJobProgress<string?> iSnap)
    {
        using (g.Box($"⏳ {iSnap.Label} —— 執行中（不能中途取消：pip 裝到一半被砍會留下半個套件）", "install/progress"))
        {
            int aFrom = Math.Max(0, iSnap.Log.Count - 15);
            for (int i = aFrom; i < iSnap.Log.Count; i++) g.Label(iSnap.Log[i]);
            if (iSnap.Log.Count == 0) g.Note("（還沒有輸出）");
        }
    }
}
