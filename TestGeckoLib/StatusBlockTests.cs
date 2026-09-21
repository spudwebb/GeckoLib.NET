using System.Collections.Generic;
using GeckoLib.NET.Packs;
using GeckoLib.NET.Struct;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestGeckoLib
{
    /// <summary>
    /// Patching the status block and the change notifications it raises.
    ///
    /// The contract that matters: a change is reported only when a field's DECODED value
    /// moved. The spa pushes raw bytes, and a byte can change without any field that
    /// overlaps it changing meaning.
    /// </summary>
    [TestClass]
    public class StatusBlockTests
    {
        private GeckoStatusBlock _block;
        private List<AccessorChangedEventArgs> _changes;

        [TestInitialize]
        public void SetUp()
        {
            _block = new GeckoStatusBlock();
            _changes = new List<AccessorChangedEventArgs>();
            _block.AccessorChanged += (sender, e) => _changes.Add(e);
        }

        private static AccessorDefinition Define(
            string key, EAccessorKind kind, int position, int? bitPosition = null, string[] items = null)
        {
            return new AccessorDefinition(
                key, "LogStructure/Group/" + key, kind, position, bitPosition, items, null, null, true);
        }

        private void Build(params AccessorDefinition[] definitions)
        {
            _block.BuildAccessors(new List<AccessorDefinition>(definitions));
        }

        [TestMethod]
        public void TheBlockStartsAsAKilobyteOfZeroes()
        {
            Assert.AreEqual(1024, _block.Snapshot().Length);
            foreach (byte value in _block.Snapshot())
            {
                Assert.AreEqual(0, value);
            }
        }

        [TestMethod]
        public void PatchingASegmentChangesOnlyThoseBytes()
        {
            _block.ReplaceSegment(100, new byte[] { 1, 2, 3 });

            byte[] snapshot = _block.Snapshot();
            Assert.AreEqual(0, snapshot[99]);
            Assert.AreEqual(1, snapshot[100]);
            Assert.AreEqual(2, snapshot[101]);
            Assert.AreEqual(3, snapshot[102]);
            Assert.AreEqual(0, snapshot[103]);
        }

        [TestMethod]
        public void AFieldWhoseValueMovedRaisesExactlyOneChange()
        {
            Build(Define("Temp", EAccessorKind.Word, 100));

            _block.ReplaceSegment(100, new byte[] { 0x01, 0x00 });

            Assert.AreEqual(1, _changes.Count);
            Assert.AreEqual("Temp", _changes[0].Accessor.Key);
            Assert.AreEqual(0, _changes[0].OldValue);
            Assert.AreEqual(256, _changes[0].NewValue);
        }

        [TestMethod]
        public void RewritingTheSameBytesRaisesNothing()
        {
            Build(Define("Temp", EAccessorKind.Word, 100));
            _block.ReplaceSegment(100, new byte[] { 0x01, 0x00 });
            _changes.Clear();

            _block.ReplaceSegment(100, new byte[] { 0x01, 0x00 });

            Assert.AreEqual(0, _changes.Count);
        }

        /// <summary>
        /// The important case: a byte changed, and a field overlaps it, but the field's
        /// own bits did not move. geckolib compares decoded values for exactly this.
        /// </summary>
        [TestMethod]
        public void AByteChangeThatDoesNotMoveAFieldsOwnBitsRaisesNothing()
        {
            Build(Define("Flag", EAccessorKind.Bool, 100, bitPosition: 0));
            _block.ReplaceSegment(100, new byte[] { 0x01 });
            _changes.Clear();

            // Flip every bit except bit 0.
            _block.ReplaceSegment(100, new byte[] { 0xff });

            Assert.AreEqual(true, _block["Flag"].Value);
            Assert.AreEqual(0, _changes.Count);
        }

        [TestMethod]
        public void AChangeElsewhereInTheBlockDoesNotDisturbAField()
        {
            Build(Define("Temp", EAccessorKind.Word, 100));
            _changes.Clear();

            _block.ReplaceSegment(200, new byte[] { 0xff, 0xff });

            Assert.AreEqual(0, _changes.Count);
        }

        [TestMethod]
        public void EveryFieldOverlappingAPatchIsConsidered()
        {
            Build(
                Define("Low", EAccessorKind.Bool, 100, bitPosition: 0),
                Define("High", EAccessorKind.Bool, 100, bitPosition: 7),
                Define("Whole", EAccessorKind.Byte, 100));

            _block.ReplaceSegment(100, new byte[] { 0x81 });

            Assert.AreEqual(3, _changes.Count);
        }

        [TestMethod]
        public void AFieldRaisesItsOwnChangedEventToo()
        {
            Build(Define("Temp", EAccessorKind.Word, 100));

            AccessorChangedEventArgs seen = null;
            _block["Temp"].Changed += (sender, e) => seen = e;

            _block.ReplaceSegment(100, new byte[] { 0x00, 0x2a });

            Assert.IsNotNull(seen);
            Assert.AreEqual(42, seen.NewValue);
        }

        [TestMethod]
        public void APatchThatWouldRunPastTheEndOfTheBlockIsRejected()
        {
            Assert.ThrowsException<System.ArgumentOutOfRangeException>(
                () => _block.ReplaceSegment(1023, new byte[] { 1, 2 }));
        }

        /// <summary>
        /// The accessor table is merged in order, so an accessory's definitions override
        /// the base pack's when they collide (geckolib async_spastruct.py:60-75).
        /// </summary>
        [TestMethod]
        public void LaterDefinitionsWinWhenTwoSetsDeclareTheSameKey()
        {
            _block.BuildAccessors(
                new List<AccessorDefinition> { Define("Shared", EAccessorKind.Byte, 100) },
                new List<AccessorDefinition> { Define("Shared", EAccessorKind.Byte, 200) });

            Assert.AreEqual(200, _block["Shared"].Position);
            Assert.AreEqual(1, _block.Accessors.Count);
        }

        [TestMethod]
        public void AnUnknownKeyReadsAsNullRatherThanThrowing()
        {
            Build(Define("Temp", EAccessorKind.Word, 100));

            Assert.IsNull(_block["NoSuchField"]);
        }

        [TestMethod]
        public void TheFieldsCoveringAPushedPositionCanBeNamed()
        {
            Build(
                Define("Word", EAccessorKind.Word, 100),
                Define("Byte", EAccessorKind.Byte, 101),
                Define("Elsewhere", EAccessorKind.Byte, 300));

            var names = new List<string>();
            foreach (GeckoStructAccessor accessor in _block.AccessorsAt(100, 2))
            {
                names.Add(accessor.Key);
            }

            CollectionAssert.AreEquivalent(new[] { "Word", "Byte" }, names);
        }
    }
}
