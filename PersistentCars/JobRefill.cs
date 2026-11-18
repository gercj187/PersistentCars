using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using UnityEngine;
using Random = System.Random;

namespace PersistentCars
{
    /// <summary>
    /// Phase 2: Wagen ohne Job bekommen neue Jobketten - mit jeder vollen Spielstunde (auch durch Schlafen, das die Zeit
    /// vorspult). Eigene Umsetzung nach den Regeln dieser Mod, es spawnen dabei KEINE neuen Wagen (der Wagenspawner
    /// bleibt ruhig - jeder Bahnhof spawnt nur einmal, siehe TryToGenerateJobs_Prefix). Vorhandene Wagen ohne Job
    /// (manuell gespawnt, Jobkette abgearbeitet, ...) bekommen neue Jobketten:
    ///   beladen, Bahnhof nimmt die Fracht an  -> Entladen hier (Ziel S-Gleis)
    ///   beladen, sonst                        -> Gueterzug (FH) zu einem Bahnhof, der sie annimmt (Ziel I, sonst O) + Entladen dort
    ///   leer, Bahnhof kann sie beladen        -> Beladen hier (Ende auf O, sonst I) + Gueterzug + Entladen
    ///   leer, sonst                           -> Leerzug (LH) zu einem Bahnhof, der sie beladen kann (Ziel I) + SL -> FH -> SU dort
    ///   auf S/L, weder be- noch entladbar     -> Bereitstellung (SP): alle solchen Wagen des Bahnhofs mit gemeinsamem Ziel
    ///                                            einsammeln und auf ein O-Gleis stellen; bei Abgabe sofort LH bzw. FH ab O
    /// Von S-Gleisen faehrt nie ein Zug ab (siehe DepartureGuard).
    /// Nicht angefasst: Garnituren mit Lok, entgleiste, fahrende Wagen, Wagen in der Naehe
    /// eines Spielers und Wagen ausserhalb von Bahnhofsgleisen. Das Job-Limit pro Bahnhof gilt hier NICHT
    /// (nur beim ersten weltweiten Fuellen) - jeder jobfreie Wagen soll einen Job bekommen.
    /// </summary>
    internal static class JobRefill
    {
        public const string SaveKey = "LastRefillGameTime";

        /// <summary>Spielzeit (OADate) des letzten Durchlaufs; NaN = noch nie (wird beim ersten Check auf "jetzt" gesetzt).</summary>
        public static double LastRefill = double.NaN;
        public static bool Running { get; private set; }

        private static float _nextCheck;
        private static readonly Random Rng = new Random();

        // ------------------------------------------------------------------ Zeit

        private static PropertyInfo _wdInstance;
        private static FieldInfo _wdManager;
        private static bool _wdLooked;

