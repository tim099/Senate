// 區塊職責：`senate cmd youtube` 播放清單那一半不連網的部分（淨室）—— 清單交付單的解析與檢查、影片 id 的檢查、清單帳本的來回。
// 物理意義：建清單與加影片是寫到別人看得到的頻道上的事，擋不住的格子要在送出之前擋：
//           公開度沒寫（API 會當成公開）、欄位名打錯、加的不是本工具上傳的影片、同一支寫兩次。
//           連網的那一半（真的建、真的加）這裡量不到 —— 那一半的讀數在 TASK-0006 的附件。
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow YouTubePlaylistSlipAndLedgerCleanRoom()
    {
        const string aName = "YouTube 播放清單：交付單沒寫公開度／欄位打錯／影片不是本工具傳的都要擋，清單帳本存得回來（淨室）";
        string aData = Path.Combine(Path.GetTempPath(), "senate_ytpl_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFails = new List<string>();
            var aUploaded = new HashSet<string>(StringComparer.Ordinal) { "vidA", "vidB" };

            // ── 解析 ──
            if (!Cmd_YouTube.TryParsePlaylistSlip("{\"persona\":\"p\",\"title\":\"T\",\"description\":\"d\",\"privacy\":\"public\",\"videos\":[\"vidA\",\"vidB\"]}",
                    out Cmd_YouTube.PlaylistSlip aOk, out List<string> aErr) || aErr.Count != 0)
                aFails.Add("正常的交付單讀不了：" + string.Join("／", aErr));
            else if (aOk.Title != "T" || aOk.Privacy != "public" || aOk.Videos.Count != 2 || aOk.Videos[1] != "vidB")
                aFails.Add("正常的交付單讀出來的值不對");
            if (Cmd_YouTube.TryParsePlaylistSlip("{\"persona\":\"p\",\"title\":\"T\",\"privacy_status\":\"private\"}", out _, out aErr) || !aErr.Any(x => x.Contains("privacy_status")))
                aFails.Add("欄位名打錯（privacy_status）應該擋下並點名那一欄");
            if (Cmd_YouTube.TryParsePlaylistSlip("{\"persona\":\"p\",\"title\":\"T\",\"privacy\":\"public\",\"videos\":\"vidA\"}", out _, out aErr) || !aErr.Any(x => x.Contains("videos")))
                aFails.Add("videos 不是陣列應該擋");
            if (Cmd_YouTube.TryParsePlaylistSlip("[1,2]", out _, out aErr)) aFails.Add("不是物件的 JSON 應該擋");

            // ── 檢查 ──
            Cmd_YouTube.PlaylistSlip Slip(string iTitle, string iPrivacy, params string[] iVideos)
                => new() { Persona = "p", Title = iTitle, Privacy = iPrivacy, Videos = iVideos.ToList() };
            if (Cmd_YouTube.LintPlaylist(Slip("T", "public", "vidA"), "", aUploaded).Count != 0) aFails.Add("正常的交付單不該有錯");
            List<string> aNoPriv = Cmd_YouTube.LintPlaylist(Slip("T", ""), "", aUploaded);
            if (aNoPriv.Count != 1 || !aNoPriv[0].Contains("privacy")) aFails.Add("沒寫公開度應該剛好一個錯、點名 privacy（API 沒給就是公開）");
            if (Cmd_YouTube.LintPlaylist(Slip("T", "friends"), "", aUploaded).Count != 1) aFails.Add("表外的公開度應該一個錯");
            if (Cmd_YouTube.LintPlaylist(Slip("", "private"), "", aUploaded).Count != 1) aFails.Add("沒有標題應該一個錯");
            if (Cmd_YouTube.LintPlaylist(Slip(new string('字', Cmd_YouTube.PlaylistTitleMax + 1), "private"), "", aUploaded).Count != 1) aFails.Add("標題超長應該一個錯");
            if (Cmd_YouTube.LintPlaylist(Slip("a<b", "private"), "", aUploaded).Count != 1) aFails.Add("標題有角括號應該一個錯");
            List<string> aNotMine = Cmd_YouTube.LintPlaylist(Slip("T", "private", "vidA", "other"), "", aUploaded);
            if (aNotMine.Count != 1 || !aNotMine[0].Contains("other")) aFails.Add("不是本工具上傳的影片應該擋、點名那一支");
            if (!Cmd_YouTube.LintPlaylist(Slip("T", "private", "vidA", "vidA"), "", aUploaded).Any(x => x.Contains("不只一次"))) aFails.Add("同一支寫兩次應該擋");
            if (!Cmd_YouTube.LintPlaylist(Slip("T", "private", "vidA"), "", null).Any(x => x.Contains("帳本"))) aFails.Add("上傳帳本讀不了又有 videos 應該擋");
            if (Cmd_YouTube.LintPlaylist(Slip("T", "private"), "", null).Count != 0) aFails.Add("沒寫 videos 時，帳本讀不了不該擋");
            if (!Cmd_YouTube.LintPlaylist(Slip("T", "private"), aData, aUploaded).Any(x => x.Contains("persona"))) aFails.Add("信件庫裡沒有這個 persona 應該擋");

            // ── 清單帳本：沒有檔 ⇒ 空；存 ⇒ 讀回一樣；壞檔 ⇒ null（⛔ 不能當成空的） ──
            Directory.CreateDirectory(aData);
            List<Cmd_YouTube.PlaylistEntry>? aEmpty = Cmd_YouTube.LoadPlaylistLedger(aData, out string? aLErr);
            if (aEmpty == null || aEmpty.Count != 0) aFails.Add("沒有帳本檔應該讀成空清單：" + aLErr);
            var aSave = new List<Cmd_YouTube.PlaylistEntry> { new() { PlaylistId = "PL1", Persona = "p", Title = "標題｜一", Privacy = "public", CreatedAt = "2026-11-10T00:00:00Z" } };
            if (!Cmd_YouTube.TrySavePlaylistLedger(aData, aSave, out aLErr)) aFails.Add("帳本存不了：" + aLErr);
            List<Cmd_YouTube.PlaylistEntry>? aBack = Cmd_YouTube.LoadPlaylistLedger(aData, out aLErr);
            if (aBack == null || aBack.Count != 1 || aBack[0].PlaylistId != "PL1" || aBack[0].Title != "標題｜一" || aBack[0].Privacy != "public")
                aFails.Add("帳本讀回來跟存進去的不一樣：" + aLErr);
            File.WriteAllText(Cmd_YouTube.PlaylistLedgerPath(aData), "{ 這不是 JSON");
            if (Cmd_YouTube.LoadPlaylistLedger(aData, out aLErr) != null || string.IsNullOrEmpty(aLErr)) aFails.Add("壞掉的帳本應該回 null ＋ 原因（不能當成空的蓋掉）");

            return aFails.Count == 0
                ? new CheckRow(aName, "解析 4 格、檢查 11 格、帳本 4 格都過（連網的建立與加入這裡不量）", CheckResult.Pass)
                : new CheckRow(aName, string.Join("；", aFails), CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aData)) Directory.Delete(aData, true); } catch { } }
    }
}
