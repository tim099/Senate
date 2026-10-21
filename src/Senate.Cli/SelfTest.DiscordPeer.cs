// 區塊職責：Discord Inbound 的同伴 webhook 判準 —— 本機 webhook 發的不收回來、別台 Senate 的 webhook 照收（淨室）。
// 物理意義：兩台 Senate 接同一組 Discord 頻道時，各自的 Outbound 用自己的 webhook 送；
//           Inbound 只丟「本機登記的 webhook id」⇒ 自己的話不會繞回來，別台的話收得到。
using SCP.Core.Discord;
using SCP.Core.Json;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow DiscordPeerWebhookCleanRoom()
    {
        const string aName = "Discord Inbound 同伴 webhook：本機的丟、別台的收、標頭拆進 meta、設定壞了一律丟（淨室）";
        string aData = Path.Combine(Path.GetTempPath(), "senate_dpeer_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aCfgDir = Path.Combine(aData, "ChatTavern", "discord");
            Directory.CreateDirectory(aCfgDir);
            string aCfg = Path.Combine(aCfgDir, SCP_DiscordPaths.ConfigFileName);
            File.WriteAllText(aCfg, "{\"webhooks\":[{\"id\":\"111\",\"enabled\":false}]}");

            var aRoute = new SCP_DiscordRoute { ChannelId = "9", TavernRoom = "tavern", Label = "probe" };
            var aWl = new SCP_DiscordWhitelist();
            string Msg(string iWebhook, bool iBot, string iUser, string iContent)
                => "{\"id\":\"1554045138668486738\",\"content\":\"" + iContent + "\""
                   + (iWebhook.Length > 0 ? ",\"webhook_id\":\"" + iWebhook + "\"" : "")
                   + ",\"author\":{\"id\":\"" + (iWebhook.Length > 0 ? iWebhook : "42") + "\",\"username\":\"" + iUser + "\"" + (iBot ? ",\"bot\":true" : "") + "}}";
            string Conv(string iJson, out SCP_DiscordInboundItem? oItem)
                => SCP_DiscordInbound.Convert(aData, aData, aRoute, aWl, SCP_JsonParser.Parse(iJson), out oItem, false);

            var aFails = new List<string>();
            const string aHeaderBody = "**`tavern`** · seq 5 · 10-21 10:00\\n午安";

            // 本機 webhook（停用中也算自己的）⇒ 丟
            string aOwn = Conv(Msg("111", true, "Abigail", aHeaderBody), out var aOwnItem);
            if (aOwn != "own-webhook" || aOwnItem != null) aFails.Add($"本機 webhook 應丟（得 {aOwn}）");

            // 別台的 webhook（作者帶 bot）⇒ 收；標頭拆進 meta、顯示名＝username、不加白名單後綴
            string aPeerWhy = Conv(Msg("222", true, "gura", aHeaderBody), out var aPeer);
            SCP_JsonData aMeta = aPeer?.MsgJson["meta"] ?? SCP_JsonData.NewObject();
            if (aPeer == null) aFails.Add($"別台 webhook 應收（得 {aPeerWhy}）");
            else
            {
                if (aPeer.MsgJson.GetString("body", "") != "午安") aFails.Add($"標頭沒拆掉（body={aPeer.MsgJson.GetString("body", "")}）");
                if (aPeer.MsgJson.GetString("sender_id", "") != "discord:222") aFails.Add("sender_id 應是 discord:222");
                if (aPeer.MsgJson.GetString("sender_name", "") != "gura") aFails.Add($"顯示名應是 gura（得 {aPeer.MsgJson.GetString("sender_name", "")}）");
                if (aMeta.GetString("source", "") != "discord") aFails.Add("meta.source 應是 discord（Outbound 才不會回送）");
                if (aMeta.GetString("discord_peer", "") != "true" || aMeta.GetString("peer_room", "") != "tavern" || aMeta.GetString("peer_seq", "") != "5")
                    aFails.Add("meta 少了 discord_peer／peer_room／peer_seq");
            }

            // 一般 bot（不是 webhook）⇒ 照舊丟
            string aBot = Conv(Msg("", true, "somebot", "嗨"), out var aBotItem);
            if (aBot != "bot-author" || aBotItem != null) aFails.Add($"一般 bot 應丟（得 {aBot}）");

            // 🔴 反向對照：真人訊息照收
            Conv(Msg("", false, "tim", "嗨"), out var aHuman);
            if (aHuman == null) aFails.Add("真人訊息應收");

            // 設定檔壞了 ⇒ webhook 訊息一律當成自己的丟掉（寧可漏收同伴，不冒回收自己的險）
            File.WriteAllText(aCfg, "{ broken");
            string aBroken = Conv(Msg("222", true, "gura", aHeaderBody), out var aBrokenItem);
            if (!aBroken.StartsWith("webhook-msg") || aBrokenItem != null) aFails.Add($"設定壞了應一律丟（得 {aBroken}）");

            return new CheckRow(aName, aFails.Count == 0
                ? "本機（含停用）⇒ own-webhook；別台 ⇒ 收、body 去標頭、peer_room=tavern／peer_seq=5、source=discord；一般 bot 丟；真人收；設定壞 ⇒ 丟"
                : string.Join("／", aFails), aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, $"例外：{e.GetType().Name}: {e.Message}", CheckResult.Fail); }
        finally { try { Directory.Delete(aData, true); } catch { } }
    }
}
