// 區塊職責：把 `SCP_PathId` 對映到 senate.local.json 的欄位 —— **唯一一處**。
// 物理意義：描述表（SCP_Core）刻意不知道值住在哪個檔；而「住在哪」只該有一份答案。
//           頁面與 `senate cmd paths` 都走本檔 ⇒ 兩邊不可能對同一格給出不同的值。
// 數值影響：純讀寫記憶體中的 config 物件（落檔由呼叫端決定）。
//
// ⚠ **資料根只有一組**（Tim 2026-08-31）。所以「哪個專案」不是一個選項：
//   酒館 `_seq.txt`、任務 `_index.txt`、`_session` lock 全都假設只有一棵資料樹 ——
//   兩棵就是兩份序號、兩份計數、persona 被切成兩半，而**沒有任何一層會喊**。
//   ⇒ 有兩個啟用專案時本檔回 **Unavailable（附理由）**，不替人挑一個。
//   🩸 「靜默挑一個」的症狀是「路徑全對，只是屬於別的專案」—— 那比解不出來難查得多。
using SCP.Core.Paths;

namespace Senate.Core;

public static class SenatePathBinding
{
    /// <summary>
    /// 那個唯一的專案。<c>oError</c> 有值 ＝ 不唯一（0 個或 &gt;1 個啟用），**呼叫端不准自己挑**。
    /// </summary>
    public static SenateProject? SingleProject(SenateConfig iConfig, out string? oError)
    {
        var aEnabled = new List<SenateProject>();
        foreach (SenateProject p in iConfig.Projects) if (p.Enabled) aEnabled.Add(p);
        if (aEnabled.Count == 1) { oError = null; return aEnabled[0]; }
        if (aEnabled.Count == 0)
        {
            oError = "沒有啟用的專案 —— 專案根沒有人說過（不是空的，是沒有起點）";
            return null;
        }
        var aNames = new List<string>();
        foreach (SenateProject p in aEnabled) aNames.Add(p.Name.Length > 0 ? p.Name : "（未命名）");
        oError = $"有 {aEnabled.Count} 個啟用的專案（{string.Join("、", aNames)}）——"
                 + " **資料根只有一組**，所以這裡不替你挑。停用其餘的，只留一個。";
        return null;
    }

    /// <summary>描述表要的「這個 Id 存起來的原始值」。Derived 的格子不會走到這裡。</summary>
    public static SCP_PathStoredValue StoredOf(SenateConfig iConfig, SCP_PathId iId)
    {
        switch (iId)
        {
            case SCP_PathId.ProjectRoot:
            {
                SenateProject? aProj = SingleProject(iConfig, out string? aErr);
                return aProj == null
                    ? SCP_PathStoredValue.Unavailable(aErr!)
                    : SCP_PathStoredValue.Of(aProj.Root);
            }
            // ⚠ 資料根是 Global：值住在那個唯一專案的欄位裡**只是過渡**
            //   （它之後會搬到 Unity 專案之外）。所以這裡讀的仍是那一格，
            //   但語意上它不是「某個專案的資料根」，是「這台機器的資料根」。
            case SCP_PathId.AgentCommandsRoot:
            {
                SenateProject? aProj = SingleProject(iConfig, out string? aErr);
                return aProj == null
                    ? SCP_PathStoredValue.Unavailable(aErr!)
                    : SCP_PathStoredValue.Of(aProj.AgentCommandsRoot);
            }
            case SCP_PathId.LettersRoot:
                return SCP_PathStoredValue.Of(iConfig.Awakening.LettersRoot ?? "");
            default:
                // 走到這裡＝描述表把某格標成 Stored 而本檔沒接 ⇒ 要大聲，不要靜默回空字串
                //（靜默的空字串會在頁面上長成「未設定」，而那是另一個意思）。
                return SCP_PathStoredValue.Unavailable(
                    $"{iId} 在描述表裡是 Stored，但 SenatePathBinding 沒有對映到任何欄位"
                    + " —— 這是程式錯誤：加了 Stored 的格子就要在這裡接一格");
        }
    }

    /// <summary>寫回記憶體中的 config。回 false ＝ 這格寫不了（呼叫端要說出來）。</summary>
    public static bool SetStored(SenateConfig iConfig, SCP_PathId iId, string iValue, out string? oError)
    {
        oError = null;
        switch (iId)
        {
            case SCP_PathId.ProjectRoot:
            case SCP_PathId.AgentCommandsRoot:
            {
                SenateProject? aProj = SingleProject(iConfig, out string? aErr);
                if (aProj == null) { oError = aErr; return false; }
                if (iId == SCP_PathId.ProjectRoot) aProj.Root = iValue;
                else aProj.AgentCommandsRoot = iValue;
                return true;
            }
            case SCP_PathId.LettersRoot:
                iConfig.Awakening.LettersRoot = iValue;
                return true;
            default:
                oError = $"{iId} 不是可設定的格子（Derived 的路徑算出來，不儲存）";
                return false;
        }
    }
}
