using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET;
using GeckoLib.NET.Devices;
using GeckoLib.NET.Discovery;
using GeckoLib.NET.Models;
using GeckoLib.NET.Packs;
using GeckoLib.NET.Protocol.Messages;
using GeckoLib.NET.Struct;

namespace GeckoConsoleApp
{
    /// <summary>
    /// An interactive shell over GeckoLib.NET, modelled on geckolib's own GeckoShell.
    ///
    /// It exists to exercise the library by hand - discover, connect, inspect the pack
    /// structure, drive the spa - and doubles as the place to reproduce anything odd a
    /// real spa does.
    /// </summary>
    internal sealed class Shell
    {
        private const string BANNER = @"
    ----------------------------- USE AT YOUR OWN RISK -----------------------------

    This lets you change spa settings that the app and the top panel do not expose.
    Not every setting has been tested, and it is possible to stop a spa pack working
    the way it used to.

    Dump the configuration with 'config' and keep it somewhere safe before changing
    anything.
    --------------------------------------------------------------------------------
";

        private readonly PackRegistry _registry;
        private readonly string _clientId;
        private readonly List<Command> _commands = new List<Command>();

        private IReadOnlyList<GeckoSpaDescriptor> _spas = new List<GeckoSpaDescriptor>();
        private GeckoSpaConnection _spa;
        private bool _trace;

        public Shell(PackRegistry registry, string clientId)
        {
            _registry = registry;
            _clientId = clientId;
            BuildCommands();
        }

        private string Prompt
        {
            get { return _spa != null && _spa.IsConnected ? _spa.Name + "$ " : "(Gecko) "; }
        }

        /// <summary>Run the shell until the user exits or stdin ends.</summary>
        /// <param name="autoTarget">Spa address to discover and connect to on startup.</param>
        /// <param name="initialCommands">Commands to run before handing over to the user.</param>
        /// <param name="trace">Start with datagram tracing on.</param>
        public async Task<int> RunAsync(string autoTarget, IList<string> initialCommands, bool trace)
        {
            _trace = trace;

            Console.WriteLine(BANNER);
            Console.WriteLine("GeckoLib.NET shell. Type 'help' for commands, 'exit' to leave.");
            Console.WriteLine("Pack definitions: SpaPackStruct revision " + _registry.Revision);
            Console.WriteLine();

            if (autoTarget != null)
            {
                await DiscoverAsync(autoTarget).ConfigureAwait(false);
                if (_spas.Count > 0) await ManageAsync("1").ConfigureAwait(false);
            }

            foreach (string command in initialCommands)
            {
                Console.WriteLine(Prompt + command);
                if (!await ExecuteAsync(command).ConfigureAwait(false))
                {
                    await ShutdownAsync().ConfigureAwait(false);
                    return 0;
                }
            }

            while (true)
            {
                Console.Write(Prompt);
                string line = Console.ReadLine();
                if (line == null) break;

                line = line.Trim();
                if (line.Length == 0) continue;

                if (!await ExecuteAsync(line).ConfigureAwait(false)) break;
            }

            await ShutdownAsync().ConfigureAwait(false);
            return 0;
        }

        private async Task ShutdownAsync()
        {
            if (_spa == null) return;

            await _spa.DisconnectAsync().ConfigureAwait(false);
            _spa.Dispose();
            _spa = null;
        }

