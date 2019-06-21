﻿namespace SqlStreamStore.Subscriptions
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using SqlStreamStore.Imports.AsyncEx.Nito.AsyncEx.Coordination;
    using SqlStreamStore.Infrastructure;
    using SqlStreamStore.Logging;
    using SqlStreamStore.Streams;

    /// <summary>
    ///     Represents a subscription to a stream.
    /// </summary>
    public sealed class StreamSubscription : IStreamSubscription
    {
        /// <summary>
        ///     The default page size to read.
        /// </summary>
        public const int DefaultPageSize = 10;

        private static readonly ILog
            s_logger = LogProvider.GetLogger("SqlStreamStore.Subscriptions.StreamSubscription");

        private int _pageSize = DefaultPageSize;
        private int _nextVersion;
        private readonly int? _continueAfterVersion;
        private readonly IReadonlyStreamStore _readonlyStreamStore;
        private readonly StreamMessageReceived _streamMessageReceived;
        private readonly bool _prefectchJsonData;
        private readonly HasCaughtUp _hasCaughtUp;
        private readonly SubscriptionDropped _subscriptionDropped;
        private readonly IDisposable _notification;
        private readonly CancellationTokenSource _disposed = new CancellationTokenSource();
        private readonly AsyncAutoResetEvent _streamStoreNotification = new AsyncAutoResetEvent();
        private readonly TaskCompletionSource<object> _started = new TaskCompletionSource<object>();
        private readonly InterlockedBoolean _notificationRaised = new InterlockedBoolean();

        public StreamSubscription(
            string streamId,
            int? continueAfterVersion,
            IReadonlyStreamStore readonlyStreamStore,
            IObservable<Unit> streamStoreAppendedNotification,
            StreamMessageReceived streamMessageReceived,
            SubscriptionDropped subscriptionDropped,
            HasCaughtUp hasCaughtUp,
            bool prefectchJsonData,
            string name)
        {
            StreamId = streamId;
            _continueAfterVersion = continueAfterVersion;
            _readonlyStreamStore = readonlyStreamStore;
            _streamMessageReceived = streamMessageReceived;
            _prefectchJsonData = prefectchJsonData;
            _subscriptionDropped = subscriptionDropped ?? ((_, __, ___) => { });
            _hasCaughtUp = hasCaughtUp ?? ((_) => { });
            Name = string.IsNullOrWhiteSpace(name) ? Guid.NewGuid().ToString() : name;

            readonlyStreamStore.OnDispose += ReadonlyStreamStoreOnOnDispose;

            _notification = streamStoreAppendedNotification.Subscribe(_ => { _streamStoreNotification.Set(); });

            Task.Run(PullAndPush);

            s_logger.Info(
                "Stream subscription created {name} continuing after version {version}",
                name,
                continueAfterVersion?.ToString() ?? "<null>");
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public string StreamId { get; }

        /// <inheritdoc />
        public int? LastVersion { get; private set; }

        /// <inheritdoc />
        public Task Started => _started.Task;

        /// <inheritdoc />
        public int MaxCountPerRead
        {
            get => _pageSize;
            set => _pageSize = value <= 0 ? 1 : value;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if(_disposed.IsCancellationRequested)
            {
                return;
            }

            _disposed.Cancel();
            _notification.Dispose();
            NotifySubscriptionDropped(SubscriptionDroppedReason.Disposed);
        }

        private void ReadonlyStreamStoreOnOnDispose()
        {
            _readonlyStreamStore.OnDispose -= ReadonlyStreamStoreOnOnDispose;
            Dispose();
        }

        private async Task PullAndPush()
        {
            if(!_continueAfterVersion.HasValue)
            {
                _nextVersion = 0;
            }
            else if(_continueAfterVersion.Value == StreamVersion.End)
            {
                await Initialize();
            }
            else
            {
                _nextVersion = _continueAfterVersion.Value + 1;
            }

            _started.SetResult(null);

            while(!_disposed.IsCancellationRequested)
            {
                bool pause = false;
                bool? lastHasCaughtUp = null;

                var messages = await Pull();

                await foreach(var message in messages)
                {
                    await Push(message);
                }

                if(!(lastHasCaughtUp ?? false))
                {
                    _hasCaughtUp(true);
                    lastHasCaughtUp = true;
                }

                await Task.Delay(100).NotOnCapturedContext();
            }
        }

        private async Task Initialize()
        {
            try
            {
                var messages = await _readonlyStreamStore.ReadStreamBackwards(
                    StreamId,
                    StreamVersion.End,
                    1,
                    false,
                    _disposed.Token);
                // Get the last stream version and subscribe from there.
                var message = await messages
                    .FirstAsync()
                    .NotOnCapturedContext();
                //Only new Messages, i.e. the one after the current last one
                _nextVersion = message.StreamVersion + 1;
                LastVersion = _nextVersion;
            }
            catch(ObjectDisposedException)
            {
                NotifySubscriptionDropped(SubscriptionDroppedReason.Disposed);
                throw;
            }
            catch(OperationCanceledException)
            {
                NotifySubscriptionDropped(SubscriptionDroppedReason.Disposed);
                throw;
            }
            catch(Exception ex)
            {
                s_logger.ErrorException($"Error reading stream {Name}/{StreamId}", ex);
                NotifySubscriptionDropped(SubscriptionDroppedReason.StreamStoreError, ex);
                throw;
            }
        }

        private async Task<ReadStreamResult> Pull()
        {
            try
            {
                return await _readonlyStreamStore
                    .ReadStreamForwards(StreamId, _nextVersion, MaxCountPerRead, _prefectchJsonData, _disposed.Token);
            }
            catch(ObjectDisposedException)
            {
                NotifySubscriptionDropped(SubscriptionDroppedReason.Disposed);
                throw;
            }
            catch(OperationCanceledException)
            {
                NotifySubscriptionDropped(SubscriptionDroppedReason.Disposed);
                throw;
            }
            catch(Exception ex)
            {
                s_logger.ErrorException($"Error reading stream {Name}/{StreamId}", ex);
                NotifySubscriptionDropped(SubscriptionDroppedReason.StreamStoreError, ex);
                throw;
            }
        }

        private async Task Push(StreamMessage message)
        {
            _nextVersion = message.StreamVersion + 1;
            LastVersion = message.StreamVersion;

            try
            {
                await _streamMessageReceived(this, message, _disposed.Token).NotOnCapturedContext();
            }
            catch(Exception ex)
            {
                s_logger.ErrorException(
                    $"Exception with subscriber receiving message {Name}/{StreamId}" +
                    $"Message: {message}.",
                    ex);
                NotifySubscriptionDropped(SubscriptionDroppedReason.SubscriberError, ex);
                throw;
            }
        }

        private void NotifySubscriptionDropped(SubscriptionDroppedReason reason, Exception exception = null)
        {
            if(_notificationRaised.CompareExchange(true, false))
            {
                return;
            }

            try
            {
                s_logger.InfoException($"Subscription dropped {Name}/{StreamId}. Reason: {reason}", exception);
                _subscriptionDropped.Invoke(this, reason, exception);
            }
            catch(Exception ex)
            {
                s_logger.ErrorException(
                    $"Error notifying subscriber that subscription has been dropped ({Name}/{StreamId}).",
                    ex);
            }
        }
    }
}