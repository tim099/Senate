// 區塊職責：catchup 的 inbox 預覽（TASK-0417）與 tavern-query kind=seq 的區間參數（TASK-0422）的自我對拍。
// 物理意義：兩格都是「答案格式正常、內容答的是別的問題」：
//           0417 —— 預覽印的是本文後面那行（📖 新詞附註／建議前往），而本文第一行被當成引文跳過
//           0422 —— 只給 from（或只給 to）時那一邊被丟掉，回成最後 4000 則、exit 0
// 數值影響：在 temp 目錄造資料根，跑完刪；⛔ 不碰真實資料根。inbox 由真正的寫入端（SCP_TavernMentions.Notify）寫，⛔ 不在這裡抄格式。
#nullable enable
using SCP.Core.Cmd;
using SCP.Core.Tavern;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow TavernInboxPreviewCleanRoom()
    {
        const string aName = "catchup inbox 預覽：取本文第一行（帶新詞附註／短本文／多行本文）、檔頭與 truncated 告示不算條目（淨室，TASK-0417）";
        string d = Path.Combine(Path.GetTempPath(), "senate_inboxpreview_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFails = new List<string>();
            Directory.CreateDirectory(Path.Combine(d, "ChatTavern", "rooms", "tavern", "messages"));
            File.WriteAllText(Path.Combine(d, "ChatTavern", "identities.json"),
                "{\"identities\":[{\"id\":\"probe\",\"display_name\":\"探針\",\"kind\":\"agent\"},{\"id\":\"meadow\",\"display_name\":\"草地\",\"kind\":\"agent\"}]}");

            const string aGloss = "\n\n---\n\n📖 **本回提到的新詞** (auto-attached by Cmd_Glossary):\n\n- **probe 大小姐**: 探針";
            void Mention(int iSeq, string iBody)
            {
                var r = SCP_TavernMentions.Notify(d, new SCP_MentionInput { Room = "tavern", Seq = iSeq, SenderId = "meadow", SenderPersona = "meadow", Body = iBody }, d);
                if (!r.Notified.Contains("probe")) aFails.Add($"seq {iSeq} 沒寫進 inbox（{string.Join(",", r.Skipped.Concat(r.Failures))}）");
            }
            Mention(1, "@probe 第一則的本文" + aGloss);   // 實測形狀：seq 21018
            Mention(2, "@probe 短本文");                   // 實測形狀：seq 20912（沒有附註 ⇒ 舊版印「建議前往」）
            Mention(3, "@probe 多行的第一行\n多行的第二行");

            string aRaw = File.ReadAllText(SCP_TavernInbox.InboxPath(d, "tavern", "probe"));
            // 🔴 反向：寫入端的形狀沒變 —— 本文第一行確實帶 `>`（不然這一格測的是別的東西）
            if (!aRaw.Contains("> @probe 第一則的本文")) aFails.Add("寫入端不再把本文寫成 `>` 引文 ⇒ 本自測的前提變了，要重看");

            var (aTitles, aSnips, _) = SCP_TavernCatchup.ParseInboxEntries(aRaw);
            if (aTitles.Count != 3) aFails.Add($"條目 {aTitles.Count} 筆（期望 3：檔頭的 `>` 行不能變成條目）");
            else
            {
                if (aSnips[0] != "@probe 第一則的本文") aFails.Add($"🔴 帶新詞附註：預覽＝「{aSnips[0]}」");
                if (aSnips[1] != "@probe 短本文") aFails.Add($"🔴 短本文：預覽＝「{aSnips[1]}」");
                if (aSnips[2] != "@probe 多行的第一行") aFails.Add($"多行本文：預覽＝「{aSnips[2]}」（期望第一行，不是第二行）");
            }

            // 🔴 反向：truncated 告示（也是 `>` 行、在檔頭位置）不算條目；條目照常解析
            var (aT2, aS2, _) = SCP_TavernCatchup.ParseInboxEntries(
                "> ⚠ **inbox truncated** — 3 條較舊待辦已歸檔\n\n## [seq=9] 💬 x @妳 (2026-01-01 00:00:00 +08)\n_at 2026-01-01T00:00:00.000Z_\n\n> 本文\n");
            if (aT2.Count != 1 || aS2[0] != "本文") aFails.Add($"truncated 告示：條目 {aT2.Count} 筆、預覽「{(aS2.Count > 0 ? aS2[0] : "")}」（期望 1 筆、「本文」）");

            // 端到端：catchup 回傳的本文裡印的是本文，⛔ 不是附註
            var aB = SCP_TavernCatchup.Build(d, d, "probe", "tavern", 1, true, false, 10);
            if (!aB.Body.Contains("↳ @probe 第一則的本文")) aFails.Add("🔴 catchup 輸出沒有印出本文預覽");
            if (aB.Body.Contains("↳ 📖") || aB.Body.Contains("↳ 建議前往")) aFails.Add("🔴 catchup 輸出還有附註／建議前往當預覽");

            return new CheckRow(aName,
                aFails.Count == 0 ? "三種本文都取到第一行；檔頭／truncated 告示不算條目；catchup 端到端印出本文（temp 目錄）" : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(d, true); } catch (Exception) { } }
    }

    static CheckRow TavernQuerySeqRangeCleanRoom()
    {
        const string aName = "tavern-query kind=seq：只給 from ＝ 到最新；只給 to／to<from／seq 與 from 並給 ⇒ exit 2；from+to 與不給照舊（淨室，TASK-0422）";
        string d = Path.Combine(Path.GetTempPath(), "senate_seqrange_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFails = new List<string>();
            string aDir = Path.Combine(d, "ChatTavern", "rooms", "tavern", "messages", "2026-01-01");
            Directory.CreateDirectory(aDir);
            var aBase = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            const int aTotal = 30;
            for (int i = 1; i <= aTotal; i++)
                File.WriteAllText(Path.Combine(aDir, i.ToString("D8") + ".json"),
                    "{\"ts\":\"" + aBase.AddSeconds(i).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)
                    + "\",\"sender_persona\":\"probe\",\"sender_id\":\"probe\",\"kind\":\"chat\",\"body\":\"m" + i + "\"}");

            string aCmd = SCP_CmdRegistry.NameOf<SCP_Cmd_TavernQuery>();
            SCP_CmdResult Q(params (string K, string V)[] iArgs)
            {
                var a = new Dictionary<string, string>(StringComparer.Ordinal) { ["data_root"] = d, ["kind"] = "seq" };
                foreach (var (k, v) in iArgs) a[k] = v;
                return SCP_CmdRegistry.Dispatch(aCmd, a);
            }
            string Text(SCP_CmdResult r) => string.Join("\n", r.Lines);
            int Hits(SCP_CmdResult r) => r.Lines.Count(l => l.StartsWith("- **[seq ", StringComparison.Ordinal));

            // ① 只給 from ⇒ 25..30，⛔ 不是最後 4000 則
            var r1 = Q(("from", "25"));
            string t1 = Text(r1);
            if (!r1.Ok || !t1.Contains("seq 25-30") || Hits(r1) != 6 || !t1.Contains("[seq 25]") || t1.Contains("[seq 24]"))
                aFails.Add($"🔴 只給 from=25：exit {r1.ExitCode}、命中 {Hits(r1)}（期望 6）、標頭「{r1.Lines.FirstOrDefault()}」");

            // ② 組不起來的區間 ⇒ exit 2，⛔ 不退回最後 4000 則
            foreach (var (aLabel, aArgs) in new[]
                     {
                         ("只給 to", new[] { ("to", "10") }),
                         ("to < from", new[] { ("from", "10"), ("to", "5") }),
                         ("seq 與 from 並給", new[] { ("seq", "3"), ("from", "1") }),
                         ("seq 與 to 並給", new[] { ("seq", "3"), ("to", "9") }),
                     })
            {
                var r = Q(aArgs);
                if (r.ExitCode != 2 || Text(r).Contains("最後 4000 則內")) aFails.Add($"🔴 {aLabel}：exit {r.ExitCode}（期望 2）");
            }

            // 🔴 ③ 反向：from+to、單一 seq、都不給 —— 照舊
            var r3 = Q(("from", "3"), ("to", "5"));
            if (!r3.Ok || !Text(r3).Contains("seq 3-5") || Hits(r3) != 3) aFails.Add($"from+to 變了：exit {r3.ExitCode}、命中 {Hits(r3)}（期望 3）");
            var r4 = Q(("seq", "7"));
            if (!r4.Ok || !Text(r4).Contains("seq 7") || Hits(r4) != 1) aFails.Add($"單一 seq 變了：命中 {Hits(r4)}（期望 1）");
            var r5 = Q();
            if (!r5.Ok || !Text(r5).Contains("最後 4000 則內") || Hits(r5) != aTotal) aFails.Add($"都不給變了：命中 {Hits(r5)}（期望 {aTotal}）");

            return new CheckRow(aName,
                aFails.Count == 0 ? "只給 from 命中 6（25..30）；4 種組不起來的區間 exit 2；from+to／seq／不給照舊（temp 目錄）" : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(d, true); } catch (Exception) { } }
    }
}
