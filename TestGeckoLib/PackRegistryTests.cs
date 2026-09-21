using System.Collections.Generic;
using GeckoLib.NET.Packs;
using GeckoLib.NET.Struct;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestGeckoLib
{
    /// <summary>
    /// The embedded pack definitions.
    ///
    /// tools/verify_packdata.py already checks all 21,586 accessors against geckolib
    /// itself; these tests check that the data survives the trip through the embedded
    /// resource and the C# reader.
    /// </summary>
    [TestClass]
    public class PackRegistryTests
    {
        private static PackRegistry _registry;

        [ClassInitialize]
        public static void LoadOnce(TestContext context)
        {
            _registry = PackRegistry.LoadEmbedded();
        }

        [TestMethod]
        public void TheEmbeddedDefinitionsCarryTheirSpaPackStructRevision()
        {
            Assert.AreEqual("40.00", _registry.Revision);
        }

        [TestMethod]
        public void APlatformIsFoundByNameWhateverItsCase()
        {
            Assert.IsTrue(_registry.TryGetPack("inXE", out PackDefinition pack));
            Assert.AreEqual("InXE", pack.Name);
            Assert.AreEqual(1, pack.PlatformType);
            Assert.AreEqual("aMainControl", pack.PlatformSegment);
            Assert.AreEqual("40.00", pack.Revision);
            Assert.IsFalse(pack.IsAccessory);

            Assert.IsTrue(_registry.TryGetPack("INXE", out PackDefinition upper));
            Assert.AreEqual("InXE", upper.Name);
        }

        [TestMethod]
        public void AnUnknownPlatformIsReportedRatherThanThrowing()
        {
            Assert.IsFalse(_registry.TryGetPack("nosuchpack", out _));
            Assert.IsFalse(_registry.TryGetConfig("inXE", 999, out _));
            Assert.IsFalse(_registry.TryGetLog("inXE", 999, out _));
        }

        [TestMethod]
        public void AnAccessoryPlatformIsMarkedAsOne()
        {
            Assert.IsTrue(_registry.TryGetPack("inclear-32k", out PackDefinition pack));
            Assert.IsTrue(pack.IsAccessory);
        }

        /// <summary>
        /// inXE config 60 / log 58 is what the spa on this network reports, so it is the
        /// combination most worth pinning down.
        /// </summary>
        [TestMethod]
        public void TheLogStructureForInXe58HasItsRangeAndDeviceKeys()
        {
            Assert.IsTrue(_registry.TryGetLog("inXE", 58, out LogStructDefinition log));

            Assert.AreEqual(58, log.Version);
            Assert.AreEqual(256, log.Begin);
            Assert.AreEqual(479, log.End);

            CollectionAssert.Contains(new List<string>(log.AllDeviceKeys), "P1");
            CollectionAssert.Contains(new List<string>(log.AllDeviceKeys), "BL");
            CollectionAssert.Contains(new List<string>(log.AllDeviceKeys), "LI");
            CollectionAssert.Contains(new List<string>(log.UserDemandKeys), "UdP1");
        }

        [TestMethod]
        public void TheConfigStructureForInXe60ListsItsOutputs()
        {
            Assert.IsTrue(_registry.TryGetConfig("inXE", 60, out ConfigStructDefinition config));

            Assert.AreEqual(60, config.Version);
            CollectionAssert.Contains(new List<string>(config.OutputKeys), "Out1");
            Assert.IsTrue(config.Accessors.Count > 100);
        }

        /// <summary>
        /// A spot check of individual fields, against the values geckolib's own generated
        /// module declares for inxe-log-58.
        /// </summary>
        [TestMethod]
        public void IndividualFieldsMatchWhatTheDefinitionsDeclare()
        {
            Assert.IsTrue(_registry.TryGetLog("inXE", 58, out LogStructDefinition log));

            AccessorDefinition waterTemp = Find(log, "RhWaterTemp");
            Assert.AreEqual(EAccessorKind.Temp, waterTemp.Kind);
            Assert.AreEqual(317, waterTemp.Position);
            Assert.AreEqual(2, waterTemp.Length);
            Assert.IsFalse(waterTemp.IsWritable);

            AccessorDefinition setPoint = Find(log, "RealSetPointG");
            Assert.AreEqual(EAccessorKind.Temp, setPoint.Kind);
            Assert.AreEqual(275, setPoint.Position);

            AccessorDefinition swmAdc = Find(log, "SwmAdc");
            Assert.AreEqual(EAccessorKind.Word, swmAdc.Kind);
            Assert.AreEqual(355, swmAdc.Position);
        }

        private static AccessorDefinition Find(ConfigStructDefinition structure, string key)
        {
            foreach (AccessorDefinition definition in structure.Accessors)
            {
                if (definition.Key == key) return definition;
            }

            Assert.Fail("No accessor called " + key);
            return null;
        }

        /// <summary>
        /// The whole point of the extraction: real definitions, bound to a real block,
        /// decode to sensible values.
        /// </summary>
        [TestMethod]
        public void RealDefinitionsBindToABlockAndDecodeATemperature()
        {
            Assert.IsTrue(_registry.TryGetConfig("inXE", 60, out ConfigStructDefinition config));
            Assert.IsTrue(_registry.TryGetLog("inXE", 58, out LogStructDefinition log));

            var block = new GeckoStatusBlock();
            block.BuildAccessors(config.Accessors, log.Accessors);

            Assert.IsNotNull(block["RhWaterTemp"]);
            Assert.IsNotNull(block["TempUnits"]);

            // 684 tenths of a degree above freezing, reported in Celsius, is 38.0.
            block.ReplaceSegment(317, new byte[] { 0x02, 0xac });
            SetTempUnits(block, "C");

            Assert.AreEqual(38.0, (double)block["RhWaterTemp"].Value, 0.001);
        }

        private static void SetTempUnits(GeckoStatusBlock block, string units)
        {
            block.MakeEverythingWritable();
            GeckoStructAccessor accessor = block["TempUnits"];
            accessor.DirectUpdate = true;
            accessor.SetValueAsync(units).GetAwaiter().GetResult();
        }
    }
}
