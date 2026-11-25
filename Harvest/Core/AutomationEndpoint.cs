using System;
using System.Collections.Generic;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace LocalAutomation;

internal sealed record AutomationActivity(string Step, string Target, long Revision, string State = "Active", long WaitUntil = 0,
    string WaitReason = "", int Retry = 0);

// Per-run evidence: entering a known phase or retrying it is not another successful action.
internal sealed class AutomationProgress
{
    private readonly HashSet<string> checkpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> distances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> waits = new(StringComparer.Ordinal);
    private string step = "", target = "", state = "Active", waitReason = "";
    private long revision, waitUntil;
    private int retry;
    internal AutomationActivity Snapshot => new(step, target, revision, state, waitUntil, waitReason, retry);

    internal void SetStep(string step, string target = "")
    {
        this.step = step; this.target = target;
        state = "Active"; waitUntil = 0; waitReason = "";
        ObserveCheckpoint("step:" + target + "\0" + step);
    }
    internal void ObserveValue(string key, long value)
    {
        if (values.TryGetValue(key, out var previous) && value > previous) revision++;
        values[key] = values.TryGetValue(key, out previous) ? Math.Max(previous, value) : value;
    }
    internal void ObserveDistance(string key, float? remaining, float minimumGain = 3f)
    {
        if (remaining is not { } value || !float.IsFinite(value) || value < 0) return;
        if (!float.IsFinite(minimumGain) || minimumGain <= 0) minimumGain = 3f;
        if (!distances.TryGetValue(key, out var best)) { distances[key] = value; return; }
        if (value <= best - minimumGain) { distances[key] = value; revision++; }
    }
    internal void ObserveCheckpoint(string key) { if (checkpoints.Add(key)) revision++; }
    internal void ObserveTerritory(uint territory) { if (territory != 0) ObserveCheckpoint("territory:" + territory); }
    internal void ObserveWait(string key, string reason, long until)
    {
        var identity = target + "\0" + step + "\0" + key;
        if (!waits.TryGetValue(identity, out var deadline) && until > 0 && until < long.MaxValue)
            waits[identity] = deadline = until;
        state = "Waiting"; waitUntil = deadline; waitReason = reason;
    }
    internal void ObserveRetry(int attempts) => retry = Math.Max(retry, attempts);
}

internal sealed record AutomationObservation(bool Busy, int Completed, int Total, string Phase, string Result = "", string Detail = "", long Progress = 0,
    AutomationActivity? Activity = null);
internal sealed record AutomationWork(Func<bool> Start, Action Stop, Func<AutomationObservation> Observe, Func<bool>? CanCancel = null, Action? Unload = null,
    Action? Disconnect = null, Action<bool, bool>? Pause = null, Func<string>? PauseState = null, Action? RetryCleanup = null,
    bool ResumeWhileOccupied = false, Func<AutomationObservation>? ObserveCancellation = null, Func<bool>? Recover = null);

