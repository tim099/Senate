// 區塊職責：畫像投遞的私層不外流淨室驗證。只用 temp 資料，不碰真實 persona 的 sketchbook／portraits。
// 數值影響：temp 裡造兩個 persona，寫四幅畫像後讀回兩邊的檔，最後清除 temp。
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow PortraitPrivateNeverDelivered()
    {
        const string aName = "畫像投遞：private_body 分層／body 夾私層標記 ⇒ 切開／無標記的私層樣子 ⇒ 擋下不寫";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_portrait_private_" + Guid.NewGuid().ToString("N"));
        try
        {
            string aLetters = Path.Combine(aTmp, "letters");
            var aRoot = new SCP_LettersRoot(aLetters);
            foreach (string p in new[] { "painter", "sitter" })
                Directory.CreateDirectory(SCP_LettersPaths.ProfileDir(aRoot, p));
            string aDelivDir = Path.Combine(SCP_LettersPaths.PersonaDir(aRoot, "sitter"), SCP_PortraitWriter.PortraitsDirName);
            string aSkDir = SCP_LettersPaths.SketchbookDir(aRoot, "painter");
            int Count(string d) => Directory.Exists(d) ? Directory.GetFiles(d, "*.md").Length : 0;
            // 同一秒寫兩幅會撞名（寫入端刻意不覆寫）⇒ 每幅之間跨過秒界
            void NextSecond() { int s = DateTime.UtcNow.Second; while (DateTime.UtcNow.Second == s) Thread.Sleep(20); }

            // ① 已知答案：公開層的字讀得回來（證明下面的「讀不到」不是尺壞了）
            var a1 = SCP_PortraitWriter.Write(aLetters, "painter", "sitter", "公開句ALPHA", "h", "", "私密句BETA");
            string d1 = a1.Error == null ? File.ReadAllText(a1.DeliveredPath) : "";
            string s1 = a1.Error == null ? File.ReadAllText(a1.SketchPath) : "";
            bool aSplit = a1.Error == null && d1.Contains("公開句ALPHA") && !d1.Contains("私密句BETA")
                && !d1.Contains(SCP_PortraitView.PrivateMarker) && s1.Contains("私密句BETA") && s1.Contains("has_private: true");

            // ② body 自己夾著標記與私層（09-28 外流的形狀）⇒ 標記以下不寄出、進 sketchbook
            NextSecond();
            string aBody2 = "公開句GAMMA\n\n" + SCP_PortraitView.PrivateMarker + "\n\n## 🔒 只給我自己看\n\n- 私密句DELTA";
            var a2 = SCP_PortraitWriter.Write(aLetters, "painter", "sitter", aBody2, "h", "", "");
            string d2 = a2.Error == null ? File.ReadAllText(a2.DeliveredPath) : "";
            string s2 = a2.Error == null ? File.ReadAllText(a2.SketchPath) : "";
            bool aMarkerCut = a2.Error == null && d2.Contains("公開句GAMMA") && !d2.Contains("私密句DELTA")
                && !d2.Contains("只給我自己看") && !d2.Contains(SCP_PortraitView.PrivateMarker)
                && s2.Contains("私密句DELTA") && s2.Contains("has_private: true") && a2.Report.Contains("移進私層");

            // ③ 沒標記的舊寫法（08-23～09-27 外流的形狀）⇒ 擋下，兩邊都不多一個檔
            NextSecond();
            int aDBefore = Count(aDelivDir), aSBefore = Count(aSkDir);
            var a3 = SCP_PortraitWriter.Write(aLetters, "painter", "sitter", "公開句\n\n> 🔒 **只給我自己看**\n> 私密句EPSILON", "h", "", "");
            var a4 = SCP_PortraitWriter.Write(aLetters, "painter", "sitter", "公開句\n\n### 🔒 私下記一筆\n私密句ZETA", "h", "", "");
            bool aBlocked = a3.Error != null && a4.Error != null
                && Count(aDelivDir) == aDBefore && Count(aSkDir) == aSBefore;

            // ④ 反向對照：標記以上沒有東西 ⇒ 公開層為空，照「body 必填」擋下
            var a5 = SCP_PortraitWriter.Write(aLetters, "painter", "sitter", SCP_PortraitView.PrivateMarker + "\n私密句ETA", "h", "", "");
            bool aEmpty = a5.Error != null && Count(aDelivDir) == aDBefore;

            return new(aName, $"private_body 分層={aSplit}／body 夾標記切開={aMarkerCut}／無標記私層擋下={aBlocked}／切完公開層空擋下={aEmpty}",
                aSplit && aMarkerCut && aBlocked && aEmpty ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new(aName, e.Message, CheckResult.Fail); }
        finally { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); }
    }
}
