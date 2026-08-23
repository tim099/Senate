// 區塊職責：開一個原生視窗、每幀重畫頁面、（可選）把畫面存成 PNG 後結束。
// 物理意義：⭐ `--screenshot` 不是花俏功能，是**驗收手段**：
//           原生視窗沒辦法被 CI／agent 用眼睛看，於是「GUI 到底有沒有畫出來、中文有沒有變方塊」
//           就沒有讀數。把 framebuffer 落成圖檔之後，那兩件事就變成可以被別人檢查的證據。
// 數值影響：--screenshot 模式跑固定幀數後**自己關掉**（第一幀 ImGui 還在建 font atlas 與量版位，
//           太早截圖會拍到空白或錯位的畫面）。互動模式則常駐直到使用者關窗。
// ⚠ 中文字型必須顯式載入。不載的話 ImGui 內建字型只有 ASCII ⇒ 中文全是方塊，
//   而那不會報錯，只會「看起來壞掉」。
using SCP.Core.Gui;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using ImGuiNET;

namespace Senate.Desktop;

public sealed class SenateWindow : IDisposable
{
    /// <summary>畫一頁：吃這一輪的輸入，回傳畫好的節點樹。</summary>
    public delegate SCP_GuiNode DrawPage(SCP_GuiInput iInput);

    readonly DrawPage m_Draw;
    readonly string m_Title;
    readonly SCP_GuiStyle m_Style;
    readonly GuiImGuiRenderer m_Renderer;

    IWindow? m_Window;
    GL? m_Gl;
    IInputContext? m_Input;
    ImGuiController? m_Controller;

    string? m_ScreenshotPath;
    int m_ScreenshotAtFrame;
    int m_Frame;

    public SenateWindow(string iTitle, DrawPage iDraw, SCP_GuiStyle? iStyle = null)
    {
        m_Title = iTitle;
        m_Draw = iDraw;
        m_Style = iStyle ?? new SCP_GuiStyle();
        m_Renderer = new GuiImGuiRenderer(m_Style);
    }

    /// <summary>找一顆有中文的字型。找不到就回 null（呼叫端要**說出來**，不要假裝有載到）。</summary>
    public static string? FindCjkFont()
    {
        string[] aCandidates =
        {
            @"C:\Windows\Fonts\msjh.ttc",       // 微軟正黑體
            @"C:\Windows\Fonts\msjhl.ttc",
            @"C:\Windows\Fonts\mingliu.ttc",    // 細明體
            @"C:\Windows\Fonts\simsun.ttc",
            "/System/Library/Fonts/PingFang.ttc",
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
        };
        foreach (string p in aCandidates) if (File.Exists(p)) return p;
        return null;
    }

    public string? FontPath { get; private set; }

    /// <summary>實際載到的字型（呼叫端印出來給人看 —— 「以為載到了」是這一族最常見的錯）。</summary>
    public string LoadedFonts { get; private set; } = "(尚未載入)";

    /// <summary>本文字級 —— 唯一來源是 <see cref="SCP_GuiStyle"/>，本類別不再自己存一份。</summary>
    public float FontSize => m_Style.FontSize;

    /// <summary>顯示參數（尺寸／間距／顏色）。</summary>
    public SCP_GuiStyle Style => m_Style;

    /// <summary>
    /// 跑起來。iScreenshotPath 非 null ⇒ 拍完就結束（不進互動迴圈）。
    /// <para>iWidth / iHeight ≤ 0 ⇒ 用 style 算出來的預設尺寸（會被螢幕可用區夾住）。</para>
    /// </summary>
    public void Run(string? iScreenshotPath = null, int iScreenshotAtFrame = 8, int iWidth = 0, int iHeight = 0)
    {
        (iWidth, iHeight) = ResolveWindowSize(iWidth, iHeight);
        m_ScreenshotPath = iScreenshotPath;
        m_ScreenshotAtFrame = iScreenshotAtFrame;

        var aOptions = WindowOptions.Default;
        aOptions.Size = new Vector2D<int>(iWidth, iHeight);
        aOptions.Title = m_Title;
        aOptions.VSync = true;
        m_Window = Window.Create(aOptions);

        m_Window.Load += OnLoad;
        m_Window.Render += OnRender;
        m_Window.FramebufferResize += s => m_Gl?.Viewport(s);
        m_Window.Closing += OnClosing;

        m_Window.Run();
        m_Window.Dispose();
    }

