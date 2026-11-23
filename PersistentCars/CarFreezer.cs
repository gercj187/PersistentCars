using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DV;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using UnityEngine;

namespace PersistentCars
{
    /// <summary>
    /// Friert stehende Zuggarnituren ein, wenn kein Spieler in der Naehe ist.
    /// Die Wagen bleiben IMMER in der Welt (Gleisbelegung bleibt fuer andere Mods sichtbar),
    /// nur ihre Physik (Stufe 1) bzw. zusaetzlich ihre Skripte (Stufe 2, experimentell) werden pausiert.
    /// Laeuft nur im Singleplayer bzw. beim MP-Host.
    /// </summary>
    internal sealed class CarFreezer : MonoBehaviour
    {
        private sealed class FrozenCar
        {
            public readonly List<KeyValuePair<Rigidbody, bool>> Bodies = new List<KeyValuePair<Rigidbody, bool>>();
            public readonly List<Behaviour> DisabledBehaviours = new List<Behaviour>();
            public bool OptimizedByUs;
            // Tiefschlaf: nur ausgeschaltete Darstellung/Sound - Drehgestelle, Collider und Rigidbodies bleiben,
            // damit Achszaehler/Gleisbelegung (z. B. eigene Stellwerksmod) unveraendert besetzt melden
            public bool Deep;
            public readonly List<Renderer> DeepRenderers = new List<Renderer>();
            public readonly List<Behaviour> DeepBehaviours = new List<Behaviour>();
            public readonly List<Component> DeepParticles = new List<Component>();   // ParticleSystem per Reflection (kein Modul-Verweis noetig)
            public Car LogicCar;
            public TrainCar Car;
        }

        private readonly Dictionary<TrainCar, FrozenCar> _frozen = new Dictionary<TrainCar, FrozenCar>();

        /// <summary>Logik-Wagen, die wir eingefroren haben - der Optimizer des Spiels darf sie nicht wecken.</summary>
        public static readonly HashSet<Car> FrozenLogicCars = new HashSet<Car>();
        private readonly Queue<List<TrainCar>> _wakeQueue = new Queue<List<TrainCar>>();
        private readonly HashSet<TrainCar> _wakeQueued = new HashSet<TrainCar>();
        private readonly Queue<List<TrainCar>> _freezeQueue = new Queue<List<TrainCar>>();
        private readonly HashSet<TrainCar> _freezeQueued = new HashSet<TrainCar>();
        private readonly Queue<TrainCar> _deepWakeQueue = new Queue<TrainCar>();
        private readonly Queue<TrainCar> _deepSleepQueue = new Queue<TrainCar>();
        private readonly HashSet<TrainCar> _deepSleepQueued = new HashSet<TrainCar>();
        private readonly HashSet<TrainCar> _deepWakeQueued = new HashSet<TrainCar>();
        private bool _wakeAllQueued;
        private readonly List<Vector3> _movingAnchors = new List<Vector3>();
        private readonly Dictionary<Type, bool> _protectedTypeCache = new Dictionary<Type, bool>();
        private string _protectedCacheKey;
        private string[] _protectedParts = new string[0];

        private bool _scanRequested;
        private float _nextScan;

        // ---- Werte fuer HUD / Log
        public int FrozenCount => _frozen.Count;
        public int DeepCount { get; private set; }
        public int TotalCars { get; private set; }
        public int TotalSets { get; private set; }
        public double LastScanCpuMs { get; private set; }
        public bool IsRunning { get; private set; }

        /// <summary>Bis zu diesem Zeitpunkt (realtimeSinceStartup) wird nichts eingefroren (z. B. nach Weltspawn).</summary>
        public static float SuspendedUntil;

        public void RequestScan() => _scanRequested = true;

        private void Start()
        {
            _lastBogieSetting = Main.Settings.DeepSleep && Main.Settings.DeepSleepBogies;
            StartCoroutine(Loop());
        }

        // ================================================================== Hauptschleife

        private IEnumerator Loop()
        {
            while (true)
            {
                yield return null;
                ProcessTransitions();
                ProcessDeepTransitions();

                bool shouldRun = ShouldRun();
                IsRunning = shouldRun;

                if (!shouldRun)
                {
                    if (_frozen.Count > 0 && MustWakeAll()) WakeAll(WorldSpawner.Running ? "world spawn" : "freezer inactive");
                    continue;
                }

                if (!_scanRequested && Time.realtimeSinceStartup < _nextScan) continue;
                _scanRequested = false;
                _nextScan = Time.realtimeSinceStartup + Main.Settings.ScanInterval;

                IEnumerator scan = Scan();
                while (true)
                {
                    bool more;
                    try
                    {
                        more = scan.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        Main.Error("Freeze scan failed: " + ex);
                        break;
                    }
                    if (!more) break;
                    yield return scan.Current;
                }
            }
        }

