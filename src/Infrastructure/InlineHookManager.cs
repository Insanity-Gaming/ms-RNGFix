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

using System.Collections.Generic;
using Sharp.Shared.Hooks;

namespace InsanityGaming.RngFix.Infrastructure;

internal sealed class InlineHookWrapper
{
    private readonly IDetourHook _hook;

    public InlineHookWrapper(IDetourHook hook, nint address, nint funcPtr)
    {
        _hook = hook;

        _hook.Prepare(address, funcPtr);
    }

    public bool Install()
    {
        var result = _hook.Install();

        if (!result)
        {
            _hook.Dispose();
        }

        return result;
    }

    public void Uninstall()
    {
        _hook.Uninstall();
        _hook.Dispose();
    }

    public nint Trampoline => _hook.Trampoline;
}

internal interface IInlineHookManager
{
    bool AddHook(nint address, nint funcPtr, out nint trampoline);
}

// The part of Timer's InlineHookManager the movement fixes use: detours by address, all removed on shutdown.
internal sealed class InlineHookManager : IInlineHookManager
{
    private readonly InterfaceBridge         _bridge;
    private readonly List<InlineHookWrapper> _hooks = [];

    public InlineHookManager(InterfaceBridge bridge)
        => _bridge = bridge;

    public void Shutdown()
    {
        foreach (var hook in _hooks)
        {
            hook.Uninstall();
        }

        _hooks.Clear();
    }

    public bool AddHook(nint address, nint funcPtr, out nint trampoline)
    {
        var detour = _bridge.HookManager.CreateDetourHook();

        var hk = new InlineHookWrapper(detour, address, funcPtr);

        var result = hk.Install();
        trampoline = 0;

        if (!result)
        {
            return false;
        }

        trampoline = hk.Trampoline;
        _hooks.Add(hk);

        return true;
    }
}
