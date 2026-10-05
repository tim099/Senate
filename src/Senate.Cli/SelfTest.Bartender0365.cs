// 區塊職責：TASK-0365（酒保重做）的自我對拍。
// 物理意義：五格各驗一個「錯了也不會叫」的地方：
//           ① @ 判定：別名、全形 ＠、程式碼區段裡的不算（09-29 誤觸那種）、中文別名後面直接接字也算、本名照舊。
//           ② 別名表：撞名／空白／含 @／指向不存在的人 存檔前擋；壞檔不被覆蓋；寫入端（Notify）真的把 @酒保 送進 tavern-keeper 的 inbox。
//           ③ 回應流程：被 @ 回 LLM、[help] 回說明、自己的訊息不回、引用不回、游標（上線那一刻）之前的不回、冷卻、模型失敗退罐頭。
//           ④ 🔴 上線期間回覆寫不進去 ⇒ 游標不動，下一輪補回。
//           ⑤ 設定：沒存過＝初始值且開關關、存了讀得回、壞檔不冒充「沒存過」也不被覆蓋。
// 數值影響：純記憶體＋temp 目錄；⛔ 不呼叫 ollama、不碰真實資料根與酒館。
#nullable enable
using Senate.Core;
using SCP.Core.Json;
using SCP.Core.Tavern;

namespace Senate.Cli;

