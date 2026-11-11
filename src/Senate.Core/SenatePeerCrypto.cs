// 區塊職責：**跨主機連線的密碼學那一層**（TASK-0011）—— 身分金鑰、簽章、連線階段的加密。
// 物理意義：
//   · 身分＝一把 P-256 簽章金鑰（ECDSA）。**節點的 id ＝ 簽章公鑰的 SHA-256 前 16 個 hex**。
//     ⛔ IP 不是身分：IP 只是每次重新公布的「現在在哪」。
//   · `FrameCipher`：握手完成後每一個 WebSocket frame 的加密（AES-GCM，計數器當 nonce，兩個方向各一把金鑰）。
//     ⇒ 就算流量經過 Cloudflare 通道，它也只看得到密文。
// 數值影響：純運算，不碰磁碟與網路。全部用 .NET 內建（⛔ 不加套件）。
#nullable enable
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Senate.Core;

public static class SenatePeerCrypto
{
    public const string Proto = "senate-peer-v1";

    public static string B64(byte[] iBytes) => Convert.ToBase64String(iBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] UnB64(string iText)
    {
        string s = (iText ?? "").Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    public static string RandomToken(int iBytes) => B64(RandomNumberGenerator.GetBytes(iBytes));

    // ── 身分金鑰 ─────────────────────────────────────────────────────

    /// <summary>產生一把 P-256 金鑰：回（私鑰 PKCS#8、公鑰 SPKI），都是 base64url。</summary>
    public static (string Priv, string Pub) NewKeyPair()
    {
        using ECDsa k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (B64(k.ExportPkcs8PrivateKey()), B64(k.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>主機 id ＝ 簽章公鑰 SPKI 的 SHA-256 前 8 bytes（16 個 hex）。</summary>
    public static string IdOf(string iSignPub)
        => Convert.ToHexString(SHA256.HashData(UnB64(iSignPub)), 0, 8).ToLowerInvariant();

    public static string Sign(string iSignPriv, byte[] iData)
    {
        using ECDsa k = ECDsa.Create();
        k.ImportPkcs8PrivateKey(UnB64(iSignPriv), out _);
        return B64(k.SignData(iData, HashAlgorithmName.SHA256));
    }

    public static bool Verify(string iSignPub, byte[] iData, string iSig)
    {
        try
        {
            using ECDsa k = ECDsa.Create();
            k.ImportSubjectPublicKeyInfo(UnB64(iSignPub), out _);
            return k.VerifyData(iData, UnB64(iSig), HashAlgorithmName.SHA256);
        }
        catch (Exception) { return false; }   // 公鑰／簽章格式壞掉 ＝ 驗不過
    }

    public static string Hmac(string iSecret, string iText)
        => B64(HMACSHA256.HashData(UnB64(iSecret), Encoding.UTF8.GetBytes(iText)));

    public static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? ""));

    // ── 連線階段 ─────────────────────────────────────────────────────

    /// <summary>握手用的臨時 ECDH。<see cref="Derive"/> 之後兩邊拿到同一組（c2s、s2c）金鑰。</summary>
    public sealed class Ephemeral : IDisposable
    {
        readonly ECDiffieHellman m_Key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        public string Pub => B64(m_Key.ExportSubjectPublicKeyInfo());

        public (byte[] C2S, byte[] S2C) Derive(string iOtherPub, string iClientNonce, string iServerNonce)
        {
            using ECDiffieHellman aOther = ECDiffieHellman.Create();
            aOther.ImportSubjectPublicKeyInfo(UnB64(iOtherPub), out _);
            byte[] aOkm = HKDF.DeriveKey(HashAlgorithmName.SHA256, m_Key.DeriveKeyFromHash(aOther.PublicKey, HashAlgorithmName.SHA256), 64,
                                         Encoding.ASCII.GetBytes(iClientNonce + "|" + iServerNonce), Encoding.ASCII.GetBytes(Proto + "|session"));
            return (aOkm.AsSpan(0, 32).ToArray(), aOkm.AsSpan(32, 32).ToArray());
        }

        public void Dispose() => m_Key.Dispose();
    }

    /// <summary>一個方向的 frame 加解密。frame ＝ 8 bytes 計數器 ｜ 16 bytes tag ｜ 密文；計數器必須嚴格遞增（⛔ 重播的 frame 丟掉）。</summary>
    public sealed class FrameCipher : IDisposable
    {
        readonly AesGcm m_Gcm;
        ulong m_Send;
        ulong m_Recv;

        public FrameCipher(byte[] iKey) { m_Gcm = new AesGcm(iKey, 16); }

        static byte[] Nonce(ulong iCounter)
        {
            byte[] n = new byte[12];
            BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), iCounter);
            return n;
        }

        public byte[] Encrypt(string iText)
        {
            ulong c = ++m_Send;
            byte[] aPt = Encoding.UTF8.GetBytes(iText);
            byte[] aOut = new byte[24 + aPt.Length];
            BinaryPrimitives.WriteUInt64BigEndian(aOut, c);
            m_Gcm.Encrypt(Nonce(c), aPt, aOut.AsSpan(24), aOut.AsSpan(8, 16));
            return aOut;
        }

        public string? Decrypt(byte[] iFrame, int iLength)
        {
            try
            {
                if (iLength < 24) return null;
                ulong c = BinaryPrimitives.ReadUInt64BigEndian(iFrame);
                if (c <= m_Recv) return null;
                byte[] aPt = new byte[iLength - 24];
                m_Gcm.Decrypt(Nonce(c), iFrame.AsSpan(24, iLength - 24), iFrame.AsSpan(8, 16), aPt);
                m_Recv = c;
                return Encoding.UTF8.GetString(aPt);
            }
            catch (Exception) { return null; }
        }

        public void Dispose() => m_Gcm.Dispose();
    }
}
