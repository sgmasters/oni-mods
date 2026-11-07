using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Klei.AI;
using UnityEngine;

namespace DupeBrainBoost
{
    /// <summary>
    /// Replacement for the duplicant BrainGroup update.
    ///
    /// Vanilla: exactly ONE dupe brain per rendered frame, and every other frame is reserved
    /// for the "priority" queue. With 200 dupes at 30 FPS each dupe re-thinks every ~7-13 real
    /// seconds, i.e. 1-2 in-game minutes at 9x speed.
    ///
    /// Here, per frame:
    ///   1. Dupes in danger (low breath / scalding / freezing) that have not thought for
    ///      UrgentIntervalSeconds of game time.
    ///   2. The vanilla priority queue (dupes that just finished or lost a chore).
    ///   3. Dupes with no chore at all.
    ///   4. Round-robin over everyone else, enough to reach TargetIntervalSeconds of game time
    ///      per dupe given the current game speed.
    /// All of it limited by MaxMillisecondsPerFrame of CPU time.
    /// </summary>
    internal static class DupeScheduler
    {
        private static readonly AccessTools.FieldRef<BrainScheduler.BrainGroup, List<Brain>> BrainsRef =
            AccessTools.FieldRefAccess<BrainScheduler.BrainGroup, List<Brain>>("brains");

        private static readonly AccessTools.FieldRef<BrainScheduler.BrainGroup, Queue<Brain>> PriorityRef =
            AccessTools.FieldRefAccess<BrainScheduler.BrainGroup, Queue<Brain>>("priorityBrains");

        private static readonly AccessTools.FieldRef<BrainScheduler.BrainGroup, int> NextRef =
            AccessTools.FieldRefAccess<BrainScheduler.BrainGroup, int>("nextUpdateBrain");

        private sealed class Info
        {
            public float lastThink = float.NegativeInfinity;
            public int lastFrame = -1;
            public double lastCostTicks;              // cost of this dupe's previous decision
            public float notBefore = float.NegativeInfinity; // cooldown after an expensive decision (game time)
            public bool cached;
            public AmountInstance breath;
            public ScaldingMonitor.Instance scalding;
            public ChoreDriver driver;
        }

        private static readonly Dictionary<Brain, Info> infos = new Dictionary<Brain, Info>();
        private static readonly List<Brain> urgent = new List<Brain>();
        private static readonly List<Brain> requeue = new List<Brain>();
        private static readonly List<Brain> pruneScratch = new List<Brain>();
        private static readonly HashSet<Brain> aliveScratch = new HashSet<Brain>();

        private sealed class StalestFirst : IComparer<Brain>
        {
            public static readonly StalestFirst Instance = new StalestFirst();
            public int Compare(Brain a, Brain c) => GetInfo(a).lastThink.CompareTo(GetInfo(c).lastThink);
        }
        private static readonly Stopwatch watch = new Stopwatch();
        private static int framesSincePrune;

        // ---- stats (since last log line) ----
        private static int statFrames, statUpdates, statUrgent, statPriority, statIdle, statBudgetHits;
        private static double statMs;
        private static float statMaxStale;
        private static float nextLogAt;

        /// <summary>Clear state when a save is loaded.</summary>
        public static void Reset()
        {
            infos.Clear();
            urgent.Clear();
            framesSincePrune = 0;
            UpdateProfiler.LogAndReset();
            ResetStats();
            nextLogAt = Time.unscaledTime + Mathf.Max(5f, BoostConfig.Instance.LogStatsEverySeconds);
        }

        private static void ResetStats()
        {
            statFrames = statUpdates = statUrgent = statPriority = statIdle = statBudgetHits = 0;
            statMs = 0;
            statMaxStale = 0f;
            statMaxThinkMs = 0;
            statSlow = statSkippedExpensive = 0;
            statSlowMs = 0;
            hitches = 0;
            hitchMaxMs = hitchOursMs = hitchProberMs = hitchCritterMs = hitchGapReal = hitchGapGame = 0;
            CritterTiming.Reset();
            hitchGaps = 0;
            ProberTiming.TotalMs = 0;
            ProberTiming.Calls = 0;
        }

        // ---- hitch detector ----
        // A "hitch" = a frame much longer than usual. We record how much of that frame was ours
        // (decisions + path-probe bookkeeping) and how regularly hitches repeat in real and game time.
        // Hitches every ~1.0 GAME second point at the game's own once-per-second updates (Sim1000ms).
        private static float smoothFrameMs = 33f;
        private static double lastFrameOursMs, lastFrameProberMs;
        private static int hitches, hitchGaps;
        private static double hitchMaxMs, hitchOursMs, hitchProberMs, hitchCritterMs, hitchGapReal, hitchGapGame;
        private static float lastHitchReal = -1f, lastHitchGame = -1f;

