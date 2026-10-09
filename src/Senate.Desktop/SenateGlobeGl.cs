// 區塊職責：球面的 **GPU 核心**（OpenGL 3.3 Core，TASK-0470）—— 格子快取在 GPU 上（分塊圖集＋索引貼圖），
//          全畫面三角形＋fragment shader 逐像素「射線 × 單位球 → 方向 → 格子」，並寫入深度（留給之後的建築）。
// 物理意義：本類別**不建 context**：呼叫端給一個 current 的 GL（視窗那顆 → 即時預覽；隱藏視窗那顆 → selftest 對拍）。
//          ・格子：SCP_GlobeCells 本來就是 256²（N 小於 256 時是 N²）分塊、只存畫過的 ⇒ GPU 照抄這個形狀：
//            每塊佔圖集（2D 陣列貼圖，每層 16×16 塊）一格，索引貼圖（R32I，tps × 6·tps）記「這一塊在圖集第幾格，−1 ＝ 沒畫過」。
//            同步時逐塊比對內容，**只上傳內容變了的那幾塊**（讀數見 <see cref="LastSync"/>）；N／分塊大小／面基底變了才整份重建。
//          ・鏡頭與取格公式是 <see cref="SCP_GlobeCamera"/>／<see cref="SCP_GlobeGrid"/> 的 float 翻譯 —— ⛔ 不是另一個真相：
//            面基底每次從 meta（State.Grid.Faces）傳成 uniform，不在 GLSL 寫死（兩份基底不一致 ⇒ 接縫錯位／鏡像，而且不會報錯）。
//          ・兩種輸出：Color（預覽）與 Index（每個像素的格子索引拆成 RGBA 四個位元組；selftest 拿它跟 CPU 參考解逐像素比）。
// 數值影響：float 在格子邊界上可能差一格（CPU 是 double）—— 那是對拍要量的東西，比例由 selftest 印出與設上限。
//          像素列由上到下 ＝ gl_FragCoord.y 由下到上 ⇒ ReadPixels 拿到的第 0 列就是畫面最上面那列（與 CPU 陣列同序），
//          ImGui 用 uv (0,0)-(1,1) 顯示這張貼圖時，畫面也是正的（貼圖第 0 列畫在上面）。
#nullable enable
using SCP.Core.Globe;
using Silk.NET.OpenGL;

namespace Senate.Desktop;

public sealed class SenateGlobeGl : IDisposable
{
    public enum Mode { Color = 0, Index = 1 }

    /// <summary>一次同步的讀數。</summary>
    public sealed class SyncReading
    {
        public bool Rebuilt;
        public int TilesOnGpu, Uploaded, Removed;
        public long UploadedBytes;
        public double Ms;
        public override string ToString() =>
            $"{(Rebuilt ? "整份重建" : "增量")}：GPU 上 {TilesOnGpu} 塊，這次上傳 {Uploaded} 塊（{UploadedBytes / 1024.0:0.#} KB）、移除 {Removed} 塊，{Ms:0.#} ms";
    }

    const int TilesPerLayerSide = 16;
    const int TilesPerLayer = TilesPerLayerSide * TilesPerLayerSide;

    readonly GL m_Gl;
    readonly uint m_Prog, m_Vao;
    readonly Dictionary<string, int> m_Uni = new(StringComparer.Ordinal);

    // ── GPU 上的格子快取 ────────────────────────────────
    uint m_Atlas, m_IndexTex;
    int m_AtlasLayers;
    int m_N, m_Tile, m_Tps;
    string m_FacesSig = "";
    readonly Dictionary<int, int> m_SlotOf = new();
    readonly Dictionary<int, byte[]> m_Shadow = new();   // GPU 上那一份的位元組副本（比對用）
    readonly Stack<int> m_FreeSlots = new();
    int m_NextSlot;
    int[] m_IndexData = Array.Empty<int>();
    object? m_SyncedState;
    int m_SyncedVersion = int.MinValue;

    public SyncReading LastSync { get; private set; } = new();
    public string GlInfo { get; }

