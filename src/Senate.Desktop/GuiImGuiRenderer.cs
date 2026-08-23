// 區塊職責：中間層節點樹 → **ImGui 視窗**（第二個 renderer）。
// 物理意義：頁面碼一行都沒改 —— 同一棵樹，文字 renderer 畫成表格，這支畫成視窗。
//           這就是中間層存在的理由：換畫布不動頁面，而**兩個 renderer 互為證人**
//           （文字輸出可以 diff，視窗可以用眼睛看，兩邊不一致就是有一邊錯了）。
// 數值影響：本 renderer **不改樹**，只讀。互動的處理方式是「回報這一幀誰被按了」——
//           immediate-mode 疊在 retained 畫布上的標準做法：
//           點擊在第 N 幀被記錄，第 N+1 幀重新 Draw 時透過 SCP_GuiInput 餵回頁面，
//           所以頁面看到的 `Button(...) == true` 慢一幀。⚠ 這不是 bug，是設計 ——
//           但**要知道它慢一幀**，否則「按了沒反應」會被誤讀成事件掉了。
using System.Numerics;
using ImGuiNET;
using SCP.Core;
using SCP.Core.Gui;

namespace Senate.Desktop;

public sealed class GuiImGuiRenderer
{
    readonly SCP_GuiStyle m_Style;

    /// <summary>
    /// 標題字型（比本文大一號）。由 <see cref="SenateWindow"/> 在字型載完後塞進來；
    /// 沒設 ⇒ 標題用本文字型（**不假裝有大一號**）。
    /// </summary>
    public ImFontPtr? TitleFont { get; set; }

    public GuiImGuiRenderer(SCP_GuiStyle? iStyle = null) { m_Style = iStyle ?? new SCP_GuiStyle(); }

    /// <summary>這一幀被按下的按鈕 id（下一幀餵回頁面）。</summary>
    public string? ClickedId { get; private set; }

    /// <summary>欄位／勾選的當前值（跨幀狀態住在 renderer 這邊，頁面不必自己存）。</summary>
    public Dictionary<string, string> Fields { get; } = new();
    public Dictionary<string, bool> Toggles { get; } = new();

    /// <summary>摺疊狀態（Box id → 展開中嗎）。⚠ 不能靠 ImGui 自己記 ——
    /// 它記在自己的 id 空間裡，頁面／CLI／session 都讀不到，於是「我摺起來的東西」換個驅動方式就散了。</summary>
    public Dictionary<string, bool> Folds { get; } = new();

    /// <summary>把上一幀收集到的互動包成下一次 Draw 的輸入，並清掉一次性的點擊。</summary>
    public SCP_GuiInput TakeInput()
    {
        var aInput = new SCP_GuiInput { ClickedId = ClickedId };
        foreach (var kv in Fields) aInput.Fields[kv.Key] = kv.Value;
        foreach (var kv in Toggles) aInput.Toggles[kv.Key] = kv.Value;
        foreach (var kv in Folds) aInput.Folds[kv.Key] = kv.Value;
        ClickedId = null;          // 點擊是事件，只送一次（不清會變成每幀都在按）
        return aInput;
    }

    /// <summary>
    /// 套用頁面這一輪要求的欄位寫入（下拉的開闔／選擇／頁碼走這條路）。
    /// <para>⚠ 只有頁面**主動要求**時才會有東西 —— 每幀無條件覆寫的話，
    /// 使用者正在 InputText 裡打的字會被蓋掉，而症狀是「打了字自己跳回去」。</para>
    /// </summary>
    public void ApplyWrites(SCP_Ui iUi)
    {
        foreach (var kv in iUi.FieldWrites) Fields[kv.Key] = kv.Value;
    }

    public void Render(SCP_GuiNode iRoot)
    {
        foreach (var aChild in iRoot.Children) RenderNode(aChild);
    }

