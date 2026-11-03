using BlitzLegacy.Core.Serialization;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class JsonNodeTests
    {
        [Test]
        public void MissingLookupsReuseAnImmutableFallbackWithoutChangingExplicitNullFactories()
        {
            var root = JsonNode.NewObject(); var missing = root["not-authored"];
            Assert.AreSame(missing, root["another"]); Assert.AreSame(missing, missing[99]);
            Assert.AreEqual(JsonKind.Null, missing.Kind); Assert.AreEqual("null", missing.ToJson(false));
            Assert.AreEqual(7, missing.AsInt(7)); Assert.AreEqual(1d, missing.AsDouble(1));
            Assert.Throws<System.InvalidOperationException>(() => missing.Set("x", 1));
            Assert.Throws<System.InvalidOperationException>(() => missing.Add(JsonNode.Num(1)));
            Assert.AreNotSame(JsonNode.Null(), JsonNode.Null()); Assert.AreEqual("{}", root.ToJson(false));
        }

        [Test]
        public void RoundTrip_KeepsNestedValues()
        {
            var source = JsonNode.NewObject()
                .Set("name", "Blitz")
                .Set("level", 10)
                .Set("ratio", 0.75d)
                .Set("prem", true)
                .Set("tanks", JsonNode.NewArray().Add(JsonNode.Str("ms-1")).Add(JsonNode.Num(3)));

            var parsed = JsonNode.Parse(source.ToJson());

            Assert.AreEqual("Blitz", parsed["name"].AsString());
            Assert.AreEqual(10, parsed["level"].AsInt());
            Assert.AreEqual(0.75d, parsed["ratio"].AsDouble(), 0.0000001d);
            Assert.IsTrue(parsed["prem"].AsBool());
            Assert.AreEqual(2, parsed["tanks"].Count);
            Assert.AreEqual("ms-1", parsed["tanks"][0].AsString());
            Assert.AreEqual(3, parsed["tanks"][1].AsInt());
        }

        [Test]
        public void Writer_SortsKeysSoSavesStayDiffable()
        {
            var node = JsonNode.NewObject().Set("b", 1).Set("a", 2);

            Assert.AreEqual("{\"a\":2,\"b\":1}", node.ToJson(false));
            Assert.AreEqual("{\n  \"a\": 2,\n  \"b\": 1\n}", node.ToJson());
        }

        [Test]
        public void Writer_EscapesControlCharactersAndQuotes()
        {
            var tricky = JsonNode.Str("quote\" back\\ tab\tnew\n");

            Assert.AreEqual("\"quote\\\" back\\\\ tab\\tnew\\n\"", tricky.ToJson(false));
            Assert.AreEqual("quote\" back\\ tab\tnew\n", JsonNode.Parse(tricky.ToJson()).AsString());
        }

        [Test]
        public void Writer_UsesInvariantNumbersRegardlessOfLocale()
        {
            var node = JsonNode.NewObject().Set("whole", 42d).Set("fraction", 0.45d);

            Assert.AreEqual("{\"fraction\":0.45,\"whole\":42}", node.ToJson(false));
        }

        [Test]
        public void MissingKeysFallBackInsteadOfThrowing()
        {
            var node = JsonNode.Parse("{}");

            Assert.IsTrue(node["missing"].IsNull);
            Assert.AreEqual("fallback", node["missing"].AsString("fallback"));
            Assert.AreEqual(42, node["missing"].AsInt(42));
            Assert.IsTrue(node["missing"]["deeper"].IsNull);
            Assert.IsTrue(node["missing"][3].IsNull);
        }

        [Test]
        public void Parser_HandlesUnicodeEscapesAndWhitespace()
        {
            var parsed = JsonNode.Parse("  {\n\t\"ru\" : \"\\u0411\\u043b\\u0438\\u0446\"\n}  ");

            Assert.AreEqual("\u0411\u043b\u0438\u0446", parsed["ru"].AsString());
        }

        [Test]
        public void TryParse_ReportsFailureWithoutThrowing()
        {
            JsonNode parsed;

            Assert.IsFalse(JsonNode.TryParse("{ nope", out parsed));
            Assert.IsTrue(parsed.IsNull);
            Assert.IsTrue(JsonNode.TryParse("[1, 2, 3]", out parsed));
            Assert.AreEqual(3, parsed.Count);
        }
    }
}