        private static bool ShouldRun()
        {
            if (!Main.IsAuthority || !Main.Settings.FreezeEnabled) return false;
            if (!WorldStreamingInit.IsLoaded || PlayerManager.PlayerTransform == null) return false;
            // Waehrend des Weltspawns niemals Physikzustaende wechseln. Teilweise eingefrorene Garnituren waren
            // die Ursache fuer extreme Kupplungskraefte, dreistellige Geschwindigkeiten und Massenentgleisungen.
            if (WorldSpawner.Running || MinStock.Running || Capacity.IsGenerating) return false;
            if (Time.realtimeSinceStartup < SuspendedUntil) return false;
            return true;
        }

        private static bool MustWakeAll()
        {
            if (!Main.IsAuthority || !Main.Settings.FreezeEnabled) return true;
            if (!WorldStreamingInit.IsLoaded || PlayerManager.PlayerTransform == null) return true;
            return WorldSpawner.Running;
        }

        // ================================================================== Scan

        private IEnumerator Scan()
        {
            var cpu = new Stopwatch();
            var slice = Stopwatch.StartNew();
            const double frameBudgetMs = 1.0;

            cpu.Start();
            RemoveDestroyedCars();

            var sets = Trainset.allSets?.ToArray();
            if (sets == null)
            {
                cpu.Stop();
                yield break;
            }

            // Positionen fahrender, nicht eingefrorener Wagen (z. B. wegrollende Zuege)
            _movingAnchors.Clear();
            if (Main.Settings.WakeNearMovingTrains)
            {
                foreach (var set in sets)
                {
                    if (set?.cars == null) continue;
                    foreach (var car in set.cars)
                        if (car != null && !_frozen.ContainsKey(car) && Speed(car) > MovingSpeed)
                            _movingAnchors.Add(car.transform.position);
                }
            }

            float freezeSqr = Main.Settings.FreezeDistance * Main.Settings.FreezeDistance;
            float wakeSqr = Main.Settings.WakeDistance * Main.Settings.WakeDistance;
            float movingSqr = Main.Settings.MovingTrainWakeDistance * Main.Settings.MovingTrainWakeDistance;
            bool ignoreDistance = Main.Settings.DebugIgnoreDistance;
            TrainCar playerCar = PlayerManager.Car;

            int totalCars = 0;
            _whyPlayerCar = _whyPlayerNear = _whyMoving = _whyMovingNear = _whyDerailed = _whyCooldown = _whyPartial = _whyLoco = 0;

            foreach (var set in sets)
            {
                var cars = set?.cars;
                if (cars != null && cars.Count > 0)
                {
                    totalCars += cars.Count;
                    EvaluateSet(cars, freezeSqr, wakeSqr, movingSqr, ignoreDistance, playerCar);
                }

                if (slice.Elapsed.TotalMilliseconds > frameBudgetMs)
                {
                    cpu.Stop();
                    yield return null;
                    slice.Reset();
                    slice.Start();
                    cpu.Start();
                }
            }

            UpdateDeepSleep();

            cpu.Stop();
            TotalCars = totalCars;
            TotalSets = sets.Length;
            LastScanCpuMs = cpu.Elapsed.TotalMilliseconds;

            // Zustand ins Log, aber nur bei Aenderung und hoechstens alle 10 s
            if (_frozen.Count != _lastLoggedFrozen && Time.realtimeSinceStartup >= _nextStatusLog)
            {
                _lastLoggedFrozen = _frozen.Count;
                _nextStatusLog = Time.realtimeSinceStartup + 10f;
                Main.Log($"Freezer: {_frozen.Count} of {totalCars} cars frozen ({sets.Length} sets), scan {LastScanCpuMs:F1} ms CPU. " +
                         $"Awake because: player near {_whyPlayerNear}, player's train {_whyPlayerCar}, moving {_whyMoving}, " +
                         $"moving train near {_whyMovingNear}, derailed {_whyDerailed}, just woken {_whyCooldown}, partly frozen {_whyPartial}, " +
                         $"with loco {_whyLoco}.{Mp.PlayerSummary()}");
            }
        }

