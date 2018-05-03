﻿namespace SqlStreamStore.HalClient.Serialization
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using SqlStreamStore.HalClient.Models;

    internal static class HalResourceJsonReader
    {
        public static async Task<IResource> ReadResource(
            JsonReader reader,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if(reader.TokenType == JsonToken.None)
            {
                await ReadNextToken(reader, cancellationToken);
            }

            await SkipComments(reader, cancellationToken);
            AssertNextTokenIsStartObject(reader);

            var resource = new Resource();

            while(await reader.ReadAsync(cancellationToken))
            {
                switch(reader.TokenType)
                {
                    case JsonToken.PropertyName:
                        var propertyName = reader.Value.ToString();
                        await ReadNextToken(reader, cancellationToken);

                        switch(propertyName)
                        {
                            case "_links":
                                resource.Links = await ReadLinks(reader, cancellationToken);
                                break;
                            case "_embedded":
                                resource.Embedded = await ReadEmbedded(reader, cancellationToken);
                                break;
                            default:
                                var value = await JToken.LoadAsync(reader, cancellationToken);

                                resource[propertyName] = value.Type == JTokenType.Null
                                    ? null
                                    : value;

                                break;
                        }

                        continue;
                    case JsonToken.EndObject:
                        await reader.ReadAsync(cancellationToken);
                        return resource;
                    case JsonToken.Comment:
                        continue;
                    default:
                        throw new JsonSerializationException($"Unexpected token encountered:{reader.TokenType}");
                }
            }

            return resource;
        }

        private static async Task<IList<ILink>> ReadLinks(
            JsonReader reader,
            CancellationToken cancellationToken)
        {
            await SkipComments(reader, cancellationToken);
            AssertNextTokenIsStartObject(reader);

            var links = new List<ILink>();

            while(await reader.ReadAsync(cancellationToken))
            {
                switch(reader.TokenType)
                {
                    case JsonToken.PropertyName:
                        var rel = reader.Value.ToString();
                        await ReadNextToken(reader, cancellationToken);
                        links.AddRange(await ReadLinks(reader, rel, cancellationToken));
                        continue;
                    case JsonToken.Comment:
                        continue;
                    case JsonToken.EndObject:
                        return links;
                    default:
                        throw new JsonSerializationException($"Unexpected token encountered:{reader.TokenType}");
                }
            }

            throw new JsonSerializationException("Unexpected end of tokens.");
        }

        private static async Task<Link[]> ReadLinks(
            JsonReader reader,
            string rel,
            CancellationToken cancellationToken)
        {
            switch(reader.TokenType)
            {
                case JsonToken.StartObject:
                    var link = (await JObject.LoadAsync(reader, cancellationToken)).ToObject<Link>();
                    link.Rel = rel;
                    return new[] { link };
                case JsonToken.StartArray:
                    return (await JArray.LoadAsync(reader, cancellationToken)).ToObject<Link[]>();
                default:
                    throw new JsonSerializationException($"Unexpected token encountered:{reader.TokenType}");
            }
        }

        private static async Task<IList<IResource>> ReadEmbedded(
            JsonReader reader,
            CancellationToken cancellationToken)
        {
            await SkipComments(reader, cancellationToken);
            AssertNextTokenIsStartObject(reader);

            var embedded = new List<IResource>();

            while(await reader.ReadAsync(cancellationToken))
            {
                switch(reader.TokenType)
                {
                    case JsonToken.PropertyName:
                        var rel = reader.Value.ToString();
                        await ReadNextToken(reader, cancellationToken);
                        embedded.AddRange(await ReadEmbedded(reader, rel, cancellationToken));
                        continue;
                    case JsonToken.Comment:
                        continue;
                    case JsonToken.EndObject:
                        await reader.ReadAsync(cancellationToken);
                        return embedded;
                    default:
                        throw new JsonSerializationException($"Unexpected token encountered:{reader.TokenType}");
                }
            }

            throw new JsonSerializationException("Unexpected end of tokens.");
        }

        private static async Task<IResource[]> ReadEmbedded(
            JsonReader reader,
            string rel,
            CancellationToken cancellationToken)
        {
            switch(reader.TokenType)
            {
                case JsonToken.StartObject:
                {
                    var resource = await ReadResource(reader, cancellationToken);
                    resource.Rel = rel;
                    return new[] { resource };
                }
                case JsonToken.StartArray:
                {
                    var resources = new List<IResource>();

                    await ReadNextToken(reader, cancellationToken);

                    while(reader.TokenType != JsonToken.EndArray)
                    {
                        var resource = await ReadResource(reader, cancellationToken);
                        resource.Rel = rel;

                        resources.Add(resource);
                    }

                    await ReadNextToken(reader, cancellationToken);

                    return resources.ToArray();
                }
                default:
                    throw new JsonSerializationException($"Unexpected token encountered:{reader.TokenType}");
            }
        }

        private static async Task SkipComments(JsonReader reader, CancellationToken cancellationToken)
        {
            while(reader.TokenType == JsonToken.Comment)
                if(!await reader.ReadAsync(cancellationToken))
                    throw new JsonSerializationException("Unexpected end of tokens.");
        }

        private static async Task ReadNextToken(JsonReader reader, CancellationToken cancellationToken)
        {
            if(!await reader.ReadAsync(cancellationToken))
                throw new JsonSerializationException("Unexpected end of tokens.");
        }

        private static void AssertNextTokenIsStartObject(JsonReader reader)
        {
            if(reader.TokenType != JsonToken.StartObject)
                throw new JsonSerializationException($"Unexpected token encounturd:{reader.TokenType}");
        }
    }
}