using System;
using System.Collections.Generic;
using GeckoLib.NET;
using GeckoLib.NET.Devices;
using GeckoLib.NET.Packs;
using GeckoLib.NET.Protocol.Messages;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestGeckoLib.Infrastructure;

namespace TestGeckoLib
{
    /// <summary>
    /// The device layer: how raw pack fields become pumps, lights and a heater.
    ///
    /// tools/device_conformance.py checks this against geckolib over 54 real snapshots.
    /// These pin down the individual behaviours, so a failure says what broke.
    /// </summary>
    [TestClass]
    public class DeviceTests
    {
        private static readonly string[] OFF_LO_HI = { "OFF", "LO", "HI" };
        private static readonly string[] OFF_ON = { "OFF", "ON" };

        [TestMethod]
        public void APumpWiredWithOnlyAHighOutputIsSingleSpeed()
        {
            var spa = new FakeSpa()
                .WithOutput("Out1", "NA", "P1H")
                .With("UdP1", EAccessorKind.Enum, items: OFF_LO_HI)
                .Build()
                .Set("Out1", "P1H");

            GeckoSpaFacade facade = spa.CreateFacade();

            Assert.IsTrue(facade.Pump1.IsAvailable);
            Assert.AreEqual(EPumpType.SingleSpeed, facade.Pump1.PumpType);
        }

        [TestMethod]
        public void APumpWiredWithBothSpeedsIsTwoSpeed()
        {
            var spa = new FakeSpa()
                .WithOutput("Out1", "NA", "P1H")
                .WithOutput("Out2", "NA", "P1L")
                .With("UdP1", EAccessorKind.Enum, items: OFF_LO_HI)
                .Build()
                .Set("Out1", "P1H")
                .Set("Out2", "P1L");

            GeckoSpaFacade facade = spa.CreateFacade();

            Assert.AreEqual(EPumpType.TwoSpeed, facade.Pump1.PumpType);
            CollectionAssert.AreEqual(OFF_LO_HI, new List<string>(facade.Pump1.Modes));
        }

        [TestMethod]
        public void AnOutputWiredToNothingLeavesThePumpUnavailable()
        {
            var spa = new FakeSpa()
                .WithOutput("Out1", "NA", "P1H")
                .With("UdP1", EAccessorKind.Enum, items: OFF_LO_HI)
                .Build()
                .Set("Out1", "NA");

            GeckoSpaFacade facade = spa.CreateFacade();

            Assert.IsFalse(facade.Pump1.IsAvailable);
            Assert.AreEqual(EPumpType.None, facade.Pump1.PumpType);
            Assert.AreEqual(0, facade.Pumps.Count);
        }

        /// <summary>
        /// A pump reconfigured as variable speed is driven by a percentage through a
        /// different field, so the plain demand field no longer applies.
        /// </summary>
        [TestMethod]
        public void APumpFlaggedAsVariableSpeedUsesItsPercentageField()
        {
            var spa = new FakeSpa()
                .WithOutput("Out1", "NA", "P1H")
                .With("UdP1", EAccessorKind.Enum, items: OFF_LO_HI)
                .With("UdVSP1", EAccessorKind.Byte)
                .With("Pump1AsVSP", EAccessorKind.Bool, bitPosition: 0)
                .Build()
                .Set("Out1", "P1H")
                .Set("Pump1AsVSP", true)
                .Set("UdVSP1", 60);

            GeckoSpaFacade facade = spa.CreateFacade();

            Assert.AreEqual(EPumpType.VariableSpeed, facade.Pump1.PumpType);
            Assert.IsTrue(facade.Pump1.IsVariableSpeed);
            Assert.AreEqual(60, facade.Pump1.Percentage);
            Assert.IsTrue(facade.Pump1.IsOn);
        }

        [TestMethod]
        public void AVariableSpeedPumpAtZeroReportsOff()
        {
            var spa = new FakeSpa()
                .WithOutput("Out1", "NA", "P1H")
                .With("UdVSP1", EAccessorKind.Byte)
                .With("Pump1AsVSP", EAccessorKind.Bool, bitPosition: 0)
                .Build()
                .Set("Out1", "P1H")
                .Set("Pump1AsVSP", true)
                .Set("UdVSP1", 0);

            GeckoSpaFacade facade = spa.CreateFacade();

            Assert.IsFalse(facade.Pump1.IsOn);
            Assert.AreEqual("OFF", facade.Pump1.State);
        }

