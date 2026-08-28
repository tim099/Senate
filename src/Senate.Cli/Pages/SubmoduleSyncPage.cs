// 區塊職責：Git Submodule 狀態頁 —— **唯讀**，但把「這一輪打算怎麼做」完整表達得出來：
//           哪些 submodule 納入、每一顆的目標 branch 是哪條（以及那個目標是哪一層解析出來的）、
//           要不要含 root、要不要推所有 remote，最後**組出一條可以直接照抄去跑的指令**。
// 物理意義：概念取自 Unity 端的 UCL_GitSubmoduleSyncPage，但**這一頁刻意不放寫入鈕**。
//           理由是宿主形狀，不是保守：CLI 一次呼叫一顆 process，
//           一輪 fetch＋pull＋push 跨十幾個 submodule 是分鐘級的事，
//           塞進「按鈕按下去那一幀」在 CLI 模式做不到 ⇒ 會變成一顆按了沒事的鈕，
//           而那比沒有鈕糟。寫入端走 `senate submodule sync`。
//           ⭐ 那條理由**只約束到「誰動手」，不約束「誰決定」** ——
//             UCL 那頁的價值有一半在逐項設定（排除哪幾顆、這顆要切哪條 branch），
//             而那半完全是唯讀的。本頁把它補上，然後把意圖**編譯成指令**：
//             使用者在畫面上把範圍調對，複製一行去跑，動手的仍然是 CLI。
//           ⇒ 分界線落在「決定」與「動手」之間，而不是落在「有沒有這個功能」。
// 數值影響：掃描唯讀。`submodule/fetch` 開著時掃描會先逐顆 fetch（只動 remote-tracking ref，
//           不碰工作目錄），關著時 ahead/behind 以上次 fetch 為準 —— 而那個新鮮度**逐列標**。
//           本頁自己不跑任何寫入型 git。
// ⚠ 顏色：共用層沒有顏色 API（一份頁面碼要同時輸出純文字，顏色傳不過去）⇒
//   狀態一律用字形（⛔ / ⚠ / ✓ / ⏭）表達。那不是降級，是讓四種 renderer 講同一句話。
// ⚠ 逐項互動**不在表格裡**：`SCP_Ui.Table` 的一列只吃字串（`TableRow(params string[])`），
//   放不進 Toggle／Dropdown。所以表格是唯讀總覽，逐項設定另開一個可摺疊區塊 ——
//   而不是為了「一列一個勾選」去給中間層新增一種節點型別
//   （新增一種 Kind 要同時改五個地方，漏掉的那一處不報錯，只會某個 renderer 少畫一塊）。
using SCP.Core.Git;
using SCP.Core.Gui;
using Senate.Core;

namespace Senate.Cli.Pages;

public sealed class SubmoduleSyncPage : SCP_GuiToolPage
{
    readonly SenateModel m_Model;

    /// <summary>上一次掃描的照片。null ＝ 還沒掃過（跟「掃過但沒有 submodule」不同形）。</summary>
    SubmoduleScanResult? m_Scan;

    /// <summary>
    /// 掃描時用的設定指紋 —— 「這份照片是用什麼設定拍的」。
    /// <para>⚠ 這一格是**防每幀重掃**的那道閘，不是裝飾：Desktop 是連續 render loop
    /// （每秒約 60 次 Draw），而 <see cref="Rescan"/> 會同步跑一整輪 git。
    /// 少了這個比對，「設定改了就重掃」會變成「每秒對每顆 submodule 跑 60 輪 git」——
    /// 而那個症狀是視窗整個卡死，不是一行錯誤訊息。</para>
    /// </summary>
    string m_ScannedFingerprint = "";

    // 區塊職責：草稿 ↔ 生效值的兩組 id。
    // 物理意義：打字欄位是**草稿**（`…`），按下「套用」才寫進**生效值**（`…/applied`）。
    // 🩸 為什麼生效值不能只放在頁面欄位（第一版就是那樣）：
    //   CLI **每次呼叫都是新 process**，頁面實例重新建、`m_Scan` 是 null ⇒
    //   「我上一步套用的是 LY」整個丟失，於是每個指令都得先按一次「套用」。
    //   實測：`--set submodule/root=X` 之後按「放棄改動」，欄位回到 Senate 而不是剛套用的 LY。
    //   ⇒ 生效值住 session（跟 Dropdown 的 `<key>/value` 同一個模式），跨 process 活得下來。
    // ⚠ 那兩個 `…/applied` 是**內部狀態不是畫面元件** ⇒ `--set` 會被「畫面上沒有這個 id」擋下
    //   （那是對的：要換 repo 就走 `--set submodule/root=…` ＋ `--click submodule/apply`，
    //     跟人在畫面上做的動作完全一樣）。
    const string RootAppliedId = "submodule/root/applied";
    const string BranchFieldId = "submodule/default-branch";
    const string BranchAppliedId = "submodule/default-branch/applied";

    /// <summary>`: base()` 讓 [CallerFilePath] 填 SourceFilePath（隱式 base() 會是 null）。</summary>
    public SubmoduleSyncPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public const string PageKey = "submodule";

    public override string Title => "Submodule 狀態";

    /// <summary>列進入口頁的「診斷」組（跟 Doctor 同一組 —— 它們回答的都是「現在是什麼狀態」）。</summary>
    public override string? MenuGroup => "診斷";

    /// <summary>「回到自動」那個選項的 value。空字串當不了 value —— 下拉會顯示「(未選)」，
    /// 而「沒選」跟「我要自動」是兩件事。</summary>
    const string AutoValue = "(auto)";