    /// <param name="iForcedWidth">
    /// 由「等寬群組」（<see cref="SCP_GuiNode.UniformWidth"/>）指定給**直接子節點**的寬度；
    /// 0 ＝ 沒有指定。⚠ 遞迴進更深一層時不往下傳 —— 等寬只約束直接子節點，
    /// 不然巢狀的分頁列也會被撐成一樣寬。
    /// </param>
    void RenderNode(SCP_GuiNode iNode, float iForcedWidth = 0f)
    {
        switch (iNode.Kind)
        {
            case SCP_GuiNodeKind.Title:
                if (TitleFont.HasValue) ImGui.PushFont(TitleFont.Value);
                ImGui.SeparatorText(iNode.Text);
                if (TitleFont.HasValue) ImGui.PopFont();
                break;

            case SCP_GuiNodeKind.Label:
                ImGui.TextUnformatted(iNode.Text);
                break;

            case SCP_GuiNodeKind.Note:
                // 附註畫暗一點 —— 文字 renderer 用「· 」前綴表達同一件事。色值來自 style，不寫死。
                ImGui.TextColored(Vec4(m_Style.NoteColor), "· " + iNode.Text);
                break;

            case SCP_GuiNodeKind.Separator:
                ImGui.Separator();
                break;

            case SCP_GuiNodeKind.Space:
                ImGui.Spacing();
                break;

            case SCP_GuiNodeKind.Button:
            {
                // 最小寬度走 style（一排按鈕寬度不一會讓版面看起來是壞的），
                // ⚠ 但取 max 不是直接套：ImGui 的 size 是**確定尺寸**不是下限，
                //   寫死就會把長標籤裁掉 —— 而裁掉的字不會報錯。
                float aW = iForcedWidth > 0f ? iForcedWidth : ButtonNaturalWidth(iNode.Text);
                if (ImGui.Button(iNode.Text + "##" + iNode.Id, new Vector2(aW, 0f)))
                    ClickedId = iNode.Id;
                break;
            }

            case SCP_GuiNodeKind.Toggle:
            {
                bool aOn = Toggles.TryGetValue(iNode.Id, out bool v) ? v : iNode.On;
                // 標籤畫在**左邊**：ImGui 原生把 label 放右邊，一排欄位下來眼睛要左右跳
                LabelLeft(iNode.Text);
                if (ImGui.Checkbox("##" + iNode.Id, ref aOn)) Toggles[iNode.Id] = aOn;
                break;
            }

            case SCP_GuiNodeKind.TextField:
            {
                string aVal = Fields.TryGetValue(iNode.Id, out string? s) ? s : iNode.Value;
                // ⚠ 中文輸入（IME）就是在這個控件上見真章 —— 字型有載到才看得見候選字上屏的結果。
                // 等寬群組裡的輸入框要跟旁邊的鈕**切齊右緣**。
                // 🩸 第一版沿用頁面級的 LabelWidth 對齊欄（150×scale ＝ 225px），
                //    而整個群組才 290px ⇒ 標籤先吃掉 225，輸入框只剩 65，
                //    我再用 `Max(TextFieldWidth*0.5, …)` 把它撐回 165 —— 於是整條凸出群組 100px。
                //    ⇒ 那個對齊欄是**頁面級的約定**，套進一個窄群組裡前提就不成立了。
                //    群組裡改成「標籤自然寬 ＋ 剩下的全給輸入框」。
                if (iForcedWidth > 0f)
                {
                    float aLabelW = LabelNaturalSpan(iNode.Text);
                    LabelLeftCompact(iNode.Text);
                    ImGui.SetNextItemWidth(Math.Max(m_Style.ButtonMinWidth * 0.5f, iForcedWidth - aLabelW));
                }
                else
                {
                    LabelLeft(iNode.Text);
                    ImGui.SetNextItemWidth(m_Style.TextFieldWidth);
                }
                if (ImGui.InputText("##" + iNode.Id, ref aVal, 4096)) Fields[iNode.Id] = aVal;
                break;
            }

            case SCP_GuiNodeKind.Row:
            {
                // 🩸 2026-08-23：舊版對每一個子節點都 SameLine()，包括群組（Box／Table／巢狀 Row）。
                //    SameLine 只把游標移到前一個元件的右邊，而群組會往下長好幾行 ——
                //    於是展開的下拉整疊畫在同一列的鈕上面，**疊成一團**。抓到它的是 Tim 的截圖。
                //    ⚠ 我的第一版修法是「遇群組就換行」（照文字 renderer 的規矩）——
                //    那不疊了，但也放棄了 ImGui **做得到**的東西：`BeginGroup` 會把游標的 X
                //    當成群組的新左緣，群組裡的每一行都從那裡開始。
                //    ⇒ 正解是**包成群組**而不是換行：一顆鈕旁邊放一整塊垂直內容，
                //    而那塊內容的左緣對齊它自己的起點（＝ Unity 端 GUILayout 的手感）。
                bool aFirst = true;
                foreach (var c in iNode.Children)
                {
                    if (!aFirst) ImGui.SameLine();
                    aFirst = false;

                    if (SCP_GuiNode.IsInline(c.Kind)) { RenderNode(c); continue; }
                    ImGui.BeginGroup();
                    RenderNode(c);
                    ImGui.EndGroup();
                }
                break;
            }

            case SCP_GuiNodeKind.Column:
            {
                float aUniform = UniformWidthOf(iNode);
                foreach (var c in iNode.Children) RenderNode(c, aUniform);
                break;
            }

            case SCP_GuiNodeKind.Box:
            {
                // 沒標題就只縮排 —— 對應文字 renderer 的 ┌─┐ 框
                float aUniform = UniformWidthOf(iNode);

                if (string.IsNullOrEmpty(iNode.Text))
                {
                    // ⚠ 沒標題的 Box 在 ImGui 裡**不畫任何東西**（沒有框、沒有標頭）——
                    //    所以「縮排」曾經是它唯一的視覺效果，而那個縮排是憑空來的：
                    //    沒有標頭可以縮在下面，卻讓內容跟外面的東西對不齊。
                    //    ⇒ 當成純粹的群組容器（同 IdScope 的「版面上透明」）。
                    //    文字 renderer 那側照舊畫框並縮排 —— 它**有**框，縮排才有依據。
                    foreach (var c in iNode.Children) RenderNode(c, aUniform);
                    break;
                }

                if (!iNode.Collapsible)
                {
                    if (ImGui.CollapsingHeader(iNode.Text, ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        ImGui.Indent();
                        foreach (var c in iNode.Children) RenderNode(c, aUniform);
                        ImGui.Unindent();
                    }
                    break;
                }

                // ⭐ 可摺疊的框：狀態**由頁面那邊給**（SetNextItemOpen），使用者點了就回報回去。
                //   ⚠ 慢一幀：這一幀點開的東西，內容要等下一幀頁面重畫才會有
                //   （子節點在收合時根本沒被建出來 —— 那正是它省事的原因）。
                bool aOpen = Folds.TryGetValue(iNode.Id, out bool aState) ? aState : iNode.Open;
                ImGui.SetNextItemOpen(aOpen);
                bool aNow = ImGui.CollapsingHeader(iNode.Text + "##" + iNode.Id);
                if (aNow != aOpen) Folds[iNode.Id] = aNow;
                if (aNow)
                {
                    ImGui.Indent();
                    foreach (var c in iNode.Children) RenderNode(c, aUniform);
                    ImGui.Unindent();
                }
                break;
            }

            case SCP_GuiNodeKind.Table:
                RenderTable(iNode);
                break;

            default:
                foreach (var c in iNode.Children) RenderNode(c);
                break;
        }
    }

    void RenderTable(SCP_GuiNode iTable)
    {
        int aCols = iTable.Headers.Count;
        foreach (var r in iTable.Children)
            if (r.Kind == SCP_GuiNodeKind.TableRow) aCols = Math.Max(aCols, r.Children.Count);
        if (aCols <= 0) return;

        // id 用節點路徑，不用「第幾張表」—— 序號會隨頁面增刪而漂（同 GuiIdScope 的理由）
        string aId = "tbl##" + (iTable.Headers.Count > 0 ? string.Join("|", iTable.Headers) : "anon");
        var aFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
                     | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable(aId, aCols, aFlags)) return;

        if (iTable.Headers.Count > 0)
        {
            foreach (string h in iTable.Headers) ImGui.TableSetupColumn(h);
            for (int i = iTable.Headers.Count; i < aCols; i++) ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();
        }

        foreach (var r in iTable.Children)
        {
            if (r.Kind != SCP_GuiNodeKind.TableRow) continue;
            ImGui.TableNextRow();
            for (int c = 0; c < aCols; c++)
            {
                ImGui.TableSetColumnIndex(c);
                if (c < r.Children.Count)
                {
                    var aCell = r.Children[c];
                    if (aCell.Kind == SCP_GuiNodeKind.TableCell) ImGui.TextUnformatted(aCell.Text);
                    else RenderNode(aCell);
                }
            }
        }
        ImGui.EndTable();
    }

