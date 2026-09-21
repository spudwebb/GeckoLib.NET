# GeckoLib.NET

A C# client library for Gecko Alliance spa packs over the in.touch2 UDP protocol. It
discovers spas on the local network, reads and writes the spa pack structure, and exposes
pumps, lights, blowers, the heater, water care and sensors as objects.

A port of the Python [geckolib](https://github.com/gazoodle/geckolib).

The library has no dependencies on any home-automation platform — it is a NuGet package
that anything can consume.

| Project | What it is |
|---|---|
| `GeckoLib.NET` | The library. `netstandard2.0` and `net462`. |
| `TestGeckoLib` | MSTest unit tests. |
| `GeckoConsoleApp` | An interactive shell for driving a spa by hand. |
| `tools/` | Python scripts that extract the pack definitions and check the port against geckolib. |

## Using it

```csharp
using (var locator = new GeckoLocator())
{
    IReadOnlyList<GeckoSpaDescriptor> spas = await locator.DiscoverAsync();

    using (var spa = new GeckoSpaConnection(spas[0], "IOS<your-uuid>", PackRegistry.LoadEmbedded()))
    {
        if (!await spa.ConnectAsync()) return;

        GeckoSpaFacade facade = spa.Facade;

        Console.WriteLine(facade.WaterHeater);          // Heater: Temperature 38.0°C, ...
        foreach (GeckoPump pump in facade.Pumps) Console.WriteLine(pump);

        await facade.Pump1.TurnOnAsync(presetMode: "HI");
        await facade.WaterHeater.SetTargetTemperatureAsync(38.5);

        spa.AccessorChanged += (s, e) => Console.WriteLine(e.Accessor.Key + " -> " + e.NewValue);
    }
}
```

The client id should be stable for a given installation: the spa keys its push
subscription on it.

Every device a pack could have is created, and each decides for itself whether this spa
actually has it — so `facade.Pump3` is always there to ask, while `facade.Pumps` contains
only what is fitted.

## Building

```
dotnet build
dotnet test
dotnet pack -c Release
```

## The shell

`GeckoConsoleApp` is an interactive shell modelled on geckolib's GeckoShell:

```
GeckoConsoleApp.exe                 # broadcast, then drop to a prompt
GeckoConsoleApp.exe 192.168.0.7     # connect to one spa, then drop to a prompt
```

```
(Gecko) discover
Simulator$ state
Simulator$ devices               # what this spa has, and what each is doing
Simulator$ accessors Ud          # wildcards: 'Ud' means 'Ud*'
Simulator$ get RhWaterTemp       # value, kind, offset, packing, allowed values
Simulator$ P1 HI                 # devices are commands, as in GeckoShell
Simulator$ set UdLi=HI           # or write the raw field
Simulator$ watercare Weekender
Simulator$ watch                 # live changes until you press Enter
Simulator$ snapshot              # a snapshot geckolib can load back
```

`help` lists everything. `--cmd "state" --cmd "exit"` scripts it, and `--trace` logs every
datagram sent and received.

**Test writes against the simulator, not a real spa.** `tools/run_against_simulator.py`
starts one, waits for it to answer, runs your commands and cleans up:

```
python tools/run_against_simulator.py state "P1 HI" state
```

Starting the simulator by hand also works — `cd <geckolib>/src && python -m geckolib
simulator` — but it is a command shell, so it exits if you detach its stdin, and
it binds port 10022 with SO_REUSEADDR, so a stale instance will silently steal traffic
from a new one.

## The pack data

Gecko's spa packs are described by a `SpaPackStruct_<rev>.xml` that geckolib compiles into
187 generated Python modules. **That XML is not in the geckolib repository** — it is
gitignored, the script that fetched it is gitignored too, and no URL for it is recorded
anywhere. The generated Python is the only surviving copy of the definitions.

The scripts in `tools/` therefore need a geckolib checkout to read. They find it next to
this one (`../geckolib` or `../github/geckolib`); set `GECKOLIB_ROOT` to point somewhere
else, or pass `--packs` / `--snapshots` explicitly.

`tools/extract_packdata.py` parses those modules with `ast` and emits
`GeckoLib.NET/Packs/packdata.json.gz` (21,586 accessors, ~55 KB gzipped), which is embedded
in the library and read on demand.

To regenerate, with geckolib importable:

```
python tools/extract_packdata.py
python tools/verify_packdata.py        # must report 0 mismatches
```

## Testing

Four layers, in increasing order of what they prove:

1. **`dotnet test`** — 85 MSTest cases. The protocol ones assert byte-for-byte against the
   fixtures in geckolib's own `tests/test_protocol_*.py`, which came from Wireshark
   captures of the real app.
2. **`python tools/verify_packdata.py`** — imports all 187 generated geckolib modules,
   constructs the real accessor objects, and compares all 21,586 of them field by field,
   including the derived length, struct format and bit mask.
3. **`python tools/snapshot_conformance.py`** — decodes each of geckolib's 54 captured spa
   snapshots with both implementations and diffs every value (16,069 of them). Catches
   endianness, mask and temperature-conversion mistakes across several pack families.
4. **`python tools/device_conformance.py`** — builds both facades over the same 54
   snapshots and diffs what they conclude (2,733 values): which pumps exist, whether they
   are single or two speed, what the heater is doing, which sensors are present. This is
   the one that catches inference bugs, because availability is worked out from how the
   pack says its outputs are wired rather than read from a field.

## Deliberate differences from geckolib

- The `<PACKT>` envelope is parsed by scanning for delimiters rather than with a greedy
  regex, so a payload containing the literal tag bytes is read correctly.
- A stalled status block transfer keeps what arrived and asks for the rest, instead of
  discarding it and re-requesting the whole block.
- Status block patches use an offset index, so a two-byte push does not walk every
  accessor.
- Reconnect, backoff and polling are not in the library. It reports state and the owner
  decides; `GeckoSpaFacade.RefreshAsync` replaces geckolib's background update task.
- The shell's `snapshot` includes the intouch version keys. geckolib's own
  `get_snapshot_data` omits them even though its parser requires them, so its snapshots do
  not round-trip through its own JSON reader; these do.

## Not ported

The inMix lighting accessory, MrSteam and BainUltra. They are separate product families
with their own device models (about 600 of geckolib's 2,900 automation lines), and no spa
here has one. Everything else in geckolib's automation layer is here.

## Licence

Copyright © 2026 spud.

geckolib is GPL-3.0, and this is a derivative work, so the same terms apply. See
[LICENSE](https://github.com/spudwebb/GeckoLib.NET/blob/main/LICENSE).