public static partial class SelfTest
{
    static string BartenderTempRoot()
    {
        string d = Path.Combine(Path.GetTempPath(), "senate-selftest-bartender-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(d, "ChatTavern", "rooms", "tavern"));
        File.WriteAllText(Path.Combine(d, "ChatTavern", "identities.json"),
            "{\"identities\":[{\"id\":\"tavern-keeper\",\"display_name\":\"酒保\",\"kind\":\"npc\"},{\"id\":\"kaguya\",\"display_name\":\"輝夜\",\"kind\":\"agent\"}]}");
        return d;
    }

    static CheckRow BartenderMentionExtract()
    {
        const string aName = "酒保・@ 判定：別名／全形 ＠／程式碼區段不算／中文別名後面直接接字也算／本名照舊（TASK-0365）";
        try
        {
            var aFails = new List<string>();
            var al = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["酒保"] = "tavern-keeper", ["bartender"] = "tavern-keeper" };
            void Expect(string iBody, string[] iWant, string iWhy)
            {
                List<string> got = SCP_TavernMentions.Extract(iBody, al);
                if (!got.SequenceEqual(iWant)) aFails.Add($"{iWhy}：「{iBody}」得 [{string.Join(",", got)}]");
            }
            Expect("@酒保 來一杯", new[] { "tavern-keeper" }, "中文別名");
            Expect("＠酒保 來一杯", new[] { "tavern-keeper" }, "全形 ＠");
            Expect("@Bartender hi", new[] { "tavern-keeper" }, "ASCII 別名不分大小寫");
            Expect("@tavern-keeper 在嗎", new[] { "tavern-keeper" }, "本名");
            Expect("引用 `@酒保` 的說明", Array.Empty<string>(), "🔴 行內程式碼裡的 @ 被當成點名");
            Expect("```\n@酒保 hi\n```", Array.Empty<string>(), "🔴 程式碼區塊裡的 @ 被當成點名");
            Expect("@酒保幫我調一杯", new[] { "tavern-keeper" }, "中文別名後面直接接中文（中文不加空格）");
            Expect("@bartender2 hi", new[] { "bartender2" }, "ASCII 別名只是更長名字的開頭 ⇒ 不算別名");
            Expect("@kaguya, @酒保！", new[] { "kaguya", "tavern-keeper" }, "標點分隔、多人、照出現順序");
            Expect("@kaguya @kaguya", new[] { "kaguya" }, "去重");
            Expect("mail@example.com", new[] { "example" }, "（現況：信箱的 @ 也會取名字，交給白名單擋）");
            if (SCP_TavernMentions.Extract("@酒保", null).Count != 0) aFails.Add("沒有別名表時 @酒保 不該算到任何人");
            return new CheckRow(aName, aFails.Count == 0 ? "12 種寫法逐格對上" : string.Join("；", aFails), aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    static CheckRow BartenderAliasStore()
    {
        const string aName = "酒保・別名表：撞名／空白／含 @／指向不存在 存檔前擋、壞檔不覆蓋、寫入端把 @酒保 送進 tavern-keeper 的 inbox（TASK-0365）";
        string d = BartenderTempRoot();
        try
        {
            var aFails = new List<string>();
            var known = new[] { "tavern-keeper", "kaguya" };
            List<string> V(params (string, string)[] kv) => SCP_TavernMentionAliases.Validate(kv.Select(x => new KeyValuePair<string, string>(x.Item1, x.Item2)), known);
            if (V(("酒保", "tavern-keeper")).Count != 0) aFails.Add("正常的別名被擋");
            if (V(("Kaguya", "tavern-keeper")).Count == 0) aFails.Add("🔴 跟本名撞名（大小寫不同）沒被擋");
            if (V(("  ", "tavern-keeper")).Count == 0) aFails.Add("空白別名沒被擋");
            if (V(("@酒保", "tavern-keeper")).Count == 0) aFails.Add("含 @ 沒被擋");
            if (V(("酒 保", "tavern-keeper")).Count == 0) aFails.Add("含空白沒被擋");
            if (V(("酒保", "nobody")).Count == 0) aFails.Add("指向不存在的人沒被擋");

            var aMap = new Dictionary<string, string> { ["酒保"] = "tavern-keeper" };
            if (!SCP_TavernMentionAliases.TrySave(d, aMap, known, out List<string> e1)) aFails.Add("存不進去：" + string.Join("；", e1));
            if (!SCP_TavernMentionAliases.Load(d, out _).TryGetValue("酒保", out string? t) || t != "tavern-keeper") aFails.Add("存了讀不回");

            // 寫入端整條：Notify 讀別名表 ⇒ @酒保 進 tavern-keeper 的 inbox；引用的不進
            var inMsg = new SCP_MentionInput { Room = "tavern", Seq = 7, SenderId = "kaguya", SenderPersona = "kaguya", Body = "@酒保 來一杯" };
            SCP_MentionResult r1 = SCP_TavernMentions.Notify(d, inMsg, d);
            if (!r1.Notified.Contains("tavern-keeper")) aFails.Add($"🔴 @酒保 沒有送進 tavern-keeper 的 inbox（{string.Join(",", r1.Skipped.Concat(r1.Failures))}）");
            SCP_MentionResult r2 = SCP_TavernMentions.Notify(d, new SCP_MentionInput { Room = "tavern", Seq = 8, SenderId = "kaguya", Body = "引用 `@酒保`" }, d);
            if (r2.Notified.Count != 0) aFails.Add("引用的 @酒保 也送了通知");

            File.WriteAllText(SCP_TavernMentionAliases.PathOf(d), "{壞掉");
            if (SCP_TavernMentionAliases.Load(d, out string? aBad).Count != 0 || aBad == null) aFails.Add("壞檔沒有回錯誤");
            if (SCP_TavernMentionAliases.TrySave(d, aMap, known, out _)) aFails.Add("🔴 壞檔被安靜覆蓋了");
            SCP_MentionResult r3 = SCP_TavernMentions.Notify(d, new SCP_MentionInput { Room = "tavern", Seq = 9, SenderId = "x", Body = "@kaguya 在嗎" }, d);
            if (!r3.Notified.Contains("kaguya") || r3.Failures.Count == 0) aFails.Add("別名表壞掉時，本名要照常通知、而且要出聲");

            return new CheckRow(aName, aFails.Count == 0 ? "擋 5 種、存讀、Notify 正反、壞檔三格，逐格對上（temp 目錄）" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(d, true); } catch (IOException) { } }
    }

    static SCP_TavernMessage BMsg(int iSeq, string iSender, string iBody, string iTriggeredBy = "")
    {
        var m = new SCP_TavernMessage { Room = "tavern", Seq = iSeq, SenderId = iSender, SenderPersona = iSender == "Tim" ? "" : iSender, SenderName = iSender, Body = iBody };
        if (iTriggeredBy.Length > 0) m.Meta["triggered_by_seq"] = iTriggeredBy;
        return m;
    }

    static CheckRow BartenderProcessBatch()
    {
        const string aName = "酒保・回應流程：@ 回 LLM／[help] 回說明／自己不回／引用不回／游標之前的不回／冷卻排隊不丟／失敗退罐頭／🔴 寫不進去游標不動（TASK-0365）";
        try
        {
            var aFails = new List<string>();
            var al = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["酒保"] = "tavern-keeper" };
            BartenderSettings s = BartenderSettings.Defaults with { Enabled = true, CooldownSeconds = 0 };
            var now = new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);
            var written = new List<SCP_JsonData>();
            int nextSeq = 100;
            (bool, int, string) Write(SCP_JsonData j) { written.Add(j); return (true, ++nextSeq, ""); }
            (bool, string, string, string) Gen(string sys, string prompt) => (true, "喵～歡迎光臨", "qwen3:0.6b", "");

            var st = new BartenderState();
            var batch = new List<SCP_TavernMessage>
            {
                BMsg(11, "kaguya", "大家早"),
                BMsg(12, "kaguya", "@酒保 推薦一杯"),
                BMsg(13, "Tim", "誰會 [help]？"),
                BMsg(14, "summit", "引用 `@酒保` 的說明"),
                BMsg(15, "tavern-keeper", "@酒保 自己叫自己"),
                BMsg(9, "kaguya", "@酒保 上線前的舊訊息"),
            };
            SenateBartender.BatchOutcome o = SenateBartender.ProcessBatch(s, st, "tavern", batch, al, 10, Gen, Write, () => now);
            if (o.Cursor != 15) aFails.Add($"游標要推到 15 得 {o.Cursor}");
            if (written.Count != 2) aFails.Add($"要回 2 則（12 的 @、13 的 help）得 {written.Count}：{string.Join(" | ", written.Select(w => w["body"].AsString()))}");
            else
            {
                if (written[0]["body"].AsString() != "喵～歡迎光臨" || written[0]["meta"]["triggered_by_seq"].AsString() != "12" || written[0]["sender_id"].AsString() != "tavern-keeper")
                    aFails.Add("@ 的回覆內容／觸發 seq／身分不對");
                if (written[0]["meta"]["tag"].AsString() != SenateBartender.ReplyTag) aFails.Add("回覆的 meta.tag 不是 bartender-relay（讀取端拿它過濾）");
                if (written[1]["meta"]["reply_kind"].AsString() != "help") aFails.Add("[help] 沒有回說明");
            }
            if (o.State.RepliedToday != 1) aFails.Add($"今天回覆數要 1（help 不算）得 {o.State.RepliedToday}");

            // 冷卻：剛回過 ⇒ 停在那一則前面、冷卻結束再回（⛔ 不丟）
            var s2 = s with { CooldownSeconds = 30 };
            written.Clear();
            var o2a = SenateBartender.ProcessBatch(s2, o.State, "tavern", new[] { BMsg(17, "kaguya", "@酒保 再來") }, al, o.Cursor, Gen, Write, () => now.AddSeconds(5));
            if (written.Count != 0 || o2a.Cursor != 15 || o2a.DeferredUntilUtc == null) aFails.Add("🔴 冷卻中卻回了、或把那一則丟掉了（游標推過去）、或沒說何時再試");
            var o2 = SenateBartender.ProcessBatch(s2, o2a.State, "tavern", new[] { BMsg(17, "kaguya", "@酒保 再來") }, al, o2a.Cursor, Gen, Write, () => now.AddSeconds(60));
            if (written.Count != 1 || o2.Cursor != 17) aFails.Add("冷卻結束後沒有回那一則");

            // 模型失敗 ⇒ 罐頭句（照 seq 輪），來源標 canned-after-error
            written.Clear();
            var o3 = SenateBartender.ProcessBatch(s, o2.State, "tavern", new[] { BMsg(18, "kaguya", "@酒保 hi") }, al, o2.Cursor,
                (a, b) => (false, "", "qwen3:0.6b", "逾時"), Write, () => now.AddMinutes(5));
            if (written.Count != 1 || written[0]["body"].AsString() != SenateBartender.Canned(s, 18) || written[0]["meta"]["reply_source"].AsString() != "canned-after-error")
                aFails.Add("模型失敗沒有退回罐頭句（或來源沒標）");
            if (!o3.State.LastError.Contains("逾時")) aFails.Add("模型失敗的原因沒記進狀態");

            // 🔴 寫不進去 ⇒ 游標停在那一則前面；下一輪寫得進去就補回
            int aBefore = o3.Cursor;
            var o4 = SenateBartender.ProcessBatch(s, o3.State, "tavern", new[] { BMsg(19, "kaguya", "@酒保 Server 停了"), BMsg(20, "kaguya", "後面那則") }, al, o3.Cursor,
                Gen, j => (false, 0, "Server 沒開"), () => now.AddMinutes(10));
            if (!o4.StoppedOnWriteFailure || o4.Cursor != aBefore) aFails.Add($"🔴 寫不進去卻推了游標（{aBefore} → {o4.Cursor}）—— 這則會永遠漏掉");
            written.Clear();
            var o5 = SenateBartender.ProcessBatch(s, o4.State, "tavern", new[] { BMsg(19, "kaguya", "@酒保 Server 停了"), BMsg(20, "kaguya", "後面那則") }, al, o4.Cursor,
                Gen, Write, () => now.AddMinutes(11));
            if (written.Count != 1 || o5.Cursor != 20) aFails.Add("下一輪沒有把漏掉的那則補回");

            // 沒設模型 ⇒ 直接罐頭，不呼叫生成
            bool aCalled = false;
            written.Clear();
            SenateBartender.ProcessBatch(s with { ModelId = "" }, o5.State, "tavern", new[] { BMsg(21, "kaguya", "@酒保") }, al, o5.Cursor,
                (a, b) => { aCalled = true; return (true, "x", "m", ""); }, Write, () => now.AddMinutes(20));
            if (aCalled || written.Count != 1 || written[0]["meta"]["reply_source"].AsString() != "canned") aFails.Add("沒設模型卻呼叫了生成，或來源不是 canned");

            // 純游標移動不寫狀態檔（游標不存檔）；只有回覆／錯誤才算變動
            var o6 = SenateBartender.ProcessBatch(s, o5.State, "tavern", new[] { BMsg(22, "kaguya", "只是聊天") }, al, 21, Gen, Write, () => now.AddMinutes(30));
            if (o6.Changed || o6.Cursor != 22) aFails.Add("沒有回覆的訊息也讓狀態變動（每則都寫檔）或游標沒推");

            return new CheckRow(aName, aFails.Count == 0 ? "8 段流程逐格對上（假 LLM、假寫入）" : string.Join("；", aFails), aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    static CheckRow BartenderSettingsStore()
    {
        const string aName = "酒保・設定：沒存過＝初始值且開關關／存了讀得回／壞檔不冒充沒存過、也不被覆蓋／不合法的值擋下（TASK-0365）";
        string d = BartenderTempRoot();
        try
        {
            var aFails = new List<string>();
            BartenderSettings s0 = SenateBartender.LoadSettings(d, out string? e0);
            if (e0 != null || s0 != BartenderSettings.Defaults || s0.Enabled) aFails.Add("沒存過時不是初始值，或開關預設是開的（Unity 還沒停之前會回兩次）");
            var mine = BartenderSettings.Defaults with { Enabled = true, PersonaPrompt = "你是酒保", CannedReplies = new[] { "一句" }, NumPredict = 512 };
            if (!SenateBartender.TrySaveSettings(d, mine, out List<string> e1)) aFails.Add("存不進去：" + string.Join("；", e1));
            if (!SenateBartender.LoadSettings(d, out _).SameAs(mine)) aFails.Add("存了讀不回同一份");
            if (SenateBartender.TrySaveSettings(d, mine with { NumPredict = 3 }, out _)) aFails.Add("生成上限 3 沒被擋");
            if (SenateBartender.TrySaveSettings(d, mine with { CannedReplies = Array.Empty<string>() }, out _)) aFails.Add("沒有罐頭句沒被擋");
            File.WriteAllText(SenateBartender.SettingsPath(d), "{壞掉");
            SenateBartender.LoadSettings(d, out string? eBad);
            if (eBad == null) aFails.Add("🔴 壞檔被說成「沒存過」");
            if (SenateBartender.TrySaveSettings(d, mine, out _)) aFails.Add("🔴 壞檔被安靜覆蓋了");
            File.WriteAllText(SenateBartender.StatePath(d), "{壞掉");
            SenateBartender.LoadState(d, out string? eSt);
            if (eSt == null) aFails.Add("🔴 壞掉的狀態檔沒回錯誤（每日上限會從 0 重算）");
            return new CheckRow(aName, aFails.Count == 0 ? "初始值／存讀／擋兩種／壞檔三格，逐格對上（temp 目錄）" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(d, true); } catch (IOException) { } }
    }
}