    /// <summary>一顆鈕不被撐開時的自然寬度（文字寬 ＋ padding，但不小於 style 的下限）。</summary>
    float ButtonNaturalWidth(string iText)
        => Math.Max(m_Style.ButtonMinWidth, ImGui.CalcTextSize(iText).X + m_Style.FramePaddingX * 2f);

    /// <summary>標籤畫在左邊、**不對齊頁面級欄位欄**時佔掉的水平空間。</summary>
    float LabelNaturalSpan(string iLabel)
        => string.IsNullOrEmpty(iLabel) ? 0f : ImGui.CalcTextSize(iLabel).X + m_Style.ItemSpacingX;

    /// <summary>標籤畫在左邊，緊貼著（不補到 LabelWidth）—— 給窄群組用。</summary>
    void LabelLeftCompact(string iLabel)
    {
        if (string.IsNullOrEmpty(iLabel)) return;
        ImGui.TextUnformatted(iLabel);
        ImGui.SameLine();
    }

    /// <summary>
    /// 等寬群組要用的寬度 ＝ **直接子節點裡最寬的那顆鈕**（沒宣告等寬 ⇒ 0，各自照自然寬度）。
    /// <para>為什麼要等寬：一排寬度不一的選項，眼睛沒有一條可以往下掃的直線 ——
    /// 那不是美觀問題，是「要看第幾項」每一次都得重新對焦。（形狀取自 Unity 端的 PopupSearch。）</para>
    /// </summary>
    float UniformWidthOf(SCP_GuiNode iNode)
    {
        if (!iNode.UniformWidth) return 0f;
        float aMax = 0f;
        foreach (var c in iNode.Children)
            if (c.Kind == SCP_GuiNodeKind.Button) aMax = Math.Max(aMax, ButtonNaturalWidth(c.Text));
        return aMax;
    }

    /// <summary>
    /// 把標籤畫在控件**左邊**並對齊到 style 的欄寬。
    /// <para>⚠ 標籤比欄寬長時不裁字、直接推開 —— 裁掉的字不會報錯，只會讓人讀不懂那一格是什麼。</para>
    /// </summary>
    void LabelLeft(string iLabel)
    {
        if (string.IsNullOrEmpty(iLabel)) return;
        ImGui.TextUnformatted(iLabel);
        float aTextW = ImGui.CalcTextSize(iLabel).X;
        if (aTextW + m_Style.ItemSpacingX < m_Style.LabelWidth) ImGui.SameLine(m_Style.LabelWidth);
        else ImGui.SameLine();
    }

    /// <summary>共用層的顏色 → ImGui 的 Vector4（共用層刻意不認識 System.Numerics）。</summary>
    internal static Vector4 Vec4(SCP_Color iColor) => new(iColor.R, iColor.G, iColor.B, iColor.A);
}
