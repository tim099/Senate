// 區塊職責：「現在是不是有一個 build.sh 正在換 exe」—— autostart 拉 Server 之前問的那一句（TASK-0309）。
// 物理意義：build.sh 開頭 `server stop --all` 之後、publish 覆寫 exe 之前有一段空窗。
//           空窗裡一則發文會觸發 autostart（TASK-0267），而它拉起的是**還沒被覆寫的舊 exe** ——
//           那顆握著 senate-server.exe ⇒ publish 撞 access denied ⇒ 留下 CLI 新／Server 舊的混版。
//           🩸 2026-09-27 09:32 實地發生過一次；第二次 build 成功只是那一段剛好沒人發文（運氣不是機制）。
// 數值影響：只讀一個檔的存在與 mtime，⛔ 不寫、不刪。旗標由 build.sh 建、由 build.sh 收。
//
// 🩸 設計判準（錯的時候長什麼樣）：
//   ① **旗標會過期**：build.sh 半路被砍（Ctrl+C、終端機關掉、trap 沒跑到）⇒ 旗標留在原地。
//      沒有過期的話，那一刻起 autostart **永久關掉**，而每一則發文都會印「build 進行中」——
//      一個不會結束的「進行中」跟一個真的進行中在輸出上同形。⇒ 超過 StaleAfter 就當沒有，並且說出來。
//   ② 判準用 mtime，⛔ 不讀檔內的 pid：Git Bash 的 `$$` 是 MSYS pid，不是 Windows pid，拿它去問 OS 會答錯人。
#nullable enable
namespace Senate.Core;

/// <summary><see cref="BuildGuard.Check"/> 的三態 —— 「沒有」與「過期」處置相同（照常拉），但要說的話不同。</summary>
public enum BuildGuardState
{
    /// <summary>沒有旗標。</summary>
    None,

    /// <summary>有一個 build 正在換 exe ⇒ **不要拉 Server**。</summary>
    Active,

    /// <summary>旗標在，但久到不可能還在 build（多半是 build.sh 半路死掉）⇒ 當沒有，但要說。</summary>
    Stale,
}

public static class BuildGuard
{
    /// <summary>旗標超過這麼久就當它是殘檔。一次 build 實測 1～2 分鐘；給寬到不會誤判正常 build。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <summary>判一次（可注入路徑與時間 —— selftest 用；生產呼叫 <see cref="Check(string)"/>）。</summary>
    public static BuildGuardState Check(string iFlagPath, DateTime iNowUtc, out string oDetail)
    {
        oDetail = "";
        if (!File.Exists(iFlagPath)) return BuildGuardState.None;
        DateTime aAt = File.GetLastWriteTimeUtc(iFlagPath);
        TimeSpan aAge = iNowUtc - aAt;
        if (aAge > StaleAfter)
        {
            oDetail = $"旗標 {iFlagPath} 已經 {aAge.TotalMinutes:0} 分鐘（> {StaleAfter.TotalMinutes:0}）⇒ 當成殘檔、照常拉起 Server"
                      + "（build.sh 多半半路被中斷；確認沒有 build 在跑之後可以刪掉它）";
            return BuildGuardState.Stale;
        }
        oDetail = $"build.sh 正在換 exe（旗標 {iFlagPath}，{Math.Max(0, aAge.TotalSeconds):0} 秒前建立）";
        return BuildGuardState.Active;
    }

    /// <summary>生產路徑：看本 repo 的旗標。</summary>
    public static BuildGuardState Check(string iRepoRoot)
        => Check(SenatePaths.BuildInProgressFlag(iRepoRoot), DateTime.UtcNow, out _);

    /// <summary>同上，附人讀的原因。</summary>
    public static BuildGuardState Check(string iRepoRoot, out string oDetail)
        => Check(SenatePaths.BuildInProgressFlag(iRepoRoot), DateTime.UtcNow, out oDetail);
}
