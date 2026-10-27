using UnityEditor;
using UnityEngine;
using System;
using System.Runtime.InteropServices;

/// <summary>
/// The second screen, on a machine that only has one.
///
/// The Editor reports exactly one display however many monitors are plugged in - extra displays
/// only resolve in a built player - so the monitor view cannot be seen from the Editor by
/// pointing it at Display 2. RDMonitorView therefore renders into a texture while in the Editor,
/// and this window draws it.
///
/// Two ways to open it. Docked, for working; and fullscreen, for showing - which gives a
/// window with no tab, no title bar and no close button, which is the whole point when the thing
/// behind the glass is supposed to look like an instrument rather than like Unity. The absence of
/// a close button is also why Q exists.
///
/// Nothing here renders anything. The camera draws itself into its target texture inside URP's
/// own loop, throttled as always; this window only shows the result.
///
/// Menu:
///   RD / Monitor Window              - docked, normal Editor tab
///   RD / Monitor Window (Fullscreen) - borderless, Q to quit
/// </summary>
public class RDMonitorWindow : EditorWindow
{
    private bool borderless;
    private bool closing;

    // Hiding the shell taskbar.
    //
    // Unity has no managed way to do this - a window can be always-on-top within the Editor's
    // own stacking and the Windows shell will still draw the taskbar over it. The only route is
    // to ask the shell window itself to hide, which is a Win32 call on Shell_TrayWnd.
    //
    // It MUST come back. Hidden state survives this window, so every exit path restores it, and
    // if the Editor is killed outright while hidden the taskbar returns by restarting Explorer.
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    private static bool taskbarHidden;

    private static void SetTaskbarVisible(bool visible)
    {
        // Idempotent. ShowWindow on the shell moves focus, which can bring us back in here a
        // second time through OnLostFocus; toggling twice is how the taskbar ends up hidden
        // with no window left to restore it.
        if (visible != taskbarHidden) return;

        taskbarHidden = !visible;

        int command = visible ? SW_SHOW : SW_HIDE;

        IntPtr primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) ShowWindow(primary, command);