        [TestMethod]
        public void TurningAPumpOnPicksHighWhenItHasOne()
        {
            GeckoSpaFacade facade = TwoSpeedPump();

            facade.Pump1.TurnOnAsync().GetAwaiter().GetResult();

            Assert.AreEqual("HI", facade.Pump1.Mode);
            Assert.IsTrue(facade.Pump1.IsOn);
        }

        /// <summary>
        /// A pump that has no "HI" gets "ON" instead, which is what geckolib falls back to.
        /// </summary>
        [TestMethod]
        public void TurningOnAPumpWithoutAHighSettingUsesOnInstead()
        {
            var spa = new FakeSpa()
                .WithOutput("Out1", "NA", "P1H")
                .With("UdP1", EAccessorKind.Enum, items: OFF_ON)
                .Build()
                .Set("Out1", "P1H");

            GeckoSpaFacade facade = spa.CreateFacade();
            facade.Pump1.TurnOnAsync().GetAwaiter().GetResult();

            Assert.AreEqual("ON", facade.Pump1.Mode);
        }

        [TestMethod]
        public void APumpCanBeSetToAParticularSpeed()
        {
            GeckoSpaFacade facade = TwoSpeedPump();

            facade.Pump1.SetModeAsync("LO").GetAwaiter().GetResult();
            Assert.AreEqual("LO", facade.Pump1.Mode);

            facade.Pump1.TurnOffAsync().GetAwaiter().GetResult();
            Assert.AreEqual("OFF", facade.Pump1.Mode);
            Assert.IsFalse(facade.Pump1.IsOn);
        }

        /// <summary>The two lights are turned on with different values.</summary>
        [TestMethod]
        public void TheMainLightTurnsOnWithHighAndTheSecondWithOn()
        {
            var spa = new FakeSpa()
                .WithOutput("Out1", "NA", "LI")
                .WithOutput("Out2", "NA", "L120")
                .With("UdLi", EAccessorKind.Enum, items: OFF_LO_HI)
                .With("UdL120", EAccessorKind.Enum, items: OFF_ON)
                .Build()
                .Set("Out1", "LI")
                .Set("Out2", "L120");

            GeckoSpaFacade facade = spa.CreateFacade();

            Assert.IsTrue(facade.Light.IsAvailable);
            Assert.IsTrue(facade.Light2.IsAvailable);

            facade.Light.TurnOnAsync().GetAwaiter().GetResult();
            facade.Light2.TurnOnAsync().GetAwaiter().GetResult();

            Assert.AreEqual("HI", facade.Light.State);
            Assert.AreEqual("ON", facade.Light2.State);
            Assert.AreEqual(2, facade.Lights.Count);
        }

        [TestMethod]
        public void TheHeaterReportsTemperaturesInTheSpasOwnUnits()
        {
            GeckoSpaFacade facade = Heater("C", current: 684, target: 702);

            Assert.IsTrue(facade.WaterHeater.IsAvailable);
            Assert.AreEqual("°C", facade.WaterHeater.TemperatureUnit);
            Assert.AreEqual(38.0, facade.WaterHeater.CurrentTemperature, 0.001);
            Assert.AreEqual(39.0, facade.WaterHeater.TargetTemperature, 0.001);
        }

        [TestMethod]
        public void WithNoLimitsDeclaredTheHeaterFallsBackToSensibleOnes()
        {
            GeckoSpaFacade celsius = Heater("C", current: 684, target: 684);
            Assert.AreEqual(GeckoWaterHeater.MIN_TEMP_C, celsius.WaterHeater.MinTemp, 0.001);
            Assert.AreEqual(GeckoWaterHeater.MAX_TEMP_C, celsius.WaterHeater.MaxTemp, 0.001);

            GeckoSpaFacade fahrenheit = Heater("F", current: 684, target: 684);
            Assert.AreEqual(GeckoWaterHeater.MIN_TEMP_F, fahrenheit.WaterHeater.MinTemp, 0.001);
            Assert.AreEqual(GeckoWaterHeater.MAX_TEMP_F, fahrenheit.WaterHeater.MaxTemp, 0.001);
        }

