// 區塊職責：TASK-0373（見林歸檔見叢前的交接閘）的自我對拍。
// 物理意義：五格各驗一個「錯了也不會叫」的地方：
//           🔴 ① 擋：當期見叢有未完、兩個出口都沒給 ⇒ exit 2，而且**什麼都沒寫**（見林不在、見叢位元組不變）。
//           ② 帶：keys_carry 三行（含 `- [ ]` 前綴與舊時戳註解）⇒ 新見叢恰好 3 條、每行只有一個時戳、歸檔檔尾端有交接節。
//           ③ 放：keys_drop_reason ⇒ 放行、新見叢不存在、理由留在歸檔檔尾端。
//           ④ 不變：當期見叢沒有未完 ⇒ 不給任何新參數也照舊放行。
//           ⑤ 空帶：keys_carry 只有標題／空行 ⇒ 擋（那不是「判過、決定不帶」）。
// 數值影響：在 temp 目錄造 letters 根與四個 persona，跑完刪；⛔ 不碰真實資料根。
#nullable enable
using SCP.Core.Cmd;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow KeysGateCleanRoom()
    {
        const string aName = "見林見叢閘：有未完沒判⇒擋且零寫入／keys_carry 帶進新見叢／drop_reason 留名／無未完照舊／空帶擋（淨室，TASK-0373）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_keysgate_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aLetters = aTmp.Replace('\\', '/');
            const string aKeysHead = "---\ntype: keys_open\npersona: p\nopened_at: 2026-01-01T00:00:00.000000Z\n---\n\n# 🌿 見叢\n\n";
            string Seed(string iWho, string iItems)
            {
                string aDir = Path.Combine(aTmp, iWho);
                Directory.CreateDirectory(Path.Combine(aDir, "wakes"));
                string aKeys = Path.Combine(aDir, "_keys_open.md");
                File.WriteAllText(aKeys, aKeysHead + iItems);
                return aKeys;
            }
            const string aTwoOpen = "- [ ] 甲還沒做  <!-- 2026-01-01T00:00:01.000000Z -->\n- [x] 乙做完了  <!-- 2026-01-01T00:00:02.000000Z -->\n- [ ] 丙還沒做  <!-- 2026-01-01T00:00:03.000000Z -->\n";
            SCP_CmdResult Run(string iWho, params (string K, string V)[] iExtra)
            {
                var aArgs = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["letters_root"] = aLetters, ["persona"] = iWho, ["digest_body"] = "# 探針見林\n本段沒有同事互動（淨室）。\n",
                    ["wake"] = "10", ["span_start"] = "1", ["span_end"] = "10",
                };
                foreach (var (k, v) in iExtra) aArgs[k] = v;
                return SCP_CmdRegistry.Dispatch("consolidate", aArgs);
            }
            string V(SCP_CmdResult iR, string iKey) => iR.Values.LastOrDefault(kv => kv.Key == iKey).Value ?? "";
            bool DigestExists(string iWho) => File.Exists(Path.Combine(aTmp, iWho, "longterm", "wake_001-010.md"));
            string Archive(string iWho) => Path.Combine(aTmp, iWho, "keys", "wake_001-010.md");

            // 🔴 ① 擋且零寫入
            string aK1 = Seed("block", aTwoOpen);
            byte[] aBefore = File.ReadAllBytes(aK1);
            SCP_CmdResult aR1 = Run("block");
            bool aBlock = aR1.ExitCode == 2 && V(aR1, "keys_gate") == "blocked_open" && V(aR1, "keys_open_count") == "2"
                          && !DigestExists("block") && File.ReadAllBytes(aK1).SequenceEqual(aBefore)
                          && string.Join("\n", aR1.Lines).Contains("丙還沒做");

            // ② 帶三條（前綴與舊時戳註解要被剝掉）
            Seed("carry", aTwoOpen);
            SCP_CmdResult aR2 = Run("carry", ("keys_carry",
                "# 整理後\n- [ ] 甲還沒做  <!-- 2026-01-01T00:00:01.000000Z -->\n- 丙改寫成這樣\n\n丁是合併出來的\n"));
            string aNewKeys = Path.Combine(aTmp, "carry", "_keys_open.md");
            string[] aOpenLines = File.Exists(aNewKeys)
                ? File.ReadAllLines(aNewKeys).Where(l => l.StartsWith("- [ ]", StringComparison.Ordinal)).ToArray()
                : Array.Empty<string>();
            bool aOneStamp = aOpenLines.All(l => l.Split("<!--").Length == 2);
            bool aCarry = aR2.ExitCode == 0 && DigestExists("carry") && aOpenLines.Length == 3 && aOneStamp
                          && aOpenLines[0].StartsWith("- [ ] 甲還沒做  <!-- ", StringComparison.Ordinal)
                          && aOpenLines[2].Contains("丁是合併出來的") && V(aR2, "keys_carried") == "3"
                          && File.Exists(Archive("carry")) && File.ReadAllText(Archive("carry")).Contains("交接到下一期")
                          && File.ReadAllText(Archive("carry")).Contains("丙還沒做");   // 原文仍在歸檔裡

            // ③ 放掉，理由留名
            Seed("drop", aTwoOpen);
            SCP_CmdResult aR3 = Run("drop", ("keys_drop_reason", "探針：兩條都已轉成碎片"));
            bool aDrop = aR3.ExitCode == 0 && DigestExists("drop") && !File.Exists(Path.Combine(aTmp, "drop", "_keys_open.md"))
                         && File.ReadAllText(Archive("drop")).Contains("探針：兩條都已轉成碎片");

            // ④ 沒有未完 ⇒ 照舊
            Seed("clean", "- [x] 全做完了  <!-- 2026-01-01T00:00:01.000000Z -->\n");
            SCP_CmdResult aR4 = Run("clean");
            bool aClean = aR4.ExitCode == 0 && DigestExists("clean") && File.Exists(Archive("clean"))
                          && !File.Exists(Path.Combine(aTmp, "clean", "_keys_open.md"));

            // ⑤ 空帶 ⇒ 擋
            Seed("empty", aTwoOpen);
            SCP_CmdResult aR5 = Run("empty", ("keys_carry", "# 只有標題\n\n> 引言\n"));
            bool aEmpty = aR5.ExitCode == 2 && V(aR5, "keys_gate") == "blocked_empty_carry" && !DigestExists("empty");

            bool aOk = aBlock && aCarry && aDrop && aClean && aEmpty;
            return new CheckRow(aName,
                $"🔴 有未完沒判⇒擋且零寫入={aBlock}（exit {aR1.ExitCode}）／帶 3 條⇒新見叢 {aOpenLines.Length} 條、單一時戳={aOneStamp}、歸檔有交接節={aCarry}"
                + $"／drop_reason 留名={aDrop}／無未完照舊={aClean}／空帶擋={aEmpty}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }
}
