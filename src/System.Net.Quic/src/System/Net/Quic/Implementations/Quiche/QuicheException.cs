// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.Serialization;

namespace System.Net.Quic.Implementations.Quiche
{

    [Serializable]
    internal class QuicheException : Exception
    {
        private QuicheException(QuicheErrorCode errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        protected QuicheException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
            ErrorCode = (QuicheErrorCode)info.GetInt32("ErrorCode");
        }

        public QuicheErrorCode ErrorCode { get; }

        public static QuicheException FromErrorCode(QuicheErrorCode errorCode)
        {
            return new QuicheException(errorCode, errorCode.GetDescription());
        }

        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue("ErrorCode", (int)ErrorCode);
        }

        public static void ThrowOnError(int result)
        {
            QuicheErrorCode errorCode = (QuicheErrorCode)result;
            if (errorCode < QuicheErrorCode.Done)
            {
                throw FromErrorCode(errorCode);
            }
        }
    }

    internal enum QuicheErrorCode : int
    {
        /// <summary>There is no more work to do.</summary>
        Done = -1,

        /// <summary>The provided buffer is too short.</summary>
        BufferTooShort = -2,

        /// <summary>The provided packet cannot be parsed because its version is unknown.</summary>
        UnknownVersion = -3,

        /// <summary>The provided packet cannot be parsed because it contains an invalid frame.</summary>
        InvalidFrame = -4,

        /// <summary>The provided packet cannot be parsed.</summary>
        InvalidPacket = -5,

        /// <summary>The operation cannot be completed because the connection is in an invalid state.</summary>
        InvalidState = -6,

        /// <summary>The operation cannot be completed because the stream is in an invalid state.</summary>
        InvalidStreamState = -7,

        /// <summary>The peer's transport params cannot be parsed.</summary>
        InvalidTransportParam = -8,

        /// <summary>A cryptographic operation failed.</summary>
        CryptoFail = -9,

        /// <summary>The TLS handshake failed.</summary>
        TlsFail = -10,

        /// <summary>The peer violated the local flow control limits.</summary>
        FlowControl = -11,

        /// <summary>The peer violated the local stream limits.</summary>
        StreamLimit = -12,

        /// <summary>The received data exceeds the stream's final size.</summary>
        FinalSize = -13,
    }

    internal static class QuicheErrorCodeExtensions
    {
        public static string GetDescription(this QuicheErrorCode errorCode)
        {
            return errorCode switch
            {
                QuicheErrorCode.Done => "There is no more work to do.",
                QuicheErrorCode.BufferTooShort => "The provided buffer is too short.",
                QuicheErrorCode.UnknownVersion => "The provided packet cannot be parsed because its version is unknown.",
                QuicheErrorCode.InvalidFrame => "The provided packet cannot be parsed because it contains an invalid frame.",
                QuicheErrorCode.InvalidPacket => "The provided packet cannot be parsed.",
                QuicheErrorCode.InvalidState => "The operation cannot be completed because the connection is in an invalid state.",
                QuicheErrorCode.InvalidStreamState => "The operation cannot be completed because the stream is in an invalid state.",
                QuicheErrorCode.InvalidTransportParam => "The peer's transport params cannot be parsed.",
                QuicheErrorCode.CryptoFail => "A cryptographic operation failed.",
                QuicheErrorCode.TlsFail => "The TLS handshake failed.",
                QuicheErrorCode.FlowControl => "The peer violated the local flow control limits.",
                QuicheErrorCode.StreamLimit => "The peer violated the local stream limits.",
                QuicheErrorCode.FinalSize => "The received data exceeds the stream's final size.",
                _ => $"Unknown Error (Error Code: {errorCode})"
            };
        }
    }
}
