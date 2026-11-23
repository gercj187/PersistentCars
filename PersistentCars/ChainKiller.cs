using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Logic.Job;
using UnityEngine;

namespace PersistentCars
{
    /// <summary>
    /// Loest eine Jobkette VOLLSTAENDIG auf - nicht nur den Job.
    ///
    /// Hintergrund (Bug 1.02.00, neue Karriere): Der Departure guard hat einen Vanilla-Leerzug ab S-Gleis
    /// (z. B. SM-LH-61 ab SM-B8S) entfernt. Danach blieb eine leere, "uninitialisierte" Kette registriert, und
    /// jedes Speichern warf "Uninitialized chain controller!". Das erste Speichern passiert schon in der
    /// Lade-Routine des Spiels - die brach ab, der Spieler wurde unter die Welt teleportiert und nichts wurde
    /// mehr gespeichert.
    ///
    /// Kill(): Job ablaufen lassen (erlaubt), DestroyChain des Spiels, die Kette aus allen Listen austragen, die
    /// das Spiel fuer Ketten fuehrt (Bahnhof, Job-/Speicher-Verwaltung, statische Listen), Reservierungen
    /// freigeben und das GameObject zerstoeren.
    /// </summary>
    internal static class ChainKiller
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Kill(JobChainController chain, string reason)
        {
            if (chain == null) return;
            string id = null;
            try { id = chain.currentJobInChain?.ID; } catch { }

            JobAcceptance.WithExpireAllowed(() =>
            {
                // 1) Spiel-eigenes Aufloesen (laesst den Job ablaufen, gibt Reservierungen frei)
                try { chain.DestroyChain(); }
                catch (Exception ex)
                {
                    Main.WarningOnce("chain-destroy", "Removing a job chain: DestroyChain failed (" + ex.Message + ") - cleaning up manually.");
                    try { if (chain.currentJobInChain != null && chain.currentJobInChain.State == DV.ThingTypes.JobState.Available) chain.currentJobInChain.ExpireJob(); }
                    catch { }
                }
            });

            // 2) aus allen bekannten Listen austragen - eine leere Kette darf nirgends mehr auftauchen
            int removed = Unregister(chain);
            try { Reservations.Forget(chain); } catch { }

            // 3) GameObject weg
            try { if (chain.jobChainGO != null) UnityEngine.Object.Destroy(chain.jobChainGO); } catch { }

            Main.Log($"Job chain {id ?? "?"} removed completely ({reason}){(removed > 0 ? $", unregistered from {removed} list(s)" : "")}.");
        }

        /// <summary>Kette per Reflection aus allen Listen/Mengen entfernen, die Ketten fuehren.</summary>
        public static int Unregister(JobChainController chain)
        {
            int removed = 0;
            var owners = new List<object> { null };   // null = statische Felder
            try { if (JobSaveManager.Instance != null) owners.Add(JobSaveManager.Instance); } catch { }
            try { if (JobsManager.Instance != null) owners.Add(JobsManager.Instance); } catch { }
            try
            {
                foreach (var st in StationController.allStations)
                {
                    var ctl = st != null ? st.GetComponentInChildren<StationProceduralJobsController>(true) : null;
                    if (ctl != null) owners.Add(ctl);
                }
            }
            catch { }

            var types = new[] { typeof(JobChainController), typeof(JobSaveManager), typeof(JobsManager), typeof(StationProceduralJobsController) };
            foreach (var owner in owners)
            {
                var t = owner?.GetType();
                foreach (var type in owner == null ? types : new[] { t })
                {
                    foreach (var f in type.GetFields(Any))
                    {
                        if (owner == null ? !f.IsStatic : f.IsStatic) continue;
                        object value;
                        try { value = f.GetValue(owner); } catch { continue; }
                        removed += RemoveFrom(value, chain);
                    }
                }
            }
            return removed;
        }

        private static int RemoveFrom(object collection, JobChainController chain)
        {
            try
            {
                switch (collection)
                {
                    case null: return 0;
                    case IList list when list.Contains(chain):
                        while (list.Contains(chain)) list.Remove(chain);
                        return 1;
                    case IDictionary dict:
                        var keys = dict.Keys.Cast<object>().Where(k => ReferenceEquals(k, chain) || ReferenceEquals(dict[k], chain)).ToList();
                        foreach (var k in keys) dict.Remove(k);
                        return keys.Count > 0 ? 1 : 0;
                }
                // HashSet<JobChainController> u. a.: Remove per Reflection
                var type = collection.GetType();
                if (type.IsGenericType && type.GetGenericArguments().Length == 1 && type.GetGenericArguments()[0].IsAssignableFrom(typeof(JobChainController)))
                {
                    var remove = type.GetMethod("Remove", new[] { type.GetGenericArguments()[0] });
                    if (remove != null && remove.ReturnType == typeof(bool) && (bool)remove.Invoke(collection, new object[] { chain })) return 1;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Kette ohne gueltigen Job/Definition ("uninitialisiert")?</summary>
        public static bool IsBroken(JobChainController chain)
        {
            if (chain == null) return true;
            try
            {
                if (chain.jobChainGO == null) return true;
                if (chain.currentJobInChain == null) return true;
                var defs = TrackAssignment.ChainDefinitions(chain);
                return defs == null || !defs.Any();
            }
            catch { return true; }
        }

        // ------------------------------------------------------------------ Absicherung beim Speichern

        /// <summary>
        /// Finalizer auf JobChainController.GetJobChainSaveData: wirft eine kaputte Kette ("Uninitialized chain
        /// controller!"), wird sie uebersprungen (null) und aufgeraeumt - Speichern und die Lade-Routine laufen weiter.
        /// </summary>
        public static Exception GetJobChainSaveData_Finalizer(Exception __exception, JobChainController __instance, ref JobChainSaveData __result)
        {
            if (__exception == null) return null;
            Main.Warning("Saving: a broken job chain was skipped and removed (" + __exception.Message + ").");
            __result = null;
            try { Unregister(__instance); } catch { }
            try { if (__instance?.jobChainGO != null) UnityEngine.Object.Destroy(__instance.jobChainGO); } catch { }
            return null;
        }

        /// <summary>Postfix auf JobSaveManager.GetJobsSaveGameData: uebersprungene (null) Ketten aus der Liste.</summary>
        public static void GetJobsSaveGameData_Postfix(ref JobsSaveGameData __result)
        {
            if (__result?.jobChains == null) return;
            if (__result.jobChains.Any(c => c == null))
                __result.jobChains = __result.jobChains.Where(c => c != null).ToArray();
        }
    }
}
