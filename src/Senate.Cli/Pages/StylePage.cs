// 區塊職責：介面尺寸設定頁 —— 從 Doctor 頁 push 進來的第二頁（頁面堆疊的第一個真消費者）。
// 物理意義：⭐ 它同時是兩件事：一個真的設定頁，也是「頁面系統在四種驅動方式下都成立」的讀數 ——
//           人在視窗裡按、agent 用 `ui --click doctor/open-style` 進來再 `--click style/big`、
//           文字模式看得到現在停在哪一頁（麵包屑）、截圖證明它真的畫出來了。
// 數值影響：按下尺寸會**寫回 senate.local.json**（走 DoctorModel.ApplySize → SenateUiStore，
//           寫完回讀確認才回報成功）。除此之外唯讀。
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed class StylePage : SCP_GuiPage
{
    readonly DoctorModel m_Model;

    public StylePage(DoctorModel iModel) { m_Model = iModel; }

    public override string Key => PageKey;
    public const string PageKey = "style";

    public override string Title => "介面尺寸";

    public override void Draw(SCP_Ui g)
    {
        // ⭐ 把「它以為自己多大」印出來 —— 尺寸這種東西「看起來變大了」不算讀數，
        //    截圖旁邊沒有數字就對不起來。
        g.Label(m_Model.Style.Describe());

        SCP_GuiSize? aPick = m_Model.Style.DrawPicker(g, "style");
        if (aPick.HasValue) m_Model.ApplySize(aPick.Value);

        if (m_Model.StyleMessage != null) g.Note(m_Model.StyleMessage);

        g.Space();
        g.Note("字級要重開視窗才會換（ImGui 的字級綁在載入時建好的 atlas）；間距與版位即時生效。");
        g.Note("純文字輸出的寬度不吃這個 scale —— 終端機的一格是字元不是像素（要調用 --width）。");
        g.Note($"常設值存在設定檔的 ui 區塊；--scale / --size 是一次性覆寫，不寫回檔案。");
    }
}
