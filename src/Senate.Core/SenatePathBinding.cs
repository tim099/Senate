// 區塊職責：把 `SCP_PathId` 對映到 senate.local.json 的欄位 —— **唯一一處**。
// 物理意義：描述表（SCP_Core）刻意不知道值住在哪個檔；而「住在哪」只該有一份答案。
//           頁面與 `senate cmd paths` 都走本檔 ⇒ 兩邊不可能對同一格給出不同的值。
// 數值影響：純讀寫記憶體中的 config 物件（落檔由呼叫端決定）。
//
// ⚠ **資料根只有一組**（Tim 2026-08-31）：酒館 `_seq.txt`、任務 `_index.txt`、`_session` lock 全都假設只有一棵資料樹 ——
//   兩棵就是兩份序號、兩份計數、persona 被切成兩半，而**沒有任何一層會喊**。
using SCP.Core.Paths;

namespace Senate.Core;

public static class SenatePathBinding
{
    /// <summary>Senate 專案根（exe 所在那棵 repo）—— 啟動時由宿主設一次；`SCP_PathId.HostRepoRoot` 的值。</summary>
    public static string HostRepoRoot { get; set; } = "";

    /// <summary>描述表要的「這個 Id 存起來的原始值」。Derived 的格子不會走到這裡。</summary>
    public static SCP_PathStoredValue StoredOf(SenateConfig iConfig, SCP_PathId iId)
    {
        switch (iId)
        {
            // Senate 專案根（Host 格，TASK-0390）：不在設定檔裡 —— 是 exe 所在那棵 repo，啟動時由 Program 宣告。
            case SCP_PathId.HostRepoRoot:
                return HostRepoRoot.Length == 0
                    ? SCP_PathStoredValue.Unavailable("宿主沒有宣告 Senate 專案根（SenatePathBinding.HostRepoRoot 沒設）")
                    : SCP_PathStoredValue.Of(HostRepoRoot);
            // 資料根／詞典根／漫畫庫根：全域 `paths` 區塊（TASK-0390）。
            case SCP_PathId.AgentCommandsRoot:
                return SCP_PathStoredValue.Of(iConfig.Paths.AgentCommandsRoot ?? "");
            case SCP_PathId.LettersRoot:
                return SCP_PathStoredValue.Of(iConfig.Awakening.LettersRoot ?? "");
            // 安裝系統（TASK-0375）：空白是合法值（＝用預設），解析結果的 Origin 會是「未設定」，由 InstallEnv 解讀。
            case SCP_PathId.PythonEnvRoot:
                return SCP_PathStoredValue.Of(iConfig.Install.PythonEnvRoot ?? "");
            case SCP_PathId.ModelsRoot:
                return SCP_PathStoredValue.Of(iConfig.Install.ModelsRoot ?? "");
            case SCP_PathId.GlossaryRoot:
                return SCP_PathStoredValue.Of(iConfig.Paths.GlossaryRoot ?? "");
            case SCP_PathId.ComicRoot:
                return SCP_PathStoredValue.Of(iConfig.Paths.ComicRoot ?? "");
            // ⛔ `BankRoot` 2026-09-17 起是 **Derived**（`<資料根>/Bank`）⇒ 本檔**不再接它那一格**。
            //   哪天有人把它改回 Stored 而忘了這裡，下面的 default 會當場出聲，
            //   ⛔ 不會靜默回一個空字串（而空字串在頁面上長成「未設定」，那是另一個意思）。
            default:
                // 走到這裡＝描述表把某格標成 Stored 而本檔沒接 ⇒ 要大聲，不要靜默回空字串
                //（靜默的空字串會在頁面上長成「未設定」，而那是另一個意思）。
                return SCP_PathStoredValue.Unavailable(
                    $"{iId} 在描述表裡是 Stored，但 SenatePathBinding 沒有對映到任何欄位"
                    + " —— 這是程式錯誤：加了 Stored 的格子就要在這裡接一格");
        }
    }