    /// <summary>
    /// ⚠ **這裡刻意不掃描**（第一版在這裡跑 <c>Rescan</c>）。
    /// <para>理由：要掃誰取決於 session 裡的生效值（<c>submodule/root/applied</c>），
    /// 而讀 session 需要 <see cref="SCP_Ui"/> —— 這個時機還沒有。
    /// 在這裡掃就只能掃「Senate 自己」，然後 <c>DrawContent</c> 發現生效值是 LY 再掃第二次
    /// ⇒ **每次開頁白跑一輪 git**（LY 是 24 顆）。</para>
    /// <para>⇒ 掃描由 <c>DrawContent</c> 的指紋比對驅動（`m_Scan == null` 也算不符），
    /// 那裡才知道生效值是什麼。建構子同理不碰磁碟（頁面目錄會建一次實例再丟掉）。</para>
    /// </summary>
    public override void OnPush()
    {
        base.OnPush();
    }

    /// <summary>
    /// 重新掃描一顆鈕，**fetch 與否由下面那個開關決定**。
    /// <para>🩸 為什麼不是兩顆鈕（原本是「重新掃描」＋「Fetch 全部後掃描」）：
    /// fetch 開關要進掃描指紋（否則它是一顆開了不會有事的開關），
    /// 而一旦進了指紋，一顆「這次帶 fetch」的鈕就會在**下一幀**被
    /// 「指紋說現在是不帶 fetch」的重掃蓋掉 —— 那顆鈕按了等於沒按，
    /// 而畫面上看不出它被回滾了。⇒ 兩個機制搶著決定同一件事，收斂成一個。</para>
    /// </summary>
    protected override void ToolBarButtons(SCP_Ui g)
    {
        // ⚠ ToggleValue 只讀驅動端的字典、不建節點 ⇒ 這裡讀得到那個開關的值，
        //   即使它的節點要等 DrawContent 才被建出來（工具列先畫）。
        bool aFetch = g.ToggleValue("submodule/fetch", false);
        if (g.Button(aFetch ? "重新掃描（含 fetch）" : "重新掃描", "submodule/rescan"))
        {
            // ⚠ 顯式傳生效值，不靠 `m_Scan?.Root` 兜 —— 工具列**先於** DrawContent 畫，
            //   而新 process 的第一輪 `m_Scan` 還是 null ⇒ 那條路會掃到「Senate 自己」，
            //   而畫面下一段馬上又用生效值掃一次。同一顆鈕掃兩個不同的 repo，
            //   使用者只會看到「按一下要等兩倍久」。
            Rescan(aFetch, g.FieldValue(RootAppliedId, m_Model.RepoRoot),
                g.FieldValue(BranchAppliedId, ""), null);
        }
    }

