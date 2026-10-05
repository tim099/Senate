// 區塊職責：**Markdown 檢視／編輯頁**（Senate 版）—— 檢視模式把 .md 渲染成標題／段落／表格／程式碼框／mermaid 樹；
//          編輯模式是一整塊純文字編輯區（SCP_Ui.TextArea），存檔寫回原檔。參考 Unity `UCL_MarkdownViewerPage`。
// 物理意義：解析交給 MarkdownDoc（純函式）；本頁只做 IO、渲染、與編輯狀態。
//          ⭐ 所有「跨輪要記得的狀態」都住 session 欄位（路徑、模式、草稿、草稿的基準雜湊），⛔ 不住實例欄位 ——
//            CLI 每次呼叫都是新 process，住實例欄位的話「按了編輯 → 下一步存檔」在 CLI 就接不起來。
// 數值影響：讀 ＝ 路徑變了或按「重新讀取」才讀一次檔；寫 ＝ 只有「存檔」那顆鈕，temp → 取代（SCP_TextFile.ReplaceOrMove）。
//
// ⚠ 與 Unity 版的差異（刻意的）：
//   · Senate 的兩個 renderer 都不吃 rich-text ⇒ inline 標記是**拿掉**，不是換成 <b> tag（見 MarkdownDoc.Inline）。
//   · related 連結**原地換頁**（附「上一份」），不 push 新頁：路徑住全域欄位，疊兩頁 viewer 會共用同一格而互相蓋掉。
//   · related 只解 相對路徑／絕對路徑／`repo:`（＝ 專案根）。`ucl_core:` 這類前綴 Senate 沒有解析器 ——
//     ⛔ 不在這裡現造第四套（skill ucl-core-paths），解不了就照實說。
//   · 存檔有**衝突閘**：開始編輯那一刻記下磁碟內容的雜湊，存檔前重讀比對；
//     不同 ＝ 你編輯期間有人（agent／Server）寫過這份檔 ⇒ 不存，要你按第二次才覆寫。
//     任務單檔的寫入端是 Server（`cmd task`），在這裡直接改單檔就是**多一個寫入端** —— 這道閘至少讓兩邊撞到時看得見。
// ⚠ 視窗文字不放 emoji（ImWchar 16 位元，U+FFFF 以上畫成方框 —— TASK-0356）；文件內文裡的 emoji 是原文，照印。
#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SCP.Core.Gui;
using SCP.Core.Io;

namespace Senate.Cli.Pages;

public sealed class MarkdownViewerPage : SCP_GuiToolPage
{
    public const string PageKey = "md-viewer";

    /// <summary>session 欄位（顯式 key ＝ 全域；同一時間只有一個 viewer 在用它們 —— 見檔頭「原地換頁」）。</summary>
    public const string PathId = "mdview/path";
    public const string ModeId = "mdview/mode";          // "edit" ／ 空 ＝ 檢視
    public const string DraftId = "mdview/draft";        // 編輯區內容
    public const string DraftPathId = "mdview/draft-path";   // 草稿是哪一份檔的
    public const string DraftBaseId = "mdview/draft-base";   // 開始編輯那一刻磁碟內容的 SHA-256 —— 衝突閘的基準
    public const string HistoryId = "mdview/history";    // related 原地換頁的「上一份」堆疊（\n 分隔）
    public const string PendingId = "mdview/pending";    // 二段確認
    public const string FrontId = "mdview/front";        // 顯示 frontmatter

    const string ModeEdit = "edit";

    readonly SenateModel m_Model;

    // 載入快取（實例欄位只放「可以從磁碟重算」的東西）
    bool m_Loaded;
    string m_LoadedPath = "";
    string? m_Error;
    string m_Raw = "";          // 原文（行尾原樣）
    string m_RawLf = "";        // 原文，行尾收成 \n —— 跟編輯區比較用
    bool m_CrLf, m_Bom;
    string m_Hash = "";
    MdDocument m_Doc = new();
    List<MdRelatedDoc> m_Related = new();
    List<string> m_RelatedSkipped = new();
    string? m_Message;

    public MarkdownViewerPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "Markdown 檢視／編輯";
    public override string? MenuGroup => "工具";

    /// <summary>
    /// 從別頁開一份檔（例：任務頁的「開啟單檔」）。路徑寫進 session 欄位再 push ——
    /// 同一份檔有沒存的草稿時**不清**，進編輯就會接回去。
    /// </summary>
    public static void Open(SCP_Ui iUi, SCP_GuiPageController? iController, SenateModel iModel, string iPath)
    {
        if (iController == null) return;
        iUi.SetField(PathId, iPath);
        iUi.SetField(ModeId, "");
        iUi.SetField(HistoryId, "");
        iUi.SetField(PendingId, "");
        iController.Push(new MarkdownViewerPage(iModel));
    }

