﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ads.Client.Commands;
using Ads.Client.Common;
using Ads.Client.CommandResponse;
using Ads.Client.Helpers;
using Ads.Client.Conversation;
using Ads.Client.Internal;

namespace Ads.Client
{

    public class Ams : IDisposable
    {
        private readonly IdGenerator invokeIdGenerator = new();
        private readonly Signal sendSignal;

        private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> pendingInvocations = new();

        public Ams(IAmsSocket amsSocket)
        {
            AmsSocket = amsSocket;
            NotificationRequests = new List<AdsNotification>();

            this.sendSignal = new Signal();
            if (!sendSignal.TryInit())
            {
                throw new Exception("Failed to initialize the send signal.");
            }
        }

        public IAmsSocket AmsSocket { get; }

        internal ushort AmsPortTarget { get; set; }
        internal AmsNetId AmsNetIdTarget { get; set; }
        internal AmsNetId AmsNetIdSource { get; set; }
        internal List<AdsNotification> NotificationRequests;


        /// <summary>
        /// Ams source port. Default of 32905
        /// </summary>
        public ushort AmsPortSource { get; set; } = 32905;

        public Task ConnectAsync()
        {
            return AmsSocket.ConnectAsync(new MessageHandler(this));
        }

        public void Dispose()
        {
            CloseConnection();
            (AmsSocket as IDisposable)?.Dispose();
            sendSignal.Dispose();
        }

        private void CloseConnection()
        {
            AmsSocket.Close();
        }

        internal event AdsNotificationDelegate OnNotification;

        internal async Task<T> RunCommandAsync<T>(AdsCommand adsCommand, CancellationToken cancellationToken)
            where T : AdsCommandResponse, new()
        {
            if (!AmsSocket.Connected) throw new InvalidOperationException("Not connected to PLC.");

            var tcs = new TaskCompletionSource<byte[]>();
            using var cancelTcs = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            uint invokeId;
            do
            {
                invokeId = invokeIdGenerator.Next();
            } while (!pendingInvocations.TryAdd(invokeId, tcs));

            using var cancelPendingInvocation =
                cancellationToken.Register(() => pendingInvocations.TryRemove(invokeId, out _));

            try
            {
                // Avoid message building if already cancelled.
                cancellationToken.ThrowIfCancellationRequested();
                var message = AmsMessageBuilder.BuildAmsMessage(this, adsCommand, invokeId);

                _ = await sendSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Avoid request sending if already cancelled. Some time might have elapsed waiting for the signal.
                    cancellationToken.ThrowIfCancellationRequested();
                    await AmsSocket.SendAsync(message).ConfigureAwait(false);
                }
                finally
                {
                    if (!sendSignal.TryRelease())
                    {
                        throw new Exception("Failed to release the send signal.");
                    }
                }
            }
            catch
            {
                pendingInvocations.TryRemove(invokeId, out _);
                throw;
            }

            var responseBytes = await tcs.Task.ConfigureAwait(false);
            var response = new T();

            cancellationToken.ThrowIfCancellationRequested();
            response.SetResponse(responseBytes);

            return response;
        }