    protected override void DrawContent(SCP_Ui g)
    {
        // ── ① 收集意圖（全部唯讀 —— 這一段不跑任何 git）───────────────
        // ⚠ 兩個**打字**欄位（repo 路徑、全域預設 branch）是**草稿**，要按「套用」才生效。
        //   🩸 第一版是「值一變就重掃」，而在視窗裡打字是**逐字元**的 ——
        //     打 `D:/Unity/LY` 會觸發 11 次重掃，每次跑一整輪 git（LY 有 24 顆 submodule）。
        //     那不是慢，是整個視窗在打字期間卡死，而症狀看起來像「這個欄位壞了」。
        //   ⇒ 打字類走草稿＋套用；**點選類（勾選／下拉）維持立即生效** ——
        //     它們是單次離散事件，不會連續觸發，而且立即看到結果才是那些元件的價值。
        // 生效值 —— 住 session，跨 process 活得下來（見 RootAppliedId 的血證）。
        // 草稿欄位的**預設**就是它：一進頁面欄位顯示「現在掃的是誰」，而不是一格空白。
        string aAppliedRoot = g.FieldValue(RootAppliedId, m_Model.RepoRoot);
        string aAppliedBranch = g.FieldValue(BranchAppliedId, "");

        string aRootDraft = DrawTargetPicker(g, aAppliedRoot, out string? aApplyRoot);
        string aBranchDraft = g.TextField("全域預設 branch", aAppliedBranch, BranchFieldId);

        g.Note("目標 branch 解析順序：**逐項指定 ＞ .gitmodules 的 branch 欄 ＞ 上面這格 ＞ 啟發式**"
               + "（只有一條分支就用它／否則 master，沒 master 才 main）。四層都空 ⇒ 那一列跳過，"
               + "**不會拿「目前所在」頂替**。");

        var aOptions = DrawOptionToggles(g);
        Dictionary<string, string> aOverrides = DrawPerItemSettings(g, out List<string> aExcluded);

        // ── ② 套用（打字欄位）／立即生效（點選欄位）────────────────────
        // 明確的點選動作（改回自己／從清單挑專案）優先 —— 它不必再按一次「套用」。
        if (aApplyRoot != null)
        {
            g.SetField(RootAppliedId, aApplyRoot);
            aAppliedRoot = aApplyRoot;
        }

        bool aPending = !SameRepo(aRootDraft, aAppliedRoot) || aBranchDraft != aAppliedBranch;
        if (aPending)
        {
            using (g.Row())
            {
                if (g.Button("✓ 套用並重新掃描", "submodule/apply"))
                {
                    g.SetField(RootAppliedId, aRootDraft);
                    g.SetField(BranchAppliedId, aBranchDraft);
                    aAppliedRoot = aRootDraft;
                    aAppliedBranch = aBranchDraft;
                    aPending = false;
                }
                if (g.Button("✗ 放棄改動", "submodule/discard"))
                {
                    // 把生效值寫回草稿欄位 —— 「放棄」要真的看得到欄位變回去，
                    // 不然使用者不知道自己現在看的是草稿還是生效值。
                    g.SetField(RootFieldId, aAppliedRoot);
                    g.SetField(BranchFieldId, aAppliedBranch);
                    aPending = false;
                }
            }
            // 這一行是**必須**的：草稿與生效值不同時，下面整張表講的是**生效值**那個 repo ——
            // 而畫面上最顯眼的欄位卻顯示草稿。不講的話「我已經改成 LY 了」與
            // 「表格還是 Senate 的」會被讀成「工具壞了」。
            // ⚠ 這一輪剛按了套用／放棄就不印（那兩顆已經把狀態收斂了，再印一句「還沒生效」是假訊號）。
            if (aPending)
            {
                g.Note("　⚠ **上面的改動還沒生效** —— 下面的表格與指令仍然是"
                       + $"「{aAppliedRoot}」"
                       + (aAppliedBranch.Length > 0 ? $"／預設 branch「{aAppliedBranch}」" : "")
                       + "。按「套用」才會重新掃描。");
            }
        }

        // ── ③ 掃描（唯一落點）────────────────────────────────────────
        // ⚠ 指紋追蹤的是**生效值**（不是草稿）＋ 點選類設定：
        //   · 打字不會改生效值 ⇒ 不會逐字元重掃（那是這一輪要修掉的血證）
        //   · fetch 與逐項覆寫是點選 ⇒ 進指紋，立即生效
        //     （不進的話「我開了先 fetch」就是一顆開了不會有事的開關）
        // ⚠ `m_Scan == null` 也算不符 —— 新 process 的第一輪就是靠這個條件掃第一次。
        string aFingerprint = Fingerprint(aAppliedRoot, aAppliedBranch, aOverrides, aOptions.Fetch);
        if (m_Scan == null || aFingerprint != m_ScannedFingerprint)
        {
            Rescan(aOptions.Fetch, aAppliedRoot, aAppliedBranch, aOverrides);
        }

        g.Space();

        // ── ③ 攤讀數 ──────────────────────────────────────────────
        if (m_Scan == null) { g.Note("還沒掃描。按上面的「重新掃描」。"); return; }

        if (!m_Scan.Ok)
        {
            // 「問不到」跟「沒有 submodule」不得同形 —— 這一格就是那個分界。
            g.Note($"✗ 掃描失敗：{m_Scan.Error}");
            return;
        }

        using (g.Box($"{m_Scan.Root}"))
        {
            g.Note(m_Scan.Fetched
                ? "已 fetch ⇒ 下面的 ahead/behind 是即時值"
                : "未 fetch ⇒ ahead/behind 以各列自己「上次 fetch」為準（逐列標在最後一欄）");
        }

        if (m_Scan.Items.Count == 0)
        {
            g.Note("這個 repo 沒有 submodule（掃描成功，真的是零）。");
            return;
        }

        DrawTable(g, aExcluded);
        DrawNotes(g, aExcluded);
        // ⚠ 傳**生效值**不是草稿：指令必須跟上面那張表講同一個 repo 與同一個預設 branch。
        //   傳草稿的話，「還沒按套用」的狀態下印出來的指令會是一條**沒有人驗證過的範圍**，
        //   而它看起來跟已生效的一模一樣。
        DrawNextStep(g, aOptions, aAppliedBranch, aExcluded);
    }

    // ===========================================================
    // 意圖收集
    // ===========================================================

    /// <summary>本頁的三個全域開關。</summary>
    readonly record struct SyncOptions(bool Fetch, bool IncludeRoot, bool PushAllRemotes);

    /// <summary>
    /// 三個開關。
    /// <para>⚠ 它們的狀態住在驅動端（session／renderer 的 Toggles），本頁只給預設值 ——
    /// 所以 `senate ui --list` 看得到、`--set` 改得動，而且跨 process 記得住。</para>
    /// </summary>
    SyncOptions DrawOptionToggles(SCP_Ui g)
    {
        // 預設全關：三個開關各自都會**擴大**影響範圍（多跑網路／多動 root／多推一個遠端），
        // 而擴大範圍要人顯式點頭，不能是預設值。
        // 這個開關是 fetch 的**唯一**入口（工具列那顆鈕吃它的值，見 ToolBarButtons 的血證）。
        // 一開就會立刻重掃並走網路 —— 那是使用者按下去要的東西，不是副作用。
        bool aFetch = g.Toggle("先 fetch 再讀（走網路；不開的話 ahead/behind 是上次 fetch 的舊值）",
            false, "submodule/fetch");
        bool aIncludeRoot = g.Toggle("root repo 本身也一起 pull / push", false, "submodule/include-root");
        bool aPushAll = g.Toggle("push 推到該 repo 的**所有** remote（關 ＝ 只推 origin）", false, "submodule/push-all-remotes");

        if (aIncludeRoot)
            g.Note("　⚠ root **永遠不切 branch** —— 專案根換分支影響整個工程，那個動作該是人自己下的，不進批次。");
        if (aPushAll)
            g.Note("　⚠ 推去哪由各 repo 的 remote 設定決定（那是每台機器各自的 local config）。"
                   + "一個 remote 失敗不影響其他 remote，但整列會記成失敗並逐個列出。"
                   + "**pull 不跟進** —— 從哪合併是 merge 決策，不是同步動作。");

        return new SyncOptions(aFetch, aIncludeRoot, aPushAll);
    }