        /// <summary>Run one command line. Returns false to leave the shell.</summary>
        private async Task<bool> ExecuteAsync(string line)
        {
            string[] parts = line.Split(new[] { ' ' }, 2);
            string name = parts[0];
            string rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

            if (string.Equals(name, "exit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "quit", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Command command = _commands.FirstOrDefault(
                c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

            try
            {
                if (command != null)
                {
                    await command.Run(rest).ConfigureAwait(false);
                }
                else if (!await TryDeviceCommandAsync(name, rest).ConfigureAwait(false))
                {
                    Console.WriteLine("Unknown command '" + name + "'. Try 'help'.");
                }
            }
            catch (Exception error)
            {
                // ArgumentException tacks "Parameter name: x" onto its message, which is
                // useful in a stack trace and noise at a prompt.
                Console.WriteLine("*** " + FirstLine(error.Message));
            }

            return true;
        }

        // ---------------------------------------------------------------- commands

        private void BuildCommands()
        {
            Add("help", "help [command]", "List commands, or explain one.", HelpAsync);
            Add("about", "about", "About this program.", _ => { About(); return Done; });
            Add("license", "license", "Show the licence terms.", _ => { License(); return Done; });

            Add("discover", "discover [ip]", "Find spas by broadcast, or probe one address.", DiscoverAsync);
            Add("list", "list", "List the spas discovery found.", _ => { List(); return Done; });
            Add("manage", "manage <n>", "Connect to spa <n> from the list.", ManageAsync);
            Add("disconnect", "disconnect", "Disconnect from the spa.", DisconnectAsync);

            Add("state", "state", "Show what the spa is doing.", _ => { State(); return Done; });
            Add("version", "version", "Show firmware and pack versions.", _ => { Version(); return Done; });
            Add("pack", "pack", "Show pack structure details.", _ => { Pack(); return Done; });
            Add("config", "config", "Dump every config structure value.", _ => { Config(); return Done; });
            Add("devices", "devices", "List the devices this spa has.", _ => { Devices(); return Done; });

            Add("accessors", "accessors [pattern]", "List accessors, e.g. 'accessors Ud*'.", a => { Accessors(a); return Done; });
            Add("get", "get <key>", "Read one accessor.", a => { Get(a); return Done; });
            Add("set", "set <key>=<value>", "Write one accessor.", SetAsync);
            Add("peek", "peek <pos>", "Read one raw byte of the status block.", a => { Peek(a); return Done; });
            Add("hexdump", "hexdump", "Hex dump the whole status block.", _ => { HexDump(); return Done; });
            Add("snapshot", "snapshot [description]", "Print a snapshot geckolib can load.", a => { Snapshot(a); return Done; });

            Add("watercare", "watercare [mode]", "Show or set the water care mode.", WatercareAsync);
            Add(
                "reminders",
                "reminders [<type> <days>]",
                "Show the maintenance reminders, or reset one of them.",
                RemindersAsync);
            Add("setpoint", "setpoint <temp>", "Set the target temperature.", SetpointAsync);
            Add("key", "key <n>", "Press a keypad button.", KeyAsync);

            Add("watch", "watch", "Print changes as they arrive. Enter to stop.", WatchAsync);
            Add("refresh", "refresh", "Re-read the whole status block.", RefreshAsync);
            Add("resync", "resync", "Re-read the log range and check the push subscription.", ResyncAsync);
            Add("trace", "trace on|off", "Log every datagram sent and received.", a => { Trace(a); return Done; });
        }

        private static Task Done
        {
            get { return Task.FromResult(0); }
        }

        private void Add(string name, string usage, string help, Func<string, Task> run)
        {
            _commands.Add(new Command { Name = name, Usage = usage, Help = help, Run = run });
        }

        private Task HelpAsync(string argument)
        {
            if (argument.Length > 0)
            {
                Command command = _commands.FirstOrDefault(
                    c => string.Equals(c.Name, argument, StringComparison.OrdinalIgnoreCase));

                if (command == null)
                {
                    Console.WriteLine("No command called '" + argument + "'.");
                }
                else
                {
                    Console.WriteLine(command.Usage);
                    Console.WriteLine("  " + command.Help);
                }

                return Done;
            }

            Console.WriteLine("Commands");
            Console.WriteLine("========");
            foreach (Command command in _commands)
            {
                Console.WriteLine("  {0,-22} {1}", command.Usage, command.Help);
            }

            Console.WriteLine("  {0,-22} {1}", "exit", "Leave the shell.");

            if (_spa != null && _spa.IsConnected && _spa.Facade != null)
            {
                string devices = string.Join(" ", DeviceCommands().ToArray());
                if (devices.Length > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("This spa's devices (e.g. 'P1 HI', 'LI ON'):");
                    Console.WriteLine("  " + devices);
                }
            }

            return Done;
        }

        private void About()
        {
            Console.WriteLine();
            Console.WriteLine("GeckoLib.NET shell - drives Gecko Alliance spas over in.touch2.");
            Console.WriteLine("A C# port of geckolib (https://github.com/gazoodle/geckolib).");
            Console.WriteLine("Pack definitions from SpaPackStruct revision " + _registry.Revision + ".");
        }

        private static void License()
        {
            Console.WriteLine();
            Console.WriteLine("geckolib is GPL-3.0. This is a derivative work, so the same terms apply.");
            Console.WriteLine("https://www.gnu.org/licenses/gpl-3.0.html");
        }

        // ---------------------------------------------------------------- connection

        private async Task DiscoverAsync(string argument)
        {
            string target = argument.Length > 0 ? argument : null;
            Console.WriteLine(target == null ? "Discovering spas..." : "Probing " + target + "...");

            using (var locator = new GeckoLocator { Log = m => Console.WriteLine("  " + m) })
            {
                _spas = await locator.DiscoverAsync(target).ConfigureAwait(false);
            }

            Console.WriteLine("Found " + _spas.Count + (_spas.Count == 1 ? " spa." : " spas."));
            List();
        }

        private void List()
        {
            for (int i = 0; i < _spas.Count; i++)
            {
                Console.WriteLine("  " + (i + 1) + ". " + _spas[i]);
            }
        }

        private async Task ManageAsync(string argument)
        {
            int index;
            if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) ||
                index < 1 || index > _spas.Count)
            {
                Console.WriteLine("Pick a spa from 'list', e.g. 'manage 1'.");
                return;
            }

            await ShutdownAsync().ConfigureAwait(false);

            GeckoSpaDescriptor descriptor = _spas[index - 1];
            Console.WriteLine("Connecting to " + descriptor.Name + " at " + descriptor.Address + "...");

            _spa = new GeckoSpaConnection(descriptor, _clientId, _registry)
            {
                Log = m => Console.WriteLine("  " + m),
                LogDatagrams = _trace
            };

            if (!await _spa.ConnectAsync().ConfigureAwait(false))
            {
                Console.WriteLine("Connect failed: " + SpaStateText.Describe(_spa.State));
                _spa.Dispose();
                _spa = null;
                return;
            }

            Console.WriteLine("Connected to " + _spa.Name + ".");

            // Water care and reminders are not in the status block, so nothing knows them
            // until somebody asks. The library leaves that to its owner; the shell asks
            // once on connect so 'state' has something to show.
            try
            {
                await _spa.Facade.RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Console.WriteLine("  could not read water care or reminders: " + FirstLine(error.Message));
            }

            State();
        }

