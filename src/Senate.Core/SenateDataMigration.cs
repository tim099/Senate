// 區塊職責：**舊版面 → `SenateData/` 的一次性搬遷** —— 開機時跑一次，冪等。
// 物理意義：路徑改了而舊檔還躺在原地時，程式會**讀到一個空的新位置**並表現得像「這台機器沒設定過」：
//           專案清單空了、頁面設定回預設、視窗版面重來。那不是錯誤畫面，是一個
//           **看起來完全正常的初始狀態** —— 使用者只會以為自己忘了設定（三態同形，
//           見 <SCP_Core>/Docs~/Coding_Standards.md §3.3）。
//           ⇒ 改路徑必須同時搬檔，而且搬完要**說出來**。
// 數值影響：只搬 config/ 與 prefs/ 那四個檔（`File.Move`，不覆寫）。不刪任何東西。
//
// ⚠ 刻意**不搬** runtime/ 那兩格（`_process_registry` / `ui_session.json`）：
//   判準就是 Tim 2026-09-01 拍板的那句「這個檔掉了，使用者要不要重做工？」——
//   runtime 的答案是「完全無感」，它們重生的成本是零。
//   而 `_process_registry` 更該重生而不是搬：裡面是**活著的 PID**，
//   搬一份舊註冊表過去等於把一批可能早就死掉的進程當成還活著 ——
//   那比沒有註冊表危險，因為它會回答問題，只是答錯。
//
// ⚠ 樣板檔（`senate.local.example.json`）也不在這裡搬：**它入版控，git 自己會搬**。
//   在這裡多寫一條，等於同一個檔有兩個搬運工，而它們對「現在該在哪」的看法遲早會分岔。
using System;
using System.Collections.Generic;
using System.IO;

namespace Senate.Core;

/// <summary>一格搬遷的結果。**四態不得同形** —— 它們的後續動作完全不同。</summary>
public enum SenateMigrationOutcome
{
    /// <summary>舊位置沒有東西 ⇒ 這台機器本來就沒有這個檔（常態，不是錯誤）。</summary>
    NothingToDo = 0,

    /// <summary>已經在新位置了 ⇒ 之前搬過（冪等重跑會走到這裡）。</summary>
    AlreadyMigrated = 1,

    /// <summary>這次真的搬了。</summary>
    Moved = 2,

    /// <summary>新舊**都有** ⇒ 不猜哪份才是對的，兩份都留著，交給人決定。</summary>
    Conflict = 3,

    /// <summary>搬的時候炸了（檔案被鎖、權限不足…）。舊檔一定還在。</summary>
    Failed = 4,
}

public readonly struct SenateMigrationStep
{
    public SenateMigrationStep(string iWhat, SenateMigrationOutcome iOutcome, string iDetail)
    {
        What = iWhat;
        Outcome = iOutcome;
        Detail = iDetail;
    }

    /// <summary>人看得懂的名字（檔名）。</summary>
    public string What { get; }

    public SenateMigrationOutcome Outcome { get; }

    /// <summary>發生了什麼 —— 一定寫得出「從哪到哪」或「為什麼沒動」。</summary>
    public string Detail { get; }
}

public static class SenateDataMigration
{
    /// <summary>
    /// 把舊版面的本機檔搬進 <see cref="SenatePaths"/> 的新位置。**冪等**，可以每次開機都跑。
    /// <para>回傳每一格的結果 —— 呼叫端要負責把 <see cref="SenateMigrationOutcome.Moved"/>、
    /// <see cref="SenateMigrationOutcome.Conflict"/>、<see cref="SenateMigrationOutcome.Failed"/>
    /// 印出來。⛔ 靜默搬檔比不搬更糟：使用者會在不知情的情況下以為檔案不見了。</para>
    /// <para>⚠ 本函式**不刪東西**。衝突時兩份都留著。</para>
    /// </summary>
    public static IReadOnlyList<SenateMigrationStep> Run(string iRepoRoot)
    {
        var aSteps = new List<SenateMigrationStep>(3);

        aSteps.Add(MoveOne(
            "senate.local.json",
            Path.Combine(iRepoRoot, "senate.local.json"),
            SenatePaths.LocalConfig(iRepoRoot)));

        aSteps.Add(MoveOne(
            "senate.pages.local.json",
            Path.Combine(iRepoRoot, "senate.pages.local.json"),
            SenatePaths.PageStore(iRepoRoot)));

        aSteps.Add(MoveOne(
            "imgui.ini",
            Path.Combine(iRepoRoot, "imgui.ini"),
            SenatePaths.ImGuiIni(iRepoRoot)));

        return aSteps;
    }

    /// <summary>這一輪有沒有需要讓人看到的事（搬了／衝突／失敗）。</summary>
    public static bool NeedsAttention(IReadOnlyList<SenateMigrationStep> iSteps)
    {
        for (int i = 0; i < iSteps.Count; i++)
        {
            if (iSteps[i].Outcome is SenateMigrationOutcome.Moved
                or SenateMigrationOutcome.Conflict
                or SenateMigrationOutcome.Failed) return true;
        }
        return false;
    }

    static SenateMigrationStep MoveOne(string iWhat, string iOld, string iNew)
    {
        bool aOldExists = File.Exists(iOld);
        bool aNewExists = File.Exists(iNew);

        if (!aOldExists && aNewExists)
            return new SenateMigrationStep(iWhat, SenateMigrationOutcome.AlreadyMigrated, iNew);

        if (!aOldExists)
            return new SenateMigrationStep(iWhat, SenateMigrationOutcome.NothingToDo, $"舊位置沒有這個檔：{iOld}");

        if (aNewExists)
        {
            // ⛔ 不比 mtime 也不比大小去猜「哪份比較新」——
            //    猜對了沒有人會知道，猜錯了使用者的設定就沒了，而兩者在畫面上同形。
            return new SenateMigrationStep(iWhat, SenateMigrationOutcome.Conflict,
                $"新舊都有，兩份都保留（未覆寫）。舊：{iOld}　新：{iNew}");
        }

        try
        {
            string? aDir = Path.GetDirectoryName(iNew);
            if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
            File.Move(iOld, iNew);
            return new SenateMigrationStep(iWhat, SenateMigrationOutcome.Moved, $"{iOld} → {iNew}");
        }
        catch (Exception e)
        {
            // 舊檔還在 ⇒ 下次開機會再試一次；這裡不吞例外訊息。
            return new SenateMigrationStep(iWhat, SenateMigrationOutcome.Failed,
                $"搬移失敗（舊檔仍在原處）：{iOld} → {iNew}　{e.GetType().Name}: {e.Message}");
        }
    }
}