// Linked into each worker: only the owner token can cancel an operation.
internal sealed class AutomationEndpoint : IDisposable
{
    private readonly ICallGateProvider<string, string> gate;
    private readonly IFramework framework;
    private readonly Func<string, JsonElement, AutomationWork> create;
    private readonly Func<bool> safe;
    private readonly Func<bool> busy;
    private readonly Func<string?>? startBlockReason;
    private readonly Func<ulong> character;
    private readonly Func<object>? candidates;
    private readonly Func<string, bool>? canStart;
    private readonly Action? resetCandidates;
    private readonly Func<long> clock;
    private readonly Dictionary<string, long> activityRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Revision, long Deadline)> activityWaits = new(StringComparer.Ordinal);
    private bool activitySupported, activityInitialized;
    private string activityStep = "", activityTarget = "", activityState = "Active", activityWaitReason = "";
    private long activityRevision, activitySequence, activityStepAt, activityProgressAt, activityClockAt, activityFrozenUntil, activityWaitUntil;
    private int activityRetry, recoveryAttempts;
    private AutomationWork? work;
    private string token = "", kind = "", state = "Idle", detail = "";
    private ulong owner;
    private bool stopIssued;
    private bool stopFailed;
    private bool pauseRequested;
    private bool pauseContinueCurrent;
    private bool cleanupRetryRequested;
    private long nextTick, startedAt, lastProgressAt;
    private int bestCompleted;
    private long bestProgress;
    private AutomationObservation observation = new(false, 0, 0, "Idle");

    internal (string Token, ulong Character, string Kind, bool Active) DisplayOwnership
        => (token, owner, kind, work != null);

    public AutomationEndpoint(IDalamudPluginInterface pi, IFramework framework, string name,
        Func<string, JsonElement, AutomationWork> create, Func<bool> safe, Func<bool> busy, Func<ulong> character, Func<object>? candidates = null,
        Func<string, bool>? canStart = null, Action? resetCandidates = null, Func<long>? clock = null, Func<string?>? startBlockReason = null)
    {
        this.framework = framework; this.create = create; this.safe = safe; this.busy = busy; this.character = character;
        this.candidates = candidates;
        this.canStart = canStart;
        this.startBlockReason = startBlockReason;
        this.resetCandidates = resetCandidates; this.clock = clock ?? (() => Environment.TickCount64);
        gate = pi.GetIpcProvider<string, string>(name + ".AutomationV1");
        gate.RegisterFunc(Handle);
        framework.Update += Tick;
    }

    private string? StartBlockReason()
    {
        if (work != null || busy()) return "Harvest 작업 또는 경로 기록 진행 중";
        if (character() == 0) return "캐릭터 접속 확인 대기";
        return startBlockReason != null ? startBlockReason() : safe() ? null : "Character cannot start safely";
    }

    private string Handle(string json)
    {
        try
        {
            if (!framework.IsInFrameworkUpdateThread)
                return JsonSerializer.Serialize(new { Version = 1, Accepted = false, Error = "Call IPC on the framework thread" });
            using var doc = JsonDocument.Parse(json);
            var request = doc.RootElement;
            var action = request.GetProperty("Action").GetString();
            if (action == "ResetCandidates")
            {
                if (resetCandidates == null || work != null || busy() || character() == 0)
                    return Reply(false, "Candidate reset requires an idle worker and a logged-in character");
                resetCandidates();
                return Reply(true);
            }
            if (action == "Candidates") return JsonSerializer.Serialize(new { Accepted = true, Candidates = candidates?.Invoke() });
            if (action == "CanStart")
                return JsonSerializer.Serialize(new { Version = 1, Accepted = true,
                    Ready = StartBlockReason() == null
                        && canStart?.Invoke(request.GetProperty("Kind").GetString() ?? "") == true });
            if (action == "Start")
            {
                if (StartBlockReason() is { } blocked) return Reply(false, blocked);
                var nextKind = request.GetProperty("Kind").GetString() ?? "";
                var nextWork = create(nextKind, request.TryGetProperty("Arguments", out var args) ? args : default);
                work = nextWork; kind = nextKind; token = Guid.NewGuid().ToString("N"); owner = character();
                state = "Running"; detail = ""; stopIssued = stopFailed = false; pauseRequested = false; pauseContinueCurrent = false; cleanupRetryRequested = false;
                startedAt = lastProgressAt = clock(); bestCompleted = 0; bestProgress = 0;
                activitySupported = activityInitialized = false; activityRevisions.Clear(); activityWaits.Clear();
                activityStep = activityTarget = activityWaitReason = ""; activityState = "Active";
                activityRevision = activitySequence = activityWaitUntil = activityFrozenUntil = 0; activityRetry = recoveryAttempts = 0;
                try
                {
                    if (!nextWork.Start())
                    {
                        if (busy()) { state = "Cancelling"; detail = "Worker rejected start after acquiring control"; return Reply(true); }
                        work = null; state = "Rejected";
                        return Reply(false, "Worker rejected start; check its prerequisites");
                    }
                    Sample();
                }
                catch (Exception ex) { state = "Cancelling"; detail = "Start failed: " + ex.Message; return Reply(true); }
            }
            else if (action is "Pause" or "Resume")
            {
                if (work == null || !request.TryGetProperty("Token", out var supplied) || supplied.GetString() != token
                    || character() != owner || owner == 0)
                    return Reply(false, "Operation token does not own the active work");
                if (work.Pause == null) return Reply(false, "Pause is not supported for this work");
                if (state == "Cancelling") return Reply(false, "Work is cancelling");
                var requested = action == "Pause";
                var continueCurrent = requested && request.TryGetProperty("ContinueCurrent", out var keep) && keep.GetBoolean();
                if (requested != pauseRequested || continueCurrent != pauseContinueCurrent)
                {
                    AdvanceActivityClock(clock());
                    // Release an existing queue/controller hold before restoring keep-working mode.
                    if (pauseRequested && requested && continueCurrent) work.Pause(false, false);
                    work.Pause(requested, continueCurrent);
                    pauseRequested = requested;
                    pauseContinueCurrent = continueCurrent;
                    // A deliberate hold must not consume the next active stall window.
                    if (!requested) lastProgressAt = clock();
                    AdvanceActivityClock(clock());
                }
            }
            else if (action == "Recover")
            {
                if (work == null || !request.TryGetProperty("Token", out var supplied) || supplied.GetString() != token
                    || character() != owner || owner == 0 || state != "Running" || pauseRequested)
                    return Reply(false, "Operation token does not own recoverable active work");
                if (work.Recover == null) return Reply(false, "Recovery is not supported for this work");
                recoveryAttempts++;
                if (!work.Recover()) return Reply(false, "Worker could not safely recover the current step");
            }
            else if (action == "RetryCleanup")
            {
                if (work == null || !request.TryGetProperty("Token", out var supplied) || supplied.GetString() != token
                    || character() != owner || owner == 0 || state != "Cancelling")
                    return Reply(false, "Operation token does not own cancelling work");
                if (work.RetryCleanup == null) return Reply(false, "Cleanup retry is not supported");
                // Reserve the recipe boundary even if an earlier Cancel arrived without a pause.
                if (kind == "CraftingLog") work.Pause?.Invoke(true, false);
                cleanupRetryRequested = true;
            }
            else if (action == "Cancel")
            {
                if (!request.TryGetProperty("Token", out var supplied) || supplied.GetString() != token || work == null
                    || character() != owner || owner == 0)
                    return Reply(false, "Operation token does not own the active work");
                if (kind == "Achievement") work.Pause?.Invoke(true, false);
                // Only an explicit owned retry may repeat a failed stop; a failed observation must not repeat it.
                if (stopFailed) { stopIssued = false; stopFailed = false; }
                pauseRequested = false;
                state = "Cancelling";
                detail = request.TryGetProperty("Reason", out var reason) ? reason.GetString() ?? "Cancelled" : "Cancelled";
            }
            else if (action != "Status") return Reply(false, "Unknown action");
            return Reply(true);
        }
        catch (Exception ex) { return Reply(false, ex.Message); }
    }

    private void Tick(IFramework _)
    {
        if (work == null || clock() < nextTick) return;
        nextTick = clock() + 500;
        try
        {
            if (character() != owner)
            {
                if (character() == 0 && work.Disconnect != null)
                {
                    // Clear the old execution while no character can receive its queued actions.
                    work.Disconnect();
                    work = null; state = "Cancelled"; detail = "Character disconnected; owned execution cleared";
                    observation = observation with { Busy = false };
                    return;
                }
                state = "Blocked"; detail = "원래 캐릭터로 접속해야 이전 작업을 안전하게 정리할 수 있습니다.";
                return;
            }
            if (state == "Blocked") { state = "Cancelling"; detail = "Original character returned; cleaning up owned work"; }
            if (state == "Cancelling")
            {
                // Do not tear down a synthesis, cast, gathering interaction, or live combat.
                if (!(work.CanCancel?.Invoke() ?? safe())) {
                    if (work.ObserveCancellation != null) Sample(work.ObserveCancellation);
                    return;
                }
                if (cleanupRetryRequested) {
                    cleanupRetryRequested = false; stopIssued = true;
                    work.RetryCleanup!();
                    stopFailed = false;
                }
                else if (!stopIssued) {
                    stopIssued = true;
                    try { work.Stop(); }
                    catch { stopFailed = true; throw; }
                }
                Sample();
                if (!observation.Busy) { work = null; state = "Cancelled"; }
                return;
            }
            Sample();
            if (!observation.Busy)
            {
                if (pauseRequested) { work.Pause?.Invoke(false, false); pauseRequested = false; }
                state = string.IsNullOrEmpty(observation.Result) ? "Failed" : observation.Result;
                detail = string.IsNullOrEmpty(observation.Detail) ? "Worker ended without a verified result" : observation.Detail;
                work = null;
            }
        }
        catch (Exception ex)
        {
            // A failed probe must retain ownership until cancellation is acknowledged.
            state = "Cancelling"; detail = "Worker error: " + ex.Message;
        }
    }

    private void Sample(Func<AutomationObservation>? observe = null)
    {
        if (work == null) return;
        observation = (observe ?? work.Observe)();
        if (observation.Completed > bestCompleted || observation.Progress > bestProgress)
            lastProgressAt = clock();
        bestCompleted = Math.Max(bestCompleted, observation.Completed);
        bestProgress = Math.Max(bestProgress, observation.Progress);
        ObserveActivity(observation.Activity, clock());
    }

    private void ObserveActivity(AutomationActivity? activity, long now)
    {
        AdvanceActivityClock(now);
        if (activity == null || string.IsNullOrWhiteSpace(activity.Step))
        {
            // Losing telemetry cannot leave an old valid wait lease active.
            activitySupported = false; activityWaitUntil = 0;
            AdvanceActivityClock(now);
            return;
        }
        activitySupported = true;
        if (activity.State == "Unknown")
        {
            if (!activityInitialized)
            {
                activityStep = activity.Step; activityTarget = activity.Target;
                activityStepAt = activityProgressAt = now;
                activityInitialized = true;
            }
            activityState = "Unknown"; activityWaitUntil = 0; activityWaitReason = "";
            AdvanceActivityClock(now);
            return;
        }
        var key = activity.Target + "\0" + activity.Step;
        var newStep = !activityRevisions.TryGetValue(key, out var revision);
        var progressed = newStep || activity.Revision > revision && activity.Revision > activityRevision;
        if (progressed) activitySequence++;
        if (newStep || !activityInitialized) activityStepAt = now;
        if (progressed || !activityInitialized) activityProgressAt = now;
        activityRevisions[key] = newStep ? activity.Revision : Math.Max(revision, activity.Revision);
        activityInitialized = true;
        var previousState = activityState;
        var previousWaitKey = activityTarget + "\0" + activityStep + "\0" + activityWaitReason;
        activityStep = activity.Step; activityTarget = activity.Target; activityRevision = activity.Revision;
        activityState = activity.State == "Paused" ? "Active" : activity.State;
        activityRetry = Math.Max(0, activity.Retry);
        activityWaitUntil = 0; activityWaitReason = activity.WaitReason;
        if (activity.State == "Waiting" && !string.IsNullOrWhiteSpace(activity.WaitReason) && activity.WaitUntil > 0 && activity.WaitUntil < long.MaxValue)
        {
            var waitKey = key + "\0" + activity.WaitReason;
            if (!activityWaits.TryGetValue(waitKey, out var wait)
                || (previousState != "Waiting" || previousWaitKey != waitKey) && activity.Revision > wait.Revision)
                activityWaits[waitKey] = wait = (activity.Revision, activity.WaitUntil);
            else if (activity.WaitUntil < wait.Deadline)
                activityWaits[waitKey] = wait = (wait.Revision, activity.WaitUntil);
            activityWaitUntil = wait.Deadline;
        }
        AdvanceActivityClock(now);
    }

    private void AdvanceActivityClock(long now)
    {
        if (!activityInitialized) { activityClockAt = now; return; }
        if (activityFrozenUntil > activityClockAt)
        {
            var held = Math.Max(0, Math.Min(now, activityFrozenUntil) - activityClockAt);
            activityStepAt += held; activityProgressAt += held;
        }
        activityClockAt = now;
        var paused = character() != owner || pauseRequested && !pauseContinueCurrent && work?.PauseState?.Invoke() == "Paused";
        var unknown = activityInitialized && (!activitySupported || activityState == "Unknown");
        activityFrozenUntil = paused || unknown ? long.MaxValue
            : activitySupported && activityState == "Waiting" && activityWaitUntil > now ? activityWaitUntil : 0;
    }

    private string Reply(bool accepted, string error = "")
    {
        var now = clock();
        AdvanceActivityClock(now);
        var reportedActivityState = state == "Cancelling" ? "Cleanup" : character() != owner && work != null ? "Blocked"
            : pauseRequested && !pauseContinueCurrent && work?.PauseState?.Invoke() == "Paused" ? "Paused"
            : activityInitialized && !activitySupported ? "Unknown" : activityState;
        return JsonSerializer.Serialize(new
        {
            Version = 1, Accepted = accepted, Error = error, Token = token, Kind = kind, State = state,
            PauseSupported = work?.Pause != null, PauseRequested = pauseRequested,
            PauseState = pauseRequested ? work?.PauseState?.Invoke() ?? "Pending" : "None",
            ResumeWhileOccupied = work?.ResumeWhileOccupied == true,
            CleanupRetrySupported = work?.RetryCleanup != null, CleanupRetryPending = cleanupRetryRequested,
            RecoverySupported = work?.Recover != null, RecoveryAttempts = recoveryAttempts,
            Busy = work != null || busy(), SafeToYield = safe(), CharacterId = character(), OwnerCharacterId = owner,
            Completed = observation.Completed, Total = observation.Total, Phase = observation.Phase,
            Detail = state == "Running" ? observation.Detail : detail, ElapsedSeconds = token.Length == 0 ? 0 : Math.Max(0, now - startedAt) / 1000,
            StalledSeconds = token.Length == 0 ? 0 : Math.Max(0, now - lastProgressAt) / 1000,
            ActivityVersion = activityInitialized ? 1 : 0,
            Activity = activitySupported ? new AutomationActivity(activityStep, activityTarget, activitySequence, reportedActivityState,
                activityWaitUntil, activityWaitReason, activityRetry) : null,
            ActivityState = reportedActivityState,
            StepKey = activityStep, TargetKey = activityTarget, ActivityRevision = activitySequence,
            StepElapsedSeconds = !activityInitialized ? 0 : Math.Max(0, now - activityStepAt) / 1000,
            StepStalledSeconds = !activityInitialized ? 0 : Math.Max(0, now - activityProgressAt) / 1000,
            WaitUntil = activityWaitUntil, WaitReason = activityWaitReason, ActivityRetry = activityRetry
        });
    }

    public void Dispose()
    {
        framework.Update -= Tick;
        gate.UnregisterFunc();
        // Unload cannot wait for game actions; never issue an unsafe or cross-character stop.
        if (work != null && character() == owner && (work.CanCancel?.Invoke() ?? safe()))
            (work.Unload ?? work.Stop)();
    }
}
