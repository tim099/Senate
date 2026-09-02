// 區塊職責：**SCP 原生 Cmd 的錯誤報告落檔** —— 形狀沿 Editor 端的 `_cmd_errors/<id>.md`（TASK-0104，Tim ⑥）。
// 物理意義：CLI 只印得下幾行，而 stack／全部 Args／執行位置定語是查錯時真正要看的東西。
//           Editor 端早就有這份檔（本 root 360 份），SCP 原生 Cmd 之前**零報告** —— 失敗只剩 stderr 幾行，
//           agent 讀完 chat 就沒有第二個地方可以回頭看。
//           ⇒ 一份檔、CLI 三行指向它：哪一格不成立／📄 錯誤報告：<路徑>／🔢 exit_code。
// 數值影響：只在 <see cref="ShouldReport"/> 為真時寫；exit 2（用法錯）刻意不寫 ——
//           打錯字配一份 stack 只會訓練人忽略這個目錄。落點由呼叫端傳根（不推導）：
//           CLI 直跑 → SenateData/runtime/_cmd_errors/；Server 執行 → <Server 根>/_cmd_errors/。
// ⚠ Args 裡單一值超過 20 行截斷並標原長 —— 一封 intro body 會讓報告變成信件副本，而報告要能一眼掃完。
using System.Globalization;
using System.Text;
using SCP.Core.Cmd;

namespace Senate.Core;

public static class CmdErrorReport
{
    public const string DirName = "_cmd_errors";
    public const int MaxArgLines = 20;

    /// <summary>
    /// 要不要寫報告。exit 1（Cmd 回報失敗）／70（例外）一律寫；exit 3（委派沒有結果）**只在真的送出過**（有 cmd_id）才寫 ——
    /// not_running／build_mismatch 那種「一筆都沒送」是使用者可以當場處置的事，每次都落一份檔只是噪音。
    /// exit 2 用法錯不寫；exit 0 不寫。
    /// </summary>
    public static bool ShouldReport(int iExitCode, bool iHasCmdId)
    {
        if (iExitCode == 1 || iExitCode == 70) return true;
        if (iExitCode == 3) return iHasCmdId;
        return false;
    }

    /// <summary>
    /// 寫一份報告，回傳路徑。<paramref name="iHostLabel"/> 是執行位置定語（<c>local</c>／<c>server</c>）。
    /// <para>寫檔失敗回 null 並由 <paramref name="iWarn"/> 說原因 —— 報告是加值，不能蓋掉原始錯誤。</para>
    /// </summary>
    public static string? Write(string iRoot, string iCmdId, string iCmdName, IReadOnlyDictionary<string, string> iArgs,
        SCP_CmdResult iResult, string iHostLabel, Action<string>? iWarn = null)
    {
        try
        {
            string aDir = Path.Combine(iRoot, DirName);
            Directory.CreateDirectory(aDir);
            string aPath = Path.Combine(aDir, iCmdId + ".md");
            File.WriteAllText(aPath, Render(iCmdId, iCmdName, iArgs, iResult, iHostLabel), new UTF8Encoding(false));
            return aPath;
        }
        catch (Exception e)
        {
            iWarn?.Invoke($"⚠ 錯誤報告寫不出來（{e.GetType().Name}: {e.Message}）—— 原始錯誤照上面那幾行");
            return null;
        }
    }

    /// <summary>報告內文（純函式，selftest 對它做斷言）。</summary>
    public static string Render(string iCmdId, string iCmdName, IReadOnlyDictionary<string, string> iArgs,
        SCP_CmdResult iResult, string iHostLabel)
    {
        DateTime aNowUtc = DateTime.UtcNow;
        var sb = new StringBuilder();
        sb.Append("# ✗ Cmd 失敗：").Append(iCmdName).Append('\n').Append('\n');
        sb.Append("- **cmd_id**: `").Append(iCmdId).Append("`\n");
        sb.Append("- **exit_code**: ").Append(iResult.ExitCode).Append(ExitMeaning(iResult.ExitCode)).Append('\n');
        sb.Append("- **失敗時間**: ").Append(aNowUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
          .Append(" (local) / ").Append(aNowUtc.ToString("o", CultureInfo.InvariantCulture)).Append(" (UTC)\n");
        // 執行位置定語：這份報告是誰跑出來的 —— local 與 server 的輸出長得一模一樣，只有這一行分得出來。
        sb.Append("- **執行位置**: ").Append(iHostLabel);
        if (iHostLabel == "server") sb.Append("（pid=").Append(ServerContext.Pid).Append('）');
        sb.Append('\n');
        sb.Append("- **build**: ").Append(iHostLabel == "server" ? ServerContext.BuildId : ServerHost.BuildId).Append('\n');
        string aClient = iArgs.TryGetValue("_caller_client", out string? c) && c.Length > 0 ? c : "unstated";
        sb.Append("- **client**: ").Append(aClient).Append('\n');
        if (iResult.Exception != null)
        {
            sb.Append("- **例外型別**: `").Append(iResult.Exception.GetType().FullName).Append("`\n");
            sb.Append("- **訊息**: ").Append(iResult.Exception.Message.Replace("\r", "").Replace("\n", " ")).Append('\n');
        }
        sb.Append('\n');

        sb.Append("## Cmd 說了什麼（Lines）\n");
        foreach (string aLine in iResult.Lines) sb.Append("- ").Append(aLine).Append('\n');
        if (iResult.Lines.Count == 0) sb.Append("- （沒有任何訊息 —— 這本身就是要修的：Fail 要說哪一格不成立）\n");
        sb.Append('\n');

        sb.Append("## Args\n");
        var aKeys = new List<string>(iArgs.Keys); aKeys.Sort(StringComparer.Ordinal);
        foreach (string aKey in aKeys)
        {
            string aValue = iArgs[aKey] ?? "";
            string[] aLines = aValue.Replace("\r\n", "\n").Split('\n');
            if (aLines.Length <= MaxArgLines)
                sb.Append("- `").Append(aKey).Append("` = ").Append(aLines.Length == 1 ? aValue : "\n  " + string.Join("\n  ", aLines)).Append('\n');
            else
                sb.Append("- `").Append(aKey).Append("` = （").Append(aLines.Length).Append(" 行，只印前 ").Append(MaxArgLines).Append(" 行）\n  ")
                  .Append(string.Join("\n  ", aLines, 0, MaxArgLines)).Append("\n  …\n");
        }
        if (aKeys.Count == 0) sb.Append("- （無）\n");
        sb.Append('\n');

        if (iResult.Outputs.Count > 0 || iResult.Values.Count > 0)
        {
            sb.Append("## 失敗前已回報的產出\n");
            foreach (string o in iResult.Outputs) sb.Append("- 📄 ").Append(o).Append('\n');
            foreach (var kv in iResult.Values) sb.Append("- 🔢 ").Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
            sb.Append('\n');
        }

        if (iResult.Exception != null)
        {
            sb.Append("## Stack trace\n```\n").Append(iResult.Exception.ToString()).Append("\n```\n");
        }
        else
        {
            sb.Append("## Stack trace\n（沒有例外 —— 這是 Cmd 自己回報的失敗，成因在上面 Lines 那段）\n");
        }
        return sb.ToString();
    }

    static string ExitMeaning(int iCode) => iCode switch
    {
        1 => "（Cmd 自己回報失敗）",
        2 => "（用法錯 —— 這種通常不會有報告）",
        3 => "（委派沒有結果：逾時／送出後沒等到）",
        70 => "（Cmd 執行時丟出例外）",
        _ => "",
    };
}
