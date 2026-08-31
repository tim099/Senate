// 區塊職責：**派遣目標的解析** —— 「這一筆要送到哪個 Unity 專案的哪個資料根」只在這裡決定。
// 物理意義：CLI 的 `ucmd` 與委派型 SCP_Cmd（UnityDelegateCmd）要回答的是同一個問題。
//           放兩份的症狀**不是編譯錯**，是兩條路徑對「多專案時該不該猜」給出不同答案 ——
//           而猜錯的那一次，Cmd 會在**別人的 Editor 上真的執行**。
//           ⇒ 一個問題一個答案的地方，這裡就是那個地方。
// 數值影響：純讀設定 ＋ 一次資料根解析（ProjectProbe），零寫入。
//           找不到／不只一個啟用 ⇒ 回錯誤與提示，**不猜**（猜的代價見上）。
// ⚠ SelectionNote 不是裝飾：它是「這一筆送到哪一台」唯一的證據。
//   一句沒有定語的成功訊息，是一個沒有人聽得見的謊（2026-08-30 血證：「移除 1 個」—— 在哪裡？）。
using System.Linq;

namespace Senate.Core;

/// <summary>一個已經解析完成的派遣目標。**只能由 <see cref="UnityTargetResolver"/> 產生。**</summary>
public sealed class UnityTarget
{
    public string ProjectName { get; init; } = "";

    public string ProjectRoot { get; init; } = "";

    /// <summary>AgentCommands 資料根（已確認存在）。</summary>
    public string DataRoot { get; init; } = "";

    /// <summary>
    /// 選擇過程的一句話（例：「未給 --project ⇒ 用唯一啟用的專案」）。
    /// <para>⚠ 自動選中的時候**一定要有值** —— 沒說出口的自動選擇，跟使用者自己點的長得一樣。</para>
    /// </summary>
    public string SelectionNote { get; init; } = "";

    /// <summary>印在每一則委派輸出上的定語。</summary>
    public string Describe() => $"{ProjectName}（{DataRoot}）";
}

/// <summary>派遣目標解析的結果：三者恰有一種有值。</summary>
public readonly struct UnityTargetResolution
{
    public UnityTargetResolution(UnityTarget? iTarget, string iError, string iHint)
    {
        Target = iTarget;
        Error = iError;
        Hint = iHint;
    }

    public UnityTarget? Target { get; }

    /// <summary>擋下的理由（空 ＝ 沒被擋）。⚠ 要說「哪一格不成立」，不是「失敗了」。</summary>
    public string Error { get; }

    /// <summary>怎麼過去（出口）。有 Error 就該有 Hint —— 只講擋下不講出口等於把人留在原地。</summary>
    public string Hint { get; }

    public bool Ok => Target != null;
}

public static class UnityTargetResolver
{
    /// <summary>
    /// 解析要派給哪個專案。
    /// <para>優先序：<c>iProjectName</c> 點名 ＞ 只有一個啟用專案時自動選（會在 SelectionNote 說出來）
    /// ＞ 擋下。**沒有第四種**：0 個與多個都是擋下，因為兩者都沒有「唯一正確答案」。</para>
    /// </summary>
    /// <param name="iConfig">設定檔內容；null ＝ 還沒有設定檔（由呼叫端讀，本層不找檔案）。</param>
    /// <param name="iConfigPath">設定檔路徑，只用來組錯誤訊息（要讓人知道去改哪一個檔）。</param>
    /// <param name="iProjectName">--project 的值；null／空 ＝ 沒點名。</param>
    public static UnityTargetResolution Resolve(SenateConfig? iConfig, string iConfigPath, string? iProjectName)
    {
        if (iConfig == null)
            return Fail($"還沒有設定檔（{iConfigPath}）",
                        "先跑 senate init，把目標 Unity 專案寫進 projects[]");

        var aEnabled = iConfig.Projects
            .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Root))
            .ToList();

        SenateProject aProj;
        string aNote = "";
        string aName = (iProjectName ?? "").Trim();
        if (aName.Length > 0)
        {
            SenateProject? aNamed = iConfig.Projects.FirstOrDefault(
                p => string.Equals(p.Name, aName, System.StringComparison.OrdinalIgnoreCase));
            if (aNamed == null)
                return Fail($"設定檔裡沒有名叫 '{aName}' 的專案",
                            $"現有：{string.Join(" / ", iConfig.Projects.Select(p => p.Name))}");
            // 「我關掉它」與「我沒設定過它」是兩件事 —— 訊息要分得出來。
            if (!aNamed.Enabled)
                return Fail($"專案 '{aNamed.Name}' 在設定檔裡是停用的（enabled=false）",
                            $"要用它就把 enabled 改回 true（{iConfigPath}）");
            aProj = aNamed;
        }
        else if (aEnabled.Count == 1)
        {
            aProj = aEnabled[0];
            aNote = $"未給 --project ⇒ 用唯一啟用的專案 '{aProj.Name}'（{aProj.Root}）";
        }
        else
        {
            // 多專案不猜 —— 派錯專案的 Cmd 會在別人的 Editor 上真的執行。
            return Fail(
                aEnabled.Count == 0 ? "設定檔裡沒有任何啟用中的專案" : "有多個啟用中的專案，不猜",
                aEnabled.Count == 0
                    ? $"把專案寫進 projects[] 並設 enabled=true（{iConfigPath}）"
                    : $"加 --project <名>；現有啟用：{string.Join(" / ", aEnabled.Select(p => p.Name))}");
        }

        string? aDataRoot = ProjectProbe.ResolveAgentCommandsRoot(aProj.Root, aProj.AgentCommandsRoot);
        if (aDataRoot == null || !System.IO.Directory.Exists(aDataRoot))
            return Fail($"AgentCommands 資料根不存在：{aDataRoot ?? "(解析失敗)"}",
                        $"檢查 {iConfigPath} 專案 '{aProj.Name}' 的 root / agentCommandsRoot");

        return new UnityTargetResolution(new UnityTarget
        {
            ProjectName = aProj.Name,
            ProjectRoot = aProj.Root,
            DataRoot = aDataRoot,
            SelectionNote = aNote,
        }, "", "");
    }

    static UnityTargetResolution Fail(string iError, string iHint)
        => new UnityTargetResolution(null, iError, iHint);
}
