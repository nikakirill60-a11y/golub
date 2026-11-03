using System.IO;
using BlitzLegacy.Core.Tanks;
using BlitzLegacy.EditorTools;
using BlitzLegacy.Runtime.Assets;
using BlitzLegacy.Runtime.Tanks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.Tests
{
    /// <summary>
    /// The hangar asks every owned vehicle whether it has a model. The list
    /// under Resources/Data answers that without loading a single mesh, so
    /// it has to say exactly what loading would.
    /// </summary>
    public sealed class TankModelIndexTests
    {
        [Test]
        public void Index_IsUpToDateWithTheModelsFolder()
        {
            Assert.AreEqual(TankModelIndexWriter.Build(), File.ReadAllText(TankModelIndexWriter.IndexPath),
                "Resources/Data/tank_models.txt is stale: reimport Models/Tanks or run TankModelIndexWriter.Write()");
        }

        [Test]
        public void HasModel_AgreesWithLoadingTheModel()
        {
            var tanks = TanksDatabase.Parse(BlitzResources.LoadText(BlitzResources.TanksData));
            Assert.Greater(tanks.Count, 500);
            try
            {
                foreach (var card in tanks.Tanks)
                {
                    var loaded = Resources.Load<GameObject>(TankModelAssets.For(card).ModelResource) != null;
                    Assert.AreEqual(loaded, TankAssetSource.HasModel(card), card.Name);
                }
            }
            finally
            {
                EditorUtility.UnloadUnusedAssetsImmediate();
            }
        }

        [Test]
        public void Parse_ReadsOneIdPerLineWhateverTheCase()
        {
            var ids = TankModelIndex.Parse("MS1\r\n\nT62A\n");
            Assert.AreEqual(2, ids.Count);
            Assert.IsTrue(ids.Contains("ms1"));
            Assert.IsTrue(ids.Contains("T62A"));
            Assert.AreEqual(0, TankModelIndex.Parse(string.Empty).Count);
        }

        [Test]
        public void HasModel_IsFalseForAVehicleWithoutAModel()
        {
            Assert.IsFalse(TankAssetSource.HasModel(null));
            Assert.IsFalse(TankAssetSource.HasModel(new TankInfo { Name = "no-such-tank" }));
            bool shipped;
            Assert.IsTrue(TankModelIndex.TryContains("MS1", out shipped));
            Assert.IsTrue(shipped);
        }
    }
}
