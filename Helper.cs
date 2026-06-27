using System.Collections.Generic;
using UnityEngine;

namespace BRCCodeDmg
{
    public static class Helper
    {
        public const string DefaultPaletteName = "dmg";
        public const string DefaultPeerPaletteName = "bombrush-blue";

        public static int scale = 1;

        private static string _paletteName = DefaultPaletteName;
        public static string paletteName
        {
            get => _paletteName;
            set => _paletteName = NormalizePaletteName(value);
        }

        public static readonly string[] PaletteNames =
        {
            "dmg",
            "cyber",
            "emu",
            "autumn",
            "paris",
            "grayscale",
            "early",
            "crow",
            "coffee",
            "winter",
            "bombrush-orange",
            "bombrush-blue"
        };

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
                "cyber",
                new[]
                {
                    new Color32(50, 153, 180, 255),
                    new Color32(46, 116, 134, 255),
                    new Color32(2, 70, 88, 255),
                    new Color32(2, 49, 61, 255),
                    new Color32(255, 255, 255, 255)
                }
            },
            {
                "emu",
                new[]
                {
                    new Color32(224, 248, 208, 255),
                    new Color32(136, 192, 112, 255),
                    new Color32(52, 104, 86, 255),
                    new Color32(8, 24, 32, 255),
                    new Color32(255, 255, 255, 255)
                }
            },
            {
                "autumn",
                new[]
                {
                    new Color32(255, 246, 211, 255),
                    new Color32(249, 168, 117, 255),
                    new Color32(235, 107, 111, 255),
                    new Color32(124, 63, 88, 255),
                    new Color32(255, 255, 255, 255)
                }
            },
            {
                "paris",
                new[]
                {
                    new Color32(218, 112, 214, 255),
                    new Color32(186, 85, 211, 255),
                    new Color32(153, 50, 204, 255),
                    new Color32(75, 0, 130, 255),
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
            },
            {
                "early",
                new[]
                {
                    new Color32(0, 0, 0, 255),
                    new Color32(85, 85, 85, 255),
                    new Color32(170, 170, 170, 255),
                    new Color32(255, 255, 255, 255),
                    new Color32(255, 255, 255, 255)
                }
            },
            {
                "crow",
                new[]
                {
                    new Color32(204, 61, 80, 255),
                    new Color32(153, 31, 39, 255),
                    new Color32(89, 22, 22, 255),
                    new Color32(38, 15, 13, 255),
                    new Color32(255, 255, 255, 255)
                }
            },
            {
                "coffee",
                new[]
                {
                    new Color32(204, 158, 122, 255),
                    new Color32(153, 116, 92, 255),
                    new Color32(115, 77, 69, 255),
                    new Color32(77, 48, 46, 255),
                    new Color32(255, 255, 255, 255)
                }
            },
            {
                "winter",
                new[]
                {
                    new Color32(159, 244, 229, 255),
                    new Color32(0, 185, 190, 255),
                    new Color32(0, 95, 140, 255),
                    new Color32(0, 43, 89, 255),
                    new Color32(255, 255, 255, 255)
                }
            },
            {
                "bombrush-orange",
                new[]
                {
                    new Color32(255, 218, 170, 255),
                    new Color32(220, 101, 57, 255),
                    new Color32(143, 55, 42, 255),
                    new Color32(59, 25, 31, 255),
                    new Color32(255, 255, 255, 255)
                }
            },
            {
                "bombrush-blue",
                new[]
                {
                    new Color32(216, 228, 255, 255),
                    new Color32(63, 111, 194, 255),
                    new Color32(38, 60, 136, 255),
                    new Color32(13, 13, 63, 255),
                    new Color32(255, 255, 255, 255)
                }
            }
        };

        public static string NormalizePaletteName(string paletteName)
        {
            return NormalizePaletteName(paletteName, DefaultPaletteName);
        }

        public static string NormalizePaletteName(string paletteName, string fallbackPaletteName)
        {
            string fallback = string.IsNullOrWhiteSpace(fallbackPaletteName)
                ? DefaultPaletteName
                : fallbackPaletteName.Trim().ToLowerInvariant();

            if (!palettes.ContainsKey(fallback))
                fallback = DefaultPaletteName;

            if (string.IsNullOrWhiteSpace(paletteName))
                return fallback;

            string normalized = paletteName.Trim().ToLowerInvariant();
            return palettes.ContainsKey(normalized) ? normalized : fallback;
        }
    }
}