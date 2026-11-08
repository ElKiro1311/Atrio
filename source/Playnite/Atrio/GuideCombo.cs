using Playnite.SDK.Events;
using System;

namespace Playnite.Atrio
{
    /// <summary>
    /// Holding View and Menu together brings Atrio back. It's the way for controllers whose Guide button
    /// already belongs to Steam or to the Windows Game Bar: the two together are rare in a game, and
    /// holding them a second rarer still.
    /// </summary>
    public class GuideCombo
    {
        public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1);

        private bool back;
        private bool start;
        private DateTime? since;
        private bool fired;

        /// <summary>Both buttons are down and haven't fired yet: the moment to look again is <see cref="DueAt"/>.</summary>
        public bool IsHolding => since != null && !fired;

        public DateTime? DueAt => IsHolding ? since + HoldTime : null;

        public void Update(ControllerInput button, bool pressed, DateTime now)
        {
            if (button == ControllerInput.Back)
            {
                back = pressed;
            }
            else if (button == ControllerInput.Start)
            {
                start = pressed;
            }
            else
            {
                return;
            }

            if (back && start)
            {
                if (since == null)
                {
                    since = now;
                    fired = false;
                }
            }
            else
            {
                since = null;
                fired = false;
            }
        }

        /// <summary>True once per hold, when both buttons have been down long enough.</summary>
        public bool ShouldFire(DateTime now)
        {
            if (since == null || fired || now - since.Value < HoldTime)
            {
                return false;
            }

            fired = true;
            return true;
        }
    }
}