        /// <summary>
        /// Not every pack reports heating as a boolean: some use an enum whose value is
        /// the word "Heating". Reading only booleans made the heater look idle while it
        /// was running, which the snapshot conformance check caught.
        /// </summary>
        [TestMethod]
        public void AnEnumHeatingFlagIsUnderstoodJustLikeABooleanOne()
        {
            var spa = new FakeSpa()
                .With("TempUnits", EAccessorKind.Enum, items: new[] { "F", "C" })
                .With("DisplayedTempG", EAccessorKind.Temp)
                .With("SetpointG", EAccessorKind.Temp)
                .With("Heating", EAccessorKind.Enum, items: new[] { "OFF", "Heating" })
                .Build()
                .Set("TempUnits", "C")
                .Set("DisplayedTempG", 38.0)
                .Set("SetpointG", 38.0)
                .Set("Heating", "Heating");

            GeckoSpaFacade facade = spa.CreateFacade();

            // The temperatures are equal, so only the flag can be saying it is heating.
            Assert.AreEqual(GeckoHeaterOperation.HEATING, facade.WaterHeater.CurrentOperation);
            Assert.AreEqual(true, facade.WaterHeater.IsHeating);
        }

        /// <summary>With no flags at all, the temperatures decide.</summary>
        [TestMethod]
        public void WithNoHeatingFlagTheTemperaturesDecideWhatTheHeaterIsDoing()
        {
            Assert.AreEqual(
                GeckoHeaterOperation.HEATING,
                Heater("C", current: 600, target: 684).WaterHeater.CurrentOperation);

            Assert.AreEqual(
                GeckoHeaterOperation.COOLING,
                Heater("C", current: 700, target: 684).WaterHeater.CurrentOperation);

            Assert.AreEqual(
                GeckoHeaterOperation.IDLE,
                Heater("C", current: 684, target: 684).WaterHeater.CurrentOperation);
        }

        /// <summary>Standby reads inverted: the field says "OFF" when standby is on.</summary>
        [TestMethod]
        public void StandbyIsOnWhenItsFieldSaysOff()
        {
            var spa = new FakeSpa()
                .With("QuietState", EAccessorKind.Enum, items: new[] { "NOT_SET", "DRAIN", "SOAK", "OFF" })
                .Build()
                .Set("QuietState", "OFF");

            GeckoSpaFacade facade = spa.CreateFacade();

            Assert.IsTrue(facade.Standby.IsAvailable);
            Assert.IsTrue(facade.Standby.IsOn);

            facade.Standby.TurnOffAsync().GetAwaiter().GetResult();
            Assert.IsFalse(facade.Standby.IsOn);
            Assert.AreEqual("NOT_SET", facade.Standby.RawState);
        }

        [TestMethod]
        public void TheErrorSensorNamesEveryFaultThatIsSet()
        {
            var spa = new FakeSpa()
                .WithError("Fuse1Err")
                .WithError("Fuse2Err")
                .WithError("ScanErr")
                .Build();

            GeckoSpaFacade facade = spa.CreateFacade();
            Assert.AreEqual("None", facade.ErrorSensor.State);
            Assert.IsFalse(facade.ErrorSensor.HasErrors);

            spa.Set("Fuse2Err", true);
            spa.Set("ScanErr", true);

            Assert.AreEqual("Fuse2Err, ScanErr", facade.ErrorSensor.State);
            Assert.IsTrue(facade.ErrorSensor.HasErrors);
        }

        [TestMethod]
        public void LockModeReportsFriendlyNamesAndAcceptsThemBack()
        {
            var spa = new FakeSpa()
                .With("LockMode", EAccessorKind.Enum, items: new[] { "UNLOCK", "PARTIAL", "FULL" })
                .Build()
                .Set("LockMode", "PARTIAL");

            GeckoSpaFacade facade = spa.CreateFacade();

            Assert.AreEqual("Partial Lock", facade.LockMode.State);
            CollectionAssert.AreEqual(
                new[] { "Unlocked", "Partial Lock", "Full Lock" },
                new List<string>(facade.LockMode.States));

            facade.LockMode.SetStateAsync("Full Lock").GetAwaiter().GetResult();
            Assert.AreEqual("Full Lock", facade.LockMode.State);
            Assert.AreEqual("FULL", facade.Spa.Struct["LockMode"].Value);
        }

