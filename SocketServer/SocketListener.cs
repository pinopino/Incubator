﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Incubator.SocketServer
{
    public class Package
    {
        internal object connection { set; get; }
        public byte[] MessageData { set; get; }
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

    public abstract class BaseListener
    {
        protected bool Debug;
        protected int BufferSize;
        protected int MaxConnectionCount;
        protected Socket Socket;
        protected ManualResetEventSlim ShutdownEvent;
        protected BlockingCollection<Package> SendingQueue;
        protected Thread SendMessageWorker;
        protected SemaphoreSlim AcceptedClientsSemaphore;
        protected volatile int ConnectedCount;

        internal IOCompletionPortTaskScheduler Scheduler;
        internal SocketAsyncEventArgsPool SocketAsyncReceiveEventArgsPool;
        internal SocketAsyncEventArgsPool SocketAsyncSendEventArgsPool;
        
        public abstract void Send(Package package);
        public abstract byte[] GetMessageBytes(string message);
    }

    public interface IInnerCallBack
    {
        void MessageReceived(byte[] messageBytes);
        void ConnectionClosed(ConnectionInfo info);
    }

    public class SocketListener : BaseListener, IInnerCallBack
    {
        #region 事件
        public event EventHandler OnServerStarting;
        public event EventHandler OnServerStarted;
        public event EventHandler<ConnectionInfo> OnConnectionCreated;
        public event EventHandler<ConnectionInfo> OnConnectionClosed;
        public event EventHandler<ConnectionInfo> OnConnectionAborted;
        public event EventHandler OnServerStopping;
        public event EventHandler OnServerStopped;
        public event EventHandler<byte[]> OnMessageReceived;
        public event EventHandler<Package> OnMessageSending;
        public event EventHandler<Package> OnMessageSent;
        #endregion
        internal ConcurrentDictionary<int, SocketConnection> ConnectionList;

        public SocketListener(int maxConnectionCount, int bufferSize, bool debug = false)
        {
            Debug = debug;
            BufferSize = bufferSize;
            MaxConnectionCount = 0;
            MaxConnectionCount = maxConnectionCount;
            Scheduler = new IOCompletionPortTaskScheduler(12, 12);
            ConnectionList = new ConcurrentDictionary<int, SocketConnection>();
            SendingQueue = new BlockingCollection<Package>();
            SendMessageWorker = new Thread(PorcessMessageQueue);
            ShutdownEvent = new ManualResetEventSlim(false);
            AcceptedClientsSemaphore = new SemaphoreSlim(maxConnectionCount, maxConnectionCount);

            SocketAsyncEventArgs socketAsyncEventArgs = null;
            SocketAsyncSendEventArgsPool = new SocketAsyncEventArgsPool(maxConnectionCount);
            SocketAsyncReceiveEventArgsPool = new SocketAsyncEventArgsPool(maxConnectionCount);
            for (int i = 0; i < maxConnectionCount; i++)
            {
                socketAsyncEventArgs = new SocketAsyncEventArgs();
                socketAsyncEventArgs.SetBuffer(ArrayPool<byte>.Shared.Rent(bufferSize), 0, bufferSize);
                SocketAsyncReceiveEventArgsPool.Push(socketAsyncEventArgs);

                socketAsyncEventArgs = new SocketAsyncEventArgs();
                socketAsyncEventArgs.SetBuffer(ArrayPool<byte>.Shared.Rent(bufferSize), 0, bufferSize);
                SocketAsyncSendEventArgsPool.Push(socketAsyncEventArgs);
            }
        }

        public void Start(IPEndPoint localEndPoint)
        {
            OnServerStarting?.Invoke(this, EventArgs.Empty);
            Socket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            Socket.Bind(localEndPoint);
            Socket.Listen(500);
            SendMessageWorker.Start();
            OnServerStarted?.Invoke(this, EventArgs.Empty);
            StartAccept();
        }

        public void Stop()
        {
            ShutdownEvent.Set();
            OnServerStopping?.Invoke(this, EventArgs.Empty);

            // 处理队列中剩余的消息
            Package package;
            while (SendingQueue.TryTake(out package))
            {
                if (package != null)
                {
                    OnMessageSending?.Invoke(this, package);
                }
            }

            // 关闭所有连接
            SocketConnection conn;
            foreach (var key in ConnectionList.Keys)
            {
                if (ConnectionList.TryRemove(key, out conn))
                {
                    conn.Dispose();
                }
            }
            Socket.Close();
            Dispose();
            OnServerStopped?.Invoke(this, EventArgs.Empty);
        }

        private void StartAccept(SocketAsyncEventArgs acceptEventArg = null)
        {
            if (ShutdownEvent.Wait(0)) // 仅检查标志，立即返回
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

            AcceptedClientsSemaphore.Wait();
            var willRaiseEvent = Socket.AcceptAsync(acceptEventArg);
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
            if (ShutdownEvent.Wait(0)) // 仅检查标志，立即返回
            {
                // 关闭事件触发，退出loop
                return;
            }

            SocketConnection connection = null;
            try
            {
                Interlocked.Increment(ref ConnectedCount);
                connection = new SocketConnection(ConnectedCount, e.AcceptSocket, this, Debug);
                ConnectionList.TryAdd(ConnectedCount, connection);
                Interlocked.Increment(ref ConnectedCount);

                connection.Start();

                OnConnectionCreated?.Invoke(this, new ConnectionInfo { Num = connection.Id, Description = string.Empty, Time = DateTime.Now });
            }
            catch (SocketException ex)
            {
                Console.WriteLine(ex.Message);
            }
            catch (ConnectionAbortedException ex)
            {
                Console.WriteLine(ex.Message);
                connection.Close();
                AcceptedClientsSemaphore.Release();
                Interlocked.Decrement(ref ConnectedCount);
                OnConnectionAborted?.Invoke(this, new ConnectionInfo { Num = connection.Id, Description = string.Empty, Time = DateTime.Now });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            StartAccept(e);
        }

        public override void Send(Package package)
        {
            this.SendingQueue.Add(package);
        }

        public override byte[] GetMessageBytes(string message)
        {
            var body = message;
            var body_bytes = Encoding.UTF8.GetBytes(body);
            var head = body_bytes.Length;
            var head_bytes = BitConverter.GetBytes(head);
            var bytes = ArrayPool<byte>.Shared.Rent(head_bytes.Length + body_bytes.Length);

            Buffer.BlockCopy(head_bytes, 0, bytes, 0, head_bytes.Length);
            Buffer.BlockCopy(body_bytes, 0, bytes, head_bytes.Length, body_bytes.Length);

            return bytes;
        }

        private void PorcessMessageQueue()
        {
            while (true)
            {
                if (ShutdownEvent.Wait(0)) // 仅检查标志，立即返回
                {
                    // 关闭事件触发，退出loop
                    return;
                }

                var package = SendingQueue.Take();
                if (package != null)
                {
                    OnMessageSending?.Invoke(this, package);
                    Sending(package);
                    OnMessageSent?.Invoke(this, package);
                }
            }
        }

        private void Sending(Package package)
        {
            var connection = (SocketConnection)package.connection;
            connection.InnerSend(package);
        }

        public void ConnectionClosed(ConnectionInfo connectionInfo)
        {
            OnConnectionClosed?.Invoke(this, connectionInfo);
        }

        public void MessageReceived(byte[] messageData)
        {
            OnMessageReceived?.Invoke(this, messageData);
        }

        private void Dispose()
        {
            Scheduler.Dispose();
            AcceptedClientsSemaphore.Dispose();
            SocketAsyncSendEventArgsPool.Dispose();
            SocketAsyncReceiveEventArgsPool.Dispose();
        }

        private void Print(string message)
        {
            if (Debug)
            {
                Console.WriteLine(message);
            }
        }
    }
}
