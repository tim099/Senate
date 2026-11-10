// 區塊職責：Discord 收發「開關開著卻跑不起來」的判定（SCP_DiscordReadiness）與狀態指令印的 ⚠（淨室）。
// 物理意義：開關、對應表跟著資料樹同步到每一台，Bot token 不會 ⇒ 新裝的機器「Inbound：開」照樣印，而它其實沒在收。
//           🩸 2026-11-10：就是這個形狀 —— 別台酒館的訊息一則都沒收，狀態指令只說「開」。
//           這裡拿**真的設定檔與真的 secrets 資料夾**（暫存根）走一輪：沒 token ⇒ 有 ⚠；放進 token ⇒ ⚠ 消失。
using SCP.Core.Discord;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow DiscordReadinessCleanRoom()
    {
        const string aName = "Discord 收發：開關開著卻跑不起來要說出來 —— 沒 token／沒對應／沒 webhook 各一句，補齊後消失（淨室）";
        string aData = Path.Combine(Path.GetTempPath(), "senate_dready_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFails = new List<string>();

            // ── 純函式：每一種被擋住的原因各一句；開關關著不算 ──
            if (SCP_DiscordReadiness.InboundBlockers(false, false, 0, 0).Count != 0) aFails.Add("Inbound 關著不該有 ⚠");
            if (SCP_DiscordReadiness.InboundBlockers(true, true, 2, 2).Count != 0) aFails.Add("token 有、對應在跑 ⇒ 不該有 ⚠");
            List<string> aNoTok = SCP_DiscordReadiness.InboundBlockers(true, false, 2, 2);
            if (aNoTok.Count != 1 || !aNoTok[0].Contains("Bot token")) aFails.Add("沒 token 應該剛好一句、提到 Bot token");
            if (SCP_DiscordReadiness.InboundBlockers(true, true, 0, 0).Count != 1) aFails.Add("沒有對應應該一句");
            if (SCP_DiscordReadiness.InboundBlockers(true, true, 3, 0).Count != 1) aFails.Add("對應全部沒在跑應該一句");
            if (SCP_DiscordReadiness.InboundBlockers(true, false, 0, 0).Count != 2) aFails.Add("沒 token 又沒對應應該兩句（兩件事都要說）");
            if (SCP_DiscordReadiness.OutboundBlockers(false, 0, 0, 0).Count != 0) aFails.Add("Outbound 關著不該有 ⚠");
            if (SCP_DiscordReadiness.OutboundBlockers(true, 1, 1, 1).Count != 0) aFails.Add("webhook 啟用且有綁 ⇒ 不該有 ⚠");
            if (SCP_DiscordReadiness.OutboundBlockers(true, 0, 0, 0).Count != 1) aFails.Add("沒 webhook 應該一句");
            if (SCP_DiscordReadiness.OutboundBlockers(true, 2, 0, 1).Count != 1) aFails.Add("webhook 全停用應該一句");
            if (SCP_DiscordReadiness.OutboundBlockers(true, 2, 1, 0).Count != 1) aFails.Add("沒有分類綁到 webhook 應該一句");

            // ── 從檔案量：暫存資料根 ＋ 真的設定檔／對應表／secrets 資料夾 ──
            string aCfgDir = Path.Combine(aData, "ChatTavern", "discord");
            Directory.CreateDirectory(aCfgDir);
            File.WriteAllText(Path.Combine(aCfgDir, SCP_DiscordPaths.ConfigFileName),
                "{\"inbound\":{\"enabled\":true},\"outbound\":{\"enabled\":true,\"category_webhooks\":{\"Main\":[\"111\"]}},\"webhooks\":[{\"id\":\"111\",\"enabled\":true}]}");
            SCP_DiscordConfig aCfg = SCP_DiscordConfigStore.Load(aData);
            if (!aCfg.InboundEnabled || !aCfg.OutboundEnabled || aCfg.Webhooks.Count != 1)
                aFails.Add($"測試設定檔沒讀成預期的樣子（in={aCfg.InboundEnabled} out={aCfg.OutboundEnabled} webhooks={aCfg.Webhooks.Count} err={aCfg.Error}）—— 設定檔格式變了，這一格要跟著改");

            SCP_DiscordTokenStatus aTok = SCP_DiscordBot.Status(aData);
            bool aEnv = aTok.EnvOverride;      // 這台有設環境變數 ⇒ 「沒 token」那一半量不到，照實說、不當成失敗
            List<string> aBefore = SCP_DiscordReadiness.InboundBlockers(aData);
            if (!aEnv && !aBefore.Any(x => x.Contains("Bot token"))) aFails.Add("暫存根沒有 token，InboundBlockers 卻沒提 Bot token");

            // 狀態指令印的那幾行（指令只加縮排，字句只有 SCP_DiscordReadiness 這一份）：⚠ 要在，而且指到安裝文件
            string aStText = string.Join("\n", SCP_DiscordReadiness.InboundWarnings(aData));
            if (!aEnv && (!aStText.Contains("⚠ Inbound 開著") || !aStText.Contains(SCP_DiscordReadiness.InstallDocName)))
                aFails.Add("InboundWarnings 沒有「⚠ Inbound 開著」或沒指到安裝文件");
            // Outbound：這份設定有一條啟用中的 webhook、Main 綁著它 ⇒ 不該有 ⚠
            if (SCP_DiscordReadiness.OutboundWarnings(aData).Count != 0) aFails.Add("webhook 啟用且有綁，OutboundWarnings 卻有東西");

            // 🔴 反向對照：把明文 token 放進 secrets 資料夾 ⇒ 「Bot token」那一句要消失
            Directory.CreateDirectory(aTok.SecretsDir);
            File.WriteAllText(aTok.PlainPath, "selftest-not-a-real-token");
            if (!SCP_DiscordBot.Status(aData).Ready) aFails.Add("放了明文 token，Status.Ready 還是 false（PlainPath 的位置變了？）");
            if (SCP_DiscordReadiness.InboundBlockers(aData).Any(x => x.Contains("Bot token"))) aFails.Add("放了 token 之後還在說讀不到 Bot token");

            string aNote = aEnv ? "；⚠ 這台設了環境變數 DISCORD_INBOUND_BOT_TOKEN ⇒「沒 token」那一半沒量" : "";
            return aFails.Count == 0
                ? new CheckRow(aName, $"純函式 11 格、檔案量測 5 格都過{aNote}", CheckResult.Pass)
                : new CheckRow(aName, string.Join("；", aFails), CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aData)) Directory.Delete(aData, true); } catch { } }
    }
}
