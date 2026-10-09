// 回歸（TASK-0474）：SCP_JsonMapper 的整數欄位不經過 double —— 大 long／ulong 逐位來回、小數與越界記 diagnostic 不四捨五入不截斷。
using SCP.Core.Json;

namespace Senate.Cli;

public static partial class SelfTest
{
    sealed class MapperIntegerProbe
    {
        public long L = 7;
        public ulong U = 7;
        public int I = 7;
        public short S = 7;
        public double Dv = 7;
    }

    static CheckRow JsonMapperIntegerPrecision()
    {
        const string name = "SCP_JsonMapper 整數精確：大 long／ulong 逐位來回、小數與越界記一筆不四捨五入不截斷、double 照舊";
        var failures = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        (MapperIntegerProbe Probe, List<string> Diag) Read(string json)
        {
            var p = new MapperIntegerProbe(); var opt = new SCP_JsonMapOptions();
            SCP_JsonMapper.Populate(p, SCP_JsonParser.Parse(json), opt);
            return (p, opt.Diagnostics);
        }
        try
        {
            var src = new MapperIntegerProbe { L = 639271089190795485, U = ulong.MaxValue, I = -5, S = short.MinValue, Dv = 1.5 };
            string text = SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(src), false);
            var (back, diag) = Read(text);
            Check(text.Contains("639271089190795485") && text.Contains("18446744073709551615"), "寫出原文：" + text);
            Check(back.L == 639271089190795485 && back.U == ulong.MaxValue && back.I == -5 && back.S == short.MinValue && back.Dv == 1.5 && diag.Count == 0,
                $"來回逐位相同（L={back.L} U={back.U} diag={diag.Count}）");
            var (minL, _) = Read("{\"L\":" + long.MinValue + "}");
            Check(minL.L == long.MinValue, "long.MinValue 來回");

            var (frac, fracDiag) = Read("{\"I\":19.6}");
            Check(frac.I == 7 && fracDiag.Count == 1 && fracDiag[0].Contains("不是整數"), "小數讀進 int ⇒ 保留原值＋記一筆：" + string.Join("；", fracDiag));
            var (whole, wholeDiag) = Read("{\"I\":3.0,\"L\":1e3}");
            Check(whole.I == 3 && whole.L == 1000 && wholeDiag.Count == 0, "剛好是整數的 3.0／1e3 照收");
            var (over, overDiag) = Read("{\"S\":70000,\"U\":-1,\"L\":9223372036854775808}");
            Check(over.S == 7 && over.U == 7 && over.L == 7 && overDiag.Count == 3, "越界（short 70000／ulong -1／long.MaxValue+1）⇒ 保留原值、各記一筆：" + string.Join("；", overDiag));
            var (dbl, dblDiag) = Read("{\"Dv\":2.25,\"I\":42}");
            Check(dbl.Dv == 2.25 && dbl.I == 42 && dblDiag.Count == 0, "反向對照：double 與一般 int 照舊");
            return new CheckRow(name, failures.Count == 0 ? "639271089190795485 與 ulong.MaxValue 逐位來回；19.6→int 記一筆不讀；3.0／1e3 照收；三種越界各記一筆；double／int 照舊" : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
    }
}
