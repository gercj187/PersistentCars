using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.ThingTypes;
using UnityEngine;

namespace PersistentCars
{
    /// <summary>
    /// Sichert jede frisch erzeugte Wagengruppe nach einer lastabhaengigen Garniturregel. Die fruehere
    /// Gefaellemessung ueber Drehgestellhoehen erkannte reale Steigungen unzuverlaessig.
    /// </summary>
    internal static class SpawnSafety
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly HashSet<TrainCar> ScheduledThisFrame = new HashSet<TrainCar>();
        private static int _scheduledFrame = -1;

        public static void Schedule(List<TrainCar> cars)
        {
            if (!Main.IsAuthority || !Main.Settings.SlopeHandbrakes ||
                (!WorldSpawner.Running && !MinStock.Running && !Capacity.IsGenerating) || cars == null || cars.Count == 0)
                return;
            var freightCars = cars.Where(c => c != null && !c.IsLoco).ToList();
            if (freightCars.Count == 0) return;

            // Mehrere gepatchte CarSpawner-Ueberladungen koennen fuer dieselbe Gruppe ineinander greifen.
            // Jede erzeugte Gruppe wird trotzdem nur einmal gesichert und protokolliert.
            if (_scheduledFrame != Time.frameCount)
            {
                _scheduledFrame = Time.frameCount;
                ScheduledThisFrame.Clear();
            }
            if (freightCars.All(car => ScheduledThisFrame.Contains(car))) return;
            foreach (var car in freightCars) ScheduledThisFrame.Add(car);

            // Noch im Spawn-Postfix sichern und danach nachsetzen, weil das Vanilla-Setup die Verteilung in
            // seinen eigenen Coroutinen noch ueberschreiben kann.
            int changesImmediately = ApplyPolicy(freightCars, out _);
            Runtime.Spawner?.StartCoroutine(SecureAfterSpawn(freightCars, changesImmediately));
        }

        private static IEnumerator SecureAfterSpawn(List<TrainCar> cars, int changes)
        {
            yield return null;
            changes += ApplyPolicy(cars, out _);
            yield return new WaitForSecondsRealtime(0.5f);
            changes += ApplyPolicy(cars, out _);

            // Job-Setup und automatische Kupplung laufen in mehreren Coroutinen. Vanilla kann dabei die
            // Handbremsverteilung spaeter erneut auf "nur letzter Wagen" setzen. Zehn Sekunden lang nachhalten.
            float protectUntil = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < protectUntil)
            {
                yield return new WaitForSecondsRealtime(1f);
                changes += ApplyPolicy(cars, out _);
            }
            changes += ApplyPolicy(cars, out var state);
            Main.Log($"Spawn safety: {state.Secured}/{state.Required} required handbrakes set across {state.Empty} empty and " +
                     $"{state.Loaded} loaded car(s) ({changes} corrections; rule 1:4 empty / 1:2 loaded).");
        }

        private sealed class PolicyState
        {
            public int Empty;
            public int Loaded;
            public int Required;
            public int Secured;
        }

        /// <summary>
        /// Gemischte Garnitur: ceil(leer/4 + beladen/2), mindestens eine und hoechstens so viele wie vorhandene
        /// Handbremsen. Die ausgewaehlten Bremsen werden gleichmaessig vom ersten bis zum letzten Wagen verteilt.
        /// Alle uebrigen frisch gespawnten Wagen werden geloest, damit niemals pauschal die ganze Garnitur bremst.
        /// </summary>
        private static int ApplyPolicy(List<TrainCar> cars, out PolicyState state)
        {
            state = new PolicyState();
            var liveCars = cars?.Where(c => c != null && !c.IsLoco).ToList() ?? new List<TrainCar>();
            state.Loaded = liveCars.Count(c => c.logicCar != null && c.logicCar.CurrentCargoTypeInCar != CargoType.None);
            state.Empty = liveCars.Count - state.Loaded;

            var brakes = liveCars
                .Select(c => new { Car = c, Brake = Reflect.Get<object>(c, "brakeSystem") })
                .Where(x => x.Brake != null && Reflect.Get<bool>(x.Brake, "hasHandbrake"))
                .ToList();
            if (brakes.Count == 0) return 0;

            state.Required = Math.Min(brakes.Count, Math.Max(1,
                (int)Math.Ceiling(state.Empty / 4.0 + state.Loaded / 2.0)));

            var selected = new HashSet<object>();
            if (state.Required == 1)
            {
                selected.Add(brakes[brakes.Count - 1].Brake);
            }
            else
            {
                for (int i = 0; i < state.Required; i++)
                {
                    int index = (int)Math.Round(i * (brakes.Count - 1.0) / (state.Required - 1.0));
                    selected.Add(brakes[index].Brake);
                }
            }

            int changes = 0;
            foreach (var entry in brakes)
            {
                bool shouldBrake = selected.Contains(entry.Brake);
                float current = Reflect.Get<float>(entry.Brake, "handbrakePosition");
                if (shouldBrake ? current >= 0.99f : current <= 0.01f)
                {
                    if (shouldBrake) state.Secured++;
                    continue;
                }
                var method = entry.Brake.GetType().GetMethod("SetHandbrakePosition", AnyInstance, null,
                    new[] { typeof(float), typeof(bool) }, null);
                if (method == null)
                {
                    Main.WarningOnce("spawn-handbrake-method", "BrakeSystem.SetHandbrakePosition(float, bool) not found; spawn handbrake protection is unavailable.");
                    return changes;
                }
                try
                {
                    method.Invoke(entry.Brake, new object[] { shouldBrake ? 1f : 0f, true });
                    changes++;
                    if (shouldBrake) state.Secured++;
                }
                catch (Exception ex)
                {
                    Main.WarningOnce("spawn-handbrake", "Applying the spawn handbrake policy failed: " + (ex.InnerException?.Message ?? ex.Message));
                }
            }
            return changes;
        }
    }
}
