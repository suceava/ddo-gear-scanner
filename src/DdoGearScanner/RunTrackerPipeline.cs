using System.IO;
using DdoGearScanner.Model;
using DdoGearScanner.Vision;
using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace DdoGearScanner;

/// <summary>A screen region expressed as fractions of the game window (x0,y0 = top-left,
/// x1,y1 = bottom-right, each 0..1). Kept resolution-independent so the same defaults work at any
/// window size; stored in settings so they can be field-tuned without a rebuild.</summary>
public readonly record struct RegionRatios(double X0, double Y0, double X1, double Y1);

/// <summary>
/// Watches the capture stream for dungeon runs and logs them to <see cref="RunStore"/>. A SECOND
/// subscriber to CaptureCoordinator.FrameArrived, alongside the gear <see cref="CapturePipeline"/> — it
/// never touches the gear path.
///
/// DDO shows the current AREA NAME (public hub OR quest name) in the tracker region; we OCR it each
/// throttled tick. A name that is NOT a known public hub means "in a dungeon" → start a run (debounced
/// so a one-frame OCR glitch can't spawn a run). Walking back into a hub, or the tracker going empty for
/// a few ticks, ends the run. The reward-panel region gives name+difficulty+XP at completion. All fields
/// are best-effort and user-editable, so OCR misses cost an edit, not a lost run.
///
/// TUNING: with DebugDumpCrops on (default), each ~5s this writes the two region crops + OCR'd text to
/// %APPDATA%\DdoGearScanner\run-debug\ and logs every tracker read — that's how the regions get dialed in.
/// </summary>
public sealed class RunTrackerPipeline
{
    // Read cadence. Regions are small (calibrated), so we can read often — needed to catch a brief
    // "Status: Completed" on quests that finish-and-teleport instantly.
    private const long IntervalMs = 300;
    // A candidate quest name must be read this many consecutive ticks before we act (glitch guard).
    private const int StartDebounce = 2;
    // An active run is only auto-ended by a hub; a blank tracker is tolerated this long (dungeon loads /
    // porting) before treating it as stale (logout/crash cleanup).
    private const long StaleEmptyMs = 5 * 60 * 1000;
    // A run must last at least this long before a chat "Adventure Completed" can finish it — guards against
    // a stale completion message still scrolled in the chat instantly finishing a just-started run.
    private const double MinRunSeconds = 12;
    // Backstop: clear a completed run's card after this long even if we never detect the zone change.
    private const double CompletedCardSeconds = 25;
    // When idle, still OCR the reward/entry region every Nth tick so we catch the entry popup and a
    // completion whose start we missed. Now that big-region OCR is native-res (fast), we can afford this
    // more often.
    private const int IdleCompletionEvery = 2;
    private const long DumpIntervalMs = 5000;

    // DDO public hubs (and a few common non-quest zones). Stored as alphanumeric-only keys because the
    // quest-tracker TITLE is in an ornate font whose word gaps OCR inconsistently as space / underscore /
    // nothing ("The Harbor" -> "The_Harbor" / "TheHarbor") — so we compare on the letters only. Anything
    // NOT here is treated as a dungeon; extend as false "runs" show up (explorer/wilderness areas aren't
    // all listed, so they may create deletable false runs for now).
    private static readonly HashSet<string> PublicZones = new(StringComparer.Ordinal)
    {
        "theharbor", "harbor", "themarketplace", "marketplace", "housedeneith", "housejorasco",
        "housekundarak", "housephiarlan", "housecannith", "thetwelve", "korthosvillage",
        "korthosisland", "korthos", "stormreach", "eberron", "thehallofheroes", "thewaywardlobster",
        "fatespinner", "theceruleanhills",
    };

    private readonly EntryPopupReader _entry;
    private readonly QuestTrackerReader _tracker;
    private readonly ChatLogReader _chat;
    private readonly RunStore _store;
    private readonly Func<(string? Id, int? Level)> _character;
    private RegionRatios _trackerRegion;
    private RegionRatios _completionRegion;   // the calibrated quest-entry-popup box
    private RegionRatios _chatRegion;         // the calibrated chat-log box (completion signal)

