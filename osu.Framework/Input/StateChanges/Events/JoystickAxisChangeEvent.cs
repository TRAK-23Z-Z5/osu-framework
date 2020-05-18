// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Input.States;

namespace osu.Framework.Input.StateChanges.Events
{
    public class JoystickAxisChangeEvent : InputStateChangeEvent
    {
        public readonly JoystickAxis Axis;
        public readonly float LastValue;

        public JoystickAxisChangeEvent(InputState state, IInput input, JoystickAxis axis, float lastValue)
            : base(state, input)
        {
            Axis = axis;
            LastValue = lastValue;
        }
    }
}
