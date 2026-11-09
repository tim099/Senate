// 區塊職責：雕刻預覽認混合材質、材質表 smooth 旗標自動平滑（TASK-0860）的淨室驗收 ——
//   · 混合寫法外觀照第一個成分（同 Blockside 05_material.js matMix／matPart）：iron*0.15、iron*0.5+oak*0.5 ⇒ iron；oak*0.5+iron*0.5 ⇒ oak；
//     不合的（第一個是 air、比例 > 1 或 0、加起來 > 1、成分不在表裡、大寫）⇒ false。
//   · 材質表 smooth：要是 true／false（字串 ⇒ 擋）。
//   · 組裝讀數：沒寫 render、材質標 smooth ⇒ 平滑＋反光；render=voxel ⇒ 方塊；材質沒標 smooth ⇒ 方塊；
//     反向對照：同一份讀數、材質表拿掉 smooth ⇒ 一格都不平滑（null）—— 尺不是永遠喊平滑。
//   · 沒有任何 material／render ⇒ null（渲染器整條不走 ⇒ 沒寫 meta 的作品不變）。
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptMixMatCleanRoom()
    {
        const string name = "雕刻預覽：混合材質照第一個成分、材質表 smooth 旗標沒寫 render 也平滑、render=voxel 照方塊（淨室，TASK-0860）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        try
        {
            const string tbl = "{\"schema\":1,\"list\":{\"iron\":{\"metal\":1,\"rough\":0.35,\"smooth\":true},\"boiler\":{\"metal\":0.85,\"rough\":0.45,\"smooth\":true},\"oak\":{\"metal\":0}}}";
            var t = SCP_SculptMaterials.Parse(SCP_JsonData.Parse(tbl), "淨室");
            var plain = SCP_SculptMaterials.Parse(SCP_JsonData.Parse(tbl.Replace(",\"smooth\":true", "")), "淨室");

            // ── 混合寫法 ──
            Check(t.TryLook("iron*0.15", out var a) && a!.Metal == 1 && a.Smooth, "iron*0.15 ⇒ iron（金屬 1、smooth）");
            Check(t.TryLook("iron*0.5+oak*0.5", out var b) && b!.Metal == 1, "iron*0.5+oak*0.5 ⇒ 照第一個 iron");
            Check(t.TryLook("oak*0.5+iron*0.5", out var c2) && c2!.Metal == 0 && !c2.Smooth, "oak*0.5+iron*0.5 ⇒ 照第一個 oak");
            Check(t.TryLook("iron*0.86+air*0.14", out _), "air 當後面的成分可以");
            Check(t.TryGet("iron*0.1", out float gm, out float gr) && gm == 1 && Math.Abs(gr - 0.35f) < 1e-6, "TryGet 也認混合寫法");
            string[] bad = { "air*0.5+iron*0.5", "iron*1.5", "iron*0", "iron*0.6+oak*0.6", "gold*0.5", "Iron*0.5", "iron*", "iron*0.5+", "iron**0.5" };
            int aBlocked = 0;
            foreach (string s0 in bad) { if (t.TryLook(s0, out _)) failures.Add("不合的混合寫法應回 false：" + s0); else aBlocked++; }
            readings.Add($"不合的混合寫法 {aBlocked}／{bad.Length} 擋下");

            // ── smooth 的型別 ──
            bool threw = false;
            try { SCP_SculptMaterials.Parse(SCP_JsonData.Parse("{\"schema\":1,\"list\":{\"iron\":{\"smooth\":\"yes\"}}}"), "淨室"); }
            catch (InvalidDataException) { threw = true; }
            Check(threw, "smooth 寫成字串應擋");

            // ── 組裝讀數 ⇒ Surface ──
            var vis = new List<SCP_SculptVoxel> { new(0, 0, 0, 1), new(1, 0, 0, 1), new(2, 0, 0, 1), new(3, 0, 0, 1), new(4, 0, 0, 1) };
            var mat = new Dictionary<long, string>
            {
                [SCP_SculptSurface.Key(0, 0, 0)] = "iron*0.15",
                [SCP_SculptSurface.Key(1, 0, 0)] = "iron*0.15",
                [SCP_SculptSurface.Key(2, 0, 0)] = "oak*0.5",
                [SCP_SculptSurface.Key(4, 0, 0)] = "boiler",
            };
            var ren = new Dictionary<long, string> { [SCP_SculptSurface.Key(1, 0, 0)] = "voxel" };
            SCP_SculptSurface? s = SCP_SculptSurface.FromCompose(vis, ren, mat, t);
            Check(s != null && s.Smooth.Count == 2 && s.IsSmooth(0, 0, 0) && s.IsSmooth(4, 0, 0), "iron*0.15、boiler 沒寫 render ⇒ 平滑（2 格）：" + s?.Smooth.Count);
            Check(s != null && !s.IsSmooth(1, 0, 0), "render=voxel ⇒ 方塊");
            Check(s != null && !s.IsSmooth(2, 0, 0), "oak 沒標 smooth ⇒ 方塊");
            float m0 = 0, r0 = 0;
            s?.MaterialAt(0, 0, 0, out m0, out r0);
            Check(m0 == 1 && Math.Abs(r0 - 0.35f) < 1e-6, "iron*0.15 的平滑格反光照 iron：" + m0 + "／" + r0);
            Check(s != null && s.Unknown.Count == 0, "混合寫法不該進「材質表沒有」：" + string.Join(",", s?.Unknown ?? new SortedSet<string>()));
            Check(SCP_SculptSurface.FromCompose(vis, null, mat, t) != null, "沒有 render 讀數、材質標 smooth ⇒ 照樣平滑（以前回 null）");
            // 反向對照：材質表拿掉 smooth ⇒ 一格都不平滑
            Check(SCP_SculptSurface.FromCompose(vis, ren, mat, plain) == null, "材質表沒有 smooth ⇒ null（反向對照）");
            Check(SCP_SculptSurface.FromCompose(vis, null, null, t) == null, "沒有 material／render ⇒ null");
            readings.Add("組裝讀數：" + (s?.Describe() ?? "null"));
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }
}
