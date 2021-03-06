﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Incubator.Network
{
    public class Package
    {
        public SocketConnection Connection { set; get; }
        public byte[] MessageData { set; get; }
        public int DataLength { set; get; }
        public bool RentFromPool { set; get; }
        public bool NeedHead { set; get; }
    }

    public class ConnectionInfo
    {
        public int Num { set; get; }
        public string Description { set; get; }
        public DateTime Time { set; get; }

        public override string ToString()
        {
            return string.Format("Id：{0}，描述：[{1}]，时间：{2}", Num, Description == string.Empty ? "空" : Description, Time);
        }
    }

    // todo: 也许叫reactor，或者eventpump更有利于今后开展抽象
    // 比如，客户端也叫listener就很不合适
    public abstract class BaseListener : IDisposable
    {
        bool _debug;
        bool _disposed;
        int _bufferSize;
        int _maxConnectionCount;

        protected volatile int _connectedCount;
        protected Socket _socket;
        protected ManualResetEventSlim _shutdownEvent;
        protected SemaphoreSlim _acceptedClientsSemaphore;

        #region 事件
        public event EventHandler<ConnectionInfo> OnConnectionCreated;
        public event EventHandler<ConnectionInfo> OnConnectionAborted;
        public event EventHandler<ConnectionInfo> OnConnectionClosed;
        #endregion

        internal ConcurrentDictionary<int, BaseConnection> ConnectionList;
        internal ObjectPool<IPooledWapper> SocketAsyncReadEventArgsPool;
        internal ObjectPool<IPooledWapper> SocketAsyncSendEventArgsPool;

        public BaseListener(int maxConnectionCount, int bufferSize, bool debug = false)
        {
            _debug = debug;
            _disposed = false;
            _bufferSize = bufferSize;
            _maxConnectionCount = maxConnectionCount;
            _shutdownEvent = new ManualResetEventSlim(false);
            _acceptedClientsSemaphore = new SemaphoreSlim(maxConnectionCount, maxConnectionCount);
            ConnectionList = new ConcurrentDictionary<int, BaseConnection>();

            SocketAsyncSendEventArgsPool = new ObjectPool<IPooledWapper>(maxConnectionCount, 12, (pool) =>
            {
                var socketAsyncEventArgs = new PooledSocketAsyncEventArgs(pool);
                socketAsyncEventArgs.SetBuffer(ArrayPool<byte>.Shared.Rent(bufferSize), 0, bufferSize);
                return socketAsyncEventArgs;
            });

            SocketAsyncReadEventArgsPool = new ObjectPool<IPooledWapper>(maxConnectionCount, 12, (pool) =>
            {
                var socketAsyncEventArgs = new PooledSocketAsyncEventArgs(pool);
                socketAsyncEventArgs.SetBuffer(ArrayPool<byte>.Shared.Rent(bufferSize), 0, bufferSize);
                return socketAsyncEventArgs;
            });
        }

        ~BaseListener()
        {
            Dispose(false);
        }

        public virtual void Start(IPEndPoint localEndPoint)
        {
            _socket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _socket.Bind(localEndPoint);
            _socket.Listen(500);
            StartAccept();
        }

        private void StartAccept(SocketAsyncEventArgs acceptEventArg = null)
        {
            if (_shutdownEvent.Wait(0)) // 仅检查标志，立即返回
            {
                // 关闭事件触发，退出loop
                return;
            }

            if (acceptEventArg == null)
            {
                acceptEventArg = new SocketAsyncEventArgs();
                acceptEventArg.Completed += Accept_Completed;
            }
            else
            {
                acceptEventArg.AcceptSocket = null;
            }

            _acceptedClientsSemaphore.Wait();
            var willRaiseEvent = _socket.AcceptAsync(acceptEventArg);
            if (!willRaiseEvent)
            {
                ProcessAccept(acceptEventArg);
            }
        }

        private void Accept_Completed(object sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }

        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            if (_shutdownEvent.Wait(0)) // 仅检查标志，立即返回
            {
                // 关闭事件触发，退出loop
                return;
            }

            BaseConnection connection = null;
            try
            {
                Interlocked.Increment(ref _connectedCount);
                connection = CreateConnection(e);
                connection.OnConnectionClosed += ConnectionClosed;
                connection.Start();
                ConnectionList.TryAdd(_connectedCount, connection);
                OnConnectionCreated?.Invoke(this, new ConnectionInfo { Num = connection.Id, Description = string.Empty, Time = DateTime.Now });
            }
            catch (SocketException ex)
            {
                Print(ex.Message);
            }
            catch (ConnectionAbortedException ex)
            {
                Print(ex.Message);
                connection.Close();
                _acceptedClientsSemaphore.Release();
                Interlocked.Decrement(ref _connectedCount);
                OnConnectionAborted?.Invoke(this, new ConnectionInfo { Num = connection.Id, Description = string.Empty, Time = DateTime.Now });
            }
            catch (Exception ex)
            {
                Print(ex.Message);
            }

            StartAccept(e);
        }

        protected abstract BaseConnection CreateConnection(SocketAsyncEventArgs e);

        protected virtual void ConnectionClosed(object sender, ConnectionInfo connectionInfo)
        {
            OnConnectionClosed?.Invoke(sender, connectionInfo);
        }

        public virtual void Stop()
        {
            _shutdownEvent.Set();
            // 关闭所有连接
            BaseConnection conn;
            foreach (var key in ConnectionList.Keys)
            {
                if (ConnectionList.TryRemove(key, out conn))
                {
                    conn.Dispose();
                }
            }
            _socket.Close();
            Dispose();
        }

        protected void Print(string message)
        {
            if (_debug)
            {
                Console.WriteLine(message);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            // 通知垃圾回收机制不再调用终结器（析构器）
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }
            if (disposing)
            {
                // 清理托管资源
                _shutdownEvent.Dispose();
                _acceptedClientsSemaphore.Dispose();
                SocketAsyncReadEventArgsPool.Dispose();
                SocketAsyncSendEventArgsPool.Dispose();
            }

            // 清理非托管资源

            // 让类型知道自己已经被释放
            _disposed = true;
        }
    }
}
