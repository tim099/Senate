// 區塊職責：**委派型 SCP_Cmd 的基底** —— 一支 `senate cmd`，工作實際由 Unity Editor 做。
// 物理意義：移植期的鷹架。有些子系統（酒館 seq 分配、錢）現在只有 Editor 那側有實作，
//           而使用者不該為此記住兩個動詞、兩套判定語意。⇒ 入口統一在 `senate cmd`，
//           底下走 AgentCommand 檔案協議派給 Editor。
//           ⚠ **這是過渡不是終局**：終局是那些子系統搬進 SCP_Core，這個基底的子類別歸零。
//             所以 PortNote 要寫「還差哪一塊」（缺口），不是寫「這支的設計是委派」。
// 數值影響：寫目標專案的 queue/trigger（Editor 端接手執行），本 process 不執行任何 handler。
//           exit code：0 成功／1 Editor 端回報失敗（含 blocked）／2 目標解析不到（用法錯）／
//           3 沒有結果（前一筆卡著 or 沒人接）—— 3 的細分走 `🔢 delegate_failure`，
//           不發明新的 exit code（腳本已經在吃 0/1/2/3 這四格）。
//
// 🩸 這個基底存在的兩個理由，都是血證：
//   ① 《無定語的成功》(2026-08-30)：委派成功的輸出跟原生的長得一模一樣 ⇒
//      「我在 CLI 上跑完了」與「Editor 替我跑完了」變成同一句話。所以每一則都印宿主定語。
//   ② 逾時讀到上一輪 (UCL 2026-08-16)：逾時時**回傳檔沒有被更新**，而它格式完整、數字合理。
//      所以順序寫死：**先確認判定，才准碰回傳檔**，而且碰的時候一起印它的 mtime。
using SCP.Core.Cmd;
using SCP.Core.Paths;

namespace Senate.Core;

public abstract class UnityDelegateCmd : SCP_Cmd
{
    /// <summary>
    /// 設定來源。**由宿主在啟動時裝上**（跟 <see cref="SCP_CmdRegistry.InvocationHint"/> 同形）——
    /// Cmd 不知道 repo 根在哪，而本層**不推導**。
    /// <para>⚠ 沒裝上時一律 fail loud，不准 fallback 到某個猜的路徑：
    /// 猜中的那次會讓人以為它本來就會找；猜錯的那次會派到另一棵資料樹上。</para>
    /// </summary>
    public static Func<(SenateConfig? Config, string Path)>? ConfigProvider;

    public sealed override SCP_CmdPortStatus PortStatus => SCP_CmdPortStatus.DelegatedToUnity;

    /// <summary>要派給 Editor 的 CmdType（UCL_Core 那側的 handler 名，例 <c>GoodMorning</c>）。</summary>
    protected abstract string UnityCmdType { get; }

    /// <summary>
    /// 成功之後補一行「走 CLI 的話下一步是什麼」。預設空 ＝ 不印。
    /// <para>⚠ 存在的理由：回傳檔裡的 <c>## next</c> 是 **Editor 端**寫的，教的是
    /// <c>run_cmd.py</c> 那條路 —— 走 CLI 的人照著打會打到另一個入口。
    /// 這裡補一行對照，**但絕不改寫回傳檔的內容**：
    /// 改寫別人的產出，就沒有人知道那份檔真正說了什麼。</para>
    /// </summary>
    protected virtual string CliNextHint => "";

