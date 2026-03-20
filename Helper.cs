using System.Collections.Generic;
using UnityEngine;

namespace BRCCodeDmg
{
    public static class Helper
    {
        public static int scale = 1;
        public static string paletteName = "dmg";

        public static readonly Dictionary<string, Color32[]> palettes = new Dictionary<string, Color32[]>
        {
            {
                "dmg",
                new[]
                {
                    new Color32(155, 188, 15, 255),
                    new Color32(139, 172, 15, 255),
                    new Color32(48, 98, 48, 255),
                    new Color32(15, 56, 15, 255),
                    new Color32(255, 255, 255, 255)
                }
            },
            {
                "grayscale",
                new[]
                {
                    new Color32(255, 255, 255, 255),
                    new Color32(170, 170, 170, 255),
                    new Color32(85, 85, 85, 255),
                    new Color32(0, 0, 0, 255),
                    new Color32(255, 255, 255, 255)
                }
            }
        };
    }
}