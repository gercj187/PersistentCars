using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Booklets;
using DV.Localization;
using DV.Logic.Job;
using DV.RenderTextureSystem.BookletRender;
using DV.ThingTypes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PersistentCars
{
    // =====================================================================================================
    // SP = SHUNTING PREPARE / BEREITSTELLUNG
    //
    // Wagen auf S-Gleisen, die der Bahnhof weder beladen noch entladen kann, duerfen nicht von S abfahren.
    // Sie bekommen einen einfachen Rangierauftrag: alle betroffenen Wagen (mehrere Abholungen auf verschiedenen
    // S-Gleisen) einsammeln und gemeinsam auf einem O-Gleis bereitstellen. Bei der Abgabe entsteht sofort die
    // Folgekette ab dem O-Gleis: LH -> SL -> FH -> SU (bzw. FH -> SU fuer beladene Wagen).
    //
    //   Sequential[ Parallel(last)[ Transport Abholung i: S-Gleis i -> O-Gleis ] ]
    //
    // * Nur Vanilla-Tasks, Jobtyp ShuntingUnload (wie die Rangierjobs von TrainServiceRework) - der Multiplayer
    //   synchronisiert den Job wie jeden anderen, nichts im Spiel stolpert ueber einen unbekannten Jobtyp.
    //   Erkannt wird der Job an seiner ID (XX-SP-00).
    // * Booklet: Deckblatt, Uebersicht, je Abholung eine Ankuppeln-Seite, Bereitstellen, Entwerten.
    //   Gebaut aus den Taskdaten des Jobs -> auf Host und Clients identisch.
    // * Speichern: eigener Schluessel im Spielstand. Wird die Mod entfernt, bleibt der Vanilla-Teil lesbar.
    // =====================================================================================================

    public class PrepareJobDefinition : StaticJobDefinition
    {
        internal static readonly HashSet<PrepareJobDefinition> All = new HashSet<PrepareJobDefinition>();

        public JobChainController Chain;
        /// <summary>Abholungen: Wagen je S-Gleis.</summary>
        public List<CarsPerTrack> Pickups = new List<CarsPerTrack>();
        /// <summary>O-Gleis, auf dem der Zug bereitgestellt wird.</summary>
        public Track EndTrack;
        /// <summary>Geplantes Ziel der Folgefahrt (Yard-ID), leer = beliebig.</summary>
        public string TargetYardId = string.Empty;

        public List<Car> AllCars => Pickups.Where(p => p?.cars != null).SelectMany(p => p.cars).ToList();

        public void Setup(JobChainController chain, List<CarsPerTrack> pickups, Track endTrack, string targetYardId)
        {
            Chain = chain;
            Pickups = pickups ?? new List<CarsPerTrack>();
            EndTrack = endTrack;
            TargetYardId = targetYardId ?? string.Empty;
        }

        private void Awake() => All.Add(this);
        private void OnDestroy() => All.Remove(this);

        protected override void GenerateJob(Station jobOriginStation, float timeLimit, float initialWage, string forcedJobId, JobLicenses requiredLicenses)
        {
            if (Pickups.Count == 0 || EndTrack == null || Pickups.Any(p => p?.track == null || p.cars == null || p.cars.Count == 0))
            {
                Main.Warning("Shunting prepare: incomplete definition, job not created.");
                return;
            }

            var end = EndTrack;
            var tasks = Pickups.Select(p => (Task)JobsGenerator.CreateTransportTask(p.cars, end, p.track, null, true)).ToList();
            var all = new ParallelTasks(tasks, 0L, true);
            var sequence = new SequentialTasks(new List<Task> { all }, 0L);

            string id = string.IsNullOrEmpty(forcedJobId) ? PrepareIds.NewId(chainData?.chainOriginYardId) : forcedJobId;
            job = new Job(sequence, JobType.ShuntingUnload, timeLimit, initialWage, chainData, id, requiredLicenses);
            PrepareIds.EnsureRegistered(job.ID);
            jobOriginStation.AddJobToStation(job);
        }

        public override List<TrackReservation> GetRequiredTrackReservations()
        {
            var result = new List<TrackReservation>();
            var cars = AllCars;
            if (EndTrack != null && cars.Count > 0)
                result.Add(new TrackReservation(EndTrack, CarSpawner.Instance.GetTotalCarsLength(cars, true)));
            return result;
        }

        public override JobDefinitionDataBase GetJobDefinitionSaveData()
        {
            return new PrepareJobDefinitionData(
                timeLimitForJob, initialWage, logicStation.ID, chainData.chainOriginYardId, chainData.chainDestinationYardId,
                (int)requiredLicenses,
                GetGuidsFromCars(AllCars),
                EndTrack != null ? EndTrack.ID.FullID : string.Empty,
                Pickups.Select(p => p?.track != null ? p.track.ID.FullID : string.Empty).ToArray(),
                Pickups.Select(p => p?.cars?.Count ?? 0).ToArray(),
                TargetYardId ?? string.Empty);
        }
    }

    public class PrepareJobDefinitionData : JobDefinitionDataBase
    {
        public string[] allCarGuids;
        public string endTrackId;
        public string[] pickupTrackIds;
        public int[] pickupCounts;
        public string targetYardId;

        [JsonConstructor]
        public PrepareJobDefinitionData(float timeLimitForJob, float initialWage, string stationId, string originStationId,
            string destinationStationId, int requiredLicenses, string[] allCarGuids, string endTrackId,
            string[] pickupTrackIds, int[] pickupCounts, string targetYardId)
            : base(timeLimitForJob, initialWage, stationId, originStationId, destinationStationId, requiredLicenses)
        {
            this.allCarGuids = allCarGuids ?? new string[0];
            this.endTrackId = endTrackId ?? string.Empty;
            this.pickupTrackIds = pickupTrackIds ?? new string[0];
            this.pickupCounts = pickupCounts ?? new int[0];
            this.targetYardId = targetYardId ?? string.Empty;
        }
    }

    public class PrepareChainSaveData : JobChainSaveData
    {
        [JsonConstructor]
        public PrepareChainSaveData(JobDefinitionDataBase[] jobChainData, string[] trainCarGuids, bool jobTaken,
            TaskSaveData[] currentJobTaskData, string firstJobId)
            : base(jobChainData, trainCarGuids, jobTaken, currentJobTaskData, firstJobId) { }

        public PrepareChainSaveData(JobChainSaveData source)
            : base(source.jobChainData, source.trainCarGuids, source.jobTaken, source.currentJobTaskData, source.firstJobId) { }
    }

    // =====================================================================================================
    // Job-IDs "SM-SP-42"
    // =====================================================================================================
    internal static class PrepareIds
    {
        public const string Code = "SP";
        private static readonly System.Random Rng = new System.Random();

        public static bool IsPrepareId(string jobId) =>
            !string.IsNullOrEmpty(jobId) && (jobId.Contains("-" + Code + "-") || jobId.StartsWith(Code + "-", StringComparison.Ordinal));

        private static object Generator()
        {
            try
            {
                var t = typeof(IdGenerator);
                for (var c = t; c != null; c = c.BaseType)
                {
                    var p = c.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (p != null) return p.GetValue(null, null);
                }
            }
            catch { }
            return null;
        }

        private static HashSet<string> Existing() => Reflect.Get<HashSet<string>>(Generator(), "existingJobIds");

        public static string NewId(string yardId)
        {
            var existing = Existing();
            string prefix = string.IsNullOrEmpty(yardId) ? Code + "-" : $"{yardId}-{Code}-";
            int start = Rng.Next(0, 100);
            for (int i = 0; i < 100; i++)
            {
                string id = prefix + ((start + i) % 100).ToString("D2");
                if (existing == null || !existing.Contains(id)) return id;
            }
            return prefix + start.ToString("D2");
        }

        public static void EnsureRegistered(string id)
        {
            try
            {
                var gen = Generator();
                var existing = Existing();
                if (gen == null || existing == null || existing.Contains(id)) return;
                var register = gen.GetType().GetMethod("RegisterJobId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (register != null && register.GetParameters().Length == 1) register.Invoke(gen, new object[] { id });
                else existing.Add(id);
            }
            catch { }
        }
    }

    // =====================================================================================================
    // Erzeugen und Folgekette
    // =====================================================================================================
    internal static class ShuntingPrepare
    {
        /// <summary>Rotes Band wie bei SL/SU - ein ganz normaler Rangierjob.</summary>
        public static Color JobColor => C.SHUNTING_UNLOAD_JOB_TYPE_COLOR;

        private sealed class Pending
        {
            public PrepareJobDefinition Def;
            public List<Car> Cars;
            public Station Station;
            public string TargetYardId;
            public float Due;
        }

        private static readonly List<Pending> FollowUps = new List<Pending>();

        /// <summary>Baut die SP-Kette (noch nicht finalisiert).</summary>
        public static JobChainController Build(StationController station, List<CarsPerTrack> pickups, Track endTrack, string targetYardId, List<TrainCar> cars)
        {
            var go = new GameObject($"ChainJob[SP]: {station.logicStation.ID} (refill)");
            go.transform.SetParent(station.transform);
            var logicCars = TrainCar.ExtractLogicCars(cars);
            var chain = new JobChainController(go) { carsForJobChain = logicCars };

            var cargo = cars.Select(c => c.logicCar.CurrentCargoTypeInCar).Where(c => c != CargoType.None).Distinct().ToList();
            float bonus = JobPaymentCalculator.CalculateShuntingBonusTimeLimit(pickups.Count);
            var payData = PaymentData(cars);
            float wage = JobPaymentCalculator.CalculateJobPayment(JobType.ShuntingUnload, 500f, payData) * (1f + 0.15f * Math.Max(0, pickups.Count - 1));

            var def = go.AddComponent<PrepareJobDefinition>();
            // Ziel der Folgefahrt als Kettenziel: so zeigt die Uebersicht (auch bei Clients) "Bahnhof -> Ziel"
            var target = string.IsNullOrEmpty(targetYardId) ? null : StationController.GetStationByYardID(targetYardId);
            string destYard = target != null ? target.stationInfo.YardID : station.stationInfo.YardID;
            def.PopulateBaseJobDefinition(station.logicStation, bonus, wage,
                new StationsChainData(station.stationInfo.YardID, destYard), Licenses(cargo, cars));
            def.Setup(chain, pickups, endTrack, targetYardId);
            chain.AddJobDefinitionToChain(def);
            return chain;
        }

        private static PaymentCalculationData PaymentData(List<TrainCar> cars)
        {
            var liveries = new Dictionary<TrainCarLivery, int>();
            foreach (var c in cars) liveries[c.carLivery] = liveries.TryGetValue(c.carLivery, out int n) ? n + 1 : 1;
            var cargoes = new Dictionary<CargoType, int>();
            foreach (var c in cars)
            {
                var ct = c.logicCar.CurrentCargoTypeInCar;
                if (ct == CargoType.None) continue;
                cargoes[ct] = cargoes.TryGetValue(ct, out int n) ? n + 1 : 1;
            }
            return new PaymentCalculationData(liveries, cargoes);
        }

        /// <summary>Rangierlizenz + Wagentypen + Fracht + Zuglaenge - annehmen nur mit passenden Lizenzen.</summary>
        private static JobLicenses Licenses(List<CargoType> cargo, List<TrainCar> cars)
        {
            var lm = LicenseManager.Instance;
            var list = lm.GetRequiredLicensesForJobType(JobType.ShuntingUnload).ToList();
            list.AddRange(lm.GetRequiredLicensesForCarTypes(cars.Select(c => c.carLivery.parentType).Distinct().ToList()));
            if (cargo.Count > 0) list.AddRange(lm.GetRequiredLicensesForCargoTypes(cargo));
            var lengthLicense = lm.GetRequiredLicenseForNumberOfTransportedCars(cars.Count);
            if (lengthLicense != null) list.Add(lengthLicense);
            return JobLicenseType_v2.ListToFlags(list);
        }

        // ------------------------------------------------------------------ Abgabe -> Folgekette

        /// <summary>Postfix auf JobChainController.OnLastJobInChainCompleted.</summary>
        public static void OnLastJobInChainCompleted_Postfix(JobChainController __instance)
        {
            try
            {
                if (!Main.IsAuthority || __instance?.jobChainGO == null) return;
                var def = __instance.jobChainGO.GetComponent<PrepareJobDefinition>();
                if (def == null) return;
                // kurz warten: Job muss aus dem JobsManager raus sein, Wagen gelten erst dann als jobfrei
                FollowUps.Add(new Pending
                {
                    Def = def,
                    Cars = def.AllCars,
                    Station = def.logicStation,
                    TargetYardId = def.TargetYardId,
                    Due = Time.realtimeSinceStartup + 1.5f
                });
            }
            catch (Exception ex) { Main.WarningOnce("sp-complete", "Shunting prepare: follow-up could not be queued: " + ex.Message); }
        }

        /// <summary>Jede Frame aus Main.OnUpdate.</summary>
        public static void Tick()
        {
            if (FollowUps.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            for (int i = FollowUps.Count - 1; i >= 0; i--)
            {
                var p = FollowUps[i];
                if (now < p.Due) continue;
                FollowUps.RemoveAt(i);
                try { JobRefill.CreateFollowUp(p.Cars, p.Station?.ID, p.TargetYardId); }
                catch (Exception ex) { Main.Warning("Shunting prepare: follow-up job failed: " + ex.Message); }
            }
        }

        public static void Reset() => FollowUps.Clear();
    }

    // =====================================================================================================
    // Speichern / Laden (eigener Schluessel)
    // =====================================================================================================
    internal static class PrepareSave
    {
        private const string SaveKey = "PersistentCars_ShuntingPrepare";
        private const string LegacySaveKey = "PersistentJobsRework_ShuntingPrepare";
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static JsonSerializer Serializer() => JsonSerializer.Create(JobSaveManager.serializeSettings);

        private static MethodInfo M(string name) => typeof(JobSaveManager).GetMethods(AnyInstance).FirstOrDefault(m => m.Name == name);

        public static bool IsPrepareChain(JobChainController chain) =>
            chain?.jobChainGO != null && chain.jobChainGO.GetComponent<PrepareJobDefinition>() != null;

        /// <summary>Postfix auf JobChainController.GetJobChainSaveData: eigener Typ -> eigener Schluessel.</summary>
        public static void GetJobChainSaveData_Postfix(JobChainController __instance, ref JobChainSaveData __result)
        {
            if (__result != null && !(__result is PrepareChainSaveData) && IsPrepareChain(__instance))
                __result = new PrepareChainSaveData(__result);
        }

        /// <summary>Postfix auf JobSaveManager.GetJobsSaveGameData: unsere Ketten NICHT im Vanilla-Teil.</summary>
        public static void GetJobsSaveGameData_Postfix(ref JobsSaveGameData __result)
        {
            if (__result?.jobChains == null) return;
            __result.jobChains = __result.jobChains.Where(c => !(c is PrepareChainSaveData)).ToArray();
        }

        /// <summary>Postfix auf SaveGameManager.UpdateInternalData.</summary>
        public static void UpdateInternalData_Postfix(SaveGameManager __instance)
        {
            if (__instance?.data == null || !Main.IsAuthority) return;
            try
            {
                var chains = new List<JobChainSaveData>();
                foreach (var def in PrepareJobDefinition.All.ToList())
                {
                    var chain = def != null ? def.Chain : null;
                    var job = chain?.currentJobInChain;
                    if (job == null || (job.State != JobState.Available && job.State != JobState.InProgress)) continue;
                    var saved = chain.GetJobChainSaveData();
                    if (saved != null) chains.Add(saved is PrepareChainSaveData ? saved : new PrepareChainSaveData(saved));
                }
                var root = new JObject
                {
                    ["version"] = 1,
                    ["jobs"] = JToken.FromObject(new JobsSaveGameData(chains.ToArray(), 0f), Serializer())
                };
                __instance.data.SetJObject(SaveKey, root);
            }
            catch (Exception ex) { Main.Warning("Shunting prepare: saving failed: " + ex.Message); }
        }

        /// <summary>Prefix auf JobSaveManager.LoadJobSaveGameData: unsere Ketten in die Vanilla-Liste mischen.</summary>
        public static void LoadJobSaveGameData_Prefix(JobsSaveGameData saveData)
        {
            try
            {
                var manager = SaveGameManager.Instance;
                var root = manager != null && manager.data != null ? manager.data.GetJObject(SaveKey) : null;
                if (root == null && manager?.data != null) root = manager.data.GetJObject(LegacySaveKey);
                var token = root?["jobs"];
                if (token == null || saveData == null) return;
                var own = token.ToObject<JobsSaveGameData>(Serializer());
                if (own?.jobChains == null || own.jobChains.Length == 0) return;

                var main = saveData.jobChains ?? new JobChainSaveData[0];
                var known = new HashSet<string>(main.Where(c => c?.firstJobId != null).Select(c => c.firstJobId));
                var add = own.jobChains.Where(c => c is PrepareChainSaveData && c.firstJobId != null && known.Add(c.firstJobId)).ToList();
                saveData.jobChains = main.Concat(add).ToArray();
                Main.Log($"Shunting prepare: {add.Count} job(s) found in the savegame.");
            }
            catch (Exception ex) { Main.Warning("Shunting prepare: reading the savegame failed: " + ex.Message); }
        }

        /// <summary>Prefix auf JobSaveManager.LoadJobChain.</summary>
        public static bool LoadJobChain_Prefix(JobChainSaveData chainSaveData, List<JobBooklet> jobBooklets, ref GameObject __result)
        {
            if (!(chainSaveData is PrepareChainSaveData own)) return true;
            try { __result = LoadChain(own, jobBooklets); }
            catch (Exception ex)
            {
                Main.Warning("Shunting prepare: loading failed: " + ex.Message);
                __result = null;
            }
            return false;
        }

        private static GameObject LoadChain(PrepareChainSaveData save, List<JobBooklet> booklets)
        {
            var manager = JobSaveManager.Instance;
            var data = save.jobChainData?.OfType<PrepareJobDefinitionData>().FirstOrDefault();
            var getCars = M("GetCarsFromCarGuids");
            var getTrack = M("GetYardTrackWithId");
            var getStation = M("GetStationWithId");
            var initBooklet = M("InitializeCorrespondingJobBooklet");
            if (manager == null || data == null || getCars == null || getTrack == null || getStation == null)
            {
                Main.Warning($"Shunting prepare: {save.firstJobId} can't be restored.");
                return null;
            }

            var allCars = getCars.Invoke(manager, new object[] { data.allCarGuids }) as List<Car>;
            var station = getStation.Invoke(manager, new object[] { data.stationId }) as Station;
            Track Find(string id)
            {
                if (string.IsNullOrEmpty(id)) return null;
                try { return getTrack.Invoke(manager, new object[] { id }) as Track; } catch { return null; }
            }
            var end = Find(data.endTrackId);

            List<CarsPerTrack> pickups = null;
            if (allCars != null && data.pickupTrackIds.Length > 0 && data.pickupTrackIds.Length == data.pickupCounts.Length
                && data.pickupCounts.Sum() == allCars.Count)
            {
                pickups = new List<CarsPerTrack>();
                int index = 0;
                for (int i = 0; i < data.pickupTrackIds.Length; i++)
                {
                    var t = Find(data.pickupTrackIds[i]);
                    if (t == null || data.pickupCounts[i] <= 0) { pickups = null; break; }
                    pickups.Add(new CarsPerTrack(t, allCars.GetRange(index, data.pickupCounts[i])));
                    index += data.pickupCounts[i];
                }
            }

            if (allCars == null || station == null || end == null || pickups == null)
            {
                Main.Warning($"Shunting prepare: {save.firstJobId}: data incomplete, skipped (cars stay, they get a new job).");
                return null;
            }

            var go = new GameObject($"[LOADED] ChainJob[SP]: {data.originStationId}");
            var chain = new JobChainController(go) { carsForJobChain = allCars };
            var def = go.AddComponent<PrepareJobDefinition>();
            def.PopulateBaseJobDefinition(station, data.timeLimitForJob, data.initialWage,
                new StationsChainData(data.originStationId, data.destinationStationId), (JobLicenses)data.requiredLicenses);
            def.ForceJobId(save.firstJobId);
            def.Setup(chain, pickups, end, data.targetYardId);
            chain.AddJobDefinitionToChain(def);
            chain.FinalizeSetupAndGenerateFirstJob(true);

            if (chain.currentJobInChain == null)
            {
                Main.Warning($"Shunting prepare: {save.firstJobId}: job could not be restored.");
                chain.DestroyChain();
                return null;
            }

            if (save.jobTaken)
            {
                JobsManager.Instance.TakeJob(chain.currentJobInChain, true);
                if (save.currentJobTaskData != null)
                {
                    try { chain.currentJobInChain.OverrideTasksStates(save.currentJobTaskData); }
                    catch (Exception ex) { Main.Warning($"Shunting prepare: {save.firstJobId}: progress not restored ({ex.Message})."); }
                }
                initBooklet?.Invoke(manager, new object[] { chain.currentJobInChain, booklets });
            }
            return go;
        }
    }

    // =====================================================================================================
    // Booklet / Uebersicht / Berichte
    // =====================================================================================================
    internal static class PrepareBooklet
    {
        public static bool IsPrepare(Job_data job) => job != null && PrepareIds.IsPrepareId(job.ID);

        // Patch-Ziele (hier, weil diese Datei die Booklet-Namespaces kennt)
        internal static Type BookletJob => typeof(BookletCreator_Job);
        internal static Type BookletOverview => typeof(BookletCreator_JobOverview);
        internal static Type BookletExpired => typeof(BookletCreator_JobExpiredReport);
        internal static Type BookletMissingLicense => typeof(BookletCreator_JobMissingLicense);

        private static string Title => C.SHUNTING_JOB_TYPE_NAME;
        private static string Subtitle => Loc.T("sp_sub");

        /// <summary>Abholungen (Start = S-Gleis, Ziel = O-Gleis).</summary>
        private static List<Task_data> Pickups(Job_data job)
        {
            var parallel = job.tasksData?.FirstOrDefault()?.nestedTasks?.FirstOrDefault();
            var pickups = parallel?.nestedTasks;
            if (pickups == null || pickups.Length == 0 || pickups.Any(p => p?.cars == null || p.destinationTrackID == null)) return null;
            return pickups.ToList();
        }

        private static TaskTemplatePaperData Page(int step, string type, string description, StationInfo station, TrackID track,
            List<Car_data> cars, int page, int total)
        {
            return new TaskTemplatePaperData(step.ToString(), type, description,
                station.YardID, station.StationColor, track.TrackPartOnly, C.TRACK_COLOR,
                string.Empty, string.Empty, TemplatePaperData.NOT_USED_COLOR,
                cars, DisplayCargo(cars), page.ToString(), total.ToString());
        }

        public static List<TemplatePaperData> CreateBooklet(Job_data job)
        {
            var pickups = Pickups(job);
            if (pickups == null) return Fallback(job);

            var station = job.chainOriginStationInfo;
            var all = pickups.SelectMany(p => p.cars).ToList();
            int total = 2 + pickups.Count + 2;
            int page = 1, step = 1;

            var pages = new List<TemplatePaperData>
            {
                new CoverPageTemplatePaperData(job.ID, Title, (page++).ToString(), total.ToString()),
                Overview(job, pickups, page++, total)
            };
            foreach (var p in pickups)
                pages.Add(Page(step++, LocalizationAPI.L("job/task_type_couple"), Loc.T("sp_pickup"),
                    station, p.startTrackID ?? p.destinationTrackID, p.cars, page++, total));
            pages.Add(Page(step++, LocalizationAPI.L("job/task_type_uncouple"), Loc.T("sp_park"),
                station, pickups[0].destinationTrackID, all, page++, total));
            pages.Add(new ValidateJobTaskTemplatePaperData(step.ToString(), page.ToString(), total.ToString()));
            return pages;
        }

        public static List<TemplatePaperData> CreateOverviewOnly(Job_data job)
        {
            var pickups = Pickups(job);
            return pickups == null
                ? new List<TemplatePaperData> { new CoverPageTemplatePaperData(job.ID, Title, string.Empty, string.Empty) }
                : new List<TemplatePaperData> { Overview(job, pickups, 0, 0) };
        }

        private static TemplatePaperData Overview(Job_data job, List<Task_data> pickups, int page, int total)
        {
            var station = job.chainOriginStationInfo;
            // wie die Vanilla-Booklets: Start- und Zielband (Ziel = naechste Fahrt, sonst derselbe Bahnhof)
            var dest = job.chainDestinationStationInfo ?? station;
            var cars = pickups.SelectMany(p => p.cars).ToList();
            var cargo = DisplayCargo(cars);

            string timeBonus = job.timeLimit > 0f ? $"{Mathf.FloorToInt(job.timeLimit / 60f)} min" : C.NO_BONUS_TIME_LIMIT_STR;
            string length = $"{C.GetCarsTotalLength(cars).ToString("N2", LocalizationAPI.CC)} m";
            string mass = $"{(C.GetCarsTotalMass(cars, cargo) * 0.001f).ToString("N2", LocalizationAPI.CC)} t";
            string value = $"${(C.GetTrainValue(cars, cargo) / 1000000f).ToString("N2", LocalizationAPI.CC)}m";
            string payment = job.basePayment.ToString("N0", LocalizationAPI.CC);
            string track = pickups[0].destinationTrackID?.FullDisplayID ?? string.Empty;
            // wie die Vanilla-Rangierjobs: "2 pick-ups: - ..." (gruen 1, orange 2, rot 3+)
            string description = C.GetShuntingPickUpsText(pickups.Count) + " - " + Loc.F("sp_desc", track);
            var distinct = cargo != null ? cargo.Distinct().ToList() : new List<CargoType>();

            return new FrontPageTemplatePaperData(
                Title, Subtitle, job.ID, ShuntingPrepare.JobColor,
                description, job.requiredLicenses,
                distinct, cargo,
                string.Empty, string.Empty, TemplatePaperData.NOT_USED_COLOR,
                LocalizationAPI.L(station.LocalizationKey), station.Type, station.StationColor,
                LocalizationAPI.L(dest.LocalizationKey), dest.Type, dest.StationColor,
                cars,
                length, mass, value, timeBonus, payment,
                page > 0 ? page.ToString() : string.Empty,
                total > 0 ? total.ToString() : string.Empty);
        }

        private static List<TemplatePaperData> Fallback(Job_data job)
        {
            Main.Warning($"Shunting prepare: {job.ID}: unexpected task structure, emergency booklet.");
            return new List<TemplatePaperData>
            {
                new CoverPageTemplatePaperData(job.ID, Title, "1", "2"),
                new ValidateJobTaskTemplatePaperData("1", "2", "2")
            };
        }

        /// <summary>Fracht je Wagen fuer die Seiten - nur wenn jeder Wagen beladen ist (der Renderer kann CargoType.None nicht).</summary>
        private static List<CargoType> DisplayCargo(List<Car_data> cars)
        {
            try
            {
                if (CarSpawner.Instance == null) return null;
                var byId = new Dictionary<string, TrainCar>();
                foreach (var car in CarSpawner.Instance.AllCars)
                    if (car != null && car.ID != null) byId[car.ID] = car;
                var result = new List<CargoType>();
                foreach (var data in cars)
                {
                    if (data == null || !byId.TryGetValue(data.ID, out var car) || car.logicCar == null) return null;
                    var cargo = car.logicCar.CurrentCargoTypeInCar;
                    if (cargo == CargoType.None) return null;
                    result.Add(cargo);
                }
                return result;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ Patches

        /// <summary>Prefix auf BookletCreator_Job.GetBookletTemplateData.</summary>
        public static bool Booklet_Prefix(Job_data job, ref List<TemplatePaperData> __result)
        {
            if (!IsPrepare(job)) return true;
            try { __result = CreateBooklet(job); }
            catch (Exception ex)
            {
                Main.Warning($"Shunting prepare: booklet {job.ID} failed: {ex.Message}");
                __result = new List<TemplatePaperData>
                {
                    new CoverPageTemplatePaperData(job.ID, "SP", "1", "2"),
                    new ValidateJobTaskTemplatePaperData("1", "2", "2")
                };
            }
            return false;
        }

        /// <summary>Prefix auf BookletCreator_JobOverview.GetJobOverviewTemplateData.</summary>
        public static bool Overview_Prefix(Job_data job, ref List<TemplatePaperData> __result)
        {
            if (!IsPrepare(job)) return true;
            try { __result = CreateOverviewOnly(job); }
            catch (Exception ex)
            {
                Main.Warning($"Shunting prepare: overview {job.ID} failed: {ex.Message}");
                __result = new List<TemplatePaperData> { new CoverPageTemplatePaperData(job.ID, "SP", string.Empty, string.Empty) };
            }
            return false;
        }

        /// <summary>
        /// Prefix auf die Berichte "abgelaufen" / "Lizenz fehlt": Vanilla liest die Taskstruktur des Jobtyps.
        /// Fuer SP-Jobs wird eine einfache Sicht (alle Wagen, eine Fahrt) uebergeben.
        /// </summary>
        public static void Report_Prefix(ref Job_data job)
        {
            try
            {
                if (!IsPrepare(job)) return;
                var pickups = Pickups(job);
                if (pickups == null) return;
                var first = pickups[0];
                var one = new Task_data(first.type, first.instanceTaskType, first.state, first.taskStartTime, first.taskFinishTime,
                    pickups.SelectMany(p => p.cars).ToList(), first.startTrackID, first.destinationTrackID, first.warehouseTaskType,
                    null, 0f, false, false, null);
                job = new Job_data(job.ID, JobType.EmptyHaul, job.state, job.completionTime, job.timeOnJob, job.timeLimit,
                    job.basePayment, job.bonusPayment, job.totalPayment, job.requiredLicenses, new[] { one },
                    job.chainOriginStationInfo, job.chainDestinationStationInfo);
            }
            catch (Exception ex) { Main.WarningOnce("sp-report", "Shunting prepare: report view failed: " + ex.Message); }
        }

        /// <summary>Postfix auf die Berichte: Jobname und Farbe von SP statt "LOGISTICAL HAUL".</summary>
        public static void Report_Postfix(Job_data job, List<TemplatePaperData> __result)
        {
            try
            {
                if (!IsPrepare(job) || __result == null) return;
                foreach (var page in __result)
                {
                    if (page is MissingLicensesPageTemplatePaperData missing)
                    {
                        missing.jobType = Title;
                        missing.jobSubtype = Subtitle;
                        missing.jobTypeColor = ShuntingPrepare.JobColor;
                    }
                    else if (page is JobExpiredTemplatePaperData expired)
                    {
                        expired.jobType = Title;
                        expired.jobSubtype = Subtitle;
                        expired.jobTypeColor = ShuntingPrepare.JobColor;
                    }
                }
            }
            catch (Exception ex) { Main.WarningOnce("sp-report-title", "Shunting prepare: report title failed: " + ex.Message); }
        }
    }
}
