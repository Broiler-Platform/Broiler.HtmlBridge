namespace Broiler.HtmlBridge;

/// <summary>What a key did: it went down, or repeated while held, or came up.</summary>
public enum KeyboardInputKind
{
    /// <summary>A key was pressed, or repeats while it is held (<see cref="KeyboardInput.Repeat"/>).</summary>
    Down,

    /// <summary>A key was released.</summary>
    Up,
}

/// <summary>
/// A user's press or release of a key, as the page's host delivers it to the page's scripts
/// (<c>InteractiveSession.DispatchKey</c>). The characters a press types follow it separately
/// (<see cref="TextInput"/>), as a platform reports them.
/// </summary>
/// <param name="Kind">What the key did.</param>
/// <param name="Key">
/// <c>KeyboardEvent.key</c>: the character the key types with the modifiers held (<c>"a"</c>,
/// <c>"A"</c>, <c>" "</c>), or the key's name (<c>"Enter"</c>, <c>"Tab"</c>, <c>"Backspace"</c>,
/// <c>"ArrowLeft"</c>, <c>"Shift"</c>), as UI Events KeyboardEvent key values name it.
/// </param>
/// <param name="Code">
/// <c>KeyboardEvent.code</c>: the physical key, whatever it types (<c>"KeyA"</c>, <c>"Space"</c>,
/// <c>"ShiftLeft"</c>, <c>"Enter"</c>), or empty when the host cannot tell.
/// </param>
public readonly record struct KeyboardInput(KeyboardInputKind Kind, string Key, string Code)
{
    /// <summary>
    /// The legacy <c>keyCode</c> and <c>which</c> of <c>keydown</c> and <c>keyup</c>: the Windows
    /// virtual-key code, 65 for A whatever the case, 13 for Enter, 16 for Shift.
    /// </summary>
    public int KeyCode { get; init; }

    /// <summary><c>KeyboardEvent.location</c>: 0 a standard key, 1 the left of a pair, 2 the right, 3 the numeric keypad.</summary>
    public int Location { get; init; }

    /// <summary>Whether the key is held down and repeating, rather than newly pressed.</summary>
    public bool Repeat { get; init; }

    /// <summary>Whether Control was held.</summary>
    public bool CtrlKey { get; init; }

    /// <summary>Whether Shift was held.</summary>
    public bool ShiftKey { get; init; }

    /// <summary>Whether Alt was held.</summary>
    public bool AltKey { get; init; }

    /// <summary>Whether the platform's meta key (Windows, Command) was held.</summary>
    public bool MetaKey { get; init; }
}

/// <summary>
/// The characters a key press typed, which a host delivers after the press
/// (<c>InteractiveSession.DispatchText</c>): the page hears each as <c>keypress</c>, and a focused text
/// field takes them as an edit, with <c>beforeinput</c> and <c>input</c>.
/// </summary>
/// <param name="Text">What was typed; control characters (a line feed, a tab, a backspace) are not text, and are left out.</param>
public readonly record struct TextInput(string Text)
{
    /// <summary>
    /// The focused field's value once the host's own editor took the text in, when it has one over the
    /// field; <see langword="null"/> to have the page put the text at the end of the field's value.
    /// </summary>
    public string? EditedValue { get; init; }
}

/// <summary>
/// A change the host's editor made to the focused text field that no typed character describes -- a
/// deletion, a paste, a cut, an undo -- delivered so the page hears it as <c>beforeinput</c> and
/// <c>input</c> and holds the value the field shows (<c>InteractiveSession.DispatchEdit</c>).
/// </summary>
/// <param name="InputType">
/// <c>InputEvent.inputType</c>, as Input Events names it: <c>"deleteContentBackward"</c>,
/// <c>"deleteContentForward"</c>, <c>"insertFromPaste"</c>, <c>"deleteByCut"</c>,
/// <c>"historyUndo"</c>, <c>"insertLineBreak"</c>.
/// </param>
/// <param name="Value">
/// The field's value after the change, or <see langword="null"/> to have the page make it: a backward
/// deletion takes the value's last character, a line break in a text area adds one at its end, and
/// any other change with no value is only heard.
/// </param>
public readonly record struct FieldEdit(string InputType, string? Value)
{
    /// <summary><c>InputEvent.data</c>: the text the change put in -- what was pasted -- or <see langword="null"/>.</summary>
    public string? Data { get; init; }
}

