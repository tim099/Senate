// 區塊職責：`senate cmd bilibili` 不連網的那一半（淨室）—— 署名的解析與挑選、內文檢查、要跑的 bili 參數、重複判定、留言帳本的來回。
// 物理意義：留言是寫到別人看得到的地方、用的是帳號主人的名字 ⇒ 擋不住的格子要在送出之前擋：
//           沒署名、署兩次、多組時挑到範圍外、同一個地方說兩次、帳本壞了卻當成空的。
//           連網的那一半（真的發、讀回）這裡量不到 —— 那一半的讀數在 TASK-0009。
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow BilibiliSignatureAndLedgerCleanRoom()
    {
        const string aName = "B 站留言：署名一定補上且只補一次、多組挑得到每一組、同一個地方不說兩次、帳本存得回來（淨室）";
        string aData = Path.Combine(Path.GetTempPath(), "senate_bili_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFails = new List<string>();
            const string P = "Claude-Ojousama";

            // ── 署名檔：一行一組、空行與 # 不算、重複的只留一組 ──
            List<string> aSigs = Cmd_Bilibili.ParseSignatures("# 註解\r\n——甲\n\n  ——乙  \n——甲\n");
            if (aSigs.Count != 2 || aSigs[0] != "——甲" || aSigs[1] != "——乙") aFails.Add("署名檔應該解析成 2 組：" + string.Join("|", aSigs));
            if (Cmd_Bilibili.DefaultSignature(P) != "——Claude-Ojousama") aFails.Add("預設署名應該是「——」接 persona：" + Cmd_Bilibili.DefaultSignature(P));
            if (Cmd_Bilibili.LintSignature("——甲") != null) aFails.Add("正常的署名不該被擋");
            if (Cmd_Bilibili.LintSignature("  ") == null) aFails.Add("空的署名應該擋");
            if (Cmd_Bilibili.LintSignature("一\n二") == null) aFails.Add("兩行的署名應該擋");
            if (Cmd_Bilibili.LintSignature("# 甲") == null) aFails.Add("# 開頭的署名應該擋（署名檔裡那是註解）");
            if (Cmd_Bilibili.LintSignature(new string('字', Cmd_Bilibili.SignatureMax + 1)) == null) aFails.Add("超長的署名應該擋");

            // ── 登入時的模型：預設署名自動帶；自訂的用 {model}／{persona}；用了 {model} 卻不知道模型 ⇒ 擋 ──
            if (Cmd_Bilibili.DefaultSignature(P, "claude-fable-5-2") != "——Claude-Ojousama（claude-fable-5-2）") aFails.Add("知道模型時預設署名應該帶括號：" + Cmd_Bilibili.DefaultSignature(P, "claude-fable-5-2"));
            if (Cmd_Bilibili.DefaultSignature(P, "  ") != "——Claude-Ojousama") aFails.Add("模型是空白時預設署名不該有空括號");
            if (!Cmd_Bilibili.TryExpandSignature("——{persona}｜{model}", P, "m1", out string aExp, out _) || aExp != "——Claude-Ojousama｜m1") aFails.Add("{persona}／{model} 應該被換掉：" + aExp);
            if (!Cmd_Bilibili.TryExpandSignature("——甲", P, "", out aExp, out _) || aExp != "——甲") aFails.Add("沒有佔位符的署名原樣回（不知道模型也不擋）");
            if (Cmd_Bilibili.TryExpandSignature("——甲（{model}）", P, "", out _, out string? aExpErr) || string.IsNullOrEmpty(aExpErr)) aFails.Add("用了 {model} 卻不知道模型應該擋（不能發一個空括號）");
            string aLetters = Path.Combine(aData, "letters");
            if (Cmd_Bilibili.ReadLoginModel(aLetters, P) != "") aFails.Add("沒有 model.md 應該回空字串");
            Directory.CreateDirectory(Path.Combine(aLetters, P, "profile"));
            File.WriteAllText(Path.Combine(aLetters, P, "profile", "model.md"), "\uFEFF\r\n  claude-fable-5-2  \r\n第二行\n");
            if (Cmd_Bilibili.ReadLoginModel(aLetters, P) != "claude-fable-5-2") aFails.Add("model.md 應該讀到第一個非空行：" + Cmd_Bilibili.ReadLoginModel(aLetters, P));

            // ── 挑署名：沒配置 ⇒ 預設；一組 ⇒ 那一組；多組 ⇒ 亂數；指定 ⇒ 那一組；範圍外 ⇒ 擋 ──
            var aNone = new List<string>();
            if (!Cmd_Bilibili.TryPickSignature(aNone, P, "", n => 0, out string s0, out int k0, out string src0, out _) || s0 != "——Claude-Ojousama" || k0 != 0 || src0 != "default")
                aFails.Add("沒配置應該用預設那一組");
            if (Cmd_Bilibili.TryPickSignature(aNone, P, "1", n => 0, out _, out _, out _, out string? e0) || string.IsNullOrEmpty(e0)) aFails.Add("沒配置卻指定 index 應該擋");
            if (!Cmd_Bilibili.TryPickSignature(new List<string> { "——甲" }, P, "", n => throw new Exception("只有一組不該擲亂數"), out string s1, out int k1, out string src1, out _) || s1 != "——甲" || k1 != 1 || src1 != "only")
                aFails.Add("只配一組應該用那一組、不擲亂數");
            var aSeen = new HashSet<string>();
            for (int i = 0; i < 3; i++)
            {
                int aPick = i;
                var aThree = new List<string> { "——甲", "——乙", "——丙" };
                if (!Cmd_Bilibili.TryPickSignature(aThree, P, "", n => n == 3 ? aPick : -1, out string s, out int k, out string src, out _) || k != i + 1 || src != "random") aFails.Add($"多組時亂數 {i} 應該挑到第 {i + 1} 組");
                else aSeen.Add(s);
            }
            if (aSeen.Count != 3) aFails.Add("三組應該每一組都挑得到：" + string.Join("|", aSeen));
            if (!Cmd_Bilibili.TryPickSignature(aSigs, P, "2", n => 0, out string s2, out int k2, out string src2, out _) || s2 != "——乙" || k2 != 2 || src2 != "index") aFails.Add("指定第 2 組應該拿到第 2 組");
            if (Cmd_Bilibili.TryPickSignature(aSigs, P, "3", n => 0, out _, out _, out _, out _)) aFails.Add("index 超出範圍應該擋");
            if (Cmd_Bilibili.TryPickSignature(aSigs, P, "x", n => 0, out _, out _, out _, out _)) aFails.Add("index 不是數字應該擋");
            if (Cmd_Bilibili.TryPickSignature(aSigs, P, "", n => 5, out _, out _, out _, out _)) aFails.Add("亂數回範圍外應該擋（不能拿錯的那一格）");

            // ── 組全文：內文＋換行＋署名；CRLF 收成 LF ──
            string aText = Cmd_Bilibili.Compose("第一行\r\n第二行\r\n\r\n", "——甲");
            if (aText != "第一行\n第二行\n——甲") aFails.Add("全文應該是內文接一行署名：" + aText.Replace("\n", "⏎"));

            // ── 已經署名過：等於配置的、等於預設的、「——」開頭帶名字的都算；普通的句子不算 ──
            if (!Cmd_Bilibili.AlreadySigned("哼。\n——乙", P, aSigs)) aFails.Add("末行是配置的署名應該算署過");
            if (!Cmd_Bilibili.AlreadySigned("哼。\n——Claude-Ojousama", P, aNone)) aFails.Add("末行是預設署名應該算署過");
            if (!Cmd_Bilibili.AlreadySigned("哼。\n——claude-ojousama（AI persona，借 Tim 的账号发言）", P, aNone)) aFails.Add("「——」開頭又帶名字應該算署過");
            if (Cmd_Bilibili.AlreadySigned("本小姐是 Claude-Ojousama。\n才沒有不甘心。", P, aSigs)) aFails.Add("內文提到自己的名字不該算署過");
            if (Cmd_Bilibili.AlreadySigned("你说的对——可是本小姐不服。", P, aSigs)) aFails.Add("句子中間的破折號不該算署過");

            // ── 送出前的檢查 ──
            List<string> L(bool iReply, string iTarget, string iRpid, string iRoot, string iBody)
                => Cmd_Bilibili.Lint(iReply, P, iTarget, iRpid, iRoot, iBody, aSigs, Cmd_Bilibili.Compose(iBody, "——甲"));
            if (L(false, "BV1H4x56GETJ", "", "", "哼。").Count != 0) aFails.Add("正常的根留言不該有錯");
            if (L(true, "BV1H4x56GETJ", "123", "", "哼。").Count != 0) aFails.Add("正常的回覆不該有錯");
            if (L(true, "BV1H4x56GETJ", "123", "45", "哼。").Count != 0) aFails.Add("回樓中樓（帶 root）不該有錯");
            if (L(false, "", "", "", "哼。").Count != 1) aFails.Add("沒有 target 應該一個錯");
            if (L(false, "BV1H4x56GETJ", "", "", "  \n ").Count != 1) aFails.Add("空的內文應該一個錯");
            if (!L(false, "BV1H4x56GETJ", "", "", "哼。\n——甲").Any(x => x.Contains("署名"))) aFails.Add("內文自己帶署名應該擋");
            if (L(false, "BV1H4x56GETJ", "", "", new string('字', Cmd_Bilibili.CommentMax)).Count != 1) aFails.Add("連署名超過字數應該一個錯");
            if (L(true, "BV1H4x56GETJ", "", "", "哼。").Count != 1) aFails.Add("回覆沒給 rpid 應該一個錯");
            if (L(true, "BV1H4x56GETJ", "12a", "", "哼。").Count != 1) aFails.Add("rpid 不是數字應該一個錯");
            if (L(true, "BV1H4x56GETJ", "12", "r", "哼。").Count != 1) aFails.Add("root 不是數字應該一個錯");
            if (L(false, "BV1H4x56GETJ", "12", "", "哼。").Count != 1) aFails.Add("根留言帶了 rpid 應該一個錯（那是 op=reply 的）");

            // ── 要跑的 bili 參數 ──
            string A(bool iReply, string iRpid, string iRoot, bool iDry) => string.Join(" ", Cmd_Bilibili.BuildBiliArgs(iReply, " BV1H4x56GETJ ", iRpid, iRoot, "F", iDry));
            if (A(false, "", "", false) != "comment post BV1H4x56GETJ --file F") aFails.Add("根留言的參數不對：" + A(false, "", "", false));
            if (A(false, "", "", true) != "--dry-run comment post BV1H4x56GETJ --file F") aFails.Add("dry-run 要放在最前面：" + A(false, "", "", true));
            if (A(true, "12", "", false) != "comment reply BV1H4x56GETJ 12 --file F") aFails.Add("回覆的參數不對：" + A(true, "12", "", false));
            if (A(true, "12", "7", false) != "comment reply BV1H4x56GETJ 12 --root 7 --file F") aFails.Add("回樓中樓要帶 --root：" + A(true, "12", "7", false));

            // ── 影片代號：網址、小寫前綴都收斂成同一個 ──
            if (Cmd_Bilibili.TargetKey("https://www.bilibili.com/video/BV1H4x56GETJ/?spm=1") != "BV1H4x56GETJ") aFails.Add("網址應該收斂成 BV 號");
            if (Cmd_Bilibili.TargetKey("bv1H4x56GETJ") != "BV1H4x56GETJ") aFails.Add("小寫前綴應該收斂成 BV");
            if (Cmd_Bilibili.TargetKey(" av170001 ") != "av170001") aFails.Add("不是 BV 號的原樣回");

            // ── 找 bili：資料夾裡有執行檔才算找到；沒有、資料夾不存在、PATH 裡的怪字串都回 null ──
            Directory.CreateDirectory(aData);
            string aBin = Path.Combine(aData, "bin");
            Directory.CreateDirectory(aBin);
            if (Cmd_Bilibili.FindBiliIn(new[] { "", "  ", aBin, Path.Combine(aData, "nope"), "\"<>|" }) != null) aFails.Add("資料夾裡沒有 bili 應該回 null");
            string aFake = Path.Combine(aBin, OperatingSystem.IsWindows() ? "bili.exe" : "bili");
            File.WriteAllText(aFake, "");
            if (Cmd_Bilibili.FindBiliIn(new[] { Path.Combine(aData, "nope"), aBin }) != aFake) aFails.Add("資料夾裡有 bili 應該找到它");

            // ── bili 的輸出：ok＋rpid、ok=false＋錯誤碼、不是 JSON ──
            if (!Cmd_Bilibili.TryParseBili("{\"ok\": true, \"data\": {\"rpid\": 319831969793}}", out bool aOk, out var aD, out _, out _) || !aOk || aD == null || aD["rpid"].AsLong() != 319831969793L) aFails.Add("成功的輸出應該讀到 rpid");
            if (!Cmd_Bilibili.TryParseBili("{\"ok\": false, \"error\": {\"code\": \"risk_control\", \"message\": \"m\"}}", out aOk, out _, out string aCode, out string aMsg) || aOk || aCode != "risk_control" || aMsg != "m") aFails.Add("失敗的輸出應該讀到錯誤碼");
            if (Cmd_Bilibili.TryParseBili("Traceback (most recent call last):", out _, out _, out _, out _)) aFails.Add("不是 JSON 的輸出應該回讀不懂（⇒ 不知道有沒有發）");
            if (Cmd_Bilibili.TryParseBili("", out _, out _, out _, out _)) aFails.Add("空的輸出應該回讀不懂");

            // ── 帳本：沒有檔 ⇒ 空；存 ⇒ 讀回一樣；壞檔 ⇒ null（⛔ 不能當成空的） ──
            Directory.CreateDirectory(aData);
            List<Cmd_Bilibili.CommentEntry>? aEmpty = Cmd_Bilibili.LoadLedger(aData, out string? aLErr);
            if (aEmpty == null || aEmpty.Count != 0) aFails.Add("沒有帳本檔應該讀成空清單：" + aLErr);
            var aSave = new List<Cmd_Bilibili.CommentEntry>
            {
                new() { Rpid = "100", Target = "BV1H4x56GETJ", Kind = "comment", Persona = P, Signature = "——甲", Text = "哼。\n——甲", PostedAt = "2026-11-11T00:00:00Z" },
                new() { Rpid = "101", Target = "BV1H4x56GETJ", Kind = "reply", ParentRpid = "55", RootRpid = "50", Persona = P, Signature = "——乙", Text = "才不是。\n——乙", PostedAt = "2026-11-11T00:01:00Z" },
            };
            if (!Cmd_Bilibili.TrySaveLedger(aData, aSave, out aLErr)) aFails.Add("帳本存不了：" + aLErr);
            List<Cmd_Bilibili.CommentEntry>? aBack = Cmd_Bilibili.LoadLedger(aData, out aLErr);
            if (aBack == null || aBack.Count != 2 || aBack[0].Rpid != "100" || aBack[0].Text != "哼。\n——甲" || aBack[1].ParentRpid != "55" || aBack[1].RootRpid != "50" || aBack[1].Signature != "——乙")
                aFails.Add("帳本讀回來跟存進去的不一樣：" + aLErr);

            // ── 同一個地方不說兩次 ──
            if (aBack != null)
            {
                if (Cmd_Bilibili.FindDuplicate(aBack, false, "https://www.bilibili.com/video/BV1H4x56GETJ", "")?.Rpid != "100") aFails.Add("同一支影片（網址寫法）已經留過根留言應該抓到");
                if (Cmd_Bilibili.FindDuplicate(aBack, false, "BV1Jfnu6jEe6", "") != null) aFails.Add("別支影片不該算重複");
                if (Cmd_Bilibili.FindDuplicate(aBack, true, "BV1H4x56GETJ", "55")?.Rpid != "101") aFails.Add("同一則留言已經回過應該抓到");
                if (Cmd_Bilibili.FindDuplicate(aBack, true, "BV1H4x56GETJ", "56") != null) aFails.Add("別則留言不該算回過");
                if (Cmd_Bilibili.FindDuplicate(aBack, true, "BV1H4x56GETJ", "100") != null) aFails.Add("回自己那則根留言不該被「這支影片留過了」擋");
            }
            File.WriteAllText(Cmd_Bilibili.LedgerPath(aData), "{ 這不是 JSON");
            if (Cmd_Bilibili.LoadLedger(aData, out aLErr) != null || string.IsNullOrEmpty(aLErr)) aFails.Add("壞掉的帳本應該回 null ＋ 原因（不能當成空的）");

            return aFails.Count == 0
                ? new CheckRow(aName, "署名 7 格、模型 7 格、挑選 9 格、組全文 1 格、署過判定 5 格、檢查 11 格、bili 參數 4 格、影片代號 3 格、找 bili 2 格、輸出解析 4 格、帳本與重複 9 格都過（連網的發出與讀回這裡不量）", CheckResult.Pass)
                : new CheckRow(aName, string.Join("；", aFails), CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aData)) Directory.Delete(aData, true); } catch { } }
    }
}
