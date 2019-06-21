﻿namespace SqlStreamStore
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Shouldly;
    using SqlStreamStore.Streams;
    using Xunit;
    using static Streams.Deleted;

    public partial class AcceptanceTests
    {
        [Fact, Trait("Category", "DeleteEvent")]
        public async Task When_delete_message_then_message_should_be_removed_from_stream()
        {
            const string streamId = "stream";
            var newStreamMessages = CreateNewStreamMessages(1, 2, 3);
            await store.AppendToStream(streamId, ExpectedVersion.NoStream, newStreamMessages);
            var idToDelete = newStreamMessages[1].MessageId;

            await store.DeleteMessage(streamId, idToDelete);

            var messages = await store.ReadStreamForwards(streamId, StreamVersion.Start, 3).ToArrayAsync();

            messages.Length.ShouldBe(2);
            messages.Any(e => e.MessageId == idToDelete).ShouldBeFalse();
        }

        [Fact, Trait("Category", "DeleteEvent")]
        public async Task When_delete_message_then_deleted_message_should_be_appended_to_deleted_stream()
        {
            const string streamId = "stream";
            var newStreamMessages = CreateNewStreamMessages(1, 2, 3);
            await store.AppendToStream(streamId, ExpectedVersion.NoStream, newStreamMessages);
            var messageIdToDelete = newStreamMessages[1].MessageId;

            await store.DeleteMessage(streamId, messageIdToDelete);

            var messages = await store.ReadStreamBackwards(DeletedStreamId, StreamVersion.End, 1).ToArrayAsync();
            var message = messages.Single();
            var messageDeleted = await message.GetJsonDataAs<MessageDeleted>();
            message.Type.ShouldBe(MessageDeletedMessageType);
            messageDeleted.StreamId.ShouldBe(streamId);
            messageDeleted.MessageId.ShouldBe(messageIdToDelete);
        }

        [Fact, Trait("Category", "DeleteEvent")]
        public async Task When_delete_message_that_does_not_exist_then_nothing_should_happen()
        {
            const string streamId = "stream";
            var newStreamMessages = CreateNewStreamMessages(1, 2, 3);
            await store.AppendToStream(streamId, ExpectedVersion.NoStream, newStreamMessages);
            var initialHead = await store.ReadHeadPosition();

            await store.DeleteMessage(streamId, Guid.NewGuid());

            var messages = await store.ReadStreamForwards(streamId, StreamVersion.Start, 3).ToArrayAsync();
            messages.Length.ShouldBe(3);
            var subsequentHead = await store.ReadHeadPosition();
            subsequentHead.ShouldBe(initialHead);
        }

        [Fact, Trait("Category", "DeleteEvent")]
        public async Task When_delete_last_message_in_stream_and_append_then_it_should_have_subsequent_version_number()
        {
            const string streamId = "stream";
            var messages = CreateNewStreamMessages(1, 2, 3);
            await store.AppendToStream(streamId, ExpectedVersion.NoStream, messages);
            await store.DeleteMessage(streamId, messages.Last().MessageId);

            messages = CreateNewStreamMessages(4);
            await store.AppendToStream(streamId, 2, messages);

            var result = await store.ReadStreamForwards(streamId, StreamVersion.Start, 3);

            var count = await result.CountAsync();

            count.ShouldBe(3);
            result.LastStreamVersion.ShouldBe(3);
        }

        [Fact, Trait("Category", "DeleteEvent")]
        public async Task When_delete_a_messages_from_stream_with_then_can_read_all_forwards()
        {
            string streamId = "stream-1";
            await AppendMessages(store, streamId, 2);
            var result = await store.ReadStreamForwards(streamId, StreamVersion.Start, 2);
            await store.DeleteMessage(streamId, (await result.FirstAsync()).MessageId);

            result = await store.ReadStreamForwards(streamId, StreamVersion.Start, 2);

            var count = await result.CountAsync();

            count.ShouldBe(1);
            result.LastStreamVersion.ShouldBe(1);
        }

        [Fact, Trait("Category", "DeleteEvent")]
        public async Task When_delete_all_messages_from_stream_with_1_messages_then_can_read_all_forwards()
        {
            string streamId = "stream-1";
            await AppendMessages(store, streamId, 1);
            var result = await store.ReadStreamForwards(streamId, StreamVersion.Start, 2);
            await store.DeleteMessage(streamId, (await result.SingleAsync()).MessageId);

            result = await store.ReadStreamForwards(streamId, StreamVersion.Start, 2);

            var countAsync = await result.CountAsync();
            countAsync.ShouldBe(0);
            result.LastStreamVersion.ShouldBe(0);
        }

        [Fact, Trait("Category", "DeleteEvent")]
        public async Task When_delete_all_messages_from_stream_with_multiple_messages_then_can_read_all_forwards()
        {
            string streamId = "stream-1";
            await AppendMessages(store, streamId, 2);
            var result = await store.ReadStreamForwards(streamId, StreamVersion.Start, 2);
            var messages = await result.ToArrayAsync();
            await store.DeleteMessage(streamId, messages[0].MessageId);
            await store.DeleteMessage(streamId, messages[1].MessageId);

            result = await store.ReadStreamForwards(streamId, StreamVersion.Start, 2);

            var count = await result.CountAsync();
            count.ShouldBe(0);
            result.LastStreamVersion.ShouldBe(1);
        }

        [Theory, Trait("Category", "DeleteEvent")]
        [InlineData("stream/id")]
        [InlineData("stream%id")]
        public async Task When_delete_stream_message_with_url_encodable_characters_then_should_not_throw(
            string streamId)
        {
            var newStreamMessages = CreateNewStreamMessages(1);
            await store.AppendToStream(streamId, ExpectedVersion.NoStream, newStreamMessages);

            await store.DeleteMessage(streamId, newStreamMessages[0].MessageId);
        }
    }
}