    /// <summary>
    /// 逐項設定：納入／排除 ＋ 目標 branch 覆寫。
    /// <para>⚠ 摺疊起來時**子節點根本不建**（`Fold` 的契約）—— 所以收合的那一輪讀不到任何
    /// Toggle／Dropdown 的回傳值。那不是 bug，但它會讓「收合 ⇒ 排除清單突然變空」，
    /// 而空的排除清單看起來完全像「使用者就是要全選」。
    /// ⇒ 收合時**直接從驅動端把值讀回來**（<see cref="SCP_Ui.FieldValue"/> 不畫任何節點），
    ///   讓意圖不因為「我把區塊收起來」而消失。</para>
    /// </summary>
    Dictionary<string, string> DrawPerItemSettings(SCP_Ui g, out List<string> oExcluded)
    {
        var aOverrides = new Dictionary<string, string>();
        oExcluded = new List<string>();

        var aItems = m_Scan is { Ok: true } aScan ? aScan.Items : new List<SubmoduleReading>();
        if (aItems.Count == 0) return aOverrides;

        using var aFold = g.Fold("逐項設定（納入哪幾顆 / 各自切哪條 branch）", "submodule/per-item", iDefaultOpen: false);

        foreach (var aItem in aItems)
        {
            string aPath = aItem.Entry.Path;
            string aOnlyId = OnlyId(aPath);
            string aBranchId = BranchId(aPath);

            bool aInclude;
            string aPick;

            if (aFold.Open)
            {
                using (g.Row())
                {
                    aInclude = g.Toggle(aPath, true, aOnlyId);
                    // 未 init 的沒有工作目錄，也沒有 branch 清單可選 —— 不畫下拉（畫了是一顆
                    // 選了不會有事的元件），改說一句它為什麼不能選。
                    if (aItem.Entry.Uninitialized)
                    {
                        g.Label("⛔ 未 init ⇒ 任何操作都會跳過它");
                        aPick = AutoValue;
                    }
                    else
                    {
                        aPick = g.Dropdown("目標", BranchOptions(aItem), AutoValue, aBranchId);
                    }
                }
            }
            else
            {
                // 收合中 —— 不畫節點，只把驅動端記住的值讀回來（見本方法的 doc comment）。
                aInclude = g.ToggleValue(aOnlyId, iFallback: true);
                aPick = g.FieldValue(aBranchId + "/value", AutoValue);
            }

            if (!aInclude) oExcluded.Add(aPath);
            if (aPick != AutoValue && aPick.Length > 0) aOverrides[aPath] = aPick;
        }

        if (!aFold.Open && (oExcluded.Count > 0 || aOverrides.Count > 0))
        {
            // 收起來的設定仍然生效 ⇒ 一定要在收合狀態下也看得見它不是預設值。
            // 🩸 這正是「跟背景一樣的東西在任何一把尺底下都叫做沒有」的形狀：
            //    一個藏在收合區塊裡的排除清單，看起來跟「沒有排除」一模一樣。
            g.Note($"　⚠ 收合中的逐項設定**仍然生效**：排除 {oExcluded.Count} 顆、覆寫 {aOverrides.Count} 顆。");
        }

        return aOverrides;
    }

    /// <summary>
    /// 某顆 submodule 的目標 branch 下拉選項：「回到自動」＋ 該 repo 的本地／origin 分支。
    /// <para>「自動」那一格**把解析結果印出來** —— 選之前就看得到選下去會變成什麼，
    /// 不然使用者要先選一次才知道自動是什麼（而那時他已經改掉設定了）。</para>
    /// </summary>
    static List<SCP_GuiOption> BranchOptions(SubmoduleReading iItem)
    {
        // 「自動」＝ 不含逐項覆寫的那三層（.gitmodules ＞ 全域預設 ＞ 啟發式）。
        // ⚠ 這裡顯示的是**掃描當下**算出來的結果（照片），因為 branch 清單本身也是照片 ——
        //   兩個值來自同一次掃描才對得起來。
        string aAuto = iItem.TargetSource == TargetBranchSource.Override
            ? "" // 照片是帶著覆寫拍的 ⇒ 那個值不能拿來當「自動會是什麼」
            : iItem.TargetBranch;

        var aList = new List<SCP_GuiOption>
        {
            new SCP_GuiOption(AutoValue, aAuto.Length > 0 ? $"(自動 → {aAuto})" : "(自動 → 無目標)"),
        };

        // ⚠ `All` 可能是 null：`SubmoduleReading.Branches` 是 struct，而未 init 的那條路徑
        //   （SubmoduleScan 只填 Entry / AbsPath）拿到的是 default ⇒ 裡面的 List 沒被建。
        //   目前呼叫端已經先擋掉未 init，但這裡不靠上下文 —— 少一道判斷的症狀是 NRE，
        //   而 NRE 在 immediate-mode 每幀重繪的宿主裡是「整個視窗掛掉」，不是一行紅字。
        if (iItem.Branches.All == null || !iItem.Branches.Known)
        {
            // 「問不到」與「一條分支都沒有」不得同形 —— 前者要人去查 git，後者是有效答案。
            aList.Add(new SCP_GuiOption("", iItem.Branches.Known
                ? "(這個 repo 沒有任何分支)"
                : "(⚠ 問不到分支清單 —— for-each-ref 失敗)"));
            return aList;
        }

        foreach (string aBranch in iItem.Branches.All) aList.Add(new SCP_GuiOption(aBranch));
        return aList;
    }