    /// <summary>
    /// 決定視窗尺寸：style 的預設值（＝基準 × scale）**夾在主螢幕可用區之內**。
    /// <para>🩸 為什麼要夾：scale 2.0 時 1280×800 會變 2560×1600 ——
    /// 在 1920×1080 的機器上那是一個比桌面還大的視窗，標題欄跑到螢幕外、關不掉，
    /// 而它不會報錯（「開起來就是壞的」不是例外，是版位）。
    /// 問不到螢幕尺寸時**不猜**，直接用 style 的值（問不到與量到 0 不得同形）。</para>
    /// </summary>
    (int w, int h) ResolveWindowSize(int iWidth, int iHeight)
    {
        int aW = iWidth > 0 ? iWidth : m_Style.WindowWidth;
        int aH = iHeight > 0 ? iHeight : m_Style.WindowHeight;
        try
        {
            var aMon = Silk.NET.Windowing.Monitor.GetMainMonitor(null);
            var aBounds = aMon.Bounds;
            if (aBounds.Size.X > 0 && aBounds.Size.Y > 0)
            {
                aW = Math.Min(aW, (int)(aBounds.Size.X * 0.95f));
                aH = Math.Min(aH, (int)(aBounds.Size.Y * 0.90f));
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"⚠ 問不到主螢幕尺寸（{e.GetType().Name}）—— 視窗用 style 的 {aW}×{aH}，沒有夾");
        }
        return (aW, aH);
    }

    void OnLoad()
    {
        IWindow aWin = m_Window ?? throw new InvalidOperationException("OnLoad 在 window 建立前被呼叫");
        m_Gl = aWin.CreateOpenGL();
        m_Input = aWin.CreateInput();
        FontPath = FindCjkFont();

        // 字型全在 onConfigureIO 裡自己組（中文 ＋ 符號合併）——
        // 🩸 用 ImGuiFontConfig + GetGlyphRangesChineseFull 的第一版：中文好了，
        //    但 ✓ ≥ ⇒ ⚠ 全變成 ?（那份 range 不含符號區），而缺字不報錯。詳見 SenateFonts。
        SenateFonts.FontSet? aFonts = null;
        m_Controller = new ImGuiController(m_Gl, aWin, m_Input, null,
            () =>
            {
                aFonts = SenateFonts.Configure(ImGui.GetIO(), FontPath, m_Style);
                LoadedFonts = aFonts.Description;
            });

        ImGui.StyleColorsDark();
        ApplyStyle();

        // 標題字型交給 renderer（沒載到就不設 ⇒ 標題用本文字級，不假裝有大一號）
        if (aFonts != null) m_Renderer.TitleFont = aFonts.Title;
    }

    /// <summary>把 <see cref="SCP_GuiStyle"/> 的尺寸／間距灌進 ImGui 的全域樣式。</summary>
    void ApplyStyle()
    {
        var aStyle = ImGui.GetStyle();
        aStyle.WindowRounding = m_Style.WindowRounding;
        aStyle.FrameRounding = m_Style.FrameRounding;
        aStyle.WindowPadding = new System.Numerics.Vector2(m_Style.WindowPaddingX, m_Style.WindowPaddingY);
        aStyle.FramePadding = new System.Numerics.Vector2(m_Style.FramePaddingX, m_Style.FramePaddingY);
        aStyle.ItemSpacing = new System.Numerics.Vector2(m_Style.ItemSpacingX, m_Style.ItemSpacingY);
        aStyle.CellPadding = new System.Numerics.Vector2(m_Style.CellPaddingX, m_Style.CellPaddingY);
        aStyle.IndentSpacing = m_Style.IndentSpacing;
        aStyle.ScrollbarSize = m_Style.ScrollbarSize;
        aStyle.GrabMinSize = m_Style.GrabMinSize;
    }

    void OnRender(double iDelta)
    {
        m_Frame++;
        m_Controller!.Update((float)iDelta);

        // 每幀重灌尺寸／間距：使用者在頁面上換尺寸時**版位即時跟著變**。
        // ⚠ 字級不在這裡 —— ImGui 的字級綁在載入時建好的 atlas，換字級要重開視窗（要說出來，不要假裝生效）。
        ApplyStyle();

        var aBg = m_Style.BackgroundColor;
        m_Gl!.ClearColor(aBg.R, aBg.G, aBg.B, aBg.A);
        m_Gl.Clear((uint)ClearBufferMask.ColorBufferBit);

        // 頁面填滿整個視窗（這是後台，不是多視窗編輯器）
        var aVp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(aVp.WorkPos);
        ImGui.SetNextWindowSize(aVp.WorkSize);
        ImGui.Begin(m_Title,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus);

        SCP_GuiNode aTree = m_Draw(m_Renderer.TakeInput());
        m_Renderer.Render(aTree);

        ImGui.End();
        m_Controller.Render();

        if (m_ScreenshotPath != null && m_Frame >= m_ScreenshotAtFrame)
        {
            SenateScreenshot.Capture(m_Gl, m_Window!.FramebufferSize.X, m_Window.FramebufferSize.Y, m_ScreenshotPath);
            m_Window.Close();
        }
    }

    void OnClosing()
    {
        m_Controller?.Dispose();
        m_Input?.Dispose();
        m_Gl?.Dispose();
    }

    public void Dispose() { m_Window?.Dispose(); }
}
