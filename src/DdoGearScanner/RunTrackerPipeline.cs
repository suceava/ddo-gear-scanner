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
/// TUNING: with Debug Mode + "Dump region crops" on, each ~5s this writes the two region crops + OCR'd text to
/// %APPDATA%\DdoGearScanner\debug\run\ and logs every tracker read — that's how the regions get dialed in.
/// </summary>
public sealed class RunTrackerPipeline
{
    // Read cadence. Regions are small (calibrated), so we can read often — needed to catch a brief
    // "Status: Completed" on quests that finish-and-teleport instantly.
    private const long IntervalMs = 300;
    // A candidate quest name must be read this many consecutive ticks before we act (glitch guard).
    private const int StartDebounce = 2;
    // A blank tracker (loading screen) must persist this many consecutive ticks after the popup closes to
    // count as "entered" — long enough that a one-frame OCR blank can't fake it, short vs a real load.
    private const int LoadDebounce = 3;
    // A blank tracker is tolerated this long (dungeon loads / porting) before treating it as stale
    // (logout/crash cleanup) — a backstop; the primary "left" signal is the quest-name match below.
    private const long StaleEmptyMs = 5 * 60 * 1000;
    // "You left" fires only after this many consecutive non-blank reads whose tracker lines DON'T match the
    // run's quest name. Set generously: the ornate title reads well but can drop to objective-only frames
    // for a few seconds (combat FX over the panel), which must not trip a false leave. ~3.3 reads/s.
    private const int LeftDebounce = 15;
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
        // High-level town hubs you recall/port back to (extend as needed — towns OCR cleanly, so this
        // list is the reliable leave-signal; quest names in the tracker do NOT OCR in-dungeon).
        "eveningstar", "meridia", "wheloon", "whelooncity", "sharn",
    };

    private readonly EntryPopupReader _entry;
    private readonly QuestTrackerReader _tracker;
    private readonly ChatLogReader _chat;
    private readonly RunStore _store;
    private readonly Func<(string? Id, int? Level)> _character;
    private readonly CharacterReader _avatar;
    private RegionRatios _trackerRegion;
    private RegionRatios _completionRegion;   // the calibrated quest-entry-popup box
    private RegionRatios _chatRegion;         // the calibrated chat-log box (completion signal)
    private RegionRatios _avatarRegion;       // the calibrated avatar box (character name + level)

    private readonly object _lock = new();
    private volatile bool _enabled = true;
    private int _busy;                 // 0/1 guard: at most one OCR read in flight
    private long _lastTick;
    private long _lastDumpTick;
    private int _idleTick;
    private int _avatarTick;

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
    private IReadOnlyList<string> _lastChatLines = Array.Empty<string>();  // previous chat lines (debug view only)
    private bool _completionPresent;    // was "Adventure Completed" present in the last chat read (rising-edge detect)
    private bool _completionBaselined;  // has this run taken its first chat read (baseline) yet
    private int? _runXp;                // latest "receive N XP" seen in chat this run (XP is chat-only)
    private string? _detectedName;     // latest character name OCR'd from the avatar region
    private int? _detectedLevel;       // latest character level OCR'd from the avatar region
    private int _pendingCount;         // consecutive "entered a non-hub area" ticks while armed by a popup
    private int _loadingTicks;         // consecutive blank-tracker ticks after the popup closed (loading in)
    private string? _lastFinalizedName;
    private bool _sawEmptySinceFinalize = true;
    private string? _lastLoggedName;   // debug: only log a tracker read when it changes
    private QuestEntry? _pendingEntry; // last quest-entry popup seen, awaiting a run to attach to
    private long _pendingEntryTick;
    // LLM (OpenRouter) escalation state: one LLM read per popup appearance / per character change. The
    // LLM answer OWNS the static popup facts (name/level/duration) and the character name; the live local
    // reads keep owning difficulty (the user can change the selection after the LLM snapshot).
    private string? _llmEntryKey;      // Key(local popup name) already escalated (null = none)
    private QuestEntry? _llmEntryValue; // the LLM's answer for that popup
    // Every character key we've escalated (success OR failure) — a SET, not a single slot: local OCR can
    // alternate between name variants with different keys ("Cleroki" / "CIeroki"), and a single-slot memo
    // ping-ponged between them, re-calling the LLM every few seconds.
    private readonly HashSet<string> _llmCharDone = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _llmCharNames = new(StringComparer.Ordinal);   // local key → confirmed name
    private int _llmCharBusy;          // 1 while an avatar escalation is in flight
    private string? _armedArea;        // the area key you stood in while the popup was up (to detect Enter vs Cancel)
    private string? _shownEntryName;   // the quest name currently shown on the "ready" card (null = not shown)
    private bool _leftPromptActive;    // the "you left the dungeon — pause/cancel/keep going?" banner is showing
    private bool _leftDismissed;       // user chose "keep going" — suppress re-prompting until back inside
    private int _leftTicks;            // consecutive non-blank reads whose tracker lines DON'T match the quest

    /// <summary>Raised when a run is written to the store (completed or abandoned).</summary>
    public event Action<RunRecord>? RunFinalized;
    /// <summary>Raised when the in-progress run changes (new run started, or ended → null).</summary>
    public event Action<RunRecord?>? CurrentChanged;
    /// <summary>Raised when the adventure-entry popup is captured (before entering) — lets the UI show
    /// "quest ready" feedback. Fires with null when the held entry is consumed/cleared.</summary>
    public event Action<QuestEntry?>? EntryHeld;
    /// <summary>Debug: the chat OCR each read — (all lines, the lines detected as newly-arrived).</summary>
    public event Action<IReadOnlyList<string>, IReadOnlyList<string>>? ChatDebug;
    /// <summary>Raised when detection thinks you've left the dungeon mid-run: true = show the
    /// "pause / cancel / keep going?" banner, false = hide it (resolved or you're back inside).</summary>
    public event Action<bool>? LeftPromptChanged;

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ddo-gear-scanner.log");
    private static void Log(string m) { try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [runs] {m}{Environment.NewLine}"); } catch { } }

    public RunTrackerPipeline(
        EntryPopupReader entry, QuestTrackerReader tracker, ChatLogReader chat, CharacterReader avatar, RunStore store,
        Func<(string? Id, int? Level)> character, RegionRatios trackerRegion, RegionRatios completionRegion,
        RegionRatios chatRegion, RegionRatios avatarRegion, OpenRouterRunReader? llm = null)
    {
        _entry = entry;
        _tracker = tracker;
        _chat = chat;
        _avatar = avatar;
        _store = store;
        _character = character;
        _trackerRegion = trackerRegion;
        _completionRegion = completionRegion;
        _chatRegion = chatRegion;
        _avatarRegion = avatarRegion;
        _llm = llm;
    }

    private readonly OpenRouterRunReader? _llm;

    public bool Enabled => _enabled;
    public RunRecord? Current { get { lock (_lock) { return _current; } } }

    /// <summary>The character name/level most recently OCR'd from the avatar region — for showing who's
    /// about to run a quest BEFORE a run is stamped. Nulls until the avatar region is calibrated + read.</summary>
    public (string? Name, int? Level) DetectedCharacter { get { lock (_lock) { return (_detectedName, _detectedLevel); } } }

    public void SetEnabled(bool on) => _enabled = on;
    public bool Toggle() { _enabled = !_enabled; return _enabled; }

    /// <summary>Apply freshly-calibrated regions live (no restart). Volatile writes; picked up next frame.</summary>
    public void SetRegions(RegionRatios tracker, RegionRatios completion, RegionRatios chat, RegionRatios avatar)
    {
        lock (_lock) { _trackerRegion = tracker; _completionRegion = completion; _chatRegion = chat; _avatarRegion = avatar; }
    }

    // ---- manual controls (the card's Start / Complete / Cancel) — detection is best-effort, so the user
    // can always override it. All mutate state under _lock and raise events after releasing it, like Apply.

    /// <summary>Manually begin a run (detection missed the entry). Uses the held entry popup's name/level
    /// if one is pending, otherwise an unnamed run the user can rename in the grid.</summary>
    public void ManualStart()
    {
        RunRecord started;
        lock (_lock)
        {
            if (_current is not null) return;
            started = NewRun(_pendingEntry?.Name ?? string.Empty) with { QuestLevel = _pendingEntry?.QuestLevel, Difficulty = _pendingEntry?.Difficulty, QuestDuration = _pendingEntry?.Duration };
            _current = started;
            _pendingEntry = null; _armedArea = null; _shownEntryName = null;
            _sawEmptySinceFinalize = false;
            _lastChatLines = Array.Empty<string>();   // fresh chat baseline for this run
            _completionBaselined = false;
            _runXp = null;
            _pendingCount = 0;
            _emptySinceTick = 0;
        }
        Log($"manual-start \"{started.DungeonName}\"");
        CurrentChanged?.Invoke(started);
        EntryHeld?.Invoke(null);
    }

    /// <summary>Manually finalize the in-progress run as completed now (detection missed the completion).</summary>
    public void ManualComplete()
    {
        RunRecord finalized;
        lock (_lock)
        {
            if (_current is null || _current.Completed) return;
            finalized = FinalizeCurrent(string.Empty);
            _current = finalized;                        // keep it on the card until you leave/dismiss
            _lastFinalizedName = finalized.DungeonName;
            _sawEmptySinceFinalize = false;
            _emptySinceTick = 0;
            ResetPending();
        }
        Log($"manual-complete \"{finalized.DungeonName}\"");
        RunFinalized?.Invoke(finalized);
        CurrentChanged?.Invoke(finalized);
    }

    /// <summary>Rename the in-progress run (fix an OCR mis-parse of the quest name while it's live). Once
    /// the run is completed/logged, rename it in the history table instead.</summary>
    public void SetCurrentName(string name)
    {
        RunRecord? updated = null;
        lock (_lock)
        {
            if (_current is null || _current.Completed) return;
            string v = name?.Trim() ?? string.Empty;
            if (v.Length == 0 || v == _current.DungeonName) return;
            _current = _current with { DungeonName = v, Edited = true };
            updated = _current;
        }
        if (updated is not null) CurrentChanged?.Invoke(updated);
    }

    /// <summary>Replace the current run with a hand-edited copy (from the card's Edit dialog). Only applies
    /// if it's still the same run; persists it if it's already completed/logged.</summary>
    public void UpdateCurrent(RunRecord updated)
    {
        RunRecord? result = null;
        lock (_lock)
        {
            if (_current is null || updated.Id != _current.Id) return;
            _current = updated with { Edited = true };
            if (_current.Completed) _store.Update(_current);
            result = _current;
        }
        if (result is not null) CurrentChanged?.Invoke(result);
    }

    /// <summary>Set the current run's difficulty by hand (auto-detect can miss the popup's selection ring).
    /// Works on the in-progress run and on a just-completed one still on the card (persists that one).</summary>
    public void SetCurrentDifficulty(string? difficulty)
    {
        RunRecord? updated = null;
        lock (_lock)
        {
            if (_current is null) return;
            string? v = string.IsNullOrWhiteSpace(difficulty) ? null : difficulty.Trim();
            if (v == _current.Difficulty) return;
            _current = _current with { Difficulty = v, Edited = true };
            if (_current.Completed) _store.Update(_current);   // already logged → update it too
            updated = _current;
        }
        if (updated is not null) CurrentChanged?.Invoke(updated);
    }

    /// <summary>Pause the in-progress run — freezes the timer (records when) and, importantly, suspends all
    /// completion/left/stale detection until Resume, so stepping out to town doesn't finish or cancel it.
    /// Also clears any pending "you left?" banner (pausing answers it).</summary>
    public void PauseCurrent()
    {
        RunRecord? updated = null;
        lock (_lock)
        {
            if (_current is null || _current.Completed || _current.Paused) return;
            _current = _current with { Paused = true, PausedUtc = DateTime.UtcNow };
            _leftPromptActive = false; _leftDismissed = false;
            updated = _current;
        }
        if (updated is not null) { Log($"paused \"{updated.DungeonName}\""); LeftPromptChanged?.Invoke(false); CurrentChanged?.Invoke(updated); }
    }

    /// <summary>Resume a paused run — shifts EnteredUtc forward by the paused span so elapsed continues
    /// seamlessly, and re-arms detection. Re-baselines the completion signal so a stale chat line can't fire.</summary>
    public void ResumeCurrent()
    {
        RunRecord? updated = null;
        lock (_lock)
        {
            if (_current is null || !_current.Paused) return;
            DateTime nowUtc = DateTime.UtcNow;
            TimeSpan paused = _current.PausedUtc is { } p ? nowUtc - p : TimeSpan.Zero;
            _current = _current with { Paused = false, PausedUtc = null, EnteredUtc = _current.EnteredUtc + paused };
            _emptySinceTick = 0;
            _leftTicks = 0;                               // fresh "am I still in my quest?" streak after resume
            _completionBaselined = false;                 // re-baseline the completion rising-edge on the next read
            _lastChatLines = Array.Empty<string>();
            updated = _current;
        }
        if (updated is not null) { Log($"resumed \"{updated.DungeonName}\""); CurrentChanged?.Invoke(updated); }
    }

    /// <summary>User answered the "you left?" banner with "keep going" — hide it and suppress re-prompting
    /// until the tracker shows you're back inside a quest.</summary>
    public void DismissLeftPrompt()
    {
        lock (_lock) { if (!_leftPromptActive) return; _leftPromptActive = false; _leftDismissed = true; }
        Log("left-prompt dismissed (keep going)");
        LeftPromptChanged?.Invoke(false);
    }

    /// <summary>Discard the current run entirely — it is NOT written to the log. Fixes a false start (a
    /// wilderness area logged as a quest) or a run left hanging "in progress" after you moved on.</summary>
    public void ManualCancel()
    {
        RunRecord? prev;
        lock (_lock)
        {
            if (_current is null) return;
            prev = _current;
            _current = null;
            _emptySinceTick = 0;
            _sawEmptySinceFinalize = true;
            _leftPromptActive = false; _leftDismissed = false;
            _pendingEntry = null; _armedArea = null; _shownEntryName = null;
            ResetPending();
        }
        Log($"cancelled \"{prev.DungeonName}\"");
        LeftPromptChanged?.Invoke(false);
        CurrentChanged?.Invoke(null);
        EntryHeld?.Invoke(null);
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
        RegionRatios trackerRegion, completionRegion, chatRegion, avatarRegion;
        lock (_lock) { runActive = _current is not null; trackerRegion = _trackerRegion; completionRegion = _completionRegion; chatRegion = _chatRegion; avatarRegion = _avatarRegion; }
        // Idle: scan the popup region (the popup only appears before entering). In a run: scan the chat
        // region for "Adventure Completed" — the reliable, persistent completion signal.
        bool readCompletion = !runActive && (++_idleTick % IdleCompletionEvery == 0);
        // Avatar (character name/level) is static and only needed to stamp a run at start — read it
        // occasionally while idle and cache the latest.
        bool readAvatar = !runActive && (++_avatarTick % 3 == 0);
        bool debugChat = ChatDebug is not null && AppSettings.Instance.DebugMode && AppSettings.Instance.DebugShowChatText;
        bool readChat = runActive || debugChat;   // also read for the debug chat view while idle
        bool dump = AppSettings.Instance.DebugMode && AppSettings.Instance.DebugDumpRunRegions && now - _lastDumpTick >= DumpIntervalMs;
        if (dump) _lastDumpTick = now;

        OpenCvMat trackerCrop, compCrop, chatCrop, avatarCrop;
        try
        {
            trackerCrop = new OpenCvMat(frame, RegionRect(frame, trackerRegion)).Clone();
            compCrop = (readCompletion || dump) ? new OpenCvMat(frame, RegionRect(frame, completionRegion)).Clone() : new OpenCvMat();
            chatCrop = (readChat || dump) ? new OpenCvMat(frame, RegionRect(frame, chatRegion)).Clone() : new OpenCvMat();
            avatarCrop = (readAvatar || dump) ? new OpenCvMat(frame, RegionRect(frame, avatarRegion)).Clone() : new OpenCvMat();
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
                {
                    entry = _entry.Read(compCrop, out compRaw);
                    // Event escalation: a popup is up → one LLM read of the same crop (deduped per popup).
                    if (entry is not null) MaybeLlmEntry(entry, compCrop);
                }

                CharacterInfo? avatar = null;
                string avatarRaw = string.Empty;
                if ((readAvatar || dump) && !avatarCrop.Empty())
                {
                    avatar = _avatar.Read(avatarCrop, out avatarRaw);
                    if (dump) Log($"char-parse: name=\"{avatar?.Name}\" lvl={avatar?.Level?.ToString() ?? "?"}");
                    // Event escalation: a (new) character is on screen → one LLM read per character change.
                    if (avatar is not null && !string.IsNullOrWhiteSpace(avatar.Name)) MaybeLlmAvatar(avatar, avatarCrop);
                }

                // Completion fires only on a NEWLY-ARRIVED "Adventure Completed" line — detected by the
                // append-only shift (see NewChatLines), so a stale one already in the log can't re-fire.
                bool freshChat = false;
                int? chatXp = null;
                string chatRaw = string.Empty;
                if (readChat && !chatCrop.Empty())
                {
                    IReadOnlyList<string> chatLines = _chat.ReadLines(chatCrop);
                    chatRaw = string.Join("\n", chatLines);

                    // Completion = RISING EDGE on "Adventure Completed" being present in the chat. This is
                    // robust to a fast, noisy combat/effects chat where line-shift alignment fails (the
                    // message just has to APPEAR, however fast the log scrolls). Baselined on the run's
                    // first chat read so a stale completion still scrolled in from the prior quest can't
                    // fire it; MinRunSeconds is a second guard.
                    bool completionNow = chatLines.Any(RunTextParser.IsAdventureCompleted);
                    if (!_completionBaselined) _completionBaselined = true;                 // first read: baseline only
                    else if (completionNow && !_completionPresent) freshChat = true;        // absent → present
                    _completionPresent = completionNow;

                    // XP only ever appears in chat ("You receive N XP") — even when the TRACKER's
                    // "Completed" is what finalizes the run. So capture the latest visible value every read
                    // and let Apply keep the newest seen this run, ready for whichever signal completes it.
                    chatXp = RunTextParser.ExtractChatXp(chatLines);

                    // Kept only to feed the debug chat view.
                    IReadOnlyList<string> newLines = NewChatLines(chatLines, _lastChatLines);
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
                if (dump) Dump(trackerCrop, compCrop, chatCrop, avatarCrop, readLabel, compRaw, chatRaw, avatarRaw);

                Apply(tracker, entry, freshChat, chatXp, compRaw, avatar);
            }
            catch (Exception ex) { Log($"read error {ex.GetType().Name}: {ex.Message}"); }
            finally { trackerCrop.Dispose(); compCrop.Dispose(); chatCrop.Dispose(); avatarCrop.Dispose(); Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private static void Dump(OpenCvMat trackerCrop, OpenCvMat compCrop, OpenCvMat chatCrop, OpenCvMat avatarCrop,
        string? questName, string compRaw, string chatRaw, string avatarRaw)
    {
        try
        {
            string dir = DebugPaths.Run;
            Directory.CreateDirectory(dir);
            if (!trackerCrop.Empty()) Cv2.ImWrite(Path.Combine(dir, "tracker.png"), trackerCrop);
            if (!compCrop.Empty()) Cv2.ImWrite(Path.Combine(dir, "completion.png"), compCrop);
            if (!chatCrop.Empty()) Cv2.ImWrite(Path.Combine(dir, "chat.png"), chatCrop);
            if (!avatarCrop.Empty()) Cv2.ImWrite(Path.Combine(dir, "character.png"), avatarCrop);
            Log($"dump: tracker=\"{questName ?? "(none)"}\" completion=<<{compRaw.Replace("\n", " | ")}>> chat=<<{chatRaw.Replace("\n", " | ")}>> char=<<{avatarRaw.Replace("\n", " | ")}>>");
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
    private void Apply(TrackerStatus tracker, QuestEntry? entry, bool freshChat, int? chatXp, string compRaw, CharacterInfo? avatar)
    {
        RunRecord? finalized = null;
        bool currentChanged = false;
        RunRecord? currentSnapshot = null;
        bool entryShowChanged = false;
        QuestEntry? entryToShow = null;
        bool? leftPromptShow = null;   // null = no change, true = show the banner, false = hide it
        long nowTick = Environment.TickCount64;
        string? name = tracker.Name;
        bool isHub = name is not null && PublicZones.Contains(Key(name));

        lock (_lock)
        {
            if (chatXp is not null) _runXp = chatXp;   // keep the freshest chat XP for the active run
            if (avatar is not null)                    // cache the latest character name/level for run-start stamping
            {
                // The LLM-confirmed name is authoritative for that character — local OCR jitter (a stray
                // quote etc.) must not clobber it. A DIFFERENT character (new key) goes back to local.
                if (!string.IsNullOrWhiteSpace(avatar.Name))
                    _detectedName = _llmCharNames.TryGetValue(Key(avatar.Name), out string? confirmed) ? confirmed : avatar.Name;
                if (avatar.Level is not null) _detectedLevel = avatar.Level;
            }

            // The adventure-entry popup (seen while still in town) names the quest cleanly — hold it until
            // a run starts. Refresh the timestamp each time it's read so it stays fresh while you pick a
            // difficulty.
            if (entry is not null)
            {
                if (!NameEq(entry.Name, _pendingEntry?.Name)) Log($"entry-popup: \"{entry.Name}\" L{entry.QuestLevel?.ToString() ?? "?"}");
                // Carry a previously-detected difficulty forward when THIS frame's read didn't catch the
                // ring (the label OCR / ring detection jitters frame-to-frame, but the selection is stable).
                // Only within the same quest — a new popup resets it.
                if (entry.Difficulty is null && _pendingEntry is { } prev && NameEq(prev.Name, entry.Name) && prev.Difficulty is not null)
                    entry = entry with { Difficulty = prev.Difficulty };
                if (!string.Equals(entry.Difficulty, _pendingEntry?.Difficulty, StringComparison.Ordinal))
                    Log($"difficulty: {entry.Difficulty ?? "?"}");
                // An LLM answer for THIS popup owns the static facts (name/level/duration) — a fresh local
                // read must not clobber them. Difficulty stays LIVE-local (the user can re-click after the
                // LLM snapshot); the LLM's difficulty only backs up a local miss.
                if (_llmEntryValue is { } ai && Key(entry.Name) == _llmEntryKey)
                    entry = new QuestEntry(ai.Name, ai.QuestLevel ?? entry.QuestLevel, entry.Difficulty ?? ai.Difficulty, ai.Duration ?? entry.Duration);
                _pendingEntry = entry;
                _pendingEntryTick = nowTick;
                _armedArea = Key(name);   // the area you're standing in while choosing (the "outside")
            }

            if (_current is { Paused: true })
            {
                // Paused: the user stepped out on purpose (recall/AFK). Suspend ALL completion/left/stale
                // detection until they Resume — being in a hub, an empty tracker, or a stale chat line must
                // not finish or cancel a paused run. (XP/avatar caching above still runs; that's harmless.)
            }
            else if (_current is { } cur)
            {
                // "Left the dungeon" = the tracker no longer shows THIS run's quest. The quest panel's ornate
                // TITLE is the quest name in-instance and the ZONE name once you leave — and it DOES OCR (the
                // earlier failure was the calibrated region cutting the title off, not the engine). So match
                // the run's clean (popup-sourced) name against every tracker line, fuzzy (ornate font is
                // noisy), debounced (title can drop to objective-only frames briefly). A blank tracker is
                // loading/porting, never "left". isHub is a harmless early trigger for known towns; stale is
                // last-ditch cleanup.
                if (name is null) { if (_emptySinceTick == 0) _emptySinceTick = nowTick; }
                else _emptySinceTick = 0;
                bool stale = _emptySinceTick != 0 && nowTick - _emptySinceTick > StaleEmptyMs;
                bool weakName = Key(cur.DungeonName).Length < 4;   // manual run w/ no usable name → can't match
                if (name is not null && !weakName)
                    _leftTicks = TrackerShowsQuest(tracker, cur.DungeonName) ? 0 : _leftTicks + 1;
                bool leftByName = !weakName && _leftTicks >= LeftDebounce;
                bool left = leftByName || isHub || stale;

                if (!cur.Completed && IsWilderness(tracker))
                {
                    // Wilderness/explorer areas (e.g. The High Road) show a "Slayer: <Area> Menaces"
                    // counter in the tracker — NOT a quest panel. DDO's entry popup for them looks like a
                    // quest entry, so a run wrongly started; the slayer counter is the unambiguous tell.
                    // DISCARD it — it is not a quest and must never be logged.
                    Log($"discarded wilderness \"{cur.DungeonName}\" (tracker: \"{name}\")");
                    _current = null;
                    _emptySinceTick = 0;
                    _sawEmptySinceFinalize = true;
                    ResetPending();
                    currentChanged = true;
                    currentSnapshot = null;
                }
                else if (cur.Completed)
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
                    finalized = FinalizeCurrent(compRaw);
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
                    // You appear to be out of the dungeon (back in a hub, or the tracker's been blank a long
                    // while). Don't silently cancel — pausing, cancelling, and "still going, just stepped
                    // out for a sec" are all real. ASK (once) via the card banner; leave the run in progress
                    // until the user chooses. "Keep going" sets _leftDismissed so it won't nag every frame.
                    if (!_leftPromptActive && !_leftDismissed)
                    {
                        _leftPromptActive = true;
                        leftPromptShow = true;
                        Log($"left-prompt \"{cur.DungeonName}\" (tracker: \"{name ?? "(blank)"}\")");
                    }
                }
                else
                {
                    // Still in the run — a dungeon name (flicker ignored) or a transient empty/load. If we
                    // read a real non-hub area, you're back inside: clear any "you left?" prompt and re-arm
                    // so a LATER exit asks again.
                    if (name is not null && !isHub && (_leftPromptActive || _leftDismissed))
                    {
                        if (_leftPromptActive) leftPromptShow = false;
                        _leftPromptActive = false; _leftDismissed = false;
                    }
                }
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
                    bool areaChanged = AreaChanged(_armedArea, Key(name));

                    // Enter vs Cancel. Clicking Enter LOADS you into the instance: the tracker goes BLANK
                    // during the loading screen, then the area changes. Cancel leaves you standing in the
                    // same visible area (tracker stays non-blank). So an entry shows up as EITHER the area
                    // changing OR the tracker going blank (loading) after the popup closed. The blank-load
                    // path is what rescues quests entered from a wilderness (long load) or whose new area
                    // has no readable title (camp/social hubs) — the old code only watched for area change
                    // and dropped the pending entry when the load outlasted its TTL.
                    if (!popupStillUp && name is null) _loadingTicks++;
                    else if (name is not null) _loadingTicks = 0;

                    bool enteredByArea = name is not null && !isHub && !popupStillUp && areaChanged;
                    // The blank-tracker "loading" path only counts if you were standing somewhere with a
                    // READABLE tracker (armed area non-blank) that then went blank — a real load-out. If the
                    // tracker was blank the whole time (a hub / quest-giver spot with no active quest),
                    // a blank tracker means nothing, and opening+cancelling a popup there must NOT start a run.
                    bool enteredByLoad = _loadingTicks >= LoadDebounce && !string.IsNullOrEmpty(_armedArea);

                    if ((enteredByArea || enteredByLoad) && !lingering)
                    {
                        // Area-change debounces over a couple ticks (flicker guard); a sustained loading
                        // screen is already its own debounce, so it starts as soon as it's confirmed.
                        if (enteredByLoad || ++_pendingCount >= StartDebounce)
                        {
                            _current = NewRun(pe.Name) with { QuestLevel = pe.QuestLevel, Difficulty = pe.Difficulty, QuestDuration = pe.Duration };
                            _pendingEntry = null;   // consumed by this run
                            _sawEmptySinceFinalize = false;
                            _lastChatLines = Array.Empty<string>();   // fresh chat baseline for this run
                            _completionBaselined = false;             // re-baseline the completion rising-edge
                            _runXp = null;                            // fresh XP for this run
                            _pendingCount = 0;
                            _loadingTicks = 0;
                            Log($"started \"{pe.Name}\"{(enteredByLoad ? " (loading)" : "")}");
                            currentChanged = true;
                            currentSnapshot = _current;
                        }
                    }
                    else
                    {
                        _pendingCount = 0;
                        // Cancelled / stayed put: the popup is gone but you're still in the SAME non-blank
                        // area (a real entry would have blanked the tracker as it loaded). Clear the entry.
                        if (!popupStillUp && name is not null && !isHub && !areaChanged)
                        {
                            _pendingEntry = null;
                            _armedArea = null;
                            _loadingTicks = 0;
                        }
                        if (isHub || name is null) _sawEmptySinceFinalize = true;
                    }
                }
                else
                {
                    // No popup held → nothing here is a run. Just track the "empty since finalize" flag.
                    _pendingCount = 0;
                    _loadingTicks = 0;
                    if (isHub || name is null) _sawEmptySinceFinalize = true;
                }
            }

            // The "QUEST READY" card should show only WHILE the popup is on screen (seen within the last
            // few seconds), then clear — even though the entry stays armed internally to attach on entry.
            // So a Cancel makes the card go idle instead of sitting on READY forever.
            bool popupUp = _pendingEntry is not null && nowTick - _pendingEntryTick < PopupGoneMs;
            // Key by name, level AND difficulty so the "ready" card re-renders when ANY of them changes
            // (previously difficulty changes never refreshed the card — the detection updated but the UI didn't).
            string? shownKey = popupUp ? $"{_pendingEntry!.Name}|{_pendingEntry.QuestLevel}|{_pendingEntry.Difficulty}" : null;
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
        if (leftPromptShow is { } show) LeftPromptChanged?.Invoke(show);
    }

    // ---- LLM (OpenRouter) event escalation ----

    /// <summary>Fire ONE LLM read for this popup appearance (deduped by the local name's key). The LLM's
    /// answer owns name/level/duration; difficulty stays live-local (the user can re-click after the
    /// snapshot). Result lands asynchronously via <see cref="ApplyLlmEntry"/>.</summary>
    private void MaybeLlmEntry(QuestEntry localEntry, OpenCvMat popupCrop)
    {
        if (_llm is not { IsEnabled: true }) return;
        string key = Key(localEntry.Name);
        if (key.Length < 3) return;
        lock (_lock)
        {
            if (string.Equals(_llmEntryKey, key, StringComparison.Ordinal)) return;   // this popup was already escalated
            _llmEntryKey = key;
            _llmEntryValue = null;
        }
        OpenCvMat crop = popupCrop.Clone();
        _ = Task.Run(async () =>
        {
            try
            {
                QuestEntry? ai = await _llm.ReadEntryAsync(crop).ConfigureAwait(false);
                if (ai is not null) ApplyLlmEntry(ai, key);
                else Log($"llm-entry: no result for \"{localEntry.Name}\"");
            }
            catch (Exception ex) { Log($"llm-entry error: {ex.Message}"); }
            finally { crop.Dispose(); }
        });
    }

    private void ApplyLlmEntry(QuestEntry ai, string forKey)
    {
        RunRecord? updatedRun = null;
        QuestEntry? updatedPending = null;
        lock (_lock)
        {
            if (!string.Equals(_llmEntryKey, forKey, StringComparison.Ordinal)) return;   // a newer popup superseded this
            _llmEntryValue = ai;
            Log($"llm-entry: \"{ai.Name}\" L{ai.QuestLevel?.ToString() ?? "?"} diff={ai.Difficulty ?? "?"} dur={ai.Duration ?? "?"}");

            // The popup may still be pending, or its run may already be live — correct whichever matches.
            if (_pendingEntry is { } pe && Key(pe.Name) == forKey)
            {
                _pendingEntry = new QuestEntry(ai.Name, ai.QuestLevel ?? pe.QuestLevel, pe.Difficulty ?? ai.Difficulty, ai.Duration ?? pe.Duration);
                updatedPending = _pendingEntry;
                _shownEntryName = null;   // force the READY card to re-render with corrected fields
            }
            if (_current is { Completed: false, Edited: false } cur && Key(cur.DungeonName) == forKey)
            {
                _current = cur with
                {
                    DungeonName = ai.Name,
                    QuestLevel = ai.QuestLevel ?? cur.QuestLevel,
                    Difficulty = cur.Difficulty ?? ai.Difficulty,
                    QuestDuration = ai.Duration ?? cur.QuestDuration,
                };
                updatedRun = _current;
            }
        }
        if (updatedRun is not null) CurrentChanged?.Invoke(updatedRun);
        else if (updatedPending is not null) EntryHeld?.Invoke(updatedPending);
    }

    /// <summary>Fire ONE LLM read when a character (by local OCR name key) hasn't been LLM-confirmed yet —
    /// so each login/relog costs one call, and OCR jitter (absorbed by Key's normalization) costs none.</summary>
    private void MaybeLlmAvatar(CharacterInfo local, OpenCvMat avatarCrop)
    {
        if (_llm is not { IsEnabled: true }) return;
        string key = Key(local.Name);
        if (key.Length < 3) return;
        lock (_lock)
        {
            if (_llmCharDone.Contains(key)) return;                                  // already escalated (ok or failed)
            if (Interlocked.CompareExchange(ref _llmCharBusy, 1, 0) == 1) return;    // one in flight at a time
        }
        OpenCvMat crop = avatarCrop.Clone();
        _ = Task.Run(async () =>
        {
            try
            {
                CharacterInfo? ai = await _llm.ReadCharacterAsync(crop).ConfigureAwait(false);
                lock (_lock) _llmCharDone.Add(key);   // success or not, never re-pay for this key
                if (ai is null || string.IsNullOrWhiteSpace(ai.Name)) { Log("llm-char: no result"); return; }
                RunRecord? updatedRun = null;
                lock (_lock)
                {
                    string confirmed = ai.Name.Trim();
                    _llmCharNames[key] = confirmed;
                    _llmCharNames[Key(confirmed)] = confirmed;   // local may later read the TRUE spelling's key
                    _llmCharDone.Add(Key(confirmed));
                    _detectedName = confirmed;
                    if (ai.Level is not null) _detectedLevel = ai.Level;
                    Log($"llm-char: \"{confirmed}\" lvl={ai.Level?.ToString() ?? "?"}");
                    if (_current is { Completed: false, Edited: false } cur)
                    {
                        _current = cur with { CharacterName = confirmed, CharacterLevel = ai.Level ?? cur.CharacterLevel };
                        updatedRun = _current;
                    }
                }
                if (updatedRun is not null) CurrentChanged?.Invoke(updatedRun);
            }
            catch (Exception ex) { Log($"llm-char error: {ex.Message}"); }
            finally { crop.Dispose(); Interlocked.Exchange(ref _llmCharBusy, 0); }
        });
    }

    // ---- helpers (all called under _lock) ----

    private RunRecord FinalizeCurrent(string compRaw)
    {
        DateTime nowUtc = DateTime.UtcNow;
        // KEEP the run's own name (from the entry popup). Completion must never rename it to whatever the
        // tracker happens to read in-dungeon (which is garbage like "Recall"). XP comes from chat (_runXp),
        // regardless of whether the chat or the tracker signalled completion.
        RunRecord run = _current! with { CompletedUtc = nowUtc, Completed = true, Xp = _runXp ?? _current.Xp, RawOcrText = compRaw };
        _store.Add(run);
        Log($"finalized \"{run.DungeonName}\" xp={run.Xp?.ToString() ?? "?"}");
        _current = null;
        _emptySinceTick = 0;
        return run;
    }

    private void ResetPending() { _pendingCount = 0; _loadingTicks = 0; }

    private RunRecord NewRun(string name)
    {
        _leftTicks = 0;   // fresh "am I still in my quest?" streak for the new run
        (string? id, int? level) = _character();
        // Stamp the auto-detected character name + level (from the avatar region) when available.
        return new RunRecord(RunRecord.NewId(), name, null, _detectedLevel ?? level, id, DateTime.UtcNow, null, null,
            false, string.Empty, false, null, _detectedName);
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

    // A wilderness/explorer area, not a quest: DDO shows a "Slayer: <Area> Menaces (Heroic/Legendary)"
    // counter in the tracker region there. Matching either tell ("slayer" / "menace") is robust to the
    // ornate-font OCR garble seen in the log ("Slåyer:", "Menaces (Heroic"); no DDO quest title has these.
    // Scanned across EVERY tracker line, not just the cleaned header — the "Slayer" counter often isn't the
    // line the name-cleaner picks (it can carry a progress count, which the cleaner treats as an objective).
    private static bool IsWilderness(TrackerStatus t)
        => IsWildernessTracker(t.Name) || (t.Lines is not null && t.Lines.Any(IsWildernessTracker));

    private static bool IsWildernessTracker(string? name)
        => name is not null
           && (name.Contains("slayer", StringComparison.OrdinalIgnoreCase)
               || name.Contains("menace", StringComparison.OrdinalIgnoreCase));

    // "Does the tracker still show THIS run's quest?" The ornate title is the quest name in-instance; once
    // you leave it becomes the zone name. Checked across ALL lines (the title can drop to an objective read
    // some frames; the over-extended region may also carry extra lines) with a fuzzy match.
    private static bool TrackerShowsQuest(TrackerStatus t, string questName)
    {
        string q = Key(questName);
        if (q.Length < 4) return false;
        if (NameSimilar(t.Name, q)) return true;
        if (t.Lines is not null)
            foreach (string line in t.Lines)
                if (NameSimilar(line, q)) return true;
        return false;
    }

    // Fuzzy "these name the same quest": normalize to alnum-lowercase, accept containment (a noisier read
    // wrapping the name, or a clean name inside a longer line) or ≤40% edit distance (ornate-font OCR).
    private static bool NameSimilar(string? line, string qKey)
    {
        string k = Key(line);
        if (k.Length < 4) return false;
        if (k.Contains(qKey) || qKey.Contains(k)) return true;
        return Levenshtein(k, qKey) <= Math.Max(k.Length, qKey.Length) * 0.40;
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
        // Compare by EDIT DISTANCE, not shared prefix: OCR jitters the ornate area font by a char or two
        // ("eveningstar" -> "evemngstar"), and a prefix compare wrongly flagged that as a new area and
        // false-started runs. Only a substantially-different string is a real area change.
        return Levenshtein(a, b) > Math.Max(a.Length, b.Length) * 0.34;
    }

    private static int Levenshtein(string a, string b)
    {
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
