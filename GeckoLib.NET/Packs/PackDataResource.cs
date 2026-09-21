using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace GeckoLib.NET.Packs
{
    /// <summary>
    /// Access to the embedded spa pack definitions.
    ///
    /// geckolib ships these as 187 generated Python modules (~190k lines) built from
    /// Gecko's SpaPackStruct XML. That XML is not in the geckolib repository and no
    /// source for it is recorded there, so tools/extract_packdata.py reads the generated
    /// modules and emits the data file embedded here.
    ///
    /// This type only exposes the raw bytes. Parsing them into a pack registry arrives
    /// with the connection layer, which is the first thing that needs them.
    /// </summary>
    public static class PackDataResource
    {
        /// <summary>Logical name of the embedded resource.</summary>
        public const string RESOURCE_NAME = "packdata.json.gz";

        /// <summary>
        /// Open the pack data as a stream of decompressed UTF-8 JSON.
        /// The caller owns the returned stream.
        /// </summary>
        public static Stream Open()
        {
            Assembly assembly = typeof(PackDataResource).Assembly;
            Stream compressed = assembly.GetManifestResourceStream(RESOURCE_NAME);
            if (compressed == null)
            {
                throw new InvalidOperationException(
                    "Embedded resource '" + RESOURCE_NAME + "' is missing from " +
                    assembly.GetName().Name + ". Run tools/extract_packdata.py and rebuild.");
            }

            return new GZipStream(compressed, CompressionMode.Decompress);
        }

        /// <summary>Read the pack data as decompressed UTF-8 JSON.</summary>
        public static byte[] ReadAllBytes()
        {
            using (Stream stream = Open())
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }
    }
}