        private int _lastLoggedFrozen = -1;
        private int _whyPlayerCar, _whyPlayerNear, _whyMoving, _whyMovingNear, _whyDerailed, _whyCooldown, _whyPartial, _whyLoco;

        /// <summary>Lok, Tender oder Caboose in der Garnitur = Zug eines Spielers -> nie einfrieren (auch im MP sicher).</summary>
        private static bool IsTrain(TrainCar car)
        {
            if (car == null) return false;
            if (car.IsLoco) return true;
            string n = car.name ?? "";
            return n.StartsWith("Loco", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("Tender", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Caboose", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Schwellen: "steht" = unter 0,3 m/s, "faehrt" (weckt Nachbarn) = ueber 1 m/s. isStationary des Spiels
        // war zu empfindlich - frisch gespawnte Wagen zittern minimal und hielten sich gegenseitig wach.
        private const float StandingSpeed = 0.3f;
        private const float MovingSpeed = 1.0f;
        // Nach dem Wecken mindestens so lange wach lassen - verhindert staendiges Ein-/Ausfrieren (Ruckler)
        private const float WakeCooldown = 30f;
        private readonly Dictionary<TrainCar, float> _lastWake = new Dictionary<TrainCar, float>();
        /// <summary>Erst einfrieren, wenn eine Garnitur so lange stillsteht (fahrende/gerade abgestellte Zuege bleiben wach).</summary>
        private const float StandStillBeforeFreeze = 120f;
        private readonly Dictionary<TrainCar, float> _lastMoved = new Dictionary<TrainCar, float>();
        /// <summary>
        /// Position je Wagen beim letzten Scan (ohne Weltverschiebung). Bewegung wird zusaetzlich ueber die
        /// Positionsaenderung erkannt: Im Multiplayer kann ein Zug, den ein Client steuert, auf dem Host als
        /// Marionette ohne Geschwindigkeit laufen - dann meldet GetForwardSpeed 0 und der Zug wuerde bei voller
        /// Fahrt eingefroren.
        /// </summary>
        private readonly Dictionary<TrainCar, Vector3> _lastPos = new Dictionary<TrainCar, Vector3>();
        private const float MovedDistance = 0.5f;

        private static float Speed(TrainCar car)
        {
            try { return Mathf.Abs(car.GetForwardSpeed()); }
            catch { return car.isStationary ? 0f : 10f; }
        }
        private float _nextStatusLog;

        private void EvaluateSet(List<TrainCar> cars, float freezeSqr, float wakeSqr, float movingSqr, bool ignoreDistance, TrainCar playerCar)
        {
            int frozenInSet = 0;
            bool allStationary = true;
            bool anyDerailed = false;
            bool containsPlayerCar = false;
            bool recentlyWoken = false;
            bool hasLoco = false;
            Vector3 originShift = WorldOrigin.Shift();
            bool recentlyMoved = false;
            float nearestPlayer = float.MaxValue;
            float nearestClient = float.MaxValue;   // nur MP-Clients (nicht der Host)
            float nearestMoving = float.MaxValue;

            for (int i = 0; i < cars.Count; i++)
            {
                var car = cars[i];
                if (car == null) continue;

                bool isFrozen = _frozen.ContainsKey(car);
                if (isFrozen) frozenInSet++;
                else if (Speed(car) > StandingSpeed) { allStationary = false; _lastMoved[car] = Time.realtimeSinceStartup; }

                Vector3 abs = car.transform.position - originShift;
                if (_lastPos.TryGetValue(car, out var lastAbs) && (abs - lastAbs).sqrMagnitude > MovedDistance * MovedDistance)
                {
                    allStationary = false;
                    _lastMoved[car] = Time.realtimeSinceStartup;
                }
                _lastPos[car] = abs;
                if (_lastMoved.TryGetValue(car, out float moved) && Time.realtimeSinceStartup - moved < StandStillBeforeFreeze) recentlyMoved = true;
                if (!isFrozen && _lastWake.TryGetValue(car, out float woke) && Time.realtimeSinceStartup - woke < WakeCooldown) recentlyWoken = true;

                if (car.derailed) anyDerailed = true;
                if (IsTrain(car)) hasLoco = true;
                if (playerCar != null && car == playerCar) containsPlayerCar = true;

                Vector3 pos = car.transform.position;
                float d = Mp.NearestPlayerSqrDistance(pos);
                if (d < nearestPlayer) nearestPlayer = d;
                float dc = Mp.NearestClientSqrDistance(pos);
                if (dc < nearestClient) nearestClient = dc;

                for (int a = 0; a < _movingAnchors.Count; a++)
                {
                    float m = (_movingAnchors[a] - pos).sqrMagnitude;
                    if (m < nearestMoving) nearestMoving = m;
                }
            }

            bool movingTrainClose = nearestMoving < movingSqr;
            // Clients wecken VOLLSTAENDIG: im ganzen Sichtbereich (Tiefschlaf-Entfernung) Physik + Darstellung.
            // Der Client sieht die Wagen ueber die Daten des Hosts - halb schlafende Wagen ruckeln dort.
            float clientWake = Main.Settings.DeepSleepDistance;
            bool clientNear = !ignoreDistance && nearestClient < clientWake * clientWake;

            // Garnitur teils eingefroren, teils nicht (z. B. neu gekuppelt) -> sicherheitshalber alles wecken
            if (frozenInSet > 0 && frozenInSet < cars.Count)
            {
                WakeCars(cars);
                _whyPartial += cars.Count;
                return;
            }

            // Zuege mit Lok/Tender/Caboose werden nie eingefroren: Im Multiplayer erkennt der Host sonst
            // u. U. nicht, dass ein Client an seinem Zug steht -> Wagen auf dem Host fest, beim Client wildes Ruckeln.
            if (hasLoco)
            {
                if (frozenInSet > 0) WakeCars(cars);
                _whyLoco += cars.Count;
                return;
            }

            if (frozenInSet > 0)
            {
                bool wake = containsPlayerCar || recentlyMoved || movingTrainClose || clientNear || (!ignoreDistance && nearestPlayer < wakeSqr);
                if (wake) { WakeCars(cars); return; }
                // von aussen geweckt (z. B. Kupplungs-Nachbar) und steht trotzdem -> wieder schlafen legen
                foreach (var c in cars)
                    if (c != null && c.rb != null && !c.rb.IsSleeping() && Speed(c) < StandingSpeed)
                        try { c.ForceOptimizationState(true); } catch { }
                return;
            }

            bool playerFar = ignoreDistance || nearestPlayer > freezeSqr;
            bool freeze = allStationary && !recentlyMoved && !anyDerailed && !containsPlayerCar && !movingTrainClose && playerFar && !recentlyWoken && !clientNear;
            if (freeze) { FreezeCars(cars); return; }

            // Grund, warum die Garnitur wach bleibt (fuers Log) - erster zutreffender Grund zaehlt
            int n = cars.Count;
            if (containsPlayerCar) _whyPlayerCar += n;
            else if (!playerFar) _whyPlayerNear += n;
            else if (!allStationary || recentlyMoved) _whyMoving += n;
            else if (movingTrainClose) _whyMovingNear += n;
            else if (anyDerailed) _whyDerailed += n;
            else if (recentlyWoken) _whyCooldown += n;
        }

        // ================================================================== Einfrieren / Wecken

        private void FreezeCars(List<TrainCar> cars)
        {
            var group = cars?.Where(car => car != null && !_frozen.ContainsKey(car)).ToList();
            if (group == null || group.Count == 0) return;
            foreach (var car in group) _wakeQueued.Remove(car);
            bool added = false;
            foreach (var car in group) added |= _freezeQueued.Add(car);
            if (!added) return;
            _freezeQueue.Enqueue(group);
        }

        private void WakeCars(List<TrainCar> cars)
        {
            var group = cars?.Where(car => car != null && _frozen.ContainsKey(car)).ToList();
            if (group == null || group.Count == 0) return;
            foreach (var car in group) _freezeQueued.Remove(car);
            bool added = false;
            foreach (var car in group) added |= _wakeQueued.Add(car);
            if (!added) return;
            _wakeQueue.Enqueue(group);
        }

        /// <summary>
        /// Zustandswechsel sind ueber Frames verteilt, aber jede gekuppelte Garnitur wird atomar in EINEM Frame
        /// umgeschaltet. Einzelne Wagen einer Garnitur ueber mehrere Frames zu verteilen zerreisst die Kupplungen.
        /// </summary>
        private void ProcessTransitions()
        {
            var budget = Stopwatch.StartNew();
            int processedSets = 0;
            const double maxMs = 1.25;
            const int maxSets = 4;

            while (_wakeQueue.Count > 0 && processedSets < maxSets && (processedSets == 0 || budget.Elapsed.TotalMilliseconds < maxMs))
            {
                var group = _wakeQueue.Dequeue();
                bool valid = false;
                foreach (var car in group)
                    if (car != null && _wakeQueued.Remove(car)) valid = true;
                if (!valid) continue;
                foreach (var car in group)
                    if (car != null) WakeCar(car);
                processedSets++;
            }

            while (_wakeQueue.Count == 0 && _freezeQueue.Count > 0 && processedSets < maxSets && (processedSets == 0 || budget.Elapsed.TotalMilliseconds < maxMs))
            {
                var group = _freezeQueue.Dequeue();
                bool valid = false;
                foreach (var car in group)
                    if (car != null && _freezeQueued.Remove(car)) valid = true;
                if (!valid) continue;

                float freezeSqr = Main.Settings.FreezeDistance * Main.Settings.FreezeDistance;
                if (group.Any(car => car == null || car.derailed || Speed(car) > StandingSpeed ||
                                     Mp.NearestPlayerSqrDistance(car.transform.position) <= freezeSqr))
                    continue;
                foreach (var car in group)
                    if (!_frozen.ContainsKey(car)) FreezeCar(car);
                processedSets++;
            }

            if (_wakeQueue.Count == 0) _wakeAllQueued = false;
        }

        /// <summary>
        /// Darstellung und Ladung werden vor der eigentlichen Physik stufenweise eingeblendet. So erzeugt ein
        /// grosser Bahnhof beim Ueberschreiten der Sichtdistanz keinen einzelnen Renderer-Spike.
        /// </summary>
        private void ProcessDeepTransitions()
        {
            const int maxCarsPerFrame = 8;
            int processed = 0;
            while (_deepWakeQueue.Count > 0 && processed < maxCarsPerFrame)
            {
                var car = _deepWakeQueue.Dequeue();
                _deepWakeQueued.Remove(car);
                if (car == null || !_frozen.TryGetValue(car, out var fc) || !fc.Deep) continue;
                DeepWake(fc);
                DeepCount = Math.Max(0, DeepCount - 1);
                processed++;
            }

            // In den Tiefschlaf ebenso fliessend: hoechstens so viele Wagen je Frame, wie nach dem Aufwecken
            // noch Platz haben - nie ein ganzer Bahnhof in einem Frame.
            float sleepDist = Main.Settings.DeepSleepDistance + 100f;
            float sleepSqr = sleepDist * sleepDist;
            while (_deepSleepQueue.Count > 0 && processed < maxCarsPerFrame)
            {
                var car = _deepSleepQueue.Dequeue();
                _deepSleepQueued.Remove(car);
                if (car == null || _wakeQueued.Contains(car) || !_frozen.TryGetValue(car, out var fc) || fc.Deep) continue;
                // erneut pruefen: inzwischen kann ein Spieler (auch ein Client) naeher gekommen sein
                if (!Main.Settings.DeepSleep || Mp.NearestPlayerSqrDistance(car.transform.position) < sleepSqr) continue;
                DeepSleepCar(car, fc);
                if (fc.Deep) DeepCount++;
                processed++;
            }
        }

        private void QueueDeepSleep(TrainCar car)
        {
            if (car != null && _deepSleepQueued.Add(car)) _deepSleepQueue.Enqueue(car);
        }

        // ================================================================== Schadensschutz rund um den Tiefschlaf

        /// <summary>Wagen im Tiefschlaf: nehmen keinen Schaden.</summary>
        private static readonly HashSet<TrainCar> _noDamage = new HashSet<TrainCar>();
        /// <summary>Gerade geweckte Wagen: noch ein paar Sekunden ohne Schaden (die Physik setzt sich).</summary>
        private static readonly Dictionary<TrainCar, float> _noDamageUntil = new Dictionary<TrainCar, float>();
        private const float DamageGraceAfterWake = 10f;

        /// <summary>Aus SpawnProtection.Damage_Prefix: Schaden an diesem Wagen ignorieren?</summary>
        public static bool IsDamageBlocked(TrainCar car)
        {
            if (car == null) return false;
            if (_noDamage.Contains(car)) return true;
            if (_noDamageUntil.TryGetValue(car, out float until))
            {
                if (Time.realtimeSinceStartup < until) return true;
                _noDamageUntil.Remove(car);
            }
            return false;
        }

        private static void GraceAfterWake(TrainCar car)
        {
            if (car == null) return;
            _noDamage.Remove(car);
            _noDamageUntil[car] = Time.realtimeSinceStartup + DamageGraceAfterWake;
        }

        private void FreezeCar(TrainCar car)
        {
            var fc = new FrozenCar();

            // a) Schlafzustand des Spiels (Rigidbodies schlafen, Schwerkraft aus). Die Drehgestelle brechen ihr
            //    FixedUpdate bei schlafendem Rigidbody sofort ab. Damit der Optimizer des Spiels den Wagen nicht
            //    wieder weckt (er weckt ganze Gleisgruppen, sobald irgendwo darin etwas faehrt), steht er in
            //    FrozenLogicCars - siehe Patch auf TrainsOptimizer.ForceOptimizationStateOnCars.
            try
            {
                car.ForceOptimizationState(true);
                fc.OptimizedByUs = true;
            }
            catch (Exception ex)
            {
                Main.WarningOnce("opt-sleep", "ForceOptimizationState(true) failed: " + ex.Message);
            }

            // b) optional (alt): Rigidbodies zusaetzlich kinematisch. Standard aus - kinematische Koerper "schlafen"
            //    nicht, dadurch liefen die Drehgestell-Skripte voll weiter und schalteten die Schwerkraft wieder ein.
            if (Main.Settings.FreezeKinematic)
            {
                foreach (var rb in car.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb == null) continue;
                    bool wasKinematic = rb.isKinematic;
                    if (!wasKinematic)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        rb.isKinematic = true;
                    }
                    fc.Bodies.Add(new KeyValuePair<Rigidbody, bool>(rb, wasKinematic));
                }
            }

            // c) Stufe 2 (experimentell): Skripte normaler Gueterwagen pausieren
            if (Main.Settings.FreezeLevel2 && CarTypes.IsRegularCar(car.carLivery))
            {
                foreach (var b in car.GetComponentsInChildren<Behaviour>(true))
                {
                    if (b == null || !b.enabled || IsProtected(b)) continue;
                    b.enabled = false;
                    fc.DisabledBehaviours.Add(b);
                }
            }

            _frozen[car] = fc;
            fc.LogicCar = car.logicCar;
            if (car.logicCar != null) FrozenLogicCars.Add(car.logicCar);
            fc.Car = car;
            // Tiefschlaf nicht sofort fuer die ganze Garnitur, sondern fliessend (ProcessDeepTransitions)
            if (Main.Settings.DeepSleep) QueueDeepSleep(car);
        }

        private void WakeCar(TrainCar car)
        {
            if (!_frozen.TryGetValue(car, out var fc)) return;
            _frozen.Remove(car);
            _lastWake[car] = Time.realtimeSinceStartup;
            _deepSleepQueued.Remove(car);
            GraceAfterWake(car);

            if (fc.Deep)
            {
                _deepWakeQueued.Remove(car);
                DeepWake(fc);
                DeepCount = Math.Max(0, DeepCount - 1);
            }
            foreach (var b in fc.DisabledBehaviours)
                if (b != null) b.enabled = true;

            foreach (var kv in fc.Bodies)
                if (kv.Key != null && !kv.Value) kv.Key.isKinematic = false;

            if (fc.LogicCar != null) FrozenLogicCars.Remove(fc.LogicCar);

            if (fc.OptimizedByUs)
            {
                try
                {
                    car.ForceOptimizationState(false);
                }
                catch (Exception ex)
                {
                    Main.WarningOnce("opt-wake", "ForceOptimizationState(false) failed: " + ex.Message);
                }
            }
        }

        // ================================================================== Tiefschlaf (Debug)

        private bool _lastBogieSetting;

        private void UpdateDeepSleep()
        {
            bool on = Main.Settings.DeepSleep;
            // Drehgestell-Schalter geaendert: alle Tiefschlaefer einmal wecken, beim naechsten Scan mit neuer Einstellung
            bool bogies = on && Main.Settings.DeepSleepBogies;
            if (bogies != _lastBogieSetting)
            {
                _lastBogieSetting = bogies;
                foreach (var fc in _frozen.Values) if (fc.Deep) DeepWake(fc);
            }
            float visualWakeSqr = Main.Settings.DeepSleepDistance * Main.Settings.DeepSleepDistance;
            float visualSleepDistance = Main.Settings.DeepSleepDistance + 100f;
            float visualSleepSqr = visualSleepDistance * visualSleepDistance;
            int deep = 0;
            foreach (var kv in _frozen)
            {
                var car = kv.Key;
                var fc = kv.Value;
                if (car == null) continue;
                float nearest = Mp.NearestPlayerSqrDistance(car.transform.position);
                if (!on || nearest <= visualWakeSqr)
                {
                    if (fc.Deep && _deepWakeQueued.Add(car)) _deepWakeQueue.Enqueue(car);
                    if (fc.Deep) deep++;
                    continue;
                }
                // 100 m Hysterese verhindert Flackern an der Sichtgrenze.
                if (!fc.Deep && nearest >= visualSleepSqr) QueueDeepSleep(car);
                if (fc.Deep) deep++;
            }
            if (deep != DeepCount && (deep == 0 || DeepCount == 0 || Math.Abs(deep - DeepCount) >= 50))
                Main.Log($"Deep sleep: {deep} far car(s) hidden and muted (beyond {Main.Settings.DeepSleepDistance:0} m).");
            DeepCount = deep;
        }

        private static void DeepSleepCar(TrainCar car, FrozenCar fc)
        {
            // unmittelbar davor: Schadensempfaenger aus - beim Abschalten von Drehgestellen/Physik darf nichts
            // Schaden melden. Wieder an erst 10 s nach dem Aufwachen.
            _noDamage.Add(car);
            _noDamageUntil.Remove(car);
            try
            {
                DisableRenderers(car.gameObject, fc);
                // Ladungsmodelle sind je nach Wagen nicht zwingend unter allen visuellen LOD-Wurzeln einsortiert.
                // Deshalb den aktuell geladenen Cargo-Prefab explizit erfassen.
                var cargo = car.CargoModelController != null ? car.CargoModelController.GetCurrentCargoModel() : null;
                if (cargo != null) DisableRenderers(cargo, fc);
                // AudioSource (AudioModule) ohne Modul-Verweis: ist ein Behaviour -> ueber den Typnamen
                foreach (var b in car.GetComponentsInChildren<Behaviour>(true))
                    if (b != null && b.enabled && b.GetType().Name == "AudioSource") { b.enabled = false; fc.DeepBehaviours.Add(b); }
                // optional: Drehgestelle abschalten (spart Bogie.FixedUpdate inkl. Postfixe anderer Mods, z. B.
                // Achszaehlung von DV_Signals). Die Wagen stehen, es gibt kein Ein-/Auszaehlen; der Zustand je
                // Drehgestell bleibt in der anderen Mod erhalten und laeuft beim Aufwachen an derselben Stelle weiter.
                if (Main.Settings.DeepSleepBogies)
                    foreach (var b in car.GetComponentsInChildren<Behaviour>(true))
                        if (b != null && b.enabled && b.GetType().Name == "Bogie") { b.enabled = false; fc.DeepBehaviours.Add(b); }
                foreach (var l in car.GetComponentsInChildren<Light>(true))
                    if (l != null && l.enabled) { l.enabled = false; fc.DeepBehaviours.Add(l); }
                foreach (var c in car.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c.GetType().Name != "ParticleSystem") continue;
                    if (!Reflect.Get<bool>(c, "isPlaying")) continue;
                    CallParticle(c, "Pause");
                    fc.DeepParticles.Add(c);
                }
                fc.Deep = true;
            }
            catch (Exception ex)
            {
                Main.WarningOnce("deep-sleep", "Deep sleep failed: " + ex.Message);
                DeepWake(fc);
            }
        }

