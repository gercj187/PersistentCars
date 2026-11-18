using System;
using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;
using UnityEngine;

namespace PersistentCars
{
    /// <summary>
    /// Von S-Gleisen faehrt NIE ein Zug ab: kein Gueterzug (FH), kein Leerzug (LH), kein Zugumlauf (CH, CircleHauls),
    /// kein Schadwagenzug / keine Schadwagenueberfuehrung (SH/MH, TrainServiceRework) - egal welche Mod die Kette baut.
    /// Erlaubt sind dort nur Rangierjobs: Beladen (SL), Entladen (SU), Bereitstellen (SP) und Rangierjobs innerhalb
    /// eines Bahnhofs (Start- = Zielbahnhof, z. B. die SO/MS/SS-Jobs von TrainServiceRework).
    ///
    /// Jede neu finalisierte (oder geladene) Kette wird geprueft. Startet ihr anstehender, noch nicht angenommener
    /// Job als Zugfahrt von einem S-Gleis, wird die Kette einen Frame spaeter aufgeloest (nicht sofort - die Mod,
    /// die sie gebaut hat, arbeitet nach dem Finalisieren evtl. noch mit ihr). Die Wagen werden vorgemerkt und
    /// bekommen beim naechsten Nachfuellen eine Bereitstellung (SP) aufs O-Gleis; ab dort faehrt der Zug.
    /// Angenommene Jobs werden nie angefasst.
    /// </summary>
    internal static class DepartureGuard
    {
        private static readonly List<JobChainController> Queue = new List<JobChainController>();
        /// <summary>Wagen-IDs, deren Zug von S abfahren wollte - sie bekommen beim Nachfuellen direkt eine SP.</summary>
        internal static readonly HashSet<string> NeedPrepare = new HashSet<string>();
        private static int _logged;

        public static void Reset() { Queue.Clear(); NeedPrepare.Clear(); }

        /// <summary>Postfix auf JobChainController.FinalizeSetupAndGenerateFirstJob.</summary>
        public static void Finalize_Postfix(JobChainController __instance)
        {
            try
            {
                if (!Main.IsAuthority || __instance == null) return;
                if (DepartsFromStorage(__instance, out _, out _)) Queue.Add(__instance);
            }
            catch (Exception ex) { Main.WarningOnce("guard-check", "Departure guard: check failed: " + ex.Message); }
        }

        /// <summary>Jede Frame aus Main.OnUpdate.</summary>
        public static void Tick()
        {
            if (Queue.Count == 0) return;
            var list = Queue.ToList();
            Queue.Clear();
            foreach (var chain in list)
            {
                try
                {
                    // inzwischen angenommen, abgeschlossen oder schon aufgeloest -> nichts tun
                    if (chain?.jobChainGO == null || !DepartsFromStorage(chain, out string jobId, out Track track)) continue;

                    var cars = chain.carsForJobChain?.Where(c => c != null).ToList() ?? new List<Car>();
                    foreach (var c in cars) if (c.ID != null) NeedPrepare.Add(c.ID);

                    var destroy = Reflect.Method(typeof(JobChainController), "DestroyChain");
                    if (destroy != null && destroy.GetParameters().Length == 0)
                        JobAcceptance.WithExpireAllowed(() => destroy.Invoke(chain, null));
                    else
                        JobAcceptance.WithExpireAllowed(() => chain.currentJobInChain?.ExpireJob());

                    if (_logged++ < 60)
                        Main.Log($"Departure guard: {jobId} would leave from storage track {track.ID.FullDisplayID} - removed, " +
                                 $"its {cars.Count} car(s) get a shunting prepare (SP) to an O track at the next refill.");
                }
                catch (Exception ex) { Main.WarningOnce("guard-destroy", "Departure guard: removing a chain failed: " + ex.Message); }
            }
        }

        /// <summary>
        /// Anstehender Job verfuegbar (nicht angenommen), ist eine Zugfahrt zu einem anderen Bahnhof und die Wagen
        /// stehen auf einem S-Gleis.
        /// </summary>
        private static bool DepartsFromStorage(JobChainController chain, out string jobId, out Track track)
        {
            jobId = null;
            track = null;
            var job = chain.currentJobInChain;
            if (job == null || job.State != JobState.Available) return false;
            jobId = job.ID;

            var def = TrackAssignment.ChainDefinitions(chain).FirstOrDefault(d => d != null && d.job == job);
            if (def == null) return false;
            if (def is StaticShuntingLoadJobDefinition || def is StaticShuntingUnloadJobDefinition || def is PrepareJobDefinition) return false;
            var cd = def.chainData;
            if (cd != null && !string.IsNullOrEmpty(cd.chainOriginYardId) &&
                string.Equals(cd.chainOriginYardId, cd.chainDestinationYardId, StringComparison.Ordinal)) return false;   // Rangieren im Bahnhof

            // Gleis, auf dem die meisten Wagen der Kette stehen
            track = chain.carsForJobChain?
                .Where(c => c?.CurrentTrack != null)
                .GroupBy(c => c.CurrentTrack)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key).FirstOrDefault();
            if (track == null) return false;

            var station = StationController.GetStationByYardID(track.ID.yardId);
            return station?.logicStation?.yard?.StorageTracks?.Contains(track) ?? false;
        }
    }
}