    public override void OnPush()
    {
        base.OnPush();
        m_Loaded = false;
        m_Message = null;
    }

    // ── 讀檔 ─────────────────────────────────────────────

    void Load(string iPath)
    {
        m_Loaded = true;
        m_LoadedPath = iPath;
        m_Error = null;
        m_Raw = m_RawLf = m_Hash = "";
        m_CrLf = m_Bom = false;
        m_Doc = new MdDocument();
        m_Related = new();
        m_RelatedSkipped = new();
        if (iPath.Length == 0) { m_Error = "還沒有指定檔案 —— 在上面的「檔案」欄填路徑"; return; }
        if (!File.Exists(iPath)) { m_Error = "找不到檔案：" + iPath; return; }
        try
        {
            byte[] aBytes = File.ReadAllBytes(iPath);
            m_Hash = Sha(aBytes);
            m_Bom = aBytes.Length >= 3 && aBytes[0] == 0xEF && aBytes[1] == 0xBB && aBytes[2] == 0xBF;
            m_Raw = new UTF8Encoding(false).GetString(aBytes, m_Bom ? 3 : 0, aBytes.Length - (m_Bom ? 3 : 0));
            m_CrLf = m_Raw.Contains("\r\n", StringComparison.Ordinal);
            m_RawLf = m_Raw.Replace("\r\n", "\n");
            m_Doc = MarkdownDoc.Parse(m_Raw);
            m_Related = MarkdownDoc.ParseRelated(m_Doc.Frontmatter, m_RelatedSkipped);
        }
        catch (Exception e)
        {
            m_Error = $"讀不了：{e.GetType().Name}: {e.Message}";
        }
    }

    static string Sha(byte[] iBytes) => Convert.ToHexString(SHA256.HashData(iBytes));

