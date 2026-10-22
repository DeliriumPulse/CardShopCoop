using System;

namespace CardShopCoop.UI
{
    internal sealed class HsvPickerState
    {
        internal float Hue;
        internal float Saturation;
        internal float Value;
        private static float Clamp(float value) => Math.Max(0, Math.Min(1, value));

        internal void Reset(float hue, float saturation, float value)
        {
            Hue = Clamp(hue);
            Saturation = Clamp(saturation);
            Value = Clamp(value);
        }

        internal void Observe(float hue, float saturation, float value)
        {
            // RGB cannot encode hue for gray, or hue/saturation for black.
            // Keep the editor's selection instead of resetting it to red.
            if (saturation > 0.0001f && value > 0.0001f)
                Hue = Clamp(hue);
            if (value > 0.0001f)
                Saturation = Clamp(saturation);
            Value = Clamp(value);
        }

        internal void SelectHue(float hue)
        {
            Hue = Clamp(hue);
            if (Saturation <= 0.0001f)
                Saturation = 1;
            if (Value <= 0.0001f)
                Value = 1;
        }
    }
}