        private static void DisableRenderers(GameObject root, FrozenCar fc)
        {
            if (root == null) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                if (r != null && r.enabled && !fc.DeepRenderers.Contains(r))
                {
                    r.enabled = false;
                    fc.DeepRenderers.Add(r);
                }
        }

        /// <summary>ParticleSystem.Pause(true) / Play(true) per Reflection.</summary>
        private static void CallParticle(Component ps, string name)
        {
            try
            {
                var m = ps.GetType().GetMethod(name, new[] { typeof(bool) });
                m?.Invoke(ps, new object[] { true });
            }
            catch { }
        }

        private static void DeepWake(FrozenCar fc)
        {
            GraceAfterWake(fc.Car);
            foreach (var r in fc.DeepRenderers) if (r != null) r.enabled = true;
            foreach (var b in fc.DeepBehaviours) if (b != null) b.enabled = true;
            foreach (var p in fc.DeepParticles) if (p != null) CallParticle(p, "Play");
            fc.DeepRenderers.Clear();
            fc.DeepBehaviours.Clear();
            fc.DeepParticles.Clear();
            fc.Deep = false;
        }

        public void WakeAll(string reason)
        {
            if (_frozen.Count == 0) return;
            if (_wakeAllQueued) return;
            _wakeAllQueued = true;
            int n = _frozen.Count;
            var alive = _frozen.Keys.Where(car => car != null).ToList();
            foreach (var group in alive.GroupBy(car => (object)car.trainset ?? car).Select(g => g.ToList()))
                WakeCars(group);
            foreach (var car in _frozen.Keys.Where(car => car == null).ToList())
            {
                var fc = _frozen[car];
                if (fc.LogicCar != null) FrozenLogicCars.Remove(fc.LogicCar);
                _frozen.Remove(car);
            }
            _freezeQueued.Clear();
            _deepSleepQueued.Clear();
            _deepSleepQueue.Clear();
            Main.Log($"Queued {n} frozen car(s) for staggered wake-up ({reason}).");
        }

