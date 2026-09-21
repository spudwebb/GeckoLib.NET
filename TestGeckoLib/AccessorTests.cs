using System.Collections.Generic;
using GeckoLib.NET.Packs;
using GeckoLib.NET.Struct;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestGeckoLib
{
    /// <summary>
    /// Decoding and encoding of pack structure fields.
    ///
    /// This is where a silent mistake is most expensive: a wrong mask or a wrong
    /// temperature conversion produces a plausible number rather than an error.
    /// </summary>
    [TestClass]
    public class AccessorTests
    {
        private GeckoStatusBlock _block;

        [TestInitialize]
        public void SetUp()
        {
            _block = new GeckoStatusBlock();
        }

        private static AccessorDefinition Define(
            string key,
            EAccessorKind kind,
            int position,
            int? bitPosition = null,
            string[] items = null,
            int? size = null,
            int? maxItems = null,
            bool writable = true)
        {
            return new AccessorDefinition(
                key, "LogStructure/Group/" + key, kind, position, bitPosition, items, size, maxItems, writable);
        }

        private void Build(params AccessorDefinition[] definitions)
        {
            _block.BuildAccessors(new List<AccessorDefinition>(definitions));
        }

        [TestMethod]
        public void AByteFieldReadsASingleByte()
        {
            Build(Define("Value", EAccessorKind.Byte, 10));
            _block.ReplaceSegment(10, new byte[] { 0x2a });

            Assert.AreEqual(42, _block["Value"].Value);
            Assert.AreEqual(1, _block["Value"].Length);
        }

        [TestMethod]
        public void AWordFieldReadsTwoBytesBigEndian()
        {
            Build(Define("Value", EAccessorKind.Word, 10));
            _block.ReplaceSegment(10, new byte[] { 0x02, 0xbe });

            Assert.AreEqual(702, _block["Value"].Value);
            Assert.AreEqual(2, _block["Value"].Length);
        }

        [TestMethod]
        public void ABoolFieldReadsOneBitAtItsPosition()
        {
            Build(Define("Flag", EAccessorKind.Bool, 10, bitPosition: 3));

            _block.ReplaceSegment(10, new byte[] { 0x00 });
            Assert.AreEqual(false, _block["Flag"].Value);

            _block.ReplaceSegment(10, new byte[] { 0x08 });
            Assert.AreEqual(true, _block["Flag"].Value);

            // A different bit must not register.
            _block.ReplaceSegment(10, new byte[] { 0x04 });
            Assert.AreEqual(false, _block["Flag"].Value);
        }

        [TestMethod]
        public void AnEnumFieldResolvesItsRawValueToAName()
        {
            Build(Define("Mode", EAccessorKind.Enum, 10, items: new[] { "OFF", "LOW", "HIGH" }));

            _block.ReplaceSegment(10, new byte[] { 0x02 });
            Assert.AreEqual("HIGH", _block["Mode"].Value);
        }

        /// <summary>
        /// geckolib reports out-of-range enum values as "Unknown" rather than throwing,
        /// because real spas do produce them.
        /// </summary>
        [TestMethod]
        public void AnEnumValueOutsideItsRangeReadsAsUnknown()
        {
            Build(Define("Mode", EAccessorKind.Enum, 10, items: new[] { "OFF", "LOW", "HIGH" }));

            _block.ReplaceSegment(10, new byte[] { 0x07 });
            Assert.AreEqual("Unknown", _block["Mode"].Value);
        }

        /// <summary>
        /// A bit-packed enum shifts by its bit position and masks to the width implied by
        /// its item count. This is the "UD_P1" shape from inxm-log-2: a 2-byte field read
        /// at bit 14 with four possible values, so mask 3.
        /// </summary>
        [TestMethod]
        public void ABitPackedEnumShiftsAndMasksToItsDeclaredWidth()
        {
            Build(Define(
                "UD_P1", EAccessorKind.Enum, 10,
                bitPosition: 14,
                items: new[] { "OFF", "LOW", "HIGH" },
                size: 2,
                maxItems: 4));

            Assert.AreEqual(3, _block["UD_P1"].Definition.BitMask);

            // 0x8000 >> 14 == 2.
            _block.ReplaceSegment(10, new byte[] { 0x80, 0x00 });
            Assert.AreEqual("HIGH", _block["UD_P1"].Value);

            // Bits below the field must be ignored.
            _block.ReplaceSegment(10, new byte[] { 0x40, 0xff });
            Assert.AreEqual("LOW", _block["UD_P1"].Value);
        }

        /// <summary>
        /// The mask width is ceil(log2(max(n, 2))). These are every item count that
        /// actually occurs across the 187 shipped pack definitions.
        /// </summary>
        [TestMethod]
        public void TheBitMaskWidensToHoldTheDeclaredNumberOfValues()
        {
            Assert.AreEqual(1, MaskFor(2));
            Assert.AreEqual(3, MaskFor(4));
            Assert.AreEqual(7, MaskFor(5));
            Assert.AreEqual(7, MaskFor(6));
            Assert.AreEqual(7, MaskFor(8));
            Assert.AreEqual(15, MaskFor(16));
            Assert.AreEqual(63, MaskFor(63));
        }

        private static int MaskFor(int maxItems)
        {
            return Define("X", EAccessorKind.Enum, 0, bitPosition: 0, maxItems: maxItems).BitMask;
        }

        [TestMethod]
        public void ATimeFieldReadsHoursFromTheHighByteAndMinutesFromTheLow()
        {
            Build(Define("FiltDur", EAccessorKind.Time, 10));

            _block.ReplaceSegment(10, new byte[] { 0x02, 0x1e });
            Assert.AreEqual("02:30", _block["FiltDur"].Value);

            _block.ReplaceSegment(10, new byte[] { 0x17, 0x3b });
            Assert.AreEqual("23:59", _block["FiltDur"].Value);
        }

        [TestMethod]
        public void ATimeFieldEncodesHoursIntoTheHighByte()
        {
            Build(Define("FiltDur", EAccessorKind.Time, 10));
            _block.ReplaceSegment(10, new byte[] { 0x00, 0x00 });

            _block["FiltDur"].DirectUpdate = true;
            _block["FiltDur"].SetValueAsync("02:30").GetAwaiter().GetResult();

            Assert.AreEqual("02:30", _block["FiltDur"].Value);
            Assert.AreEqual(0x02, _block.Snapshot()[10]);
            Assert.AreEqual(0x1e, _block.Snapshot()[11]);
        }

        /// <summary>
        /// Temperatures are stored as tenths of a degree Fahrenheit offset from freezing,
        /// and the conversion depends on the spa's own TempUnits field.
        /// </summary>
        [TestMethod]
        public void ATemperatureIsConvertedAccordingToTheSpasConfiguredUnits()
        {
            Build(
                Define("TempUnits", EAccessorKind.Enum, 5, items: new[] { "F", "C" }),
                Define("RhWaterTemp", EAccessorKind.Temp, 10));

            // 684 tenths of a degree above freezing.
            _block.ReplaceSegment(10, new byte[] { 0x02, 0xac });

            _block.ReplaceSegment(5, new byte[] { 0x01 });
            Assert.AreEqual("C", _block["TempUnits"].Value);
            Assert.AreEqual(38.0, (double)_block["RhWaterTemp"].Value, 0.001);

            _block.ReplaceSegment(5, new byte[] { 0x00 });
            Assert.AreEqual("F", _block["TempUnits"].Value);
            Assert.AreEqual(100.4, (double)_block["RhWaterTemp"].Value, 0.001);
        }

        [TestMethod]
        public void ATemperatureIsEncodedBackIntoTheSameRawValue()
        {
            Build(
                Define("TempUnits", EAccessorKind.Enum, 5, items: new[] { "F", "C" }),
                Define("SetpointG", EAccessorKind.Temp, 10));

            _block.ReplaceSegment(5, new byte[] { 0x01 });
            _block["SetpointG"].DirectUpdate = true;
            _block["SetpointG"].SetValueAsync(38.0).GetAwaiter().GetResult();

            Assert.AreEqual(38.0, (double)_block["SetpointG"].Value, 0.001);
            Assert.AreEqual(684, _block["SetpointG"].RawValue);
        }

        [TestMethod]
        public void WritingABitPackedFieldLeavesTheSurroundingBitsAlone()
        {
            Build(Define("Flag", EAccessorKind.Bool, 10, bitPosition: 2));
            _block.ReplaceSegment(10, new byte[] { 0xfb });

            _block["Flag"].DirectUpdate = true;
            _block["Flag"].SetValueAsync(true).GetAwaiter().GetResult();

            Assert.AreEqual(0xff, _block.Snapshot()[10]);
        }

        [TestMethod]
        public void AReadOnlyFieldRefusesToBeWritten()
        {
            Build(Define("Locked", EAccessorKind.Byte, 10, writable: false));

            Assert.IsFalse(_block["Locked"].IsWritable);
            Assert.ThrowsException<System.InvalidOperationException>(
                () => _block["Locked"].SetValueAsync(1).GetAwaiter().GetResult());
        }

        [TestMethod]
        public void SettingAnEnumToAValueItDoesNotHaveIsRejected()
        {
            Build(Define("Mode", EAccessorKind.Enum, 10, items: new[] { "OFF", "LOW", "HIGH" }));
            _block["Mode"].DirectUpdate = true;

            Assert.ThrowsException<System.ArgumentException>(
                () => _block["Mode"].SetValueAsync("TURBO").GetAwaiter().GetResult());
        }

        [TestMethod]
        public void TheTagIsTheLastSegmentOfTheStructurePath()
        {
            AccessorDefinition definition = Define("RhWaterTemp", EAccessorKind.Temp, 10);
            Assert.AreEqual("RhWaterTemp", definition.Tag);
            Assert.AreEqual("LogStructure/Group/RhWaterTemp", definition.Path);
        }
    }
}
