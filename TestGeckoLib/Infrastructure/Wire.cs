using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TestGeckoLib.Infrastructure
{
    /// <summary>
    /// Helpers for writing the byte-exact expectations taken from geckolib's own
    /// protocol tests (tests/test_protocol_*.py).
    ///
    /// Expected values are built from pieces rather than from escaped string literals,
    /// because C#'s \x escape is variable length - "\x0f\x02" does not mean what it looks
    /// like it means.
    /// </summary>
    internal static class Wire
    {
        private static readonly Encoding LATIN1 = Encoding.GetEncoding(28591);

        /// <summary>
        /// Build a byte array from a mix of strings (latin-1) and byte values.
        /// </summary>
        public static byte[] Build(params object[] parts)
        {
            var bytes = new List<byte>();
            foreach (object part in parts)
            {
                if (part is string text)
                {
                    bytes.AddRange(LATIN1.GetBytes(text));
                }
                else if (part is byte[] raw)
                {
                    bytes.AddRange(raw);
                }
                else if (part is int value)
                {
                    if (value < 0 || value > 255)
                    {
                        throw new ArgumentOutOfRangeException(nameof(parts), "Byte values must be 0-255");
                    }

                    bytes.Add((byte)value);
                }
                else
                {
                    throw new ArgumentException("Unsupported part type " + part.GetType().Name, nameof(parts));
                }
            }

            return bytes.ToArray();
        }

        /// <summary>
        /// Assert two byte arrays match, reporting the difference the way Python's repr
        /// would so a failure is readable.
        /// </summary>
        public static void AssertEqual(byte[] expected, byte[] actual)
        {
            if (expected == null || actual == null)
            {
                Assert.AreEqual(expected, actual);
                return;
            }

            if (expected.Length != actual.Length || !Same(expected, actual))
            {
                Assert.Fail(
                    "Bytes differ.\n  expected: {0}\n  actual:   {1}",
                    Describe(expected),
                    Describe(actual));
            }
        }

        private static bool Same(byte[] left, byte[] right)
        {
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
        }

        /// <summary>Render bytes the way Python's repr does.</summary>
        public static string Describe(byte[] buffer)
        {
            var text = new StringBuilder(buffer.Length + 8);
            foreach (byte value in buffer)
            {
                if (value >= 32 && value < 127)
                {
                    text.Append((char)value);
                }
                else
                {
                    text.Append("\\x").Append(value.ToString("x2"));
                }
            }

            return text.ToString();
        }
    }
}
