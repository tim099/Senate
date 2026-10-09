// 區塊職責：球面 GPU 的**離屏宿主**（TASK-0470）—— 沒有視窗的行程（CLI selftest）用隱藏 GLFW 視窗建一顆 GL 3.3 Core，
//          跑跟視窗預覽**同一份** SenateGlobeGl，畫進 FBO 再讀回。給對拍與讀數用，⛔ 不是預覽的路（預覽在視窗 context 裡、不讀回）。
// 物理意義：context 建立與失敗的處理照 SenateSculptRenderer：第一次呼叫才建；建不出來 ⇒ 回 false 與原因，之後不重試。
//          每次呼叫 MakeCurrent、結束時 Clear，以 lock 序列化。
// 數值影響：讀回的列已經是由上到下（見 SenateGlobeGl 檔頭），⛔ 不再翻。
#nullable enable
using SCP.Core.Globe;
using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Senate.Desktop;

public sealed class SenateGlobeOffscreen : IDisposable
{
    readonly object m_Lock = new();
    IWindow? m_Window;
    IGLContext? m_Context;
    GL? m_Gl;
    SenateGlobeGl? m_Globe;
    string? m_InitError;
    uint m_Fbo, m_Color, m_Depth;
    int m_W, m_H;

    public string GlInfo => m_Globe?.GlInfo ?? "";
    /// <summary>建不出 context／shader 的原因（null ＝ 還沒試或成功）。⚠ 只有這種失敗算「這台沒有 GPU」，渲染途中的 GL 錯誤是 bug。</summary>
    public string? InitError => m_InitError;
    public SenateGlobeGl.SyncReading LastSync => m_Globe?.LastSync ?? new SenateGlobeGl.SyncReading();

    /// <summary>畫一張並讀回 RGBA（由上到下）。</summary>
    public bool TryRender(SCP_GlobeGpuScene iScene, SenateGlobeGl.Mode iMode, out byte[] oRgba, out string oError, bool iTamperFace0 = false)
    {
        oRgba = Array.Empty<byte>(); oError = "";
        lock (m_Lock)
        {
            if (!EnsureContext(out oError)) return false;
            try
            {
                m_Context!.MakeCurrent();
                int w = iScene.Camera.Width, h = iScene.Camera.Height;
                EnsureTarget(w, h);
                var gl = m_Gl!;
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, m_Fbo);
                gl.Viewport(0, 0, (uint)w, (uint)h);
                m_Globe!.Draw(iScene, iMode, iTamperFace0);
                oRgba = new byte[w * h * 4];
                gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
                unsafe { fixed (byte* p = oRgba) gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p); }
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                GLEnum e = gl.GetError();
                if (e != GLEnum.NoError) { oError = "OpenGL 錯誤：" + e; oRgba = Array.Empty<byte>(); return false; }
                return true;
            }
            catch (Exception e) { oError = "球面 GPU 渲染失敗（" + e.GetType().Name + "）：" + e.Message; return false; }
            finally { try { m_Context?.Clear(); } catch (Exception) { } }
        }
    }

    bool EnsureContext(out string oError)
    {
        oError = "";
        if (m_Globe != null) return true;
        if (m_InitError != null) { oError = m_InitError; return false; }
        try
        {
            var o = WindowOptions.Default;
            o.Size = new Vector2D<int>(16, 16);
            o.Title = "senate-globe-offscreen";
            o.IsVisible = false;
            o.VSync = false;
            o.ShouldSwapAutomatically = false;
            o.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3));
            m_Window = Window.Create(o);
            m_Window.Initialize();
            m_Context = m_Window.GLContext ?? throw new InvalidOperationException("視窗沒有 GL context");
            m_Context.MakeCurrent();
            m_Gl = GL.GetApi(m_Context);
            m_Globe = new SenateGlobeGl(m_Gl);
            m_Context.Clear();
            return true;
        }
        catch (Exception e)
        {
            m_InitError = "建不出 OpenGL 3.3 context 或球面 shader（" + e.GetType().Name + "：" + e.Message + "）";
            try { m_Globe?.Dispose(); m_Gl?.Dispose(); m_Window?.Dispose(); } catch (Exception) { }
            m_Globe = null; m_Gl = null; m_Window = null; m_Context = null;
            oError = m_InitError;
            return false;
        }
    }

    unsafe void EnsureTarget(int w, int h)
    {
        if (m_Fbo != 0 && m_W == w && m_H == h) return;
        var gl = m_Gl!;
        if (m_Fbo != 0) { gl.DeleteFramebuffer(m_Fbo); gl.DeleteRenderbuffer(m_Color); gl.DeleteRenderbuffer(m_Depth); }
        m_Color = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, m_Color);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)w, (uint)h);
        m_Depth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, m_Depth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)w, (uint)h);
        m_Fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, m_Fbo);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, m_Color);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, m_Depth);
        GLEnum s = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (s != GLEnum.FramebufferComplete) throw new InvalidOperationException("球面離屏 FBO 不完整：" + s);
        m_W = w; m_H = h;
    }

    public void Dispose()
    {
        lock (m_Lock)
        {
            if (m_Gl != null && m_Context != null)
            {
                try
                {
                    m_Context.MakeCurrent();
                    m_Globe?.Dispose();
                    if (m_Fbo != 0) { m_Gl.DeleteFramebuffer(m_Fbo); m_Gl.DeleteRenderbuffer(m_Color); m_Gl.DeleteRenderbuffer(m_Depth); }
                }
                catch (Exception) { }
            }
            try { m_Gl?.Dispose(); m_Window?.Dispose(); } catch (Exception) { }
            m_Globe = null; m_Gl = null; m_Window = null; m_Context = null;
        }
    }
}
