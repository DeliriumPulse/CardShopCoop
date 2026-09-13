using System;
using HarmonyLib;

namespace CardShopCoop.Sync
{
    /// <summary>One ordered entry in CoopCore's module catalog. Lifecycle order is the
    /// catalog order. A null Module marks a patch-only static helper with no session
    /// lifecycle.</summary>
    internal sealed class CoopModuleEntry
    {
        public readonly ICoopModule Module;
        public readonly string Name;
        public readonly int HostSlot;
        public readonly int ClientSlot;
        public readonly Action<Harmony> Patches;

        public CoopModuleEntry(ICoopModule module, string name,
            int hostSlot = -1, int clientSlot = -1, Action<Harmony> patches = null)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Module name is required.", nameof(name));
            Module = module;
            Name = name;
            HostSlot = hostSlot;
            ClientSlot = clientSlot;
            Patches = patches;
        }
    }

    /// <summary>A patch registrar selected from the single module catalog.</summary>
    internal readonly struct CoopModulePatch
    {
        public readonly string Name;
        public readonly Action<Harmony> Apply;

        public CoopModulePatch(string name, Action<Harmony> apply)
        {
            Name = name;
            Apply = apply;
        }
    }
}