    static string OnlyId(string iPath) => "submodule/only/" + iPath;
    static string BranchId(string iPath) => "submodule/branch/" + iPath;

    /// <summary>可直接打字的 repo 路徑欄位 id —— **這一頁「對誰動手」的唯一真相源**。</summary>
    const string RootFieldId = "submodule/root";

    /// <summary>上一次「貼上」的結果（null ＝ 這一輪還沒人按過）。純顯示狀態。</summary>
    string? m_PasteMessage;

    /// <summary>
    /// 把貼進來的路徑洗乾淨。
    /// <para>Windows 檔案總管的「複製路徑」給的是**帶雙引號**的字串
    /// （<c>"D:/Unity/LY"</c>）—— 直接送進 <c>Directory.Exists</c> 一定是 false，
    /// 而畫面上會顯示「路徑不存在」，於是使用者會以為自己複製錯了。</para>
    /// <para>⚠ 只去掉**包住整串**的引號與前後空白，不動路徑中間的任何字元
    /// （資料夾名可以含引號與空白，順手「清理」會把合法路徑改壞）。</para>
    /// </summary>
    static string CleanPath(string iText)
    {
        string aText = iText.Trim();
        if (aText.Length >= 2 && aText[0] == '"' && aText[aText.Length - 1] == '"')
            aText = aText.Substring(1, aText.Length - 2).Trim();
        return aText;
    }

    /// <summary>
    /// 掃哪個 repo —— **一個可以直接打路徑的欄位**（預設 Senate 自己）。
    /// <para>形狀取自 UCL 端那頁（路徑欄 ＋「本專案」鈕）。這頁最常見的用途本來就是
    /// 操作**別的** repo（Senate 是後台，Unity 專案才是要整理的那個），
    /// 所以「只能從設定檔的清單裡挑」等於把主要用途擋在設定之後。</para>
    /// <para>⚠ 沒有「…」瀏覽鈕：開資料夾對話框要碰 OS，而共用層零依賴、
    /// <see cref="SCP_GuiHost"/> 目前也沒有那格能力 ⇒ 畫一顆按了沒事的鈕比沒有那顆糟。</para>
    /// <para>⭐ **單一真相源**：路徑只住在這個欄位裡。下面那個下拉（有設定檔專案時才畫）
    /// 只是把值**填進來**的捷徑，它自己不持有狀態 —— 兩個元件各記一份的話，
    /// 「我明明選了 A，它卻掃了 B」就會發生，而那不會報錯。</para>
    /// </summary>
    /// <param name="oApplyRoot">
    /// 非 null ＝ 使用者做了一個**明確的點選動作**（改回自己／從清單選一個專案），
    /// 呼叫端應該直接套用它。⚠ 為什麼不在這裡就 <c>Rescan</c>：那會在逐項設定收集**之前**掃描，
    /// 於是這一輪的覆寫全部漏掉，然後下一輪指紋不符再補掃一次 —— 白掃一輪 git。
    /// ⇒ 讓 Rescan 只有一個落點。
    /// </param>
    string DrawTargetPicker(SCP_Ui g, string iAppliedRoot, out string? oApplyRoot)
    {
        oApplyRoot = null;
        // 預設值是**生效值**（不是 Senate 自己）—— 一進頁面欄位就該顯示「現在掃的是誰」。
        string aRoot = g.TextField("repo 路徑", iAppliedRoot, RootFieldId);

        using (g.Row())
        {
            // 回到預設（Senate 自己）。⚠ 條件看**生效值**不是草稿：這顆鈕要回答的是
            // 「現在掃的不是 Senate，我要換回去」，而草稿只是還沒生效的字。
            // 只在真的不是自己時才畫 —— 一顆按下去等於沒事的鈕看起來像壞的。
            if (!SameRepo(iAppliedRoot, m_Model.RepoRoot)
                && g.Button("↩ 改回 Senate 自己", "submodule/root/self"))
            {
                g.SetField(RootFieldId, m_Model.RepoRoot);
                oApplyRoot = m_Model.RepoRoot;   // 點選 ⇒ 立即生效，不必再按套用
            }

            // ⇣ 貼上 —— 🩸 ImGui 的 InputText 在這個宿主上**吃不到 Ctrl+V**
            //（ImGui 的剪貼簿 callback 沒被接上，見 SCP_GuiHost.ReadClipboard），
            // 所以一個只能手打絕對路徑的欄位，實際上就是一個不會被用的欄位。
            // ⚠ 貼上只**填草稿**、不套用：貼進來的路徑常常還要修一下（多一層、少一層），
            //   而「貼上就立刻跑一輪 git」會讓修錯的那次白掃一輪。
            // ⚠ 沒掛 ReadClipboard 的宿主**不畫這顆鈕**（不是畫一顆按了沒事的）。
            if (SCP_GuiHost.ReadClipboard != null && g.Button("⇣ 貼上", "submodule/root/paste"))
            {
                SCP_ClipboardRead aRead = SCP_GuiHost.ReadClipboard();
                if (aRead.Ok && aRead.Text.Length > 0) g.SetField(RootFieldId, CleanPath(aRead.Text));
                // 成功也要有話說：效果發生在**上面那個欄位**，而它下一輪才會更新
                //（跟按鈕事件同一個「慢一幀」節奏）⇒ 沒有這行字的話「貼上了」與「沒反應」同形。
                m_PasteMessage = aRead.Message;
            }

            // 設定檔裡的專案 —— 有才畫。沒有 senate.local.json 時畫一個只有一個選項的下拉
            // 是純雜訊（那個選項就是上面欄位的現值）。
            var aOptions = new List<SCP_GuiOption>();
            foreach (var aProject in m_Model.Projects)
            {
                if (aProject.Root.Length == 0) continue;
                // ⚠ 停用／路徑壞掉的**照樣列出來並標原因** ——
                //   「我關掉它」「設定壞了」「沒設定過」是三件事，消失掉會讓人以為是第三種。
                string aLabel = aProject.State switch
                {
                    ProbeState.Ok => aProject.Enabled ? aProject.Name : $"{aProject.Name}（停用）",
                    ProbeState.Missing => $"{aProject.Name}（路徑不存在）",
                    ProbeState.NotGitRepo => $"{aProject.Name}（非 git repo）",
                    _ => $"{aProject.Name}（未設定）",
                };
                aOptions.Add(new SCP_GuiOption(aProject.Root, aLabel));
            }
            if (aOptions.Count > 0)
            {
                // current 一律傳空字串：這個下拉是**動作**不是狀態（選了就填進欄位），
                // 傳現值會讓它看起來像第二個真相源。
                string aPick = g.Dropdown("設定檔的專案", aOptions, "", "submodule/project");
                if (aPick.Length > 0 && !SameRepo(aPick, iAppliedRoot))
                {
                    g.SetField(RootFieldId, aPick);
                    oApplyRoot = aPick;   // 點選 ⇒ 立即生效
                }
            }
        }

        if (m_PasteMessage != null) g.Note($"　{m_PasteMessage}");

        if (!SameRepo(iAppliedRoot, m_Model.RepoRoot))
        {
            // 這**不是**警告 —— 操作別的 repo 是本頁的正常用法。它是一句事實陳述，
            // 存在的理由是：這個欄位的值會被 session 記住（跨 process），
            // 所以「我上次改成 LY」會在下次開頁時仍然生效，而畫面必須說得出它現在對著誰。
            g.Note($"　▶ 現在掃的是 **{iAppliedRoot}**（不是 Senate 自己）。這個值會被記住 —— 下次開頁還是它。");
        }

        return aRoot;
    }