        /// <summary>
        /// The heat pump and inGrid share a field, so each only exists if the pack says
        /// that particular hardware was detected.
        /// </summary>
        [TestMethod]
        public void TheHeatPumpOnlyExistsWhenThePackSaysOneIsFitted()
        {
            var without = new FakeSpa()
                .With("CoolZoneMode", EAccessorKind.Enum, items: new[] { "CHILL", "INTERNAL_HEAT" })
                .With("ModbusHeatPumpDetected", EAccessorKind.Bool, bitPosition: 0)
                .Build();

            Assert.IsFalse(without.CreateFacade().HeatPump.IsAvailable);

            var with = new FakeSpa()
                .With("CoolZoneMode", EAccessorKind.Enum, items: new[] { "CHILL", "INTERNAL_HEAT" })
                .With("ModbusHeatPumpDetected", EAccessorKind.Bool, bitPosition: 0)
                .Build()
                .Set("ModbusHeatPumpDetected", true)
                .Set("CoolZoneMode", "CHILL");

            GeckoSpaFacade facade = with.CreateFacade();
            Assert.IsTrue(facade.HeatPump.IsAvailable);
            Assert.AreEqual("Cool", facade.HeatPump.State);
        }

        [TestMethod]
        public void TheKeypadOnlyHasButtonsForWhatIsFitted()
        {
            var spa = new FakeSpa()
                .WithOutput("Out1", "NA", "P1H")
                .WithOutput("Out2", "NA", "LI")
                .With("UdP1", EAccessorKind.Enum, items: OFF_LO_HI)
                .With("UdLi", EAccessorKind.Enum, items: OFF_LO_HI)
                .Build()
                .Set("Out1", "P1H")
                .Set("Out2", "LI");

            GeckoSpaFacade facade = spa.CreateFacade();

            var names = new List<string>();
            foreach (GeckoButton button in facade.Keypad.Buttons) names.Add(button.Name);

            CollectionAssert.AreEquivalent(new[] { "Key Pump 1", "Key Light" }, names);
        }

        [TestMethod]
        public void PressingAKeypadButtonAsksTheSpaToPressIt()
        {
            var spa = new FakeSpa()
                .WithOutput("Out1", "NA", "P1H")
                .With("UdP1", EAccessorKind.Enum, items: OFF_LO_HI)
                .Build()
                .Set("Out1", "P1H");

            GeckoSpaFacade facade = spa.CreateFacade();
            facade.Keypad.Buttons[0].PressAsync().GetAwaiter().GetResult();

            CollectionAssert.AreEqual(new[] { GeckoKeys.Keypad.PUMP_1 }, spa.PressedKeys);
        }

        [TestMethod]
        public void TheSpaIsInUseWhileAnythingIsRunning()
        {
            GeckoSpaFacade facade = TwoSpeedPump();
            Assert.IsFalse(facade.IsInUse);

            facade.Pump1.TurnOnAsync().GetAwaiter().GetResult();
            Assert.IsTrue(facade.IsInUse);

            facade.Pump1.TurnOffAsync().GetAwaiter().GetResult();
            Assert.IsFalse(facade.IsInUse);
        }

        [TestMethod]
        public void ChangingADeviceRaisesAChangeOnTheFacade()
        {
            GeckoSpaFacade facade = TwoSpeedPump();

            GeckoDevice changed = null;
            facade.Changed += (sender, e) => changed = e.Device;

            facade.Pump1.TurnOnAsync().GetAwaiter().GetResult();

            Assert.IsNotNull(changed);
            Assert.AreEqual("P1", changed.Key);
        }

        [TestMethod]
        public void WaterCareIsUnavailableOnUnitsThatDoNotHaveIt()
        {
            Assert.IsTrue(new FakeSpa("inxe").Build().CreateFacade().WaterCare.IsAvailable);
            Assert.IsFalse(new FakeSpa("mrsteam").Build().CreateFacade().WaterCare.IsAvailable);
            Assert.IsFalse(new FakeSpa("mas-ibc-32k").Build().CreateFacade().Reminders.IsAvailable);
        }