    private readonly object _lock = new();
    private volatile bool _enabled = true;
    private int _busy;                 // 0/1 guard: at most one OCR read in flight
    private long _lastTick;
    private long _lastDumpTick;
    private int _idleTick;

    // The adventure-entry popup names the quest in a clean font (the tracker title is unreadable). We
    // hold it only long enough to cover a loading screen and attach it when the dungeon appears; a Cancel
    // clears it right away (see the "stayed put" case below), and this is just the backstop.
    private const long PendingEntryTtlMs = 45 * 1000;
    // While the popup is on screen you are still at the entrance (often standing in a non-hub wilderness),
    // NOT inside — so a run must not start until the popup has been gone this long (you clicked Enter).
    // Must exceed the popup re-read interval so a gap between reads isn't mistaken for "gone".
    private const long PopupGoneMs = 5000;

    // State (guarded by _lock).
    private RunRecord? _current;       // the in-progress run, not yet in the store
    private long _emptySinceTick;      // when the tracker went blank while in a run (0 = not blank)
    private IReadOnlyList<string> _lastChatLines = Array.Empty<string>();  // previous chat lines (to spot new arrivals by the shift)
    private int _pendingCount;         // consecutive "entered a non-hub area" ticks while armed by a popup
    private string? _lastFinalizedName;
    private bool _sawEmptySinceFinalize = true;
    private string? _lastLoggedName;   // debug: only log a tracker read when it changes
    private QuestEntry? _pendingEntry; // last quest-entry popup seen, awaiting a run to attach to
    private long _pendingEntryTick;
    private string? _armedArea;        // the area key you stood in while the popup was up (to detect Enter vs Cancel)
    private string? _shownEntryName;   // the quest name currently shown on the "ready" card (null = not shown)

