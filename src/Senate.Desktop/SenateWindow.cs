// 區塊職責：開一個原生視窗、每幀重畫頁面、（可選）把畫面存成 PNG 後結束。
// 物理意義：⭐ `--screenshot` 不是花俏功能，是**驗收手段**：
//           原生視窗沒辦法被 CI／agent 用眼睛看，於是「GUI 到底有沒有畫出來、中文有沒有變方塊」
//           就沒有讀數。把 framebuffer 落成圖檔之後，那兩件事就變成可以被別人檢查的證據。
// 數值影響：--screenshot 模式跑固定幀數後**自己關掉**（第一幀 ImGui 還在建 font atlas 與量版位，
//           太早截圖會拍到空白或錯位的畫面）。互動模式則常駐直到使用者關窗。
// ⚠ 中文字型必須顯式載入。不載的話 ImGui 內建字型只有 ASCII ⇒ 中文全是方塊，
//   而那不會報錯，只會「看起來壞掉」。
using System.Runtime.InteropServices;
using System.Text;
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
    /// <summary>
    /// 畫一頁：吃這一輪的輸入，回傳畫好的 <see cref="SCP_Ui"/>。
    /// <para>⚠ 回的是整個 <c>SCP_Ui</c> 而不是 <c>Root</c> —— 因為除了節點樹之外，
    /// 頁面這一輪**要求寫回的欄位值**（<see cref="SCP_Ui.FieldWrites"/>）也掛在它身上。
    /// 只回樹的話，下拉選單選了一項之後那個選擇無處可去，畫面下一幀會跳回舊值。</para>
    /// </summary>
    public delegate SCP_Ui DrawPage(SCP_GuiInput iInput);

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

    /// <summary>
    /// ImGui 版面檔（`imgui.ini`）要寫到哪。null ＝ 用 ImGui 預設。
    /// <para>⚠ ImGui 的預設是**相對 cwd 的 `imgui.ini`** —— 不是相對執行檔、也不是相對 repo。
    /// ⇒ 同一顆 exe 從不同目錄啟動會讀寫不同的版面檔，而使用者只會覺得
    /// 「我拖好的版面有時候會不見」。落點必須由宿主顯式指定。</para>
    /// </summary>
    public string? IniPath { get; set; }

    /// <summary>
    /// `IniFilename` 的非託管字串。⚠ **必須活到 context 銷毀** ——
    /// ImGui 只存指標不複製內容，buffer 被 GC 掉之後它會讀到已釋放的記憶體。
    /// </summary>
    IntPtr m_IniPathUtf8 = IntPtr.Zero;

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

    /// <summary>
    /// 剪貼簿有沒有接上 ImGui（Ctrl+C / Ctrl+V）。
    /// <para>⚠ 一定要有這行讀數：沒接上的症狀是「按 Ctrl+V 安靜地沒反應」，
    /// 而那跟「這個宿主本來就不支援」長得一模一樣 —— 兩者要分得出來。</para>
    /// </summary>
    public string ClipboardStatus { get; private set; } = "(尚未安裝)";

    /// <summary>本文字級 —— 唯一來源是 <see cref="SCP_GuiStyle"/>，本類別不再自己存一份。</summary>
    public float FontSize => m_Style.FontSize;

    /// <summary>顯示參數（尺寸／間距／顏色）。</summary>
    public SCP_GuiStyle Style => m_Style;

    /// <summary>
    /// 把跨輪狀態（欄位／勾選／摺疊）灌進 renderer 當**初始值** —— 讓視窗接續 CLI session。
    /// <para>⭐ 存在的理由是驗收：視窗裡「展開的下拉／收起來的區塊」本來只有人點得到，
    /// 截圖模式沒有點擊入口 ⇒ 那些狀態在視窗長什麼樣**沒有讀數**。
    /// 先用 CLI（`--set` / `--fold` / `--click`，那一側會驗 id 存不存在）擺好狀態，再開窗截圖。</para>
    /// <para>⚠ **單向**：視窗不會把使用者在視窗裡的操作寫回 session。
    /// 兩邊互寫要處理「誰後寫誰贏」，而那是一個沒有人要求過的功能；
    /// 單向的行為講出來就不會被誤會，雙向寫壞了才會。</para>
    /// <para>⚠ **不含導覽（nav）**：視窗要停在哪一頁走 `--page`。
    /// 兩個機制搶著決定同一件事的結果是「我明明指定了頁卻開在別頁」。</para>
    /// </summary>
    public void Seed(SCP_GuiState iState)
    {
        if (iState == null) return;
        foreach (var kv in iState.Fields) m_Renderer.Fields[kv.Key] = kv.Value;
        foreach (var kv in iState.Toggles) m_Renderer.Toggles[kv.Key] = kv.Value;
        foreach (var kv in iState.Folds) m_Renderer.Folds[kv.Key] = kv.Value;
    }

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

        ApplyIniPath();

        // 剪貼簿 —— 必須在 controller 建好之後（那時 io 才存在）。
        // 🩸 這條線原本從來沒接上 ⇒ 視窗裡每一個輸入框都貼不上，而且不報錯。
        ClipboardStatus = ImGuiClipboardBridge.Install(ImGui.GetIO());

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

    /// <summary>
    /// 每幀把鍵盤 modifier（Ctrl / Shift / Alt / Super）餵給 ImGui。
    /// <para>🩸 <b>為什麼要自己補</b>（2026-08-28，Tim 實測 Ctrl+V 沒反應）：
    /// Silk.NET 的 <c>ImGuiController</c> 有 <c>AddKeyEvent</c> 與 <c>TranslateInputKeyToImGuiKey</c>，
    /// 但它的 metadata 裡**完全沒有 <c>ModCtrl</c> / <c>ImGuiMod</c>**（只有 <c>get_KeyCtrl</c> 讀取）——
    /// 也就是它從來沒有把 modifier 狀態送進 ImGui。
    /// 而 ImGui 的快捷鍵（Ctrl+V / Ctrl+C / Ctrl+A / Ctrl+X）判斷的是 <c>io.KeyMods</c>，
    /// 所以那些組合鍵**全部無效**，而**單獨打字照樣正常**（那條走 <c>AddInputCharacter</c>，
    /// 跟 modifier 無關）—— 兩者症狀不同形，正是它難被發現的原因。</para>
    /// <para>⚠ 順便把剪貼簿相關的字母鍵也補上：`TranslateInputKeyToImGuiKey` 有沒有涵蓋它們
    /// 我沒有 IL 層的讀數，而 <c>AddKeyEvent</c> 對「狀態沒變」是幂等的 ⇒
    /// 重複餵不會打架，漏掉才會壞。**在沒有讀數的地方選不會壞的那一邊。**</para>
    /// </summary>
    void FeedKeyModifiers()
    {
        if (m_Input == null || m_Input.Keyboards.Count == 0) return;
        IKeyboard aKb = m_Input.Keyboards[0];
        ImGuiIOPtr aIo = ImGui.GetIO();

        bool aCtrl = aKb.IsKeyPressed(Key.ControlLeft) || aKb.IsKeyPressed(Key.ControlRight);
        bool aShift = aKb.IsKeyPressed(Key.ShiftLeft) || aKb.IsKeyPressed(Key.ShiftRight);
        bool aAlt = aKb.IsKeyPressed(Key.AltLeft) || aKb.IsKeyPressed(Key.AltRight);
        bool aSuper = aKb.IsKeyPressed(Key.SuperLeft) || aKb.IsKeyPressed(Key.SuperRight);

        // 兩種都餵（官方 backend 也是這樣做）：`Mod*` 給快捷鍵判斷用，
        // 實體左右鍵給「哪一顆被按著」用。
        aIo.AddKeyEvent(ImGuiKey.ModCtrl, aCtrl);
        aIo.AddKeyEvent(ImGuiKey.ModShift, aShift);
        aIo.AddKeyEvent(ImGuiKey.ModAlt, aAlt);
        aIo.AddKeyEvent(ImGuiKey.ModSuper, aSuper);
        aIo.AddKeyEvent(ImGuiKey.LeftCtrl, aKb.IsKeyPressed(Key.ControlLeft));
        aIo.AddKeyEvent(ImGuiKey.RightCtrl, aKb.IsKeyPressed(Key.ControlRight));
        aIo.AddKeyEvent(ImGuiKey.LeftShift, aKb.IsKeyPressed(Key.ShiftLeft));
        aIo.AddKeyEvent(ImGuiKey.RightShift, aKb.IsKeyPressed(Key.ShiftRight));

        // 剪貼簿與選取的四顆字母鍵 ＋ Insert（Shift+Insert 也是貼上）
        aIo.AddKeyEvent(ImGuiKey.V, aKb.IsKeyPressed(Key.V));
        aIo.AddKeyEvent(ImGuiKey.C, aKb.IsKeyPressed(Key.C));
        aIo.AddKeyEvent(ImGuiKey.X, aKb.IsKeyPressed(Key.X));
        aIo.AddKeyEvent(ImGuiKey.A, aKb.IsKeyPressed(Key.A));
        aIo.AddKeyEvent(ImGuiKey.Insert, aKb.IsKeyPressed(Key.Insert));

        m_LastKeyCtrl = aCtrl;
        m_LastKeyV = aKb.IsKeyPressed(Key.V);
    }

    bool m_LastKeyCtrl;
    bool m_LastKeyV;

    /// <summary>自我對拍的結果：注入 ModCtrl 之後 io.KeyCtrl 讀回來是什麼（"(未跑)" ＝ 還沒到那一幀）。</summary>
    string m_ProbeKeyCtrl = "(未跑)";

    /// <summary>
    /// 畫一行鍵盤／剪貼簿診斷（`--keydebug`）。
    /// <para>⭐ 存在的理由：「Ctrl+V 沒反應」有三個可能的斷點 ——
    /// ① ImGui 收不到 Ctrl ② ImGui 收到了但沒呼叫 callback ③ callback 被呼叫但剪貼簿是空的。
    /// 三者在畫面上長得一模一樣，而這一行把它們分開。</para>
    /// </summary>
    void DrawKeyDebug()
    {
        ImGuiIOPtr aIo = ImGui.GetIO();
        ImGui.Separator();
        ImGui.TextUnformatted(
            $"[keydebug] io.KeyCtrl={aIo.KeyCtrl}  Silk:Ctrl={m_LastKeyCtrl} V={m_LastKeyV}"
            + $"  ｜ clipboard callback: Get={ImGuiClipboardBridge.GetCalls} Set={ImGuiClipboardBridge.SetCalls}"
            + $"  ｜ WantTextInput={aIo.WantTextInput}");
        ImGui.TextUnformatted(
            $"  自我對拍（不需按鍵）：注入 ModCtrl=true 之後 io.KeyCtrl 讀回 = {m_ProbeKeyCtrl}"
            + "　← 這格是 False 就是我補的那條路沒生效");
        ImGui.TextUnformatted(
            "  請你按：先點進「repo 路徑」欄位 → 按住 Ctrl 看 io.KeyCtrl 是否變 True → 按 Ctrl+V 看 Get 是否 +1。"
            + " Get 不動 ⇒ ImGui 沒把組合鍵交給 InputText；Get 有動但沒字 ⇒ 剪貼簿是空的。");
    }

    /// <summary>開了就在畫面底部畫一行鍵盤／剪貼簿診斷（`ui --window --keydebug`）。</summary>
    public bool KeyDebug { get; set; }

    void OnRender(double iDelta)
    {
        m_Frame++;

        // ⚠ 補鍵盤 modifier **必須在 Update 之前**：`Update` 內部會呼叫 `ImGui.NewFrame`，
        //   而 NewFrame 才會消化 AddKeyEvent 的事件佇列。放在後面的話 Ctrl 會慢一幀到，
        //   於是 ImGui 看到 V 的那一幀 Ctrl 還是 false ⇒ 快捷鍵永遠差一步，而它不會報錯。
        FeedKeyModifiers();

        // keydebug 的**自我對拍**：注入一次 ModCtrl=true，下一幀讀回 io.KeyCtrl。
        // ⭐ 驗的是「我補的那條路真的會讓 ImGui 的 KeyMods 變化」——
        //   這一格不需要有人按鍵盤，所以它是我唯一能自己拿到的讀數。
        //   ⚠ 必須在 FeedKeyModifiers **之後**（否則會被實際鍵盤狀態的 false 蓋掉）
        //     且在 Update（NewFrame）之前（否則要等下一幀才被消化）。
        if (KeyDebug)
        {
            if (m_Frame == 4) ImGui.GetIO().AddKeyEvent(ImGuiKey.ModCtrl, true);
            else if (m_Frame == 5) m_ProbeKeyCtrl = ImGui.GetIO().KeyCtrl ? "True" : "False";
            else if (m_Frame == 6) ImGui.GetIO().AddKeyEvent(ImGuiKey.ModCtrl, false);
        }

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

        SCP_Ui aUi = m_Draw(m_Renderer.TakeInput());
        m_Renderer.Render(aUi.Root);
        // 頁面要求的欄位寫入在**畫完之後**才套 —— 這一幀顯示的是頁面自己算出來的結果，
        // 套進 renderer 是為了下一幀（跟按鈕事件同一個「慢一幀」的節奏）。
        m_Renderer.ApplyWrites(aUi);

        if (KeyDebug) DrawKeyDebug();

        ImGui.End();
        m_Controller.Render();

        if (m_ScreenshotPath != null && m_Frame >= m_ScreenshotAtFrame)
        {
            SenateScreenshot.Capture(m_Gl, m_Window!.FramebufferSize.X, m_Window.FramebufferSize.Y, m_ScreenshotPath);
            m_Window.Close();
        }
    }

    /// <summary>
    /// 把 <see cref="IniPath"/> 接到 ImGui 的 <c>io.IniFilename</c>。
    /// <para>⚠ 三件事漏掉任何一件都是**靜默**的（版面不存、下次開窗回預設，
    /// 而那跟「使用者沒調過版面」同形）：</para>
    /// <list type="number">
    /// <item>目錄要先建 —— ImGui 存 ini 時不會替你建目錄。</item>
    /// <item>字串要 UTF-8 —— repo 可能住在含中日文的路徑下，ANSI 會編出另一串位元組。</item>
    /// <item>buffer 要活到 context 銷毀 —— ImGui 只存指標，不複製內容。</item>
    /// </list>
    /// </summary>
    void ApplyIniPath()
    {
        string? aPath = IniPath;
        if (string.IsNullOrWhiteSpace(aPath)) return;   // 沒指定 ⇒ 保持 ImGui 預設，不假裝設過

        string? aDir = Path.GetDirectoryName(aPath);
        if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);

        byte[] aBytes = Encoding.UTF8.GetBytes(aPath);
        IntPtr aBuf = Marshal.AllocHGlobal(aBytes.Length + 1);
        Marshal.Copy(aBytes, 0, aBuf, aBytes.Length);
        Marshal.WriteByte(aBuf, aBytes.Length, 0);      // 結尾的 NUL，C 端靠它判長度

        unsafe { ImGui.GetIO().NativePtr->IniFilename = (byte*)aBuf; }

        // 舊的先換掉再釋放，避免重入時把還在用的那塊放掉。
        IntPtr aOld = m_IniPathUtf8;
        m_IniPathUtf8 = aBuf;
        if (aOld != IntPtr.Zero) Marshal.FreeHGlobal(aOld);
    }

    void OnClosing()
    {
        // ⚠ 順序：先讓 ImGui 把版面存下來，再把 context 拆掉。
        //    controller 先 Dispose 的話，ImGui 還沒 flush 的 ini 就永遠寫不出去了 ——
        //    而使用者看到的是「我這次調的版面沒被記住」，沒有任何錯誤訊息。
        if (m_IniPathUtf8 != IntPtr.Zero)
        {
            try { ImGui.SaveIniSettingsToDisk(IniPath!); }
            catch (Exception e) { Console.Error.WriteLine($"⚠ ImGui 版面存檔失敗：{e.Message}"); }
        }

        m_Controller?.Dispose();
        m_Input?.Dispose();
        m_Gl?.Dispose();

        // context 沒了之後那個指標才可以放掉（ImGui 存的是指標不是複本）。
        if (m_IniPathUtf8 != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(m_IniPathUtf8);
            m_IniPathUtf8 = IntPtr.Zero;
        }
    }

    public void Dispose() { m_Window?.Dispose(); }
}
