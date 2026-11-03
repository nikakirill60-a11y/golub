using System;
using System.Collections.Generic;

namespace BlitzLegacy.Core.Serialization
{
    /// <summary>Shared conversions for the collection shapes used by saves.</summary>
    public static class JsonMap
    {
        public static JsonNode FromStrings(IEnumerable<string> values)
        {
            var node = JsonNode.NewArray();
            if (values == null)
            {
                return node;
            }

            foreach (var value in values)
            {
                node.Add(JsonNode.Str(value ?? string.Empty));
            }

            return node;
        }

        public static void ReadStrings(JsonNode node, List<string> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            into.Clear();
            if (node == null)
            {
                return;
            }

            foreach (var item in node.Items)
            {
                into.Add(item.AsString());
            }
        }

        public static JsonNode FromInts(IEnumerable<int> values)
        {
            var node = JsonNode.NewArray();
            if (values == null)
            {
                return node;
            }

            foreach (var value in values)
            {
                node.Add(JsonNode.Num(value));
            }

            return node;
        }

        public static void ReadInts(JsonNode node, List<int> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            into.Clear();
            if (node == null)
            {
                return;
            }

            foreach (var item in node.Items)
            {
                into.Add(item.AsInt());
            }
        }

        public static JsonNode FromIntMap(IDictionary<string, int> map)
        {
            var node = JsonNode.NewObject();
            if (map == null)
            {
                return node;
            }

            foreach (var pair in map)
            {
                node.Set(pair.Key, pair.Value);
            }

            return node;
        }

        public static void ReadIntMap(JsonNode node, Dictionary<string, int> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            into.Clear();
            if (node == null)
            {
                return;
            }

            foreach (var pair in node.Fields)
            {
                into[pair.Key] = pair.Value.AsInt();
            }
        }
    }
}