        private async Task DisconnectAsync(string argument)
        {
            if (!RequireSpa()) return;

            await ShutdownAsync().ConfigureAwait(false);
            Console.WriteLine("Disconnected.");
        }

        // ---------------------------------------------------------------- reporting

        private void State()
        {
            if (!RequireSpa()) return;

            GeckoSpaFacade facade = _spa.Facade;
            Console.WriteLine();
            Console.WriteLine("  " + facade.WaterHeater);

            foreach (GeckoPump pump in facade.Pumps) Console.WriteLine("  " + pump);
            foreach (GeckoBlower blower in facade.Blowers) Console.WriteLine("  " + blower);
            foreach (GeckoLight light in facade.Lights) Console.WriteLine("  " + light);
            foreach (GeckoSwitch item in facade.Switches) Console.WriteLine("  " + item);

            if (facade.Standby.IsAvailable) Console.WriteLine("  " + facade.Standby);
            if (facade.LockMode.IsAvailable) Console.WriteLine("  " + facade.LockMode);
            if (facade.HeatPump.IsAvailable) Console.WriteLine("  " + facade.HeatPump);
            if (facade.InGrid.IsAvailable) Console.WriteLine("  " + facade.InGrid);

            Console.WriteLine("  " + facade.WaterCare);
            Console.WriteLine("  " + facade.Reminders);

            foreach (GeckoSensor sensor in facade.Sensors) Console.WriteLine("  " + sensor);
            foreach (GeckoBinarySensor sensor in facade.BinarySensors) Console.WriteLine("  " + sensor);

            Console.WriteLine("  " + facade.ErrorSensor);
        }

