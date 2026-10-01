// 區塊職責：TASK-0360（自由時間搬到 Senate）的自我對拍。
// 物理意義：三格各驗一個「錯了也不會叫」的地方：
//           ① 設定檔：不存在／讀不了／不合法 三種狀態不可同形（都會退回預設值，差別只在有沒有說出來）。
//           ② 活動 md：欄位寫回要讀得回來、帶冒號的值要加引號、同 id 不准覆寫（反向對照：零寫入）。
//           ③ 後台頁：專案根走宿主解析器、設定讀的是磁碟上那一份（畫面數字 ＝ 檔裡的數字，不是常數）。
// 數值影響：全在 temp 目錄裡寫、跑完刪；⛔ 不碰真實資料根。
#nullable enable
using SCP.Core.FreeTime;
using SCP.Core.Gui;
using Senate.Cli.Pages;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow FreeTimeSettingsCleanRoom()
    {
        const string aName = "自由時間設定檔：不存在／讀不了／不合法三態不同形，合法值寫入讀回（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_ft_set_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aPath = SCP_FreeTimeSettings.PathOf(aTmp);
            // ① 不存在 ⇒ 預設值、oFileExists=false、沒有錯誤
            var s0 = SCP_FreeTimeSettings.Read(aTmp, out bool aExists0, out string? aErr0);
            bool aMissing = !aExists0 && aErr0 == null && s0.PixelsPerSession == 10;
            // 🔴 ② 不合法 ⇒ Write 擋下、**檔不存在**（零寫入）
            var aBad = SCP_FreeTimeSettings.Defaults(); aBad.PixelsPerSession = 0;
            bool aBadBlocked = SCP_FreeTimeSettings.Validate(aBad).Count > 0
                               && !SCP_FreeTimeSettings.Write(aTmp, aBad, out _) && !File.Exists(aPath);
            // ③ 合法 ⇒ 寫入讀回
            var aGood = SCP_FreeTimeSettings.Defaults(); aGood.PixelsPerSession = 7; aGood.StarveThreshold = 3;
            bool aWrote = SCP_FreeTimeSettings.Write(aTmp, aGood, out string? aWriteErr);
            var s1 = SCP_FreeTimeSettings.Read(aTmp, out bool aExists1, out string? aErr1);
            bool aRoundTrip = aWrote && aExists1 && aErr1 == null && s1.PixelsPerSession == 7 && s1.StarveThreshold == 3;
            // 🔴 ④ 壞檔 ⇒ 預設值，但**一定要帶錯誤**（⛔ 不准跟「不存在」同形）
            File.WriteAllText(aPath, "{ not json");
            var s2 = SCP_FreeTimeSettings.Read(aTmp, out bool aExists2, out string? aErr2);
            bool aCorrupt = aExists2 && aErr2 != null && s2.PixelsPerSession == 10;
            bool aOk = aMissing && aBadBlocked && aRoundTrip && aCorrupt;
            return new CheckRow(aName,
                $"不存在⇒預設且不報錯={aMissing}／🔴 不合法擋下零寫入={aBadBlocked}／寫入讀回={aRoundTrip}"
                + (aWriteErr != null ? $"（{aWriteErr}）" : "") + $"／🔴 壞檔⇒預設且帶錯誤={aCorrupt}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }

    static CheckRow FreeTimeActivityMdCleanRoom()
    {
        const string aName = "自由時間活動 md：專案層新建／欄位寫回讀回／冒號值加引號／缺欄補上／同 id 不覆寫（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_ft_md_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            bool aCreated = SCP_FreeTimeCatalog.CreateProjectActivity(aTmp, "probe-act", "探針: 有冒號", "做法", "", 5,
                                                                      out string aPath, out string? aCreateErr);
            // 帶冒號的名字要讀得回原字（加了引號、讀取端剝掉）
            bool aNameBack = aCreated && SCP_FreeTimeCatalog.ReadField(aPath, "name") == "探針: 有冒號";
            // 既有欄位改值
            bool aMin = SCP_FreeTimeCatalog.WriteField(aPath, "min_minutes", "15", out _)
                        && SCP_FreeTimeCatalog.ReadField(aPath, "min_minutes") == "15";
            // 原本沒有的欄位（建立時 group 留空 ⇒ 沒寫進 frontmatter）要被補上
            bool aInsert = SCP_FreeTimeCatalog.ReadField(aPath, "group").Length == 0
                           && SCP_FreeTimeCatalog.WriteField(aPath, "group", "遊戲", out _)
                           && SCP_FreeTimeCatalog.ReadField(aPath, "group") == "遊戲";
            // 掃描看得到它（專案層）
            var aWarn = new List<string>();
            var aHit = SCP_FreeTimeCatalog.Scan(aTmp, aWarn).Find(a => a.Id == "probe-act");
            bool aScanned = aHit != null && aHit.IsProjectLayer && aHit.MinMinutes == 15 && aHit.Group == "遊戲";
            // 🔴 同 id 再建一次 ⇒ 擋下、檔內容一個位元組都不變
            string aBefore = File.ReadAllText(aPath);
            bool aNoOverwrite = !SCP_FreeTimeCatalog.CreateProjectActivity(aTmp, "probe-act", "別的", "", "", 0, out _, out _)
                                && File.ReadAllText(aPath) == aBefore;
            bool aOk = aCreated && aNameBack && aMin && aInsert && aScanned && aNoOverwrite;
            return new CheckRow(aName,
                $"新建={aCreated}{(aCreateErr != null ? $"（{aCreateErr}）" : "")}／冒號值讀回={aNameBack}／改值讀回={aMin}"
                + $"／缺欄補上={aInsert}／掃描看得到={aScanned}／🔴 同 id 不覆寫={aNoOverwrite}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }

    // 頁面讀的設定要是**磁碟上那一份**：檔裡寫 7，畫面就要印 7（舊版那些數字是程式碼常數，畫面跟檔不可能不一致 ——
    //   搬成設定檔之後才有「頁面印的是預設值、檔裡其實改過」這種失敗，所以這一格專門量它）。
    static CheckRow FreeTimePageReadsDisk()
    {
        const string aName = "自由時間後台頁：專案根走宿主解析器、設定值來自磁碟";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_ft_page_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aCfgDir = Path.Combine(aTmp, "SenateData", "config");
            Directory.CreateDirectory(aCfgDir);
            string aProjRoot = aTmp.Replace(Path.DirectorySeparatorChar, '/');
            File.WriteAllText(Path.Combine(aCfgDir, "senate.local.json"),
                "{\n  \"schemaVersion\": 1,\n  \"projects\": [\n    {\n"
                + "      \"name\": \"Probe\",\n      \"root\": \"" + aProjRoot + "\",\n"
                + "      \"agentCommandsRoot\": \"auto\",\n      \"enabled\": true,\n      \"profile\": \"\"\n"
                + "    }\n  ],\n  \"awakening\": {\n    \"lettersRoot\": \"auto\"\n  }\n}\n");
            string aData = aProjRoot + "/AgentCommands";
            Directory.CreateDirectory(aData);
            var aSet = SCP_FreeTimeSettings.Defaults(); aSet.PixelsPerSession = 7;
            bool aSeeded = SCP_FreeTimeSettings.Write(aData, aSet, out _);

            var aModel = new SenateModel(aTmp);
            bool aRootOk = aModel.ProjectRoot.Error == null
                           && aModel.ProjectRoot.Value.Replace(Path.DirectorySeparatorChar, '/') == aProjRoot;
            var aPage = new SCP_GuiFreeTimePage(aModel);
            aPage.OnPush();
            var aUi = new SCP_Ui();
            aPage.Draw(aUi);
            string aText = SCP_GuiTextRenderer.Render(aUi.Root, 200);
            bool aShowsDisk = aText.Contains("⟨7⟩", StringComparison.Ordinal) && aText.Contains("每場 7 張", StringComparison.Ordinal);
            bool aShowsProject = aText.Contains(aProjRoot + "/docs/FreeTime/Activities", StringComparison.Ordinal);
            bool aOk = aSeeded && aRootOk && aShowsDisk && aShowsProject;
            return new CheckRow(aName,
                $"種設定檔={aSeeded}／專案根解析={aRootOk}（{aModel.ProjectRoot.Value}）／畫面印磁碟值 7={aShowsDisk}／專案層路徑={aShowsProject}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }
}
