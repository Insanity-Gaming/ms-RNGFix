/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 * Ported to RngFix (Insanity-Gaming/ms-RNGFix) from Source2Surf/Timer @ 063cd772d3e6ac64a5f9261f79c2d51a8ab6973c
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;

namespace InsanityGaming.RngFix.Infrastructure;

internal enum EGameMode
{
    None,
    Surf,
    Bhop,
}

// Stands in for Timer's timer-gamemodes.jsonc defaults: a map is in the first mode whose prefix its name starts with,
// in any case.
internal static class GameMode
{
    private static readonly (string Prefix, EGameMode Mode)[] Prefixes =
    [
        ("surf", EGameMode.Surf),
        ("bhop", EGameMode.Bhop),
    ];

    public static EGameMode Current { get; private set; } = EGameMode.None;

    public static void Refresh(string? mapName)
    {
        Current = EGameMode.None;

        if (string.IsNullOrEmpty(mapName))
        {
            return;
        }

        foreach (var (prefix, mode) in Prefixes)
        {
            if (mapName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Current = mode;

                return;
            }
        }
    }

    public static void Reset()
        => Current = EGameMode.None;
}