    /// <summary>
    /// 兩個路徑指不指同一個 repo。
    /// <para>🩸 取自 UCL 端那頁：純字串比對會把 <c>D:/Unity/LY</c>、<c>D:\Unity\LY</c>、
    /// <c>D:/Unity/LY/</c> 判成三個不同的 repo，於是「改回自己」那顆鈕會對著同一個 repo
    /// 一直出現 —— 而假訊號會訓練人忽略訊號。</para>
    /// <para>⚠ 不用 <c>Path.GetFullPath</c>：它對不存在的路徑會丟例外，而這一欄是使用者隨手打的。</para>
    /// </summary>
    static bool SameRepo(string iA, string iB)
        => string.Equals(NormRepo(iA), NormRepo(iB), StringComparison.OrdinalIgnoreCase);

    static string NormRepo(string iPath)
        => string.IsNullOrEmpty(iPath) ? "" : iPath.Replace('\\', '/').TrimEnd('/');

    // ===========================================================
    // 讀數呈現
    // ===========================================================

    void DrawTable(SCP_Ui g, List<string> iExcluded)
    {
        if (m_Scan == null) return;

        int aIncluded = m_Scan.Items.Count - iExcluded.Count;
        g.Label($"共 {m_Scan.Items.Count} 顆；**納入 {aIncluded} 顆**"
                + (iExcluded.Count > 0 ? $"，排除 {iExcluded.Count} 顆（下面第一欄標 ⏸）" : ""));

        using (g.Table("submodule", "目前", "目標", "來源", "工作區", "↑ahead ↓behind", "remote", "上次 fetch"))
        {
            foreach (var aItem in m_Scan.Items)
            {
                bool aOut = iExcluded.Contains(aItem.Entry.Path);
                g.TableRow(
                    (aOut ? "⏸ " : "") + aItem.Entry.Path,
                    CurrentText(aItem),
                    aItem.TargetBranch.Length > 0 ? aItem.TargetBranch : "—",
                    SubmoduleScan.SourceText(aItem.TargetSource),
                    DirtyText(aItem),
                    AheadBehindText(aItem),
                    aItem.Remotes.Count == 0 ? (aItem.Entry.Uninitialized ? "-" : "⚠ 無")
                        : (aItem.Remotes.Count > 1 ? "⇈ " + string.Join(" / ", aItem.Remotes) : aItem.Remotes[0]),
                    aItem.FetchAgeText);
            }
        }
    }

