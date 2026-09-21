using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GeckoLib.NET;
using GeckoLib.NET.Devices;
using GeckoLib.NET.Packs;
using GeckoLib.NET.Struct;

namespace GeckoConsoleApp
{
    /// <summary>
    /// Manual harness for GeckoLib.NET, in the style of the other Test/Console projects
    /// in this tree.
    ///
    /// Normally this starts an interactive shell (see <see cref="Shell"/>). It also has
    /// one non-interactive mode, --block, which decodes a saved status block offline;
    /// tools/snapshot_conformance.py drives that to diff this implementation against
    /// geckolib.
    /// </summary>
    internal static class Program
    {
        // Stable per-installation identifier. The spa keys its push subscription on this.
        private const string CLIENT_ID = "IOS5d9f6a21-0e4b-4c8a-9d3f-7b1c2e8a4f60";

        private static async Task<int> Main(string[] args)
        {
            // Without this the console encodes non-ASCII in the OEM code page, so the
            // degree sign in a temperature comes out as a stray character - both on
            // screen and, more importantly, when the output is redirected into a tool.
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (IOException)
            {
                // No console attached; whatever stdout is will have to do.
            }

            if (args.Any(a => a == "-h" || a == "--help" || a == "/?"))
            {
                Usage();
                return 0;
            }

            PackRegistry registry;
            try
            {
                registry = PackRegistry.LoadEmbedded();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Could not load the pack definitions: " + error.Message);
                return 2;
            }

            string block = ValueOf(args, "--block");
            if (block != null)
            {
                return DumpBlock(
                    registry,
                    block,
                    ValueOf(args, "--pack"),
                    ParseInt(ValueOf(args, "--config")),
                    ParseInt(ValueOf(args, "--log")),
                    args.Contains("--devices"));
            }

            string target = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
            List<string> commands = ValuesOf(args, "--cmd");
            bool trace = args.Contains("--trace");

            var shell = new Shell(registry, CLIENT_ID);
            return await shell.RunAsync(target, commands, trace).ConfigureAwait(false);
        }

        /// <summary>
        /// Decode a saved status block offline and dump every accessor as "key=value".
        ///
        /// This is the conformance harness: geckolib can dump the same snapshot through
        /// its own accessors, and the two outputs should be identical. It needs no spa
        /// and no network, so it can run over every snapshot in the geckolib repository.
        /// Output is kept bare so the harness can parse it.
        /// </summary>
        private static int DumpBlock(
            PackRegistry registry,
            string path,
            string platformKey,
            int configVersion,
            int logVersion,
            bool devices)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException error)
            {
                Console.Error.WriteLine("Could not read " + path + ": " + error.Message);
                return 2;
            }

            if (bytes.Length != GeckoStatusBlock.BLOCK_LENGTH)
            {
                Console.Error.WriteLine(
                    "Expected " + GeckoStatusBlock.BLOCK_LENGTH + " bytes, got " + bytes.Length);
                return 2;
            }

            ConfigStructDefinition config;
            LogStructDefinition log;
            if (!registry.TryGetConfig(platformKey, configVersion, out config) ||
                !registry.TryGetLog(platformKey, logVersion, out log))
            {
                Console.Error.WriteLine(
                    "No definitions for " + platformKey + " config " + configVersion + " log " + logVersion);
                return 3;
            }

            var structure = new GeckoStatusBlock();

            // An inMix accessory is declared in the block itself, so honour it here too.
            IList<AccessorDefinition> inMixConfig = null;
            IList<AccessorDefinition> inMixLog = null;
            if (bytes[GeckoKeys.InMix.PACK_TYPE_POSITION] == GeckoKeys.InMix.PACK_TYPE)
            {
                ConfigStructDefinition accessoryConfig;
                LogStructDefinition accessoryLog;
                if (registry.TryGetConfig(
                        GeckoKeys.InMix.PLATFORM_KEY, bytes[GeckoKeys.InMix.CONFIG_LIB_POSITION], out accessoryConfig))
                {
                    inMixConfig = accessoryConfig.Accessors;
                }

                if (registry.TryGetLog(
                        GeckoKeys.InMix.PLATFORM_KEY, bytes[GeckoKeys.InMix.STATUS_LIB_POSITION], out accessoryLog))
                {
                    inMixLog = accessoryLog.Accessors;
                }
            }

            structure.BuildAccessors(config.Accessors, log.Accessors, inMixConfig, inMixLog);
            structure.ReplaceSegment(0, bytes);

            if (devices)
            {
                GeckoOfflineSpa spa = GeckoOfflineSpa.Create(
                    registry, bytes, platformKey, configVersion, logVersion);
                DumpDevices(spa.CreateFacade());
                return 0;
            }

            var keys = new List<string>(structure.Accessors.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                object value = structure[key].Value;
                Console.WriteLine(key + "=" + (value is double
                    ? ((double)value).ToString("0.0", CultureInfo.InvariantCulture)
                    : Convert.ToString(value, CultureInfo.InvariantCulture)));
            }

