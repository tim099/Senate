// 區塊職責：酒館的**延後發文匣** —— alter 配對間隔不足時，訊息先放這裡，到點由酒館 Server 投回自己的 tavern lane。
// 物理意義：TASK-0312（epic 0295 ③）。Editor 版是在 handler 裡 `await` 最多 900 秒（期間 Watcher 不接別的 Cmd）；
//          Senate 這側兩個「等」的位置都不行：
//            ⛔ CLI sleep —— agent 的 Bash 前景上限 120 秒，它會把一次發文變成一次逾時；
//            ⛔ Server 的 tavern lane 裡 sleep —— lane 串行，一則配對發言會卡住全體的發文。
//          ⇒ 放進匣子、CLI 當下回「已排程」；Server 心跳每圈 `FlushDue`，到點的用 `SCP_ServerCmdClient.Submit`
//            送進**自己的** tavern lane，之後跟任何一則發文一樣：配號建檔＋發薪＋@mention（寫入端沒有多一個）。
// 數值影響：匣子在 `<酒館 Server 根>/deferred/<id>.json`；投遞時先改名成 `.claimed` 再送、送完刪。
//   🔴 順序刻意是「先認領、再送」：當機在兩者之間 ⇒ 那一則**留在 .claimed、沒發**，⛔ 不會發兩次
//     （seq 全域遞增、發文會付錢 —— 同一則發兩次是付兩次錢）。啟動時留下的 .claimed 只回報、⛔ 不自動重送：
//     它可能已經進了 queue，要人對照酒館再決定（同 exit 7「不知道」的處置）。
//   ⚠ Server 停著的時候到點 ⇒ 下次啟動的第一圈送出（晚到，不會丟）。
#nullable enable
using System.Globalization;
using SCP.Core.Json;
using SCP.Core.Proc;
using SCP.Core.Tavern;

namespace Senate.Core;

public static class SenateTavernDeferred
{
    public const string DirName = "deferred";
    const string ClaimedSuffix = ".claimed";

    public static string Dir(string iServerRoot) => Path.Combine(iServerRoot, DirName);

    /// <summary>放一則進匣子。回匣子裡那個檔的路徑；失敗丟例外（呼叫端要說「確定沒排進去」）。</summary>
    public static string Schedule(string iServerRoot, string iDataRoot, string iRoom, string iMsgJson,
                                  string iPersona, DateTime iNotBeforeUtc)
    {
        string aDir = Dir(iServerRoot);
        Directory.CreateDirectory(aDir);
        string aId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                     + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + "-" + Safe(iPersona);
        SCP_JsonData aDoc = SCP_JsonData.NewObject();
        aDoc.Set("id", SCP_JsonData.NewString(aId));
        aDoc.Set("not_before_utc", SCP_JsonData.NewString(iNotBeforeUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
        aDoc.Set("scheduled_at_utc", SCP_JsonData.NewString(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
        aDoc.Set("persona", SCP_JsonData.NewString(iPersona));
        aDoc.Set("data_root", SCP_JsonData.NewString(iDataRoot));
        aDoc.Set("room", SCP_JsonData.NewString(iRoom));
        aDoc.Set("msg_json", SCP_JsonData.NewString(iMsgJson));
        string aPath = Path.Combine(aDir, aId + ".json");
        string aTmp = aPath + ".tmp";
        File.WriteAllText(aTmp, SCP_JsonWriter.Write(aDoc) + "\n", new System.Text.UTF8Encoding(false));
        File.Move(aTmp, aPath);   // 新檔、名字含 guid ⇒ 不會撞到既有檔
        return aPath.Replace('\\', '/');
    }

    /// <summary>
    /// 把到點的投回 tavern lane。回送出的筆數。單筆壞掉只跳過並回報，⛔ 不讓一筆壞檔擋住後面的。
    /// </summary>
    public static int FlushDue(string iServerRoot, DateTime iNowUtc, Action<string> iLog)
    {
        string aDir = Dir(iServerRoot);
        if (!Directory.Exists(aDir)) return 0;
        int aSent = 0;
        foreach (string aPath in Directory.GetFiles(aDir, "*.json"))
        {
            SCP_JsonData aDoc;
            try { aDoc = SCP_JsonData.Parse(File.ReadAllText(aPath)); }
            catch (Exception e) { iLog($"⚠ 延後發文匣有一份讀不了（{Path.GetFileName(aPath)}：{e.Message}）—— 跳過，⛔ 沒有送"); continue; }
            if (!DateTime.TryParse(aDoc.GetString("not_before_utc", ""), CultureInfo.InvariantCulture,
                                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime aDue))
            { iLog($"⚠ 延後發文 {Path.GetFileName(aPath)} 沒有可讀的 not_before_utc —— 跳過，⛔ 沒有送"); continue; }
            if (aDue > iNowUtc) continue;

            string aClaimed = aPath + ClaimedSuffix;
            try { File.Move(aPath, aClaimed); }
            catch (Exception) { continue; }   // 別人先認領了（或檔剛好被動）⇒ 這一圈不碰它

            try
            {
                var aArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["data_root"] = aDoc.GetString("data_root", ""),
                    ["room"] = aDoc.GetString("room", SCP_TavernRegion.DefaultRoom),
                    ["msg_json"] = aDoc.GetString("msg_json", ""),
                    ["_caller_client"] = "senate-server:deferred",
                };
                string aCmdId = SCP_ServerCmdClient.Submit(iServerRoot, SCP_TavernWriter.LaneName, "tavern-write", aArgs);
                File.Delete(aClaimed);
                aSent++;
                iLog($"· 延後發文到點 ⇒ 送進 tavern lane（{aDoc.GetString("persona", "?")}，cmd {aCmdId}）");
            }
            catch (Exception e)
            {
                iLog($"🔴 延後發文 {Path.GetFileName(aPath)} 已認領但**送不進 queue**（{e.Message}）—— 留在 `{Path.GetFileName(aClaimed)}`，⛔ 沒有發；要人對照酒館再決定");
            }
        }
        return aSent;
    }

    /// <summary>上次沒收乾淨的 `.claimed`（認領了、不知道送出去沒）—— 只列出來，⛔ 不自動重送。</summary>
    public static IReadOnlyList<string> Orphans(string iServerRoot)
    {
        string aDir = Dir(iServerRoot);
        if (!Directory.Exists(aDir)) return Array.Empty<string>();
        return Directory.GetFiles(aDir, "*" + ClaimedSuffix);
    }

    static string Safe(string iS)
    {
        var aSb = new System.Text.StringBuilder();
        foreach (char c in iS ?? "") aSb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return aSb.Length > 0 ? aSb.ToString() : "anon";
    }
}
