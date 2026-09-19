namespace HyPanel.Server.Security;

using System.Numerics;

/// <summary>
/// Minimal RFC 7748 X25519 scalar multiplication used only to generate a REALITY key pair for a brand-new service.
/// The BCL does not expose X25519, and this is never used to process attacker-controlled or long-lived secrets, so a
/// straightforward BigInteger Montgomery ladder is acceptable. RFC 7748 test vectors pin the behavior.
/// </summary>
internal static class X25519
{
    private const int ScalarBytes = 32;
    private static readonly BigInteger Prime = (BigInteger.One << 255) - 19;
    private static readonly BigInteger CurveFactor = 121665;

    public static byte[] ScalarMultiplyBase(byte[] scalar)
    {
        var basePoint = new byte[ScalarBytes];
        basePoint[0] = 9;
        return ScalarMultiply(scalar, basePoint);
    }

    /// <summary>Returns the canonical clamped form of a scalar, matching what the official REALITY tooling prints.</summary>
    public static byte[] ClampScalar(byte[] scalar) => Clamp(scalar);

    public static byte[] ScalarMultiply(byte[] scalar, byte[] uCoordinate)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(scalar.Length, ScalarBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(uCoordinate.Length, ScalarBytes);

        var k = Clamp(scalar);
        var x1 = DecodeUCoordinate(uCoordinate);
        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        var swap = 0;

        for (var bit = 254; bit >= 0; bit--)
        {
            var current = (k[bit >> 3] >> (bit & 7)) & 1;
            swap ^= current;
            (x2, x3) = ConditionalSwap(swap, x2, x3);
            (z2, z3) = ConditionalSwap(swap, z2, z3);
            swap = current;

            var a = Mod(x2 + z2);
            var aa = Mod(a * a);
            var b = Mod(x2 - z2);
            var bb = Mod(b * b);
            var e = Mod(aa - bb);
            var c = Mod(x3 + z3);
            var d = Mod(x3 - z3);
            var da = Mod(d * a);
            var cb = Mod(c * b);

            var x3Squared = Mod(da + cb);
            x3 = Mod(x3Squared * x3Squared);
            var z3Squared = Mod(da - cb);
            z3 = Mod(x1 * Mod(z3Squared * z3Squared));
            x2 = Mod(aa * bb);
            z2 = Mod(e * Mod(aa + CurveFactor * e));
        }

        (x2, x3) = ConditionalSwap(swap, x2, x3);
        (z2, z3) = ConditionalSwap(swap, z2, z3);
        return EncodeUCoordinate(Mod(x2 * BigInteger.ModPow(z2, Prime - 2, Prime)));
    }

    private static byte[] Clamp(byte[] scalar)
    {
        var clamped = (byte[])scalar.Clone();
        clamped[0] &= 248;
        clamped[31] &= 127;
        clamped[31] |= 64;
        return clamped;
    }

    private static BigInteger DecodeUCoordinate(byte[] value)
    {
        var littleEndian = (byte[])value.Clone();
        littleEndian[31] &= 127;
        return new BigInteger(littleEndian, isUnsigned: true, isBigEndian: false);
    }

    private static byte[] EncodeUCoordinate(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        var result = new byte[ScalarBytes];
        Array.Copy(bytes, result, Math.Min(bytes.Length, ScalarBytes));
        return result;
    }

    private static BigInteger Mod(BigInteger value)
    {
        var result = value % Prime;
        return result.Sign < 0 ? result + Prime : result;
    }

    private static (BigInteger Left, BigInteger Right) ConditionalSwap(int swap, BigInteger left, BigInteger right)
    {
        if (swap == 0) return (left, right);
        return (right, left);
    }
}
