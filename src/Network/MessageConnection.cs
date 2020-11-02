﻿// <copyright file="MessageConnection.cs" company="JP Dillingham">
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

namespace Soulseek.Network
{
    using System;
    using System.Buffers;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Pipelines;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Soulseek.Messaging.Messages;
    using Soulseek.Network.Tcp;

    /// <summary>
    ///     Provides client connections to the Soulseek network.
    /// </summary>
    internal sealed class MessageConnection : Connection, IMessageConnection
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="MessageConnection"/> class.
        /// </summary>
        /// <param name="username">The username of the peer associated with the connection, if applicable.</param>
        /// <param name="ipEndPoint">The remote IP endpoint of the connection.</param>
        /// <param name="options">The optional options for the connection.</param>
        /// <param name="tcpClient">The optional TcpClient instance to use.</param>
        internal MessageConnection(string username, IPEndPoint ipEndPoint, ConnectionOptions options = null, ITcpClient tcpClient = null)
            : this(ipEndPoint, options, tcpClient)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException("The username must not be a null or empty string, or one consisting only of whitespace", nameof(username));
            }

            Username = username;
        }

        /// <summary>
        ///     Initializes a new instance of the <see cref="MessageConnection"/> class.
        /// </summary>
        /// <param name="ipEndPoint">The remote IP endpoint of the connection.</param>
        /// <param name="options">The optional options for the connection.</param>
        /// <param name="tcpClient">The optional TcpClient instance to use.</param>
        internal MessageConnection(IPEndPoint ipEndPoint, ConnectionOptions options = null, ITcpClient tcpClient = null)
            : base(ipEndPoint, options, tcpClient)
        {
            // bind the connected event to begin reading upon connection. if we received a connected client, this will never fire
            // and the read loop must be started via ReadContinuouslyAsync().
            Connected += (sender, e) =>
            {
                // if Username is empty, this is a server connection. begin reading continuously, and throw on exception.
                if (IsServerConnection)
                {
                    Task.Run(() => ReadFromPipeContinuouslyAsync()).ForgetButThrowWhenFaulted<ConnectionException>();
                }
                else
                {
                    // swallow exceptions from peer connections; these will be handled by timeouts.
                    Task.Run(() => ReadFromPipeContinuouslyAsync()).Forget();
                }
            };
        }

        /// <summary>
        ///     Occurs when message data is received.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This event is separate from the underlying <see cref="Connection.DataRead"/> because it is bounded to the
        ///         message payload. The base event will be raised when reading the message length and code, while this event will not.
        ///     </para>
        ///     <para>
        ///         This event is only useful for tracking the progress of large messages (larger than the receive buffer);
        ///         basically only the response to a browse request.  There is no corresponding event for data written, as this
        ///         library sends messages in their entirety, and the two would be fuctionally identical.
        ///     </para>
        /// </remarks>
        public event EventHandler<MessageDataEventArgs> MessageDataRead;

        /// <summary>
        ///     Occurs when a new message is read in its entirety.
        /// </summary>
        public event EventHandler<MessageEventArgs> MessageRead;

        /// <summary>
        ///     Occurs when a new message is received, but before it is read.
        /// </summary>
        public event EventHandler<MessageReceivedEventArgs> MessageReceived;

        /// <summary>
        ///     Occurs when a message is written in its entirety.
        /// </summary>
        public event EventHandler<MessageEventArgs> MessageWritten;

        /// <summary>
        ///     Gets a value indicating whether this connection is connected to the server, as opposed to a peer.
        /// </summary>
        public bool IsServerConnection => string.IsNullOrEmpty(Username);

        /// <summary>
        ///     Gets the unique identifier for the connection.
        /// </summary>
        public override ConnectionKey Key => new ConnectionKey(Username, IPEndPoint);

        /// <summary>
        ///     Gets a value indicating whether the internal continuous read loop is running.
        /// </summary>
        public bool ReadingContinuously { get; private set; }

        /// <summary>
        ///     Gets the username of the peer associated with the connection, if applicable.
        /// </summary>
        public string Username { get; private set; } = string.Empty;

        /// <summary>
        ///     Begins the internal continuous read loop, if it has not yet started.
        /// </summary>
        public void StartReadingContinuously()
        {
            if (!ReadingContinuously)
            {
                Task.Run(() => ReadFromPipeContinuouslyAsync()).Forget();
            }
        }

        /// <summary>
        ///     Asynchronously writes the specified <paramref name="message"/> to the connection.
        /// </summary>
        /// <param name="message">The message to write.</param>
        /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="ArgumentException">Thrown when the specified <paramref name="message"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        ///     Thrown when the connection state is not <see cref="ConnectionState.Connected"/>, or when the underlying TcpClient
        ///     is not connected.
        /// </exception>
        /// <exception cref="MessageException">
        ///     Thrown when an error is encountered while converting the message to a byte array.
        /// </exception>
        /// <exception cref="ConnectionWriteException">Thrown when an unexpected error occurs.</exception>
        public Task WriteAsync(IOutgoingMessage message, CancellationToken? cancellationToken = null)
        {
            if (message == default)
            {
                throw new ArgumentException("The specified message is null", nameof(message));
            }

            byte[] bytes;

            try
            {
                bytes = message.ToByteArray();
            }
            catch (Exception ex)
            {
                throw new MessageException("Failed to convert the message to a byte array", ex);
            }

            return WriteMessageInternalAsync(bytes, cancellationToken ?? CancellationToken.None);
        }

        private async Task ReadContinuouslyAsync()
        {
            if (ReadingContinuously)
            {
                return;
            }

            ReadingContinuously = true;
            byte[] codeBytes = null;

            void RaiseMessageDataRead(object sender, ConnectionDataEventArgs e)
            {
                Interlocked.CompareExchange(ref MessageDataRead, null, null)?
                    .Invoke(this, new MessageDataEventArgs(codeBytes, e.CurrentLength, e.TotalLength));
            }

            try
            {
                while (true)
                {
                    try
                    {
                        var message = new List<byte>();

                        var lengthBytes = await ReadAsync(4, CancellationToken.None).ConfigureAwait(false);
                        var length = BitConverter.ToInt32(lengthBytes, 0);
                        message.AddRange(lengthBytes);

                        codeBytes = await ReadAsync(4, CancellationToken.None).ConfigureAwait(false);
                        message.AddRange(codeBytes);

                        RaiseMessageDataRead(this, new ConnectionDataEventArgs(0, length - 4));

                        Interlocked.CompareExchange(ref MessageReceived, null, null)?
                            .Invoke(this, new MessageReceivedEventArgs(length, codeBytes));

                        DataRead += RaiseMessageDataRead;

                        var payloadBytes = await ReadAsync(length - 4, CancellationToken.None).ConfigureAwait(false);
                        message.AddRange(payloadBytes);

                        var messageBytes = message.ToArray();
                        Interlocked.CompareExchange(ref MessageRead, null, null)?
                            .Invoke(this, new MessageEventArgs(messageBytes));
                    }
                    finally
                    {
                        DataRead -= RaiseMessageDataRead;
                    }
                }
            }
            finally
            {
                ReadingContinuously = false;
            }
        }

        private async Task ReadFromPipeContinuouslyAsync()
        {
            if (ReadingContinuously)
            {
                return;
            }

            ReadingContinuously = true;

            void RaiseMessageDataRead(byte[] codeBytes, long currentLength, long totalLength)
            {
                Interlocked.CompareExchange(ref MessageDataRead, null, null)?
                    .Invoke(this, new MessageDataEventArgs(codeBytes, currentLength, totalLength));
            }

            try
            {
                var reader = PipeReader.Create(TcpClient.GetRawStream());

                while (true)
                {
                    ReadResult result = await reader.ReadAsync().ConfigureAwait(false);
                    ReadOnlySequence<byte> buffer = result.Buffer;

                    while (TryReadMessage(ref buffer, out ReadOnlySequence<byte> message))
                    {
                        var messageBytes = message.ToArray();

                        Console.WriteLine($"Firing MessageRead with {messageBytes.Length} bytes");
                        Interlocked.CompareExchange(ref MessageRead, null, null)?
                            .Invoke(this, new MessageEventArgs(messageBytes));
                    }

                    reader.AdvanceTo(buffer.Start, buffer.End);

                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            finally
            {
                ReadingContinuously = false;
            }
        }

        private bool Reading = false;

        private bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> message)
        {
            if (buffer.Length < 8)
            {
                message = default;
                return false;
            }

            var lengthBytes = buffer.Slice(0, 4);
            var length = BitConverter.ToInt32(lengthBytes.ToArray(), 0);
            var codeBytes = buffer.Slice(4, 4);

            if (!Reading)
            {
                Console.WriteLine($"Firing MessageReceived");
                Interlocked.CompareExchange(ref MessageReceived, null, null)?
                    .Invoke(this, new MessageReceivedEventArgs(length, codeBytes.ToArray()));

                Reading = true;
            }

            Interlocked.CompareExchange(ref MessageDataRead, null, null)?
                .Invoke(this, new MessageDataEventArgs(codeBytes.ToArray(), buffer.Length, length));

            ResetInactivityTime();

            if (buffer.Length < length)
            {
                message = default;
                return false;
            }

            message = buffer.Slice(0, length + 4);
            buffer = buffer.Slice(buffer.GetPosition(length + 4));

            Reading = false;

            return true;
        }

        private async Task WriteMessageInternalAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

            Interlocked.CompareExchange(ref MessageWritten, null, null)?
                .Invoke(this, new MessageEventArgs(bytes));
        }
    }
}