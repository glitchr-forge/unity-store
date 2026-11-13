using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Glitchr.Store.Tests
{
    /// <summary>
    /// Ed25519 (RFC 8032) for the tests only: test keys made from fixed seeds, tokens and rotations
    /// signed as a Forge or omnihub signs them, and a verification, so the licence chain runs with
    /// real signatures in a project without Forge Seal too. Forge Store itself never signs, and
    /// verifies only through Forge Seal. Checked against RFC 8032's first vector.
    /// </summary>
    internal static class TestSigner
    {
        private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
        private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
        private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
        private static readonly BigInteger I = BigInteger.ModPow(2, (P - 1) / 4, P);
        private static readonly BigInteger[] Base = MakeBase();

        /// <summary>A 32-byte seed from a word: the test key's private half, never anything real.</summary>
        public static byte[] Seed(string word)
        {
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes("forge-seal-test:" + word));
        }

        public static byte[] PublicKey(byte[] seed)
        {
            return Encode(Multiply(Scalar(seed, out _), Base));
        }

        public static string PublicKeyBase64(byte[] seed) => Convert.ToBase64String(PublicKey(seed));

        public static byte[] Sign(byte[] seed, byte[] message)
        {
            BigInteger a = Scalar(seed, out byte[] prefix);
            byte[] publicKey = Encode(Multiply(a, Base));
            BigInteger r = Reduce(Sha512(prefix, message));
            byte[] rEncoded = Encode(Multiply(r, Base));
            BigInteger k = Reduce(Sha512(rEncoded, publicKey, message));
            BigInteger s = (r + k * a) % L;
            var signature = new byte[64];
            Buffer.BlockCopy(rEncoded, 0, signature, 0, 32);
            Buffer.BlockCopy(LittleEndian32(s), 0, signature, 32, 32);

            return signature;
        }

        /// <summary>A token as a Forge or omnihub writes it: base64url(JSON) "." base64url(signature).</summary>
        public static string Token(byte[] seed, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            return Base64Url(bytes) + "." + Base64Url(Sign(seed, bytes));
        }

        /// <summary>RFC 8032, 5.1.7 (cofactorless): what Forge Seal's Ed25519.Verify answers.</summary>
        public static bool Verify(byte[] signature, byte[] message, byte[] publicKey)
        {
            if (signature == null || publicKey == null || signature.Length != 64 || publicKey.Length != 32) return false;
            BigInteger[] a = Decode(publicKey, 0), r = Decode(signature, 0);
            var sBytes = new byte[32];
            Buffer.BlockCopy(signature, 32, sBytes, 0, 32);
            BigInteger s = Unsigned(sBytes);
            if (a == null || r == null || s >= L) return false;
            var rBytes = new byte[32];
            Buffer.BlockCopy(signature, 0, rBytes, 0, 32);
            BigInteger h = Reduce(Sha512(rBytes, publicKey, message));
            BigInteger[] left = Multiply(s, Base), right = Add(r, Multiply(h, a));

            return Mod(left[0] * right[2] - right[0] * left[2]).IsZero && Mod(left[1] * right[2] - right[1] * left[2]).IsZero;
        }

        private static BigInteger[] Decode(byte[] bytes, int offset)
        {
            var copy = new byte[32];
            Buffer.BlockCopy(bytes, offset, copy, 0, 32);
            int sign = copy[31] >> 7;
            copy[31] &= 0x7F;
            BigInteger y = Unsigned(copy);
            if (y >= P) return null;
            BigInteger x2 = Mod((y * y - 1) * Inverse(D * y * y + 1));
            BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
            if (!Mod(x * x - x2).IsZero) x = Mod(x * I);
            if (!Mod(x * x - x2).IsZero) return null;
            if ((x.IsEven ? 0 : 1) != sign) x = Mod(P - x);
            if (x.IsZero && sign == 1) return null;

            return new[] { x, y, BigInteger.One, Mod(x * y) };
        }

        public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static BigInteger Scalar(byte[] seed, out byte[] prefix)
        {
            byte[] h = Sha512(seed);
            var head = new byte[32];
            Buffer.BlockCopy(h, 0, head, 0, 32);
            head[0] &= 248;
            head[31] &= 127;
            head[31] |= 64;
            prefix = new byte[32];
            Buffer.BlockCopy(h, 32, prefix, 0, 32);

            return Unsigned(head);
        }

        private static byte[] Sha512(params byte[][] parts)
        {
            using (SHA512 sha = SHA512.Create())
            {
                int length = 0;
                foreach (byte[] part in parts) length += part.Length;
                var all = new byte[length];
                int at = 0;
                foreach (byte[] part in parts)
                {
                    Buffer.BlockCopy(part, 0, all, at, part.Length);
                    at += part.Length;
                }

                return sha.ComputeHash(all);
            }
        }

        private static BigInteger Reduce(byte[] digest) => Unsigned(digest) % L;

        private static BigInteger Unsigned(byte[] littleEndian)
        {
            var copy = new byte[littleEndian.Length + 1];
            Buffer.BlockCopy(littleEndian, 0, copy, 0, littleEndian.Length);

            return new BigInteger(copy);
        }

        private static byte[] LittleEndian32(BigInteger value)
        {
            byte[] raw = value.ToByteArray();
            var bytes = new byte[32];
            Buffer.BlockCopy(raw, 0, bytes, 0, Math.Min(32, raw.Length));

            return bytes;
        }

        // Points in extended coordinates (X, Y, Z, T).
        private static BigInteger[] MakeBase()
        {
            BigInteger y = Mod(4 * Inverse(5));
            BigInteger x2 = Mod((y * y - 1) * Inverse(D * y * y + 1));
            BigInteger x = BigInteger.ModPow(x2, (P + 3) / 8, P);
            if (!Mod(x * x - x2).IsZero) x = Mod(x * I);
            if (!x.IsEven) x = P - x;

            return new[] { x, y, BigInteger.One, Mod(x * y) };
        }

        private static BigInteger[] Add(BigInteger[] p, BigInteger[] q)
        {
            BigInteger a = Mod((p[1] - p[0]) * (q[1] - q[0]));
            BigInteger b = Mod((p[1] + p[0]) * (q[1] + q[0]));
            BigInteger c = Mod(2 * p[3] * q[3] * D);
            BigInteger d = Mod(2 * p[2] * q[2]);
            BigInteger e = b - a, f = d - c, g = d + c, h = b + a;

            return new[] { Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h) };
        }

        private static BigInteger[] Multiply(BigInteger scalar, BigInteger[] point)
        {
            BigInteger[] result = { BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero };
            while (scalar > 0)
            {
                if (!scalar.IsEven) result = Add(result, point);
                point = Add(point, point);
                scalar >>= 1;
            }

            return result;
        }

        private static byte[] Encode(BigInteger[] point)
        {
            BigInteger zi = Inverse(point[2]);
            BigInteger x = Mod(point[0] * zi), y = Mod(point[1] * zi);
            byte[] bytes = LittleEndian32(y);
            if (!x.IsEven) bytes[31] |= 0x80;

            return bytes;
        }

        private static BigInteger Inverse(BigInteger x) => BigInteger.ModPow(Mod(x), P - 2, P);

        private static BigInteger Mod(BigInteger x)
        {
            BigInteger r = x % P;

            return r.Sign < 0 ? r + P : r;
        }
    }
}