        private static void TrackHitch(float gameNow)
        {
            float frameMs = Time.unscaledDeltaTime * 1000f;
            bool hitch = frameMs > Mathf.Max(60f, smoothFrameMs * 2.5f);
            if (hitch)
            {
                hitches++;
                if (frameMs > hitchMaxMs) hitchMaxMs = frameMs;
                hitchOursMs += lastFrameOursMs;
                hitchProberMs += lastFrameProberMs;
                if (CritterTiming.LastFrame == Time.frameCount - 1) hitchCritterMs += CritterTiming.LastMs;
                float realNow = Time.unscaledTime;
                if (lastHitchReal >= 0f)
                {
                    hitchGaps++;
                    hitchGapReal += realNow - lastHitchReal;
                    hitchGapGame += gameNow - lastHitchGame;
                }
                lastHitchReal = realNow;
                lastHitchGame = gameNow;
            }
            else
            {
                smoothFrameMs = smoothFrameMs * 0.95f + frameMs * 0.05f;
            }
            lastFrameProberMs = ProberTiming.LastMs;
        }

        public static void Update(BrainScheduler.BrainGroup group)
        {
            var cfg = BoostConfig.Instance;
            TrackHitch(GameClock.Instance != null ? GameClock.Instance.GetTime() : Time.time);
            List<Brain> brains = BrainsRef(group);
            Queue<Brain> priority = PriorityRef(group);
            int count = brains.Count;
            if (count == 0)
            {
                priority.Clear();
                return;
            }

            float now = GameClock.Instance != null ? GameClock.Instance.GetTime() : Time.time;
            int frame = Time.frameCount;
            // Game time advanced this frame; the sim itself caps a frame at 0.2 s.
            float gameDt = Mathf.Min(Time.deltaTime, 0.2f);
            int quota = Mathf.Clamp(Mathf.CeilToInt(count * gameDt / cfg.TargetIntervalSeconds),
                cfg.MinBrainsPerFrame, cfg.EffectiveMaxBrainsPerFrame);
            long budgetTicks = (long)(cfg.EffectiveBudgetMs * Stopwatch.Frequency / 1000.0);

            watch.Restart();
            int done = 0;
            frameDone = 0;
            frameBudgetTicks = budgetTicks;
            int next = NextRef(group);
            int rr = 0;

            // 0. One regular round-robin decision every frame, no matter what (vanilla's guarantee),
            //    so nobody can be starved by the urgent / after-chore queues.
            RoundRobin(brains, ref next, ref done, ref rr, 1, long.MaxValue, now, frame);

            // 1. Danger scan (cheap: a few field reads per dupe). Most-stale first.
            urgent.Clear();
            for (int i = 0; i < count; i++)
            {
                Brain b = brains[i];
                if (b == null) continue;
                Info info = GetInfo(b);
                if (now - info.lastThink >= cfg.UrgentIntervalSeconds && InDanger(info, cfg))
                    urgent.Add(b);
            }
            if (urgent.Count > 1)
                urgent.Sort(StalestFirst.Instance); // reused comparer: List.Sort(Comparison) would allocate every frame
            // Danger gets at most UrgentBudgetShare of the frame budget (but always at least one decision).
            long urgentTicks = (long)(budgetTicks * cfg.UrgentBudgetShare);
            int urgentDone = 0;
            for (int i = 0; i < urgent.Count; i++)
            {
                if (urgentDone > 0 && watch.ElapsedTicks + (long)emaThinkTicks >= urgentTicks) break;
                if (Think(urgent[i], now, frame)) { done++; urgentDone++; statUrgent++; }
            }

            // 2. Vanilla priority queue: dupes that just ended a chore (are standing idle).
            int pending = priority.Count;
            while (pending-- > 0 && priority.Count > 0 && !OverBudget(budgetTicks, done))
            {
                Brain pb = priority.Dequeue();
                if (Think(pb, now, frame)) { done++; statPriority++; }
                else if (pb != null && pb.IsRunning() && GetInfo(pb).lastFrame != frame) requeue.Add(pb);
            }

            foreach (var pb in requeue) priority.Enqueue(pb); // try again next frame
            requeue.Clear();

            // 3. Dupes with no chore at all.
            for (int i = 0; i < count && i < brains.Count && !OverBudget(budgetTicks, done); i++)
            {
                Brain b = brains[i];
                if (b == null) continue;
                Info info = GetInfo(b);
                if (info.driver != null && !info.driver.HasChore() && now - info.lastThink >= cfg.UrgentIntervalSeconds)
                {
                    if (Think(b, now, frame)) { done++; statIdle++; }
                }
            }

            // 4. Round robin for everyone else, up to the quota for this game speed.
            if (!RoundRobin(brains, ref next, ref done, ref rr, quota, budgetTicks, now, frame))
                statBudgetHits++;
            NextRef(group) = brains.Count > 0 ? next % brains.Count : 0;

            watch.Stop();
            statFrames++;
            statUpdates += done;
            statMs += watch.Elapsed.TotalMilliseconds;
            lastFrameOursMs = watch.Elapsed.TotalMilliseconds;

            if (++framesSincePrune >= 600) Prune(brains);
            MaybeLog(count, quota);
        }