    /// <summary>逐列的說明 —— 表格說「是什麼」，這裡說「所以會怎樣」。</summary>
    void DrawNotes(SCP_Ui g, List<string> iExcluded)
    {
        if (m_Scan == null) return;
        foreach (string aWarning in m_Scan.Warnings) g.Note(aWarning);

        foreach (var aItem in m_Scan.Items)
        {
            string aPath = aItem.Entry.Path;
            // 被排除的列不再逐條解釋它的狀態 —— 那些話講的是「動手時會怎樣」，
            // 而這一顆這一輪不會被動到。留著只會讓真正要看的那幾行被推出畫面。
            if (iExcluded.Contains(aPath))
            {
                g.Note($"⏸ {aPath}：這一輪**排除**（不會出現在指令的 --only 裡）。");
                continue;
            }
            if (aItem.Entry.Uninitialized)
            {
                g.Note($"⛔ {aPath}：未 init（內容不在本機）—— 任何操作都會跳過它。"
                       + "要它進來：git submodule update --init");
                continue;
            }
            if (aItem.Entry.Flag == SCP_GitSubmoduleFlag.Conflict)
                g.Note($"⛔ {aPath}：有合併衝突 —— 人要先處理，工具一律不碰。");
            if (aItem.Entry.Flag == SCP_GitSubmoduleFlag.Unknown)
                g.Note($"⚠ {aPath}：認不得的 submodule status 旗標 —— 保守當成不要動它。");
            if (aItem.Entry.Flag == SCP_GitSubmoduleFlag.ShaMismatch)
                g.Note($"⚠ {aPath}：目前 SHA 與父層記的 gitlink 不同 ⇒ **父層還沒 bump**，"
                       + "同事 pull 父層拿到的還是舊版。");
            if (aItem.TargetSource == TargetBranchSource.None)
                g.Note($"⏭ {aPath}：解析不到目標 branch（四層全空）—— 會被跳過。"
                       + "在上面的逐項設定挑一條，或填全域預設，或在 .gitmodules 寫 branch =。");
            if (aItem.Dirty == SCP_GitDirtyState.Dirty)
                g.Note($"⚠ {aPath}：dirty（有未 commit 的追蹤檔修改）⇒ 切 branch 與 pull 都會**跳過它**。"
                       + "push 不受影響（推的是已 commit 的東西）。");
            if (aItem.Dirty == SCP_GitDirtyState.Unknown)
                g.Note($"⚠ {aPath}：git status 問不到 —— 狀態不明一律當成擋下處理。");
            if (aItem.IsDetached && aItem.TargetBranch.Length > 0)
                g.Note($"⛔ {aPath}：detached HEAD ⇒ pull 不會動它（pull 不負責移動 branch）。"
                       + $"要一次到位：連 --checkout 一起跑（會先確認 HEAD 已在 {aItem.TargetBranch} 歷史上才切）。");
            if (!aItem.AheadBehind.Known && !aItem.IsDetached)
                g.Note($"⚠ {aPath}：沒有 upstream ⇒ ahead/behind **未知**（不是 0 —— 0 的意思是對齊）。");
            if (aItem.Remotes.Count == 0)
                g.Note($"⚠ {aPath}：沒有設定任何 remote ⇒ push 沒地方推，會跳過並列出。");
        }
    }

    /// <summary>
    /// 下一步該打什麼 —— **把畫面上的意圖編譯成指令**。
    /// <para>本頁不放寫入鈕（見檔頭），所以這一格不是附錄，是這一頁的出口：
    /// 一個「只能看不能動」的畫面如果不指路，看得到問題的人就卡在這裡了。</para>
    /// <para>⚠ 指令帶的是**畫面上現在的設定**（排除清單、逐項覆寫、三個開關），
    /// 不是一份寫死的範本。範本會在使用者調整設定之後靜默過期，
    /// 而過期的提示比沒有提示糟 —— 它讓人照著做，然後得到一個他沒有要的範圍。</para>
    /// </summary>
    void DrawNextStep(SCP_Ui g, SyncOptions iOptions, string iDefaultBranch, List<string> iExcluded)
    {
        if (m_Scan == null) return;
        g.Space();

        int aIncluded = m_Scan.Items.Count - iExcluded.Count;

        using (g.Box("要動手的話（寫入端在 CLI，不在這一頁）"))
        {
            if (aIncluded == 0)
            {
                // 「一顆都沒納入」不是一條可以跑的指令 —— 印出來只會讓人跑一個空批次然後看到 ✓0，
                // 而 ✓0 看起來像「都做完了」。
                g.Note("⚠ 逐項設定把**每一顆都排除了** ⇒ 沒有可以跑的指令。先在上面納入至少一顆。");
                return;
            }

            string aArgs = CommonArgs(iOptions, iDefaultBranch, iExcluded);

            g.Note($"先看它打算做什麼（唯讀）：senate submodule status{aArgs}");
            g.Note($"只把本地弄到最新（不碰遠端）：senate submodule sync{aArgs} --checkout --pull");
            g.Note($"推出去（會寫遠端 ⇒ 必須加 --yes 明示）：senate submodule sync{aArgs} --push --yes");
            g.Note("　（想先確認範圍就把 --yes 換成 --dry-run —— 它印出打算做的事，不動任何東西。）");

            if (iExcluded.Count > 0)
                g.Note($"　⚠ 上面每一條都帶了 {iExcluded.Count} 個 --only 之外的排除"
                       + "（--only 是白名單：沒列到的**不會**被動到）。");
        }
    }

