using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

// The developer console, like in CS: Ё (the key left of 1, KeyCode.BackQuote) or F1 opens and closes it, Escape closes it.
// A dark panel over the top half of the screen: the log (commands, answers, errors of the game), the input line with a
// caret and, under the panel, hints for the line being typed (matching commands, the weapons of give).
// Enter runs the line (several commands at once: "god 1; give awp"), Up / Down = history, Tab = completes the command
// (and the weapon of give, the team of bot_add ...), PageUp / PageDown or the mouse wheel scroll the log, Ctrl+V pastes.
// "help" lists the commands, "help <command>" explains one; Docs/console.md describes all of them.
// Commands ignore the case; a line typed in the Russian layout by mistake ("пщв" = "god") still works.
// While it is open (IsOpen, also in the frame it closes) the game ignores the keyboard and the mouse: the player stands,
// does not shoot or switch weapons, the menus do not open, the cursor is free.
// It adds itself to every scene with a RoundManager, never in simulations, batch mode or without graphics.
// Other scripts may run a line from code with DevConsole.Execute (the screenshot tests do).
[DefaultExecutionOrder(-200)]
public class DevConsole : MonoBehaviour
{
    // the console is on the screen (also in the frame it closes): movement, shooting, weapon keys and menus ignore the keys
    public static bool IsOpen => open || Time.frameCount == closedFrame;