            return 0;
        }

        /// <summary>
        /// Dump the device layer as "category.key=value" lines, so geckolib's facade over
        /// the same snapshot can be diffed against it.
        /// </summary>
        private static void DumpDevices(GeckoSpaFacade facade)
        {
            foreach (GeckoPump pump in new[] { facade.Pump1, facade.Pump2, facade.Pump3, facade.Pump4, facade.Pump5 })
            {
                Write("pump." + pump.Key + ".available", pump.IsAvailable);
                if (!pump.IsAvailable) continue;
                Write("pump." + pump.Key + ".type", pump.PumpType);
                Write("pump." + pump.Key + ".mode", pump.Mode);
                Write("pump." + pump.Key + ".on", pump.IsOn);
            }

            Write("blower.available", facade.Blower.IsAvailable);
            if (facade.Blower.IsAvailable)
            {
                Write("blower.mode", facade.Blower.Mode);
                Write("blower.on", facade.Blower.IsOn);
            }

            Write("waterfall.available", facade.Waterfall.IsAvailable);
            if (facade.Waterfall.IsAvailable) Write("waterfall.on", facade.Waterfall.IsOn);

            Write("bubblegen.available", facade.BubbleGenerator.IsAvailable);

            foreach (GeckoLight light in new GeckoLight[] { facade.Light, facade.Light2 })
            {
                Write("light." + light.Key + ".available", light.IsAvailable);
                if (!light.IsAvailable) continue;
                Write("light." + light.Key + ".state", light.State);
                Write("light." + light.Key + ".on", light.IsOn);
            }

            Write("heater.available", facade.WaterHeater.IsAvailable);
            if (facade.WaterHeater.IsAvailable)
            {
                Write("heater.unit", facade.WaterHeater.TemperatureUnit);
                Write("heater.current", facade.WaterHeater.CurrentTemperature);
                Write("heater.target", facade.WaterHeater.TargetTemperature);
                Write("heater.real_target", facade.WaterHeater.RealTargetTemperature);
                Write("heater.min", facade.WaterHeater.MinTemp);
                Write("heater.max", facade.WaterHeater.MaxTemp);
                Write("heater.operation", facade.WaterHeater.CurrentOperation);
            }

            foreach (GeckoSensor sensor in facade.Sensors) Write("sensor." + sensor.Name, sensor.State);
            foreach (GeckoBinarySensor sensor in facade.BinarySensors) Write("binary." + sensor.Name, sensor.IsOn);

            Write("error.state", facade.ErrorSensor.State);

            Write("eco.available", facade.EcoMode.IsAvailable);
            if (facade.EcoMode.IsAvailable) Write("eco.on", facade.EcoMode.IsOn);

            Write("standby.available", facade.Standby.IsAvailable);
            if (facade.Standby.IsAvailable) Write("standby.on", facade.Standby.IsOn);

            Write("lockmode.available", facade.LockMode.IsAvailable);
            if (facade.LockMode.IsAvailable) Write("lockmode.state", facade.LockMode.State);

            Write("heatpump.available", facade.HeatPump.IsAvailable);
            if (facade.HeatPump.IsAvailable) Write("heatpump.state", facade.HeatPump.State);

            Write("ingrid.available", facade.InGrid.IsAvailable);
            if (facade.InGrid.IsAvailable) Write("ingrid.state", facade.InGrid.State);

            Write("watercare.available", facade.WaterCare.IsAvailable);
            Write("reminders.available", facade.Reminders.IsAvailable);

            Write("counts.pumps", facade.Pumps.Count);
            Write("counts.blowers", facade.Blowers.Count);
            Write("counts.lights", facade.Lights.Count);
            Write("counts.keypad_buttons", facade.Keypad.Buttons.Count);
            Write("spa.in_use", facade.IsInUse);
        }

        private static void Write(string key, object value)
        {
            string text = value is double
                ? ((double)value).ToString("0.0", CultureInfo.InvariantCulture)
                : Convert.ToString(value, CultureInfo.InvariantCulture);

            Console.WriteLine(key + "=" + text);
        }

        private static int ParseInt(string text)
        {
            int value;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;

            throw new ArgumentException("Expected a number, got '" + text + "'");
        }

        private static string ValueOf(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }

            return null;
        }

        private static List<string> ValuesOf(string[] args, string name)
        {
            var values = new List<string>();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) values.Add(args[i + 1]);
            }

            return values;
        }

        private static void Usage()
        {
            Console.WriteLine("Usage: GeckoConsoleApp [spa-ip] [--cmd \"...\"]... [--trace]");
            Console.WriteLine();
            Console.WriteLine("  Starts an interactive shell. Type 'help' once it is running.");
            Console.WriteLine();
            Console.WriteLine("  spa-ip      discover and connect to this spa on startup, instead");
            Console.WriteLine("              of broadcasting for whatever is on the network.");
            Console.WriteLine("  --cmd       run a command before handing over; repeatable, so");
            Console.WriteLine("              --cmd \"state\" --cmd \"exit\" works as a script.");
            Console.WriteLine("  --trace     log every datagram sent and received.");
            Console.WriteLine();
            Console.WriteLine("Offline:");
            Console.WriteLine("  --block FILE --pack KEY --config N --log N");
            Console.WriteLine("              decode a saved 1024-byte status block and dump every");
            Console.WriteLine("              accessor as key=value. Used by the conformance harness.");
            Console.WriteLine("  --devices   with --block, dump the device layer instead of the accessors.");
        }
    }
}