    public SenateGlobeGl(GL iGl)
    {
        m_Gl = iGl;
        GlInfo = (iGl.GetStringS(StringName.Version) ?? "?") + "｜" + (iGl.GetStringS(StringName.Renderer) ?? "?");
        m_Prog = Link(Vs, Fs);
        m_Vao = iGl.GenVertexArray();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 同步：State → GPU（只上傳內容變了的分塊）
    // ════════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 把格子同步到 GPU。同一個 State 物件＋同一個版本 ⇒ 什麼都不做（每幀呼叫零成本）。
    /// </summary>
    public unsafe SyncReading Sync(SCP_GlobeState iState, int iVersion)
    {
        if (ReferenceEquals(m_SyncedState, iState) && m_SyncedVersion == iVersion) return LastSync;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new SyncReading();
        SCP_GlobeCells c = iState.Cells;
        string aSig = FacesSig(iState.Grid);
        if (c.N != m_N || c.Tile != m_Tile || aSig != m_FacesSig || m_IndexTex == 0)
        {
            ResetCache(c.N, c.Tile, aSig);
            r.Rebuilt = true;
        }

        var aLive = new HashSet<int>(c.TileKeys);
        bool aIndexDirty = r.Rebuilt;
        // 移除：GPU 上有、State 沒有的（例：重播後某塊消失）
        foreach (int k in m_Shadow.Keys.Where(k => !aLive.Contains(k)).ToList())
        {
            int s = m_SlotOf[k];
            m_SlotOf.Remove(k); m_Shadow.Remove(k); m_FreeSlots.Push(s);
            m_IndexData[k] = -1;
            r.Removed++; aIndexDirty = true;
        }
        int aTileBytes = m_Tile * m_Tile * 3;
        foreach (int k in aLive)
        {
            byte[] b = c.TileBytes(k);
            if (m_Shadow.TryGetValue(k, out byte[]? old) && old.AsSpan().SequenceEqual(b)) continue;
            if (!m_SlotOf.TryGetValue(k, out int s))
            {
                s = m_FreeSlots.Count > 0 ? m_FreeSlots.Pop() : m_NextSlot++;
                EnsureLayers(s / TilesPerLayer + 1);
                m_SlotOf[k] = s;
                m_IndexData[k] = s;
                aIndexDirty = true;
            }
            UploadTile(s, b);
            m_Shadow[k] = (byte[])b.Clone();
            r.Uploaded++; r.UploadedBytes += aTileBytes;
        }
        if (aIndexDirty) UploadIndex();
        r.TilesOnGpu = m_Shadow.Count;
        m_SyncedState = iState;
        m_SyncedVersion = iVersion;
        r.Ms = sw.Elapsed.TotalMilliseconds;
        LastSync = r;
        return r;
    }

    static string FacesSig(SCP_GlobeGrid g) =>
        string.Join(";", g.Faces.Select(f => string.Join(",", f.Normal) + "|" + string.Join(",", f.U) + "|" + string.Join(",", f.V)));

    unsafe void ResetCache(int iN, int iTile, string iFacesSig)
    {
        var gl = m_Gl;
        if (m_Atlas != 0) gl.DeleteTexture(m_Atlas);
        if (m_IndexTex != 0) gl.DeleteTexture(m_IndexTex);
        m_Atlas = 0; m_AtlasLayers = 0;
        m_N = iN; m_Tile = iTile; m_Tps = iN / iTile; m_FacesSig = iFacesSig;
        m_SlotOf.Clear(); m_Shadow.Clear(); m_FreeSlots.Clear(); m_NextSlot = 0;
        m_IndexData = new int[6 * m_Tps * m_Tps];
        Array.Fill(m_IndexData, -1);

        int aMaxTex = gl.GetInteger(GetPName.MaxTextureSize);
        if (6 * m_Tps > aMaxTex || TilesPerLayerSide * iTile > aMaxTex)
            throw new InvalidOperationException($"這張顯卡的貼圖上限 {aMaxTex} 放不下 N={iN} 的分塊圖集／索引");
        m_IndexTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, m_IndexTex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R32i, (uint)m_Tps, (uint)(6 * m_Tps), 0, PixelFormat.RedInteger, PixelType.Int, null);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>圖集層數不夠 ⇒ 加倍重建，並把 GPU 上已有的分塊從副本重新上傳（舊貼圖直接丟）。</summary>
    unsafe void EnsureLayers(int iNeed)
    {
        if (iNeed <= m_AtlasLayers && m_Atlas != 0) return;
        var gl = m_Gl;
        int aMaxLayers = gl.GetInteger(GetPName.MaxArrayTextureLayers);
        int aLayers = Math.Max(1, m_AtlasLayers);
        while (aLayers < iNeed) aLayers *= 2;
        aLayers = Math.Min(aLayers, aMaxLayers);
        if (aLayers < iNeed) throw new InvalidOperationException($"分塊圖集要 {iNeed} 層，超過這張顯卡的上限 {aMaxLayers}");
        if (m_Atlas != 0) gl.DeleteTexture(m_Atlas);
        int aSide = TilesPerLayerSide * m_Tile;
        m_Atlas = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2DArray, m_Atlas);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgb8, (uint)aSide, (uint)aSide, (uint)aLayers, 0, PixelFormat.Rgb, PixelType.UnsignedByte, null);
        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        m_AtlasLayers = aLayers;
        foreach (var kv in m_Shadow) UploadTile(m_SlotOf[kv.Key], kv.Value);
    }

    unsafe void UploadTile(int iSlot, byte[] iBytes)
    {
        var gl = m_Gl;
        int aLayer = iSlot / TilesPerLayer, aIn = iSlot % TilesPerLayer;
        int x = (aIn % TilesPerLayerSide) * m_Tile, y = (aIn / TilesPerLayerSide) * m_Tile;
        gl.BindTexture(TextureTarget.Texture2DArray, m_Atlas);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        // 分塊位元組：列 ＝ 面內 j（往上）、欄 ＝ 面內 i ⇒ 貼圖 (x＋i, y＋j)，shader 用同一個對應取
        fixed (byte* p = iBytes)
            gl.TexSubImage3D(TextureTarget.Texture2DArray, 0, x, y, aLayer, (uint)m_Tile, (uint)m_Tile, 1, PixelFormat.Rgb, PixelType.UnsignedByte, p);
        gl.BindTexture(TextureTarget.Texture2DArray, 0);
    }

    unsafe void UploadIndex()
    {
        var gl = m_Gl;
        gl.BindTexture(TextureTarget.Texture2D, m_IndexTex);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        fixed (int* p = m_IndexData)
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)m_Tps, (uint)(6 * m_Tps), PixelFormat.RedInteger, PixelType.Int, p);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 畫：呼叫端已綁好目標 FBO（含深度）並設好 viewport ＝ 鏡頭畫面大小
    // ════════════════════════════════════════════════════════════════════════════
    /// <param name="iTamperFace0">反向對照用：把第 0 面的 U 翻號再傳進 shader（對拍必須因此失敗）。</param>
    public void Draw(SCP_GlobeGpuScene iScene, Mode iMode, bool iTamperFace0 = false)
    {
        var gl = m_Gl;
        SCP_GlobeCamera cam = iScene.Camera;
        cam.Validate();
        if (iScene.Zones.Count > SCP_GlobeGpuScene.MaxZones)
            throw new InvalidOperationException($"施工區 {iScene.Zones.Count} 個，超過 GPU 預覽的上限 {SCP_GlobeGpuScene.MaxZones}（⛔ 不默默只畫前幾個）");
        Sync(iScene.State, iScene.StateVersion);

        gl.ClearColor(12 / 255f, 14 / 255f, 22 / 255f, 1f);
        if (iMode == Mode.Index) gl.ClearColor(1f, 1f, 1f, 1f);   // 沒打到球 ＝ 0xFFFFFFFF
        gl.ClearDepth(1.0);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.ScissorTest);

        gl.UseProgram(m_Prog);
        var e = new double[3]; var rt = new double[3]; var up = new double[3]; var fw = new double[3];
        cam.Basis(e, rt, up, fw);
        cam.NearFar(out double aNear, out double aFar);
        U3("uEye", e); U3("uRight", rt); U3("uUp", up); U3("uForward", fw);
        gl.Uniform1(Loc("uTanX"), (float)cam.TanHalfX);
        gl.Uniform1(Loc("uTanY"), (float)cam.TanHalfY);
        gl.Uniform2(Loc("uSize"), (float)cam.Width, (float)cam.Height);
        gl.Uniform1(Loc("uNear"), (float)aNear);
        gl.Uniform1(Loc("uFar"), (float)aFar);
        // 光：與 CPU ortho 同一式（從左上前方來）
        double lx = -0.8 * fw[0] - 0.35 * rt[0] + 0.5 * up[0], ly = -0.8 * fw[1] - 0.35 * rt[1] + 0.5 * up[1], lz = -0.8 * fw[2] - 0.35 * rt[2] + 0.5 * up[2];
        double ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
        gl.Uniform3(Loc("uLight"), (float)(lx / ll), (float)(ly / ll), (float)(lz / ll));

        SCP_GlobeGrid g = iScene.State.Grid;
        for (int f = 0; f < 6; f++)
        {
            SCP_GlobeFace face = g.Faces[f];
            // ⚠ 一律轉 float：基底是 List<int>，直接丟會挑到 glUniform3i，打在 vec3 上 ＝ InvalidOperation（2026-10-09 第一次跑就撞到）
            gl.Uniform3(Loc($"uFN[{f}]"), (float)face.Normal[0], (float)face.Normal[1], (float)face.Normal[2]);
            float s = iTamperFace0 && f == 0 ? -1f : 1f;
            gl.Uniform3(Loc($"uFU[{f}]"), s * face.U[0], s * face.U[1], s * face.U[2]);
            gl.Uniform3(Loc($"uFV[{f}]"), (float)face.V[0], (float)face.V[1], (float)face.V[2]);
        }
        gl.Uniform1(Loc("uN"), m_N);
        gl.Uniform1(Loc("uTile"), m_Tile);
        gl.Uniform1(Loc("uTps"), m_Tps);
        int b = iScene.State.BaseRgb;
        gl.Uniform3(Loc("uBase"), ((b >> 16) & 255) / 255f, ((b >> 8) & 255) / 255f, (b & 255) / 255f);
        gl.Uniform1(Loc("uMode"), (int)iMode);
        gl.Uniform1(Loc("uGrat"), (float)iScene.Graticule);
        gl.Uniform1(Loc("uSeams"), iScene.Seams ? 1 : 0);
        gl.Uniform1(Loc("uZoneCount"), iScene.Zones.Count);
        for (int z = 0; z < iScene.Zones.Count; z++)
        {
            SCP_GlobeZone zn = iScene.Zones[z];
            gl.Uniform4(Loc($"uZones[{z}]"), (float)zn.South, (float)zn.West, (float)zn.North, (float)zn.East);
            gl.Uniform1(Loc($"uZoneStatus[{z}]"), zn.Status == "done" ? 2 : zn.Status == "paused" ? 1 : 0);
        }

        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2DArray, m_Atlas);
        gl.Uniform1(Loc("uAtlas"), 0);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, m_IndexTex);
        gl.Uniform1(Loc("uIndex"), 1);
        gl.Uniform1(Loc("uHasAtlas"), m_Atlas != 0 ? 1 : 0);

        gl.BindVertexArray(m_Vao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.BindVertexArray(0);
        gl.ActiveTexture(TextureUnit.Texture1);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        gl.UseProgram(0);
        gl.Disable(EnableCap.DepthTest);
    }

    void U3(string iName, double[] v) => m_Gl.Uniform3(Loc(iName), (float)v[0], (float)v[1], (float)v[2]);

    int Loc(string iName)
    {
        if (!m_Uni.TryGetValue(iName, out int l)) m_Uni[iName] = l = m_Gl.GetUniformLocation(m_Prog, iName);
        return l;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // shader
    // ════════════════════════════════════════════════════════════════════════════
    const string Vs = @"#version 330 core
void main() {
    vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    gl_Position = vec4(p, 0.0, 1.0);
}";

    // 取格：與 SCP_GlobeGrid.DirToCell 同一式（面取 n·d 最大、同分取面序在前；a ＝ atan(切平面座標)／(π/4)；k ＝ floor((a＋1)／2·N) 夾在 0..N−1）。
    // 取色：與 SCP_GlobeCells.Locate 同一個分塊鍵 (f·tps ＋ j/Tile)·tps ＋ i/Tile；塊內 (i%Tile, j%Tile)。值 0 ＝ 沒畫過 ⇒ 底色。
    // 疊圖：語意同 CPU ortho（施工區框線優先、再經緯線；面接縫最後蓋上），容許寬度改用螢幕導數（約一像素）。
    const string Fs = @"#version 330 core
uniform vec3 uEye; uniform vec3 uRight; uniform vec3 uUp; uniform vec3 uForward;
uniform float uTanX; uniform float uTanY; uniform vec2 uSize; uniform float uNear; uniform float uFar;
uniform vec3 uLight;
uniform vec3 uFN[6]; uniform vec3 uFU[6]; uniform vec3 uFV[6];
uniform int uN; uniform int uTile; uniform int uTps;
uniform vec3 uBase;
uniform int uMode;
uniform float uGrat;
uniform int uSeams;
uniform int uZoneCount;
uniform vec4 uZones[64];
uniform int uZoneStatus[64];
uniform sampler2DArray uAtlas;
uniform isampler2D uIndex;
uniform int uHasAtlas;
out vec4 oColor;
const float PI = 3.14159265358979;

int paramToCell(float t) {
    float a = atan(t) / (PI / 4.0);
    int k = int(floor((a + 1.0) * 0.5 * float(uN)));
    return clamp(k, 0, uN - 1);
}
float lonGap(float a, float b) { float d = mod(a - b, 360.0); return d < 0.0 ? d + 360.0 : d; }
float normLon(float v) { v = mod(v + 180.0, 360.0); if (v < 0.0) v += 360.0; return v - 180.0; }

void main() {
    float px = gl_FragCoord.x - 0.5;
    float py = gl_FragCoord.y - 0.5;          // 由上到下的列號（見檔頭）
    float nx = 2.0 * (px + 0.5) / uSize.x - 1.0;
    float ny = 1.0 - 2.0 * (py + 0.5) / uSize.y;
    vec3 d = normalize(uForward + uRight * (nx * uTanX) + uUp * (ny * uTanY));
    float b = dot(uEye, d);
    float c = dot(uEye, uEye) - 1.0;
    float disc = b * b - c;
    if (disc < 0.0 || b >= 0.0) {
        gl_FragDepth = 1.0;
        oColor = uMode == 1 ? vec4(1.0) : vec4(12.0 / 255.0, 14.0 / 255.0, 22.0 / 255.0, 1.0);
        return;
    }
    float t = c / (-b + sqrt(disc));
    vec3 p = uEye + t * d;

    int face = 0; float best = -1e30;
    for (int f = 0; f < 6; f++) { float v = dot(uFN[f], p); if (v > best) { best = v; face = f; } }
    float tu = dot(uFU[face], p) / best, tv = dot(uFV[face], p) / best;
    int i = paramToCell(tu);
    int j = paramToCell(tv);

    // 深度：鏡頭空間距離 → NDC（與一般透視投影同一式），讓之後光柵化的建築跟球面正確遮擋
    float zEye = t * dot(d, uForward);
    float ndc = (uFar + uNear) / (uFar - uNear) - 2.0 * uFar * uNear / ((uFar - uNear) * zEye);
    gl_FragDepth = clamp(ndc * 0.5 + 0.5, 0.0, 1.0);

    if (uMode == 1) {
        int idx = face * uN * uN + j * uN + i;
        oColor = vec4(float(idx & 255), float((idx >> 8) & 255), float((idx >> 16) & 255), float((idx >> 24) & 255)) / 255.0;
        return;
    }

    vec3 col = uBase;
    if (uHasAtlas == 1) {
        int key = (face * uTps + j / uTile) * uTps + i / uTile;
        int slot = texelFetch(uIndex, ivec2(key % uTps, key / uTps), 0).r;
        if (slot >= 0) {
            int layer = slot / 256, inl = slot % 256;
            ivec3 tc = ivec3((inl % 16) * uTile + i % uTile, (inl / 16) * uTile + j % uTile, layer);
            vec3 v = texelFetch(uAtlas, tc, 0).rgb;
            if (v != vec3(0.0)) col = v;
        }
    }
    float shade = 0.35 + 0.65 * max(0.0, dot(p, uLight));
    col *= shade;

    float lat = degrees(asin(clamp(p.z, -1.0, 1.0)));
    float lon = degrees(atan(p.y, p.x));
    // 線寬以「螢幕像素」量：每條邊用**它自己那個座標**的螢幕梯度長度換算成「離這條邊幾個像素」。
    // 🩸 2026-10-09 Tim 報：第一版四條邊共用 max(緯度導數, 經度導數) ⇒ 靠近球的輪廓時透視壓縮讓其中一個方向的導數暴增，
    //   另一個方向的邊被一起加粗（框線在邊緣變成粗白條）。
    float dLatX = dFdx(lat), dLatY = dFdy(lat);
    float dLonX = dFdx(lon), dLonY = dFdy(lon);
    if (dLonX > 180.0) dLonX -= 360.0; else if (dLonX < -180.0) dLonX += 360.0;
    if (dLonY > 180.0) dLonY -= 360.0; else if (dLonY < -180.0) dLonY += 360.0;
    float gLat = max(length(vec2(dLatX, dLatY)), 1e-7);   // 每像素幾度（緯度）
    float gLon = max(length(vec2(dLonX, dLonY)), 1e-7);   // 每像素幾度（經度）
    bool zoneHit = false;
    const float zPx = 1.5;   // 框線寬（像素，只畫在框內那一側）
    for (int z = 0; z < uZoneCount; z++) {
        vec4 zb = uZones[z];
        if (lat < zb.x || lat > zb.z) continue;
        float lo = normLon(lon), w = normLon(zb.y), e = normLon(zb.w);
        bool inside = w <= e ? (lo >= w && lo <= e) : (lo >= w || lo <= e);
        if (!inside) continue;
        float dW = lonGap(lon, zb.y) / gLon, dE = lonGap(zb.w, lon) / gLon;
        if ((lat - zb.x) / gLat < zPx || (zb.z - lat) / gLat < zPx || dW < zPx || dE < zPx) {
            int s = uZoneStatus[z];
            col = s == 2 ? vec3(200.0, 200.0, 200.0) / 255.0 : s == 1 ? vec3(255.0, 150.0, 40.0) / 255.0 : vec3(255.0, 230.0, 0.0) / 255.0;
            zoneHit = true;
            break;
        }
    }
    if (!zoneHit && uGrat > 0.0) {
        float eLat = abs(lat - round(lat / uGrat) * uGrat);
        float eLon = abs(lon - round(lon / uGrat) * uGrat);
        if (eLat / gLat < 0.6 || (eLon / gLon < 0.6 && abs(lat) < 89.0)) col = col * 0.55 + vec3(0.45);
    }
    // 面接縫：離面邊（等角參數 |a|＝1）不到約一像素 ⇒ 接縫色。⚠ 只看 fwidth(face) 會畫成虛線（2×2 像素組剛好跨過接縫才亮）；
    //   跨面那一組的參數導數是兩面混算、沒有意義 ⇒ 那一組直接算接縫
    if (uSeams == 1) {
        float au = atan(tu) / (PI / 4.0), av = atan(tv) / (PI / 4.0);
        bool aCross = fwidth(float(face)) > 0.0;
        float w = max(fwidth(au), fwidth(av));
        if (aCross || 1.0 - max(abs(au), abs(av)) < w * 0.75) col = vec3(1.0, 80.0 / 255.0, 200.0 / 255.0);
    }
    oColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}";

    uint Link(string iVs, string iFs)
    {
        var gl = m_Gl;
        uint aVs = Compile(ShaderType.VertexShader, iVs), aFs = Compile(ShaderType.FragmentShader, iFs);
        uint p = gl.CreateProgram();
        gl.AttachShader(p, aVs); gl.AttachShader(p, aFs);
        gl.LinkProgram(p);
        gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int ok);
        gl.DeleteShader(aVs); gl.DeleteShader(aFs);
        if (ok == 0) throw new InvalidOperationException("球面 shader 連結失敗：" + gl.GetProgramInfoLog(p));
        return p;
    }

    uint Compile(ShaderType iType, string iSrc)
    {
        var gl = m_Gl;
        uint s = gl.CreateShader(iType);
        gl.ShaderSource(s, iSrc);
        gl.CompileShader(s);
        gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("球面 " + iType + " 編譯失敗：" + gl.GetShaderInfoLog(s));
        return s;
    }

    public void Dispose()
    {
        if (m_Atlas != 0) m_Gl.DeleteTexture(m_Atlas);
        if (m_IndexTex != 0) m_Gl.DeleteTexture(m_IndexTex);
        m_Gl.DeleteProgram(m_Prog);
        m_Gl.DeleteVertexArray(m_Vao);
        m_Atlas = m_IndexTex = 0;
    }
}