    static bool open;
    static int closedFrame = -10;
    static DevConsole instance;
    // the log and the history stay when another map is loaded (not between Play sessions)
    static readonly List<Line> log = new List<Line>();
    static readonly List<string> history = new List<string>();
    static bool showFps;
    static bool welcomed;
    static string lastError = "";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        open = false;
        closedFrame = -10;
        instance = null;
        log.Clear();
        history.Clear();
        showFps = false;
        welcomed = false;
        lastError = "";
    }

    // ---------------- bootstrap ----------------

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        TryCreate(SceneManager.GetActiveScene());
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TryCreate(scene);
    }

    static bool SimulationFlag()
    {
        return System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-sim") >= 0;
    }

    // a console for a scene with rounds, never in a simulation or without graphics
    static void TryCreate(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;
        if (Application.isBatchMode || SimulationFlag()) return;
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

        RoundManager rm = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.GetComponentInChildren<DevConsole>(true) != null) return;   // already there
            if (rm == null) rm = root.GetComponentInChildren<RoundManager>(true);
        }
        if (rm == null || rm.simulation) return;

        GameObject go = new GameObject("DevConsole");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<DevConsole>();
    }

    // runs a console line from code (tests); false when this scene has no console
    public static bool Execute(string line)
    {
        if (instance == null) return false;
        instance.Run(line, true);
        return true;
    }

    // opens or closes the console from code (tests)
    public static void SetOpen(bool value)
    {
        if (instance == null) return;
        if (value && !open) instance.Open();
        else if (!value && open) instance.Close();
    }

    // ---------------- types and colours ----------------

    struct Line
    {
        public string text;
        public Color color;

        public Line(string text, Color color)
        {
            this.text = text;
            this.color = color;
        }
    }

    class Command
    {
        public string name;
        public string usage;          // "give <оружие>"
        public string description;    // one Russian line for help
        public string[] details;      // more lines for "help <command>": examples, notes
        public System.Action<string[]> run;
        public readonly List<string> aliases = new List<string>();
    }

    static readonly Color PanelBack = new Color(0.03f, 0.04f, 0.05f, 0.9f);
    static readonly Color Gold = new Color(1f, 0.78f, 0.3f);
    static readonly Color EchoColor = new Color(0.62f, 0.74f, 0.88f);
    static readonly Color TextColor = new Color(0.9f, 0.9f, 0.9f);
    static readonly Color OkColor = new Color(0.55f, 0.92f, 0.5f);
    static readonly Color ErrorColor = new Color(1f, 0.45f, 0.4f);
    static readonly Color HintColor = new Color(1f, 0.82f, 0.4f);
    static readonly Color DimColor = new Color(0.62f, 0.64f, 0.66f);

    const int MaxLogLines = 600;
    const int MaxHistory = 50;
    const int MaxInput = 256;
    const int MaxHints = 8;
    const string Title = "КОНСОЛЬ РАЗРАБОТЧИКА";
    const string HeaderHint = "Ё / F1 / Esc — закрыть   ·   Enter — выполнить   ·   Tab — дополнить   ·   Вверх/Вниз — история   ·   PgUp/PgDn — прокрутка";
    const string ShortHeaderHint = "Esc — закрыть · Tab — дополнить · PgUp/PgDn — прокрутка";

    // the level for rays (bot_place): not the capsules (Ignore Raycast) and not the player's own model (LocalBody)
    const int WorldMask = Physics.DefaultRaycastLayers & ~(1 << 2) & ~(1 << 9);

    // ---------------- state ----------------

    RoundManager rounds;
    Health player;
    PlayerController playerController;
    PlayerLoadout loadout;

    readonly List<Command> commands = new List<Command>();
    readonly Dictionary<string, Command> byName = new Dictionary<string, Command>();
    // an alias stands for a command line: "freeze" = "bot_stop 1"; the list keeps the order for the hints
    readonly Dictionary<string, string> aliasLines = new Dictionary<string, string>();
    readonly List<string> aliasOrder = new List<string>();

    string input = "";
    int caret;
    int historyIndex = -1;   // -1 = the line being typed, else a line of the history
    string draft = "";
    int scroll;              // lines scrolled up from the newest one
    int pageLines = 10;
    float typedAt;

    // Tab: after the common start of the candidates every press shows the next one
    bool tabCycling;
    readonly List<string> tabList = new List<string>();
    int tabIndex;
    string tabBefore = "";
    string tabTail = "";
    string tabShown = "";

    // a held arrow / Delete / PageUp repeats
    KeyCode repeatKey = KeyCode.None;
    float repeatAt;

    int openedFrame = -10;
    float charToggleBlockUntil;

    // the speed set by "timescale" (0 = not set): the pause menu sets 0 and then 1, the console puts it back
    float wantedTimeScale;

    // fps counter
    float fpsTime;
    int fpsFrames;
    float fpsValue;
    string fpsText = "";

    // hints under the panel for the line being typed
    readonly List<Line> hints = new List<Line>();
    string hintsFor;

    // gui
    GUIStyle textStyle, titleStyle, hintStyle, fpsStyle;
    Font mono;
    bool fontTried;
    int measuredSize = -1;
    float lineHeight = 20f;

    // ---------------- life ----------------

    void Awake()
    {
        // background runs pass -sim on the command line; RoundManager reads it only in its Start
        RoundManager rm = FindAnyObjectByType<RoundManager>();
        if ((rm != null && rm.simulation) || SimulationFlag() || Application.isBatchMode)
        {
            enabled = false;
            return;
        }
        instance = this;
        rounds = rm;
        BuildCommands();
    }

    void OnEnable()
    {
        Application.logMessageReceived += OnUnityLog;
    }

    void OnDisable()
    {
        Application.logMessageReceived -= OnUnityLog;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        // the scene is left with the console open: the next scene starts with it closed
        if (open)
        {
            open = false;
            closedFrame = Time.frameCount;
        }
        if (mono != null) Destroy(mono);
    }

    // errors and exceptions of the game are shown in the log too (not the normal Debug.Log lines)
    void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        string text = condition ?? "";
        int nl = text.IndexOf('\n');
        if (nl >= 0) text = text.Substring(0, nl);
        if (text == lastError) return;
        lastError = text;
        Print("[ошибка игры] " + text, ErrorColor);
    }

    void Open()
    {
        open = true;
        openedFrame = Time.frameCount;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        scroll = 0;
        typedAt = Time.unscaledTime;
        if (!welcomed)
        {
            welcomed = true;
            Print("Консоль разработчика. Ё или F1 — открыть и закрыть, Esc — закрыть.", Gold);
            Print("Введите help — список команд", HintColor);
        }
    }

    void Close()
    {
        open = false;
        closedFrame = Time.frameCount;
        tabCycling = false;
        // back to the game: the cursor is locked again, unless a menu keeps it (the pause menu, the buy menu)
        if (!MainMenu.IsOpen && !BuyMenu.IsOpen)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            // the mouse jump of the lock and this frame's click are not a turn or a shot
            MainMenu.LastClosedFrame = Time.frameCount;
        }
    }

    void Update()
    {
        CountFps();
        KeepTimeScale();

        bool toggle = ToggleKeyDown();
        if (!open)
        {
            if (toggle) Open();
            return;
        }

        // the key that opened the console is not typed into it
        if (Time.frameCount == openedFrame) return;

        if (toggle || Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
            return;
        }
        Typing();
    }

    void LateUpdate()
    {
        // nothing may lock the cursor under the console
        if (open && Cursor.lockState != CursorLockMode.None)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    // ` ~ ё Ё: the characters of the console key (in any layout); they are never typed into the line
    static bool IsToggleChar(char c)
    {
        return c == '`' || c == '~' || c == 'ё' || c == 'Ё';
    }

    // the console key went down: BackQuote or F1; some layouts do not report the key as BackQuote, then its character
    // counts (but not the auto-repeat of a held key)
    bool ToggleKeyDown()
    {
        bool key = Input.GetKeyDown(KeyCode.BackQuote) || Input.GetKeyDown(KeyCode.F1);
        bool held = Input.GetKey(KeyCode.BackQuote);

        bool typed = false;
        string s = Input.inputString;
        for (int i = 0; i < s.Length; i++)
            if (IsToggleChar(s[i])) typed = true;

        if (typed)
        {
            bool fresh = Time.unscaledTime >= charToggleBlockUntil && !held && !key;
            // every repeat of a held key pushes the block further (the first repeat comes about 0.5 s after the press)
            charToggleBlockUntil = Time.unscaledTime + 0.6f;
            if (fresh) key = true;
        }
        return key;
    }

    // ---------------- typing ----------------

    void Typing()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            Submit();
            return;
        }

        if (ctrl && Input.GetKeyDown(KeyCode.V)) Insert(ClipboardLine());
        else if (ctrl && Input.GetKeyDown(KeyCode.L))
        {
            log.Clear();
            scroll = 0;
        }

        // typed characters (Backspace comes as '\b' with the system's auto-repeat); with Ctrl held they are shortcuts
        string s = Input.inputString;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\b') Backspace();
            else if (ctrl && !alt) continue;
            else if (!char.IsControl(c) && !IsToggleChar(c)) Insert(c.ToString());
        }

        if (KeyRepeat(KeyCode.Delete) && caret < input.Length)
        {
            input = input.Remove(caret, 1);
            Edited();
        }
        if (KeyRepeat(KeyCode.LeftArrow)) MoveCaret(caret - 1);
        if (KeyRepeat(KeyCode.RightArrow)) MoveCaret(caret + 1);
        if (Input.GetKeyDown(KeyCode.Home)) MoveCaret(0);
        if (Input.GetKeyDown(KeyCode.End)) MoveCaret(input.Length);
        if (KeyRepeat(KeyCode.UpArrow)) HistoryUp();
        if (KeyRepeat(KeyCode.DownArrow)) HistoryDown();
        if (KeyRepeat(KeyCode.PageUp)) scroll += Mathf.Max(1, pageLines - 2);
        if (KeyRepeat(KeyCode.PageDown)) scroll = Mathf.Max(0, scroll - Mathf.Max(1, pageLines - 2));
        if (Input.GetKeyDown(KeyCode.Tab)) Autocomplete();
    }

    // true on the press and then, while the key is held, about 28 times a second (one key at a time)
    bool KeyRepeat(KeyCode key)
    {
        float now = Time.unscaledTime;
        if (Input.GetKeyDown(key))
        {
            repeatKey = key;
            repeatAt = now + 0.45f;
            return true;
        }
        if (repeatKey == key && Input.GetKey(key) && now >= repeatAt)
        {
            repeatAt = now + 0.035f;
            return true;
        }
        return false;
    }

    static string ClipboardLine()
    {
        string s = GUIUtility.systemCopyBuffer ?? "";
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) s = s.Substring(0, nl);
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsControl(c) && !IsToggleChar(c)) sb.Append(c);
        return sb.ToString();
    }

    void Insert(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        if (input.Length + s.Length > MaxInput) s = s.Substring(0, Mathf.Max(0, MaxInput - input.Length));
        if (s.Length == 0) return;
        input = input.Insert(caret, s);
        caret += s.Length;
        Edited();
    }

    void Backspace()
    {
        if (caret <= 0) return;
        input = input.Remove(caret - 1, 1);
        caret--;
        Edited();
    }

    void MoveCaret(int to)
    {
        caret = Mathf.Clamp(to, 0, input.Length);
        typedAt = Time.unscaledTime;
    }

    // the line was changed by hand: it is the line being typed now (not a line of the history), Tab starts again
    void Edited()
    {
        historyIndex = -1;
        draft = input;
        tabCycling = false;
        typedAt = Time.unscaledTime;
    }

    void SetInput(string s)
    {
        input = s ?? "";
        caret = input.Length;
        typedAt = Time.unscaledTime;
    }

    void Submit()
    {
        string line = input.Trim();
        SetInput("");
        historyIndex = -1;
        draft = "";
        tabCycling = false;
        scroll = 0;
        if (line.Length == 0) return;

        if (history.Count == 0 || history[history.Count - 1] != line) history.Add(line);
        if (history.Count > MaxHistory) history.RemoveAt(0);
        Run(line, true);
    }

    void HistoryUp()
    {
        if (history.Count == 0) return;
        if (historyIndex < 0)
        {
            draft = input;
            historyIndex = history.Count - 1;
        }
        else if (historyIndex > 0)
        {
            historyIndex--;
        }
        SetInput(history[historyIndex]);
        tabCycling = false;
    }

    void HistoryDown()
    {
        if (historyIndex < 0) return;
        historyIndex++;
        if (historyIndex >= history.Count)
        {
            historyIndex = -1;
            SetInput(draft);
        }
        else
        {
            SetInput(history[historyIndex]);
        }
        tabCycling = false;
    }

    // ---------------- Tab ----------------

    void Autocomplete()
    {
        // the same Tab again: the next candidate
        if (tabCycling && input == tabShown && tabList.Count > 0)
        {
            tabIndex = (tabIndex + 1) % tabList.Count;
            SetAround(tabBefore + tabList[tabIndex], tabTail);
            tabShown = input;
            return;
        }
        tabCycling = false;

        // the word left of the caret is completed; the text right of it stays
        string head = input.Substring(0, caret);
        string tail = input.Substring(caret);
        int cut = Mathf.Max(head.LastIndexOf(' '), head.LastIndexOf(';'));
        string before = head.Substring(0, cut + 1);
        string token = head.Substring(cut + 1);
        // only the command after the last ';' counts ("god 1; giv")
        string segment = before.Substring(before.LastIndexOf(';') + 1);

        List<string> found = Candidates(segment, token);
        if (found.Count == 0) return;

        if (found.Count == 1)
        {
            SetAround(before + found[0] + (tail.StartsWith(" ") ? "" : " "), tail);
            return;
        }

        string common = CommonStart(found);
        if (common.Length > token.Length)
        {
            SetAround(before + common, tail);
            return;
        }

        // nothing more in common: the candidates go to the log, every Tab shows the next one
        Print("  " + string.Join("   ", found), DimColor);
        tabList.Clear();
        tabList.AddRange(found);
        tabIndex = 0;
        tabBefore = before;
        tabTail = tail;
        SetAround(before + found[0], tail);
        tabShown = input;
        tabCycling = true;
    }

    // the line = head + tail, the caret between them
    void SetAround(string head, string tail)
    {
        input = head + tail;
        if (input.Length > MaxInput) input = input.Substring(0, MaxInput);
        caret = Mathf.Min(head.Length, input.Length);
        typedAt = Time.unscaledTime;
    }

    // what may stand at the place of the token: a command (first word) or an argument of the command
    List<string> Candidates(string before, string token)
    {
        List<string> list = new List<string>();
        string t = token.ToLowerInvariant();
        string[] words = SplitWords(before);

        if (words.Length == 0)
        {
            bool cyrillic = HasCyrillic(t);
            foreach (Command c in commands)
                if (c.name.StartsWith(t, System.StringComparison.Ordinal)) list.Add(c.name);
            foreach (string a in aliasOrder)
                if (a.StartsWith(t, System.StringComparison.Ordinal) && HasCyrillic(a) == cyrillic && !list.Contains(a)) list.Add(a);
            return list;
        }

        string cmd = CommandName(words[0]);
        string nt = Normalize(t);
        switch (cmd)
        {
            case "give":
                if (words.Length > 1) break;
                foreach (string[] w in WeaponNames)
                    if (w[1].StartsWith(nt, System.StringComparison.Ordinal)) list.Add(w[1]);
                if ("auto".StartsWith(nt, System.StringComparison.Ordinal)) list.Add("auto");
                break;
            case "help":
                if (words.Length > 1) break;
                foreach (Command c in commands)
                    if (c.name.StartsWith(t, System.StringComparison.Ordinal)) list.Add(c.name);
                break;
            case "bot_add":
            case "bot_place":
                AddMatching(list, t, "t", "ct");
                break;
            case "bot_kill":
                AddMatching(list, t, "t", "ct", "all");
                break;
            case "endround":
                AddMatching(list, t, "t", "ct", "draw");
                break;
        }
        return list;
    }

    static void AddMatching(List<string> list, string t, params string[] options)
    {
        foreach (string o in options)
            if (o.StartsWith(t, System.StringComparison.Ordinal)) list.Add(o);
    }

    static string CommonStart(List<string> list)
    {
        string common = list[0];
        foreach (string s in list)
        {
            int n = 0;
            while (n < common.Length && n < s.Length && common[n] == s[n]) n++;
            common = common.Substring(0, n);
        }
        return common;
    }

    // ---------------- running commands ----------------

    void Run(string line, bool echo)
    {
        line = (line ?? "").Trim();
        if (line.Length == 0) return;
        if (echo) Print("> " + line, EchoColor);

        foreach (string part in line.Split(';'))
            RunOne(part.Trim(), true);
    }

    void RunOne(string text, bool tryLayout)
    {
        if (text.Length == 0) return;
        List<string> words = new List<string>(SplitWords(text));
        if (words.Count == 0) return;

        // an alias stands for a command with arguments: "freeze" = "bot_stop 1"
        string head = words[0].ToLowerInvariant();
        if (aliasLines.TryGetValue(head, out string target))
        {
            words.RemoveAt(0);
            words.InsertRange(0, SplitWords(target));
            head = words[0];
        }

        if (byName.TryGetValue(head, out Command cmd))
        {
            words.RemoveAt(0);
            Invoke(cmd, words.ToArray());
            return;
        }

        // typed in the Russian layout by mistake: "пщв" is "god"
        if (tryLayout && HasCyrillic(text))
        {
            string latin = FromRussianLayout(text);
            string[] lw = SplitWords(latin);
            if (lw.Length > 0 && IsKnown(lw[0]))
            {
                Print("(русская раскладка: " + latin + ")", DimColor);
                RunOne(latin, false);
                return;
            }
        }

        string near = Closest(head);
        if (near == null && HasCyrillic(head)) near = Closest(FromRussianLayout(head).ToLowerInvariant());
        Print("Неизвестная команда: " + head + (near != null ? ". Может быть, " + near + "?" : "") + "   (help — список команд)", ErrorColor);
    }

    void Invoke(Command cmd, string[] args)
    {
        try
        {
            cmd.run(args);
        }
        catch (System.Exception e)
        {
            Print("Ошибка в команде " + cmd.name + ": " + e.Message, ErrorColor);
        }
    }

    bool IsKnown(string word)
    {
        string w = word.ToLowerInvariant();
        return byName.ContainsKey(w) || aliasLines.ContainsKey(w);
    }

    // the command a word stands for (an alias gives its command), "" when none
    string CommandName(string word)
    {
        string w = word.ToLowerInvariant();
        if (aliasLines.TryGetValue(w, out string target))
        {
            string[] tw = SplitWords(target);
            w = tw.Length > 0 ? tw[0] : "";
        }
        return byName.ContainsKey(w) ? w : "";
    }

    Command FindCommand(string word)
    {
        string name = CommandName(word);
        if (name.Length == 0 && HasCyrillic(word)) name = CommandName(FromRussianLayout(word));
        return name.Length > 0 ? byName[name] : null;
    }

    // the command or alias nearest to a mistyped word (a few letters wrong), null when nothing is close
    string Closest(string word)
    {
        string best = null;
        int bestDistance = int.MaxValue;
        int limit = Mathf.Max(2, word.Length / 3);
        foreach (string name in AllNames())
        {
            // a unique start ("bot_p") is the nearest anyway
            int d = name.StartsWith(word, System.StringComparison.Ordinal) ? 1 : Distance(word, name);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = name;
            }
        }
        return bestDistance <= limit ? best : null;
    }

    IEnumerable<string> AllNames()
    {
        foreach (Command c in commands) yield return c.name;
        foreach (string a in aliasOrder) yield return a;
    }

    // the Levenshtein distance: letters to change, add or remove
    static int Distance(string a, string b)
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
                cur[j] = Mathf.Min(Mathf.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            int[] t = prev;
            prev = cur;
            cur = t;
        }
        return prev[b.Length];
    }

    // words split by spaces; "quoted text" is one word
    static string[] SplitWords(string text)
    {
        List<string> words = new List<string>();
        StringBuilder sb = new StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    words.Add(sb.ToString());
                    sb.Length = 0;
                }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words.ToArray();
    }

    static bool HasCyrillic(string s)
    {
        foreach (char c in s)
            if ((c >= 'А' && c <= 'я') || c == 'ё' || c == 'Ё') return true;
        return false;
    }

    // the keys of the Russian layout give the letters of the same keys in the English one ("пщв" -> "god")
    const string RuKeys = "йцукенгшщзхъфывапролджэячсмитьбюЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ";
    const string EnKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>";

    static string FromRussianLayout(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            int i = RuKeys.IndexOf(c);
            sb.Append(i >= 0 ? EnKeys[i] : c);
        }
        return sb.ToString();
    }

    // ---------------- log ----------------

    static void Print(string text, Color color)
    {
        foreach (string part in (text ?? "").Split('\n'))
            log.Add(new Line(part.TrimEnd('\r'), color));
        if (log.Count > MaxLogLines) log.RemoveRange(0, log.Count - MaxLogLines);
    }

    static void Say(string text) { Print(text, TextColor); }
    static void Ok(string text) { Print(text, OkColor); }
    static void Error(string text) { Print(text, ErrorColor); }
    static void Hint(string text) { Print(text, HintColor); }

    static string OnOff(bool on) { return on ? "вкл" : "выкл"; }

    // ---------------- the game objects ----------------

    bool FindGame()
    {
        if (rounds == null) rounds = RoundManager.Current;
        if (player == null && rounds != null) player = rounds.player;
        if (player == null)
        {
            PlayerController pc = FindAnyObjectByType<PlayerController>(FindObjectsInactive.Include);
            if (pc != null) player = pc.GetComponent<Health>();
        }
        if (player != null)
        {
            if (playerController == null) playerController = player.GetComponent<PlayerController>();
            if (loadout == null) loadout = player.GetComponent<PlayerLoadout>();
        }
        return rounds != null;
    }

    bool NeedRounds()
    {
        if (FindGame()) return true;
        Error("На этой сцене нет игры");
        return false;
    }

    bool NeedPlayer()
    {
        FindGame();
        if (player != null) return true;
        Error("На этой сцене нет игрока");
        return false;
    }

    bool PlayerAlive => player != null && !player.IsDead && player.gameObject.activeInHierarchy;

    bool NeedAlive()
    {
        if (!NeedPlayer()) return false;
        if (PlayerAlive) return true;
        Error("Вы мертвы — дождитесь следующего раунда");
        return false;
    }

    // the team of the player's enemies (CT when the player has no team)
    int EnemyTeam => player != null && (player.team == 0 || player.team == 1) ? 1 - player.team : 1;

    static string TeamName(int team) { return team == 0 ? "T" : team == 1 ? "CT" : "все"; }

    // 0 = T, 1 = CT, -1 = all, -2 = not a team word
    static int ParseTeam(string s, bool numbers)
    {
        switch (s.ToLowerInvariant())
        {
            case "t": case "т": case "terrorists": case "terrorist": case "террористы": return 0;
            case "ct": case "кт": case "спецназ": return 1;
            case "all": case "все": case "всех": return -1;
        }
        if (numbers)
        {
            if (s == "0") return 0;
            if (s == "1") return 1;
        }
        return -2;
    }

    static bool TryFloat(string s, out float v)
    {
        return float.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    static bool TryInt(string s, out int v)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return true;
        // "16000.0", "1e4"
        if (TryFloat(s, out float f) && f > int.MinValue && f < int.MaxValue)
        {
            v = Mathf.RoundToInt(f);
            return true;
        }
        return false;
    }

    // "1 / on / вкл", "0 / off / выкл"; no argument = the opposite of now
    bool TrySwitch(string[] args, bool now, out bool value)
    {
        value = !now;
        if (args.Length == 0) return true;
        switch (args[0].ToLowerInvariant())
        {
            case "1": case "on": case "true": case "yes": case "вкл": case "да": case "+":
                value = true;
                return true;
            case "0": case "off": case "false": case "no": case "выкл": case "нет": case "-":
                value = false;
                return true;
        }
        Error("Нужно 0 или 1 (без числа — переключить)");
        return false;
    }

    // where the player looks from: his camera (after death the camera of the watched teammate)
    Transform ViewTransform()
    {
        FindGame();
        if (playerController != null && playerController.cameraTransform != null) return playerController.cameraTransform;
        return Camera.main != null ? Camera.main.transform : null;
    }

    // ---------------- weapons for give ----------------

    // the weapon (WeaponData.weaponName), then its short names; the first short name is the one Tab offers
    static readonly string[][] WeaponNames =
    {
        new[] { "AK-47", "ak47", "ak", "калаш" },
        new[] { "M4A4", "m4a4", "m4" },
        new[] { "M4A1-S", "m4a1s", "m4a1", "m4s" },
        new[] { "AWP", "awp", "авп" },
        new[] { "Desert Eagle", "deagle", "deag", "de", "дигл" },
        new[] { "USP-S", "usps", "usp" },
        new[] { "Glock-18", "glock", "glock18" },
        new[] { "P2000", "p2000", "p2k" },
        new[] { "P250", "p250" },
        new[] { "Five-SeveN", "fiveseven", "five7", "57" },
        new[] { "Tec-9", "tec9", "tec" },
        new[] { "CZ75-Auto", "cz75", "cz", "cz75a" },
        new[] { "Dual Berettas", "elite", "dualies", "berettas", "duals" },
        new[] { "R8 Revolver", "r8", "revolver" },
        new[] { "MAC-10", "mac10", "mac" },
        new[] { "MP9", "mp9" },
        new[] { "MP7", "mp7" },
        new[] { "MP5-SD", "mp5sd", "mp5" },
        new[] { "UMP-45", "ump45", "ump" },
        new[] { "P90", "p90" },
        new[] { "PP-Bizon", "bizon", "ppbizon" },
        new[] { "Nova", "nova" },
        new[] { "XM1014", "xm1014", "xm" },
        new[] { "Sawed-Off", "sawedoff", "sawed", "sawnoff" },
        new[] { "MAG-7", "mag7", "mag" },
        new[] { "M249", "m249" },
        new[] { "Negev", "negev" },
        new[] { "FAMAS", "famas" },
        new[] { "Galil AR", "galil", "galilar" },
        new[] { "SG 553", "sg553", "sg", "sg556", "krieg" },
        new[] { "AUG", "aug" },
        new[] { "SSG 08", "ssg08", "ssg", "scout" },
        new[] { "SCAR-20", "scar20", "scar" },
        new[] { "G3SG1", "g3sg1", "g3" },
        new[] { "Zeus x27", "zeus", "taser", "x27" },
        new[] { "Knife", "knife", "нож" },
    };

    // lowercase letters and digits only: "M4A1-S" -> "m4a1s", "Desert Eagle" -> "deserteagle"
    static string Normalize(string s)
    {
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char ch in s.ToLowerInvariant())
        {
            char c = ch == 'ё' ? 'е' : ch;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    // "auto": the auto sniper of the player's side (T: G3SG1, CT: SCAR-20)
    string AutoSniper => player != null && player.team == 1 ? "SCAR-20" : "G3SG1";

    WeaponData ExactWeapon(string t)
    {
        if (t.Length == 0) return null;
        if (t == "auto" || t == "автомат") return Economy.Find(AutoSniper);
        foreach (string[] w in WeaponNames)
            for (int i = 1; i < w.Length; i++)
                if (w[i] == t) return Economy.Find(w[0]);
        foreach (WeaponData d in Economy.Weapons)
            if (d != null && (Normalize(d.weaponName) == t || Normalize(d.name) == t)) return d;
        return null;
    }

    WeaponData ResolveWeapon(string text, out string problem)
    {
        problem = null;
        string t = Normalize(text);
        if (t.Length == 0)
        {
            problem = "Какое оружие? Например: give ak47   (give без имени — список)";
            return null;
        }

        WeaponData d = ExactWeapon(t);
        if (d == null && HasCyrillic(text)) d = ExactWeapon(Normalize(FromRussianLayout(text)));
        if (d != null) return d;

        // a unique start of a name: "dese" -> Desert Eagle
        List<WeaponData> found = new List<WeaponData>();
        foreach (string[] w in WeaponNames)
        {
            bool match = Normalize(w[0]).StartsWith(t, System.StringComparison.Ordinal);
            for (int i = 1; i < w.Length && !match; i++) match = w[i].StartsWith(t, System.StringComparison.Ordinal);
            WeaponData wd = match ? Economy.Find(w[0]) : null;
            if (wd != null && !found.Contains(wd)) found.Add(wd);
        }
        foreach (WeaponData wd in Economy.Weapons)
            if (wd != null && !found.Contains(wd) && Normalize(wd.weaponName).StartsWith(t, System.StringComparison.Ordinal)) found.Add(wd);

        if (found.Count == 1) return found[0];
        if (found.Count > 1)
        {
            List<string> names = new List<string>();
            foreach (WeaponData wd in found) names.Add(wd.weaponName);
            problem = "Подходит несколько: " + string.Join(", ", names) + ". Уточните";
        }
        else
        {
            problem = "Нет такого оружия: " + text + "   (give без имени — список)";
        }
        return null;
    }

    // "ak47 — AK-47" lines of all weapons, several per line
    void PrintWeaponList()
    {
        StringBuilder sb = new StringBuilder();
        int inLine = 0;
        foreach (string[] w in WeaponNames)
        {
            if (Economy.Find(w[0]) == null) continue;
            string item = (w[1] + " — " + w[0]).PadRight(24);
            sb.Append(item);
            if (++inLine == 4)
            {
                Say("  " + sb.ToString().TrimEnd());
                sb.Length = 0;
                inLine = 0;
            }
        }
        if (sb.Length > 0) Say("  " + sb.ToString().TrimEnd());
        Print("  auto — автоматическая снайперка своей стороны (T: G3SG1, CT: SCAR-20); можно и полное имя: give desert eagle", DimColor);
    }

    // ---------------- the commands ----------------

    Command Add(string name, string usage, string description, System.Action<string[]> run, params string[] details)
    {
        Command c = new Command { name = name, usage = usage, description = description, run = run, details = details };
        commands.Add(c);
        byName[name] = c;
        return c;
    }

    // alias = a short word for a command line ("freeze" = "bot_stop 1")
    void Alias(string alias, string line)
    {
        aliasLines[alias] = line;
        aliasOrder.Add(alias);
        string[] w = SplitWords(line);
        if (w.Length > 0 && byName.TryGetValue(w[0], out Command c))
            c.aliases.Add(w.Length > 1 ? alias + " (= " + line + ")" : alias);
    }

    void BuildCommands()
    {
        Add("help", "help [команда]", "список команд; help <команда> — подробно о команде", CmdHelp,
            "Пример: help give");
        Alias("помощь", "help");
        Alias("?", "help");

        Add("clear", "clear", "очистить консоль", a => { log.Clear(); scroll = 0; }, "То же самое — Ctrl+L.");
        Alias("cls", "clear");
        Alias("очистить", "clear");

        Add("bot_stop", "bot_stop [0|1]", "боты стоят на месте и не стреляют (без числа — переключить)", CmdBotStop,
            "Замороженных ботов можно убить. Примеры: bot_stop 1 · freeze · unfreeze");
        Alias("freeze", "bot_stop 1");
        Alias("unfreeze", "bot_stop 0");
        Alias("заморозить", "bot_stop 1");
        Alias("разморозить", "bot_stop 0");

        Add("god", "god [0|1]", "бессмертие: урон по вам не проходит", CmdGod,
            "Попадания по вам видны (красные дуги), но здоровье не уменьшается. kill всё равно убивает.");
        Alias("бог", "god");

        Add("give", "give <оружие>", "выдать себе оружие бесплатно: give ak47, give awp, give deagle", CmdGive,
            "Регистр, пробелы и дефисы не важны, хватает начала имени (give dese). Tab дополняет имя.",
            "Старое оружие этого слота падает на пол (в бою). give без имени — список всех имён.",
            "Примеры: give ak47 · give m4a1s · give usp · give scout · give auto · give zeus · give knife");
        Alias("дать", "give");

        Add("ammo", "ammo [0|1]", "бесконечные патроны у всех, без перезарядки", CmdAmmo,
            "В тренировке включено с самого начала. Пример: ammo 1");
        Alias("sv_infinite_ammo", "ammo");
        Alias("патроны", "ammo");

        Add("money", "money [сумма]", "поставить себе деньги (без суммы — $16000, больше нельзя)", CmdMoney,
            "Только в бою (в тренировке всё бесплатно). Примеры: money · money 800 · money 4750");
        Alias("деньги", "money");

        Add("buyanywhere", "buyanywhere [0|1]", "покупать в любом месте и в любое время (B; за деньги)", CmdBuyAnywhere,
            "Снимает ограничения зоны и 20 секунд закупки, в том числе в перерыве между раундами.");
        Alias("купитьвезде", "buyanywhere");

        Add("bot_add", "bot_add [t|ct] [число]", "добавить ботов перед собой (по умолчанию 1 противник)", CmdBotAdd,
            "Не больше 20 за раз. Боты встают в нескольких метрах перед вами, лицом к вам, и играют до конца матча",
            "(каждый раунд появляются со своей командой; restart их убирает).",
            "Примеры: bot_add · bot_add 3 · bot_add ct 5 · bot_add t");
        Alias("добавить", "bot_add");

        Add("bot_kill", "bot_kill [t|ct|all]", "убить ботов (по умолчанию всех)", CmdBotKill,
            "Деньги за таких убийц никому не начисляются. Во время фризтайма раунд закончится, когда он пройдёт.",
            "Примеры: bot_kill · bot_kill ct · bot_kill t");
        Alias("убитьботов", "bot_kill");

        Add("kill", "kill", "убить себя", CmdKill, "Работает и с включённым god. В тренировке вы сразу появитесь снова.");
        Alias("суицид", "kill");

        Add("bot_place", "bot_place [t|ct]", "поставить ближайшего бота-противника туда, куда вы смотрите", CmdBotPlace,
            "Смотрите на пол (или стену) не дальше 300 м. Удобно вместе с bot_stop 1. Пример: bot_place · bot_place ct");
        Alias("поставить", "bot_place");

        Add("restart", "restart", "новый матч: счёт 0:0, $800, исходные стороны", CmdRestart,
            "Боты, добавленные bot_add, уходят. В тренировке — просто вернуться на спавн.");
        Alias("mp_restartgame", "restart");
        Alias("рестарт", "restart");

        Add("endround", "endround [t|ct|draw]", "закончить раунд сейчас (по умолчанию ничья)", CmdEndRound,
            "Счёт, деньги за раунд и MVP — как при обычном конце раунда. Примеры: endround · endround t · endround ct");
        Alias("конецраунда", "endround");

        Add("freezetime", "freezetime [сек]", "фризтайм в начале раунда (со следующего раунда)", CmdFreezeTime,
            "Без числа — показать текущее значение. 0 — без фризтайма. Пример: freezetime 0");
        Alias("фризтайм", "freezetime");

        Add("roundtime", "roundtime [сек]", "длина раунда в секундах (со следующего раунда)", CmdRoundTime,
            "Без числа — показать текущее значение (5–3600). Пример: roundtime 120");
        Alias("времяраунда", "roundtime");

        Add("buytime", "buytime [сек]", "время закупки в секундах (уже в этом раунде)", CmdBuyTime,
            "Без числа — показать текущее значение (0–3600). Пример: buytime 60");
        Alias("времязакупки", "buytime");

        Add("maxrounds", "maxrounds [число]", "длина матча в раундах (смена сторон после половины)", CmdMaxRounds,
            "Без числа — показать текущее значение (1–100). Пример: maxrounds 30 (смена сторон после 15, победа — 16)");
        Alias("раунды", "maxrounds");

        Add("timescale", "timescale [0.1–4]", "скорость игры (1 — обычная)", CmdTimeScale,
            "Без числа — показать текущую. Примеры: timescale 0.3 (замедление) · timescale 2 · timescale 1");
        Alias("host_timescale", "timescale");
        Alias("скорость", "timescale");

        Add("noclip", "noclip [0|1]", "полёт сквозь стены: WASD, Пробел — вверх, C/Ctrl — вниз, Shift — быстрее", CmdNoclip,
            "Летите туда, куда смотрите; за пределы карты не вылететь. Левый Ctrl в проекте ещё и стреляет (кнопка Fire1) —",
            "чтобы спускаться без выстрелов, используйте C.");
        Alias("полет", "noclip");
        Alias("полёт", "noclip");

        Add("ff", "ff [0|1]", "огонь по своим (союзники получают треть урона)", CmdFriendlyFire,
            "В обычной игре включён. Пример: ff 0");
        Alias("friendlyfire", "ff");
        Alias("mp_friendlyfire", "ff");

        Add("fps", "fps [0|1]", "показывать FPS в правом верхнем углу", CmdFps, "Пример: fps 1");
        Alias("cl_showfps", "fps");
        Alias("фпс", "fps");

        Add("hp", "hp [число]", "поставить себе здоровье (без числа — 100)", CmdHp, "От 1 до 1000. Пример: hp 1 · hp 500");
        Alias("health", "hp");
        Alias("здоровье", "hp");

        Add("status", "status", "что сейчас: карта, счёт, живые, деньги, включённые команды", CmdStatus);
        Alias("статус", "status");
    }

    void CmdHelp(string[] a)
    {
        if (a.Length > 0)
        {
            Command c = FindCommand(a[0]);
            if (c == null)
            {
                string near = Closest(a[0].ToLowerInvariant());
                Error("Нет такой команды: " + a[0] + (near != null ? ". Может быть, " + near + "?" : ""));
                return;
            }
            Print(c.usage, Gold);
            Say("  " + c.description);
            if (c.aliases.Count > 0) Print("  Другие имена: " + string.Join(", ", c.aliases), DimColor);
            if (c.details != null)
                foreach (string d in c.details) Say("  " + d);
            return;
        }

        int width = 0;
        foreach (Command c in commands) width = Mathf.Max(width, c.usage.Length);
        Print("Команды (help <команда> — подробно, Tab — дополнить, несколько команд — через ;):", Gold);
        foreach (Command c in commands) Say("  " + c.usage.PadRight(width + 2) + c.description);
        Print("  У команд есть русские имена (help god покажет их); можно печатать и в русской раскладке.", DimColor);
    }

    void CmdBotStop(string[] a)
    {
        if (!NeedRounds()) return;
        if (!TrySwitch(a, RoundManager.BotsFrozen, out bool on)) return;
        rounds.SetBotsFrozen(on);
        Ok(on ? "Боты заморожены: стоят на месте и не стреляют" : "Боты разморожены");
    }

    void CmdGod(string[] a)
    {
        if (!NeedPlayer()) return;
        if (!TrySwitch(a, player.godMode, out bool on)) return;
        player.godMode = on;
        Ok("Бессмертие: " + OnOff(on));
    }

    void CmdGive(string[] a)
    {
        if (!NeedPlayer()) return;
        if (a.Length == 0)
        {
            Say("give <оружие> — имена:");
            PrintWeaponList();
            return;
        }
        if (loadout == null)
        {
            Error("У игрока нет набора оружия (PlayerLoadout)");
            return;
        }
        if (!NeedAlive()) return;

        WeaponData d = ResolveWeapon(string.Join(" ", a), out string problem);
        if (d == null)
        {
            Error(problem);
            return;
        }
        if (loadout.Give(d)) Ok("Выдано: " + d.weaponName);
        else Error("Не получилось выдать " + d.weaponName);
    }

    void CmdAmmo(string[] a)
    {
        if (!TrySwitch(a, Weapon.InfiniteAmmo, out bool on)) return;
        Weapon.InfiniteAmmo = on;
        // full magazines at once (an empty gun would wait for a reload that infinite ammo never starts)
        if (on)
        {
            foreach (Weapon w in FindObjectsByType<Weapon>(FindObjectsInactive.Exclude))
                if (w != null && w.data != null && !w.IsMelee && w.Ammo < w.magazineSize) w.ResetAmmo();
        }
        Ok("Бесконечные патроны: " + OnOff(on));
    }

    void CmdMoney(string[] a)
    {
        if (!NeedPlayer()) return;
        if (!Economy.Enabled)
        {
            Error("В этом режиме денег нет (в тренировке всё бесплатно)");
            return;
        }
        int amount = Economy.MaxMoney;
        if (a.Length > 0 && !TryInt(a[0].TrimStart('$'), out amount))
        {
            Error("Нужна сумма числом, например: money 16000");
            return;
        }
        amount = Mathf.Clamp(amount, 0, Economy.MaxMoney);
        int change = amount - Economy.MoneyOf(player);
        // through Add: the HUD shows the "+$..." line like for any other money
        if (change != 0) Economy.Add(player, change, "Консоль");
        Ok("Деньги: $" + Economy.MoneyOf(player));
    }

    void CmdBuyAnywhere(string[] a)
    {
        if (!TrySwitch(a, Economy.BuyAnywhere, out bool on)) return;
        Economy.BuyAnywhere = on;
        Ok("Покупка в любом месте и в любое время: " + OnOff(on) + (on ? " (B — меню закупки)" : ""));
    }

    void CmdBotAdd(string[] a)
    {
        if (!NeedRounds()) return;
        FindGame();
        int team = EnemyTeam;
        int count = 1;
        foreach (string s in a)
        {
            int t = ParseTeam(s, false);
            if (t == 0 || t == 1)
            {
                team = t;
                continue;
            }
            if (TryInt(s, out int n))
            {
                count = n;
                continue;
            }
            Error("Не понял «" + s + "». Пример: bot_add ct 3");
            return;
        }
        if (count < 1)
        {
            Error("Сколько ботов? Хотя бы 1");
            return;
        }
        if (count > 20)
        {
            Hint("Не больше 20 за раз");
            count = 20;
        }

        Transform view = ViewTransform();
        Vector3 from = PlayerAlive ? player.transform.position : view != null ? view.position : Vector3.zero;
        Vector3 forward = view != null ? view.forward : (player != null ? player.transform.forward : Vector3.forward);

        int added = rounds.SpawnBotsNear(team, count, from, forward);
        if (added <= 0) Error("Перед вами нет свободного места (или на сцене нет префаба бота)");
        else if (added < count) Ok("Добавлено ботов: " + added + " из " + count + " (" + TeamName(team) + ") — больше не поместилось");
        else Ok("Добавлено ботов: " + added + " (" + TeamName(team) + ")");
    }

    void CmdBotKill(string[] a)
    {
        if (!NeedRounds()) return;
        int team = -1;
        if (a.Length > 0)
        {
            team = ParseTeam(a[0], true);
            if (team == -2)
            {
                Error("Нужно t, ct или all");
                return;
            }
        }
        int killed = rounds.KillTeam(team);
        if (killed == 0)
        {
            Hint("Живых ботов" + (team >= 0 ? " " + TeamName(team) : "") + " нет");
            return;
        }
        Ok("Убито ботов: " + killed + (team >= 0 ? " (" + TeamName(team) + ")" : ""));
        if (RoundManager.InFreezeTime) Print("Если команда погибла целиком, раунд закончится, когда пройдёт фризтайм", DimColor);
    }

    void CmdKill(string[] a)
    {
        if (!NeedPlayer()) return;
        if (!PlayerAlive)
        {
            Hint("Вы уже мертвы");
            return;
        }
        // god does not stop this death
        bool god = player.godMode;
        player.godMode = false;
        player.TakeDamage(player.maxHealth * 10f);
        player.ApplyPending();
        player.godMode = god;
        Ok("Вы погибли");
    }

    void CmdBotPlace(string[] a)
    {
        if (!NeedRounds()) return;
        FindGame();
        int team = EnemyTeam;
        if (a.Length > 0)
        {
            team = ParseTeam(a[0], true);
            if (team != 0 && team != 1)
            {
                Error("Нужно t или ct");
                return;
            }
        }

        Transform view = ViewTransform();
        if (view == null)
        {
            Error("Нет камеры");
            return;
        }
        if (!LookPoint(view, out Vector3 point, out Vector3 normal))
        {
            Error("Посмотрите на пол или стену (не дальше 300 м)");
            return;
        }
        // a little off the wall, then the nearest walkable place
        if (!NavMesh.SamplePosition(point + normal * 0.4f, out NavMeshHit nav, 3f, NavMesh.AllAreas))
        {
            Error("Туда бот встать не может");
            return;
        }

        Vector3 me = PlayerAlive ? player.transform.position : view.position;
        Vector3 gap = nav.position - me;
        gap.y = 0f;
        if (PlayerAlive && gap.magnitude < 1f)
        {
            Error("Слишком близко к вам");
            return;
        }

        Health bot = NearestBot(team, me);
        if (bot == null)
        {
            Error("Нет живых ботов " + TeamName(team));
            return;
        }

        NavMeshAgent agent = bot.GetComponent<NavMeshAgent>();
        if (agent != null && agent.enabled)
        {
            if (!agent.Warp(nav.position))
            {
                Error("Не получилось переставить бота");
                return;
            }
            if (agent.isOnNavMesh) agent.ResetPath();
        }
        else
        {
            bot.transform.position = nav.position + Vector3.up;
        }

        // facing the player
        Vector3 look = me - nav.position;
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f) bot.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
        Ok("Бот " + bot.name + " переставлен");
    }

    // the first point of the level on the view's line (players and bots do not count)
    static bool LookPoint(Transform view, out Vector3 point, out Vector3 normal)
    {
        point = Vector3.zero;
        normal = Vector3.up;
        RaycastHit[] hits = Physics.RaycastAll(view.position, view.forward, 300f, WorldMask, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
        foreach (RaycastHit h in hits)
        {
            if (h.collider.GetComponentInParent<Health>() != null) continue;
            point = h.point;
            normal = h.normal;
            return true;
        }
        return false;
    }

    static Health NearestBot(int team, Vector3 from)
    {
        Health best = null;
        float bestDistance = float.MaxValue;
        foreach (Health h in Health.All)
        {
            if (h == null || h.IsDead || !h.gameObject.activeInHierarchy || h.team != team) continue;
            if (h.GetComponent<BotController>() == null) continue;
            float d = (h.transform.position - from).sqrMagnitude;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = h;
            }
        }
        return best;
    }

    void CmdRestart(string[] a)
    {
        if (!NeedRounds()) return;
        if (rounds.RestartMatch()) Ok(rounds.IsSolo ? "Вы снова на спавне" : "Новый матч");
        else Error("Сейчас нельзя");
    }

    void CmdEndRound(string[] a)
    {
        if (!NeedRounds()) return;
        int winner = -1;
        if (a.Length > 0)
        {
            string s = a[0].ToLowerInvariant();
            if (s == "draw" || s == "ничья" || s == "-1") winner = -1;
            else
            {
                winner = ParseTeam(s, true);
                if (winner != 0 && winner != 1)
                {
                    Error("Нужно t, ct или draw");
                    return;
                }
            }
        }

        if (rounds.EndRoundNow(winner))
        {
            Ok("Раунд окончен: " + (winner == 0 ? "победа T" : winner == 1 ? "победа CT" : "ничья"));
            return;
        }
        if (rounds.IsSolo) Error("В тренировке нет раундов");
        else if (rounds.MatchOver) Error("Матч окончен: restart — новый матч");
        else if (rounds.InBreak) Error("Раунд уже закончен, идёт перерыв");
        else Error("Сейчас нельзя");
    }

    // shows the value without an argument, else parses and clamps it; false = nothing to set
    bool NumberArg(string[] a, string what, string now, float min, float max, out float value)
    {
        value = 0f;
        if (a.Length == 0)
        {
            Say(what + ": " + now);
            return false;
        }
        if (!TryFloat(a[0], out value))
        {
            Error("Нужно число от " + Num(min) + " до " + Num(max));
            return false;
        }
        value = Mathf.Clamp(value, min, max);
        return true;
    }

    static string Num(float v)
    {
        return v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    void CmdFreezeTime(string[] a)
    {
        if (!NeedRounds()) return;
        if (!NumberArg(a, "Фризтайм", Num(rounds.freezeTime) + " с", 0f, 60f, out float v)) return;
        rounds.freezeTime = v;
        Ok("Фризтайм: " + Num(v) + " с (со следующего раунда)");
    }

    void CmdRoundTime(string[] a)
    {
        if (!NeedRounds()) return;
        if (!NumberArg(a, "Длина раунда", Num(rounds.roundTime) + " с", 5f, 3600f, out float v)) return;
        rounds.roundTime = v;
        Ok("Длина раунда: " + Num(v) + " с (со следующего раунда)");
    }

    void CmdBuyTime(string[] a)
    {
        if (!NumberArg(a, "Время закупки", Num(Economy.BuyTimeLength) + " с", 0f, 3600f, out float v)) return;
        float old = Economy.BuyTimeLength;
        Economy.BuyTimeLength = v;
        // the running round too (its buy time started BuyTimeLength before its end)
        if (Economy.BuyTimeEnd > 0f) Economy.BuyTimeEnd += v - old;
        Ok("Время закупки: " + Num(v) + " с");
    }

    void CmdMaxRounds(string[] a)
    {
        if (!NeedRounds()) return;
        if (!NumberArg(a, "Длина матча", rounds.maxRounds + " раундов", 1f, 100f, out float v)) return;
        rounds.maxRounds = Mathf.RoundToInt(v);
        int half = rounds.halftimeAfterRound > 0 ? rounds.halftimeAfterRound : rounds.maxRounds / 2;
        Ok("Матч до " + rounds.maxRounds + " раундов: смена сторон после " + half + "-го, для победы нужно " + rounds.RoundsToWin);
        if (rounds.MatchOver) Print("Матч уже окончен: restart — новый матч", DimColor);
    }

    void CmdTimeScale(string[] a)
    {
        float now = wantedTimeScale > 0f ? wantedTimeScale : Time.timeScale;
        if (!NumberArg(a, "Скорость игры", Num(now), 0.1f, 4f, out float v)) return;
        wantedTimeScale = Mathf.Approximately(v, 1f) ? 0f : v;
        if (!MainMenu.IsOpen) Time.timeScale = v;
        Ok("Скорость игры: " + Num(v) + (MainMenu.IsOpen ? " (после закрытия меню)" : ""));
    }

    // the pause menu stops the time and starts it again with 1: the console's speed comes back after it
    void KeepTimeScale()
    {
        if (wantedTimeScale <= 0f || MainMenu.IsOpen || Time.timeScale <= 0f) return;
        if (!Mathf.Approximately(Time.timeScale, wantedTimeScale)) Time.timeScale = wantedTimeScale;
    }

    void CmdNoclip(string[] a)
    {
        if (!NeedPlayer()) return;
        if (playerController == null)
        {
            Error("У игрока нет PlayerController");
            return;
        }
        if (!TrySwitch(a, playerController.Noclip, out bool on)) return;
        playerController.Noclip = on;
        Ok("Полёт сквозь стены: " + OnOff(on) + (on ? " (WASD — куда смотрите, Пробел — вверх, C — вниз, Shift — быстрее)" : ""));
    }

    void CmdFriendlyFire(string[] a)
    {
        if (!TrySwitch(a, Weapon.FriendlyFire, out bool on)) return;
        Weapon.FriendlyFire = on;
        Ok("Огонь по своим: " + OnOff(on));
    }

    void CmdFps(string[] a)
    {
        if (!TrySwitch(a, showFps, out bool on)) return;
        showFps = on;
        Ok("FPS в углу экрана: " + OnOff(on));
    }

    void CmdHp(string[] a)
    {
        if (!NeedAlive()) return;
        int hp = Mathf.RoundToInt(player.maxHealth);
        if (a.Length > 0 && !TryInt(a[0], out hp))
        {
            Error("Нужно число, например: hp 100");
            return;
        }
        hp = Mathf.Clamp(hp, 1, 1000);
        player.current = hp;
        Ok("Здоровье: " + hp);
    }

    void CmdStatus(string[] a)
    {
        if (!NeedRounds()) return;
        string scene = SceneManager.GetActiveScene().name;
        string map = scene == MainMenu.DesertScene ? "Пустыня" : scene == MainMenu.ArenaScene ? "Арена" : scene;

        int[] alive = new int[2], total = new int[2];
        foreach (Health h in Health.All)
        {
            if (h == null || h.invulnerable || h.team < 0 || h.team > 1) continue;
            total[h.team]++;
            if (!h.IsDead && h.gameObject.activeInHierarchy) alive[h.team]++;
        }

        if (rounds.IsSolo)
        {
            Say("Карта: " + map + " · тренировка");
        }
        else
        {
            Say("Карта: " + map + " · бой " + rounds.botsPerTeam + "×" + rounds.botsPerTeam + " · матч до " + rounds.maxRounds + " раундов");
            string state = rounds.MatchOver ? "матч окончен"
                : rounds.InBreak ? "перерыв между раундами"
                : RoundManager.InFreezeTime ? "фризтайм, ещё " + Mathf.CeilToInt(RoundManager.FreezeTimeLeft) + " с"
                : "раунд идёт, осталось " + Mathf.CeilToInt(rounds.TimeLeft) + " с";
            Say("Счёт: T " + rounds.score[0] + " : " + rounds.score[1] + " CT · " + state);
            Say("Живы: T " + alive[0] + "/" + total[0] + " · CT " + alive[1] + "/" + total[1]);
            if (Economy.Enabled)
                Say("Закупка: " + (Economy.InBuyTime ? "ещё " + Mathf.CeilToInt(Economy.BuyTimeLeft) + " с" : "закончилась")
                    + " · время закупки " + Num(Economy.BuyTimeLength) + " с · фризтайм " + Num(rounds.freezeTime) + " с · раунд " + Num(rounds.roundTime) + " с");
        }

        if (player != null)
        {
            string weapon = "";
            if (loadout != null)
            {
                WeaponData d = loadout.InSlot(loadout.CurrentSlot);
                if (d != null) weapon = " · в руках " + d.weaponName;
            }
            string you = PlayerAlive ? Mathf.CeilToInt(player.current) + " HP" : "мёртв";
            string money = Economy.Enabled ? " · $" + Economy.MoneyOf(player) : "";
            Say("Вы: " + TeamName(player.team) + " · " + you + money + weapon);
        }

        List<string> on = new List<string>();
        if (player != null && player.godMode) on.Add("god");
        if (playerController != null && playerController.Noclip) on.Add("noclip");
        if (Weapon.InfiniteAmmo) on.Add("ammo");
        if (Economy.BuyAnywhere) on.Add("buyanywhere");
        if (RoundManager.BotsFrozen) on.Add("bot_stop");
        if (!Weapon.FriendlyFire) on.Add("ff 0");
        if (wantedTimeScale > 0f) on.Add("timescale " + Num(wantedTimeScale));
        if (showFps) on.Add("fps");
        Say("Включено: " + (on.Count > 0 ? string.Join(", ", on) : "ничего"));
    }

    // ---------------- fps ----------------

    void CountFps()
    {
        fpsFrames++;
        fpsTime += Time.unscaledDeltaTime;
        if (fpsTime < 0.5f) return;
        fpsValue = fpsFrames / fpsTime;
        float ms = 1000f * fpsTime / Mathf.Max(1, fpsFrames);
        fpsText = Mathf.RoundToInt(fpsValue) + " FPS  " + ms.ToString("0.0", CultureInfo.InvariantCulture) + " мс";
        fpsFrames = 0;
        fpsTime = 0f;
    }

    void DrawFps()
    {
        if (fpsText.Length == 0) return;
        float k = GameHud.Scale;
        fpsStyle.fontSize = Mathf.Max(10, Mathf.RoundToInt(16f * k));
        Vector2 size = GameHud.Measure(fpsStyle, fpsText);
        float pad = Mathf.Round(5f * k);
        Rect r = new Rect(Mathf.Round(Screen.width - size.x - 14f * k), Mathf.Round(8f * k), Mathf.Ceil(size.x), Mathf.Ceil(size.y));
        GameHud.Fill(new Rect(r.x - pad, r.y - pad * 0.5f, r.width + pad * 2f, r.height + pad), new Color(0f, 0f, 0f, 0.4f));
        Color c = fpsValue >= 60f ? OkColor : fpsValue >= 30f ? HintColor : ErrorColor;
        GameHud.Text(r, fpsText, fpsStyle, c, TextAnchor.MiddleLeft);
    }

    // ---------------- drawing ----------------

    void EnsureStyles()
    {
        if (!fontTried)
        {
            fontTried = true;
            // a monospace font of the system, so the help columns line up; the default font if there is none
            try
            {
                string[] installed = Font.GetOSInstalledFontNames();
                foreach (string want in new[] { "Consolas", "Lucida Console", "Courier New", "DejaVu Sans Mono" })
                {
                    if (System.Array.IndexOf(installed, want) < 0) continue;
                    mono = Font.CreateDynamicFontFromOSFont(want, 18);
                    if (mono != null) break;
                }
            }
            catch (System.Exception)
            {
                mono = null;
            }
        }

        if (textStyle != null) return;
        textStyle = GameHud.MakeStyle(FontStyle.Normal);
        if (mono != null) textStyle.font = mono;
        textStyle.alignment = TextAnchor.MiddleLeft;
        titleStyle = GameHud.MakeStyle(FontStyle.Bold);
        hintStyle = GameHud.MakeStyle(FontStyle.Normal);
        fpsStyle = GameHud.MakeStyle(FontStyle.Bold);
        if (mono != null) fpsStyle.font = mono;
    }

    void OnGUI()
    {
        GUI.depth = -200;   // above everything, the pause menu too
        Event e = Event.current;
        EnsureStyles();

        if (!open)
        {
            if (showFps && e.type == EventType.Repaint) DrawFps();
            return;
        }

        float k = Mathf.Max(0.55f, Screen.height / 1080f);
        int fontSize = Mathf.Max(11, Mathf.RoundToInt(17f * k));
        textStyle.fontSize = fontSize;
        titleStyle.fontSize = fontSize;
        hintStyle.fontSize = Mathf.Max(10, Mathf.RoundToInt(14f * k));
        if (fontSize != measuredSize)
        {
            measuredSize = fontSize;
            lineHeight = Mathf.Ceil(GameHud.Measure(textStyle, "Ag|Йй").y);
        }
        float lineH = Mathf.Max(8f, lineHeight);

        float pad = Mathf.Round(12f * k);
        Rect panel = new Rect(0f, 0f, Screen.width, Mathf.Round(Screen.height * 0.5f));
        Rect header = new Rect(pad, Mathf.Round(6f * k), panel.width - pad * 2f, Mathf.Round(30f * k));
        float inputH = lineH + Mathf.Round(10f * k);
        Rect inputBox = new Rect(pad, panel.yMax - pad - inputH, panel.width - pad * 2f, inputH);
        float logTop = header.yMax + Mathf.Round(8f * k);
        Rect logArea = new Rect(pad, logTop, panel.width - pad * 2f, Mathf.Max(lineH, inputBox.y - Mathf.Round(8f * k) - logTop));
        pageLines = Mathf.Max(1, Mathf.FloorToInt(logArea.height / lineH));

        BuildHints();
        float hintPad = Mathf.Round(6f * k);
        Rect hintBox = new Rect(pad, panel.yMax + Mathf.Round(4f * k), Mathf.Min(panel.width - pad * 2f, Mathf.Round(1200f * k)),
            hints.Count * lineH + hintPad * 2f);

        // the clicks and the wheel over the console are its own (the menu buttons under it get nothing)
        if (e.isMouse || e.type == EventType.ScrollWheel)
        {
            bool over = panel.Contains(e.mousePosition) || (hints.Count > 0 && hintBox.Contains(e.mousePosition));
            if (over)
            {
                if (e.type == EventType.ScrollWheel) scroll = Mathf.Max(0, scroll - Mathf.RoundToInt(e.delta.y));
                e.Use();
            }
            return;
        }
        if (e.type != EventType.Repaint) return;

        // panel and header
        GameHud.Fill(panel, PanelBack);
        GameHud.Fill(new Rect(0f, panel.yMax - Mathf.Max(2f, Mathf.Round(2f * k)), panel.width, Mathf.Max(2f, Mathf.Round(2f * k))), Gold);
        titleStyle.alignment = TextAnchor.MiddleLeft;
        titleStyle.normal.textColor = Gold;
        GUI.Label(header, Title, titleStyle);
        // the keys on the right; a shorter text (or none) in a narrow window
        float room = header.width - GameHud.Width(titleStyle, Title) - 24f * k;
        string keys = GameHud.Width(hintStyle, HeaderHint) <= room ? HeaderHint
            : GameHud.Width(hintStyle, ShortHeaderHint) <= room ? ShortHeaderHint : "";
        hintStyle.alignment = TextAnchor.MiddleRight;
        hintStyle.normal.textColor = DimColor;
        if (keys.Length > 0) GUI.Label(header, keys, hintStyle);
        GameHud.Fill(new Rect(pad, header.yMax + Mathf.Round(3f * k), panel.width - pad * 2f, 1f), new Color(1f, 1f, 1f, 0.1f));

        DrawLog(logArea, lineH, k);
        DrawInput(inputBox, lineH, k);
        if (hints.Count > 0) DrawHints(hintBox, lineH, hintPad);
    }

    void DrawLog(Rect area, float lineH, float k)
    {
        int visible = Mathf.Max(1, Mathf.FloorToInt(area.height / lineH));
        int maxScroll = Mathf.Max(0, log.Count - visible);
        scroll = Mathf.Clamp(scroll, 0, maxScroll);
        int first = Mathf.Max(0, log.Count - visible - scroll);
        int last = Mathf.Min(log.Count, first + visible);

        float barW = Mathf.Max(3f, Mathf.Round(5f * k));
        // newest lines at the bottom, long lines are cut at the edge
        GUI.BeginGroup(new Rect(area.x, area.y, area.width - barW - Mathf.Round(6f * k), area.height));
        float y = area.height - (last - first) * lineH;
        for (int i = first; i < last; i++)
        {
            textStyle.normal.textColor = log[i].color;
            GUI.Label(new Rect(0f, y, 6000f, lineH), log[i].text, textStyle);
            y += lineH;
        }
        GUI.EndGroup();

        if (maxScroll <= 0) return;

        // scroll bar on the right
        Rect track = new Rect(area.xMax - barW, area.y, barW, area.height);
        GameHud.Fill(track, new Color(1f, 1f, 1f, 0.06f));
        float thumbH = Mathf.Max(12f * k, area.height * visible / Mathf.Max(1f, log.Count));
        float thumbY = area.y + (area.height - thumbH) * (1f - scroll / (float)maxScroll);
        GameHud.Fill(new Rect(track.x, thumbY, barW, thumbH), new Color(1f, 1f, 1f, 0.3f));

        if (scroll > 0)
        {
            string more = "ниже ещё строк: " + scroll + "  (PgDn)";
            Vector2 size = GameHud.Measure(hintStyle, more);
            Rect r = new Rect(area.xMax - barW - size.x - 16f * k, area.yMax - size.y - 2f * k, size.x + 8f * k, size.y);
            GameHud.Fill(r, new Color(0f, 0f, 0f, 0.7f));
            hintStyle.alignment = TextAnchor.MiddleCenter;
            hintStyle.normal.textColor = HintColor;
            GUI.Label(r, more, hintStyle);
        }
    }

    void DrawInput(Rect box, float lineH, float k)
    {
        GameHud.Fill(box, new Color(0f, 0f, 0f, 0.45f));
        GameHud.DrawFrame(box, 1f, new Color(1f, 1f, 1f, 0.14f));

        float x = box.x + Mathf.Round(8f * k);
        textStyle.normal.textColor = Gold;
        GUI.Label(new Rect(x, box.y, 40f * k, box.height), ">", textStyle);
        x += GameHud.Width(textStyle, ">") + Mathf.Round(8f * k);

        Rect area = new Rect(x, box.y, box.xMax - x - Mathf.Round(8f * k), box.height);
        float caretX = PrefixWidth(input, caret);
        // a long line moves left so the caret stays visible
        float offset = Mathf.Max(0f, caretX - (area.width - 4f));

        GUI.BeginGroup(area);
        if (input.Length == 0)
        {
            textStyle.normal.textColor = new Color(1f, 1f, 1f, 0.25f);
            GUI.Label(new Rect(0f, 0f, area.width, area.height), "введите команду (help — список)", textStyle);
        }
        else
        {
            textStyle.normal.textColor = Color.white;
            GUI.Label(new Rect(-offset, 0f, 8000f, area.height), input, textStyle);
        }

        // the caret: solid while typing, then it blinks
        bool caretOn = ((Time.unscaledTime - typedAt) % 1f) < 0.55f;
        if (caretOn)
        {
            float w = Mathf.Max(1f, Mathf.Round(2f * k));
            float h = lineH - Mathf.Round(4f * k);
            GameHud.Fill(new Rect(Mathf.Round(caretX - offset), Mathf.Round((area.height - h) * 0.5f), w, h), Color.white);
        }
        GUI.EndGroup();
    }

    // the width of the first n characters (with a mark at the end, so spaces at the end count)
    float PrefixWidth(string text, int n)
    {
        if (n <= 0) return 0f;
        return GameHud.Width(textStyle, text.Substring(0, n) + "|") - GameHud.Width(textStyle, "|");
    }

    void DrawHints(Rect box, float lineH, float pad)
    {
        GameHud.Fill(box, new Color(0.03f, 0.04f, 0.05f, 0.92f));
        GameHud.DrawFrame(box, 1f, new Color(1f, 1f, 1f, 0.12f));
        GUI.BeginGroup(new Rect(box.x + pad, box.y + pad, box.width - pad * 2f, box.height - pad * 2f));
        float y = 0f;
        foreach (Line l in hints)
        {
            textStyle.normal.textColor = l.color;
            GUI.Label(new Rect(0f, y, 6000f, lineH), l.text, textStyle);
            y += lineH;
        }
        GUI.EndGroup();
    }

    // the hints for the line being typed: the matching commands, or the usage of the command and the weapons of give
    void BuildHints()
    {
        if (hintsFor == input) return;
        hintsFor = input;
        hints.Clear();

        // the last command of the line ("god 1; giv" -> "giv")
        string text = input.Substring(input.LastIndexOf(';') + 1).TrimStart();
        if (text.Length == 0) return;
        int space = text.IndexOf(' ');

        if (space < 0)
        {
            string t = text.ToLowerInvariant();
            int more = 0;
            foreach (Command c in commands)
            {
                if (!c.name.StartsWith(t, System.StringComparison.Ordinal)) continue;
                if (hints.Count < MaxHints) hints.Add(new Line(c.usage + "  —  " + c.description, TextColor));
                else more++;
            }
            foreach (string alias in aliasOrder)
            {
                if (!alias.StartsWith(t, System.StringComparison.Ordinal)) continue;
                Command c = FindCommand(alias);
                if (c == null) continue;
                if (hints.Count < MaxHints) hints.Add(new Line(alias + " = " + aliasLines[alias] + "  —  " + c.description, DimColor));
                else more++;
            }
            if (more > 0) hints.Add(new Line("… и ещё " + more + " (Tab — дополнить)", DimColor));
            return;
        }

        Command cmd = FindCommand(text.Substring(0, space));
        if (cmd == null) return;
        hints.Add(new Line(cmd.usage + "  —  " + cmd.description, HintColor));

        if (cmd.name != "give") return;
        string token = Normalize(text.Substring(space + 1));
        if (token.Length == 0) return;
        int extra = 0;
        int shown = 0;
        foreach (string[] w in WeaponNames)
        {
            bool match = Normalize(w[0]).StartsWith(token, System.StringComparison.Ordinal);
            for (int i = 1; i < w.Length && !match; i++) match = w[i].StartsWith(token, System.StringComparison.Ordinal);
            if (!match) continue;
            if (shown < MaxHints)
            {
                hints.Add(new Line("  " + w[1] + "  —  " + w[0], TextColor));
                shown++;
            }
            else extra++;
        }
        if ("auto".StartsWith(token, System.StringComparison.Ordinal) && shown < MaxHints)
            hints.Add(new Line("  auto  —  " + AutoSniper, TextColor));
        if (extra > 0) hints.Add(new Line("  … и ещё " + extra, DimColor));
    }
}
