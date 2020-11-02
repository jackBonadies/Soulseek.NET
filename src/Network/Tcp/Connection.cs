﻿// <copyright file="Connection.cs" company="JP Dillingham">
//     Copyright (c) JP Dillingham. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
//     as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty
//     of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License along with this program. If not, see https://www.gnu.org/licenses/.
// </copyright>

namespace Soulseek.Network.Tcp
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;
    using SystemTimer = System.Timers.Timer;

    /// <summary>
    ///     Provides client connections for TCP network services.
    /// </summary>
    internal class Connection : IConnection
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="Connection"/> class.
        /// </summary>
        /// <param name="ipEndPoint">The remote IP endpoint of the connection.</param>
        /// <param name="options">The optional options for the connection.</param>
        /// <param name="tcpClient">The optional TcpClient instance to use.</param>
        public Connection(IPEndPoint ipEndPoint, ConnectionOptions options = null, ITcpClient tcpClient = null)
        {
            Id = Guid.NewGuid();

            IPEndPoint = ipEndPoint;
            Options = options ?? new ConnectionOptions();

            TcpClient = tcpClient ?? new TcpClientAdapter(new TcpClient());
            TcpClient.Client.ReceiveBufferSize = Options.ReadBufferSize;
            TcpClient.Client.SendBufferSize = Options.WriteBufferSize;

            //if (Options.InactivityTimeout > 0)
            //{
            //    InactivityTimer = new SystemTimer()
            //    {
            //        Enabled = false,
            //        AutoReset = false,
            //        Interval = Options.InactivityTimeout,
            //    };

            //    InactivityTimer.Elapsed += (sender, e) =>
            //    {
            //        var ex = new TimeoutException($"Inactivity timeout of {Options.InactivityTimeout} milliseconds was reached");
            //        Disconnect(ex.Message, ex);
            //    };
            //}

            WatchdogTimer = new SystemTimer()
            {
                Enabled = false,
                AutoReset = true,
                Interval = 250,
            };

            WatchdogTimer.Elapsed += (sender, e) =>
            {
                if (TcpClient == null || !TcpClient.Connected)
                {
                    Disconnect("The server connection was closed unexpectedly");
                }
            };

            if (TcpClient.Connected)
            {
                State = ConnectionState.Connected;
                InactivityTimer?.Start();
                WatchdogTimer.Start();
                Stream = TcpClient.GetStream();
            }
        }

        /// <summary>
        ///     Occurs when the connection is connected.
        /// </summary>
        public event EventHandler Connected;

        /// <summary>
        ///     Occurs when data is ready from the connection.
        /// </summary>
        public event EventHandler<ConnectionDataEventArgs> DataRead;

        /// <summary>
        ///     Occurs when data has been written to the connection.
        /// </summary>
        public event EventHandler<ConnectionDataEventArgs> DataWritten;

        /// <summary>
        ///     Occurs when the connection is disconnected.
        /// </summary>
        public event EventHandler<ConnectionDisconnectedEventArgs> Disconnected;

        /// <summary>
        ///     Occurs when the connection state changes.
        /// </summary>
        public event EventHandler<ConnectionStateChangedEventArgs> StateChanged;

        /// <summary>
        ///     Gets the connection id.
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        ///     Gets the amount of time since the last activity.
        /// </summary>
        public TimeSpan InactiveTime => DateTime.UtcNow - LastActivityTime;

        /// <summary>
        ///     Gets or sets the remote IP endpoint of the connection.
        /// </summary>
        public IPEndPoint IPEndPoint { get; protected set; }

        /// <summary>
        ///     Gets the unique identifier of the connection.
        /// </summary>
        public virtual ConnectionKey Key => new ConnectionKey(IPEndPoint);

        /// <summary>
        ///     Gets or sets the options for the connection.
        /// </summary>
        public ConnectionOptions Options { get; protected set; }

        /// <summary>
        ///     Gets or sets the current connection state.
        /// </summary>
        public ConnectionState State { get; protected set; }

        /// <summary>
        ///     Gets or sets the connection type.
        /// </summary>
        public ConnectionTypes Type { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether the object is disposed.
        /// </summary>
        protected bool Disposed { get; set; } = false;

        /// <summary>
        ///     Gets or sets the timer used to monitor for transfer inactivity.
        /// </summary>
        protected SystemTimer InactivityTimer { get; set; }

        /// <summary>
        ///     Gets or sets the network stream for the connection.
        /// </summary>
        protected INetworkStream Stream { get; set; }

        /// <summary>
        ///     Gets or sets the TcpClient used by the connection.
        /// </summary>
        protected ITcpClient TcpClient { get; set; }

        /// <summary>
        ///     Gets or sets the timer used to monitor the status of the TcpClient.
        /// </summary>
        protected SystemTimer WatchdogTimer { get; set; }

        /// <summary>
        ///     Gets or sets the time at which the last activity took place.
        /// </summary>
        protected DateTime LastActivityTime { get; set; } = DateTime.UtcNow;

        private TaskCompletionSource<string> DisconnectTaskCompletionSource { get; } = new TaskCompletionSource<string>();

        /// <summary>
        ///     Asynchronously connects the client to the configured <see cref="IPEndPoint"/>.
        /// </summary>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">
        ///     Thrown when the connection is already connected, or is transitioning between states.
        /// </exception>
        /// <exception cref="TimeoutException">
        ///     Thrown when the time attempting to connect exceeds the configured <see cref="ConnectionOptions.ConnectTimeout"/> value.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        ///     Thrown when <paramref name="cancellationToken"/> cancellation is requested.
        /// </exception>
        /// <exception cref="ConnectionException">Thrown when an unexpected error occurs.</exception>
        public async Task ConnectAsync(CancellationToken? cancellationToken = null)
        {
            if (State != ConnectionState.Pending && State != ConnectionState.Disconnected)
            {
                throw new InvalidOperationException($"Invalid attempt to connect a connected or transitioning connection (current state: {State})");
            }

            cancellationToken ??= CancellationToken.None;

            // create a new TCS to serve as the trigger which will throw when the CTS times out a TCS is basically a 'fake' task
            // that ends when the result is set programmatically. create another for cancellation via the externally provided token.
            var timeoutTaskCompletionSource = new TaskCompletionSource<bool>();
            var cancellationTaskCompletionSource = new TaskCompletionSource<bool>();

            try
            {
                ChangeState(ConnectionState.Connecting, $"Connecting to {IPEndPoint}");

                // create a new CTS with our desired timeout. when the timeout expires, the cancellation will fire
                using (var timeoutCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(Options.ConnectTimeout)))
                {
                    var connectTask = TcpClient.ConnectAsync(IPEndPoint.Address, IPEndPoint.Port);

                    // register the TCS with the CTS. when the cancellation fires (due to timeout), it will set the value of the
                    // TCS via the registered delegate, ending the 'fake' task, then bind the externally supplied CT with the same
                    // TCS. either the timeout or the external token can now cancel the operation.
                    await using (timeoutCancellationTokenSource.Token.Register(() => timeoutTaskCompletionSource.TrySetResult(true)))
                    await using (((CancellationToken)cancellationToken).Register(() => cancellationTaskCompletionSource.TrySetResult(true)))
                    {
                        var completedTask = await Task.WhenAny(connectTask, timeoutTaskCompletionSource.Task, cancellationTaskCompletionSource.Task).ConfigureAwait(false);

                        if (completedTask == timeoutTaskCompletionSource.Task)
                        {
                            throw new TimeoutException($"Operation timed out after {Options.ConnectTimeout} milliseconds");
                        }
                        else if (completedTask == cancellationTaskCompletionSource.Task)
                        {
                            throw new OperationCanceledException("Operation cancelled", cancellationToken.Value);
                        }

                        if (connectTask.Exception?.InnerException != null)
                        {
                            throw connectTask.Exception.InnerException;
                        }
                    }
                }

                InactivityTimer?.Start();
                WatchdogTimer.Start();
                Stream = TcpClient.GetStream();

                ChangeState(ConnectionState.Connected, $"Connected to {IPEndPoint}");
            }
            catch (Exception ex)
            {
                Disconnect($"Connection Error: {ex.Message}", ex);

                if (ex is TimeoutException || ex is OperationCanceledException)
                {
                    throw;
                }

                throw new ConnectionException($"Failed to connect to {IPEndPoint}: {ex.Message}", ex);
            }
        }

        /// <summary>
        ///     Disconnects the client.
        /// </summary>
        /// <param name="message">The optional message or reason for the disconnect.</param>
        /// <param name="exception">The optional Exception associated with the disconnect.</param>
        public void Disconnect(string message = null, Exception exception = null)
        {
            if (State != ConnectionState.Disconnected && State != ConnectionState.Disconnecting)
            {
                message ??= exception?.Message;

                ChangeState(ConnectionState.Disconnecting, message);

                InactivityTimer?.Stop();
                WatchdogTimer.Stop();
                Stream?.Close();
                TcpClient?.Close();

                ChangeState(ConnectionState.Disconnected, message, exception);
            }
        }

        /// <summary>
        ///     Releases the managed and unmanaged resources used by the <see cref="IConnection"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///     Decouples and returns the underlying TCP connection for this connection, allowing the TCP connection to survive
        ///     beyond the lifespan of this instance.
        /// </summary>
        /// <returns>The underlying TCP connection for this connection.</returns>
        public ITcpClient HandoffTcpClient()
        {
            var tcpClient = TcpClient;

            TcpClient = null;
            Stream = null;

            return tcpClient;
        }

        /// <summary>
        ///     Asynchronously reads the specified number of bytes from the connection.
        /// </summary>
        /// <remarks>The connection is disconnected if a <see cref="ConnectionReadException"/> is thrown.</remarks>
        /// <param name="length">The number of bytes to read.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A Task representing the asynchronous operation, including the read bytes.</returns>
        /// <exception cref="ArgumentException">Thrown when the specified <paramref name="length"/> is less than 1.</exception>
        /// <exception cref="InvalidOperationException">
        ///     Thrown when the connection state is not <see cref="ConnectionState.Connected"/>, or when the underlying TcpClient
        ///     is not connected.
        /// </exception>
        /// <exception cref="ConnectionReadException">Thrown when an unexpected error occurs.</exception>
        public Task<byte[]> ReadAsync(long length, CancellationToken? cancellationToken = null)
        {
            if (length < 0)
            {
                throw new ArgumentException("The requested length must be greater than or equal to zero", nameof(length));
            }

            if (!TcpClient.Connected)
            {
                throw new InvalidOperationException("The underlying Tcp connection is closed");
            }

            if (State != ConnectionState.Connected)
            {
                throw new InvalidOperationException($"Invalid attempt to send to a disconnected or transitioning connection (current state: {State})");
            }

            return ReadInternalAsync(length, cancellationToken ?? CancellationToken.None);
        }

        /// <summary>
        ///     Asynchronously reads the specified number of bytes from the connection.
        /// </summary>
        /// <remarks>The connection is disconnected if a <see cref="ConnectionReadException"/> is thrown.</remarks>
        /// <param name="length">The number of bytes to read.</param>
        /// <param name="outputStream">The stream to which the read data is to be written.</param>
        /// <param name="governor">The delegate used to govern transfer speed.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A Task representing the asynchronous operation, including the read bytes.</returns>
        /// <exception cref="ArgumentException">Thrown when the specified <paramref name="length"/> is less than 1.</exception>
        /// <exception cref="ArgumentException">Thrown when the specified <paramref name="outputStream"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        ///     Thrown when the specified <paramref name="outputStream"/> is not writeable.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///     Thrown when the connection state is not <see cref="ConnectionState.Connected"/>, or when the underlying TcpClient
        ///     is not connected.
        /// </exception>
        /// <exception cref="ConnectionReadException">Thrown when an unexpected error occurs.</exception>
        public Task ReadAsync(long length, Stream outputStream, Func<CancellationToken, Task> governor, CancellationToken? cancellationToken = null)
        {
            if (length < 0)
            {
                throw new ArgumentException("The requested length must be greater than or equal to zero", nameof(length));
            }

            if (outputStream == null)
            {
                throw new ArgumentNullException(nameof(outputStream), "The specified output stream is null");
            }

            if (!outputStream.CanWrite)
            {
                throw new InvalidOperationException("The specified output stream is not writeable");
            }

            if (!TcpClient.Connected)
            {
                throw new InvalidOperationException("The underlying Tcp connection is closed");
            }

            if (State != ConnectionState.Connected)
            {
                throw new InvalidOperationException($"Invalid attempt to send to a disconnected or transitioning connection (current state: {State})");
            }

            return ReadInternalAsync(length, outputStream, governor ?? ((t) => Task.CompletedTask), cancellationToken ?? CancellationToken.None);
        }

        /// <summary>
        ///     Waits for the connection to disconnect, returning the message or throwing the Exception which caused the disconnect.
        /// </summary>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>The message describing the reason for the disconnect.</returns>
        /// <exception cref="Exception">Thrown when the connection is disconnected as the result of an Exception.</exception>
        public Task<string> WaitForDisconnect(CancellationToken? cancellationToken = null)
        {
            cancellationToken?.Register(() =>
                Disconnect(exception: new OperationCanceledException("Operation cancelled")));

            return DisconnectTaskCompletionSource.Task;
        }

        /// <summary>
        ///     Asynchronously writes the specified bytes to the connection.
        /// </summary>
        /// <remarks>The connection is disconnected if a <see cref="ConnectionWriteException"/> is thrown.</remarks>
        /// <param name="bytes">The bytes to write.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <exception cref="ArgumentException">Thrown when the specified <paramref name="bytes"/> array is null or empty.</exception>
        /// <exception cref="InvalidOperationException">
        ///     Thrown when the connection state is not <see cref="ConnectionState.Connected"/>, or when the underlying TcpClient
        ///     is not connected.
        /// </exception>
        /// <exception cref="ConnectionWriteException">Thrown when an unexpected error occurs.</exception>
        public Task WriteAsync(byte[] bytes, CancellationToken? cancellationToken = null)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new ArgumentException("Invalid attempt to send empty data", nameof(bytes));
            }

            if (!TcpClient.Connected)
            {
                throw new InvalidOperationException("The underlying Tcp connection is closed");
            }

            if (State != ConnectionState.Connected)
            {
                throw new InvalidOperationException($"Invalid attempt to send to a disconnected or transitioning connection (current state: {State})");
            }

            return WriteInternalAsync(bytes, cancellationToken ?? CancellationToken.None);
        }

        /// <summary>
        ///     Asynchronously writes the specified bytes to the connection.
        /// </summary>
        /// <remarks>The connection is disconnected if a <see cref="ConnectionWriteException"/> is thrown.</remarks>
        /// <param name="length">The number of bytes to write.</param>
        /// <param name="inputStream">The stream from which the written data is to be read.</param>
        /// <param name="governor">The delegate used to govern transfer speed.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A Task representing the asynchronous operation.</returns>
        /// <exception cref="ArgumentException">Thrown when the specified <paramref name="length"/> is less than 1.</exception>
        /// <exception cref="ArgumentNullException">Thrown when the specified <paramref name="inputStream"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        ///     Thrown when the specified <paramref name="inputStream"/> is not readable.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        ///     Thrown when the connection state is not <see cref="ConnectionState.Connected"/>, or when the underlying TcpClient
        ///     is not connected.
        /// </exception>
        /// <exception cref="ConnectionWriteException">Thrown when an unexpected error occurs.</exception>
        public Task WriteAsync(long length, Stream inputStream, Func<CancellationToken, Task> governor, CancellationToken? cancellationToken = null)
        {
            if (length <= 0)
            {
                throw new ArgumentException("The requested length must be greater than or equal to zero", nameof(length));
            }

            if (inputStream == null)
            {
                throw new ArgumentNullException(nameof(inputStream), "The specified output stream is null");
            }

            if (!inputStream.CanRead)
            {
                throw new InvalidOperationException("The specified input stream is not readable");
            }

            if (!TcpClient.Connected)
            {
                throw new InvalidOperationException("The underlying Tcp connection is closed");
            }

            if (State != ConnectionState.Connected)
            {
                throw new InvalidOperationException($"Invalid attempt to send to a disconnected or transitioning connection (current state: {State})");
            }

            return WriteInternalAsync(length, inputStream, governor ?? ((t) => Task.CompletedTask), cancellationToken ?? CancellationToken.None);
        }

        /// <summary>
        ///     Changes the state of the connection to the specified <paramref name="state"/> and raises events with the
        ///     optionally specified <paramref name="message"/>.
        /// </summary>
        /// <param name="state">The state to which to change.</param>
        /// <param name="message">The optional message describing the nature of the change.</param>
        /// <param name="exception">The optional Exception associated with the change.</param>
        protected void ChangeState(ConnectionState state, string message, Exception exception = null)
        {
            var eventArgs = new ConnectionStateChangedEventArgs(previousState: State, currentState: state, message: message, exception: exception);

            State = state;

            Interlocked.CompareExchange(ref StateChanged, null, null)?
                .Invoke(this, eventArgs);

            if (State == ConnectionState.Connected)
            {
                Interlocked.CompareExchange(ref Connected, null, null)?
                    .Invoke(this, EventArgs.Empty);
            }
            else if (State == ConnectionState.Disconnected)
            {
                Interlocked.CompareExchange(ref Disconnected, null, null)?
                    .Invoke(this, new ConnectionDisconnectedEventArgs(message, exception));

                if (exception != null)
                {
                    DisconnectTaskCompletionSource.SetException(exception);
                }
                else
                {
                    DisconnectTaskCompletionSource.SetResult(message);
                }
            }
        }

        /// <summary>
        ///     Releases the managed and unmanaged resources used by the <see cref="Connection"/>.
        /// </summary>
        /// <param name="disposing">A value indicating whether the object is in the process of disposing.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!Disposed)
            {
                if (disposing)
                {
                    Disconnect("Connection is being disposed", new ObjectDisposedException(GetType().Name));
                    InactivityTimer?.Dispose();
                    WatchdogTimer.Dispose();
                    Stream?.Dispose();
                    TcpClient?.Dispose();
                }

                Disposed = true;
            }
        }

        private async Task<byte[]> ReadInternalAsync(long length, CancellationToken cancellationToken)
        {
            await using var stream = new MemoryStream();

            await ReadInternalAsync(length, stream, (c) => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
            return stream.ToArray();
        }

        private async Task ReadInternalAsync(long length, Stream outputStream, Func<CancellationToken, Task> governor, CancellationToken cancellationToken)
        {
            ResetInactivityTime();

            var buffer = new byte[Options.ReadBufferSize];
            long totalBytesRead = 0;

            try
            {
                while (totalBytesRead < length)
                {
                    await governor(cancellationToken).ConfigureAwait(false);

                    var bytesRemaining = length - totalBytesRead;
                    var bytesToRead = bytesRemaining >= buffer.Length ? buffer.Length : (int)bytesRemaining; // cast to int is safe because of the check against buffer length.

                    var bytesRead = await Stream.ReadAsync(buffer, 0, bytesToRead, cancellationToken).ConfigureAwait(false);

                    if (bytesRead == 0)
                    {
                        throw new ConnectionException("Remote connection closed");
                    }

                    totalBytesRead += bytesRead;

                    await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);

                    Interlocked.CompareExchange(ref DataRead, null, null)?
                        .Invoke(this, new ConnectionDataEventArgs(totalBytesRead, length));

                    ResetInactivityTime();
                }

                await outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Disconnect($"Read error: {ex.Message}", ex);

                if (ex is TimeoutException || ex is OperationCanceledException)
                {
                    throw;
                }

                throw new ConnectionReadException($"Failed to read {length} bytes from {IPEndPoint}: {ex.Message}", ex);
            }
        }

        protected void ResetInactivityTime()
        {
            InactivityTimer?.Reset();
            LastActivityTime = DateTime.UtcNow;
        }

        private async Task WriteInternalAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await using var stream = new MemoryStream(bytes);

            await WriteInternalAsync(bytes.Length, stream, (c) => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteInternalAsync(long length, Stream inputStream, Func<CancellationToken, Task> governor, CancellationToken cancellationToken)
        {
            ResetInactivityTime();

            var inputBuffer = new byte[Options.WriteBufferSize];
            var totalBytesWritten = 0;

            try
            {
                while (totalBytesWritten < length)
                {
                    await governor(cancellationToken).ConfigureAwait(false);

                    var bytesRemaining = length - totalBytesWritten;

                    var bytesToRead = bytesRemaining >= inputBuffer.Length ? inputBuffer.Length : (int)bytesRemaining;
                    var bytesRead = await inputStream.ReadAsync(inputBuffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);

                    await Stream.WriteAsync(inputBuffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);

                    totalBytesWritten += bytesRead;

                    Interlocked.CompareExchange(ref DataWritten, null, null)?
                        .Invoke(this, new ConnectionDataEventArgs(totalBytesWritten, length));

                    ResetInactivityTime();
                }
            }
            catch (Exception ex)
            {
                Disconnect($"Write error: {ex.Message}", ex);

                if (ex is TimeoutException || ex is OperationCanceledException)
                {
                    throw;
                }

                throw new ConnectionWriteException($"Failed to write {length} bytes to {IPEndPoint}: {ex.Message}", ex);
            }
        }
    }
}