    /// <summary>組要送過去的 args。⚠ 這裡只組**這一支的語意**，`persona` / 環境標記由 client 補。</summary>
    protected abstract Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs);

    /// <summary>
    /// 要走哪一條 queue 分道（＝ run_cmd.py 的 <c>--persona</c>）。預設讀 <c>persona</c> 參數。
    /// <para>⚠ 取一個沒宣告的參數名會丟例外（<see cref="SCP_CmdArgs"/> 的規矩：那是 Cmd 跟自己的
    /// 規格不同步，屬於程式錯誤）。⇒ 子類別要嘛在 ArgSpecs 宣告 <c>persona</c>，要嘛覆寫本方法。
    /// **刻意不做 try/catch 退回空字串** —— 那會讓「忘了宣告」靜默變成「走 anonymous 分道」，
    /// 而那條分道會讓全員互相阻塞（summit 2026-08-16／kiara 2026-08-17 血證）。</para>
    /// </summary>
    protected virtual string PersonaLane(SCP_CmdArgs iArgs) => iArgs.Get("persona");

    /// <summary>
    /// 每一支委派 Cmd 都有的兩個參數。子類別把自己的接在後面。
    /// <para>⚠ 沒接上就用不到 <c>project</c> / <c>timeout</c> —— 而且本基底讀它們時會丟例外。</para>
    /// </summary>
    protected static IEnumerable<SCP_CmdArgSpec> CommonSpecs()
    {
        yield return new SCP_CmdArgSpec("project",
            "派給哪個 Unity 專案（senate.local.json 的 projects[].name）。只有一個啟用專案時可省略");
        yield return new SCP_CmdArgSpec("timeout",
            "等 Editor 回應的秒數", iDefault: ((int)AgentCmdClient.DefaultWaitTimeoutSec).ToString());
    }

    public sealed override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        var aResult = new SCP_CmdResult();

        if (ConfigProvider == null)
            return SCP_CmdResult.Fail(70,
                "✗ 宿主沒有裝上設定來源（UnityDelegateCmd.ConfigProvider）——",
                "  這是程式錯誤不是用法錯：委派需要知道派給哪個專案，而本層不推導路徑。");

        (SenateConfig? aConfig, string aConfigPath) = ConfigProvider();
        UnityTargetResolution aTarget = UnityTargetResolver.Resolve(
            aConfig, aConfigPath, iArgs.Get("project"));
        if (!aTarget.Ok)
            return SCP_CmdResult.Fail(2, "✗ " + aTarget.Error, "  " + aTarget.Hint);

        UnityTarget aWhere = aTarget.Target!;
        // 定語第一行 —— 在做任何事之前就印，因為失敗訊息也要帶著它。
        aResult.Lines.Add($"⤷ 由 Unity Editor 執行 @ {aWhere.Describe()}");
        if (aWhere.SelectionNote.Length > 0) aResult.Lines.Add("· " + aWhere.SelectionNote);
        aResult.AddValue("delegate_host", "unity");
        aResult.AddValue("project", aWhere.ProjectName);
        aResult.AddValue("data_root", aWhere.DataRoot);

        string aLane = PersonaLane(iArgs);
        // queue 分道 ＝ persona。⚠ SafeQueueId 對空值／路徑穿越是**靜默**退回 anonymous，
        // 而那條分道會讓全員互相阻塞（summit 2026-08-16 兩次 ensure_idle 逾時、
        // kiara 2026-08-17 卡 120s —— 兩次的唯一線索都只是路徑裡那個字）。
        // ⇒ 不擋（有些 Cmd 本來就沒有 persona），但**一定要說出口**：
        //   靜默掉進共用分道，跟正常走自己的分道，在輸出上長得一模一樣。
        string aEffectiveLane = SCP_DataPaths.SafeQueueId(aLane);
        if (aEffectiveLane == AgentCmdClient.AnonymousQueueId)
        {
            aResult.Lines.Add(aLane.Trim().Length == 0
                ? $"⚠ 沒有 persona ⇒ 走共用分道 '{AgentCmdClient.AnonymousQueueId}'（會跟別人互相阻塞）"
                : $"⚠ persona '{aLane}' 不是合法分道名（空／含 .. ／含斜線）⇒ 退回 "
                  + $"'{AgentCmdClient.AnonymousQueueId}'（會跟別人互相阻塞）");
        }
        aResult.AddValue("queue_lane", aEffectiveLane);

        double aTimeout = ParseTimeout(iArgs, aResult);

        // ① 前一筆還卡著就別送 —— 送了會排在後面，然後兩筆一起逾時。
        if (!AgentCmdClient.EnsureIdle(aWhere.DataRoot, aLane, aTimeout, aResult.Lines.Add, out string aWhy))
        {
            aResult.ExitCode = 3;
            aResult.AddValue("delegate_failure", "queue_busy");
            aResult.Lines.Add($"✗ queue 分道 '{(aLane.Length == 0 ? AgentCmdClient.AnonymousQueueId : aLane)}' "
                              + "前一筆還沒被取走 —— 這一筆**沒有送出**。");
            if (aWhy.Length > 0) aResult.Lines.Add("  " + aWhy);
            aResult.Lines.Add("  出口：senate ucmd status（看誰卡著）／確認 Editor 的 Watcher 有開");
            return aResult;
        }

        // ② 送出
        string aCmdId;
        try
        {
            aCmdId = AgentCmdClient.Submit(aWhere.DataRoot, aLane, UnityCmdType,
                BuildUnityArgs(iArgs), aResult.Lines.Add);
        }
        catch (Exception e)
        {
            aResult.ExitCode = 3;
            aResult.AddValue("delegate_failure", "submit_failed");
            aResult.Lines.Add($"✗ 送不出去：{e.GetType().Name}: {e.Message}");
            return aResult;
        }
        aResult.AddValue("cmd_id", aCmdId);

        // ③ 等判定。stderr 那半也收進 Lines —— 宿主只有一條輸出通道，
        //    而失敗判決不能只走一邊（PS 5.1 會把 native stderr 重編碼吞掉）。
        //    iPrintOutputs:false —— 回傳檔由 AppendReport 統一經手（帶 mtime）。
        //    不關掉的話同一個檔會被印兩次，而**只有一次帶定語**。
        AgentCmdWaitResult aVerdict = AgentCmdClient.Wait(
            aWhere.DataRoot, aLane, aCmdId, aTimeout, AgentCmdClient.DefaultPollSec,
            aResult.Lines.Add, aResult.Lines.Add, iPrintOutputs: false);

        if (aVerdict == AgentCmdWaitResult.Timeout)
        {
            aResult.ExitCode = 3;
            aResult.AddValue("delegate_failure", "timeout");
            // ⛔ 這裡**刻意不去讀回傳檔**：逾時代表它沒被更新，讀到的會是上一輪的內容，
            //    而那份內容格式完整、數字合理 —— 它比沒有東西可讀危險得多。
            aResult.Lines.Add("⛔ 逾時 ⇒ 本 Cmd **不去讀回傳檔**（那份是上一輪的，而它看起來正常）。");
            return aResult;
        }

        if (aVerdict == AgentCmdWaitResult.Failed)
        {
            // Editor 端「回報失敗」含兩種：真的爆了，與**正常語意的 blocked**（例：同 persona 已在線）。
            // 兩者都不是 CLI 的故障 ⇒ exit 1（Cmd 自己回報失敗），而出口清單在回傳檔裡。
            aResult.ExitCode = 1;
            aResult.AddValue("delegate_failure", "cmd_failed");
            AppendReport(aResult, aWhere.DataRoot, aCmdId);
            // blocked 的出口清單在回傳檔裡，而那份清單是 Editor 寫的 ⇒ 一律 python 形。
            // ⚠ 這裡**不去對映**那些出口成 CLI 指令：那份清單是動態的（隨守衛列出），
            //   憑猜寫一份對照表，錯的那條印出來跟對的一模一樣。
            //   ⇒ 只說「它是哪一種形狀、去哪裡查本入口的等價物」，不代它翻譯。
            aResult.Lines.Add("⚠ 回傳檔裡的出口清單是 Editor 寫的、寫成 `run_cmd.py`／`awakening.py` 形 ——"
                              + " 本入口的等價指令查 `senate cmd`（本 CLI 不猜對映）。");
            return aResult;
        }

        AppendReport(aResult, aWhere.DataRoot, aCmdId);
        // ── 下一步：**由本 CLI 自己講，而且講的是 CLI 的指令** ─────────────
        // 物理意義：走 `senate cmd` 的人，指路牌就該是 `senate cmd`。
        //          Tim 2026-08-31 拍板：「Senate CLI 內的 Cmd 回傳值必須給 CLI 的指令，
        //          而非指向 .py 或 Unity Cmd。」
        // ⚠ 為什麼不是「改寫回傳檔」：那份檔是 Editor 的產出，改寫它就沒有人知道
        //   那份檔**真正**說了什麼（而它是所有 client 共用的）。⇒ 這裡是**覆蓋指路權**，不是改稿。
        // ⚠ 為什麼措辭從「對照」改成「這就是下一步」：舊版寫
        //   「回傳檔教的是 run_cmd.py 那條路／走 CLI 的對應下一步：…」——
        //   那把 Editor 那段擺成正文、把 CLI 擺成註腳，而讀的人照正文走。
        //   🩸 現場：calli 2026-08-31（酒館 seq 15143）照 brief §9 與 wake 回傳檔的
        //   `## next` 跑 `awakening.py consolidate`，撞 registry 退場守衛 exit 1 ——
        //   **而 digest 其實已經寫進磁碟**。那份清單沒有壞，它只是在回答一個舊問題。
        //   ⇒ 主從關係要顛倒過來：CLI 的下一步是正文，回傳檔那段標明「不適用於本入口」。
        if (CliNextHint.Length > 0)
        {
            aResult.Lines.Add("## next（本入口＝`senate cmd`，照這行走）");
            aResult.Lines.Add("   " + CliNextHint);
            aResult.Lines.Add("⚠ 回傳檔裡的 `## next` 是 Editor 端寫的、只認 `run_cmd.py`／`awakening.py`"
                              + " —— **那一段對本入口不適用**，別照它打。");
            aResult.Lines.Add("   回傳檔的其餘內容（讀數／守衛／出口清單）照讀，那些與 client 無關。");
        }
        return aResult;
    }

    /// <summary>
    /// 判定完成之後才做的事：把 result 檔的回傳檔／純量併進來，**每個回傳檔一起印 mtime**。
    /// <para>⚠ mtime 回答的是「這個檔何時被寫」，不是「內容何時產生」（2026-08-27 血證：
    /// checkout 落地時間被我讀成重出）。但在「這份是不是這一輪的」這一問上，它夠用。</para>
    /// </summary>
    static void AppendReport(SCP_CmdResult oResult, string iDataRoot, string iCmdId)
    {
        (bool aFound, List<string> aOutputs, List<KeyValuePair<string, string>> aValues) =
            AgentCmdClient.ResultReport(iDataRoot, iCmdId);
        if (!aFound)
        {
            // 「沒有 result 檔」與「有 result 檔但沒有回傳檔」是兩件事 —— 不可同形。
            oResult.Lines.Add("⚠ 沒有 result 檔（舊版 Editor／落檔失敗）⇒ 沒有回傳檔清單可以印。");
            return;
        }
        // 路徑本身走 Outputs（宿主會印 📄，那是機器可讀的那一欄）；
        // 這裡只補**定語**——同一個路徑印兩次的話，讀的人有一半機率引用到沒有 mtime 的那行。
        foreach (string aPath in aOutputs)
        {
            oResult.AddOutput(aPath);
            oResult.Lines.Add($"  ⏱ {System.IO.Path.GetFileName(aPath)}{DescribeStamp(aPath)}");
        }
        foreach (var aKv in aValues) oResult.AddValue(aKv.Key, aKv.Value);
    }

    /// <summary>回傳檔的時間戳註記；檔案不在就直說 —— 印不出時間跟「時間很舊」是兩件事。</summary>
    static string DescribeStamp(string iPath)
    {
        try
        {
            if (!File.Exists(iPath)) return "　⚠ 檔案不存在（Editor 說它寫了，而這台看不到）";
            return $"　（mtime {File.GetLastWriteTime(iPath):yyyy-MM-dd HH:mm:ss}）";
        }
        catch (Exception e) { return $"　⚠ 讀不到 mtime：{e.GetType().Name}"; }
    }

    static double ParseTimeout(SCP_CmdArgs iArgs, SCP_CmdResult oResult)
    {
        string aRaw = iArgs.Get("timeout");
        if (double.TryParse(aRaw, out double aSec) && aSec > 0) return aSec;
        // 打錯的 timeout 靜默取預設 ＝ 使用者以為自己設了 5 秒而實際等 120 秒。
        oResult.Lines.Add($"⚠ timeout='{aRaw}' 不是正數 ⇒ 用預設 {AgentCmdClient.DefaultWaitTimeoutSec:0}s");
        return AgentCmdClient.DefaultWaitTimeoutSec;
    }
}