    /// <summary>Raised when a run is written to the store (completed or abandoned).</summary>
    public event Action<RunRecord>? RunFinalized;
    /// <summary>Raised when the in-progress run changes (new run started, or ended → null).</summary>
    public event Action<RunRecord?>? CurrentChanged;
    /// <summary>Raised when the adventure-entry popup is captured (before entering) — lets the UI show
    /// "quest ready" feedback. Fires with null when the held entry is consumed/cleared.</summary>
    public event Action<QuestEntry?>? EntryHeld;
    /// <summary>Debug: the chat OCR each read — (all lines, the lines detected as newly-arrived).</summary>
    public event Action<IReadOnlyList<string>, IReadOnlyList<string>>? ChatDebug;

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ddo-gear-scanner.log");
    private static void Log(string m) { try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [runs] {m}{Environment.NewLine}"); } catch { } }

    public RunTrackerPipeline(
        EntryPopupReader entry, QuestTrackerReader tracker, ChatLogReader chat, RunStore store,
        Func<(string? Id, int? Level)> character, RegionRatios trackerRegion, RegionRatios completionRegion, RegionRatios chatRegion)
    {
        _entry = entry;
        _tracker = tracker;
        _chat = chat;
        _store = store;
        _character = character;
        _trackerRegion = trackerRegion;
        _completionRegion = completionRegion;
        _chatRegion = chatRegion;
    }

    public bool Enabled => _enabled;
    public RunRecord? Current { get { lock (_lock) { return _current; } } }

    public void SetEnabled(bool on) => _enabled = on;
    public bool Toggle() { _enabled = !_enabled; return _enabled; }

    /// <summary>Apply freshly-calibrated regions live (no restart). Volatile writes; picked up next frame.</summary>
    public void SetRegions(RegionRatios tracker, RegionRatios completion, RegionRatios chat)
    {
        lock (_lock) { _trackerRegion = tracker; _completionRegion = completion; _chatRegion = chat; }
    }

    /// <summary>Wired to CaptureCoordinator.FrameArrived. Throttled; only one OCR read runs at a time.
    /// The frame is owned by the caller, so region crops are cloned before going off-thread.</summary>
    public void OnFrame(OpenCvMat frame)
    {
        if (!_enabled || frame.Empty()) return;

        long now = Environment.TickCount64;
        if (now - _lastTick < IntervalMs) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // a read is already running
        _lastTick = now;

        bool runActive;
        RegionRatios trackerRegion, completionRegion, chatRegion;
        lock (_lock) { runActive = _current is not null; trackerRegion = _trackerRegion; completionRegion = _completionRegion; chatRegion = _chatRegion; }
        // Idle: scan the popup region (the popup only appears before entering). In a run: scan the chat
        // region for "Adventure Completed" — the reliable, persistent completion signal.
        bool readCompletion = !runActive && (++_idleTick % IdleCompletionEvery == 0);
        bool debugChat = ChatDebug is not null && AppSettings.Instance.DebugMode && AppSettings.Instance.DebugShowChatText;
        bool readChat = runActive || debugChat;   // also read for the debug chat view while idle
        bool dump = AppSettings.Instance.DebugDumpCrops && now - _lastDumpTick >= DumpIntervalMs;
        if (dump) _lastDumpTick = now;

        OpenCvMat trackerCrop, compCrop, chatCrop;
        try
        {
            trackerCrop = new OpenCvMat(frame, RegionRect(frame, trackerRegion)).Clone();
            compCrop = (readCompletion || dump) ? new OpenCvMat(frame, RegionRect(frame, completionRegion)).Clone() : new OpenCvMat();
            chatCrop = (readChat || dump) ? new OpenCvMat(frame, RegionRect(frame, chatRegion)).Clone() : new OpenCvMat();
        }
        catch (Exception ex) { Log($"crop error {ex.GetType().Name}: {ex.Message}"); Interlocked.Exchange(ref _busy, 0); return; }

        _ = Task.Run(() =>
        {
            try
            {
                TrackerStatus tracker = _tracker.Read(trackerCrop);
                QuestEntry? entry = null;
                string compRaw = string.Empty;
                if (readCompletion && !compCrop.Empty())
                    entry = _entry.Read(compCrop, out compRaw);

                // Completion fires only on a NEWLY-ARRIVED "Adventure Completed" line — detected by the
                // append-only shift (see NewChatLines), so a stale one already in the log can't re-fire.
                bool freshChat = false;
                int? chatXp = null;
                string chatRaw = string.Empty;
                if (readChat && !chatCrop.Empty())
                {
                    IReadOnlyList<string> chatLines = _chat.ReadLines(chatCrop);
                    chatRaw = string.Join("\n", chatLines);
                    IReadOnlyList<string> newLines = NewChatLines(chatLines, _lastChatLines);
                    freshChat = newLines.Any(RunTextParser.IsAdventureCompleted);
                    if (freshChat) chatXp = RunTextParser.ExtractChatXp(chatLines);   // grab "receive N XP" if it's shown
                    _lastChatLines = chatLines;
                    if (debugChat) ChatDebug?.Invoke(chatLines, newLines);
                }

                // Debug: log what the tracker read whenever it changes, and periodically dump the crops.
                string readLabel = (tracker.Name ?? "(none)") + (tracker.Completed ? " [COMPLETED]" : "");
                if (!string.Equals(readLabel, _lastLoggedName, StringComparison.Ordinal))
                {
                    Log($"tracker-read: \"{readLabel}\"");
                    _lastLoggedName = readLabel;
                }
                if (dump) Dump(trackerCrop, compCrop, chatCrop, readLabel, compRaw, chatRaw);

                Apply(tracker, entry, freshChat, chatXp, compRaw);
            }
            catch (Exception ex) { Log($"read error {ex.GetType().Name}: {ex.Message}"); }
            finally { trackerCrop.Dispose(); compCrop.Dispose(); chatCrop.Dispose(); Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private static void Dump(OpenCvMat trackerCrop, OpenCvMat compCrop, OpenCvMat chatCrop, string? questName, string compRaw, string chatRaw)
    {
        try
        {
            string dir = Path.Combine(AppSettings.AppDataDir, "run-debug");
            Directory.CreateDirectory(dir);
            if (!trackerCrop.Empty()) Cv2.ImWrite(Path.Combine(dir, "tracker.png"), trackerCrop);
            if (!compCrop.Empty()) Cv2.ImWrite(Path.Combine(dir, "completion.png"), compCrop);
            if (!chatCrop.Empty()) Cv2.ImWrite(Path.Combine(dir, "chat.png"), chatCrop);
            Log($"dump: tracker=\"{questName ?? "(none)"}\" completion=<<{compRaw.Replace("\n", " | ")}>> chat=<<{chatRaw.Replace("\n", " | ")}>>");
        }
        catch (Exception ex) { Log($"dump error {ex.GetType().Name}: {ex.Message}"); }
    }

    // Decide what this tick's reads mean. Runs off the frame thread; mutates state under _lock and
    // raises events AFTER releasing it (handlers marshal to the UI thread themselves).
    //
    // The quest-tracker panel drives everything: a title appearing = entry, "Status: Completed" = done.
    // Crucially, while a run is active we IGNORE which dungeon name is read (you can't hop dungeons), so
    // OCR flicker on the ornate title can't spawn phantom "went somewhere else" runs. A run ends only on
    // Completed, returning to a hub, or the tracker staying empty.
    private void Apply(TrackerStatus tracker, QuestEntry? entry, bool freshChat, int? chatXp, string compRaw)
    {
        RunRecord? finalized = null;
        bool currentChanged = false;
        RunRecord? currentSnapshot = null;
        bool entryShowChanged = false;
        QuestEntry? entryToShow = null;
        long nowTick = Environment.TickCount64;
        string? name = tracker.Name;
        bool isHub = name is not null && PublicZones.Contains(Key(name));

        lock (_lock)
        {
            // The adventure-entry popup (seen while still in town) names the quest cleanly — hold it until
            // a run starts. Refresh the timestamp each time it's read so it stays fresh while you pick a
            // difficulty.
            if (entry is not null)
            {
                if (!NameEq(entry.Name, _pendingEntry?.Name)) Log($"entry-popup: \"{entry.Name}\" L{entry.QuestLevel?.ToString() ?? "?"}");
                _pendingEntry = entry;
                _pendingEntryTick = nowTick;
                _armedArea = Key(name);   // the area you're standing in while choosing (the "outside")
            }

            if (_current is { } cur)
            {
                // "Left the dungeon" is a POSITIVE signal: you're back in a hub. An empty/garbled tracker
                // is NOT leaving — dungeons have internal loading screens and porting between areas, which
                // blank the tracker for a while. So we tolerate empty indefinitely, ending only on a hub,
                // with a long empty-timeout purely as stale cleanup (logout/crash). Death-and-return isn't
                // specially handled (fine to treat as one run).
                if (name is null) { if (_emptySinceTick == 0) _emptySinceTick = nowTick; }
                else _emptySinceTick = 0;
                bool stale = _emptySinceTick != 0 && nowTick - _emptySinceTick > StaleEmptyMs;
                bool left = isHub || stale;

                if (cur.Completed)
                {
                    // Already recorded. Clear the completed card once you've left: a hub, OR the zone
                    // changed (the tracker goes blank as you load out — completing often ports you to a
                    // non-hub area). A short timeout is the backstop.
                    bool zoneChanged = name is null;
                    bool doneShowing = left || zoneChanged
                        || (cur.CompletedUtc is { } cu && (DateTime.UtcNow - cu).TotalSeconds >= CompletedCardSeconds);
                    if (doneShowing)
                    {
                        _current = null;
                        _sawEmptySinceFinalize = true;
                        currentChanged = true;
                        currentSnapshot = null;
                    }
                }
                // Completion: the chat log's "Adventure Completed" (reliable, persistent) or the tracker's
                // "Status: Completed". Require the run to have lasted a bit so a STALE "Adventure Completed"
                // still scrolled in the chat can't instantly finish a freshly-started run.
                else if ((freshChat || tracker.Completed) && (DateTime.UtcNow - cur.EnteredUtc).TotalSeconds >= MinRunSeconds)
                {
                    // Write the run, but KEEP it on the card until you leave. XP is best-effort from chat.
                    finalized = FinalizeCurrent(compRaw, chatXp);
                    _current = finalized;                        // keep displaying the finished run
                    _lastFinalizedName = finalized.DungeonName;
                    _sawEmptySinceFinalize = false;
                    _emptySinceTick = 0;
                    ResetPending();
                    currentChanged = true;
                    currentSnapshot = finalized;
                }
                else if (left)
                {
                    finalized = AbandonCurrent();          // back in a hub without finishing
                    _sawEmptySinceFinalize = true;
                    currentChanged = true;
                }
                // else: still in the run — a dungeon name (flicker ignored) or a transient empty/load. Wait.
            }
            else   // no active run
            {
                // TIGHT GATE: a run can ONLY begin after the adventure-entry popup was captured (its
                // "Select Difficulty / Level: N" anchor is unmistakably a quest entry). Without a held
                // popup, NOTHING starts a run — hubs, character select, loading screens, and OCR garbage
                // are all ignored. This replaces the old "anything that isn't a known hub is a dungeon"
                // approach, which is what let CHARACTER SELECTION etc. through.
                if (_pendingEntry is { } pe && nowTick - _pendingEntryTick < PendingEntryTtlMs)
                {
                    bool lingering = _lastFinalizedName is not null && NameEq(_lastFinalizedName, pe.Name) && !_sawEmptySinceFinalize;
                    bool popupStillUp = nowTick - _pendingEntryTick < PopupGoneMs;   // still at the entrance
                    // Enter vs Cancel: entering LOADS you into the instance so your area changes; Cancel
                    // leaves you exactly where you stood. So only start once the area differs from the
                    // "outside" area we recorded while the popup was up.
                    bool areaChanged = AreaChanged(_armedArea, Key(name));
                    bool entered = name is not null && !isHub && !popupStillUp && areaChanged;
                    if (entered && !lingering)
                    {
                        // Confirm the entry over a couple of ticks so a loading-screen flicker can't fire it.
                        if (++_pendingCount >= StartDebounce)
                        {
                            _current = NewRun(pe.Name) with { QuestLevel = pe.QuestLevel };
                            _pendingEntry = null;   // consumed by this run
                            _sawEmptySinceFinalize = false;
                            _lastChatLines = Array.Empty<string>();   // fresh chat baseline for this run
                            _pendingCount = 0;
                            Log($"started \"{pe.Name}\"");
                            currentChanged = true;
                            currentSnapshot = _current;
                        }
                    }
                    else
                    {
                        _pendingCount = 0;
                        // Cancelled / stayed put: the popup is gone but the area is unchanged (a real entry
                        // would have loaded you elsewhere). Clear the armed entry now — no reason to hold it.
                        if (!popupStillUp && name is not null && !isHub && !AreaChanged(_armedArea, Key(name)))
                        {
                            _pendingEntry = null;
                            _armedArea = null;
                        }
                        if (isHub || name is null) _sawEmptySinceFinalize = true;
                    }
                }
                else
                {
                    // No popup held → nothing here is a run. Just track the "empty since finalize" flag.
                    _pendingCount = 0;
                    if (isHub || name is null) _sawEmptySinceFinalize = true;
                }
            }

            // The "QUEST READY" card should show only WHILE the popup is on screen (seen within the last
            // few seconds), then clear — even though the entry stays armed internally to attach on entry.
            // So a Cancel makes the card go idle instead of sitting on READY forever.
            bool popupUp = _pendingEntry is not null && nowTick - _pendingEntryTick < PopupGoneMs;
            // Key by name AND level so switching difficulty tier (which changes the level) refreshes the card.
            string? shownKey = popupUp ? $"{_pendingEntry!.Name}|{_pendingEntry.QuestLevel}" : null;
            if (!string.Equals(shownKey, _shownEntryName, StringComparison.Ordinal))
            {
                _shownEntryName = shownKey;
                entryShowChanged = true;
                entryToShow = popupUp ? _pendingEntry : null;
            }
        }

        if (finalized is not null) RunFinalized?.Invoke(finalized);
        if (currentChanged) CurrentChanged?.Invoke(currentSnapshot);
        if (entryShowChanged) EntryHeld?.Invoke(entryToShow);
    }

    // ---- helpers (all called under _lock) ----

    private RunRecord FinalizeCurrent(string compRaw, int? xp)
    {
        DateTime nowUtc = DateTime.UtcNow;
        // KEEP the run's own name (from the entry popup). Completion must never rename it to whatever the
        // tracker happens to read in-dungeon (which is garbage like "Recall").
        RunRecord run = _current! with { CompletedUtc = nowUtc, Completed = true, Xp = xp ?? _current.Xp, RawOcrText = compRaw };
        _store.Add(run);
        Log($"finalized \"{run.DungeonName}\" xp={run.Xp?.ToString() ?? "?"}");
        _current = null;
        _emptySinceTick = 0;
        return run;
    }

    private RunRecord AbandonCurrent()
    {
        RunRecord abandoned = _current! with { CompletedUtc = DateTime.UtcNow, Completed = false };
        _store.Add(abandoned);
        Log($"left \"{abandoned.DungeonName}\"");
        _current = null;
        _emptySinceTick = 0;
        return abandoned;
    }

    private void ResetPending() { _pendingCount = 0; }

    private RunRecord NewRun(string name)
    {
        (string? id, int? level) = _character();
        return new RunRecord(RunRecord.NewId(), name, null, level, id, DateTime.UtcNow, null, null, false, string.Empty);
    }

    private static Rect RegionRect(OpenCvMat frame, RegionRatios r)
    {
        int x0 = Clamp((int)(frame.Width * r.X0), 0, frame.Width - 1);
        int y0 = Clamp((int)(frame.Height * r.Y0), 0, frame.Height - 1);
        int x1 = Clamp((int)(frame.Width * r.X1), x0 + 1, frame.Width);
        int y1 = Clamp((int)(frame.Height * r.Y1), y0 + 1, frame.Height);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    // Alphanumeric-only lowercase key — the tracker title's font OCRs word gaps inconsistently, so all
    // name comparisons (public-zone match, "same run") go through this to stay stable across ticks.
    private static string Key(string? s) => s is null ? string.Empty : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool NameEq(string? a, string? b)
    {
        string ka = Key(a), kb = Key(b);
        return ka.Length > 0 && ka == kb;
    }

    // Newly-arrived chat lines, found by the append-only SHIFT: the chat is a scrolling log, so when new
    // lines appear the old ones move up by exactly that many rows. We find the smallest shift N at which
    // the current lines line up with the previous lines moved up N rows — then the bottom N lines are new.
    // This catches even a single new line, and OCR jitter can't fake it (the whole stack must realign).
    private static IReadOnlyList<string> NewChatLines(IReadOnlyList<string> current, IReadOnlyList<string> previous)
    {
        // No baseline yet, or nothing to compare → report NOTHING new (never fire on the baseline read).
        if (previous.Count == 0 || current.Count == 0) return Array.Empty<string>();
        for (int n = 0; n <= previous.Count; n++)
        {
            int compared = 0, matched = 0;
            for (int i = 0; i < current.Count && i + n < previous.Count; i++)
            {
                compared++;
                if (LineSimilar(current[i], previous[i + n])) matched++;
            }
            if (compared >= 2 && matched >= compared * 0.6)
                return current.Skip(Math.Max(0, current.Count - n)).ToList();   // clean shift → bottom N are new
        }
        // Couldn't determine the shift (jitter / big jump). Do NOT treat everything as new — that would
        // re-fire stale lines. When unsure, report nothing (miss-and-retry beats a false completion).
        return Array.Empty<string>();
    }

    private static bool LineSimilar(string a, string b)
    {
        var wa = ChatWords(a);
        var wb = ChatWords(b);
        if (wa.Count == 0 || wb.Count == 0) return wa.Count == wb.Count;
        int shared = wa.Count(wb.Contains);
        return (double)shared / Math.Max(wa.Count, wb.Count) >= 0.6;   // same line despite OCR jitter
    }

    private static HashSet<string> ChatWords(string s)
        => new(System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), @"[a-z]{3,}").Select(m => m.Value));

    // True if area key `b` is a SUBSTANTIALLY different area than the armed key `a` — used to tell an
    // actual zone-in (Enter) from staying put (Cancel). Tolerant of the ornate name's OCR flicker: a
    // long shared prefix (minor char errors like l↔1, o↔0) counts as the same area.
    private static bool AreaChanged(string? a, string b)
    {
        a ??= string.Empty;
        if (a.Length == 0) return b.Length > 0;   // was blank/none → any real area is a change
        if (b.Length == 0) return false;          // now blank (loading) → not a change for start purposes
        if (a == b) return false;
        int common = 0, n = Math.Min(a.Length, b.Length);
        while (common < n && a[common] == b[common]) common++;
        return common < n * 0.6;                  // <60% shared prefix ⇒ a different area
    }
}