    /// <summary>
    /// **資料根的唯一入口**（TASK-0390）：走描述表的 `AgentCommandsRoot`。回 null ＝ 解不出來（原因在 <paramref name="oError"/>）。
    /// </summary>
    public static string? ResolveDataRoot(SenateConfig? iConfig, out string? oError)
    {
        oError = null;
        if (iConfig == null) { oError = "還沒有設定檔（先跑 senate init）"; return null; }
        SCP_PathResolution aR = SCP_PathRegistry.Resolve(SCP_PathId.AgentCommandsRoot, id => StoredOf(iConfig, id));
        if (aR.Error != null) { oError = aR.Error; return null; }
        return aR.Value;
    }

    /// <summary>
    /// 解出詞典根（走描述表：手填 ＞ auto ⇒ `<Senate 專案根>/Glossary`）。
    /// 回 null ＝ 解不出來（原因在 <paramref name="oError"/>）—— 呼叫端**要說出來**，本次不附詞典。
    /// </summary>
    public static string? ResolveGlossaryRoot(SenateConfig? iConfig, out string? oError)
    {
        oError = null;
        if (iConfig == null) { oError = "還沒有設定檔"; return null; }
        SCP_PathResolution aR = SCP_PathRegistry.Resolve(SCP_PathId.GlossaryRoot, id => StoredOf(iConfig, id));
        if (aR.Error != null) { oError = aR.Error; return null; }
        return aR.Value;
    }

    /// <summary>
    /// 把 <c>SCP_DataPaths.Letters(資料根)</c> 接到本設定檔的信件根解析（TASK-0390，2026-10-07）。
    /// <para>只有傳進來的資料根＝設定那一組時才回設定值；別的資料根回 null（＝慣例值），
    /// ⛔ 不然 selftest 的暫存樹會寫進真的信件庫。每次現讀設定檔 ⇒ 後台改了路徑，常駐 Server 下一次呼叫就跟上。</para>
    /// <para>設定檔讀不了／解不出來 ⇒ 回 null 退慣例值：那跟本函式存在之前同形，而設定壞掉時
    /// `senate cmd paths`／doctor 會出聲（這一層沒有可以說話的通道）。</para>
    /// </summary>
    public static void InstallLettersResolver(string iRepoRoot)
    {
        SCP_DataPaths.LettersResolver = iDataRoot =>
        {
            SenateConfig? aCfg;
            try { aCfg = SenateConfig.Load(SenateConfig.DefaultPath(iRepoRoot)); }
            catch (Exception) { return null; }
            if (aCfg == null) return null;
            SCP_PathResolution aData = SCP_PathRegistry.Resolve(SCP_PathId.AgentCommandsRoot, id => StoredOf(aCfg, id));
            if (aData.Error != null || !SamePath(aData.Value, iDataRoot.Value)) return null;
            SCP_PathResolution aLetters = SCP_PathRegistry.Resolve(SCP_PathId.LettersRoot, id => StoredOf(aCfg, id));
            if (aLetters.Error != null || aLetters.Value.Length == 0) return null;
            return new SCP_LettersRoot(aLetters.Value);
        };
    }

    static bool SamePath(string iA, string iB)
        => string.Equals(iA.Replace('\\', '/').TrimEnd('/'), iB.Replace('\\', '/').TrimEnd('/'),
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>寫回記憶體中的 config。回 false ＝ 這格寫不了（呼叫端要說出來）。</summary>
    public static bool SetStored(SenateConfig iConfig, SCP_PathId iId, string iValue, out string? oError)
    {
        oError = null;
        switch (iId)
        {
            case SCP_PathId.AgentCommandsRoot:
                iConfig.Paths.AgentCommandsRoot = iValue;
                return true;
            case SCP_PathId.GlossaryRoot:
                iConfig.Paths.GlossaryRoot = iValue;
                return true;
            case SCP_PathId.ComicRoot:
                iConfig.Paths.ComicRoot = iValue;
                return true;
            case SCP_PathId.LettersRoot:
                iConfig.Awakening.LettersRoot = iValue;
                return true;
            case SCP_PathId.PythonEnvRoot:
                iConfig.Install.PythonEnvRoot = iValue;
                return true;
            case SCP_PathId.ModelsRoot:
                iConfig.Install.ModelsRoot = iValue;
                return true;
            default:
                oError = $"{iId} 不是可設定的格子（Derived 的路徑算出來，不儲存）";
                return false;
        }
    }
}