    // ── 工具列 ───────────────────────────────────────────

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        // ⚠ 工具列先於 DrawContent 畫 ⇒ 這裡就要確保讀過檔，否則剛開頁那一輪「複製全文」「顯示 frontmatter」不會出現
        string aPath = iUi.FieldValue(PathId, "");
        if (!m_Loaded || aPath != m_LoadedPath) Load(aPath);
        if (iUi.Button("重新讀取", "mdview/btn/reload")) { m_Loaded = false; m_Message = null; }
        OpenFolderButton(iUi, aPath.Length > 0 ? Path.GetDirectoryName(aPath) : null, "mdview/btn/open-dir");
        if (SCP_GuiHost.CopyToClipboard != null && m_Raw.Length > 0 && iUi.Button("複製全文", "mdview/btn/copy"))
            m_Message = SCP_GuiHost.CopyToClipboard(m_Raw);
        if (m_Doc.Frontmatter != null) iUi.Toggle("顯示 frontmatter", false, FrontId);
    }

    // ── 內容 ─────────────────────────────────────────────

    protected override void DrawContent(SCP_Ui g)
    {
        string aPath = g.FieldValue(PathId, "");
        if (!m_Loaded || aPath != m_LoadedPath) Load(aPath);
        // ⚠ 編輯模式要「草稿屬於這一份檔」才成立：路徑欄被外部改掉（CLI --set）時，
        //   不這樣擋的話畫面會拿 A 的草稿配 B 的路徑，而按存檔就是把 A 的內容寫進 B。
        bool aEdit = g.FieldValue(ModeId, "") == ModeEdit && m_Error == null && g.FieldValue(DraftPathId, "") == aPath;

        if (aEdit) g.Label("檔案：" + aPath);   // 編輯中不給換路徑：草稿綁著這一份
        else g.TextField("檔案", aPath, PathId);
        if (m_Message != null) g.Note(m_Message);
        if (m_Error != null) { g.Note("[注意] " + m_Error); return; }

        g.Label($"{m_Raw.Length.ToString(CultureInfo.InvariantCulture)} 字｜行尾 {(m_CrLf ? "CRLF" : "LF")}｜{(m_Bom ? "UTF-8 BOM" : "UTF-8")}｜{m_Doc.Blocks.Count} 個區塊");
        DrawHistoryRow(g, aEdit);

        if (aEdit) DrawEditor(g, aPath);
        else
        {
            if (g.Button("編輯", "mdview/btn/edit")) EnterEdit(g, aPath);
            DrawViewer(g);
        }
    }

    // ── 編輯模式 ─────────────────────────────────────────

    void EnterEdit(SCP_Ui g, string iPath)
    {
        string aDraftPath = g.FieldValue(DraftPathId, "");
        string aDraft = g.FieldValue(DraftId, "");
        if (aDraftPath == iPath && aDraft.Length > 0)
        {
            // 同一份檔有留下來的草稿（上次沒存就離開）⇒ 接回去，⛔ 不拿磁碟版蓋掉
            g.SetField(ModeId, ModeEdit);
            m_Message = "接回上次未存的草稿（要丟掉它：按「放棄修改」）";
            return;
        }
        if (aDraftPath.Length > 0 && aDraftPath != iPath && aDraft.Length > 0 && !Confirm(g, "drop-other"))
        {
            m_Message = $"另一份檔（{aDraftPath}）還留著草稿 ⇒ 再按一次「編輯」會丟掉它";
            return;
        }
        g.SetField(DraftId, m_RawLf);
        g.SetField(DraftPathId, iPath);
        g.SetField(DraftBaseId, m_Hash);
        g.SetField(ModeId, ModeEdit);
        m_Message = null;
    }

    void DrawEditor(SCP_Ui g, string iPath)
    {
        string aText = g.FieldValue(DraftId, m_RawLf);
        bool aDirty = aText.Replace("\r\n", "\n") != m_RawLf;
        using (g.Row())
        {
            if (g.Button("存檔", "mdview/btn/save")) Save(g, iPath, aText);
            if (g.Button(aDirty ? "放棄修改" : "回到檢視", "mdview/btn/discard")) Discard(g, aDirty);
            g.Label(aDirty ? "（有未存的修改）" : "（與磁碟相同）");
        }
        if (g.FieldValue(DraftBaseId, "") != m_Hash)
            g.Note("[注意] 磁碟上的檔在你開始編輯之後被改過（可能是 agent／Server 寫的）—— 存檔前先「放棄修改」重開，或按兩次存檔覆寫");
        g.TextArea("", aText, DraftId, 32);
    }

    void Save(SCP_Ui g, string iPath, string iText)
    {
        if (g.FieldValue(DraftPathId, "") != iPath) { m_Message = "草稿不屬於這一份檔 ⇒ 沒有存（先「放棄修改」再重開）"; return; }
        // 衝突閘：比的是**現在**磁碟上的位元組，⛔ 不是本頁載入時那份（CLI 每次都是新 process，載入時那份就是現在）
        string aBase = g.FieldValue(DraftBaseId, "");
        byte[] aNow;
        try { aNow = File.Exists(iPath) ? File.ReadAllBytes(iPath) : Array.Empty<byte>(); }
        catch (Exception e) { m_Message = $"存檔前讀不了原檔 ⇒ 沒有存：{e.GetType().Name}: {e.Message}"; return; }
        if (Sha(aNow) != aBase && !Confirm(g, "overwrite"))
        {
            m_Message = "磁碟上的檔在你開始編輯之後被改過 ⇒ 沒有存。要用你的版本蓋掉它，再按一次「存檔」";
            return;
        }

        // 行尾、BOM 照原檔：只改內容，⛔ 不順手把整份檔的換行或編碼換掉（那會讓 diff 整份翻紅）
        string aOut = iText.Replace("\r\n", "\n");
        if (m_CrLf) aOut = aOut.Replace("\n", "\r\n");
        byte[] aBytes = new UTF8Encoding(m_Bom).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(aOut)).ToArray();
        try
        {
            string aTmp = iPath + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllBytes(aTmp, aBytes);
            SCP_TextFile.ReplaceOrMove(aTmp, iPath);
            byte[] aBack = File.ReadAllBytes(iPath);
            bool aSame = aBack.AsSpan().SequenceEqual(aBytes);
            g.SetField(DraftBaseId, Sha(aBack));
            m_Message = aSame
                ? $"已存（{aBytes.Length.ToString(CultureInfo.InvariantCulture)} 位元組，回讀逐位元組相符）"
                : "[注意] 寫完回讀跟寫進去的不一樣 —— 可能剛好有別的寫入端在動這份檔，先「重新讀取」看現況";
        }
        catch (Exception e)
        {
            m_Message = $"存檔失敗（原檔未動）：{e.GetType().Name}: {e.Message}";
        }
        m_Loaded = false;   // 回讀磁碟再畫
    }

    void Discard(SCP_Ui g, bool iDirty)
    {
        if (iDirty && !Confirm(g, "discard"))
        {
            m_Message = "有未存的修改 ⇒ 再按一次「放棄修改」才會丟掉";
            return;
        }
        g.SetField(ModeId, "");
        g.SetField(DraftId, "");
        g.SetField(DraftPathId, "");
        g.SetField(DraftBaseId, "");
        m_Message = null;
    }

    /// <summary>二段確認：第一次按只記下來（回 false），同一件事第二次按才回 true。</summary>
    static bool Confirm(SCP_Ui g, string iWhat)
    {
        if (g.FieldValue(PendingId, "") == iWhat) { g.SetField(PendingId, ""); return true; }
        g.SetField(PendingId, iWhat);
        return false;
    }

    // ── related／上一份 ──────────────────────────────────

    void DrawHistoryRow(SCP_Ui g, bool iEdit)
    {
        string aHist = g.FieldValue(HistoryId, "");
        if (iEdit || (aHist.Length == 0 && m_Related.Count == 0 && m_RelatedSkipped.Count == 0)) return;
        using (g.Box("關聯文件", "mdview/related"))
        {
            using (g.Row())
            {
                if (aHist.Length > 0 && g.Button("上一份", "mdview/btn/history-back"))
                {
                    int aCut = aHist.LastIndexOf('\n');
                    g.SetField(PathId, aCut < 0 ? aHist : aHist.Substring(aCut + 1));
                    g.SetField(HistoryId, aCut < 0 ? "" : aHist.Substring(0, aCut));
                }
                for (int i = 0; i < m_Related.Count; i++)
                {
                    MdRelatedDoc r = m_Related[i];
                    if (!g.Button(r.Label, "mdview/btn/related/" + i.ToString(CultureInfo.InvariantCulture))) continue;
                    string? aAbs = ResolveRelated(r.Url, out string aWhy);
                    if (aAbs == null) { m_Message = $"開不了「{r.Label}」（{r.Url}）：{aWhy}"; continue; }
                    g.SetField(HistoryId, aHist.Length == 0 ? m_LoadedPath : aHist + "\n" + m_LoadedPath);
                    g.SetField(PathId, aAbs);
                }
            }
            foreach (MdRelatedDoc r in m_Related) if (r.Description != null) g.Note($"{r.Label}：{r.Description}");
            foreach (string s in m_RelatedSkipped) g.Note("略過（related 需要 `url | label [| 說明]`）：" + s);
        }
    }

    /// <summary>related 的 url → 絕對路徑。解不了回 null 並說為什麼（⛔ 不靜默略過那顆鈕）。</summary>
    string? ResolveRelated(string iUrl, out string oWhy)
    {
        oWhy = "";
        string aCandidate;
        if (iUrl.StartsWith("repo:", StringComparison.OrdinalIgnoreCase))
        {
            string aRoot = m_Model.ProjectRoot.Value;
            if (aRoot.Length == 0) { oWhy = "專案根解不出來（到「路徑管理」頁看）"; return null; }
            aCandidate = Path.Combine(aRoot, iUrl.Substring(5).TrimStart('/', '\\'));
        }
        else if (iUrl.IndexOf(':') > 1 && !Path.IsPathRooted(iUrl))
        {
            oWhy = $"「{iUrl.Substring(0, iUrl.IndexOf(':') + 1)}」前綴 Senate 沒有解析器（只認相對路徑／絕對路徑／repo:）";
            return null;
        }
        else aCandidate = Path.IsPathRooted(iUrl) ? iUrl : Path.Combine(Path.GetDirectoryName(m_LoadedPath) ?? "", iUrl);
        aCandidate = Path.GetFullPath(aCandidate).Replace('\\', '/');
        if (!File.Exists(aCandidate)) { oWhy = "檔案不存在：" + aCandidate; return null; }
        return aCandidate;
    }

    // ── 檢視模式：逐 block 渲染 ──────────────────────────

    void DrawViewer(SCP_Ui g)
    {
        if (m_Doc.Frontmatter != null && g.ToggleValue(FrontId, false))
            using (g.Box("frontmatter", "mdview/frontmatter"))
                g.Paragraph(m_Doc.Frontmatter);
        g.Separator();
        for (int i = 0; i < m_Doc.Blocks.Count; i++) DrawBlock(g, m_Doc.Blocks[i], i);
    }

    void DrawBlock(SCP_Ui g, MdBlock b, int iIndex)
    {
        switch (b.Type)
        {
            case MdBlockType.Heading:
                // 只有兩種字級（本文／標題）⇒ H1-H2 用標題字，H3 以下用 ■ 前綴＋縮排區分層級
                if (b.HeadingLevel <= 2) g.Title(MarkdownDoc.Inline(b.Text));
                else g.Paragraph(new string(' ', (b.HeadingLevel - 3) * 2) + "■ " + MarkdownDoc.Inline(b.Text));
                break;
            case MdBlockType.Paragraph:
                g.Paragraph(MarkdownDoc.Inline(b.Text));
                break;
            case MdBlockType.Bullet:
                g.Paragraph("  " + MarkdownDoc.Inline(b.Text));
                break;
            case MdBlockType.Quote:
                g.Paragraph("│ " + MarkdownDoc.Inline(b.Text));
                break;
            case MdBlockType.CodeFence:
                using (g.Box(b.CodeLang.Length > 0 ? b.CodeLang : "code", "mdview/code/" + iIndex.ToString(CultureInfo.InvariantCulture)))
                {
                    if (SCP_GuiHost.CopyToClipboard != null && g.Button("複製", "mdview/btn/copy-code/" + iIndex.ToString(CultureInfo.InvariantCulture)))
                        m_Message = SCP_GuiHost.CopyToClipboard(b.CodeBody);
                    g.Paragraph(b.CodeBody);   // code 原文照印，⛔ 不過 Inline（** 之類在 code 裡是字面）
                }
                break;
            case MdBlockType.HorizontalRule:
                g.Separator();
                break;
            case MdBlockType.Empty:
                g.Space();
                break;
            case MdBlockType.Table:
                DrawTable(g, b.TableRows);
                break;
            case MdBlockType.Mermaid:
                DrawMermaid(g, b, iIndex);
                break;
        }
    }

    static void DrawTable(SCP_Ui g, List<string[]> iRows)
    {
        if (iRows.Count == 0) return;
        int aCols = iRows[0].Length;
        string[] aHead = iRows[0].Select(MarkdownDoc.Inline).ToArray();
        using (g.Table(aHead))
            for (int r = 1; r < iRows.Count; r++)
            {
                var aCells = new string[aCols];   // 資料列格數不足補空字串、多的截掉（以 header 為準）
                for (int c = 0; c < aCols; c++) aCells[c] = c < iRows[r].Length ? MarkdownDoc.Inline(iRows[r][c]) : "";
                g.TableRow(aCells);
            }
    }

    // mermaid：不做真的圖排版 —— 從入度 0 的節點 DFS 成縮排樹，重訪標「(重複)」（同 Unity 版 v1）
    static void DrawMermaid(SCP_Ui g, MdBlock b, int iIndex)
    {
        MdMermaidGraph? aGraph = b.Graph;
        using (g.Box("mermaid graph " + (aGraph?.Direction ?? ""), "mdview/mermaid/" + iIndex.ToString(CultureInfo.InvariantCulture)))
        {
            if (aGraph == null || aGraph.Nodes.Count == 0)
            {
                g.Note("（mermaid：空的，或是這個簡化解析器看不懂的語法 —— 下面是原文）");
                g.Paragraph(b.CodeBody);
                return;
            }
            var aInDeg = aGraph.Nodes.Keys.ToDictionary(k => k, _ => 0);
            foreach (MdMermaidEdge e in aGraph.Edges) if (aInDeg.ContainsKey(e.To)) aInDeg[e.To]++;
            var aSeen = new HashSet<string>();
            foreach (var kv in aInDeg) if (kv.Value == 0 && !aSeen.Contains(kv.Key)) MermaidNode(g, aGraph, kv.Key, 0, aSeen, null);
            foreach (string id in aGraph.Nodes.Keys) if (!aSeen.Contains(id)) MermaidNode(g, aGraph, id, 0, aSeen, null);   // 環狀殘餘
        }
    }

    static void MermaidNode(SCP_Ui g, MdMermaidGraph iGraph, string iId, int iDepth, HashSet<string> ioSeen, string? iEdgeLabel)
    {
        if (!iGraph.Nodes.TryGetValue(iId, out MdMermaidNode? n)) return;
        bool aAgain = ioSeen.Contains(iId);
        string aNode = n.Shape switch { "round" => $"( {n.Label} )", "diamond" => $"< {n.Label} >", _ => $"[ {n.Label} ]" };
        string aArrow = iDepth == 0 ? "" : iEdgeLabel == null ? "→ " : $"─{iEdgeLabel}→ ";
        g.Label(new string(' ', iDepth * 2) + aArrow + aNode + (aAgain ? "  (重複)" : ""));
        if (aAgain) return;
        ioSeen.Add(iId);
        foreach (MdMermaidEdge e in iGraph.Edges)
            if (e.From == iId) MermaidNode(g, iGraph, e.To, iDepth + 1, ioSeen, e.Label);
    }
}