        /// <summary>Round-robin decisions until <paramref name="untilRr"/> total; false if stopped by the budget.</summary>
        private static bool RoundRobin(List<Brain> brains, ref int next, ref int done, ref int rr, int untilRr,
            long budgetTicks, float now, int frame)
        {
            int count = brains.Count;
            int visited = 0;
            while (rr < untilRr && visited < count && brains.Count > 0)
            {
                if (budgetTicks != long.MaxValue && OverBudget(budgetTicks, done)) return false;
                if (next >= brains.Count || next < 0) next = 0;
                Brain b = brains[next];
                next++;
                visited++;
                if (b == null) continue;
                Info info = GetInfo(b);
                if (info.lastFrame == frame) continue; // already thought this frame
                float stale = now - info.lastThink;
                if (!float.IsInfinity(stale) && stale > statMaxStale && b.IsRunning()) statMaxStale = stale;
                if (Think(b, now, frame)) { done++; rr++; }
            }
            return true;
        }

        /// <summary>
        /// Always allow at least MinBrainsPerFrame decisions. After that, stop if the NEXT decision is
        /// expected to cross the budget (single decisions can cost several ms in big colonies, so
        /// checking only the time already spent overshoots badly).
        /// </summary>
        private static bool OverBudget(long budgetTicks, int done)
        {
            if (done < BoostConfig.Instance.MinBrainsPerFrame) return false;
            return watch.ElapsedTicks + (long)emaThinkTicks >= budgetTicks;
        }

        // Smoothed cost of one decision, biased upward so spikes count for more than dips.
        private static double emaThinkTicks = Stopwatch.Frequency / 2000.0; // start at 0.5 ms
        private static double statMaxThinkMs;

        // Per-frame context for Think (set at the start of Update).
        private static int lastGcCount;
        private static int frameDone;
        private static long frameBudgetTicks;
        private static int statSlow, statSkippedExpensive;
        private static double statSlowMs;

        /// <summary>
        /// Runs one dupe's decision, unless:
        ///  - the dupe is cooling down after an expensive decision (never more often than the base game would), or
        ///  - its previous decision was so costly that running it now would blow this frame's budget
        ///    (it then waits for a frame where it can be the first decision).
        /// </summary>
        private static bool Think(Brain b, float now, int frame)
        {
            if (b == null || !b.IsRunning()) return false;
            Info info = GetInfo(b);
            if (info.lastFrame == frame) return false;
            if (now < info.notBefore) return false;
            if (frameDone > 0 && info.lastCostTicks > 0 && watch.ElapsedTicks + (long)info.lastCostTicks > frameBudgetTicks)
            {
                statSkippedExpensive++;
                return false;
            }
            info.lastFrame = frame;
            info.lastThink = now;
            long t0 = watch.ElapsedTicks;
            b.UpdateBrain();
            double cost = watch.ElapsedTicks - t0;
            emaThinkTicks = cost > emaThinkTicks ? emaThinkTicks * 0.7 + cost * 0.3 : emaThinkTicks * 0.95 + cost * 0.05;
            double ms = cost * 1000.0 / Stopwatch.Frequency;
            if (ms > statMaxThinkMs) statMaxThinkMs = ms;
            info.lastCostTicks = cost;
            var cfg = BoostConfig.Instance;
            if (ms >= cfg.ExpensiveDecisionMs)
            {
                info.notBefore = now + cfg.ExpensiveCooldownSeconds;
                statSlow++;
                statSlowMs += ms;
            }
            frameDone++;
            return true;
        }

        private static Info GetInfo(Brain b)
        {
            if (!infos.TryGetValue(b, out Info info))
            {
                info = new Info();
                infos[b] = info;
            }
            if (!info.cached)
            {
                info.cached = true;
                GameObject go = b.gameObject;
                try { info.breath = Db.Get().Amounts.Breath.Lookup(go); } catch { info.breath = null; }
                info.scalding = go.GetSMI<ScaldingMonitor.Instance>();
                info.driver = b.GetComponent<ChoreDriver>();
            }
            return info;
        }