        private void Devices()
        {
            if (!RequireSpa()) return;

            GeckoSpaFacade facade = _spa.Facade;
            Console.WriteLine("  {0,-22} {1,-12} {2}", "DEVICE", "KEY", "STATE");
            foreach (GeckoDevice device in facade.AllDevices)
            {
                Console.WriteLine("  {0,-22} {1,-12} {2}", device.Name, device.Key, device);
            }

            Console.WriteLine();
            Console.WriteLine("  {0} device(s); spa in use: {1}", facade.AllDevices.Count, facade.IsInUse);
        }

        private void Version()
        {
            if (!RequireSpa()) return;

            Console.WriteLine("  {0,-22} {1}", "pack definitions", _registry.Revision);
            Console.WriteLine("  {0,-22} {1}", "intouch version EN", _spa.Versions.EnVersion);
            Console.WriteLine("  {0,-22} {1}", "intouch version CO", _spa.Versions.CoVersion);
            Console.WriteLine("  {0,-22} {1}", "spa pack", _spa.PackInfo.PlatformKey + " " + _spa.PackVersion);
            Console.WriteLine("  {0,-22} {1}", "config version", _spa.PackInfo.ConfigVersion);
            Console.WriteLine("  {0,-22} {1}", "log version", _spa.PackInfo.LogVersion);
            Console.WriteLine("  {0,-22} {1}", "pack type", _spa.Pack.PlatformType);
            Console.WriteLine("  {0,-22} {1}", "low level config #", _spa.ConfigNumber);
            Console.WriteLine("  {0,-22} {1}", "radio", _spa.Channel);
        }

        private void Pack()
        {
            if (!RequireSpa()) return;

            Console.WriteLine("  {0,-22} {1}", "platform", _spa.Pack.Name);
            Console.WriteLine("  {0,-22} {1}", "segment", _spa.Pack.PlatformSegment);
            Console.WriteLine("  {0,-22} {1}", "log range", _spa.LogStruct.Begin + ".." + _spa.LogStruct.End);
            Console.WriteLine("  {0,-22} {1}", "accessors", _spa.Struct.Accessors.Count);
            Console.WriteLine("  {0,-22} {1}", "outputs", string.Join(" ", _spa.ConfigStruct.OutputKeys.ToArray()));
            Console.WriteLine("  {0,-22} {1}", "devices", string.Join(" ", _spa.LogStruct.AllDeviceKeys.ToArray()));
            Console.WriteLine("  {0,-22} {1}", "user demands", string.Join(" ", _spa.LogStruct.UserDemandKeys.ToArray()));
            Console.WriteLine("  {0,-22} {1}", "error keys", string.Join(" ", _spa.LogStruct.ErrorKeys.ToArray()));
        }

        private void Config()
        {
            if (!RequireSpa()) return;

            foreach (AccessorDefinition definition in _spa.ConfigStruct.Accessors)
            {
                GeckoStructAccessor accessor = _spa.Struct[definition.Key];
                if (accessor == null) continue;
                Console.WriteLine("  {0,-28} {1}", definition.Key, Format(accessor.Value));
            }
        }

        private void Accessors(string pattern)
        {
            if (!RequireSpa()) return;

            Regex matcher = Wildcard(pattern);
            int shown = 0;

            foreach (string key in _spa.Struct.Accessors.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!matcher.IsMatch(key)) continue;

                GeckoStructAccessor accessor = _spa.Struct[key];
                Console.WriteLine("  {0,-28} {1,-12} {2,4} {3}",
                    key, accessor.Kind, accessor.Position, Format(accessor.Value));
                shown++;
            }

