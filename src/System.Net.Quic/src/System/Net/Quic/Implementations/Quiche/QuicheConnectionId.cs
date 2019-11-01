// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Security.Cryptography;

namespace System.Net.Quic.Implementations.Quiche
{
    internal struct QuicheConnectionId : IEquatable<QuicheConnectionId>
    {
        private static readonly RNGCryptoServiceProvider _rng = new RNGCryptoServiceProvider();

        public ReadOnlyMemory<byte> Value { get; }

        public QuicheConnectionId(ReadOnlyMemory<byte> value)
        {
            Value = value;
        }

        public static QuicheConnectionId NewId()
        {
            byte[] buf = new byte[NativeMethods.QUICHE_MAX_CONN_ID_LEN];
            _rng.GetBytes(buf);
            return new QuicheConnectionId(buf);
        }

        private static char[] _hexChars = new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'a', 'b', 'c', 'd', 'e', 'f' };

        public override string ToString()
        {
            char[] chrs = new char[Value.Length * 2];
            for (int i = 0; i < Value.Length; i++)
            {
                int lo = Value.Span[i] & 0x0f;
                int hi = Value.Span[i] >> 4;
                chrs[i * 2] = _hexChars[hi];
                chrs[(i * 2) + 1] = _hexChars[lo];
            }
            return new string(chrs);
        }

        public bool Equals(QuicheConnectionId other)
        {
            return Value.Span.SequenceEqual(other.Value.Span);
        }

        public override bool Equals(object obj) => obj is QuicheConnectionId other && Equals(other);

        public override int GetHashCode()
        {
            int hash = 0;
            for (int i = 0; i < Value.Length; i++)
            {
                hash = HashCode.Combine(hash, Value.Span[i]);
            }
            return hash;
        }
    }
}
