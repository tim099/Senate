// 區塊職責：視窗裡的 **GPU 即時畫面**（TASK-0470）—— 把 `gpu:` Image 節點的場景畫進主視窗 context 的 FBO 貼圖，交給 ImGui 直接顯示。
// 物理意義：⛔ 不讀回 CPU：場景版本變了才重畫進貼圖，沒變就沿用上一張（每幀零成本）；格子快取（SenateGlobeGl）常駐在 GPU 上。
//          畫的位置是 ImGui 建這一幀的樹的途中（ImGui 自己的繪製在 m_Controller.Render() 才發生）⇒ 我們動到的 GL 狀態
//          （FBO、viewport、program、VAO、貼圖、開關）畫完一律**還原成進來時的樣子** —— 漏還原一格的症狀是整個 UI 畫歪或全黑，而且不報錯。
// 數值影響：失敗（shader 編不過、顯卡上限、施工區超量…）回報給 SCP_GuiGpuViews，頁面讀得到原因並退回 CPU；同一個初始化錯誤不重試。
#nullable enable
using SCP.Core.Globe;
using SCP.Core.Gui;
using Silk.NET.OpenGL;

namespace Senate.Desktop;

public sealed class SenateGpuViews : IDisposable
{
    sealed class Target { public uint Fbo, Color, Depth; public int W, H; public long Version = -1; public string? Error; public bool FlipY; }

    readonly GL m_Gl;
    SenateGlobeGl? m_Globe;
    SenateSculptRenderer? m_Sculpt;   // TASK-0472：同一份雕刻渲染器，外部 GL 模式（網格快取在它身上）
    string? m_InitError;
    readonly Dictionary<string, Target> m_Targets = new(StringComparer.Ordinal);

    public SenateGpuViews(GL iGl) { m_Gl = iGl; }

    /// <summary>
    /// 畫（或沿用）這個 key 的貼圖。失敗 ⇒ false ＋原因（也已回報給登記處）。
    /// <paramref name="oFlipY"/>：貼圖是 GL 慣例（第 0 列在最下面）⇒ 顯示時要上下翻（雕刻）；球面 shader 自己就畫成由上到下 ⇒ false。
    /// </summary>
    public bool TryPaint(string iKey, out uint oTex, out int oW, out int oH, out bool oFlipY, out string oError)
    {
        oTex = 0; oW = oH = 0; oFlipY = false; oError = "";
        if (!SCP_GuiGpuViews.TryGet(iKey, out SCP_GuiGpuFrame aFrame)) { oError = "還沒有場景：" + iKey; return false; }
        if (!m_Targets.TryGetValue(iKey, out Target? t)) m_Targets[iKey] = t = new Target();
        oFlipY = t.FlipY;
        if (t.Version == aFrame.Version)
        {
            if (t.Error != null) { oError = t.Error; return false; }
            oTex = t.Color; oW = t.W; oH = t.H; return true;
        }
        t.Version = aFrame.Version;
        t.Error = null;
        var aSaved = SaveState();
        string aInfo = "";
        try
        {
            EnsureTarget(t, aFrame.Width, aFrame.Height);
            if (aFrame.Scene is SCP_GlobeGpuScene aGlobe)
            {
                if (m_InitError != null) throw new InvalidOperationException(m_InitError);
                try { m_Globe ??= new SenateGlobeGl(m_Gl); }
                catch (Exception e) { m_InitError = "球面 GPU 初始化失敗：" + e.Message; throw new InvalidOperationException(m_InitError); }
                m_Gl.BindFramebuffer(FramebufferTarget.Framebuffer, t.Fbo);
                m_Gl.Viewport(0, 0, (uint)t.W, (uint)t.H);
                m_Globe.Draw(aGlobe, SenateGlobeGl.Mode.Color);
                t.FlipY = false;
                aInfo = "GPU " + m_Globe.GlInfo + "｜格子同步 " + m_Globe.LastSync;
            }
            else if (aFrame.Scene is SCP.Core.Sculpture.SCP_SculptGpuScene aSculpt)
            {
                m_Sculpt ??= new SenateSculptRenderer(m_Gl);
                if (!m_Sculpt.TryRenderToFbo(aSculpt.Voxels, aSculpt.Params, t.Fbo, out string aWhy)) throw new InvalidOperationException(aWhy);
                t.FlipY = true;
                aInfo = m_Sculpt.Name + "｜" + m_Sculpt.LastReading;
            }
            else throw new InvalidOperationException("這個宿主不會畫 " + aFrame.Scene.GetType().Name);
            GLEnum aErr = m_Gl.GetError();
            if (aErr != GLEnum.NoError) throw new InvalidOperationException("OpenGL 錯誤：" + aErr);
            SCP_GuiGpuViews.Report(iKey, aFrame.Version, null, aInfo);
            oTex = t.Color; oW = t.W; oH = t.H; oFlipY = t.FlipY;
            return true;
        }
        catch (Exception e)
        {
            t.Error = oError = e.Message;
            SCP_GuiGpuViews.Report(iKey, aFrame.Version, oError, aInfo);
            return false;
        }
        finally { RestoreState(aSaved); }
    }