        // The second bar, on a multi-monitor desktop. Only the first is found by class name,
        // so this covers the common case of one extra screen rather than all of them.
        IntPtr secondary = FindWindow("Shell_SecondaryTrayWnd", null);
        if (secondary != IntPtr.Zero) ShowWindow(secondary, command);
    }

    [MenuItem("RD/Monitor Window", priority = 62)]
    private static void Open()
    {
        RDMonitorWindow window = GetWindow<RDMonitorWindow>("RD Monitor");
        window.minSize = new Vector2(320f, 200f);
        window.Show();
    }

    [MenuItem("RD/Monitor Window (Fullscreen)", priority = 63)]
    private static void OpenFullscreen()
    {
        RDMonitorWindow window = CreateInstance<RDMonitorWindow>();
        window.borderless = true;

        // Overscanned by a margin. Under Windows display scaling the Editor measures windows in
        // logical points while currentResolution reports physical pixels, and the two disagree
        // by enough to leave a strip of desktop along one edge. Oversizing costs nothing - the
        // excess falls off the screen - and a strip of taskbar in the middle of the piece costs
        // a great deal.
        Vector2 size = new Vector2(Screen.currentResolution.width + 64,
                                   Screen.currentResolution.height + 64);

        // ShowAsDropDown rather than ShowPopup. A popup still sits inside the Editor's own
        // stacking, so the Windows taskbar and Unity's status bar stay visible as strips top and
        // bottom - which is exactly the chrome this window exists to get rid of. A drop-down is
        // always-on-top and covers both.
        SetTaskbarVisible(false);

        window.ShowAsDropDown(new Rect(-32f, -32f, 0f, 0f), size);
        window.Focus();
    }

    // The texture is redrawn by the running game, not by the Editor's own repaint schedule, so
    // the window has to ask for repaints itself or it shows a still.
    private void OnInspectorUpdate() => Repaint();

    private void OnGUI()
    {
        HandleQuitKey();

        RenderTexture texture = RDMonitorView.EditorPreview;

        // A RenderTexture can survive as an object while its GPU surface is gone - a device
        // reset, a resolution change, the headset taking the display. Drawing one of those is
        // what turned the window white rather than throwing anything.
        if (texture != null && !texture.IsCreated()) texture = null;

        Rect area = new Rect(0f, 0f, position.width, position.height);

        // Painted past every edge. The window is deliberately oversized so it covers the shell,
        // and a fill that stops exactly at the reported size leaves a hairline of whatever is
        // behind it along the bottom - which on a dark screen in a dark room is the one thing
        // anyone looks at.
        EditorGUI.DrawRect(new Rect(-64f, -64f, position.width + 128f, position.height + 128f),
                           Color.black);

        if (texture == null)
        {
            // Drawn rather than HelpBox'd so the fullscreen window stays black and quiet instead
            // of putting an Editor-looking panel on a screen someone is meant to look at.
            GUIStyle style = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 1f) }
            };

            GUI.Label(area,
                      Application.isPlaying
                          ? "No monitor preview - check RDMonitorView's Preview In Editor Window."
                          : "Enter Play mode.",
                      style);

            return;
        }

        // Letterboxed rather than stretched: the monitor view is a composed shot, and judging the
        // reticle against a distorted one is worse than not looking.
        GUI.DrawTexture(area, texture, ScaleMode.ScaleToFit, false);
    }

    /// <summary>
    /// Q closes it. A borderless popup has no other way out, so this is not a convenience.
    ///
    /// Escape is accepted too. Not because it was asked for, but because a fullscreen window with
    /// exactly one undocumented exit is a thing that gets panicked at during an install, and the
    /// cost of a second key is nothing.
    /// </summary>
    private void HandleQuitKey()
    {
        Event e = Event.current;

        if (e == null || e.type != EventType.KeyDown) return;

        if (e.keyCode != KeyCode.Q && e.keyCode != KeyCode.Escape) return;

        e.Use();
        RequestClose();
    }

    /// <summary>
    /// Closes once, and not from inside the event that asked for it.
    ///
    /// Closing straight out of OnGUI destroys the window while its own event is still being
    /// dispatched. Worse, restoring the taskbar in OnDestroy hands focus back to the shell,
    /// which makes the container send another close, which raises OnLostFocus, which closed it
    /// again - "Destroying object multiple times", and a half-destroyed fullscreen window left
    /// painting white over the desktop. The flag breaks the loop and delayCall gets the destroy
    /// out of the event.
    /// </summary>
    private void RequestClose()
    {
        if (closing) return;

        closing = true;

        // Restore the shell BEFORE the window is destroyed, never from inside OnDestroy.
        // ShowWindow activates the taskbar, Windows sends a close to the container that owns
        // this window, and Unity then destroys a window it is already in the middle of
        // destroying. The closing flag cannot stop that one, because the second destroy comes
        // from native code rather than from anything here.
        SetTaskbarVisible(true);

        EditorApplication.delayCall += () =>
        {
            if (this != null) Close();
        };
    }

    private void OnDestroy()
    {
        closing = true;

        // Only reached for closes this window did not start - the tab being closed by hand, a
        // domain reload, the Editor quitting. Deferred so the shell is not touched while this
        // object is still being destroyed, and idempotent, so the restore above wins the race
        // harmlessly. The lambda calls a static, so it captures nothing that is going away.
        if (taskbarHidden)
            EditorApplication.delayCall += () => SetTaskbarVisible(true);
    }

    /// <summary>
    /// Deliberately does NOT close on lost focus any more.
    ///
    /// It used to, on the reasoning that an unfocused popup cannot receive the key that closes
    /// it. The cost was far worse than the problem: anything that takes focus shut the public
    /// screen mid-show - a printer process, a Windows notification, the operator clicking the
    /// Editor to read the console. A screen that goes black when a receipt prints is not a
    /// screen anyone can exhibit behind.
    ///
    /// Clicking the window gives focus back and Q works again, and RD > Close Monitor Window
    /// closes it from the menu without needing focus at all.
    /// </summary>
    private void OnLostFocus()
    {
    }

    [MenuItem("RD/Close Monitor Window", priority = 64)]
    private static void CloseAll()
    {
        foreach (RDMonitorWindow window in Resources.FindObjectsOfTypeAll<RDMonitorWindow>())
            window.RequestClose();
    }
}