        private static bool InDanger(Info info, BoostConfig cfg)
        {
            try
            {
                var breath = info.breath;
                if (breath != null)
                {
                    float max = breath.GetMax();
                    if (max > 0f && breath.value < max * cfg.LowBreathFraction) return true;
                }
                var sc = info.scalding;
                if (sc != null && !sc.IsNullOrStopped() && (sc.IsScalding() || sc.IsScolding())) return true;
            }
            catch (Exception)
            {
                // Missing components on unusual dupes (e.g. bionic) - treat as not in danger.
            }
            return false;
        }

        private static void Prune(List<Brain> brains)
        {
            framesSincePrune = 0;
            var alive = aliveScratch;
            alive.Clear();
            for (int i = 0; i < brains.Count; i++) alive.Add(brains[i]);
            pruneScratch.Clear();
            foreach (var kv in infos)
                if (kv.Key == null || !alive.Contains(kv.Key)) pruneScratch.Add(kv.Key);
            foreach (var b in pruneScratch) infos.Remove(b);
            pruneScratch.Clear();
        }

        private static void MaybeLog(int count, int quota)
        {
            float every = BoostConfig.Instance.LogStatsEverySeconds;
            if (every <= 0f || Time.unscaledTime < nextLogAt) return;
            nextLogAt = Time.unscaledTime + every;
            if (statFrames == 0) return;
            Debug.LogFormat(
                "[DupeBrainBoost] dupes={0} speed={1:0.##}x fps={2:0} | decisions/frame avg={3:0.0} (quota now {4}) | " +
                "urgent={5} afterChore={6} idle={7} | cpu avg={8:0.00}ms/frame, budget hit {9}/{10} frames | " +
                "longest wait between decisions={11:0.0} game-s | one decision avg={12:0.00}ms max={13:0.0}ms | " +
                "slow decisions (>{14:0}ms)={15} costing {16:0}ms, deferred for budget={17} | GC runs={18}, managed heap {19:0}MB | safe-spot searches={20} ({21:0}ms, skipped {22}) | sensors run={23} skipped={24}",
                count, Time.timeScale, statFrames / Mathf.Max(0.001f, every),
                (float)statUpdates / statFrames, quota,
                statUrgent, statPriority, statIdle,
                statMs / statFrames, statBudgetHits, statFrames,
                statMaxStale,
                statUpdates > 0 ? statMs / statUpdates : 0.0, statMaxThinkMs,
                BoostConfig.Instance.ExpensiveDecisionMs, statSlow, statSlowMs, statSkippedExpensive,
                System.GC.CollectionCount(0) - lastGcCount, System.GC.GetTotalMemory(false) / 1048576.0,
                SafeCellThrottle.Runs, SafeCellThrottle.Ms, SafeCellThrottle.Skipped,
                SensorThrottle.Runs, SensorThrottle.Skipped);
            SensorThrottle.Runs = SensorThrottle.Skipped = 0;
            lastGcCount = System.GC.CollectionCount(0);
            SafeCellThrottle.ResetStats();
            Debug.LogFormat(
                "[DupeBrainBoost] hitches={0} (frames >{1:0}ms, longest {2:0}ms) | in those frames: decisions {3:0.0}ms, path-probe bookkeeping {4:0.0}ms " +
                "| hitch every {5:0.00} real-s = {6:0.00} game-s | path-probe bookkeeping avg {7:0.00}ms/frame " +
                "| critter brains: {8:0.0}ms in hitch frames, avg {9:0.00}ms/frame, worst {10:0}ms, critters on background probing {11}",
                hitches, Mathf.Max(60f, smoothFrameMs * 2.5f), hitchMaxMs,
                hitches > 0 ? hitchOursMs / hitches : 0.0, hitches > 0 ? hitchProberMs / hitches : 0.0,
                hitchGaps > 0 ? hitchGapReal / hitchGaps : 0.0, hitchGaps > 0 ? hitchGapGame / hitchGaps : 0.0,
                ProberTiming.Calls > 0 ? ProberTiming.TotalMs / ProberTiming.Calls : 0.0,
                hitches > 0 ? hitchCritterMs / hitches : 0.0,
                CritterTiming.Frames > 0 ? CritterTiming.TotalMs / CritterTiming.Frames : 0.0, CritterTiming.MaxMs, CritterAsync.Count);
            UpdateProfiler.LogAndReset();
            ResetStats();
        }
    }
}