    unsafe void EnsureTarget(Target t, int iW, int iH)
    {
        if (t.Fbo != 0 && t.W == iW && t.H == iH) return;
        var gl = m_Gl;
        DeleteTarget(t);
        t.Color = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, t.Color);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)iW, (uint)iH, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        t.Depth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, t.Depth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)iW, (uint)iH);
        t.Fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, t.Fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, t.Color, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, t.Depth);
        GLEnum s = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (s != GLEnum.FramebufferComplete) throw new InvalidOperationException("球面 FBO 不完整：" + s);
        t.W = iW; t.H = iH;
    }

    void DeleteTarget(Target t)
    {
        if (t.Fbo != 0) m_Gl.DeleteFramebuffer(t.Fbo);
        if (t.Color != 0) m_Gl.DeleteTexture(t.Color);
        if (t.Depth != 0) m_Gl.DeleteRenderbuffer(t.Depth);
        t.Fbo = t.Color = t.Depth = 0;
    }

    // ── GL 狀態保存／還原 ──────────────────────────────
    sealed class Saved
    {
        public int DrawFbo, ReadFbo, Program, Vao, ActiveTex, Tex0, Tex1, Tex2, TexArr0, Rb, Unpack, DepthFunc;
        // 雕刻渲染器還會動這些（TASK-0472）：深度寫入、剔除哪一面、混色函式／方程式
        public bool DepthMask;
        public int CullMode, BlendSrcRgb, BlendDstRgb, BlendSrcA, BlendDstA, BlendEqRgb, BlendEqA, Pack;
        public readonly int[] Viewport = new int[4];
        public readonly float[] Clear = new float[4];
        public float ClearDepth;
        public bool Blend, Depth, Scissor, Cull;
    }

    Saved SaveState()
    {
        var gl = m_Gl;
        var s = new Saved
        {
            DrawFbo = gl.GetInteger(GetPName.DrawFramebufferBinding),
            ReadFbo = gl.GetInteger(GetPName.ReadFramebufferBinding),
            Program = gl.GetInteger(GetPName.CurrentProgram),
            Vao = gl.GetInteger(GetPName.VertexArrayBinding),
            ActiveTex = gl.GetInteger(GetPName.ActiveTexture),
            Rb = gl.GetInteger(GetPName.RenderbufferBinding),
            Blend = gl.IsEnabled(EnableCap.Blend), Depth = gl.IsEnabled(EnableCap.DepthTest),
            Scissor = gl.IsEnabled(EnableCap.ScissorTest), Cull = gl.IsEnabled(EnableCap.CullFace),
        };
        gl.GetInteger(GetPName.Viewport, s.Viewport);
        gl.GetFloat(GetPName.ColorClearValue, s.Clear);
        s.ClearDepth = gl.GetFloat(GetPName.DepthClearValue);
        s.Unpack = gl.GetInteger(GetPName.UnpackAlignment);
        s.DepthFunc = gl.GetInteger(GetPName.DepthFunc);
        s.DepthMask = gl.GetBoolean(GetPName.DepthWritemask);
        s.CullMode = gl.GetInteger(GetPName.CullFaceMode);
        s.BlendSrcRgb = gl.GetInteger(GetPName.BlendSrcRgb); s.BlendDstRgb = gl.GetInteger(GetPName.BlendDstRgb);
        s.BlendSrcA = gl.GetInteger(GetPName.BlendSrcAlpha); s.BlendDstA = gl.GetInteger(GetPName.BlendDstAlpha);
        s.BlendEqRgb = gl.GetInteger(GetPName.BlendEquationRgb); s.BlendEqA = gl.GetInteger(GetPName.BlendEquationAlpha);
        s.Pack = gl.GetInteger(GetPName.PackAlignment);
        gl.ActiveTexture(TextureUnit.Texture0);
        s.Tex0 = gl.GetInteger(GetPName.TextureBinding2D);
        s.TexArr0 = gl.GetInteger(GetPName.TextureBinding2DArray);
        gl.ActiveTexture(TextureUnit.Texture1);
        s.Tex1 = gl.GetInteger(GetPName.TextureBinding2D);
        gl.ActiveTexture(TextureUnit.Texture2);
        s.Tex2 = gl.GetInteger(GetPName.TextureBinding2D);
        gl.ActiveTexture((TextureUnit)s.ActiveTex);
        return s;
    }

    void RestoreState(Saved s)
    {
        var gl = m_Gl;
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)s.DrawFbo);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)s.ReadFbo);
        gl.Viewport(s.Viewport[0], s.Viewport[1], (uint)s.Viewport[2], (uint)s.Viewport[3]);
        gl.ClearColor(s.Clear[0], s.Clear[1], s.Clear[2], s.Clear[3]);
        gl.ClearDepth(s.ClearDepth);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, s.Unpack);
        gl.DepthFunc((DepthFunction)s.DepthFunc);
        gl.UseProgram((uint)s.Program);
        gl.BindVertexArray((uint)s.Vao);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, (uint)s.Rb);
        gl.DepthMask(s.DepthMask);
        gl.CullFace((TriangleFace)s.CullMode);
        gl.BlendFuncSeparate((BlendingFactor)s.BlendSrcRgb, (BlendingFactor)s.BlendDstRgb, (BlendingFactor)s.BlendSrcA, (BlendingFactor)s.BlendDstA);
        gl.BlendEquationSeparate((BlendEquationModeEXT)s.BlendEqRgb, (BlendEquationModeEXT)s.BlendEqA);
        gl.PixelStore(PixelStoreParameter.PackAlignment, s.Pack);
        gl.ActiveTexture(TextureUnit.Texture2);
        gl.BindTexture(TextureTarget.Texture2D, (uint)s.Tex2);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, (uint)s.Tex1);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, (uint)s.Tex0);
        gl.BindTexture(TextureTarget.Texture2DArray, (uint)s.TexArr0);
        gl.ActiveTexture((TextureUnit)s.ActiveTex);
        Set(EnableCap.Blend, s.Blend); Set(EnableCap.DepthTest, s.Depth);
        Set(EnableCap.ScissorTest, s.Scissor); Set(EnableCap.CullFace, s.Cull);
    }

    void Set(EnableCap c, bool on) { if (on) m_Gl.Enable(c); else m_Gl.Disable(c); }

    public void Dispose()
    {
        foreach (Target t in m_Targets.Values) DeleteTarget(t);
        m_Targets.Clear();
        m_Globe?.Dispose();
        m_Globe = null;
        m_Sculpt?.Dispose();
        m_Sculpt = null;
    }
}