            Console.WriteLine("  " + shown + " of " + _spa.Struct.Accessors.Count + " accessors.");
        }

        /// <summary>
        /// geckolib's accessor filter: an empty or bare pattern gets a trailing '*', so
        /// 'accessors Ud' lists everything beginning with Ud.
        /// </summary>
        private static Regex Wildcard(string pattern)
        {
            if (pattern == null) pattern = string.Empty;
            if (!pattern.EndsWith("?", StringComparison.Ordinal)) pattern += "*";

            string expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(expression, RegexOptions.CultureInvariant);
        }

        private void Get(string key)
        {
            if (!RequireSpa()) return;

            GeckoStructAccessor accessor = Find(key);
            if (accessor == null) return;

            Console.WriteLine("  {0} = {1}", accessor.Key, Format(accessor.Value));
            Console.WriteLine("  {0,-12} {1}", "kind", accessor.Kind);
            Console.WriteLine("  {0,-12} {1} ({2} byte{3})",
                "position", accessor.Position, accessor.Length, accessor.Length == 1 ? string.Empty : "s");
            Console.WriteLine("  {0,-12} {1}", "raw", accessor.RawValue);
            Console.WriteLine("  {0,-12} {1}", "writable", accessor.IsWritable);

            if (accessor.Definition.BitPosition.HasValue)
            {
                Console.WriteLine("  {0,-12} bit {1} mask 0x{2:x}",
                    "packing", accessor.Definition.BitPosition, accessor.Definition.BitMask);
            }

            if (accessor.Definition.Items != null)
            {
                Console.WriteLine("  {0,-12} {1}", "values", string.Join(", ", accessor.Definition.Items));
            }

            Console.WriteLine("  {0,-12} {1}", "path", accessor.Definition.Path);
        }

        private async Task SetAsync(string assignment)
        {
            if (!RequireSpa()) return;

            string[] parts = assignment.Split(new[] { '=' }, 2);
            if (parts.Length != 2)
            {
                Console.WriteLine("Usage: set <key>=<value>");
                return;
            }

            GeckoStructAccessor accessor = Find(parts[0].Trim());
            if (accessor == null) return;

            await WriteAsync(accessor, parts[1].Trim()).ConfigureAwait(false);
        }

        private async Task WriteAsync(GeckoStructAccessor accessor, string value)
        {
            if (!accessor.IsWritable)
            {
                Console.WriteLine("  " + accessor.Key + " is read-only in this pack's definitions.");
                return;
            }

            object before = accessor.Value;

            // Let a rejected value throw before anything is announced, so a failed write
            // does not read as though it went through.
            bool ok = await accessor.SetValueAsync(value).ConfigureAwait(false);

            Console.WriteLine("  {0}: {1} -> {2}", accessor.Key, Format(before), Format(accessor.Value));
            if (!ok) Console.WriteLine("  the spa did not acknowledge the write");
        }

