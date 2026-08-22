// 區塊職責：中間層樹 → **純文字排版**（第一個 renderer）。
// 物理意義：這個 renderer 存在的理由不是「沒有視窗時的降級」，是**驗收手段**：
//           UI 有了文字輸出，就能 diff、能快照測試、能貼進聊天室給人看、能在 CI 跑。
//           ⇒ 「介面看起來對」變成「介面的讀數對」。
// 數值影響：純函式（樹進、字串出），零 IO、零全域狀態。
// ⚠ 寬度計算必須認得**全角字**：中文一個字佔兩格，用 string.Length 對齊表格會歪，
//   而歪掉的表格不會報錯，只會讓人不想讀 —— 不想讀就等於這些字沒寫。
using System.Text;

namespace Senate.Gui;

public static class GuiTextRenderer
{
    public const int DefaultWidth = 96;

    public static string Render(GuiNode iRoot, int iWidth = DefaultWidth)
    {
        var sb = new StringBuilder();
        foreach (var child in iRoot.Children) RenderNode(child, sb, 0, iWidth);
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    static void RenderNode(GuiNode iNode, StringBuilder oSb, int iIndent, int iWidth)
    {
        string pad = new string(' ', iIndent);
        int inner = Math.Max(20, iWidth - iIndent);

        switch (iNode.Kind)
        {
            case GuiNodeKind.Title:
                oSb.Append(pad).Append("── ").Append(iNode.Text).Append(' ')
                   .Append(new string('─', Math.Max(0, inner - Width(iNode.Text) - 4))).Append('\n');
                break;

            case GuiNodeKind.Label:
                oSb.Append(pad).Append(iNode.Text).Append('\n');
                break;

            case GuiNodeKind.Note:
                oSb.Append(pad).Append("· ").Append(iNode.Text).Append('\n');
                break;

            case GuiNodeKind.Separator:
                oSb.Append(pad).Append(new string('─', inner)).Append('\n');
                break;

            case GuiNodeKind.Space:
                oSb.Append('\n');
                break;

            case GuiNodeKind.Button:
                oSb.Append(pad).Append(Inline(iNode)).Append('\n');
                break;

            case GuiNodeKind.Toggle:
            case GuiNodeKind.TextField:
                oSb.Append(pad).Append(Inline(iNode)).Append('\n');
                break;

            case GuiNodeKind.Row:
            {
                // 一列 = 子節點的 inline 形式串起來。含群組的 Row 退化成逐項換行
                // （文字模式沒有真正的水平版位；硬排會讓巢狀內容互相蓋掉，寧可誠實地換行）。
                bool aAllInline = iNode.Children.All(IsInlineKind);
                if (aAllInline)
                {
                    oSb.Append(pad)
                       .Append(string.Join("   ", iNode.Children.Select(Inline)))
                       .Append('\n');
                }
                else
                {
                    foreach (var c in iNode.Children) RenderNode(c, oSb, iIndent, iWidth);
                }
                break;
            }

            case GuiNodeKind.Column:
                foreach (var c in iNode.Children) RenderNode(c, oSb, iIndent, iWidth);
                break;

            case GuiNodeKind.Box:
            {
                string aTitle = string.IsNullOrEmpty(iNode.Text) ? "" : $" {iNode.Text} ";
                oSb.Append(pad).Append('┌').Append(aTitle)
                   .Append(new string('─', Math.Max(0, inner - Width(aTitle) - 2))).Append('┐').Append('\n');
                foreach (var c in iNode.Children) RenderNode(c, oSb, iIndent + 2, iWidth);
                oSb.Append(pad).Append('└').Append(new string('─', Math.Max(0, inner - 2))).Append('┘').Append('\n');
                break;
            }

            case GuiNodeKind.Table:
                RenderTable(iNode, oSb, iIndent);
                break;

            default:
                foreach (var c in iNode.Children) RenderNode(c, oSb, iIndent, iWidth);
                break;
        }
    }

    static bool IsInlineKind(GuiNode iNode) => iNode.Kind is GuiNodeKind.Label or GuiNodeKind.Note
        or GuiNodeKind.Button or GuiNodeKind.Toggle or GuiNodeKind.TextField;

    static string Inline(GuiNode iNode) => iNode.Kind switch
    {
        GuiNodeKind.Button => $"[ {iNode.Text} ]",
        GuiNodeKind.Toggle => $"[{(iNode.On ? "x" : " ")}] {iNode.Text}",
        GuiNodeKind.TextField => $"{iNode.Text}: ⟨{iNode.Value}⟩",
        GuiNodeKind.Note => $"· {iNode.Text}",
        _ => iNode.Text,
    };

    static void RenderTable(GuiNode iTable, StringBuilder oSb, int iIndent)
    {
        var rows = new List<List<string>>();
        if (iTable.Headers.Count > 0) rows.Add(iTable.Headers.ToList());
        foreach (var r in iTable.Children)
        {
            if (r.Kind != GuiNodeKind.TableRow) continue;
            rows.Add(r.Children.Select(c => c.Kind == GuiNodeKind.TableCell ? c.Text : Inline(c)).ToList());
        }
        if (rows.Count == 0) return;

        int cols = rows.Max(r => r.Count);
        var w = new int[cols];
        foreach (var r in rows)
            for (int i = 0; i < r.Count; i++) w[i] = Math.Max(w[i], Width(r[i]));

        string pad = new string(' ', iIndent);
        for (int ri = 0; ri < rows.Count; ri++)
        {
            var cells = new List<string>();
            for (int ci = 0; ci < cols; ci++)
            {
                string cell = ci < rows[ri].Count ? rows[ri][ci] : "";
                cells.Add(cell + new string(' ', Math.Max(0, w[ci] - Width(cell))));
            }
            oSb.Append(pad).Append(string.Join("  ", cells).TrimEnd()).Append('\n');
            if (ri == 0 && iTable.Headers.Count > 0)
                oSb.Append(pad)
                   .Append(string.Join("  ", w.Select(x => new string('─', Math.Max(1, x)))))
                   .Append('\n');
        }
    }

    /// <summary>顯示寬度（全角字算 2 格）。</summary>
    public static int Width(string iText)
    {
        int n = 0;
        foreach (char c in iText) n += IsWide(c) ? 2 : 1;
        return n;
    }

    static bool IsWide(char c) =>
        (c >= 0x1100 && c <= 0x115F) ||    // Hangul Jamo
        (c >= 0x2E80 && c <= 0xA4CF) ||    // CJK radicals … Yi（含中日韓漢字、假名、注音）
        (c >= 0xAC00 && c <= 0xD7A3) ||    // Hangul syllables
        (c >= 0xF900 && c <= 0xFAFF) ||    // CJK compatibility ideographs
        (c >= 0xFE30 && c <= 0xFE6F) ||    // CJK compatibility forms
        (c >= 0xFF00 && c <= 0xFF60) ||    // 全角 ASCII
        (c >= 0xFFE0 && c <= 0xFFE6);
}
