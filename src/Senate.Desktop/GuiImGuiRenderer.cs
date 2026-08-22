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
using SCP.Core.Gui;

namespace Senate.Desktop;

public sealed class GuiImGuiRenderer
{
    /// <summary>這一幀被按下的按鈕 id（下一幀餵回頁面）。</summary>
    public string? ClickedId { get; private set; }

    /// <summary>欄位／勾選的當前值（跨幀狀態住在 renderer 這邊，頁面不必自己存）。</summary>
    public Dictionary<string, string> Fields { get; } = new();
    public Dictionary<string, bool> Toggles { get; } = new();

    /// <summary>把上一幀收集到的互動包成下一次 Draw 的輸入，並清掉一次性的點擊。</summary>
    public SCP_GuiInput TakeInput()
    {
        var aInput = new SCP_GuiInput { ClickedId = ClickedId };
        foreach (var kv in Fields) aInput.Fields[kv.Key] = kv.Value;
        foreach (var kv in Toggles) aInput.Toggles[kv.Key] = kv.Value;
        ClickedId = null;          // 點擊是事件，只送一次（不清會變成每幀都在按）
        return aInput;
    }

    public void Render(SCP_GuiNode iRoot)
    {
        foreach (var aChild in iRoot.Children) RenderNode(aChild);
    }

    void RenderNode(SCP_GuiNode iNode)
    {
        switch (iNode.Kind)
        {
            case SCP_GuiNodeKind.Title:
                ImGui.SeparatorText(iNode.Text);
                break;

            case SCP_GuiNodeKind.Label:
                ImGui.TextUnformatted(iNode.Text);
                break;

            case SCP_GuiNodeKind.Note:
                // 附註畫暗一點 —— 文字 renderer 用「· 」前綴表達同一件事
                ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.68f, 1f), "· " + iNode.Text);
                break;

            case SCP_GuiNodeKind.Separator:
                ImGui.Separator();
                break;

            case SCP_GuiNodeKind.Space:
                ImGui.Spacing();
                break;

            case SCP_GuiNodeKind.Button:
                if (ImGui.Button(iNode.Text + "##" + iNode.Id)) ClickedId = iNode.Id;
                break;

            case SCP_GuiNodeKind.Toggle:
            {
                bool aOn = Toggles.TryGetValue(iNode.Id, out bool v) ? v : iNode.On;
                if (ImGui.Checkbox(iNode.Text + "##" + iNode.Id, ref aOn)) Toggles[iNode.Id] = aOn;
                break;
            }

            case SCP_GuiNodeKind.TextField:
            {
                string aVal = Fields.TryGetValue(iNode.Id, out string? s) ? s : iNode.Value;
                // ⚠ 中文輸入（IME）就是在這個控件上見真章 —— 字型有載到才看得見候選字上屏的結果。
                if (ImGui.InputText(iNode.Text + "##" + iNode.Id, ref aVal, 4096)) Fields[iNode.Id] = aVal;
                break;
            }

            case SCP_GuiNodeKind.Row:
            {
                bool aFirst = true;
                foreach (var c in iNode.Children)
                {
                    if (!aFirst) ImGui.SameLine();
                    aFirst = false;
                    RenderNode(c);
                }
                break;
            }

            case SCP_GuiNodeKind.Column:
                foreach (var c in iNode.Children) RenderNode(c);
                break;

            case SCP_GuiNodeKind.Box:
            {
                // 有標題就用可摺疊區塊，沒標題就只縮排 —— 對應文字 renderer 的 ┌─┐ 框
                if (string.IsNullOrEmpty(iNode.Text))
                {
                    ImGui.Indent();
                    foreach (var c in iNode.Children) RenderNode(c);
                    ImGui.Unindent();
                }
                else if (ImGui.CollapsingHeader(iNode.Text, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Indent();
                    foreach (var c in iNode.Children) RenderNode(c);
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
}
