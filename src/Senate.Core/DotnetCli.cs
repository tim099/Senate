// 區塊職責：問 dotnet 自己的版本。
// 物理意義：🩸 第一版把 `Environment.Version`（**執行期**版本，10.0.11）印在「.NET SDK」那一列，
//           而同一次執行裡 setup 腳本印的是 `dotnet --version`（**SDK** 版本，10.0.400）——
//           兩個都是真數字，但答的是不同問題。這種錯不會報錯，只會讓人日後拿錯的版本去對照。
//           ⇒ 要 SDK 版本就去問 SDK，要執行期版本就標成執行期。
// 數值影響：起一個 dotnet 子行程；問不到回 null（**不要**回猜的值）。
using System.Diagnostics;

namespace Senate.Core;

public static class DotnetCli
{
    public static string? SdkVersion()
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi);
            if (p == null) return null;
            string aOut = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(15_000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 && aOut.Length > 0 ? aOut : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>執行本程式的 .NET 執行期版本（跟 SDK 版本是兩件事）。</summary>
    public static string RuntimeVersion => Environment.Version.ToString();
}