        private void RemoveDestroyedCars()
        {
            _noDamage.RemoveWhere(c => c == null);
            _deepSleepQueued.RemoveWhere(c => c == null);
            if (_noDamageUntil.Count > 0)
            {
                float t = Time.realtimeSinceStartup;
                foreach (var k in _noDamageUntil.Where(kv => kv.Key == null || t >= kv.Value).Select(kv => kv.Key).ToList())
                    _noDamageUntil.Remove(k);
            }
            if (_lastWake.Count > 0)
            {
                float now = Time.realtimeSinceStartup;
                var old = _lastWake.Where(kv => kv.Key == null || now - kv.Value > WakeCooldown).Select(kv => kv.Key).ToList();
                foreach (var k in old) _lastWake.Remove(k);
            }
            if (_lastMoved.Count > 0)
            {
                float now = Time.realtimeSinceStartup;
                var oldMoved = _lastMoved.Where(kv => kv.Key == null || now - kv.Value > StandStillBeforeFreeze).Select(kv => kv.Key).ToList();
                foreach (var k in oldMoved) _lastMoved.Remove(k);
            }
            if (_lastPos.Count > 0)
            {
                var gone = _lastPos.Keys.Where(k => k == null).ToList();
                foreach (var k in gone) _lastPos.Remove(k);
            }
            List<TrainCar> dead = null;
            foreach (var car in _frozen.Keys)
                if (car == null) (dead ??= new List<TrainCar>()).Add(car);
            if (dead != null)
                foreach (var car in dead)
                {
                    var fc = _frozen[car];
                    if (fc.LogicCar != null) FrozenLogicCars.Remove(fc.LogicCar);
                    _frozen.Remove(car);
                }
        }