        private void Peek(string argument)
        {
            if (!RequireSpa()) return;

            int position;
            if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out position) ||
                position < 0 || position >= GeckoStatusBlock.BLOCK_LENGTH)
            {
                Console.WriteLine("Usage: peek <0.." + (GeckoStatusBlock.BLOCK_LENGTH - 1) + ">");
                return;
            }

            byte value = _spa.Struct.Snapshot()[position];
            Console.WriteLine("  byte {0} = {1} (0x{1:x2})", position, value);
        }

        private void HexDump()
        {
            if (!RequireSpa()) return;

            byte[] block = _spa.Struct.Snapshot();
            for (int offset = 0; offset < block.Length; offset += 16)
            {
                var hex = new StringBuilder();
                var text = new StringBuilder();

                for (int i = 0; i < 16; i++)
                {
                    byte value = block[offset + i];
                    hex.Append(value.ToString("x2")).Append(i == 7 ? "  " : " ");
                    text.Append(value >= 32 && value < 127 ? (char)value : '.');
                }

                Console.WriteLine("{0:x8}  {1} {2}", offset, hex, text);
            }
        }

        /// <summary>
        /// Print the status block in geckolib's snapshot format, so it can be pasted into
        /// a .snapshot file and replayed by the simulator or the conformance harness.
        ///
        /// Note this emits the intouch version keys, which geckolib's own
        /// get_snapshot_data does not, even though its parser requires them
        /// (utils/snapshot.py:306). Its own snapshots therefore do not round-trip;
        /// these do.
        /// </summary>
        private void Snapshot(string description)
        {
            if (!RequireSpa()) return;

            byte[] block = _spa.Struct.Snapshot();
            var bytes = new StringBuilder();
            for (int i = 0; i < block.Length; i++)
            {
                if (i > 0) bytes.Append(", ");
                bytes.Append("'0x").Append(block[i].ToString("x")).Append("'");
            }

            var line = new StringBuilder();
            line.Append("{'Name': '").Append(_spa.Name).Append("'");
            line.Append(", 'SpaPackStruct.xml revision': '").Append(_spa.Pack.Revision).Append("'");
            line.Append(", 'Spa pack': '").Append(_spa.PackInfo.PlatformKey)
                .Append(" ").Append(_spa.PackVersion).Append("'");
            line.Append(", 'intouch version EN': '").Append(_spa.Versions.EnVersion).Append("'");
            line.Append(", 'intouch version CO': '").Append(_spa.Versions.CoVersion).Append("'");
            line.Append(", 'Low level configuration #': ").Append(_spa.ConfigNumber);
            line.Append(", 'Config version': ").Append(_spa.PackInfo.ConfigVersion);
            line.Append(", 'Log version': ").Append(_spa.PackInfo.LogVersion);
            line.Append(", 'Pack type': ").Append(_spa.Pack.PlatformType);
            if (description.Length > 0) line.Append(", 'Description': '").Append(description).Append("'");
            line.Append(", 'Snapshot UTC Time': '")
                .Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff+00:00", CultureInfo.InvariantCulture))
                .Append("'");
            line.Append(", 'Status Block': [").Append(bytes).Append("]}");

            Console.WriteLine(line.ToString());
        }

        // ---------------------------------------------------------------- spa commands

        private async Task WatercareAsync(string argument)
        {
            if (!RequireSpa()) return;

            if (argument.Length == 0)
            {
                EWatercareMode? mode = await _spa.GetWatercareModeAsync().ConfigureAwait(false);
                Console.WriteLine(mode.HasValue
                    ? "  " + WatercareMessage.ToDisplayName(mode.Value)
                    : "  the spa did not answer");

                Console.WriteLine("  modes: " + string.Join(", ", WatercareMessage.MODE_NAMES));
                return;
            }

            int index = Array.FindIndex(
                WatercareMessage.MODE_NAMES,
                n => string.Equals(n, argument, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                Console.WriteLine("  Pick one of: " + string.Join(", ", WatercareMessage.MODE_NAMES));
                return;
            }

            bool ok = await _spa.SetWatercareModeAsync((EWatercareMode)index).ConfigureAwait(false);
            Console.WriteLine(ok ? "  set to " + WatercareMessage.MODE_NAMES[index] : "  the spa refused");
        }

        private async Task RemindersAsync(string argument)
        {
            if (!RequireSpa()) return;

            if (!string.IsNullOrEmpty(argument) && !await ResetReminderAsync(argument).ConfigureAwait(false))
            {
                return;
            }

            IList<Reminder> reminders = await _spa.GetRemindersAsync().ConfigureAwait(false);
            if (reminders == null)
            {
                Console.WriteLine("  the spa did not answer");
                return;
            }

            foreach (Reminder reminder in reminders)
            {
                if (reminder.Type == EReminderType.Invalid) continue;
                Console.WriteLine("  {0,-22} due in {1} days", reminder.Type, reminder.Days);
            }
        }

        /// <summary>Handle "reminders RinseFilter 7". Returns false if it could not be done.</summary>
        private async Task<bool> ResetReminderAsync(string argument)
        {
            string[] parts = argument.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            EReminderType type;
            short days;
            if (parts.Length != 2 ||
                !Enum.TryParse(parts[0], true, out type) ||
                !short.TryParse(parts[1], out days))
            {
                Console.WriteLine("  usage: reminders <type> <days>, e.g. reminders RinseFilter 7");
                Console.WriteLine("  types: " + string.Join(", ", Enum.GetNames(typeof(EReminderType))));
                return false;
            }

            GeckoReminders manager = _spa.Facade.Reminders;

            // The write sends every slot, so make sure we know what is in them first.
            await manager.RefreshAsync().ConfigureAwait(false);

            try
            {
                bool ok = await manager.SetAsync(type, days).ConfigureAwait(false);
                Console.WriteLine(ok ? "  reset " + type + " to " + days + " days" : "  the spa refused");
                return ok;
            }
            catch (InvalidOperationException error)
            {
                Console.WriteLine("  " + error.Message);
                return false;
            }
        }

        private async Task SetpointAsync(string argument)
        {
            if (!RequireSpa()) return;

            GeckoStructAccessor setpoint = _spa.Struct["SetpointG"];
            if (setpoint == null)
            {
                Console.WriteLine("  this spa has no SetpointG field");
                return;
            }

            if (argument.Length == 0)
            {
                Console.WriteLine("  " + Format(setpoint.Value));
                return;
            }

            await WriteAsync(setpoint, argument).ConfigureAwait(false);
        }

        private async Task KeyAsync(string argument)
        {
            if (!RequireSpa()) return;

            int key;
            if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out key))
            {
                Console.WriteLine("Usage: key <n>, e.g. key " + GeckoKeys.Keypad.PUMP_1 + " for pump 1.");
                return;
            }

            bool ok = await _spa.PressKeyAsync(key).ConfigureAwait(false);
            Console.WriteLine(ok ? "  pressed" : "  the spa did not acknowledge it");
        }

        /// <summary>
        /// geckolib's shell exposes each device as its own command - "P1 HI", "LI ON".
        /// This does the same through the device layer, so the command means what the
        /// device means rather than writing a raw field.
        /// </summary>
        private async Task<bool> TryDeviceCommandAsync(string name, string argument)
        {
            if (_spa == null || !_spa.IsConnected || _spa.Facade == null) return false;

            GeckoDevice device = null;
            foreach (GeckoDevice candidate in _spa.Facade.UserDevices)
            {
                if (string.Equals(candidate.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    device = candidate;
                    break;
                }
            }

            if (device == null) return false;

            if (argument.Length == 0)
            {
                Console.WriteLine("  " + device);

                var pumpInfo = device as GeckoPump;
                if (pumpInfo != null && pumpInfo.Modes.Count > 0)
                {
                    Console.WriteLine("  values: " + string.Join(", ", pumpInfo.Modes));
                }

                return true;
            }

            var pump = device as GeckoPump;
            if (pump != null)
            {
                bool ok = pump.IsVariableSpeed
                    ? await SetPumpPercentageAsync(pump, argument).ConfigureAwait(false)
                    : await pump.SetModeAsync(argument).ConfigureAwait(false);

                Console.WriteLine(ok ? "  " + device : "  the spa did not acknowledge it");
                return true;
            }

            var light = device as GeckoLight;
            if (light != null)
            {
                bool on = !string.Equals(argument, "OFF", StringComparison.OrdinalIgnoreCase);
                bool ok = on
                    ? await light.TurnOnAsync().ConfigureAwait(false)
                    : await light.TurnOffAsync().ConfigureAwait(false);

                Console.WriteLine(ok ? "  " + device : "  the spa did not acknowledge it");
                return true;
            }

            Console.WriteLine("  Don't know how to drive " + device.Name);
            return true;
        }

        private static async Task<bool> SetPumpPercentageAsync(GeckoPump pump, string argument)
        {
            int percentage;
            if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out percentage))
            {
                Console.WriteLine("  " + pump.Key + " is variable speed; give it a percentage.");
                return false;
            }

            return percentage <= 0
                ? await pump.TurnOffAsync().ConfigureAwait(false)
                : await pump.TurnOnAsync(percentage).ConfigureAwait(false);
        }

        private IEnumerable<string> DeviceCommands()
        {
            foreach (GeckoDevice device in _spa.Facade.UserDevices) yield return device.Key;
        }

        // ---------------------------------------------------------------- live

        private async Task WatchAsync(string argument)
        {
            if (!RequireSpa()) return;

            Console.WriteLine("Watching for changes. Press Enter to stop.");

            EventHandler<AccessorChangedEventArgs> onChanged = (sender, e) => Console.WriteLine(
                "  {0:HH:mm:ss} {1} {2} -> {3}",
                DateTime.Now, e.Accessor.Key, Format(e.OldValue), Format(e.NewValue));

            EventHandler<SpaStateChangedEventArgs> onState = (sender, e) =>
                Console.WriteLine("  {0:HH:mm:ss} state {1}", DateTime.Now, e);

            _spa.AccessorChanged += onChanged;
            _spa.StateChanged += onState;

            try
            {
                await Task.Run(() => Console.ReadLine()).ConfigureAwait(false);
            }
            finally
            {
                _spa.AccessorChanged -= onChanged;
                _spa.StateChanged -= onState;
            }

            Console.WriteLine("Stopped watching.");
        }

        private async Task RefreshAsync(string argument)
        {
            if (!RequireSpa()) return;

            bool ok = await _spa.ReadStatusBlockAsync(0, GeckoStatusBlock.BLOCK_LENGTH).ConfigureAwait(false);
            Console.WriteLine(ok ? "  status block re-read" : "  could not read the status block");
        }

        private async Task ResyncAsync(string argument)
        {
            if (!RequireSpa()) return;

            bool ok = await _spa.ResyncAsync().ConfigureAwait(false);
            Console.WriteLine(ok ? "  resynced" : "  resync failed");
        }

        private void Trace(string argument)
        {
            _trace = !string.Equals(argument, "off", StringComparison.OrdinalIgnoreCase);
            if (_spa != null) _spa.LogDatagrams = _trace;
            Console.WriteLine("  datagram tracing " + (_trace ? "on" : "off"));
        }

        // ---------------------------------------------------------------- helpers

        private bool RequireSpa()
        {
            if (_spa != null && _spa.IsConnected) return true;

            Console.WriteLine("Not connected. Use 'discover' then 'manage 1'.");
            return false;
        }

        private GeckoStructAccessor Find(string key)
        {
            GeckoStructAccessor accessor = _spa.Struct[key];
            if (accessor != null) return accessor;

            // Be forgiving about case, then offer near misses rather than just failing.
            string match = _spa.Struct.Accessors.Keys.FirstOrDefault(
                k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

            if (match != null) return _spa.Struct[match];

            Console.WriteLine("This spa has no field called '" + key + "'.");

            string[] near = _spa.Struct.Accessors.Keys
                .Where(k => k.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(k => k, StringComparer.Ordinal)
                .Take(10)
                .ToArray();

            if (near.Length > 0) Console.WriteLine("  did you mean: " + string.Join(", ", near));
            return null;
        }

        private void ShowIfPresent(GeckoStatusBlock block, string key, string label)
        {
            GeckoStructAccessor accessor = block[key];
            if (accessor == null) return;

            Console.WriteLine("  {0,-22} {1}", label, Format(accessor.Value));
        }

        /// <summary>
        /// The first line of an exception message. ArgumentException appends
        /// "Parameter name: x", which is useful in a stack trace and noise at a prompt.
        /// </summary>
        private static string FirstLine(string message)
        {
            if (message == null) return string.Empty;

            int end = message.IndexOfAny(new[] { '\r', '\n' });
            return (end < 0 ? message : message.Substring(0, end)).Trim();
        }

        private static string Format(object value)
        {
            if (value is double) return ((double)value).ToString("0.0", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private sealed class Command
        {
            public string Name;
            public string Usage;
            public string Help;
            public Func<string, Task> Run;
        }
    }
}
