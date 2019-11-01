// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Text;

namespace System.Net.Quic.Implementations.Quiche
{
    internal static class QuicheConfigFactory
    {
        internal static IntPtr CreateConfig(IList<SslApplicationProtocol> applicationProtocols)
        {
            if (applicationProtocols.Count == 0)
            {
                // TODO: Better exception
                throw new Exception("No application protocol specified");
            }

            // Construct application protocol list.
            // Build a single buffer that is a set of non-empty 8-bit length prefixed strings:
            // For example "\x08http/1.1\x08http/0.9"
            int totalLength = applicationProtocols.Count + applicationProtocols.Sum(p => p.Protocol.Length);

            // PERF: Could probably stackalloc this if it's small enough
            // I believe quiche copies the data out as soon as the function is called, so it's safe to do so.
            byte[] appProtocolsBuffer = new byte[totalLength];
            int offset = 0;
            foreach (SslApplicationProtocol protocol in applicationProtocols)
            {
                if (protocol.Protocol.Length > byte.MaxValue)
                {
                    // TODO: Does SslApplicationProtocol already enforce this? Seems like it should.
                    // TODO: Resource string
                    throw new InvalidOperationException($"Application Protocol value is too long: {protocol}");
                }

                appProtocolsBuffer[offset] = (byte)protocol.Protocol.Length;
                protocol.Protocol.CopyTo(appProtocolsBuffer.AsMemory(offset + 1));
                offset += protocol.Protocol.Length + 1;
            }

#if false
            Console.WriteLine("App protocol:");
            foreach (byte b in appProtocolsBuffer)
            {
                Console.Write($"{b:X2}");
            }
            Console.WriteLine();
#endif

            // This value will trigger QUIC version negotiation, apparently...
            // TODO: Understand how this works better
            // TODO: don't hardcode value
            IntPtr configHandle = NativeMethods.quiche_config_new(0xbabababa);

            // Disable certificate validation
            NativeMethods.quiche_config_verify_peer(configHandle, false);

            // Disable TLS grease, for now
            // TODO: Should we enable this?
            NativeMethods.quiche_config_grease(configHandle, false);

            // Set the value on the internal config struct
            int err = NativeMethods.quiche_config_set_application_protos(configHandle, appProtocolsBuffer, (UIntPtr)appProtocolsBuffer.Length);
            if (err != 0)
            {
                NativeMethods.quiche_config_free(configHandle);
                // TODO: Better exception
                throw new Exception($"Quiche error: {err}");
            }

            // Default values for various config limits
            NativeMethods.quiche_config_set_initial_max_data(configHandle, 10_000_000);
            NativeMethods.quiche_config_set_initial_max_stream_data_bidi_local(configHandle, 1_000_000);
            NativeMethods.quiche_config_set_initial_max_stream_data_uni(configHandle, 1_000_000);
            NativeMethods.quiche_config_set_initial_max_streams_bidi(configHandle, 100);
            NativeMethods.quiche_config_set_initial_max_streams_uni(configHandle, 100);

            return configHandle;
        }
    }
}