        internal async Task<TResult> PerformRequestAsync<TRequest, TResult>(
            IAdsConversation<TRequest, TResult> conversation, CancellationToken cancellationToken) where TRequest : struct, IAdsRequest
        {
            static void WriteRequest(Span<byte> span, IAdsRequest request, int requestedLength, Ams ams, uint invokeId)
            {
                var len = request.BuildRequest(span.Slice(AmsHeaderHelper.AmsTcpHeaderSize + AmsHeaderHelper.AmsHeaderSize));
                Debug.Assert(len == requestedLength, $"Serialized to wrong size, expect {requestedLength}, actual {len}");

                AmsMessageBuilder.WriteHeader(span, ams, request.Command, invokeId, len);
            }

            static TResult HandleResponse(IAdsConversation<TRequest, TResult> conversation, ReadOnlySpan<byte> span)
            {
                var offset = AmsHeaderHelper.AmsHeaderSize;
                offset += WireFormatting.ReadUInt32(span.Slice(offset), out var errorCode);

                if (errorCode != 0) throw new AdsException(errorCode);

                offset += WireFormatting.ReadInt32(span.Slice(offset), out var dataLength);

                return conversation.ParseResponse(span.Slice(offset, dataLength));
            }

            if (!AmsSocket.Connected) throw new InvalidOperationException("Not connected to PLC.");

            var tcs = new TaskCompletionSource<byte[]>();
            using var cancelTcs = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            uint invokeId;
            do
            {
                invokeId = invokeIdGenerator.Next();
            } while (!pendingInvocations.TryAdd(invokeId, tcs));

            using var cancelPendingInvocation =
                cancellationToken.Register(() => pendingInvocations.TryRemove(invokeId, out _));

            try
            {
                // Avoid message building if already cancelled.
                cancellationToken.ThrowIfCancellationRequested();

                var request = conversation.BuildRequest();
                var requestedLength = request.GetRequestLength();
                var buffer = ArrayPool<byte>.Shared.Rent(AmsHeaderHelper.AmsTcpHeaderSize +
                    AmsHeaderHelper.AmsHeaderSize + requestedLength);
                try
                {
                    WriteRequest(buffer, request, requestedLength, this, invokeId);

                    _ = await sendSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        // Avoid request sending if already cancelled. Some time might have elapsed waiting for the signal.
                        cancellationToken.ThrowIfCancellationRequested();
                        await AmsSocket.SendAsync(new ArraySegment<byte>(buffer, 0,
                                AmsHeaderHelper.AmsTcpHeaderSize + AmsHeaderHelper.AmsHeaderSize + requestedLength))
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!sendSignal.TryRelease())
                        {
                            throw new Exception("Failed to release the send signal.");
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch
            {
                pendingInvocations.TryRemove(invokeId, out _);
                throw;
            }

            var responseBytes = await tcs.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return HandleResponse(conversation, responseBytes);
        }

        private void HandleException(Exception exception)
        {
            foreach (var id in pendingInvocations.Keys)
            {
                if (pendingInvocations.TryRemove(id, out var tcs))
                {
                    tcs.TrySetException(exception);
                }
            }

            CloseConnection();
        }

        private void HandleMessage(byte[] message)
        {
            uint amsErrorCode = AmsHeaderHelper.GetErrorCode(message);
            uint invokeId = AmsHeaderHelper.GetInvokeId(message);
            bool isNotification = (AmsHeaderHelper.GetCommandId(message) == AdsCommandId.DeviceNotification);

            if (AmsPortTarget != AmsHeaderHelper.GetAmsPortSource(message)) return;
            if (!AmsNetIdTarget.Bytes.SequenceEqual(AmsHeaderHelper.GetAmsNetIdSource(message))) return;

            //If notification then just start the callback
            if (isNotification && (OnNotification != null))
            {
                var notifications = AdsNotification.GetNotifications(message);
                foreach (var notification in notifications)
                {
                    var notificationRequest = NotificationRequests.FirstOrDefault(n => n.NotificationHandle == notification.NotificationHandle);
                    if (notificationRequest != null)
                    {
                        notificationRequest.ByteValue = notification.ByteValue;

                        OnNotification(null, new AdsNotificationArgs(notificationRequest));
                    }
                }
            }

            //If not a notification then find the original command and call async callback
            if (!isNotification)
            {
                if (!pendingInvocations.TryRemove(invokeId, out var adsCommandResult))
                {
                    // Unknown or timed-out request
                    return;
                }

                if (amsErrorCode > 0)
                {
                    adsCommandResult.TrySetException(new AdsException(amsErrorCode));
                }
                else
                {
                    adsCommandResult.TrySetResult(message);
                }
            }
        }

        private class MessageHandler : IIncomingMessageHandler
        {
            private readonly Ams ams;

            public MessageHandler(Ams ams) => this.ams = ams;

            public void HandleException(Exception exception)
            {
                ams.HandleException(exception);
            }

            public void HandleMessage(byte[] message)
            {
                ams.HandleMessage(message);
            }
        }
    }
}
