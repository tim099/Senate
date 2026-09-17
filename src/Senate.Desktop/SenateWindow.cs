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

    System.Diagnostics.Stopwatch? m_SoakClock;
    double m_SoakWorstFrameMs;
    double m_SoakFirstFrameMs;

    /// <summary>
    /// 開窗之後**真的轉這麼多秒**再收工（0 ＝ 不轉，維持原本的固定幀數行為）。
    /// <para>⭐ 為什麼要有這一格：8 幀（約 130ms）拍得到「畫得出來」，
    /// 拍不到「每幀成本」與「背景工作跑的時候畫面凍不凍」——
    /// 而後兩者壞掉的樣子是**畫面看起來正常、只是不動**，截圖與它同形。
    /// ⇒ 會重畫的宿主要開真視窗轉一段時間，那一段時間裡的幀數才是讀數。
    /// 🩸 出處：@basecamp 2026-08-28 的驗收清單條文（她 headless 全綠交付的頁面，
    /// Tim 開一次視窗就抓到卡死）。</para>
    /// </summary>
    public double SoakSeconds { get; set; }

    /// <summary>soak 跑完的讀數（幀數／秒數／平均 fps／最慢一幀）。沒跑 soak 就是 null。</summary>
    public string? SoakReading { get; private set; }

    /// <summary>
    /// 收工時內容子區域的捲動讀數（TASK-0236 ④：**誰在捲／捲了多少**，可複驗）。
    /// <para>⚠ 它一定指名容器（`scp/content`）—— 不指名的讀數沒辦法分辨
    /// 「這一格捲不動」與「我量錯了另一格」，而那正是本張單被誤判三次的原因。</para>
    /// </summary>
    public string? ScrollReading { get; private set; }

    // 外層視窗的捲動讀數（每幀更新，收工時併進 ScrollReading）。
    float m_OuterScrollY, m_OuterScrollMaxY, m_OuterContentH, m_OuterWindowH;

    // ── 常駐窗的對外接點（TASK-0214）──────────────────────────────────
    // 區塊職責：讓宿主在**每一幀畫完之後**插手一次 —— 拿到這一幀真的畫出來的那棵樹。
    // 數值影響：⭐ 每幀的固定成本＝**一次 null 檢查**（OnFrameServed?.Invoke）＋ 幾個數的累加。
    //           ⛔ 這裡**不做** IO、不序列化樹、也不保存樹 —— 那些只在宿主真的要用時才發生，
    //           而宿主自己靠 FileSystemWatcher 決定要不要動（Tim 2026-09-15：文字樹要走額外指令）。

    /// <summary>
    /// 每幀畫完之後叫一次，參數是**這一幀真的畫出來的那棵樹**。
    /// <para>⚠ 給的是畫完的樹不是畫之前的 —— 這正是「文字要描述窗上現在長什麼樣」那一格；
    /// 給畫之前的樹會讓 --list 永遠慢一幀，而慢一幀的清單跟正確的清單長得一模一樣。</para>
    /// </summary>
    public Action<SCP_GuiNode>? OnFrameServed { get; set; }

    /// <summary>注入一次點擊（下一幀生效）—— 走 renderer 的同一格，跟真的用滑鼠按下去無法區分。</summary>
    public void InjectClick(string iId) => m_Renderer.InjectClick(iId);

    /// <summary>注入欄位寫入／勾選／摺疊（立刻寫進 renderer 的跨幀狀態，下一幀畫出來）。</summary>
    public void InjectField(string iId, string iValue) => m_Renderer.Fields[iId] = iValue;
    public void InjectToggle(string iId, bool iOn) => m_Renderer.Toggles[iId] = iOn;
    public void InjectFold(string iId, bool iOpen) => m_Renderer.Folds[iId] = iOpen;
    public bool TryGetToggle(string iId, out bool oOn) => m_Renderer.Toggles.TryGetValue(iId, out oOn);
    public bool TryGetFold(string iId, out bool oOpen) => m_Renderer.Folds.TryGetValue(iId, out oOpen);

    /// <summary>截圖出口 —— 常駐模式下由宿主隨時呼叫（--screenshot 那條路是開窗時就決定的，不共用）。</summary>
    public void CaptureTo(string iPath)
    {
        if (m_Gl == null || m_Window == null) return;
        SenateScreenshot.Capture(m_Gl, m_Window.FramebufferSize.X, m_Window.FramebufferSize.Y, iPath);
    }

    // ── 常駐 fps（永遠在量，不必開 soak）───────────────────────────────
    // 🩸 為什麼要兩個讀數：累積值會被開窗那幾秒稀釋 ——
    //   一顆開了兩小時的窗剛剛凍了 3 秒，累積 fps 幾乎不動，**而那 3 秒正是要抓的東西**。
    //   ⇒ 只印一個的話，「一直很順」與「剛剛凍了一下」在讀數上同形。
    System.Diagnostics.Stopwatch? m_LiveClock;
    int m_LiveFrames;
    double m_LiveWorstMs;
    double m_LiveFirstFrameMs;
    int m_RecentFrames;
    double m_RecentWorstMs;
    double m_RecentStartSec;
    string m_FpsRecent = "（還沒滿一個取樣窗）";

    /// <summary>最近一個取樣窗的長度（秒）。短到抓得到一次凍結，長到不會被單幀抖動洗掉。</summary>
    public const double RecentWindowSeconds = 5.0;

    /// <summary>自開窗以來的 fps 讀數（第一幀分開印，理由同 soak）。</summary>
    public string FpsTotal
    {
        get
        {
            if (m_LiveClock is not { } aClock || m_LiveFrames <= 0) return "（還沒有幀）";
            double aSec = aClock.Elapsed.TotalSeconds;
            if (aSec <= 0) return "（還沒有幀）";
            return $"{m_LiveFrames} 幀 / {aSec:0.0} 秒 ⇒ 平均 {m_LiveFrames / aSec:0.0} fps"
                 + $"，第一幀 {m_LiveFirstFrameMs:0.0} ms"
                 + (m_LiveFrames > 1 ? $"，其餘最慢 {m_LiveWorstMs:0.0} ms" : "，其餘：沒有第二幀");
        }
    }

    /// <summary>最近 <see cref="RecentWindowSeconds"/> 秒的 fps 讀數。</summary>
    public string FpsRecent => m_FpsRecent;

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

    /// <summary>
    /// 視窗／工作列那顆 icon 的安裝讀數。
    /// <para>⚠ 跟 <see cref="ClipboardStatus"/> 同一個規矩：沒設到的症狀是「看起來就是預設圖示」，
    /// 而那跟「這台系統圖示快取沒更新」長得一樣 —— 兩者要分得出來，所以這裡回的是一句話不是 bool。</para>
    /// </summary>
    public string WindowIconStatus { get; private set; } = "(尚未設定)";

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

        // 視窗 icon —— 必須在窗開好之後（那時才有 HWND）。
        // 🩸 GLFW 撈的是名為 GLFW_ICON 的資源，apphost 埋的是數字 ID 32512 ⇒ 名字對不上，
        //    它會安靜地退回系統預設。詳見 SenateWindowIcon。
        WindowIconStatus = SenateWindowIcon.Apply(m_Window);
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
        // ⭐ 版面診斷：外層**不該**有捲動空間（TopBar 釘住的前提）。
        //   ⚠ 這一格是讀數不是保證 —— 它 > 0 就表示有東西把外層撐高了，而那時 TopBar 會被滾走。
        ImGui.TextUnformatted(
            $"  外層 ScrollY = {ImGui.GetScrollY():0.##} ／ 上限 ScrollMaxY = {ImGui.GetScrollMaxY():0.##}"
            + "　← **不是 0 就表示外層仍會捲**（TopBar 會被滾出畫面）");
        ImGui.TextUnformatted(
            $"  自我對拍（不需按鍵）：注入 ModCtrl=true 之後 io.KeyCtrl 讀回 = {m_ProbeKeyCtrl}"
            + "　← 這格是 False 就是我補的那條路沒生效");
        ImGui.TextUnformatted(
            "  請你按：先點進「repo 路徑」欄位 → 按住 Ctrl 看 io.KeyCtrl 是否變 True → 按 Ctrl+V 看 Get 是否 +1。"
            + " Get 不動 ⇒ ImGui 沒把組合鍵交給 InputText；Get 有動但沒字 ⇒ 剪貼簿是空的。");
    }

    /// <summary>開了就在畫面底部畫一行鍵盤／剪貼簿診斷（`ui --window --keydebug`）。</summary>
    public bool KeyDebug { get; set; }

    /// <summary>
    /// **捲動探針**：開頭這幾幀每幀強制叫**內容子區域**（`scp/content`）往下捲 200px。
    /// <para>🩸 為什麼需要它（2026-09-17）：「TopBar 釘住了沒」是一個**行為**，
    /// 而我連兩版都只驗了結構（頂欄畫在子區域外面）就宣告修好，兩次都被 Tim 用滾輪推翻。
    /// ⇒ 唯一誠實的驗法是**真的叫它捲一次**再看畫面。</para>
    /// <para>🩸 TASK-0236：而第三版的探針**叫錯了容器** —— 它對**外層視窗**呼叫 `SetScrollY`，
    /// 而外層是 `NoScrollbar | NoScrollWithMouse` ⇒ `ScrollMaxY == 0` ⇒ 被夾回 0、
    /// 畫面完全不動，於是它印的 `ScrollY=0 / ScrollMaxY=0` **看起來正是「釘住了」**。
    /// ⇒ 三次誤判的共同形狀：**探針從來沒有碰到真正會捲的那個容器。**
    /// 現在它打的是 <see cref="GuiImGuiRenderer.ContentScrollProbePx"/>，
    /// 而那一格長在 renderer 裡 —— 只有那裡知道會捲的子區域在哪。</para>
    /// <para>⚠ 射程：它驗「那個容器捲不捲得動」，⛔ 不是「滾輪事件會不會被吃掉」——
    /// 我第一版注入 `io.MouseWheel`，而那一格在 `NewFrame` 就被消化了（灌太晚），那個探針**沒有生效**。</para>
    /// <para>用法：`senate ui --page <頁> --window --scroll-probe 6 --screenshot <png>` ——
    /// 收工時會印一行 `scp/content: ScrollY=… / ScrollMaxY=…`（⭐ **可複驗的讀數**，
    /// ⛔ 不是「看起來還在」）；截圖裡頂欄還在 ＝ 釘住了；不見了 ＝ 沒釘住。</para>
    /// </summary>
    public int ScrollProbeFrames { get; set; }

    /// <summary>
    /// 反向對照：開了就**當作沒有任何節點是釘住的** ⇒ TopBar 應該跟著內容被捲走。
    /// <para>⚠ 它是驗收條件的一半，不是除錯殘留：**沒有紅過的守衛等於沒有讀數**。</para>
    /// </summary>
    public bool IgnorePinned { get; set; }

    void OnRender(double iDelta)
    {
        m_Frame++;

        // soak 的讀數在這裡累積。⚠ 第一幀不計最慢值：那一幀含 font atlas 與版位量測，
        //   它一定是全場最慢的，計進去會讓「最慢一幀」永遠回答同一件事（＝沒有回答任何事）。
        if (SoakSeconds > 0)
        {
            m_SoakClock ??= System.Diagnostics.Stopwatch.StartNew();
            if (m_Frame > 1) m_SoakWorstFrameMs = Math.Max(m_SoakWorstFrameMs, iDelta * 1000.0);
        }

        // 常駐 fps：⭐ **永遠在量**，不必開 soak（TASK-0214 ④）——
        //   這顆窗就是大家日常在用的那顆，所以它的 fps 才是「實際流程」的讀數。
        //   ⚠ 成本＝幾個數的累加，⛔ 沒有 IO、沒有字串（字串只在換取樣窗時做一次）。
        m_LiveClock ??= System.Diagnostics.Stopwatch.StartNew();
        m_LiveFrames++;
        m_RecentFrames++;
        double aNowSec = m_LiveClock.Elapsed.TotalSeconds;
        // 🩸 第一幀的長度**要在幀尾量**（見 OnRender 末段）——
        //   第一版我在這裡（幀首）讀 Elapsed，而碼錶就是這一幀剛起的 ⇒ 永遠印 0.0 ms。
        //   ⚠ 那正是 D21 那條血證的形狀：**一個全綠的數字在描述一個還沒被量的東西**，
        //   而它跟「第一幀真的很快」印出來一模一樣。活體抓到的（2026-09-15 kiara）。
        if (m_Frame > 1)
        {
            double aMs = iDelta * 1000.0;
            m_LiveWorstMs = Math.Max(m_LiveWorstMs, aMs);
            m_RecentWorstMs = Math.Max(m_RecentWorstMs, aMs);
        }
        if (aNowSec - m_RecentStartSec >= RecentWindowSeconds)
        {
            double aSpan = aNowSec - m_RecentStartSec;
            m_FpsRecent = $"{m_RecentFrames} 幀 / {aSpan:0.0} 秒 ⇒ 平均 {m_RecentFrames / aSpan:0.0} fps"
                        + $"，最慢一幀 {m_RecentWorstMs:0.0} ms";
            m_RecentStartSec = aNowSec;
            m_RecentFrames = 0;
            m_RecentWorstMs = 0;
        }

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
        // ⚠ **外層視窗自己不捲**（`NoScrollbar | NoScrollWithMouse`）—— 捲動一律發生在
        //   renderer 開的那個內容子區域裡。
        // 🩸 2026-09-17：只把內容包進 child、外層沒關捲動 ⇒ 滾輪滾的是**外層**，
        //   於是釘住的 TopBar 照樣被滾出畫面（Tim 實測回報）。
        //   ⇒ 「內容在一個會捲的子區域裡」**不蘊含**「外層不會捲」，那是兩個獨立的開關。
        ImGui.Begin(m_Title,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

        // ⭐ 捲動探針：交給 renderer 去叫**內容子區域**捲（`scp/content`）。
        // 🩸 TASK-0236：這裡原本寫的是 `ImGui.SetScrollY(...)`，而此刻的「當前視窗」是**外層**，
        //   外層帶著 `NoScrollbar | NoScrollWithMouse` ⇒ `ScrollMaxY == 0` ⇒ **被夾回 0**。
        //   於是探針每次都印「捲不動」，而那**跟「釘住了」長得一模一樣** —— 我三次驗收全栽在這裡。
        //   ⇒ 受測體必須是**真的會捲的那個容器**，而它在 renderer 手上，不在這裡。
        // ⚠ 它是「症狀的探針」不是「輸入路徑的探針」（⛔ 不可以拿它宣稱「滾輪沒問題」）：
        //   我第一版灌 `io.MouseWheel`，那一格在 `NewFrame` 就被消化掉了（灌太晚）⇒ 沒生效。
        m_Renderer.ContentScrollProbePx =
            ScrollProbeFrames > 0 && m_Frame <= ScrollProbeFrames ? 200f : 0f;
        m_Renderer.IgnorePinned = IgnorePinned;

        // ⚠ keydebug **畫在內容之前**（＝跟著釘住的那一段一起留在畫面上）。
        // 🩸 2026-09-17：外層視窗關掉捲動之後，內容子區域吃掉所有剩餘高度
        //   ⇒ 任何畫在 `Render` 之後的東西都被推到畫面外，而外層又捲不動 ⇒ **永遠看不到**。
        //   ⛔ 而那種失效不會報錯：它只是不見了。
        if (KeyDebug) DrawKeyDebug();

        SCP_Ui aUi = m_Draw(m_Renderer.TakeInput());
        m_Renderer.Render(aUi.Root);
        // 頁面要求的欄位寫入在**畫完之後**才套 —— 這一幀顯示的是頁面自己算出來的結果，
        // 套進 renderer 是為了下一幀（跟按鈕事件同一個「慢一幀」的節奏）。
        m_Renderer.ApplyWrites(aUi);

        // ⭐ **外層視窗自己的捲動讀數** —— 跟子區域那一格並排印，⛔ 不是二選一。
        // 🩸 TASK-0236 第四次：我只印了子區域（`scp/content`），量到它捲得動、頂欄沒動，
        //   就宣告修好了 —— 而 Tim 用滾輪照樣把頂欄推出畫面。
        //   「子區域會捲」與「外層不會捲」是**兩個獨立的命題**，我只量了第一個。
        //   ⇒ 兩個容器都要有讀數；只印一個的話，另一個出事時畫面上不會有任何痕跡。
        m_OuterScrollY = ImGui.GetScrollY();
        m_OuterScrollMaxY = ImGui.GetScrollMaxY();
        m_OuterContentH = ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y;
        m_OuterWindowH = ImGui.GetWindowSize().Y;

        ImGui.End();
        m_Controller.Render();

        // ⭐ 常駐窗對外的唯一出口（TASK-0214 ②③）：這一幀的樹交給宿主。
        // 🩸 **位置是 `m_Controller.Render()` 之後，不是之前** —— 第一版我放在 ApplyWrites 旁邊，
        //   那裡 ImGui 還沒把這一幀畫進 framebuffer ⇒ 宿主在那裡截圖，拍到的是**上一幀之前**的畫面。
        //   ⚠ 而失效的樣子是：截圖回 `✓ 已落檔`、檔案大小正常、圖看起來就是一張正常的截圖 ——
        //   抓到它的不是眼睛，是驗收 ③ 那句「操作前後兩張要**不同**」：
        //   下拉明明開了（--list 從 0 個 pick 變 3 個），兩張圖的 md5 **一字不差**。
        //   ⇒ 成功與沒做同形，而這正是本張單存在的理由。（活體：kiara 2026-09-15）
        //   📌 所以它跟下面那段截圖走**同一個位置**，不是巧合：能拍到的地方才是能交出去的地方。
        OnFrameServed?.Invoke(aUi.Root);

        // ⚠ soak 開著時，收工的判準從「幀數」換成「時間」——
        //   兩個判準同時成立會讓視窗在第 8 幀就關掉，而那正是 soak 要避免的事。
        if (SoakSeconds > 0 && m_Frame == 1 && m_SoakClock != null)
            m_SoakFirstFrameMs = m_SoakClock.Elapsed.TotalMilliseconds;

        // 常駐 fps 的第一幀，同一個道理、同一個位置（幀尾才量得到這一幀有多長）。
        if (m_Frame == 1 && m_LiveClock != null)
            m_LiveFirstFrameMs = m_LiveClock.Elapsed.TotalMilliseconds;

        // ⚠ **先判「這個模式會不會自己收工」，再判「收工了沒」。** 只有兩種模式會自己關窗：
        //   `--screenshot`（拍完關）與 `--soak`（轉完關）。互動模式兩者皆無 ⇒ 永遠不從這裡關窗。
        // 🩸 2026-09-03 我把收工判準寫成 `SoakSeconds > 0 ? 時間到 : m_Frame >= 8`，
        //   互動模式落到後者、第 8 幀就成立 ⇒ **開窗約 0.1 秒自己關掉**（Tim 回報）。
        //   而我那一輪的每一個讀數都走 `--screenshot` 或 `--soak` —— **兩個都是會關窗的模式**，
        //   所以這隻在我選的受測體上一次都不會現形。受測體必須涵蓋「不會關窗」那一種。
        bool aSelfClosing = m_ScreenshotPath != null || SoakSeconds > 0;
        if (!aSelfClosing) return;

        bool aDone = SoakSeconds > 0
            ? m_SoakClock is { } aClock && aClock.Elapsed.TotalSeconds >= SoakSeconds
            : m_Frame >= m_ScreenshotAtFrame;

        if (!aDone) return;

        if (SoakSeconds > 0 && m_SoakClock != null)
        {
            double aSec = m_SoakClock.Elapsed.TotalSeconds;
            // 🩸 第一幀**分開印，不合併也不省略**：2026-09-03 我第一版把它併進「最慢一幀」的
            //   排除項（理由是 font atlas 會讓它必定最慢），而 submodule 頁的真症狀
            //   剛好整格住在第一幀（同步掃 git ⇒ 凍 8-10 秒，之後 0 幀）。
            //   ⇒ 排除掉之後讀數印的是「最慢一幀 0.0 ms」——**一個全綠的數字在描述一個凍住的視窗**。
            SoakReading = $"soak：{m_Frame} 幀 / {aSec:0.00} 秒 ⇒ 平均 {m_Frame / aSec:0.0} fps"
                + $"，第一幀 {m_SoakFirstFrameMs:0.0} ms"
                + (m_Frame > 1 ? $"，其餘最慢 {m_SoakWorstFrameMs:0.0} ms" : "，其餘：沒有第二幀");
        }

        // ⭐ TASK-0236 ④：把「誰在捲／捲了多少」留成一行可複驗的讀數。
        // ⚠ 取在**關窗前的最後一幀** —— 探針是跨幀累積的，中途任何一幀都還沒捲到底。
        if (ScrollProbeFrames > 0)
            ScrollReading = $"scroll-probe：{m_Renderer.LastContentScrollReading ?? "（子區域這一幀沒有被畫出來 —— 這是「沒量到」，不是「捲不動」）"}"
                + $"\n　　　　外層視窗：ScrollY={m_OuterScrollY:0.#} / ScrollMaxY={m_OuterScrollMaxY:0.#}"
                + $"（內容高 {m_OuterContentH:0.#} / 視窗高 {m_OuterWindowH:0.#}）"
                + (m_OuterScrollMaxY > 0.5f ? "　⛔ **外層有捲動範圍 —— 頂欄會被推出畫面**" : "　✅ 外層釘死")
                + $"\n　　　　pinned={(IgnorePinned ? "ignored（反向對照）" : "on")}｜probe {ScrollProbeFrames} 幀 × 200px";

        if (m_ScreenshotPath != null)
            SenateScreenshot.Capture(m_Gl, m_Window!.FramebufferSize.X, m_Window.FramebufferSize.Y, m_ScreenshotPath);

        // soak 沒帶截圖路徑時照樣要關窗 —— 否則 --soak 單獨用會卡成互動模式。
        m_Window!.Close();
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