        // ================================================================== Stufe 2: geschuetzte Komponenten

        private bool IsProtected(Behaviour b)
        {
            string key = Main.Settings.ProtectedComponentNames ?? "";
            if (key != _protectedCacheKey)
            {
                _protectedCacheKey = key;
                _protectedParts = key.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                _protectedTypeCache.Clear();
            }

            var type = b.GetType();
            if (_protectedTypeCache.TryGetValue(type, out bool cached)) return cached;

            bool result;
            string asm = type.Assembly.GetName().Name;
            // Nur Spiel-Komponenten anfassen - niemals Unity-, Multiplayer- oder andere Mod-Komponenten
            if (type.Name.IndexOf("Brake", StringComparison.OrdinalIgnoreCase) >= 0)
                result = true; // Luft- und Handbremsen bleiben die ausdrueckliche Ausnahme vom Tiefschlaf
            else if (asm != "Assembly-CSharp" && !asm.StartsWith("DV."))
                result = true;
            else
                result = _protectedParts.Any(p => type.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

            _protectedTypeCache[type] = result;
            return result;
        }

        /// <summary>Schreibt die haeufigsten Komponenten-Typen auf wachen Gueterwagen ins Log (Entscheidungshilfe fuer Stufe 2).</summary>
        public void LogComponentStatistics()
        {
            try
            {
                var counts = new Dictionary<string, int>();
                int carCount = 0;
                foreach (var car in CarSpawner.Instance.AllCars)
                {
                    if (car == null || _frozen.ContainsKey(car) || !CarTypes.IsRegularCar(car.carLivery)) continue;
                    carCount++;
                    foreach (var b in car.GetComponentsInChildren<Behaviour>(true))
                    {
                        if (b == null) continue;
                        var t = b.GetType();
                        string name = $"{t.FullName} [{t.Assembly.GetName().Name}]{(b.enabled ? "" : " (disabled)")}";
                        counts[name] = counts.TryGetValue(name, out int c) ? c + 1 : 1;
                    }
                }

                Main.Log($"Component statistics over {carCount} awake freight car(s):");
                foreach (var kv in counts.OrderByDescending(k => k.Value).Take(40))
                    Main.Log($"  {kv.Value,6}x  {kv.Key}");
            }
            catch (Exception ex)
            {
                Main.Warning("Component statistics failed: " + ex.Message);
            }
        }
    }
}
