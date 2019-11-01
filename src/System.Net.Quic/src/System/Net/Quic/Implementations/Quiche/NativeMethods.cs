// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Source: https://github.com/cloudflare/quiche/blob/d4e24ec88749629d15249f1e34bf95ae1b1b9f54/include/quiche.h

using System;
using System.Runtime.InteropServices;

// Ignore pinvoke whitelist for these APIs
#pragma warning disable BCL0015

namespace System.Net.Quic.Implementations.Quiche
{
    internal static class NativeMethods
    {
        private const string DllName = "quiche";

        public const uint QUICHE_PROTOCOL_VERSION = 0xff000017;
        public const uint QUICHE_MAX_CONN_ID_LEN = 20;
        public const uint QUICHE_MIN_CLIENT_INITIAL_LEN = 200;

        public const int QUICHE_MAX_DATAGRAM_SIZE = 1350;

        [DllImport(DllName)]
        public static extern IntPtr quiche_version();

        public delegate void DebugLoggingCallback(IntPtr message, IntPtr state);
        [DllImport(DllName)]
        public static extern int quiche_enable_debug_logging(
            DebugLoggingCallback cb,
            IntPtr state);

        [DllImport(DllName)]
        public static extern IntPtr quiche_config_new(uint version);

        /// <summary>Configures whether to verify the peer's certificate.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_verify_peer(IntPtr config, bool v);

        /// <summary>Configures whether to send GREASE.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_grease(IntPtr config, bool v);

        /// <summary>Enables logging of secrets.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_log_keys(IntPtr config);

        /// <summary>Enables sending or receiving early data.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_enable_early_data(IntPtr config);

        /// <summary>Configures the list of supported application protocols.</summary>
        [DllImport(DllName)]
        public static extern int quiche_config_set_application_protos(
            IntPtr config,
            byte[] protos,
            UIntPtr protos_len);

        /// <summary>Sets the `idle_timeout` transport parameter.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_set_idle_timeout(IntPtr config, long v);

        /// <summary>Sets the `max_packet_size` transport parameter.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_set_max_packet_size(IntPtr config, long v);

        /// <summary>Sets the `initial_max_data` transport parameter.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_set_initial_max_data(IntPtr config, long v);

        /// <summary>Sets the `initial_max_stream_data_bidi_local` transport parameter.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_set_initial_max_stream_data_bidi_local(IntPtr config, long v);

        /// <summary>Sets the `initial_max_stream_data_bidi_remote` transport parameter.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_set_initial_max_stream_data_bidi_remote(IntPtr config, long v);

        /// <summary>Sets the `initial_max_stream_data_uni` transport parameter.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_set_initial_max_stream_data_uni(IntPtr config, long v);

        /// <summary>Sets the `initial_max_streams_bidi` transport parameter.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_set_initial_max_streams_bidi(IntPtr config, long v);

        /// <summary>Sets the `initial_max_streams_uni` transport parameter.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_set_initial_max_streams_uni(IntPtr config, long v);

        /// <summary>Sets the `ack_delay_exponent` transport parameter.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_set_ack_delay_exponent(IntPtr config, long v);

        /// <summary>Sets the `max_ack_delay` transport parameter.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_set_max_ack_delay(IntPtr config, long v);

        /// <summary>Sets the `disable_active_migration` transport parameter.</summary>
        [DllImport(DllName)]
        public static extern void quiche_config_set_disable_active_migration(IntPtr config, bool v);

        [DllImport(DllName)]
        public static extern void quiche_config_free(IntPtr handle);

        [DllImport(DllName)]
        public static unsafe extern IntPtr quiche_connect(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string serverName,
            byte* scid,
            UIntPtr scid_len,
            IntPtr config);

        [DllImport(DllName)]
        public static unsafe extern IntPtr quiche_conn_recv(IntPtr conn, byte* buf, UIntPtr bufLen);

        [DllImport(DllName)]
        public static unsafe extern IntPtr quiche_conn_send(IntPtr conn, byte* @out, UIntPtr outLen);

        [DllImport(DllName)]
        public static extern bool quiche_conn_is_established(IntPtr conn);

        [DllImport(DllName)]
        public static extern bool quiche_conn_is_in_early_data(IntPtr conn);

        [DllImport(DllName)]
        public static extern bool quiche_conn_is_closed(IntPtr conn);

        [DllImport(DllName)]
        public static unsafe extern void quiche_conn_application_proto(IntPtr conn, out IntPtr @out, out UIntPtr outLen);

        [DllImport(DllName)]
        public static unsafe extern IntPtr quiche_conn_stream_send(IntPtr conn, ulong streamId, byte* buf, UIntPtr len, bool fin);

        [DllImport(DllName)]
        public static unsafe extern IntPtr quiche_conn_stream_recv(
            IntPtr conn, ulong stream_id, byte* @out, UIntPtr buf_len, out bool fin);

        [DllImport(DllName)]
        public static extern IntPtr quiche_conn_readable(IntPtr conn);

        [DllImport(DllName)]
        public static extern IntPtr quiche_conn_writable(IntPtr conn);

        [DllImport(DllName)]
        public static extern bool quiche_stream_iter_next(IntPtr iter, out ulong streamId);

        [DllImport(DllName)]
        public static extern void quiche_stream_iter_free(IntPtr iter);
    }
}