/// <summary>What an input method did: began composing, changed what it composes, committed it, or gave it up.</summary>
public enum CompositionInputKind
{
    /// <summary>A composition began.</summary>
    Start,

    /// <summary>The text being composed changed (<see cref="CompositionInput.Text"/>).</summary>
    Update,

    /// <summary>The composition was committed: <see cref="CompositionInput.Text"/> is what the field keeps.</summary>
    Commit,

    /// <summary>The composition was given up, and the field keeps what it had before it began.</summary>
    Cancel,
}

/// <summary>
/// A step of an input method's composition in the focused text field (<c>InteractiveSession.DispatchComposition</c>):
/// the page hears <c>compositionstart</c>, <c>compositionupdate</c> and <c>compositionend</c>, and the field
/// holds the text being composed, through <c>beforeinput</c> and <c>input</c> of type
/// <c>insertCompositionText</c>. The composition replaces the field's selection as it was when it began.
/// </summary>
/// <param name="Kind">What the input method did.</param>
/// <param name="Text">The text being composed, or committed; empty for a start or a cancel.</param>
public readonly record struct CompositionInput(CompositionInputKind Kind, string Text)
{
    /// <summary>
    /// The field's value once the host's own editor took the text in, when it has one over the field;
    /// <see langword="null"/> to have the page put the text in place of what the composition replaces.
    /// </summary>
    public string? EditedValue { get; init; }
}

/// <summary>
/// The selection the host's editor holds in the focused text field after the user changed it -- a drag,
/// Shift and an arrow, a click that placed the caret -- delivered so the page's <c>selectionStart</c> and
/// <c>selectionEnd</c> follow it (<c>InteractiveSession.DispatchSelection</c>).
/// </summary>
/// <param name="Start">Where the selection starts, in UTF-16 code units of the field's value.</param>
/// <param name="End">Where it ends; equal to <paramref name="Start"/> for a caret.</param>
public readonly record struct FieldSelectionInput(int Start, int End)
{
    /// <summary>Whether the user selected backwards, so the caret is at <see cref="Start"/>: <c>selectionDirection</c> is <c>backward</c>.</summary>
    public bool Backward { get; init; }
}

/// <summary>What a <see cref="KeyboardInput"/>, a <see cref="TextInput"/> or a <see cref="FieldEdit"/> did on the page.</summary>
/// <param name="Delivered">Whether the page's scripts were given it at all.</param>
/// <param name="DefaultPrevented">
/// Whether the page's scripts cancelled what it does by default: the press's <c>keydown</c>, a typed
/// character's <c>keypress</c>, or an edit's <c>beforeinput</c>. A host undoes what its own editor did
/// for it, and performs none of its own default actions -- no scrolling for an arrow key or a space.
/// </param>
public readonly record struct KeyboardInputResult(bool Delivered, bool DefaultPrevented)
{
    /// <summary>
    /// Whether the page acted on the key itself -- moved focus for a Tab, activated the focused link,
    /// button or checkbox, submitted a form -- so the host does nothing more with it.
    /// </summary>
    public bool Handled { get; init; }
}

/// <summary>
/// The text field that has focus, for a host that edits text with an editor of its own: what it is,
/// what it holds, and where its border box is across the page's layout, in CSS pixels.
/// </summary>
/// <param name="InputType">The field's type: <c>"text"</c>, <c>"password"</c>, <c>"search"</c>, … or <c>"textarea"</c>.</param>
/// <param name="Value">The field's value.</param>
/// <param name="InFrame">Whether the field is in a frame's document rather than the page's.</param>
public sealed record FocusedTextField(string InputType, string Value, bool InFrame)
{
    /// <summary>Where the field's border box starts across the page's layout.</summary>
    public double X { get; init; }

    /// <summary>Where it starts down the page's layout.</summary>
    public double Y { get; init; }

    /// <summary>How wide it is.</summary>
    public double Width { get; init; }

    /// <summary>How tall it is.</summary>
    public double Height { get; init; }

    /// <summary>Where the page's selection in the field starts: what a script set, or the user selected.</summary>
    public int SelectionStart { get; init; }

    /// <summary>Where it ends.</summary>
    public int SelectionEnd { get; init; }

    /// <summary>Whether it is backwards: the caret at <see cref="SelectionStart"/>.</summary>
    public bool SelectionBackward { get; init; }
}
