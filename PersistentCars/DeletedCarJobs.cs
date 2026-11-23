using System;
using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;

namespace PersistentCars
{
    /// <summary>
    /// Bugfix des Spiels: Loescht der Spieler einen Wagen, der zu einem Job gehoert (Funkgeraet, im Multiplayer auch
    /// ueber den Host), bleibt der Job im Vanilla-Spiel bestehen und ist nie mehr erfuellbar. Jetzt wird die ganze
    /// Jobkette mit aufgeloest: ein ausliegender Job verschwindet vom Brett, ein angenommener wird abgebrochen.
    /// Die uebrigen Wagen sind danach jobfrei und bekommen zur naechsten vollen Spielstunde eine neue Jobkette.
    /// Nicht beim Verlassen der Welt und nicht beim Debug-Hard-Reset (der raeumt die Jobs selbst ab).
    /// </summary>
    internal static class DeletedCarJobs
    {
        /// <summary>Aus den Loesch-Prefixen von CarSpawner (DeleteTrainCars / DeleteCar), vor dem Loeschen.</summary>
        public static void OnDeleting(IEnumerable<TrainCar> cars, bool worldUnload)
        {
            try
            {
                if (!Main.IsAuthority || worldUnload || WorldInit.HardResetRunning) return;
                if (!WorldStreamingInit.IsLoaded || !AStartGameData.carsAndJobsLoadingFinished) return;
                var jm = JobsManager.Instance;
                if (jm == null || cars == null) return;

                var jobs = new HashSet<Job>();
                foreach (var car in cars)
                {
                    var logic = car != null ? car.logicCar : null;
                    var job = logic != null ? jm.GetJobOfCar(logic) : null;
                    if (job != null) jobs.Add(job);
                }

                foreach (var job in jobs)
                    RemoveJob(job);
            }
            catch (Exception ex) { Main.WarningOnce("deleted-car-jobs", "Removing the job of a deleted car failed: " + ex.Message); }
        }

        private static void RemoveJob(Job job)
        {
            var chain = Reservations.FindChain(job);
            string state = job.State.ToString();

            JobAcceptance.WithExpireAllowed(() =>
            {
                // angenommen: wie Wegwerfen abbrechen (Booklet wird ungueltig)
                if (job.State == JobState.InProgress)
                {
                    var abandon = Reflect.Method(typeof(JobsManager), "AbandonJob");
                    if (abandon != null && abandon.GetParameters().Length == 1) abandon.Invoke(JobsManager.Instance, new object[] { job });
                }

                if (chain == null && job.State == JobState.Available) job.ExpireJob();
            });
            if (chain != null) ChainKiller.Kill(chain, "car deleted");

            Main.Log($"Car deleted: job {job.ID} ({state}) removed together with its job chain - the remaining cars get a new job at the next full game hour.");
        }
    }
}
