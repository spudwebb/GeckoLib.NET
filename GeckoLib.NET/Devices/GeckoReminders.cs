using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GeckoLib.NET.Protocol.Messages;

namespace GeckoLib.NET.Devices
{
    /// <summary>
    /// The spa's maintenance reminders.
    ///
    /// Like water care these live outside the pack structure, so they are only as fresh
    /// as the last read.
    /// </summary>
    public sealed class GeckoReminders : GeckoDevice
    {
        private IList<Reminder> _all = new List<Reminder>();

        public GeckoReminders(IGeckoSpa spa)
            : base(spa, "Reminders", "REMINDERS")
        {
            IsAvailable = GeckoWaterCare.IsSpaPack(spa);
        }

        /// <summary>
        /// The reminders the spa is tracking. The protocol always returns ten slots; the
        /// unused ones come back as Invalid and are not included here.
        /// </summary>
        public IList<Reminder> Reminders
        {
            get
            {
                var active = new List<Reminder>();
                foreach (Reminder reminder in _all)
                {
                    if (reminder.Type != EReminderType.Invalid) active.Add(reminder);
                }

                return active;
            }
        }

        /// <summary>When the reminders were last read.</summary>
        public DateTime? LastUpdate { get; private set; }

        /// <summary>Find one reminder, or null if the spa is not tracking it.</summary>
        public Reminder Get(EReminderType type)
        {
            foreach (Reminder reminder in _all)
            {
                if (reminder.Type == type) return reminder;
            }

            return null;
        }

        /// <summary>Read the reminders from the spa.</summary>
        public async Task<IList<Reminder>> RefreshAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            IList<Reminder> reminders = await Spa.GetRemindersAsync(cancellationToken).ConfigureAwait(false);
            if (reminders != null) SetReminders(reminders);
            return reminders;
        }

        /// <summary>
        /// Reset one reminder's countdown. The whole set is written back, because the
        /// protocol has no way to set just one.
        /// </summary>
        public async Task<bool> SetAsync(
            EReminderType type, short days, CancellationToken cancellationToken = default(CancellationToken))
        {
            var updated = new List<Reminder>();
            bool found = false;

            foreach (Reminder reminder in _all)
            {
                if (reminder.Type == type)
                {
                    updated.Add(new Reminder(type, days));
                    found = true;
                }
                else
                {
                    updated.Add(reminder);
                }
            }

            if (!found)
            {
                throw new InvalidOperationException("This spa is not tracking a " + type + " reminder");
            }

            if (!await Spa.SetRemindersAsync(updated, cancellationToken).ConfigureAwait(false))
            {
                // Leave the local copy alone: it should keep saying what the spa last
                // told us, not what we wanted and failed to write.
                return false;
            }

            SetReminders(updated);
            return true;
        }

        /// <summary>Record reminders read elsewhere, e.g. by the facade's update loop.</summary>
        internal void SetReminders(IList<Reminder> reminders)
        {
            _all = reminders ?? new List<Reminder>();
            LastUpdate = DateTime.UtcNow;
            RaiseChanged();
        }

        public override string ToString()
        {
            if (LastUpdate == null) return Name + ": Waiting...";

            IList<Reminder> active = Reminders;
            if (active.Count == 0) return Name + ": none";

            var parts = new List<string>();
            foreach (Reminder reminder in active) parts.Add(reminder.ToString());
            return Name + ": " + string.Join(", ", parts.ToArray());
        }
    }
}
