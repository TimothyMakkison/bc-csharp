﻿using System;
using System.Diagnostics;

using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math.Raw;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace Org.BouncyCastle.Math.EC.Rfc8032
{
    using F = Rfc7748.X25519Field;

    /// <summary>
    /// A low-level implementation of the Ed25519, Ed25519ctx, and Ed25519ph instantiations of the Edwards-Curve Digital
    /// Signature Algorithm specified in <a href="https://www.rfc-editor.org/rfc/rfc8032">RFC 8032</a>.
    /// </summary>
    /// <remarks>
    /// The implementation strategy is mostly drawn from <a href="https://ia.cr/2012/309">
    /// Mike Hamburg, "Fast and compact elliptic-curve cryptography"</a>, notably the "signed multi-comb" algorithm (for
    /// scalar multiplication by a fixed point), the "half Niels coordinates" (for precomputed points), and the
    /// "extensible coordinates" (for accumulators). Standard
    /// <a href="https://hyperelliptic.org/EFD/g1p/auto-twisted-extended.html">extended coordinates</a> are used during
    /// precomputations, needing only a single extra point addition formula.
    /// </remarks>
    public static class Ed25519
    {
        // -x^2 + y^2 == 1 + 0x52036CEE2B6FFE738CC740797779E89800700A4D4141D8AB75EB4DCA135978A3 * x^2 * y^2

        public enum Algorithm
        {
            Ed25519 = 0,
            Ed25519ctx = 1,
            Ed25519ph = 2,
        }

        private const long M08L = 0x000000FFL;
        private const long M28L = 0x0FFFFFFFL;
        private const long M32L = 0xFFFFFFFFL;

        private const int CoordUints = 8;
        private const int PointBytes = CoordUints * 4;
        private const int ScalarUints = 8;
        private const int ScalarBytes = ScalarUints * 4;

        public static readonly int PrehashSize = 64;
        public static readonly int PublicKeySize = PointBytes;
        public static readonly int SecretKeySize = 32;
        public static readonly int SignatureSize = PointBytes + ScalarBytes;

        // "SigEd25519 no Ed25519 collisions"
        private static readonly byte[] Dom2Prefix = new byte[]{ 0x53, 0x69, 0x67, 0x45, 0x64, 0x32, 0x35, 0x35, 0x31,
            0x39, 0x20, 0x6e, 0x6f, 0x20, 0x45, 0x64, 0x32, 0x35, 0x35, 0x31, 0x39, 0x20, 0x63, 0x6f, 0x6c, 0x6c, 0x69,
            0x73, 0x69, 0x6f, 0x6e, 0x73 };

        private static readonly uint[] P = { 0xFFFFFFEDU, 0xFFFFFFFFU, 0xFFFFFFFFU, 0xFFFFFFFFU, 0xFFFFFFFFU,
            0xFFFFFFFFU, 0xFFFFFFFFU, 0x7FFFFFFFU };
        private static readonly uint[] L = { 0x5CF5D3EDU, 0x5812631AU, 0xA2F79CD6U, 0x14DEF9DEU, 0x00000000U,
            0x00000000U, 0x00000000U, 0x10000000U };

        private const int L0 = -0x030A2C13;     // L0:26/--
        private const int L1 =  0x012631A6;     // L1:24/22
        private const int L2 =  0x079CD658;     // L2:27/--
        private const int L3 = -0x006215D1;     // L3:23/--
        private const int L4 =  0x000014DF;     // L4:12/11

        private static readonly uint[] Order8_y1 = { 0x706A17C7, 0x4FD84D3D, 0x760B3CBA, 0x0F67100D, 0xFA53202A,
            0xC6CC392C, 0x77FDC74E, 0x7A03AC92 };
        private static readonly uint[] Order8_y2 = { 0x8F95E826, 0xB027B2C2, 0x89F4C345, 0xF098EFF2, 0x05ACDFD5,
            0x3933C6D3, 0x880238B1, 0x05FC536D };

        private static readonly int[] B_x = { 0x0325D51A, 0x018B5823, 0x007B2C95, 0x0304A92D, 0x00D2598E, 0x01D6DC5C,
            0x01388C7F, 0x013FEC0A, 0x029E6B72, 0x0042D26D };
        private static readonly int[] B_y = { 0x02666658, 0x01999999, 0x00666666, 0x03333333, 0x00CCCCCC, 0x02666666,
            0x01999999, 0x00666666, 0x03333333, 0x00CCCCCC, };

        // 2^128 * B
        private static readonly int[] B128_x = { 0x00B7E824, 0x0011EB98, 0x003E5FC8, 0x024E1739, 0x0131CD0B, 0x014E29A0,
            0x034E6138, 0x0132C952, 0x03F9E22F, 0x00984F5F };
        private static readonly int[] B128_y = { 0x03F5A66B, 0x02AF4452, 0x0049E5BB, 0x00F28D26, 0x0121A17C, 0x02C29C3A,
            0x0047AD89, 0x0087D95F, 0x0332936E, 0x00BE5933 };

        // Note that d == -121665/121666
        private static readonly int[] C_d = { 0x035978A3, 0x02D37284, 0x018AB75E, 0x026A0A0E, 0x0000E014, 0x0379E898,
            0x01D01E5D, 0x01E738CC, 0x03715B7F, 0x00A406D9 };
        private static readonly int[] C_d2 = { 0x02B2F159, 0x01A6E509, 0x01156EBD, 0x00D4141D, 0x0001C029, 0x02F3D130,
            0x03A03CBB, 0x01CE7198, 0x02E2B6FF, 0x00480DB3 };
        private static readonly int[] C_d4 = { 0x0165E2B2, 0x034DCA13, 0x002ADD7A, 0x01A8283B, 0x00038052, 0x01E7A260,
            0x03407977, 0x019CE331, 0x01C56DFF, 0x00901B67 };

        private const int WnafWidth = 5;
        private const int WnafWidthBase = 7;

        // ScalarMultBase is hard-coded for these values of blocks, teeth, spacing so they can't be freely changed
        private const int PrecompBlocks = 8;
        private const int PrecompTeeth = 4;
        private const int PrecompSpacing = 8;
        //private const int PrecompRange = PrecompBlocks * PrecompTeeth * PrecompSpacing; // range == 256
        private const int PrecompPoints = 1 << (PrecompTeeth - 1);
        private const int PrecompMask = PrecompPoints - 1;

        private static readonly object PrecompLock = new object();
        private static PointPrecomp[] PrecompBaseWnaf = null;
        private static PointPrecomp[] PrecompBase128Wnaf = null;
        private static int[] PrecompBaseComb = null;

        private struct PointAccum
        {
            internal int[] x, y, z, u, v;
        }

        private struct PointAffine
        {
            internal int[] x, y;
        }

        private struct PointExtended
        {
            internal int[] x, y, z, t;
        }

        private struct PointPrecomp
        {
            internal int[] ymx_h;       // (y - x)/2
            internal int[] ypx_h;       // (y + x)/2
            internal int[] xyd;         // x.y.d
        }

        private struct PointPrecompZ
        {
            internal int[] ymx_h;       // (y - x)/2
            internal int[] ypx_h;       // (y + x)/2
            internal int[] xyd;         // x.y.d
            internal int[] z;
        }

        // Temp space to avoid allocations in point formulae.
        private struct PointTemp
        {
            internal int[] r0, r1;
        }

        private static byte[] CalculateS(byte[] r, byte[] k, byte[] s)
        {
            uint[] t = new uint[ScalarUints * 2];   DecodeScalar(r, 0, t);
            uint[] u = new uint[ScalarUints];       DecodeScalar(k, 0, u);
            uint[] v = new uint[ScalarUints];       DecodeScalar(s, 0, v);

            Nat256.MulAddTo(u, v, t);

            byte[] result = new byte[ScalarBytes * 2];
            for (int i = 0; i < t.Length; ++i)
            {
                Codec.Encode32(t[i], result, i * 4);
            }
            return ReduceScalar(result);
        }

        private static bool CheckContextVar(byte[] ctx, byte phflag)
        {
            return ctx == null && phflag == 0x00
                || ctx != null && ctx.Length < 256;
        }

        private static int CheckPoint(int[] x, int[] y)
        {
            int[] t = F.Create();
            int[] u = F.Create();
            int[] v = F.Create();

            F.Sqr(x, u);
            F.Sqr(y, v);
            F.Mul(u, v, t);
            F.Sub(v, u, v);
            F.Mul(t, C_d, t);
            F.AddOne(t);
            F.Sub(t, v, t);
            F.Normalize(t);

            return F.IsZero(t);
        }

        private static int CheckPoint(int[] x, int[] y, int[] z)
        {
            int[] t = F.Create();
            int[] u = F.Create();
            int[] v = F.Create();
            int[] w = F.Create();

            F.Sqr(x, u);
            F.Sqr(y, v);
            F.Sqr(z, w);
            F.Mul(u, v, t);
            F.Sub(v, u, v);
            F.Mul(v, w, v);
            F.Sqr(w, w);
            F.Mul(t, C_d, t);
            F.Add(t, w, t);
            F.Sub(t, v, t);
            F.Normalize(t);

            return F.IsZero(t);
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static bool CheckPointVar(ReadOnlySpan<byte> p)
        {
            if ((Codec.Decode32(p[28..]) & 0x7FFFFFFFU) < P[7])
                return true;
            for (int i = CoordUints - 2; i >= 0; --i)
            {
                if (Codec.Decode32(p[(i * 4)..]) < P[i])
                    return true;
            }
            return false;
        }
#else
        private static bool CheckPointVar(byte[] p)
        {
            if ((Codec.Decode32(p, 28) & 0x7FFFFFFFU) < P[7])
                return true;
            for (int i = CoordUints - 2; i >= 0; --i)
            {
                if (Codec.Decode32(p, i * 4) < P[i])
                    return true;
            }
            return false;
        }
#endif

        private static bool CheckPointFullVar(byte[] p)
        {
            uint y7 = Codec.Decode32(p, 28) & 0x7FFFFFFFU;

            uint t0 = y7;
            uint t1 = y7 ^ P[7];
            uint t2 = y7 ^ Order8_y1[7];
            uint t3 = y7 ^ Order8_y2[7];

            for (int i = CoordUints - 2; i > 0; --i)
            {
                uint yi = Codec.Decode32(p, i * 4);

                t0 |= yi;
                t1 |= yi ^ P[i];
                t2 |= yi ^ Order8_y1[i];
                t3 |= yi ^ Order8_y2[i];
            }

            uint y0 = Codec.Decode32(p, 0);

            // Reject 0 and 1
            if (t0 == 0 && y0 <= 1U)
                return false;

            // Reject P - 1 and non-canonical encodings (i.e. >= P)
            if (t1 == 0 && y0 >= (P[0] - 1U))
                return false;

            t2 |= y0 ^ Order8_y1[0];
            t3 |= y0 ^ Order8_y2[0];

            // Reject order 8 points
            return (t2 != 0) & (t3 != 0);
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static bool CheckScalarVar(ReadOnlySpan<byte> s, Span<uint> n)
        {
            DecodeScalar(s, n);
            return !Nat.Gte(ScalarUints, n, L);
        }
#else
        private static bool CheckScalarVar(byte[] s, uint[] n)
        {
            DecodeScalar(s, 0, n);
            return !Nat256.Gte(n, L);
        }
#endif

        private static byte[] Copy(byte[] buf, int off, int len)
        {
            byte[] result = new byte[len];
            Array.Copy(buf, off, result, 0, len);
            return result;
        }

        private static IDigest CreateDigest()
        {
            var d = new Sha512Digest();
            if (d.GetDigestSize() != 64)
                throw new InvalidOperationException();
            return d;
        }

        public static IDigest CreatePrehash()
        {
            return CreateDigest();
        }

        private static bool DecodePointVar(byte[] p, int pOff, bool negate, ref PointAffine r)
        {
            byte[] py = Copy(p, pOff, PointBytes);
            if (!CheckPointFullVar(py))
                return false;

            int x_0 = (py[PointBytes - 1] & 0x80) >> 7;
            py[PointBytes - 1] &= 0x7F;

            F.Decode(py, 0, r.y);

            int[] u = F.Create();
            int[] v = F.Create();

            F.Sqr(r.y, u);
            F.Mul(C_d, u, v);
            F.SubOne(u);
            F.AddOne(v);

            if (!F.SqrtRatioVar(u, v, r.x))
                return false;

            F.Normalize(r.x);
            if (x_0 == 1 && F.IsZeroVar(r.x))
                return false;

            if (negate ^ (x_0 != (r.x[0] & 1)))
            {
                F.Negate(r.x, r.x);
            }

            return true;
        }

        private static void DecodeScalar(byte[] k, int kOff, uint[] n)
        {
            Codec.Decode32(k, kOff, n, 0, ScalarUints);
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static void DecodeScalar(ReadOnlySpan<byte> k, Span<uint> n)
        {
            Codec.Decode32(k, n[..ScalarUints]);
        }
#endif

        private static void Dom2(IDigest d, byte phflag, byte[] ctx)
        {
            Debug.Assert(ctx != null);

            int n = Dom2Prefix.Length;

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            Span<byte> t = stackalloc byte[n + 2 + ctx.Length];
            Dom2Prefix.CopyTo(t);
            t[n] = phflag;
            t[n + 1] = (byte)ctx.Length;
            ctx.CopyTo(t.Slice(n + 2));

            d.BlockUpdate(t);
#else
            byte[] t = new byte[n + 2 + ctx.Length];
            Dom2Prefix.CopyTo(t, 0);
            t[n] = phflag;
            t[n + 1] = (byte)ctx.Length;
            ctx.CopyTo(t, n + 2);

            d.BlockUpdate(t, 0, t.Length);
#endif
        }

        private static int EncodePoint(ref PointAccum p, byte[] r, int rOff)
        {
#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            return EncodePoint(ref p, r.AsSpan(rOff));
#else
            int[] x = F.Create();
            int[] y = F.Create();

            F.Inv(p.z, y);
            F.Mul(p.x, y, x);
            F.Mul(p.y, y, y);
            F.Normalize(x);
            F.Normalize(y);

            int result = CheckPoint(x, y);

            F.Encode(y, r, rOff);
            r[rOff + PointBytes - 1] |= (byte)((x[0] & 1) << 7);

            return result;
#endif
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static int EncodePoint(ref PointAccum p, Span<byte> r)
        {
            int[] x = F.Create();
            int[] y = F.Create();

            F.Inv(p.z, y);
            F.Mul(p.x, y, x);
            F.Mul(p.y, y, y);
            F.Normalize(x);
            F.Normalize(y);

            int result = CheckPoint(x, y);

            F.Encode(y, r);
            r[PointBytes - 1] |= (byte)((x[0] & 1) << 7);

            return result;
        }
#endif

        public static void GeneratePrivateKey(SecureRandom random, byte[] k)
        {
            if (k.Length != SecretKeySize)
                throw new ArgumentException(nameof(k));

            random.NextBytes(k);
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        public static void GeneratePrivateKey(SecureRandom random, Span<byte> k)
        {
            if (k.Length != SecretKeySize)
                throw new ArgumentException(nameof(k));

            random.NextBytes(k);
        }
#endif

        public static void GeneratePublicKey(byte[] sk, int skOff, byte[] pk, int pkOff)
        {
#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            GeneratePublicKey(sk.AsSpan(skOff), pk.AsSpan(pkOff));
#else
            IDigest d = CreateDigest();
            byte[] h = new byte[64];

            d.BlockUpdate(sk, skOff, SecretKeySize);
            d.DoFinal(h, 0);

            byte[] s = new byte[ScalarBytes];
            PruneScalar(h, 0, s);

            ScalarMultBaseEncoded(s, pk, pkOff);
#endif
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        public static void GeneratePublicKey(ReadOnlySpan<byte> sk, Span<byte> pk)
        {
            IDigest d = CreateDigest();
            Span<byte> h = stackalloc byte[64];

            d.BlockUpdate(sk[..SecretKeySize]);
            d.DoFinal(h);

            Span<byte> s = stackalloc byte[ScalarBytes];
            PruneScalar(h, s);

            ScalarMultBaseEncoded(s, pk);
        }
#endif

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static uint GetWindow4(ReadOnlySpan<uint> x, int n)
#else
        private static uint GetWindow4(uint[] x, int n)
#endif
        {
            int w = (int)((uint)n >> 3), b = (n & 7) << 2;
            return (x[w] >> b) & 15U;
        }

        private static void ImplSign(IDigest d, byte[] h, byte[] s, byte[] pk, int pkOff, byte[] ctx, byte phflag,
            byte[] m, int mOff, int mLen, byte[] sig, int sigOff)
        {
            if (ctx != null)
            {
                Dom2(d, phflag, ctx);
            }
            d.BlockUpdate(h, ScalarBytes, ScalarBytes);
            d.BlockUpdate(m, mOff, mLen);
            d.DoFinal(h, 0);

            byte[] r = ReduceScalar(h);
            byte[] R = new byte[PointBytes];
            ScalarMultBaseEncoded(r, R, 0);

            if (ctx != null)
            {
                Dom2(d, phflag, ctx);
            }
            d.BlockUpdate(R, 0, PointBytes);
            d.BlockUpdate(pk, pkOff, PointBytes);
            d.BlockUpdate(m, mOff, mLen);
            d.DoFinal(h, 0);

            byte[] k = ReduceScalar(h);
            byte[] S = CalculateS(r, k, s);

            Array.Copy(R, 0, sig, sigOff, PointBytes);
            Array.Copy(S, 0, sig, sigOff + PointBytes, ScalarBytes);
        }

        private static void ImplSign(byte[] sk, int skOff, byte[] ctx, byte phflag, byte[] m, int mOff, int mLen,
            byte[] sig, int sigOff)
        {
            if (!CheckContextVar(ctx, phflag))
                throw new ArgumentException("ctx");

            IDigest d = CreateDigest();
            byte[] h = new byte[64];

            d.BlockUpdate(sk, skOff, SecretKeySize);
            d.DoFinal(h, 0);

            byte[] s = new byte[ScalarBytes];
            PruneScalar(h, 0, s);

            byte[] pk = new byte[PointBytes];
            ScalarMultBaseEncoded(s, pk, 0);

            ImplSign(d, h, s, pk, 0, ctx, phflag, m, mOff, mLen, sig, sigOff);
        }

        private static void ImplSign(byte[] sk, int skOff, byte[] pk, int pkOff, byte[] ctx, byte phflag, byte[] m,
            int mOff, int mLen, byte[] sig, int sigOff)
        {
            if (!CheckContextVar(ctx, phflag))
                throw new ArgumentException("ctx");

            IDigest d = CreateDigest();
            byte[] h = new byte[64];

            d.BlockUpdate(sk, skOff, SecretKeySize);
            d.DoFinal(h, 0);

            byte[] s = new byte[ScalarBytes];
            PruneScalar(h, 0, s);

            ImplSign(d, h, s, pk, pkOff, ctx, phflag, m, mOff, mLen, sig, sigOff);
        }

        private static bool ImplVerify(byte[] sig, int sigOff, byte[] pk, int pkOff, byte[] ctx, byte phflag, byte[] m,
            int mOff, int mLen)
        {
            if (!CheckContextVar(ctx, phflag))
                throw new ArgumentException("ctx");

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            Span<byte> RS = stackalloc byte[PointBytes + ScalarBytes];
            RS.CopyFrom(sig.AsSpan(sigOff, PointBytes + ScalarBytes));

            var R = RS[..PointBytes];
            var S = RS[PointBytes..];

            if (!CheckPointVar(R))
                return false;

            Span<uint> nS = stackalloc uint[ScalarUints];
            if (!CheckScalarVar(S, nS))
                return false;

            Init(out PointAffine pA);
            if (!DecodePointVar(pk, pkOff, true, ref pA))
                return false;

            IDigest d = CreateDigest();
            Span<byte> h = stackalloc byte[64];

            if (ctx != null)
            {
                Dom2(d, phflag, ctx);
            }
            d.BlockUpdate(R);
            d.BlockUpdate(pk.AsSpan(pkOff, PointBytes));
            d.BlockUpdate(m.AsSpan(mOff, mLen));
            d.DoFinal(h);

            Span<byte> k = stackalloc byte[ScalarBytes];
            ReduceScalar(h, k);

            Span<uint> nA = stackalloc uint[ScalarUints];
            DecodeScalar(k, nA);

            Init(out PointAccum pR);
            ScalarMultStrausVar(nS, nA, ref pA, ref pR);

            Span<byte> check = stackalloc byte[PointBytes];
            return 0 != EncodePoint(ref pR, check) && check.SequenceEqual(R);
#else
            byte[] R = Copy(sig, sigOff, PointBytes);
            byte[] S = Copy(sig, sigOff + PointBytes, ScalarBytes);

            if (!CheckPointVar(R))
                return false;

            uint[] nS = new uint[ScalarUints];
            if (!CheckScalarVar(S, nS))
                return false;

            Init(out PointAffine pA);
            if (!DecodePointVar(pk, pkOff, true, ref pA))
                return false;

            IDigest d = CreateDigest();
            byte[] h = new byte[64];

            if (ctx != null)
            {
                Dom2(d, phflag, ctx);
            }
            d.BlockUpdate(R, 0, PointBytes);
            d.BlockUpdate(pk, pkOff, PointBytes);
            d.BlockUpdate(m, mOff, mLen);
            d.DoFinal(h, 0);

            byte[] k = ReduceScalar(h);

            uint[] nA = new uint[ScalarUints];
            DecodeScalar(k, 0, nA);

            Init(out PointAccum pR);
            ScalarMultStrausVar(nS, nA, ref pA, ref pR);

            byte[] check = new byte[PointBytes];
            return 0 != EncodePoint(ref pR, check, 0) && Arrays.AreEqual(check, R);
#endif
        }

        private static void Init(out PointAccum r)
        {
            r.x = F.Create();
            r.y = F.Create();
            r.z = F.Create();
            r.u = F.Create();
            r.v = F.Create();
        }

        private static void Init(out PointAffine r)
        {
            r.x = F.Create();
            r.y = F.Create();
        }

        private static void Init(out PointExtended r)
        {
            r.x = F.Create();
            r.y = F.Create();
            r.z = F.Create();
            r.t = F.Create();
        }

        private static void Init(out PointPrecomp r)
        {
            r.ymx_h = F.Create();
            r.ypx_h = F.Create();
            r.xyd = F.Create();
        }

        private static void Init(out PointPrecompZ r)
        {
            r.ymx_h = F.Create();
            r.ypx_h = F.Create();
            r.xyd = F.Create();
            r.z = F.Create();
        }

        private static void Init(out PointTemp r)
        {
            r.r0 = F.Create();
            r.r1 = F.Create();
        }

        private static void InvertDoubleZs(PointExtended[] points)
        {
            int count = points.Length;
            int[] cs = F.CreateTable(count);

            int[] u = F.Create();
            F.Copy(points[0].z, 0, u, 0);
            F.Copy(u, 0, cs, 0);

            int i = 0;
            while (++i < count)
            {
                F.Mul(u, points[i].z, u);
                F.Copy(u, 0, cs, i * F.Size);
            }

            F.Add(u, u, u);
            F.InvVar(u, u);
            --i;

            int[] t = F.Create();

            while (i > 0)
            {
                int j = i--;
                F.Copy(cs, i * F.Size, t, 0);
                F.Mul(t, u, t);
                F.Mul(u, points[j].z, u);
                F.Copy(t, 0, points[j].z, 0);
            }

            F.Copy(u, 0, points[0].z, 0);
        }

        private static bool IsNeutralElementVar(int[] x, int[] y)
        {
            return F.IsZeroVar(x) && F.IsOneVar(y);
        }

        private static bool IsNeutralElementVar(int[] x, int[] y, int[] z)
        {
            return F.IsZeroVar(x) && F.AreEqualVar(y, z);
        }

        private static void PointAdd(ref PointExtended p, ref PointExtended q, ref PointExtended r, ref PointTemp t)
        {
            // p may ref the same point as r (or q), but q may not ref the same point as r.
            Debug.Assert(q.x != r.x & q.y != r.y && q.z != r.z && q.t != r.t);

            int[] a = r.x;
            int[] b = r.y;
            int[] c = t.r0;
            int[] d = t.r1;
            int[] e = a;
            int[] f = c;
            int[] g = d;
            int[] h = b;

            F.Apm(p.y, p.x, b, a);
            F.Apm(q.y, q.x, d, c);
            F.Mul(a, c, a);
            F.Mul(b, d, b);
            F.Mul(p.t, q.t, c);
            F.Mul(c, C_d2, c);
            F.Add(p.z, p.z, d);
            F.Mul(d, q.z, d);
            F.Apm(b, a, h, e);
            F.Apm(d, c, g, f);
            F.Mul(e, h, r.t);
            F.Mul(f, g, r.z);
            F.Mul(e, f, r.x);
            F.Mul(h, g, r.y);
        }

        private static void PointAdd(ref PointPrecomp p, ref PointAccum r, ref PointTemp t)
        {
            int[] a = r.x;
            int[] b = r.y;
            int[] c = t.r0;
            int[] e = r.u;
            int[] f = a;
            int[] g = b;
            int[] h = r.v;

            F.Apm(r.y, r.x, b, a);
            F.Mul(a, p.ymx_h, a);
            F.Mul(b, p.ypx_h, b);
            F.Mul(r.u, r.v, c);
            F.Mul(c, p.xyd, c);
            F.Apm(b, a, h, e);
            F.Apm(r.z, c, g, f);
            F.Mul(f, g, r.z);
            F.Mul(f, e, r.x);
            F.Mul(g, h, r.y);
        }

        private static void PointAdd(ref PointPrecompZ p, ref PointAccum r, ref PointTemp t)
        {
            int[] a = r.x;
            int[] b = r.y;
            int[] c = t.r0;
            int[] d = r.z;
            int[] e = r.u;
            int[] f = a;
            int[] g = b;
            int[] h = r.v;

            F.Apm(r.y, r.x, b, a);
            F.Mul(a, p.ymx_h, a);
            F.Mul(b, p.ypx_h, b);
            F.Mul(r.u, r.v, c);
            F.Mul(c, p.xyd, c);
            F.Mul(r.z, p.z, d);
            F.Apm(b, a, h, e);
            F.Apm(d, c, g, f);
            F.Mul(f, g, r.z);
            F.Mul(f, e, r.x);
            F.Mul(g, h, r.y);
        }

        private static void PointAddVar(bool negate, ref PointPrecomp p, ref PointAccum r, ref PointTemp t)
        {
            int[] a = r.x;
            int[] b = r.y;
            int[] c = t.r0;
            int[] e = r.u;
            int[] f = a;
            int[] g = b;
            int[] h = r.v;

            int[] na, nb;
            if (negate)
            {
                na = b; nb = a;
            }
            else
            {
                na = a; nb = b;
            }
            int[] nf = na, ng = nb;

            F.Apm(r.y, r.x, b, a);
            F.Mul(na, p.ymx_h, na);
            F.Mul(nb, p.ypx_h, nb);
            F.Mul(r.u, r.v, c);
            F.Mul(c, p.xyd, c);
            F.Apm(b, a, h, e);
            F.Apm(r.z, c, ng, nf);
            F.Mul(f, g, r.z);
            F.Mul(f, e, r.x);
            F.Mul(g, h, r.y);
        }

        private static void PointAddVar(bool negate, ref PointPrecompZ p, ref PointAccum r, ref PointTemp t)
        {
            int[] a = r.x;
            int[] b = r.y;
            int[] c = t.r0;
            int[] d = r.z;
            int[] e = r.u;
            int[] f = a;
            int[] g = b;
            int[] h = r.v;

            int[] na, nb;
            if (negate)
            {
                na = b; nb = a;
            }
            else
            {
                na = a; nb = b;
            }
            int[] nf = na, ng = nb;

            F.Apm(r.y, r.x, b, a);
            F.Mul(na, p.ymx_h, na);
            F.Mul(nb, p.ypx_h, nb);
            F.Mul(r.u, r.v, c);
            F.Mul(c, p.xyd, c);
            F.Mul(r.z, p.z, d);
            F.Apm(b, a, h, e);
            F.Apm(d, c, ng, nf);
            F.Mul(f, g, r.z);
            F.Mul(f, e, r.x);
            F.Mul(g, h, r.y);
        }

        private static void PointCopy(ref PointAccum p, ref PointExtended r)
        {
            F.Copy(p.x, 0, r.x, 0);
            F.Copy(p.y, 0, r.y, 0);
            F.Copy(p.z, 0, r.z, 0);
            F.Mul(p.u, p.v, r.t);
        }

        private static void PointCopy(ref PointAffine p, ref PointExtended r)
        {
            F.Copy(p.x, 0, r.x, 0);
            F.Copy(p.y, 0, r.y, 0);
            F.One(r.z);
            F.Mul(p.x, p.y, r.t);
        }

        private static void PointCopy(ref PointExtended p, ref PointPrecompZ r)
        {
            // To avoid halving x and y, we double t and z instead.
            F.Apm(p.y, p.x, r.ypx_h, r.ymx_h);
            F.Mul(p.t, C_d2, r.xyd);
            F.Add(p.z, p.z, r.z);
        }

        private static void PointDouble(ref PointAccum r)
        {
            int[] a = r.x;
            int[] b = r.y;
            int[] c = r.z;
            int[] e = r.u;
            int[] f = a;
            int[] g = b;
            int[] h = r.v;

            F.Add(r.x, r.y, e);
            F.Sqr(r.x, a);
            F.Sqr(r.y, b);
            F.Sqr(r.z, c);
            F.Add(c, c, c);
            F.Apm(a, b, h, g);
            F.Sqr(e, e);
            F.Sub(h, e, e);
            F.Add(c, g, f);
            F.Carry(f); // Probably unnecessary, but keep until better bounds analysis available
            F.Mul(f, g, r.z);
            F.Mul(f, e, r.x);
            F.Mul(g, h, r.y);
        }

        private static void PointLookup(int block, int index, ref PointPrecomp p)
        {
            Debug.Assert(0 <= block && block < PrecompBlocks);
            Debug.Assert(0 <= index && index < PrecompPoints);

            int off = block * PrecompPoints * 3 * F.Size;

            for (int i = 0; i < PrecompPoints; ++i)
            {
                int cond = ((i ^ index) - 1) >> 31;
                F.CMov(cond, PrecompBaseComb, off, p.ymx_h, 0);     off += F.Size;
                F.CMov(cond, PrecompBaseComb, off, p.ypx_h, 0);     off += F.Size;
                F.CMov(cond, PrecompBaseComb, off, p.xyd  , 0);     off += F.Size;
            }
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static void PointLookupZ(ReadOnlySpan<uint> x, int n, ReadOnlySpan<int> table, ref PointPrecompZ r)
        {
            // TODO This method is currently hard-coded to 4-bit windows and 8 precomputed points

            uint w = GetWindow4(x, n);

            int sign = (int)(w >> (4 - 1)) ^ 1;
            int abs = ((int)w ^ -sign) & 7;

            Debug.Assert(sign == 0 || sign == 1);
            Debug.Assert(0 <= abs && abs < 8);

            for (int i = 0; i < 8; ++i)
            {
                int cond = ((i ^ abs) - 1) >> 31;
                F.CMov(cond, table, r.ymx_h);       table = table[F.Size..];
                F.CMov(cond, table, r.ypx_h);       table = table[F.Size..];
                F.CMov(cond, table, r.xyd);         table = table[F.Size..];
                F.CMov(cond, table, r.z);           table = table[F.Size..];
            }

            F.CSwap(sign, r.ymx_h, r.ypx_h);
            F.CNegate(sign, r.xyd);
        }
#else
        private static void PointLookupZ(uint[] x, int n, int[] table, ref PointPrecompZ r)
        {
            // TODO This method is currently hard-coded to 4-bit windows and 8 precomputed points

            uint w = GetWindow4(x, n);

            int sign = (int)(w >> (4 - 1)) ^ 1;
            int abs = ((int)w ^ -sign) & 7;

            Debug.Assert(sign == 0 || sign == 1);
            Debug.Assert(0 <= abs && abs < 8);

            for (int i = 0, off = 0; i < 8; ++i)
            {
                int cond = ((i ^ abs) - 1) >> 31;
                F.CMov(cond, table, off, r.ymx_h, 0);       off += F.Size;
                F.CMov(cond, table, off, r.ypx_h, 0);       off += F.Size;
                F.CMov(cond, table, off, r.xyd  , 0);       off += F.Size;
                F.CMov(cond, table, off, r.z    , 0);       off += F.Size;
            }

            F.CSwap(sign, r.ymx_h, r.ypx_h);
            F.CNegate(sign, r.xyd);
        }
#endif

        private static void PointPrecompute(ref PointAffine p, PointExtended[] points, int pointsOff, int pointsLen,
            ref PointTemp t)
        {
            Debug.Assert(pointsLen > 0);

            Init(out points[pointsOff]);
            PointCopy(ref p, ref points[pointsOff]);

            Init(out PointExtended d);
            PointAdd(ref points[pointsOff], ref points[pointsOff], ref d, ref t);

            for (int i = 1; i < pointsLen; ++i)
            {
                Init(out points[pointsOff + i]);
                PointAdd(ref points[pointsOff + i - 1], ref d, ref points[pointsOff + i], ref t);
            }
        }

        private static int[] PointPrecomputeZ(ref PointAffine p, int count, ref PointTemp t)
        {
            Debug.Assert(count > 0);

            Init(out PointExtended q);
            PointCopy(ref p, ref q);

            Init(out PointExtended d);
            PointAdd(ref q, ref q, ref d, ref t);

            Init(out PointPrecompZ r);
            int[] table = F.CreateTable(count * 4);
            int off = 0;

            int i = 0;
            for (;;)
            {
                PointCopy(ref q, ref r);

                F.Copy(r.ymx_h, 0, table, off);     off += F.Size;
                F.Copy(r.ypx_h, 0, table, off);     off += F.Size;
                F.Copy(r.xyd  , 0, table, off);     off += F.Size;
                F.Copy(r.z    , 0, table, off);     off += F.Size;

                if (++i == count)
                    break;

                PointAdd(ref q, ref d, ref q, ref t);
            }

            return table;
        }

        private static void PointPrecomputeZ(ref PointAffine p, PointPrecompZ[] points, int count, ref PointTemp t)
        {
            Debug.Assert(count > 0);

            Init(out PointExtended q);
            PointCopy(ref p, ref q);

            Init(out PointExtended d);
            PointAdd(ref q, ref q, ref d, ref t);

            int i = 0;
            for (;;)
            {
                ref PointPrecompZ r = ref points[i];
                Init(out r);
                PointCopy(ref q, ref r);

                if (++i == count)
                    break;

                PointAdd(ref q, ref d, ref q, ref t);
            }
        }

        private static void PointSetNeutral(ref PointAccum p)
        {
            F.Zero(p.x);
            F.One(p.y);
            F.One(p.z);
            F.Zero(p.u);
            F.One(p.v);
        }

        public static void Precompute()
        {
            lock (PrecompLock)
            {
                if (PrecompBaseComb != null)
                    return;

                int wnafPoints = 1 << (WnafWidthBase - 2);
                int combPoints = PrecompBlocks * PrecompPoints;
                int totalPoints = wnafPoints * 2 + combPoints;

                PointExtended[] points = new PointExtended[totalPoints];
                Init(out PointTemp t);

                Init(out PointAffine b);
                F.Copy(B_x, 0, b.x, 0);
                F.Copy(B_y, 0, b.y, 0);

                PointPrecompute(ref b, points, 0, wnafPoints, ref t);

                Init(out PointAffine b128);
                F.Copy(B128_x, 0, b128.x, 0);
                F.Copy(B128_y, 0, b128.y, 0);

                PointPrecompute(ref b128, points, wnafPoints, wnafPoints, ref t);

                Init(out PointAccum p);
                F.Copy(B_x, 0, p.x, 0);
                F.Copy(B_y, 0, p.y, 0);
                F.One(p.z);
                F.Copy(B_x, 0, p.u, 0);
                F.Copy(B_y, 0, p.v, 0);

                int pointsIndex = wnafPoints * 2;
                PointExtended[] toothPowers = new PointExtended[PrecompTeeth];
                for (int tooth = 0; tooth < PrecompTeeth; ++tooth)
                {
                    Init(out toothPowers[tooth]);
                }
                Init(out PointExtended u);
                for (int block = 0; block < PrecompBlocks; ++block)
                {
                    ref PointExtended sum = ref points[pointsIndex++];
                    Init(out sum);

                    for (int tooth = 0; tooth < PrecompTeeth; ++tooth)
                    {
                        if (tooth == 0)
                        {
                            PointCopy(ref p, ref sum);
                        }
                        else
                        {
                            PointCopy(ref p, ref u);
                            PointAdd(ref sum, ref u, ref sum, ref t);
                        }

                        PointDouble(ref p);
                        PointCopy(ref p, ref toothPowers[tooth]);

                        if (block + tooth != PrecompBlocks + PrecompTeeth - 2)
                        {
                            for (int spacing = 1; spacing < PrecompSpacing; ++spacing)
                            {
                                PointDouble(ref p);
                            }
                        }
                    }

                    F.Negate(sum.x, sum.x);
                    F.Negate(sum.t, sum.t);

                    for (int tooth = 0; tooth < (PrecompTeeth - 1); ++tooth)
                    {
                        int size = 1 << tooth;
                        for (int j = 0; j < size; ++j, ++pointsIndex)
                        {
                            Init(out points[pointsIndex]);
                            PointAdd(ref points[pointsIndex - size], ref toothPowers[tooth], ref points[pointsIndex],
                                ref t);
                        }
                    }
                }
                Debug.Assert(pointsIndex == totalPoints);

                // Set each z coordinate to 1/(2.z) to avoid calculating halves of x, y in the following code
                InvertDoubleZs(points);

                PrecompBaseWnaf = new PointPrecomp[wnafPoints];
                for (int i = 0; i < wnafPoints; ++i)
                {
                    ref PointExtended q = ref points[i];
                    ref PointPrecomp r = ref PrecompBaseWnaf[i];
                    Init(out r);

                    // Calculate x/2 and y/2 (because the z value holds half the inverse; see above).
                    F.Mul(q.x, q.z, q.x);
                    F.Mul(q.y, q.z, q.y);

                    // y/2 +/- x/2
                    F.Apm(q.y, q.x, r.ypx_h, r.ymx_h);

                    // x/2 * y/2 * (4.d) == x.y.d
                    F.Mul(q.x, q.y, r.xyd);
                    F.Mul(r.xyd, C_d4, r.xyd);

                    F.Normalize(r.ymx_h);
                    F.Normalize(r.ypx_h);
                    F.Normalize(r.xyd);
                }

                PrecompBase128Wnaf = new PointPrecomp[wnafPoints];
                for (int i = 0; i < wnafPoints; ++i)
                {
                    ref PointExtended q = ref points[wnafPoints + i];
                    ref PointPrecomp r = ref PrecompBase128Wnaf[i];
                    Init(out r);

                    // Calculate x/2 and y/2 (because the z value holds half the inverse; see above).
                    F.Mul(q.x, q.z, q.x);
                    F.Mul(q.y, q.z, q.y);

                    // y/2 +/- x/2
                    F.Apm(q.y, q.x, r.ypx_h, r.ymx_h);

                    // x/2 * y/2 * (4.d) == x.y.d
                    F.Mul(q.x, q.y, r.xyd);
                    F.Mul(r.xyd, C_d4, r.xyd);

                    F.Normalize(r.ymx_h);
                    F.Normalize(r.ypx_h);
                    F.Normalize(r.xyd);
                }

                PrecompBaseComb = F.CreateTable(combPoints * 3);
                Init(out PointPrecomp s);
                int off = 0;
                for (int i = wnafPoints * 2; i < totalPoints; ++i)
                {
                    ref PointExtended q = ref points[i];

                    // Calculate x/2 and y/2 (because the z value holds half the inverse; see above).
                    F.Mul(q.x, q.z, q.x);
                    F.Mul(q.y, q.z, q.y);

                    // y/2 +/- x/2
                    F.Apm(q.y, q.x, s.ypx_h, s.ymx_h);

                    // x/2 * y/2 * (4.d) == x.y.d
                    F.Mul(q.x, q.y, s.xyd);
                    F.Mul(s.xyd, C_d4, s.xyd);

                    F.Normalize(s.ymx_h);
                    F.Normalize(s.ypx_h);
                    F.Normalize(s.xyd);

                    F.Copy(s.ymx_h, 0, PrecompBaseComb, off);       off += F.Size;
                    F.Copy(s.ypx_h, 0, PrecompBaseComb, off);       off += F.Size;
                    F.Copy(s.xyd  , 0, PrecompBaseComb, off);       off += F.Size;
                }
                Debug.Assert(off == PrecompBaseComb.Length);
            }
        }

        private static void PruneScalar(byte[] n, int nOff, byte[] r)
        {
            Array.Copy(n, nOff, r, 0, ScalarBytes);

            r[0] &= 0xF8;
            r[ScalarBytes - 1] &= 0x7F;
            r[ScalarBytes - 1] |= 0x40;
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static void PruneScalar(ReadOnlySpan<byte> n, Span<byte> r)
        {
            n[..ScalarBytes].CopyTo(r);

            r[0] &= 0xF8;
            r[ScalarBytes - 1] &= 0x7F;
            r[ScalarBytes - 1] |= 0x40;
        }
#endif

        private static byte[] ReduceScalar(byte[] n)
        {
            byte[] r = new byte[ScalarBytes];

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            ReduceScalar(n, r);
#else
            long x00 =  Codec.Decode32(n,  0)       & M32L;         // x00:32/--
            long x01 = (Codec.Decode24(n,  4) << 4) & M32L;         // x01:28/--
            long x02 =  Codec.Decode32(n,  7)       & M32L;         // x02:32/--
            long x03 = (Codec.Decode24(n, 11) << 4) & M32L;         // x03:28/--
            long x04 =  Codec.Decode32(n, 14)       & M32L;         // x04:32/--
            long x05 = (Codec.Decode24(n, 18) << 4) & M32L;         // x05:28/--
            long x06 =  Codec.Decode32(n, 21)       & M32L;         // x06:32/--
            long x07 = (Codec.Decode24(n, 25) << 4) & M32L;         // x07:28/--
            long x08 =  Codec.Decode32(n, 28)       & M32L;         // x08:32/--
            long x09 = (Codec.Decode24(n, 32) << 4) & M32L;         // x09:28/--
            long x10 =  Codec.Decode32(n, 35)       & M32L;         // x10:32/--
            long x11 = (Codec.Decode24(n, 39) << 4) & M32L;         // x11:28/--
            long x12 =  Codec.Decode32(n, 42)       & M32L;         // x12:32/--
            long x13 = (Codec.Decode24(n, 46) << 4) & M32L;         // x13:28/--
            long x14 =  Codec.Decode32(n, 49)       & M32L;         // x14:32/--
            long x15 = (Codec.Decode24(n, 53) << 4) & M32L;         // x15:28/--
            long x16 =  Codec.Decode32(n, 56)       & M32L;         // x16:32/--
            long x17 = (Codec.Decode24(n, 60) << 4) & M32L;         // x17:28/--
            long x18 =                 n[63]        & M08L;         // x18:08/--
            long t;

            //x18 += (x17 >> 28); x17 &= M28L;
            x09 -= x18 * L0;                            // x09:34/28
            x10 -= x18 * L1;                            // x10:33/30
            x11 -= x18 * L2;                            // x11:35/28
            x12 -= x18 * L3;                            // x12:32/31
            x13 -= x18 * L4;                            // x13:28/21

            x17 += (x16 >> 28); x16 &= M28L;            // x17:28/--, x16:28/--
            x08 -= x17 * L0;                            // x08:54/32
            x09 -= x17 * L1;                            // x09:52/51
            x10 -= x17 * L2;                            // x10:55/34
            x11 -= x17 * L3;                            // x11:51/36
            x12 -= x17 * L4;                            // x12:41/--

            //x16 += (x15 >> 28); x15 &= M28L;
            x07 -= x16 * L0;                            // x07:54/28
            x08 -= x16 * L1;                            // x08:54/53
            x09 -= x16 * L2;                            // x09:55/53
            x10 -= x16 * L3;                            // x10:55/52
            x11 -= x16 * L4;                            // x11:51/41

            x15 += (x14 >> 28); x14 &= M28L;            // x15:28/--, x14:28/--
            x06 -= x15 * L0;                            // x06:54/32
            x07 -= x15 * L1;                            // x07:54/53
            x08 -= x15 * L2;                            // x08:56/--
            x09 -= x15 * L3;                            // x09:55/54
            x10 -= x15 * L4;                            // x10:55/53

            //x14 += (x13 >> 28); x13 &= M28L;
            x05 -= x14 * L0;                            // x05:54/28
            x06 -= x14 * L1;                            // x06:54/53
            x07 -= x14 * L2;                            // x07:56/--
            x08 -= x14 * L3;                            // x08:56/51
            x09 -= x14 * L4;                            // x09:56/--

            x13 += (x12 >> 28); x12 &= M28L;            // x13:28/22, x12:28/--
            x04 -= x13 * L0;                            // x04:54/49
            x05 -= x13 * L1;                            // x05:54/53
            x06 -= x13 * L2;                            // x06:56/--
            x07 -= x13 * L3;                            // x07:56/52
            x08 -= x13 * L4;                            // x08:56/52

            x12 += (x11 >> 28); x11 &= M28L;            // x12:28/24, x11:28/--
            x03 -= x12 * L0;                            // x03:54/49
            x04 -= x12 * L1;                            // x04:54/51
            x05 -= x12 * L2;                            // x05:56/--
            x06 -= x12 * L3;                            // x06:56/52
            x07 -= x12 * L4;                            // x07:56/53

            x11 += (x10 >> 28); x10 &= M28L;            // x11:29/--, x10:28/--
            x02 -= x11 * L0;                            // x02:55/32
            x03 -= x11 * L1;                            // x03:55/--
            x04 -= x11 * L2;                            // x04:56/55
            x05 -= x11 * L3;                            // x05:56/52
            x06 -= x11 * L4;                            // x06:56/53

            x10 += (x09 >> 28); x09 &= M28L;            // x10:29/--, x09:28/--
            x01 -= x10 * L0;                            // x01:55/28
            x02 -= x10 * L1;                            // x02:55/54
            x03 -= x10 * L2;                            // x03:56/55
            x04 -= x10 * L3;                            // x04:57/--
            x05 -= x10 * L4;                            // x05:56/53

            x08 += (x07 >> 28); x07 &= M28L;            // x08:56/53, x07:28/--
            x09 += (x08 >> 28); x08 &= M28L;            // x09:29/25, x08:28/--

            t    = (x08 >> 27) & 1L;
            x09 += t;                                   // x09:29/26

            x00 -= x09 * L0;                            // x00:55/53
            x01 -= x09 * L1;                            // x01:55/54
            x02 -= x09 * L2;                            // x02:57/--
            x03 -= x09 * L3;                            // x03:57/--
            x04 -= x09 * L4;                            // x04:57/42

            x01 += (x00 >> 28); x00 &= M28L;
            x02 += (x01 >> 28); x01 &= M28L;
            x03 += (x02 >> 28); x02 &= M28L;
            x04 += (x03 >> 28); x03 &= M28L;
            x05 += (x04 >> 28); x04 &= M28L;
            x06 += (x05 >> 28); x05 &= M28L;
            x07 += (x06 >> 28); x06 &= M28L;
            x08 += (x07 >> 28); x07 &= M28L;
            x09  = (x08 >> 28); x08 &= M28L;

            x09 -= t;

            Debug.Assert(x09 == 0L || x09 == -1L);

            x00 += x09 & L0;
            x01 += x09 & L1;
            x02 += x09 & L2;
            x03 += x09 & L3;
            x04 += x09 & L4;

            x01 += (x00 >> 28); x00 &= M28L;
            x02 += (x01 >> 28); x01 &= M28L;
            x03 += (x02 >> 28); x02 &= M28L;
            x04 += (x03 >> 28); x03 &= M28L;
            x05 += (x04 >> 28); x04 &= M28L;
            x06 += (x05 >> 28); x05 &= M28L;
            x07 += (x06 >> 28); x06 &= M28L;
            x08 += (x07 >> 28); x07 &= M28L;

            Codec.Encode56((ulong)(x00 | (x01 << 28)), r, 0);
            Codec.Encode56((ulong)(x02 | (x03 << 28)), r, 7);
            Codec.Encode56((ulong)(x04 | (x05 << 28)), r, 14);
            Codec.Encode56((ulong)(x06 | (x07 << 28)), r, 21);
            Codec.Encode32((uint)x08, r, 28);
#endif

            return r;
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static void ReduceScalar(ReadOnlySpan<byte> n, Span<byte> r)
        {
            long x00 =  Codec.Decode32(n[ 0..])       & M32L;       // x00:32/--
            long x01 = (Codec.Decode24(n[ 4..]) << 4) & M32L;       // x01:28/--
            long x02 =  Codec.Decode32(n[ 7..])       & M32L;       // x02:32/--
            long x03 = (Codec.Decode24(n[11..]) << 4) & M32L;       // x03:28/--
            long x04 =  Codec.Decode32(n[14..])       & M32L;       // x04:32/--
            long x05 = (Codec.Decode24(n[18..]) << 4) & M32L;       // x05:28/--
            long x06 =  Codec.Decode32(n[21..])       & M32L;       // x06:32/--
            long x07 = (Codec.Decode24(n[25..]) << 4) & M32L;       // x07:28/--
            long x08 =  Codec.Decode32(n[28..])       & M32L;       // x08:32/--
            long x09 = (Codec.Decode24(n[32..]) << 4) & M32L;       // x09:28/--
            long x10 =  Codec.Decode32(n[35..])       & M32L;       // x10:32/--
            long x11 = (Codec.Decode24(n[39..]) << 4) & M32L;       // x11:28/--
            long x12 =  Codec.Decode32(n[42..])       & M32L;       // x12:32/--
            long x13 = (Codec.Decode24(n[46..]) << 4) & M32L;       // x13:28/--
            long x14 =  Codec.Decode32(n[49..])       & M32L;       // x14:32/--
            long x15 = (Codec.Decode24(n[53..]) << 4) & M32L;       // x15:28/--
            long x16 =  Codec.Decode32(n[56..])       & M32L;       // x16:32/--
            long x17 = (Codec.Decode24(n[60..]) << 4) & M32L;       // x17:28/--
            long x18 =                 n[63]          & M08L;       // x18:08/--
            long t;

            //x18 += (x17 >> 28); x17 &= M28L;
            x09 -= x18 * L0;                            // x09:34/28
            x10 -= x18 * L1;                            // x10:33/30
            x11 -= x18 * L2;                            // x11:35/28
            x12 -= x18 * L3;                            // x12:32/31
            x13 -= x18 * L4;                            // x13:28/21

            x17 += (x16 >> 28); x16 &= M28L;            // x17:28/--, x16:28/--
            x08 -= x17 * L0;                            // x08:54/32
            x09 -= x17 * L1;                            // x09:52/51
            x10 -= x17 * L2;                            // x10:55/34
            x11 -= x17 * L3;                            // x11:51/36
            x12 -= x17 * L4;                            // x12:41/--

            //x16 += (x15 >> 28); x15 &= M28L;
            x07 -= x16 * L0;                            // x07:54/28
            x08 -= x16 * L1;                            // x08:54/53
            x09 -= x16 * L2;                            // x09:55/53
            x10 -= x16 * L3;                            // x10:55/52
            x11 -= x16 * L4;                            // x11:51/41

            x15 += (x14 >> 28); x14 &= M28L;            // x15:28/--, x14:28/--
            x06 -= x15 * L0;                            // x06:54/32
            x07 -= x15 * L1;                            // x07:54/53
            x08 -= x15 * L2;                            // x08:56/--
            x09 -= x15 * L3;                            // x09:55/54
            x10 -= x15 * L4;                            // x10:55/53

            //x14 += (x13 >> 28); x13 &= M28L;
            x05 -= x14 * L0;                            // x05:54/28
            x06 -= x14 * L1;                            // x06:54/53
            x07 -= x14 * L2;                            // x07:56/--
            x08 -= x14 * L3;                            // x08:56/51
            x09 -= x14 * L4;                            // x09:56/--

            x13 += (x12 >> 28); x12 &= M28L;            // x13:28/22, x12:28/--
            x04 -= x13 * L0;                            // x04:54/49
            x05 -= x13 * L1;                            // x05:54/53
            x06 -= x13 * L2;                            // x06:56/--
            x07 -= x13 * L3;                            // x07:56/52
            x08 -= x13 * L4;                            // x08:56/52

            x12 += (x11 >> 28); x11 &= M28L;            // x12:28/24, x11:28/--
            x03 -= x12 * L0;                            // x03:54/49
            x04 -= x12 * L1;                            // x04:54/51
            x05 -= x12 * L2;                            // x05:56/--
            x06 -= x12 * L3;                            // x06:56/52
            x07 -= x12 * L4;                            // x07:56/53

            x11 += (x10 >> 28); x10 &= M28L;            // x11:29/--, x10:28/--
            x02 -= x11 * L0;                            // x02:55/32
            x03 -= x11 * L1;                            // x03:55/--
            x04 -= x11 * L2;                            // x04:56/55
            x05 -= x11 * L3;                            // x05:56/52
            x06 -= x11 * L4;                            // x06:56/53

            x10 += (x09 >> 28); x09 &= M28L;            // x10:29/--, x09:28/--
            x01 -= x10 * L0;                            // x01:55/28
            x02 -= x10 * L1;                            // x02:55/54
            x03 -= x10 * L2;                            // x03:56/55
            x04 -= x10 * L3;                            // x04:57/--
            x05 -= x10 * L4;                            // x05:56/53

            x08 += (x07 >> 28); x07 &= M28L;            // x08:56/53, x07:28/--
            x09 += (x08 >> 28); x08 &= M28L;            // x09:29/25, x08:28/--

            t    = (x08 >> 27) & 1L;
            x09 += t;                                   // x09:29/26

            x00 -= x09 * L0;                            // x00:55/53
            x01 -= x09 * L1;                            // x01:55/54
            x02 -= x09 * L2;                            // x02:57/--
            x03 -= x09 * L3;                            // x03:57/--
            x04 -= x09 * L4;                            // x04:57/42

            x01 += (x00 >> 28); x00 &= M28L;
            x02 += (x01 >> 28); x01 &= M28L;
            x03 += (x02 >> 28); x02 &= M28L;
            x04 += (x03 >> 28); x03 &= M28L;
            x05 += (x04 >> 28); x04 &= M28L;
            x06 += (x05 >> 28); x05 &= M28L;
            x07 += (x06 >> 28); x06 &= M28L;
            x08 += (x07 >> 28); x07 &= M28L;
            x09  = (x08 >> 28); x08 &= M28L;

            x09 -= t;

            Debug.Assert(x09 == 0L || x09 == -1L);

            x00 += x09 & L0;
            x01 += x09 & L1;
            x02 += x09 & L2;
            x03 += x09 & L3;
            x04 += x09 & L4;

            x01 += (x00 >> 28); x00 &= M28L;
            x02 += (x01 >> 28); x01 &= M28L;
            x03 += (x02 >> 28); x02 &= M28L;
            x04 += (x03 >> 28); x03 &= M28L;
            x05 += (x04 >> 28); x04 &= M28L;
            x06 += (x05 >> 28); x05 &= M28L;
            x07 += (x06 >> 28); x06 &= M28L;
            x08 += (x07 >> 28); x07 &= M28L;

            Codec.Encode56((ulong)(x00 | (x01 << 28)), r);
            Codec.Encode56((ulong)(x02 | (x03 << 28)), r[7..]);
            Codec.Encode56((ulong)(x04 | (x05 << 28)), r[14..]);
            Codec.Encode56((ulong)(x06 | (x07 << 28)), r[21..]);
            Codec.Encode32((uint)x08, r[28..]);
        }
#endif

        private static void ScalarMult(byte[] k, ref PointAffine p, ref PointAccum r)
        {
#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            ScalarMult(k.AsSpan(), ref p, ref r);
#else
            uint[] n = new uint[ScalarUints];
            DecodeScalar(k, 0, n);

            // Recode the scalar into signed-digit form
            {
                uint c1 = Nat.CAdd(ScalarUints, ~(int)n[0] & 1, n, L, n);   Debug.Assert(c1 == 0U);
                uint c2 = Nat.ShiftDownBit(ScalarUints, n, 1U);             Debug.Assert(c2 == (1U << 31));
            }

            Init(out PointPrecompZ q);
            Init(out PointTemp t);
            int[] table = PointPrecomputeZ(ref p, 8, ref t);

            PointSetNeutral(ref r);

            int w = 63;
            for (;;)
            {
                PointLookupZ(n, w, table, ref q);
                PointAdd(ref q, ref r, ref t);

                if (--w < 0)
                    break;

                for (int i = 0; i < 4; ++i)
                {
                    PointDouble(ref r);
                }
            }
#endif
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static void ScalarMult(ReadOnlySpan<byte> k, ref PointAffine p, ref PointAccum r)
        {
            Span<uint> n = stackalloc uint[ScalarUints];
            DecodeScalar(k, n);

            // Recode the scalar into signed-digit form
            {
                uint c1 = Nat.CAdd(ScalarUints, ~(int)n[0] & 1, n, L, n);   Debug.Assert(c1 == 0U);
                uint c2 = Nat.ShiftDownBit(ScalarUints, n, 1U);             Debug.Assert(c2 == (1U << 31));
            }

            Init(out PointPrecompZ q);
            Init(out PointTemp t);
            int[] table = PointPrecomputeZ(ref p, 8, ref t);

            PointSetNeutral(ref r);

            int w = 63;
            for (;;)
            {
                PointLookupZ(n, w, table, ref q);
                PointAdd(ref q, ref r, ref t);

                if (--w < 0)
                    break;

                for (int i = 0; i < 4; ++i)
                {
                    PointDouble(ref r);
                }
            }
        }
#endif

        private static void ScalarMultBase(byte[] k, ref PointAccum r)
        {
#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            ScalarMultBase(k.AsSpan(), ref r);
#else
            // Equivalent (but much slower)
            //Init(out PointAffine p);
            //F.Copy(B_x, 0, p.x, 0);
            //F.Copy(B_y, 0, p.y, 0);
            //ScalarMult(k, ref p, ref r);

            Precompute();

            uint[] n = new uint[ScalarUints];
            DecodeScalar(k, 0, n);

            // Recode the scalar into signed-digit form, then group comb bits in each block
            {
                uint c1 = Nat.CAdd(ScalarUints, ~(int)n[0] & 1, n, L, n);   Debug.Assert(c1 == 0U);
                uint c2 = Nat.ShiftDownBit(ScalarUints, n, 1U);             Debug.Assert(c2 == (1U << 31));

                /*
                 * Because we are using 4 teeth and 8 spacing, each limb of n corresponds to one of the 8 blocks.
                 * Therefore we can efficiently group the bits for each comb position using a (double) shuffle. 
                 */
                for (int i = 0; i < ScalarUints; ++i)
                {
                    n[i] = Interleave.Shuffle2(n[i]);
                }
            }

            Init(out PointPrecomp p);
            Init(out PointTemp t);

            PointSetNeutral(ref r);
            int resultSign = 0;

            int cOff = (PrecompSpacing - 1) * PrecompTeeth;
            for (;;)
            {
                for (int b = 0; b < PrecompBlocks; ++b)
                {
                    uint w = n[b] >> cOff;
                    int sign = (int)(w >> (PrecompTeeth - 1)) & 1;
                    int abs = ((int)w ^ -sign) & PrecompMask;

                    Debug.Assert(sign == 0 || sign == 1);
                    Debug.Assert(0 <= abs && abs < PrecompPoints);

                    PointLookup(b, abs, ref p);

                    F.CNegate(resultSign ^ sign, r.x);
                    F.CNegate(resultSign ^ sign, r.u);
                    resultSign = sign;

                    PointAdd(ref p, ref r, ref t);
                }

                if ((cOff -= PrecompTeeth) < 0)
                    break;

                PointDouble(ref r);
            }

            F.CNegate(resultSign, r.x);
            F.CNegate(resultSign, r.u);
#endif
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static void ScalarMultBase(ReadOnlySpan<byte> k, ref PointAccum r)
        {
            // Equivalent (but much slower)
            //Init(out PointAffine p);
            //F.Copy(B_x, 0, p.x, 0);
            //F.Copy(B_y, 0, p.y, 0);
            //ScalarMult(k, ref p, ref r);

            Precompute();

            Span<uint> n = stackalloc uint[ScalarUints];
            DecodeScalar(k, n);

            // Recode the scalar into signed-digit form, then group comb bits in each block
            {
                uint c1 = Nat.CAdd(ScalarUints, ~(int)n[0] & 1, n, L, n);   Debug.Assert(c1 == 0U);
                uint c2 = Nat.ShiftDownBit(ScalarUints, n, 1U);             Debug.Assert(c2 == (1U << 31));

                /*
                 * Because we are using 4 teeth and 8 spacing, each limb of n corresponds to one of the 8 blocks.
                 * Therefore we can efficiently group the bits for each comb position using a (double) shuffle. 
                 */
                for (int i = 0; i < ScalarUints; ++i)
                {
                    n[i] = Interleave.Shuffle2(n[i]);
                }
            }

            Init(out PointPrecomp p);
            Init(out PointTemp t);

            PointSetNeutral(ref r);
            int resultSign = 0;

            int cOff = (PrecompSpacing - 1) * PrecompTeeth;
            for (;;)
            {
                for (int b = 0; b < PrecompBlocks; ++b)
                {
                    uint w = n[b] >> cOff;
                    int sign = (int)(w >> (PrecompTeeth - 1)) & 1;
                    int abs = ((int)w ^ -sign) & PrecompMask;

                    Debug.Assert(sign == 0 || sign == 1);
                    Debug.Assert(0 <= abs && abs < PrecompPoints);

                    PointLookup(b, abs, ref p);

                    F.CNegate(resultSign ^ sign, r.x);
                    F.CNegate(resultSign ^ sign, r.u);
                    resultSign = sign;

                    PointAdd(ref p, ref r, ref t);
                }

                if ((cOff -= PrecompTeeth) < 0)
                    break;

                PointDouble(ref r);
            }

            F.CNegate(resultSign, r.x);
            F.CNegate(resultSign, r.u);
        }
#endif

        private static void ScalarMultBaseEncoded(byte[] k, byte[] r, int rOff)
        {
#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            ScalarMultBaseEncoded(k.AsSpan(), r.AsSpan(rOff));
#else
            Init(out PointAccum p);
            ScalarMultBase(k, ref p);
            if (0 == EncodePoint(ref p, r, rOff))
                throw new InvalidOperationException();
#endif
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static void ScalarMultBaseEncoded(ReadOnlySpan<byte> k, Span<byte> r)
        {
            Init(out PointAccum p);
            ScalarMultBase(k, ref p);
            if (0 == EncodePoint(ref p, r))
                throw new InvalidOperationException();
        }
#endif

        internal static void ScalarMultBaseYZ(byte[] k, int kOff, int[] y, int[] z)
        {
#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            ScalarMultBaseYZ(k.AsSpan(kOff), y.AsSpan(), z.AsSpan());
#else
            byte[] n = new byte[ScalarBytes];
            PruneScalar(k, kOff, n);

            Init(out PointAccum p);
            ScalarMultBase(n, ref p);

            if (0 == CheckPoint(p.x, p.y, p.z))
                throw new InvalidOperationException();

            F.Copy(p.y, 0, y, 0);
            F.Copy(p.z, 0, z, 0);
#endif
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        internal static void ScalarMultBaseYZ(ReadOnlySpan<byte> k, Span<int> y, Span<int> z)
        {
            Span<byte> n = stackalloc byte[ScalarBytes];
            PruneScalar(k, n);

            Init(out PointAccum p);
            ScalarMultBase(n, ref p);

            if (0 == CheckPoint(p.x, p.y, p.z))
                throw new InvalidOperationException();

            F.Copy(p.y, y);
            F.Copy(p.z, z);
        }
#endif

        private static void ScalarMultOrderVar(ref PointAffine p, ref PointAccum r)
        {
#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            Span<sbyte> ws_p = stackalloc sbyte[253];
#else
            sbyte[] ws_p = new sbyte[253];
#endif
            Wnaf.GetSignedVar(L, WnafWidth, ws_p);

            int count = 1 << (WnafWidth - 2);
            PointPrecompZ[] tp = new PointPrecompZ[count];
            Init(out PointTemp t);
            PointPrecomputeZ(ref p, tp, count, ref t);

            PointSetNeutral(ref r);

            for (int bit = 252;;)
            {
                int wp = ws_p[bit];
                if (wp != 0)
                {
                    int index = (wp >> 1) ^ (wp >> 31);
                    PointAddVar(wp < 0, ref tp[index], ref r, ref t);
                }

                if (--bit < 0)
                    break;

                PointDouble(ref r);
            }
        }

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
        private static void ScalarMultStrausVar(ReadOnlySpan<uint> nb, ReadOnlySpan<uint> np, ref PointAffine p,
            ref PointAccum r)
#else
        private static void ScalarMultStrausVar(uint[] nb, uint[] np, ref PointAffine p, ref PointAccum r)
#endif
        {
            Debug.Assert(nb.Length == ScalarUints);
            Debug.Assert(nb[ScalarUints - 1] <= L[ScalarUints - 1]);

            Debug.Assert(np.Length == ScalarUints);
            Debug.Assert(np[ScalarUints - 1] <= L[ScalarUints - 1]);

            Precompute();

#if NETCOREAPP2_1_OR_GREATER || NETSTANDARD2_1_OR_GREATER
            Span<sbyte> ws_b = stackalloc sbyte[253];
            Span<sbyte> ws_p = stackalloc sbyte[253];
#else
            sbyte[] ws_b = new sbyte[253];
            sbyte[] ws_p = new sbyte[253];
#endif

            Wnaf.GetSignedVar(nb, WnafWidthBase, ws_b);
            Wnaf.GetSignedVar(np, WnafWidth, ws_p);

            int count = 1 << (WnafWidth - 2);
            PointPrecompZ[] tp = new PointPrecompZ[count];
            Init(out PointTemp t);
            PointPrecomputeZ(ref p, tp, count, ref t);

            PointSetNeutral(ref r);

            for (int bit = 252;;)
            {
                int wb = ws_b[bit];
                if (wb != 0)
                {
                    int index = (wb >> 1) ^ (wb >> 31);
                    PointAddVar(wb < 0, ref PrecompBaseWnaf[index], ref r, ref t);
                }

                int wp = ws_p[bit];
                if (wp != 0)
                {
                    int index = (wp >> 1) ^ (wp >> 31);
                    PointAddVar(wp < 0, ref tp[index], ref r, ref t);
                }

                if (--bit < 0)
                    break;

                PointDouble(ref r);
            }
        }

        public static void Sign(byte[] sk, int skOff, byte[] m, int mOff, int mLen, byte[] sig, int sigOff)
        {
            byte[] ctx = null;
            byte phflag = 0x00;

            ImplSign(sk, skOff, ctx, phflag, m, mOff, mLen, sig, sigOff);
        }

        public static void Sign(byte[] sk, int skOff, byte[] pk, int pkOff, byte[] m, int mOff, int mLen, byte[] sig, int sigOff)
        {
            byte[] ctx = null;
            byte phflag = 0x00;

            ImplSign(sk, skOff, pk, pkOff, ctx, phflag, m, mOff, mLen, sig, sigOff);
        }

        public static void Sign(byte[] sk, int skOff, byte[] ctx, byte[] m, int mOff, int mLen, byte[] sig, int sigOff)
        {
            byte phflag = 0x00;

            ImplSign(sk, skOff, ctx, phflag, m, mOff, mLen, sig, sigOff);
        }

        public static void Sign(byte[] sk, int skOff, byte[] pk, int pkOff, byte[] ctx, byte[] m, int mOff, int mLen, byte[] sig, int sigOff)
        {
            byte phflag = 0x00;

            ImplSign(sk, skOff, pk, pkOff, ctx, phflag, m, mOff, mLen, sig, sigOff);
        }

        public static void SignPrehash(byte[] sk, int skOff, byte[] ctx, byte[] ph, int phOff, byte[] sig, int sigOff)
        {
            byte phflag = 0x01;

            ImplSign(sk, skOff, ctx, phflag, ph, phOff, PrehashSize, sig, sigOff);
        }

        public static void SignPrehash(byte[] sk, int skOff, byte[] pk, int pkOff, byte[] ctx, byte[] ph, int phOff, byte[] sig, int sigOff)
        {
            byte phflag = 0x01;

            ImplSign(sk, skOff, pk, pkOff, ctx, phflag, ph, phOff, PrehashSize, sig, sigOff);
        }

        public static void SignPrehash(byte[] sk, int skOff, byte[] ctx, IDigest ph, byte[] sig, int sigOff)
        {
            byte[] m = new byte[PrehashSize];
            if (PrehashSize != ph.DoFinal(m, 0))
                throw new ArgumentException("ph");

            byte phflag = 0x01;

            ImplSign(sk, skOff, ctx, phflag, m, 0, m.Length, sig, sigOff);
        }

        public static void SignPrehash(byte[] sk, int skOff, byte[] pk, int pkOff, byte[] ctx, IDigest ph, byte[] sig, int sigOff)
        {
            byte[] m = new byte[PrehashSize];
            if (PrehashSize != ph.DoFinal(m, 0))
                throw new ArgumentException("ph");

            byte phflag = 0x01;

            ImplSign(sk, skOff, pk, pkOff, ctx, phflag, m, 0, m.Length, sig, sigOff);
        }

        public static bool ValidatePublicKeyFull(byte[] pk, int pkOff)
        {
            Init(out PointAffine p);
            if (!DecodePointVar(pk, pkOff, false, ref p))
                return false;

            Init(out PointAccum r);
            ScalarMultOrderVar(ref p, ref r);

            F.Normalize(r.x);
            F.Normalize(r.y);
            F.Normalize(r.z);

            return IsNeutralElementVar(r.x, r.y, r.z);
        }

        public static bool ValidatePublicKeyPartial(byte[] pk, int pkOff)
        {
            Init(out PointAffine p);
            return DecodePointVar(pk, pkOff, false, ref p);
        }

        public static bool Verify(byte[] sig, int sigOff, byte[] pk, int pkOff, byte[] m, int mOff, int mLen)
        {
            byte[] ctx = null;
            byte phflag = 0x00;

            return ImplVerify(sig, sigOff, pk, pkOff, ctx, phflag, m, mOff, mLen);
        }

        public static bool Verify(byte[] sig, int sigOff, byte[] pk, int pkOff, byte[] ctx, byte[] m, int mOff, int mLen)
        {
            byte phflag = 0x00;

            return ImplVerify(sig, sigOff, pk, pkOff, ctx, phflag, m, mOff, mLen);
        }

        public static bool VerifyPrehash(byte[] sig, int sigOff, byte[] pk, int pkOff, byte[] ctx, byte[] ph, int phOff)
        {
            byte phflag = 0x01;

            return ImplVerify(sig, sigOff, pk, pkOff, ctx, phflag, ph, phOff, PrehashSize);
        }

        public static bool VerifyPrehash(byte[] sig, int sigOff, byte[] pk, int pkOff, byte[] ctx, IDigest ph)
        {
            byte[] m = new byte[PrehashSize];
            if (PrehashSize != ph.DoFinal(m, 0))
                throw new ArgumentException("ph");

            byte phflag = 0x01;

            return ImplVerify(sig, sigOff, pk, pkOff, ctx, phflag, m, 0, m.Length);
        }
    }
}
