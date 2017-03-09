// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;

namespace System.Net.Http.Managed
{
    internal abstract class HttpConnectionManager
    {
        public abstract void AddConnection(HttpConnection connection);
        public abstract void PutConnection(HttpConnection connection);
    }

    internal sealed class HttpConnectionPool : HttpConnectionManager, IDisposable
    {
        HttpConnectionKey _key;
        ConcurrentDictionary<HttpConnection, HttpConnection> _activeConnections;
        ConcurrentBag<HttpConnection> _idleConnections;
        bool _disposed;

        public HttpConnectionPool(HttpConnectionKey key)
        {
            _key = key;
            _activeConnections = new ConcurrentDictionary<HttpConnection, HttpConnection>();
            _idleConnections = new ConcurrentBag<HttpConnection>();
        }

        public HttpConnectionKey Key => _key;

        public HttpConnection GetConnection()
        {
            HttpConnection connection;
            if (_idleConnections.TryTake(out connection))
            {
                if (!_activeConnections.TryAdd(connection, connection))
                {
                    throw new InvalidOperationException();
                }

                return connection;
            }

            return null;
        }

        public override void AddConnection(HttpConnection connection)
        {
            if (!_activeConnections.TryAdd(connection, connection))
            {
                throw new InvalidOperationException();
            }
        }

        public override void PutConnection(HttpConnection connection)
        {
            HttpConnection unused;
            if (!_activeConnections.TryRemove(connection, out unused))
            {
                throw new InvalidOperationException();
            }

            _idleConnections.Add(connection);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                foreach (HttpConnection connection in _activeConnections.Keys)
                {
                    connection.Dispose();
                }

                foreach (HttpConnection connection in _idleConnections)
                {
                    connection.Dispose();
                }
            }
        }
    }
}