        [TestMethod]
        public void RefreshingReadsWaterCareAndRemindersFromTheSpa()
        {
            var spa = new FakeSpa().Build();
            spa.WatercareMode = EWatercareMode.Weekender;
            spa.RemindersToReturn = new List<Reminder>
            {
                new Reminder(EReminderType.RinseFilter, 4),
                new Reminder(EReminderType.Invalid, 0)
            };

            GeckoSpaFacade facade = spa.CreateFacade();
            facade.RefreshAsync().GetAwaiter().GetResult();

            Assert.AreEqual(EWatercareMode.Weekender, facade.WaterCare.Mode);
            Assert.AreEqual("Weekender", facade.WaterCare.State);

            // The protocol always returns ten slots; the unused ones are not reminders.
            Assert.AreEqual(1, facade.Reminders.Reminders.Count);
            Assert.AreEqual(EReminderType.RinseFilter, facade.Reminders.Reminders[0].Type);
        }

        [TestMethod]
        public void SettingOneReminderSendsTheWholeSetBack()
        {
            var spa = new FakeSpa().Build();
            spa.RemindersToReturn = new List<Reminder>
            {
                new Reminder(EReminderType.RinseFilter, 4),
                new Reminder(EReminderType.CleanFilter, 20),
                new Reminder(EReminderType.Invalid, 0)
            };

            GeckoSpaFacade facade = spa.CreateFacade();
            facade.RefreshAsync().GetAwaiter().GetResult();

            Assert.IsTrue(facade.Reminders.SetAsync(EReminderType.RinseFilter, 7).GetAwaiter().GetResult());

            // SETRM carries every slot, so the untouched ones must come back unchanged
            // or the spa forgets them.
            Assert.AreEqual(1, spa.WrittenReminders.Count);
            IList<Reminder> written = spa.WrittenReminders[0];
            Assert.AreEqual(3, written.Count);
            Assert.AreEqual(7, written[0].Days);
            Assert.AreEqual(EReminderType.CleanFilter, written[1].Type);
            Assert.AreEqual(20, written[1].Days);
            Assert.AreEqual(EReminderType.Invalid, written[2].Type);

            Assert.AreEqual(7, facade.Reminders.Get(EReminderType.RinseFilter).Days);
        }

        [TestMethod]
        public void ARejectedReminderWriteLeavesTheReportedDaysAlone()
        {
            var spa = new FakeSpa().Build();
            spa.RemindersToReturn = new List<Reminder> { new Reminder(EReminderType.RinseFilter, 4) };
            spa.AcceptReminderWrites = false;

            GeckoSpaFacade facade = spa.CreateFacade();
            facade.RefreshAsync().GetAwaiter().GetResult();

            Assert.IsFalse(facade.Reminders.SetAsync(EReminderType.RinseFilter, 7).GetAwaiter().GetResult());

            // Still what the spa last told us, not what we wanted it to be.
            Assert.AreEqual(4, facade.Reminders.Get(EReminderType.RinseFilter).Days);
        }

        [TestMethod]
        public void SettingAReminderTheSpaDoesNotTrackIsRejected()
        {
            var spa = new FakeSpa().Build();
            spa.RemindersToReturn = new List<Reminder> { new Reminder(EReminderType.RinseFilter, 4) };

            GeckoSpaFacade facade = spa.CreateFacade();
            facade.RefreshAsync().GetAwaiter().GetResult();

            Assert.ThrowsException<InvalidOperationException>(
                () => facade.Reminders.SetAsync(EReminderType.ChangeWater, 90).GetAwaiter().GetResult());

            Assert.AreEqual(0, spa.WrittenReminders.Count);
        }

        private static GeckoSpaFacade TwoSpeedPump()
        {
            return new FakeSpa()
                .WithOutput("Out1", "NA", "P1H")
                .WithOutput("Out2", "NA", "P1L")
                .With("UdP1", EAccessorKind.Enum, items: OFF_LO_HI)
                .Build()
                .Set("Out1", "P1H")
                .Set("Out2", "P1L")
                .CreateFacade();
        }

        private static GeckoSpaFacade Heater(string units, int current, int target)
        {
            return new FakeSpa()
                .With("TempUnits", EAccessorKind.Enum, items: new[] { "F", "C" })
                .With("DisplayedTempG", EAccessorKind.Temp)
                .With("SetpointG", EAccessorKind.Temp)
                .Build()
                .Set("TempUnits", units)
                .SetRaw("DisplayedTempG", current)
                .SetRaw("SetpointG", target)
                .CreateFacade();
        }
    }
}
