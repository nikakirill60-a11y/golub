using System;
using BlitzLegacy.Core.Telemetry;
using BlitzLegacy.Runtime.Telemetry;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class DeploymentSettingsTests
    {
        private string _previousDsn;

        [SetUp]
        public void SaveEnvironment()
        {
            _previousDsn = Environment.GetEnvironmentVariable(TelemetryPolicy.DsnEnvironmentVariable);
        }

        [TearDown]
        public void RestoreEnvironment()
        {
            Environment.SetEnvironmentVariable(TelemetryPolicy.DsnEnvironmentVariable, _previousDsn);
        }

        [TestCase("http://public@example.invalid/1")]
        [TestCase("https://public:secret@example.invalid/1")]
        [TestCase("not-a-dsn")]
        public void InvalidOrPrivateDsnDisablesTelemetry(string dsn)
        {
            Environment.SetEnvironmentVariable(TelemetryPolicy.DsnEnvironmentVariable, dsn);
            Assert.That(BlitzTelemetry.DeploymentDsn(), Is.Empty);
        }

        [Test]
        public void PublicDsnCanBeSuppliedAtRuntime()
        {
            const string dsn = "https://public@example.invalid/1";
            Environment.SetEnvironmentVariable(TelemetryPolicy.DsnEnvironmentVariable, dsn);
            Assert.That(BlitzTelemetry.DeploymentDsn(), Is.EqualTo(dsn));
        }
    }
}