        /// <summary>Spielzeit aus dem WeatherDriver (wie BedSleepingController), sonst null.</summary>
        public static DateTime? GameTime()
        {
            try
            {
                if (!_wdLooked)
                {
                    _wdLooked = true;
                    Type wd = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        wd = asm.GetType("DV.WeatherSystem.WeatherDriver", false) ?? asm.GetType("WeatherDriver", false);
                        if (wd != null) break;
                    }
                    _wdInstance = wd?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                    _wdManager = wd?.GetField("manager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_wdInstance == null || _wdManager == null)
                        Main.Warning("Job refill: game time (WeatherDriver) not found - refill only via the debug button.");
                }
                var inst = _wdInstance?.GetValue(null, null);
                var mgr = inst != null ? _wdManager.GetValue(inst) : null;
                var dt = mgr?.GetType().GetProperty("DateTime")?.GetValue(mgr, null);
                return dt is DateTime d ? d : (DateTime?)null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Jede Frame aus Main.OnUpdate - prueft jede Sekunde (Echtzeit), ob eine neue volle Spielstunde begonnen hat.
        /// Dann laeuft ein Durchlauf. Springt die Zeit mehrere Stunden (Schlafen, Zeitraffer), gibt es EINEN Durchlauf.
        /// </summary>
        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + 1f;
            if (!Main.IsActive || !Main.IsAuthority || Running || WorldSpawner.Running || Runtime.Spawner == null) return;
            if (!WorldStreamingInit.IsLoaded || !AStartGameData.carsAndJobsLoadingFinished || !WorldInit.Initialized) return;

            var now = GameTime();
            if (now == null) return;
            // volle Stunde als Zahl (OADate * 24, abgerundet); jede Aenderung - auch rueckwaerts (Tageswechsel
            // bei Spielen, die nur die Uhrzeit weiterzaehlen) - ist eine neue volle Stunde
            double hour = Math.Floor(now.Value.ToOADate() * 24.0);
            if (double.IsNaN(LastRefill)) { LastRefill = hour / 24.0; return; }   // erster Check: ab jetzt zaehlen
            double lastHour = Math.Floor(LastRefill * 24.0 + 1e-6);
            if (hour == lastHour) return;

            Main.Log($"Job refill: new game hour {now.Value:HH}:00 - jobless cars get new jobs.");
            LastRefill = hour / 24.0;
            Start();
        }

        public static void Start()
        {
            if (Running || Runtime.Spawner == null || !Main.IsAuthority) return;
            Runtime.Spawner.StartCoroutine(Run());
        }

        // ------------------------------------------------------------------ Ablauf

        private sealed class Group
        {
            public StationController Station;
            public Track Track;
            public List<TrainCar> Cars;
            public bool Loaded;
        }

        private static IEnumerator Run()
        {
            Running = true;
            int created = 0, skipped = 0;
            var counts = new Dictionary<string, int>();
            try
            {
                _dumped.Clear();
                _shuntAssigned.Clear();
                _notTaken = Reservations.ReservedByNotTakenJobs();
                var groups = CollectGroups();
                Main.Log($"Job refill: {groups.Count} group(s) of jobless cars found (skipped: {_exLoco} trainset(s) with loco/tender/caboose, " +
                         $"{_exDerailed} derailed, {_exMoving} moving, {_exNoStationTrack} jobless car(s) outside S/I/O/L station tracks).");
                var prepare = new List<Group>();
                var loadable = new List<Group>();
                foreach (var g in groups)
                {
                    if (g.Station == null) continue;

                    JobChainController chain = null;
                    string kind = "";
                    _why = "no free fitting track at this or a target station";
                    try
                    {
                        // Von S-Gleisen faehrt NIE ein Zug ab: dort nur Rangierjobs (SL/SU/SP). Gueterzug (FH) und Leerzug
                        // (LH) starten nur von I/O. Wagen auf S, die der Bahnhof weder beladen noch entladen kann,
                        // werden gesammelt und bekommen eine Bereitstellung (SP) aufs O-Gleis (siehe unten).
                        bool onStorage = (g.Station.logicStation.yard.StorageTracks?.Contains(g.Track) ?? false) || IsLoadingTrack(g.Station, g.Track);
                        if (onStorage && g.Cars.Any(c => c?.logicCar?.ID != null && DepartureGuard.NeedPrepare.Contains(c.logicCar.ID)))
                        {
                            prepare.Add(g);   // Zug wollte von S abfahren (z. B. Schadwagen einer anderen Mod) -> direkt bereitstellen
                            continue;
                        }
                        if (g.Loaded)
                        {
                            var cargo = g.Cars.Select(c => c.logicCar.CurrentCargoTypeInCar).Distinct().ToList();
                            if (onStorage && !CanUnloadHere(g.Station, cargo)) { prepare.Add(g); continue; }
                            chain = TryUnloadHere(g, out kind) ?? (onStorage ? null : TryHaulLoaded(g, out kind));
                        }
                        else
                        {
                            if (onStorage && !CanLoad(g.Station, g.Cars)) { prepare.Add(g); continue; }
                            // beladbar und nicht beschaedigt: erst sammeln - mehrere Gruppen werden zu EINEM Beladejob
                            // mit mehreren Abholungen gebuendelt (siehe unten)
                            if (CanLoad(g.Station, g.Cars) && !AnyDamaged(g.Cars)) { loadable.Add(g); continue; }
                            chain = TryLoadHere(g, out kind) ?? (onStorage ? null : TryEmptyHaul(g, out kind));
                        }
                        if (chain == null && onStorage) kind = "none - storage/loading track, shunting jobs only: " + _why;
                    }
                    catch (Exception ex)
                    {
                        Main.Warning($"Job refill: {g.Station.logicStation.ID} {g.Track.ID.FullDisplayID}: {ex.Message}");
                    }

                    if (chain == null)
                    {
                        skipped++;
                        // Grund ins Log: meist ist kein passendes Gleis frei (Ziel I/O voll, kein freies S/O zum Entladen/Beladen)
                        if (skipped <= 60)
                            Main.Log($"Job refill: {g.Station.logicStation.ID} {g.Track.ID.FullDisplayID}: {g.Cars.Count} {(g.Loaded ? "loaded" : "empty")} car(s) -> no job " +
                                     $"({(kind.StartsWith("none") ? kind : _why)}).");
                        continue;
                    }
                    if (FinalizeChain(chain, g, kind))
                    {
                        created++;
                        counts[kind] = counts.TryGetValue(kind, out int n) ? n + 1 : 1;
                    }
                    yield return null; // eine Kette pro Frame - keine Ruckler
                }

                // Beladejobs (SL): je Bahnhof die gesammelten leeren Wagen nach gemeinsamer Fracht buendeln
                foreach (var st in loadable.GroupBy(x => x.Station))
                {
                    var open = st.ToList();
                    while (open.Count > 0)
                    {
                        var bundle = BuildLoadBundle(st.Key, open, out string bkind);
                        if (bundle.Chain != null)
                        {
                            foreach (var x in bundle.Groups) open.Remove(x);
                            var first = bundle.Groups[0];
                            var all = new Group { Station = first.Station, Track = first.Track, Loaded = false, Cars = bundle.Groups.SelectMany(x => x.Cars).ToList() };
                            if (FinalizeChain(bundle.Chain, all, $"{bkind} ({bundle.Groups.Count} pick-up(s))"))
                            {
                                created++;
                                counts[bkind] = counts.TryGetValue(bkind, out int n) ? n + 1 : 1;
                            }
                            yield return null;
                            continue;
                        }

                        // kein Buendel moeglich: erste Gruppe einzeln wie bisher (Beladen, sonst Leerzug von I/O)
                        var g1 = open[0];
                        open.RemoveAt(0);
                        string kind = "";
                        JobChainController chain = null;
                        bool onStorage = (g1.Station.logicStation.yard.StorageTracks?.Contains(g1.Track) ?? false) || IsLoadingTrack(g1.Station, g1.Track);
                        try { chain = TryLoadHere(g1, out kind) ?? (onStorage ? null : TryEmptyHaul(g1, out kind)); }
                        catch (Exception ex) { Main.Warning($"Job refill: {g1.Station.logicStation.ID} {g1.Track.ID.FullDisplayID}: {ex.Message}"); }
                        if (chain == null)
                        {
                            skipped++;
                            if (skipped <= 60)
                                Main.Log($"Job refill: {g1.Station.logicStation.ID} {g1.Track.ID.FullDisplayID}: {g1.Cars.Count} empty car(s) -> no job ({_why}).");
                            continue;
                        }
                        if (FinalizeChain(chain, g1, kind))
                        {
                            created++;
                            counts[kind] = counts.TryGetValue(kind, out int n) ? n + 1 : 1;
                        }
                        yield return null;
                    }
                }

                // Bereitstellungen (SP): je Bahnhof die gesammelten S-Wagen nach gemeinsamem Ziel buendeln
                foreach (var st in prepare.GroupBy(g => g.Station))
                {
                    foreach (var plan in PlanPrepare(st.Key, st.ToList()))
                    {
                        if (plan.Chain == null)
                        {
                            skipped += plan.Groups.Count;
                            if (skipped <= 60) Main.Log($"Job refill: {st.Key.logicStation.ID}: {plan.Groups.Sum(x => x.Cars.Count)} car(s) on storage tracks -> no shunting prepare ({plan.Why}).");
                            continue;
                        }
                        var first = plan.Groups[0];
                        var all = new Group { Station = first.Station, Track = first.Track, Loaded = first.Loaded, Cars = plan.Groups.SelectMany(x => x.Cars).ToList() };
                        string kind = "shunting prepare";
                        if (FinalizeChain(plan.Chain, all, $"{kind} ({plan.Groups.Count} pick-up(s) -> {plan.EndTrack.ID.FullDisplayID}, then to {(string.IsNullOrEmpty(plan.Target) ? "any station" : plan.Target)})"))
                        {
                            created++;
                            counts[kind] = counts.TryGetValue(kind, out int n) ? n + 1 : 1;
                            foreach (var c in all.Cars) if (c?.logicCar?.ID != null) DepartureGuard.NeedPrepare.Remove(c.logicCar.ID);
                        }
                        yield return null;
                    }
                }
            }
            finally
            {
                Running = false;
                Main.Log($"Job refill finished: {created} new job chain(s) ({string.Join(", ", counts.Select(kv => kv.Key + " " + kv.Value))}), {skipped} group(s) left without a job.");
            }
        }

        /// <summary>Kette finalisieren (Job erzeugen), Tafeln setzen, bei Fehler sauber aufloesen.</summary>
        private static bool FinalizeChain(JobChainController chain, Group g, string kind)
        {
            try
            {
                // Vanilla verweigert manuell gespawnte Wagen in Jobs ("Bad job logic, player spawned car used in job").
                // Gewuenscht ist aber, dass auch sie Jobs bekommen -> Merker entfernen (die Mod loescht nie Wagen).
                foreach (var c in g.Cars) ConvertPlayerSpawned(c);
                chain.FinalizeSetupAndGenerateFirstJob();
                PlateSync.Update(chain);
                Main.Log($"Job refill: {g.Station.logicStation.ID} {g.Track.ID.FullDisplayID}: {g.Cars.Count} car(s) -> {kind}.");
                return true;
            }
            catch (Exception ex)
            {
                Main.Warning($"Job refill: finalizing chain at {g.Station.logicStation.ID} failed: {ex.Message}");
                // Reservierungen nicht liegen lassen: Kette sauber aufloesen, sonst nur das GameObject entfernen
                try
                {
                    var destroy = Reflect.Method(typeof(JobChainController), "DestroyChain");
                    if (destroy != null && destroy.GetParameters().Length == 0) JobAcceptance.WithExpireAllowed(() => destroy.Invoke(chain, null));
                    else UnityEngine.Object.Destroy(chain.jobChainGO);
                }
                catch { try { UnityEngine.Object.Destroy(chain.jobChainGO); } catch { } }
                return false;
            }
        }

        // ------------------------------------------------------------------ Beladen mit mehreren Abholungen (SL)

        private sealed class LoadBundle
        {
            public List<Group> Groups = new List<Group>();
            public JobChainController Chain;
        }

        /// <summary>
        /// Ein Beladejob fuer mehrere einzeln stehende leere Wagen(gruppen) eines Bahnhofs: Fracht waehlen, die die
        /// meisten Wagen laden koennen, dann Abholungen sammeln (Wagenzahl des Bahnhofs, laengstes O-Gleis), freies
        /// O-Gleis fuer danach und ein Zielbahnhof mit freiem I-Gleis. Nur sinnvoll ab 2 Gruppen - eine einzelne Gruppe
        /// geht den bisherigen Weg (TryLoadHere, dort haengen auch andere Mods wie TrainServiceRework dran).
        /// </summary>
        private static LoadBundle BuildLoadBundle(StationController station, List<Group> open, out string kind)
        {
            kind = "load + freight haul";
            var result = new LoadBundle();
            if (open.Count < 2) return result;
            var rs = station.proceduralJobsRuleset;
            var yard = station.logicStation.yard;
            if (rs?.outputCargoGroups == null) return result;
            int maxCars = Math.Max(1, rs.maxCarsPerJob);
            // groesster FREIER Platz auf einem O-Gleis (nach dem Beladen), nicht die Gleislaenge
            float maxLen = yard.TransferOutTracks?.Where(t => t != null).Select(t => ShuntFree(t)).DefaultIfEmpty(0f).Max() ?? 0f;
            if (maxLen <= 0f) return result;

            // (Frachtgruppe, Fracht) -> Gruppen, die sie laden koennen
            var options = new List<(List<StationController> stations, CargoType ct, List<Group> groups)>();
            foreach (var cg in rs.outputCargoGroups)
                foreach (var ct in cg.cargoTypes ?? new List<CargoType>())
                {
                    if (ct == CargoType.None) continue;
                    if ((yard.GetWarehouseMachinesThatSupportCargoTypes(new List<CargoType> { ct })?.Count ?? 0) == 0) continue;
                    var fit = open.Where(x => x.Cars.All(c => ct.ToV2().IsLoadableOnCarType(c.carLivery.parentType))).ToList();
                    if (fit.Count >= 2) options.Add((cg.stations ?? new List<StationController>(), ct, fit));
                }

            foreach (var opt in options.OrderByDescending(o => o.groups.Sum(x => x.Cars.Count)).ThenBy(_ => Rng.Next()))
            {
                // Abholungen sammeln
                var picked = new List<Group>();
                int count = 0;
                float len = 0f;
                foreach (var x in opt.groups.OrderBy(q => q.Track.ID.FullDisplayID))
                {
                    float gl = Length(x.Cars);
                    if (count + x.Cars.Count > maxCars || len + gl > maxLen) continue;
                    picked.Add(x);
                    count += x.Cars.Count;
                    len += gl;
                }
                if (picked.Count < 2) continue;

                var pickupTracks = new HashSet<Track>(picked.Select(x => x.Track));
                var cars = picked.SelectMany(x => x.Cars).ToList();
                var ctList = new List<CargoType> { opt.ct };
                var machines = yard.GetWarehouseMachinesThatSupportCargoTypes(ctList);
                var outTrack = ShuntTrack(yard.TransferOutTracks?.Where(t => !pickupTracks.Contains(t)).ToList(), len);   // Abfahrt: immer O
                if (outTrack == null) { DumpTransferTracks(station); continue; }

                foreach (var dst in opt.stations.Where(s => s != null && s != station).OrderBy(_ => Rng.Next()))
                {
                    var dy = dst.logicStation.yard;
                    var destTrack = FreeTrack(dy.TransferInTracks, len);   // Ankunft: immer I
                    if (destTrack == null) continue;

                    var cargoPerCar = cars.Select(_ => opt.ct).ToList();
                    var pickups = picked.GroupBy(x => x.Track)
                        .Select(t => new CarsPerTrack(t.Key, TrainCar.ExtractLogicCars(t.SelectMany(x => x.Cars).ToList())))
                        .ToList();
                    var chain = NewChain(station, dst, cars, JobType.ShuntingLoad);
                    chain.AddJobDefinitionToChain(LoadDefinition(chain, station, dst, picked[0].Track, outTrack, machines[Rng.Next(machines.Count)], cars, cargoPerCar, pickups));
                    chain.AddJobDefinitionToChain(TransportDefinition(chain, station, dst, outTrack, destTrack, cars, cargoPerCar));
                    var um = dy.GetWarehouseMachinesThatSupportCargoTypes(ctList);
                    var unloadTrack = UnloadTarget(dst, cars, len, destTrack);
                    if (um != null && um.Count > 0 && unloadTrack != null)
                    {
                        chain.AddJobDefinitionToChain(UnloadDefinition(chain, station, dst, destTrack, unloadTrack, um[Rng.Next(um.Count)], cars, cargoPerCar));
                        kind = "load + freight haul + unload";
                    }
                    result.Groups = picked;
                    result.Chain = chain;
                    return result;
                }
                // kein Ziel frei: O-Gleis wieder freigeben
                if (_shuntAssigned.TryGetValue(outTrack, out float a)) _shuntAssigned[outTrack] = Math.Max(0f, a - len);
            }
            return result;
        }

        private static MethodInfo _tsrIsDamaged;
        private static bool _tsrLooked;

        /// <summary>Schadwagen (TrainServiceRework) gehen den Einzelweg - TSR haengt sich dort ein und baut eigene Jobs.</summary>
        private static bool AnyDamaged(List<TrainCar> cars)
        {
            try
            {
                if (!_tsrLooked)
                {
                    _tsrLooked = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name != "TrainServiceRework") continue;
                        var t = asm.GetType("TrainServiceRework.DamagedJobSystem", false);
                        _tsrIsDamaged = t?.GetMethod("IsDamaged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(TrainCar) }, null);
                    }
                }
                if (_tsrIsDamaged == null) return false;
                foreach (var c in cars)
                    if (c != null && _tsrIsDamaged.Invoke(null, new object[] { c }) is bool b && b) return true;
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------ Bereitstellung (SP)

        private sealed class PreparePlan
        {
            public List<Group> Groups = new List<Group>();
            public JobChainController Chain;
            public Track EndTrack;
            public string Target;
            public string Why = "";
        }

        /// <summary>
        /// Moegliche Ziele der Folgefahrt: leer -> Bahnhoefe, die die Wagen beladen koennen; beladen -> Bahnhoefe,
        /// die die Fracht annehmen. Vorgemerkte Wagen (Zug wollte von S abfahren) -> null = jedes Ziel, das danach
        /// gewaehlt wird (z. B. von TrainServiceRework fuer Schadwagen).
        /// </summary>
        private static HashSet<StationController> PrepareTargets(Group g)
        {
            if (g.Cars.Any(c => c?.logicCar?.ID != null && DepartureGuard.NeedPrepare.Contains(c.logicCar.ID))) return null;
            var result = new HashSet<StationController>();
            foreach (var s in StationController.allStations)
            {
                if (s == null || s == g.Station) continue;
                if (g.Loaded)
                {
                    var cargo = g.Cars.Select(c => c.logicCar.CurrentCargoTypeInCar).Distinct().ToList();
                    if (CanUnloadHere(s, cargo)) result.Add(s);
                }
                else if (CanLoad(s, g.Cars)) result.Add(s);
            }
            return result;
        }

        /// <summary>
        /// Buendelt die S-Wagen eines Bahnhofs zu Bereitstellungen: Ziel waehlen, das die meisten Wagen gemeinsam haben,
        /// dann so viele Abholungen dazunehmen, wie Wagenzahl (Regelwerk des Bahnhofs) und ein freies O-Gleis erlauben.
        /// </summary>
        private static List<PreparePlan> PlanPrepare(StationController station, List<Group> groups)
        {
            var plans = new List<PreparePlan>();
            var y = station.logicStation.yard;
            int maxCars = Math.Max(1, station.proceduralJobsRuleset != null ? station.proceduralJobsRuleset.maxCarsPerJob : 20);
            // Abfahrt immer von O. Nur wenn der Bahnhof gar keine O-Gleise hat (z. B. CP), muss es ein I-Gleis sein.
            var departure = (y.TransferOutTracks?.Where(t => t != null).ToList() ?? new List<Track>());
            if (departure.Count == 0) departure = y.TransferInTracks?.Where(t => t != null).ToList() ?? new List<Track>();
            var targets = groups.ToDictionary(g => g, PrepareTargets);
            var open = groups.OrderBy(g => g.Track.ID.FullDisplayID).ToList();

            while (open.Count > 0)
            {
                // 1) vorgemerkte Wagen (Ziel offen), sonst das Ziel, das die meisten Wagen teilen
                StationController target = null;
                List<Group> fitting;
                var anyTarget = open.Where(g => targets[g] == null).ToList();
                if (anyTarget.Count > 0) fitting = anyTarget;
                else
                {
                    target = open.SelectMany(g => targets[g].Select(s => (s, n: g.Cars.Count)))
                                 .GroupBy(x => x.s).OrderByDescending(x => x.Sum(v => v.n)).Select(x => x.Key).FirstOrDefault();
                    if (target == null)
                    {
                        plans.Add(new PreparePlan { Groups = open.ToList(), Why = g0Why(open[0]) });
                        break;
                    }
                    fitting = open.Where(g => targets[g].Contains(target)).ToList();
                }

                // 2) Abholungen sammeln: Wagenzahl und Laenge begrenzen - Laenge = der groesste FREIE Platz auf einem
                //    O-Gleis (nicht die Gleislaenge), sonst passt das Buendel nirgends hin
                float maxLen = departure.Count > 0 ? departure.Max(t => ShuntFree(t)) : 0f;
                if (maxLen < Length(fitting.OrderBy(g => Length(g.Cars)).First().Cars))
                {
                    plans.Add(new PreparePlan { Groups = open.ToList(), Why = departure.Count == 0
                        ? $"{station.logicStation.ID} has no O/I tracks"
                        : $"no free space on an O track at {station.logicStation.ID} (largest free {Math.Max(0f, maxLen):0} m)" });
                    DumpTransferTracks(station);
                    break;
                }
                var plan = new PreparePlan { Target = target?.logicStation?.ID };
                int count = 0;
                float len = 0f;
                foreach (var g in fitting)
                {
                    float gl = Length(g.Cars);
                    if (count + g.Cars.Count > maxCars && plan.Groups.Count > 0) continue;
                    if (len + gl > maxLen) continue;
                    plan.Groups.Add(g);
                    count += g.Cars.Count;
                    len += gl;
                }
                foreach (var g in plan.Groups) open.Remove(g);

                // 3) freies O-Gleis (Rangierjobs haben Vorrang), nie ein Abholgleis
                var pickupTracks = new HashSet<Track>(plan.Groups.Select(g => g.Track));
                plan.EndTrack = ShuntTrack(departure.Where(t => !pickupTracks.Contains(t)).ToList(), len);
                if (plan.EndTrack == null)
                {
                    plan.Why = $"no free O track at {station.logicStation.ID} for {len:0} m";
                    DumpTransferTracks(station);
                    plans.Add(plan);
                    continue;
                }

                // Abholungen je Gleis zusammenfassen (Reihenfolge wie im Zug)
                var pickups = plan.Groups.GroupBy(g => g.Track)
                    .Select(t => new CarsPerTrack(t.Key, TrainCar.ExtractLogicCars(t.SelectMany(g => g.Cars).ToList())))
                    .ToList();
                var cars = plan.Groups.SelectMany(g => g.Cars).ToList();
                plan.Chain = ShuntingPrepare.Build(station, pickups, plan.EndTrack, plan.Target, cars);
                plans.Add(plan);
            }
            return plans;
        }

        private static string g0Why(Group g) => g.Loaded
            ? "no station accepts this cargo"
            : $"no station can load {g.Cars[0].carLivery?.parentType?.name ?? "these cars"}";

        private static bool CanUnloadHere(StationController s, List<CargoType> cargo)
            => Accepts(s, cargo) && (s.logicStation.yard.GetWarehouseMachinesThatSupportCargoTypes(cargo)?.Count ?? 0) > 0;

        /// <summary>Bevorzugtes Ziel fuer die naechste Fahrt (Folgefahrt nach einer Bereitstellung).</summary>
        private static string _preferTarget;

        private static IEnumerable<StationController> PreferFirst(IEnumerable<StationController> list)
        {
            var l = list.ToList();
            if (string.IsNullOrEmpty(_preferTarget)) return l;
            var p = l.FirstOrDefault(s => s.logicStation.ID == _preferTarget);
            if (p == null) return l;
            l.Remove(p);
            l.Insert(0, p);
            return l;
        }

        /// <summary>
        /// Nach Abgabe einer Bereitstellung (SP): die Wagen stehen auf dem O-Gleis (oder einem Ausweichgleis) und
        /// bekommen sofort ihre Fahrt - leer: LH (-> SL -> FH -> SU), beladen: FH (-> SU). Bevorzugt zum geplanten Ziel.
        /// Klappt das nicht, bleiben sie jobfrei und bekommen zur naechsten vollen Spielstunde einen Job.
        /// </summary>
        internal static void CreateFollowUp(List<Car> logicCars, string stationId, string targetYardId)
        {
            if (!Main.IsAuthority || logicCars == null || logicCars.Count == 0) return;
            var jm = JobsManager.Instance;
            var cars = TrainCar.ExtractTrainCars(logicCars.Where(c => c != null).ToList()).Where(c => c != null).ToList();
            if (cars.Count == 0) return;
            if (cars.Any(c => jm.GetJobOfCar(c.logicCar) != null)) { Main.Log("Job refill: shunting prepare handed in - cars already have a job, no follow-up."); return; }
            if (cars.Any(c => c.derailed)) { Main.Log("Job refill: shunting prepare handed in - a car is derailed, follow-up at the next refill."); return; }

            var track = cars.Where(c => c.logicCar?.CurrentTrack != null).GroupBy(c => c.logicCar.CurrentTrack)
                            .OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
            var trackToStation = TrackStations();
            if (track == null || !trackToStation.TryGetValue(track, out var station))
            {
                Main.Log("Job refill: shunting prepare handed in - cars are not on a station track, follow-up at the next refill.");
                return;
            }

            bool loaded = cars.Any(c => c.logicCar.CurrentCargoTypeInCar != CargoType.None);
            var g = new Group { Station = station, Track = track, Cars = cars, Loaded = loaded };
            if (!Running)
            {
                _notTaken = Reservations.ReservedByNotTakenJobs();
                _shuntAssigned.Clear();
            }

            JobChainController chain = null;
            string kind = "";
            _why = "no free fitting track at a target station";
            _preferTarget = targetYardId;
            try
            {
                chain = loaded
                    ? TryUnloadHere(g, out kind) ?? TryHaulLoaded(g, out kind)
                    : TryLoadHere(g, out kind) ?? TryEmptyHaul(g, out kind);
            }
            finally { _preferTarget = null; }

            if (chain == null)
            {
                Main.Log($"Job refill: shunting prepare handed in at {station.logicStation.ID} {track.ID.FullDisplayID} - no follow-up job yet ({(kind.StartsWith("none") ? kind : _why)}), next try at the next full hour.");
                return;
            }
            FinalizeChain(chain, g, "after shunting prepare: " + kind);
        }

        /// <summary>
        /// Manuell gespawnter Wagen -> normaler Job-Wagen. Der Merker sitzt doppelt (TrainCar und Logik-Wagen,
        /// dort readonly), und solche Wagen haben kein Schadens-/Gebuehrenmodell - ohne das scheitert die Job-Logik.
        /// </summary>
        private static void ConvertPlayerSpawned(TrainCar car)
        {
            if (car == null || !car.playerSpawnedCar) return;
            car.playerSpawnedCar = false;
            if (car.logicCar != null) Reflect.Set(car.logicCar, "playerSpawnedCar", false);

            // Viele dieser Mitglieder sind im Spiel privat -> per Reflection (ohne Publicizer)
            var save = Reflect.Get<object>(car, "carStateSave");
            if (save == null || Reflect.Get<object>(save, "debtTrackerCar") != null) return;   // hat schon Gebuehren-Tracking
            var plates = Reflect.Get<object>(car, "trainPlatesCtrl");

            var carDamage = car.CarDamage;
            if (carDamage == null)
            {
                carDamage = car.gameObject.AddComponent<DV.Damage.CarDamageModel>();
                SetAny(car, carDamage, "CarDamage", "<CarDamage>k__BackingField", "carDamage");
                Call(carDamage, "OnCreated", car);
                Hook(carDamage, "CarEffectiveHealthStateUpdate", "EffectiveHealthPercentage100Notation", plates, "UpdateCarHealthData");
            }

            var cargoDamage = car.CargoDamage;
            if (cargoDamage == null && !car.IsLoco)
            {
                cargoDamage = car.gameObject.AddComponent<DV.Damage.CargoDamageModel>();
                SetAny(car, cargoDamage, "CargoDamage", "<CargoDamage>k__BackingField", "cargoDamage");
                Call(cargoDamage, "OnCreated", car);
                Hook(cargoDamage, "CargoEffectiveHealthStateUpdate", "EffectiveHealthPercentage100Notation", plates, "UpdateCargoHealthData");
            }

            var oldDebt = car.gameObject.GetComponent<DV.ServicePenalty.CarDebtController>();
            if (oldDebt != null) UnityEngine.Object.DestroyImmediate(oldDebt, true);
            var debt = car.gameObject.AddComponent<DV.ServicePenalty.CarDebtController>();
            Call(debt, "OnCreated", car, false);
            Reflect.Set(debt, "ignoreCarDamageDebt", false);
            Call(debt, "SetDebtTracker", carDamage, cargoDamage);
            Call(save, "Initialize", carDamage, cargoDamage);
            Call(save, "SetDebtTrackerCar", Reflect.Get<object>(debt, "CarDebtTracker"));
            Main.Log($"Job refill: manually spawned car {car.ID} converted to a regular job car.");
        }

        /// <summary>Erstes setzbares Mitglied gewinnt (Property mit privatem Setter, sonst Backing-Field).</summary>
        private static void SetAny(object obj, object value, params string[] names)
        {
            foreach (var n in names)
            {
                try { if (Reflect.Set(obj, n, value)) return; } catch { }
            }
            Main.WarningOnce("setany-" + names[0], $"Job refill: could not set {obj.GetType().Name}.{names[0]}.");
        }

        /// <summary>Methode per Reflection aufrufen (passende Parameterzahl).</summary>
        private static object Call(object obj, string name, params object[] args)
        {
            if (obj == null) return null;
            for (var t = obj.GetType(); t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == name && m.GetParameters().Length == args.Length)
                        return m.Invoke(obj, args);
            Main.WarningOnce("call-" + name, $"Job refill: method {obj.GetType().Name}.{name} not found.");
            return null;
        }

        /// <summary>Anzeige der Tafel setzen und das Health-Event mit der Tafel verbinden.</summary>
        private static void Hook(object model, string eventName, string valueName, object plates, string plateMethod)
        {
            if (model == null || plates == null) return;
            try
            {
                var mi = plates.GetType().GetMethod(plateMethod, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi == null) return;
                mi.Invoke(plates, new[] { Reflect.Get<object>(model, valueName) });
                var ev = model.GetType().GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ev != null) ev.GetAddMethod(true)?.Invoke(model, new object[] { Delegate.CreateDelegate(ev.EventHandlerType, plates, mi) });
            }
            catch (Exception ex) { Main.WarningOnce("hook-" + eventName, $"Job refill: connecting {eventName} to the car plate failed: {ex.Message}"); }
        }

        // ------------------------------------------------------------------ Kandidaten

        private static int _exLoco, _exDerailed, _exMoving, _exNoStationTrack;
        /// <summary>Grund, warum die letzte Try*-Methode keinen Job gebaut hat (fuers Log).</summary>
        private static string _why = "";

        private static List<Group> CollectGroups()
        {
            _exLoco = _exDerailed = _exMoving = _exNoStationTrack = 0;
            var result = new List<Group>();
            var jm = JobsManager.Instance;
            var trackToStation = TrackStations();
            var seen = new HashSet<TrainCar>();

            foreach (var car in CarSpawner.Instance.AllCars.ToList())
            {
                if (car == null || seen.Contains(car)) continue;
                var set = car.trainset;
                if (set == null) continue;
                foreach (var c in set.cars) if (c != null) seen.Add(c);

                if (set.cars.Any(c => c == null || IsProtected(c))) { _exLoco++; continue; }
                if (set.cars.Any(c => c.derailed)) { _exDerailed++; continue; }
                if (set.cars.Any(c => Mathf.Abs(c.GetForwardSpeed()) > 0.3f)) { _exMoving++; continue; }
                // vom Spieler gespawnte Wagen ausdruecklich MIT - auch sie sollen Jobs bekommen.
                // Kein Mindestabstand zum Spieler mehr: es wird nichts gespawnt oder bewegt, nur ein Job vergeben.

                // zusammenhaengende Laeufe ohne Job, gleicher Zustand (beladen/leer), gleiches Gleis
                Group cur = null;
                foreach (var c in set.cars)
                {
                    var track = c.logicCar?.CurrentTrack;
                    bool jobless = jm.GetJobOfCar(c.logicCar) == null;
                    bool loaded = c.logicCar != null && c.logicCar.CurrentCargoTypeInCar != CargoType.None;
                    if (!jobless || track == null || !trackToStation.TryGetValue(track, out var sc))
                    {
                        if (jobless && track != null) _exNoStationTrack++;
                        cur = null;
                        continue;
                    }
                    if (cur == null || cur.Track != track || cur.Loaded != loaded ||
                        (loaded && cur.Cars[0].logicCar.CurrentCargoTypeInCar != c.logicCar.CurrentCargoTypeInCar) ||
                        (!loaded && cur.Cars[0].carLivery?.parentType != c.carLivery?.parentType))   // leer: gleicher Wagentyp
                    {
                        cur = new Group { Station = sc, Track = track, Cars = new List<TrainCar>(), Loaded = loaded };
                        result.Add(cur);
                    }
                    cur.Cars.Add(c);
                }
            }

            // zu lange Gruppen auf die maximale Wagenzahl des Bahnhofs zerlegen
            var split = new List<Group>();
            foreach (var g in result)
            {
                int max = Math.Max(1, g.Station.proceduralJobsRuleset != null ? g.Station.proceduralJobsRuleset.maxCarsPerJob : 20);
                // ausserdem nie laenger als das laengste O/I-Gleis des Bahnhofs - sonst passt der Zug nach dem
                // Beladen nirgends hin (SM-B7S: 17 Wagen = 312 m, kein O/I-Gleis so lang)
                float maxLen = LongestTransferTrack(g.Station);
                int i = 0;
                while (i < g.Cars.Count)
                {
                    var part = new List<TrainCar>();
                    while (i < g.Cars.Count && part.Count < max)
                    {
                        part.Add(g.Cars[i]);
                        if (part.Count > 1 && maxLen > 0f && Length(part) > maxLen) { part.RemoveAt(part.Count - 1); break; }
                        i++;
                    }
                    if (part.Count == 0) { part.Add(g.Cars[i]); i++; }
                    split.Add(new Group { Station = g.Station, Track = g.Track, Loaded = g.Loaded, Cars = part });
                }
            }
            return split;
        }

        private static bool IsProtected(TrainCar car)
        {
            if (car.IsLoco) return true;
            string n = car.name ?? "";
            return n.StartsWith("Loco", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("Tender", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Caboose", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Gueterverkehrs-Gleise (S/I/O) aller Bahnhoefe -> Bahnhof.</summary>
        /// <summary>Nutzbare Gleislaenge: Gleislaenge minus 40 m Weichen-/Sicherheitsabstand (wie das Spiel rechnet).</summary>
        private static float UsableLength(Track t)
        {
            object v = Reflect.Get<object>(t, "length");
            double len = v is double d ? d : v is float f ? f : 0.0;
            return (float)Math.Max(0.0, len - 40.0);
        }

        private static float LongestTransferTrack(StationController sc)
        {
            var y = sc.logicStation.yard;
            float best = 0f;
            foreach (var list in new[] { y.TransferOutTracks })   // nach dem Beladen: O
                if (list != null) foreach (var t in list) if (t != null) best = Math.Max(best, UsableLength(t));
            return best;
        }

        private static readonly HashSet<string> _dumped = new HashSet<string>();

        /// <summary>Einmal pro Durchlauf und Bahnhof: Belegung aller O/I-Gleise ins Log (Laenge, Wagen, reserviert, frei).</summary>
        private static void DumpTransferTracks(StationController sc)
        {
            string id = sc.logicStation.ID;
            if (!_dumped.Add(id)) return;
            var yto = YardTracksOrganizer.Instance;
            var y = sc.logicStation.yard;
            var carsOn = new Dictionary<Track, float>();
            foreach (var tc in CarSpawner.Instance.AllCars)
            {
                var t = tc?.logicCar?.CurrentTrack;
                if (t == null) continue;
                carsOn[t] = (carsOn.TryGetValue(t, out float v) ? v : 0f) + CarSpawner.Instance.GetTotalCarsLength(new List<Car> { tc.logicCar }, true);
            }
            var parts = new List<string>();
            foreach (var list in new[] { y.TransferOutTracks, y.TransferInTracks })
                if (list != null)
                    foreach (var t in list)
                    {
                        if (t == null) continue;
                        float usable = UsableLength(t);
                        float free = (float)yto.GetFreeSpaceOnTrack(t);
                        float cars = carsOn.TryGetValue(t, out float c) ? c : 0f;
                        float nt = _notTaken.TryGetValue(t, out float ntv) ? ntv : 0f;
                        parts.Add($"{t.ID.FullDisplayID} {usable:0}m: cars {cars:0}, reserved {Math.Max(0f, usable - cars - free):0} (by not-taken jobs {nt:0}), free {free:0}, free for shunting {free + nt:0}");
                    }
            Main.Log($"Job refill: O/I tracks at {id}: " + string.Join(" | ", parts));
        }

        /// <summary>Ladegleise (L) eines Bahnhofs - die Gleise der Lagerhaus-Maschinen.</summary>
        private static IEnumerable<Track> LoadingTracks(StationController sc)
        {
            var result = new List<Track>();
            try
            {
                if (Reflect.Get<object>(sc, "warehouseMachineControllers") is IEnumerable ctrls)
                    foreach (var c in ctrls)
                    {
                        var wm = Reflect.Get<object>(c, "warehouseMachine");
                        var t = Reflect.GetAny<Track>(wm, "WarehouseTrack", "warehouseTrack");
                        if (t != null) result.Add(t);
                    }
            }
            catch { }
            return result;
        }

        private static bool IsLoadingTrack(StationController sc, Track t) => LoadingTracks(sc).Contains(t);

        private static Dictionary<Track, StationController> TrackStations()
        {
            var map = new Dictionary<Track, StationController>();
            foreach (var sc in StationController.allStations)
            {
                var y = sc?.logicStation?.yard;
                if (y == null) continue;
                foreach (var list in new[] { y.StorageTracks, y.TransferInTracks, y.TransferOutTracks })
                    if (list != null) foreach (var t in list) if (t != null) map[t] = sc;
                foreach (var t in LoadingTracks(sc)) map[t] = sc;   // L-Gleise: dort stehengebliebene Wagen bekommen Rangierjobs
            }
            return map;
        }

        // ------------------------------------------------------------------ Entscheidungen

        private static JobChainController TryUnloadHere(Group g, out string kind)
        {
            kind = "unload here";
            var cargo = g.Cars.Select(c => c.logicCar.CurrentCargoTypeInCar).Distinct().ToList();
            _why = $"{g.Station.logicStation.ID} does not accept {string.Join("/", cargo)}";
            if (!Accepts(g.Station, cargo)) return null;
            var machines = g.Station.logicStation.yard.GetWarehouseMachinesThatSupportCargoTypes(cargo);
            _why = $"no warehouse machine at {g.Station.logicStation.ID} for {string.Join("/", cargo)}";
            if (machines == null || machines.Count == 0) return null;
            var dest = UnloadTarget(g.Station, g.Cars, Length(g.Cars), g.Track);
            _why = $"no free track at {g.Station.logicStation.ID} to park the unloaded cars ({Length(g.Cars):0} m)";
            if (dest == null) return null;

            var chain = NewChain(g.Station, g.Station, g.Cars, JobType.ShuntingUnload);
            chain.AddJobDefinitionToChain(UnloadDefinition(chain, g.Station, g.Station, g.Track, dest, machines[Rng.Next(machines.Count)], g.Cars));
            return chain;
        }

        private static JobChainController TryHaulLoaded(Group g, out string kind)
        {
            kind = "freight haul";
            var cargo = g.Cars.Select(c => c.logicCar.CurrentCargoTypeInCar).Distinct().ToList();
            var targets = StationController.allStations
                .Where(s => s != null && s != g.Station && Accepts(s, cargo)
                            && (s.logicStation.yard.GetWarehouseMachinesThatSupportCargoTypes(cargo)?.Count ?? 0) > 0)
                .OrderBy(_ => Rng.Next()).ToList();
            targets = PreferFirst(targets).ToList();
            float len = Length(g.Cars);
            foreach (var dst in targets)
            {
                var y = dst.logicStation.yard;
                var destTrack = FreeTrack(y.TransferInTracks, len);   // Ankunft: immer I
                if (destTrack == null) continue;

                var chain = NewChain(g.Station, dst, g.Cars, JobType.Transport);
                chain.AddJobDefinitionToChain(TransportDefinition(chain, g.Station, dst, g.Track, destTrack, g.Cars));
                var machines = y.GetWarehouseMachinesThatSupportCargoTypes(cargo);
                var unloadTrack = UnloadTarget(dst, g.Cars, len, destTrack);
                if (machines.Count > 0 && unloadTrack != null)
                {
                    chain.AddJobDefinitionToChain(UnloadDefinition(chain, g.Station, dst, destTrack, unloadTrack, machines[Rng.Next(machines.Count)], g.Cars));
                    kind = "freight haul + unload";
                }
                return chain;
            }
            _why = $"no station that accepts {string.Join("/", cargo)} has a free I track for {len:0} m";
            return null;
        }

        private static JobChainController TryLoadHere(Group g, out string kind)
        {
            kind = "load + freight haul";
            var rs = g.Station.proceduralJobsRuleset;
            _why = $"{g.Station.logicStation.ID} has no cargo that can be loaded into {g.Cars[0].carLivery?.parentType?.name ?? "these cars"}";
            if (rs?.outputCargoGroups == null) return null;
            float len = Length(g.Cars);
            var yard = g.Station.logicStation.yard;
            bool cargoFits = false, machineFound = false, outFound = false;

            foreach (var cg in rs.outputCargoGroups.OrderBy(_ => Rng.Next()))
            {
                var cargoType = cg.cargoTypes?.Where(ct => ct != CargoType.None && g.Cars.All(c => ct.ToV2().IsLoadableOnCarType(c.carLivery.parentType)))
                                              .Select(ct => (CargoType?)ct).FirstOrDefault();
                if (cargoType == null) continue;
                cargoFits = true;
                var ctList = new List<CargoType> { cargoType.Value };
                var machines = yard.GetWarehouseMachinesThatSupportCargoTypes(ctList);
                if (machines == null || machines.Count == 0) continue;
                machineFound = true;
                var outTrack = ShuntTrack(yard.TransferOutTracks, len);   // Abfahrt: immer O
                if (outTrack == null) continue;
                outFound = true;

                foreach (var dst in (cg.stations ?? new List<StationController>()).Where(s => s != null && s != g.Station).OrderBy(_ => Rng.Next()))
                {
                    var dy = dst.logicStation.yard;
                    var destTrack = FreeTrack(dy.TransferInTracks, len);   // Ankunft: immer I
                    if (destTrack == null) continue;

                    var cargoPerCar = g.Cars.Select(_ => cargoType.Value).ToList();
                    var chain = NewChain(g.Station, dst, g.Cars, JobType.ShuntingLoad);
                    chain.AddJobDefinitionToChain(LoadDefinition(chain, g.Station, dst, g.Track, outTrack, machines[Rng.Next(machines.Count)], g.Cars, cargoPerCar));
                    chain.AddJobDefinitionToChain(TransportDefinition(chain, g.Station, dst, outTrack, destTrack, g.Cars, cargoPerCar));
                    var um = dy.GetWarehouseMachinesThatSupportCargoTypes(ctList);
                    var unloadTrack = UnloadTarget(dst, g.Cars, len, destTrack);
                    if (um.Count > 0 && unloadTrack != null)
                    {
                        chain.AddJobDefinitionToChain(UnloadDefinition(chain, g.Station, dst, destTrack, unloadTrack, um[Rng.Next(um.Count)], g.Cars, cargoPerCar));
                        kind = "load + freight haul + unload";
                    }
                    return chain;
                }
            }
            if (cargoFits && !machineFound) _why = $"no warehouse machine at {g.Station.logicStation.ID} for the loadable cargo";
            else if (machineFound && !outFound) { _why = $"no free O track at {g.Station.logicStation.ID} for {len:0} m (after loading)"; DumpTransferTracks(g.Station); }
            else if (outFound) _why = $"no target station with a free I track for {len:0} m";
            return null;
        }

        private static JobChainController TryEmptyHaul(Group g, out string kind)
        {
            kind = "logistical haul";
            float len = Length(g.Cars);
            var targets = StationController.allStations
                .Where(s => s != null && s != g.Station && CanLoad(s, g.Cars))
                .OrderBy(_ => Rng.Next()).ToList();
            targets = PreferFirst(targets).ToList();
            foreach (var dst in targets)
            {
                var y = dst.logicStation.yard;
                var destTrack = FreeTrack(y.TransferInTracks, len);  // LH-Ankunft: immer I
                if (destTrack == null) continue;
                var chain = NewChain(g.Station, dst, g.Cars, JobType.EmptyHaul);
                chain.AddJobDefinitionToChain(EmptyHaulDefinition(chain, g.Station, dst, g.Track, destTrack, g.Cars));
                // gleich weiter: am Ziel beladen (SL, Ende auf O) -> Gueterzug (FH, Ankunft I) -> Entladen (SU)
                string more = AppendLoadAndHaul(chain, dst, destTrack, g.Cars, len);
                if (more != null) kind += more;
                return chain;
            }
            _why = $"no station that can load these empty cars has a free I track for {len:0} m";
            return null;
        }

        /// <summary>
        /// Haengt an einen Leerzug die Folgejobs am Ziel an: SL (Start = Ankunftsgleis I, Ende O) -> FH -> SU.
        /// Gibt die Ergaenzung fuer das Log zurueck, null = nichts angehaengt (dann bleibt es beim LH).
        /// </summary>
        private static string AppendLoadAndHaul(JobChainController chain, StationController st, Track arrival, List<TrainCar> cars, float len)
        {
            try
            {
                var rs = st.proceduralJobsRuleset;
                if (rs?.outputCargoGroups == null) return null;
                var yard = st.logicStation.yard;
                foreach (var cg in rs.outputCargoGroups.OrderBy(_ => Rng.Next()))
                {
                    var cargoType = cg.cargoTypes?.Where(ct => ct != CargoType.None && cars.All(c => ct.ToV2().IsLoadableOnCarType(c.carLivery.parentType)))
                                                  .Select(ct => (CargoType?)ct).FirstOrDefault();
                    if (cargoType == null) continue;
                    var ctList = new List<CargoType> { cargoType.Value };
                    var machines = yard.GetWarehouseMachinesThatSupportCargoTypes(ctList);
                    if (machines == null || machines.Count == 0) continue;
                    var outTrack = FreeTrack(yard.TransferOutTracks?.Where(t => t != arrival).ToList(), len);   // nach dem Beladen: O
                    if (outTrack == null) continue;

                    foreach (var dst in (cg.stations ?? new List<StationController>()).Where(s => s != null && s != st).OrderBy(_ => Rng.Next()))
                    {
                        var dy = dst.logicStation.yard;
                        var destTrack = FreeTrack(dy.TransferInTracks, len);   // Ankunft: immer I
                        if (destTrack == null) continue;

                        var cargoPerCar = cars.Select(_ => cargoType.Value).ToList();
                        chain.AddJobDefinitionToChain(LoadDefinition(chain, st, dst, arrival, outTrack, machines[Rng.Next(machines.Count)], cars, cargoPerCar));
                        chain.AddJobDefinitionToChain(TransportDefinition(chain, st, dst, outTrack, destTrack, cars, cargoPerCar));
                        var um = dy.GetWarehouseMachinesThatSupportCargoTypes(ctList);
                        var unloadTrack = UnloadTarget(dst, cars, len, destTrack);
                        if (um != null && um.Count > 0 && unloadTrack != null)
                        {
                            chain.AddJobDefinitionToChain(UnloadDefinition(chain, st, dst, destTrack, unloadTrack, um[Rng.Next(um.Count)], cars, cargoPerCar));
                            return " + load + freight haul + unload";
                        }
                        return " + load + freight haul";
                    }
                }
            }
            catch (Exception ex) { Main.WarningOnce("lh-append", "Job refill: follow-up jobs after the empty haul could not be added: " + ex.Message); }
            return null;
        }

        // ------------------------------------------------------------------ Hilfen: Bahnhofsdaten

        private static bool Accepts(StationController s, List<CargoType> cargo)
        {
            var groups = s?.proceduralJobsRuleset?.inputCargoGroups;
            return groups != null && cargo.All(ct => groups.Any(gr => gr.cargoTypes != null && gr.cargoTypes.Contains(ct)));
        }

        private static bool CanLoad(StationController s, List<TrainCar> cars)
        {
            var groups = s?.proceduralJobsRuleset?.outputCargoGroups;
            if (groups == null) return false;
            foreach (var gr in groups)
                foreach (var ct in gr.cargoTypes ?? new List<CargoType>())
                    if (cars.All(c => ct.ToV2().IsLoadableOnCarType(c.carLivery.parentType))
                        && (s.logicStation.yard.GetWarehouseMachinesThatSupportCargoTypes(new List<CargoType> { ct })?.Count ?? 0) > 0)
                        return true;
            return false;
        }

        /// <summary>
        /// Ziel nach dem Entladen: Kann der Bahnhof die leeren Wagen wieder beladen, bleiben sie auf S (spaeter Beladen).
        /// Sonst folgt spaeter ein Leerzug - der darf nicht von S starten, also nach O (Ausweichen I).
        /// </summary>
        private static Track UnloadTarget(StationController station, List<TrainCar> cars, float len, Track exclude)
        {
            var y = station.logicStation.yard;
            // nie das Gleis, das dieselbe Kette schon als Ankunfts-/Startgleis nutzt: sonst wird es doppelt
            // reserviert ("Not enough free space for reservation! Something is not right.")
            List<Track> Without(List<Track> l) => l?.Where(t => t != exclude).ToList();
            if (CanLoad(station, cars)) return ShuntTrack(Without(y.StorageTracks), len);
            return ShuntTrack(Without(y.TransferOutTracks), len);   // verlaesst spaeter den Bahnhof (Leerzug): immer O
        }

        private static float Length(List<TrainCar> cars) => CarSpawner.Instance.GetTotalTrainCarsLength(TrainCar.ExtractLogicCars(cars), true);

        /// <summary>Kleinstes Gleis, auf das der Zug noch passt.</summary>
        /// <summary>Pro Durchlauf: von noch nicht angenommenen Jobs reservierte Meter je Gleis.</summary>
        private static Dictionary<Track, float> _notTaken = new Dictionary<Track, float>();
        /// <summary>Pro Durchlauf: von Rangierjobs dieses Durchlaufs schon belegte Meter (kein Gleis doppelt vergeben).</summary>
        private static readonly Dictionary<Track, float> _shuntAssigned = new Dictionary<Track, float>();

        /// <summary>
        /// Platz fuer Rangierjobs (Beladen/Entladen): Rangierjobs haben Vorrang. Reservierungen von Jobs, die noch
        /// niemand angenommen hat, zaehlen fuer sie als frei - nur Reservierungen angenommener Jobs und echte
        /// Wagen sperren das Gleis.
        /// </summary>
        private static float ShuntFree(Track t)
        {
            float free = (float)YardTracksOrganizer.Instance.GetFreeSpaceOnTrack(t);
            if (_notTaken.TryGetValue(t, out float nt)) free += nt;
            if (_shuntAssigned.TryGetValue(t, out float a)) free -= a;
            return free;
        }

        private static Track ShuntTrack(List<Track> tracks, float length)
        {
            var t = tracks?.Where(x => x != null && ShuntFree(x) >= length).OrderBy(ShuntFree).FirstOrDefault();
            if (t != null) _shuntAssigned[t] = (_shuntAssigned.TryGetValue(t, out float a) ? a : 0f) + length;
            return t;
        }

        private static Track FreeTrack(List<Track> tracks, float length)
        {
            var yto = YardTracksOrganizer.Instance;
            return tracks?.Where(t => t != null && yto.GetFreeSpaceOnTrack(t) >= length)
                          .OrderBy(t => yto.GetFreeSpaceOnTrack(t)).FirstOrDefault();
        }

        // ------------------------------------------------------------------ Hilfen: Definitionen

        private static JobChainController NewChain(StationController from, StationController to, List<TrainCar> cars, JobType first)
        {
            var go = new GameObject($"ChainJob[{first}]: {from.logicStation.ID} - {to.logicStation.ID} (refill)");
            go.transform.SetParent(from.transform);
            var chain = new JobChainController(go) { carsForJobChain = TrainCar.ExtractLogicCars(cars) };
            return chain;
        }

        /// <summary>Echte Lizenz-Anforderungen des Jobs (Jobart + Wagentypen + Fracht + Zuglaenge) - annehmen nur mit passenden Lizenzen.</summary>
        private static JobLicenses Licenses(JobType type, List<CargoType> cargo, List<TrainCar> cars)
        {
            var lm = LicenseManager.Instance;
            var list = lm.GetRequiredLicensesForJobType(type).ToList();
            list.AddRange(lm.GetRequiredLicensesForCarTypes(cars.Select(c => c.carLivery.parentType).Distinct().ToList()));
            list.AddRange(lm.GetRequiredLicensesForCargoTypes(cargo));
            var lengthLicense = lm.GetRequiredLicenseForNumberOfTransportedCars(cars.Count);
            if (lengthLicense != null) list.Add(lengthLicense);
            return JobLicenseType_v2.ListToFlags(list);
        }

        private static PaymentCalculationData PaymentData(List<TrainCar> cars, List<CargoType> cargo)
        {
            var liveries = new Dictionary<TrainCarLivery, int>();
            foreach (var c in cars) liveries[c.carLivery] = liveries.TryGetValue(c.carLivery, out int n) ? n + 1 : 1;
            var cargoes = new Dictionary<CargoType, int>();
            foreach (var ct in cargo) cargoes[ct] = cargoes.TryGetValue(ct, out int n) ? n + 1 : 1;
            return new PaymentCalculationData(liveries, cargoes);
        }

        private static StaticTransportJobDefinition TransportDefinition(JobChainController chain, StationController from, StationController to,
            Track start, Track dest, List<TrainCar> cars, List<CargoType> cargoPerCar = null)
        {
            cargoPerCar ??= cars.Select(c => c.logicCar.CurrentCargoTypeInCar).ToList();
            float dist = JobPaymentCalculator.GetDistanceBetweenStations(from, to);
            float bonus = JobPaymentCalculator.CalculateHaulBonusTimeLimit(dist);
            float wage = JobPaymentCalculator.CalculateJobPayment(JobType.Transport, dist, PaymentData(cars, cargoPerCar));

            var def = chain.jobChainGO.AddComponent<StaticTransportJobDefinition>();
            def.PopulateBaseJobDefinition(from.logicStation, bonus, wage, new StationsChainData(from.stationInfo.YardID, to.stationInfo.YardID),
                                          Licenses(JobType.Transport, cargoPerCar.Distinct().ToList(), cars));
            def.startingTrack = start;
            def.destinationTrack = dest;
            def.carsToTransport = TrainCar.ExtractLogicCars(cars);
            def.transportedCargoPerCar = cargoPerCar;
            def.cargoAmountPerCar = cars.Select(_ => 1f).ToList();
            def.forceCorrectCargoStateOnCars = false;
            return def;
        }

        /// <summary>Leerzug (LH) - eigene Definition (die Vanilla-Helfer im EmptyHaulJobProceduralGenerator sind privat).</summary>
        private static StaticEmptyHaulJobDefinition EmptyHaulDefinition(JobChainController chain, StationController from, StationController to,
            Track start, Track dest, List<TrainCar> cars)
        {
            float dist = JobPaymentCalculator.GetDistanceBetweenStations(from, to);
            float bonus = JobPaymentCalculator.CalculateHaulBonusTimeLimit(dist);
            float wage = JobPaymentCalculator.CalculateJobPayment(JobType.EmptyHaul, dist, PaymentData(cars, new List<CargoType>()));

            var def = chain.jobChainGO.AddComponent<StaticEmptyHaulJobDefinition>();
            def.PopulateBaseJobDefinition(from.logicStation, bonus, wage, new StationsChainData(from.stationInfo.YardID, to.stationInfo.YardID),
                                          Licenses(JobType.EmptyHaul, new List<CargoType>(), cars));
            def.startingTrack = start;
            def.destinationTrack = dest;
            def.carsToTransport = TrainCar.ExtractLogicCars(cars);
            return def;
        }

        private static StaticShuntingUnloadJobDefinition UnloadDefinition(JobChainController chain, StationController from, StationController to,
            Track start, Track dest, WarehouseMachine machine, List<TrainCar> cars, List<CargoType> cargoPerCar = null)
        {
            cargoPerCar ??= cars.Select(c => c.logicCar.CurrentCargoTypeInCar).ToList();
            float bonus = JobPaymentCalculator.CalculateShuntingBonusTimeLimit(1);
            float wage = JobPaymentCalculator.CalculateJobPayment(JobType.ShuntingUnload, 500f, PaymentData(cars, cargoPerCar));

            var def = chain.jobChainGO.AddComponent<StaticShuntingUnloadJobDefinition>();
            def.PopulateBaseJobDefinition(to.logicStation, bonus, wage, new StationsChainData(from.stationInfo.YardID, to.stationInfo.YardID),
                                          Licenses(JobType.ShuntingUnload, cargoPerCar.Distinct().ToList(), cars));
            def.startingTrack = start;
            def.carsPerDestinationTrack = new List<CarsPerTrack> { new CarsPerTrack(dest, TrainCar.ExtractLogicCars(cars)) };
            def.unloadData = CargoData(cars, cargoPerCar);
            def.unloadMachine = machine;
            def.forceCorrectCargoStateOnCars = false;
            return def;
        }

        private static StaticShuntingLoadJobDefinition LoadDefinition(JobChainController chain, StationController from, StationController to,
            Track start, Track dest, WarehouseMachine machine, List<TrainCar> cars, List<CargoType> cargoPerCar, List<CarsPerTrack> pickups = null)
        {
            int n = pickups != null && pickups.Count > 0 ? pickups.Count : 1;
            float bonus = JobPaymentCalculator.CalculateShuntingBonusTimeLimit(n);
            float wage = JobPaymentCalculator.CalculateJobPayment(JobType.ShuntingLoad, 500f, PaymentData(cars, cargoPerCar)) * (1f + 0.15f * (n - 1));

            var def = chain.jobChainGO.AddComponent<StaticShuntingLoadJobDefinition>();
            def.PopulateBaseJobDefinition(from.logicStation, bonus, wage, new StationsChainData(from.stationInfo.YardID, to.stationInfo.YardID),
                                          Licenses(JobType.ShuntingLoad, cargoPerCar.Distinct().ToList(), cars));
            def.carsPerStartingTrack = pickups != null && pickups.Count > 0
                ? pickups
                : new List<CarsPerTrack> { new CarsPerTrack(start, TrainCar.ExtractLogicCars(cars)) };
            def.destinationTrack = dest;            // nach dem Beladen auf O (sonst I) bereitstellen - nicht im Ladegleis
            def.loadData = CargoData(cars, cargoPerCar);
            def.loadMachine = machine;
            def.forceCorrectCargoStateOnCars = false;
            return def;
        }

        private static List<CarsPerCargoType> CargoData(List<TrainCar> cars, List<CargoType> cargoPerCar)
        {
            var result = new List<CarsPerCargoType>();
            foreach (var grp in cars.Select((c, i) => (c, ct: cargoPerCar[i])).GroupBy(x => x.ct))
                result.Add(new CarsPerCargoType(grp.Key, grp.Select(x => x.c.logicCar).ToList(), grp.Count()));
            return result;
        }
    }
}