    /// <summary>兩條指令共用的引數 —— 只寫一份，避免其中一條哪天忘了跟上新開關。</summary>
    string CommonArgs(SyncOptions iOptions, string iDefaultBranch, List<string> iExcluded)
    {
        if (m_Scan == null) return "";

        // 路徑可能含空白 ⇒ 一律加引號。不加的話 git 會拿到一個「合法但不是你要的」引數，
        // 而那不會報錯（同 SCP_Git 為什麼用 ArgumentList 而不自己拼字串）。
        var aArgs = new System.Text.StringBuilder();
        aArgs.Append($" --root \"{m_Scan.Root}\"");

        if (iDefaultBranch.Length > 0) aArgs.Append($" --branch {iDefaultBranch}");
        if (iOptions.Fetch) aArgs.Append(" --fetch");
        if (iOptions.IncludeRoot) aArgs.Append(" --include-root");
        if (iOptions.PushAllRemotes) aArgs.Append(" --push-all-remotes");

        // --only 只在真的有排除時才帶：全選時帶一份完整清單只會讓指令長到沒人想讀，
        // 而且那份清單會在 submodule 增加時**安靜地過期**（新的那顆不在白名單裡 ⇒ 不會被動到）。
        // ⚠ 語法是**重複旗標**（`--only a --only b`），不是逗號分隔 —— 見 Program.ArgValues。
        if (iExcluded.Count > 0)
        {
            foreach (var aItem in m_Scan.Items)
            {
                if (iExcluded.Contains(aItem.Entry.Path)) continue;
                aArgs.Append($" --only \"{aItem.Entry.Path}\"");
            }
        }

        // 逐項覆寫 → `--set-branch <path>=<branch>`（可重複）。
        // ⚠ 判斷用照片的 TargetSource 而不是另外傳一份 overrides 進來：照片是**這一輪的設定**拍的，
        //   所以「表格那一列顯示的目標」與「指令帶出去的目標」保證是同一個值。
        //   另外傳一份的話就有兩個真相源，而它們分岔的症狀是「畫面說 Dev、指令帶 master」。
        foreach (var aItem in m_Scan.Items)
        {
            if (iExcluded.Contains(aItem.Entry.Path)) continue;
            if (aItem.TargetSource != TargetBranchSource.Override) continue;
            aArgs.Append($" --set-branch \"{aItem.Entry.Path}={aItem.TargetBranch}\"");
        }

        return aArgs.ToString();
    }

    void Rescan(bool iFetch, string? iRoot = null, string? iDefaultBranch = null,
        IReadOnlyDictionary<string, string>? iOverrides = null)
    {
        string aRoot = iRoot ?? m_Scan?.Root ?? m_Model.RepoRoot;
        // 生效 branch 由呼叫端顯式給（頁面不再自己存一份 —— 那份會跟 session 分岔）
        string aBranch = iDefaultBranch ?? "";
        m_Scan = SubmoduleScan.Scan(aRoot, iFetch,
            string.IsNullOrWhiteSpace(aBranch) ? null : aBranch,
            iOverrides);
        m_ScannedFingerprint = Fingerprint(aRoot, aBranch, iOverrides, iFetch);
    }

    /// <summary>
    /// 「這份照片是用什麼設定拍的」的指紋。
    /// <para>⚠ 覆寫要**排序後**才串起來：字典的列舉順序不保證穩定，而一個會隨列舉順序變的指紋
    /// 會讓「設定沒變」被判成「變了」⇒ 每幀重掃（見 <see cref="m_ScannedFingerprint"/> 的血證）。</para>
    /// </summary>
    static string Fingerprint(string iRoot, string iDefaultBranch,
        IReadOnlyDictionary<string, string>? iOverrides, bool iFetch)
    {
        // ⚠ 這裡的 iRoot / iDefaultBranch 是**生效值**不是草稿 —— 傳草稿進來就等於「打字即重掃」。
        var aParts = new List<string> { NormRepo(iRoot), iDefaultBranch, iFetch ? "fetch" : "" };
        if (iOverrides != null)
        {
            var aKeys = new List<string>(iOverrides.Keys);
            aKeys.Sort(StringComparer.Ordinal);
            foreach (string aKey in aKeys) aParts.Add($"{aKey}={iOverrides[aKey]}");
        }
        // ⚠ 一定要有分隔符：直接串接會讓 (root="D:/A", branch="B") 與 (root="D:/AB", branch="")
        //   得到**同一個指紋** ⇒ 設定其實變了卻被判成沒變 ⇒ 拿上一個 repo 的照片配新設定，
        //   而那正是「綠燈全亮、量到的是別的 repo」的形狀。
        //   用 "|" 當分隔符：Windows 路徑禁止這個字元，git 的 ref 名也不收它 ⇒ 不會有歧義。
        return string.Join("|", aParts);
    }

    static string CurrentText(SubmoduleReading iItem)
    {
        if (iItem.Entry.Uninitialized) return "⛔ 未 init";
        if (iItem.CurrentBranch == null) return "⚠ 問不到";
        if (iItem.CurrentBranch == SCP_Git.DetachedHead) return "⛔ detached";
        return iItem.OnTarget ? "✓ " + iItem.CurrentBranch : "⚠ " + iItem.CurrentBranch;
    }

    static string DirtyText(SubmoduleReading iItem)
    {
        if (iItem.Entry.Uninitialized) return "-";
        return iItem.Dirty switch
        {
            SCP_GitDirtyState.Clean => "✓ 乾淨",
            SCP_GitDirtyState.Dirty => "⚠ dirty",
            _ => "⚠ 問不到",
        };
    }

    static string AheadBehindText(SubmoduleReading iItem)
    {
        if (iItem.Entry.Uninitialized) return "-";
        // ⚠ 未知不顯示 0 —— 0 是「對齊」，那是一個答案。
        if (!iItem.AheadBehind.Known) return "未知";
        return $"↑{iItem.AheadBehind.Ahead} ↓{iItem.AheadBehind.Behind}";
    }
}
