// Minimal drop-in replacement for
// Vuplex.WebView.Internal.NativeKeyboardListener
// to support Unity's new Input System.

using System;
using System.Collections.Generic;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputKey = UnityEngine.InputSystem.Key;
using InputKeyControl = UnityEngine.InputSystem.Controls.KeyControl;
using IMECompositionString = UnityEngine.InputSystem.LowLevel.IMECompositionString;
#endif

namespace Vuplex.WebView.Internal
{
    public class KeyboardEventArgs : EventArgs
    {
        public KeyboardEventArgs(string key, KeyModifier modifiers)
        {
            Key = key;
            Modifiers = modifiers;
        }
        public readonly string Key;
        public readonly KeyModifier Modifiers;
        public override string ToString() => $"Key: {Key}, Modifiers: {Modifiers}";
    }

    public class NativeKeyboardListener : MonoBehaviour
    {

        public event EventHandler ImeCompositionCancelled;
        public event EventHandler<EventArgs<string>> ImeCompositionChanged;
        public event EventHandler<EventArgs<string>> ImeCompositionFinished;
        public event EventHandler<KeyboardEventArgs> KeyDownReceived;
        public event EventHandler<KeyboardEventArgs> KeyUpReceived;

        public static NativeKeyboardListener Instantiate() =>
            new GameObject("NativeKeyboardListener").AddComponent<NativeKeyboardListener>();

        class KeyRepeatState
        {
            public string Key;
            public bool HasRepeated;
        }

        bool? _imeShouldBeEnabled;
        List<string> _keysDown = new List<string>();
        KeyRepeatState _keyRepeatState;
        KeyModifier _modifiersDown;
        string _currentImeComposition = string.Empty;
        readonly List<char> _pendingImeCommitChars = new List<char>();

        static readonly (InputKey key, string js)[] _nonTextKeyMap = new (InputKey, string)[] {
#if ENABLE_INPUT_SYSTEM
            (InputKey.DownArrow, "ArrowDown"), (InputKey.RightArrow, "ArrowRight"), (InputKey.LeftArrow, "ArrowLeft"), (InputKey.UpArrow, "ArrowUp"),
            (InputKey.Backspace, "Backspace"), (InputKey.End, "End"), (InputKey.Enter, "Enter"), (InputKey.Escape, "Escape"),
            (InputKey.Delete, "Delete"), (InputKey.Home, "Home"), (InputKey.Insert, "Insert"),
            (InputKey.PageDown, "PageDown"), (InputKey.PageUp, "PageUp"), (InputKey.Tab, "Tab")
#endif
        };

#if ENABLE_INPUT_SYSTEM
        InputKeyboard _keyboard;
#endif

        void Awake()
        {
#if ENABLE_INPUT_SYSTEM
            _keyboard = InputKeyboard.current;
            if (_keyboard == null)
            {
                WebViewLogger.LogWarning("No hardware Keyboard device is available (Input System). Native keyboard detection will be disabled.");
                return;
            }
            _enableImeIfNeeded();
            _keyboard.onTextInput += _onTextInput;
            _keyboard.onIMECompositionChange += _onImeCompositionChange;
#else
            WebViewLogger.LogWarning(
                "3D WebView's native keyboard detection now uses Unity's new Input System, " +
                "but ENABLE_INPUT_SYSTEM is not defined for this build. Please set 'Active Input Handling' " +
                "to 'Input System Package' or 'Both' in Project Settings > Player.");
#endif
        }

        void OnDestroy()
        {
#if ENABLE_INPUT_SYSTEM
            if (_keyboard != null)
            {
                _keyboard.onTextInput -= _onTextInput;
                _keyboard.onIMECompositionChange -= _onImeCompositionChange;
            }
#endif
        }

        bool _determineIfImeShouldBeEnabled()
        {
            var isMacOS = Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor;
            if (!isMacOS)
            {
                return true;
            }
            var correctlySupportsIme = false;
#if UNITY_2020_3
            correctlySupportsIme = VXUnityVersion.Instance.Minor >= 38;
#elif UNITY_2021_2_OR_NEWER
            correctlySupportsIme = true;
#endif
            if (correctlySupportsIme)
            {
                return true;
            }
            switch (Application.systemLanguage)
            {
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.ChineseTraditional:
                case SystemLanguage.Japanese:
                case SystemLanguage.Korean:
                    WebViewLogger.LogWarning($"The system language is set to a language that uses IME ({Application.systemLanguage}), but the version of Unity in use ({Application.unityVersion}) has a bug where IME doesn't work correctly on macOS. To use IME with 3D WebView on macOS, please upgrade to Unity 2021.2 or newer.");
                    break;
            }
            return false;
        }

        void _enableImeIfNeeded()
        {
            if (_imeShouldBeEnabled == null)
            {
                _imeShouldBeEnabled = _determineIfImeShouldBeEnabled();
            }
#if ENABLE_INPUT_SYSTEM
            if (_keyboard != null && _imeShouldBeEnabled == true)
            {
                _keyboard.SetIMEEnabled(true);
            }
#endif
        }

#if ENABLE_INPUT_SYSTEM
        KeyModifier _getModifiers()
        {
            var modifiers = KeyModifier.None;
            if (_keyboard == null)
            {
                return modifiers;
            }
            if (_keyboard.leftShiftKey.isPressed || _keyboard.rightShiftKey.isPressed)
            {
                modifiers |= KeyModifier.Shift;
            }
            if (_keyboard.leftCtrlKey.isPressed || _keyboard.rightCtrlKey.isPressed)
            {
                modifiers |= KeyModifier.Control;
            }
            if (_keyboard.leftAltKey.isPressed || _keyboard.rightAltKey.isPressed)
            {
                modifiers |= KeyModifier.Alt;
            }
            if (
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                _keyboard.leftWindowsKey.isPressed || _keyboard.rightWindowsKey.isPressed
#else
                _keyboard.leftCommandKey.isPressed || _keyboard.rightCommandKey.isPressed
#endif
            )
            {
                modifiers |= KeyModifier.Meta;
            }
            return modifiers;
        }

        void _onTextInput(char ch)
        {
            _modifiersDown = _getModifiers();
            var altGrPressed = _modifiersDown == (KeyModifier.Alt | KeyModifier.Control);
            var modsToSend = altGrPressed ? KeyModifier.None : _modifiersDown;

            var s = _charToJsKey(ch);
            var args = new KeyboardEventArgs(s, modsToSend);
            KeyDownReceived?.Invoke(this, args);

            if (_tryMapCharToKey(ch, out var key))
            {
                _keysDown.Add(s);
            }
            else
            {
                KeyUpReceived?.Invoke(this, new KeyboardEventArgs(s, modsToSend));
            }

            if (!string.IsNullOrEmpty(_currentImeComposition))
            {
                _pendingImeCommitChars.Add(ch);
            }
        }

        void _onImeCompositionChange(IMECompositionString composition)
        {
            var previous = _currentImeComposition;
            _currentImeComposition = composition.ToString();
            // See original comments: remove apostrophes from Windows Pinyin composition to keep caret positions in sync.
            if (_currentImeComposition.Contains("'"))
            {
                _currentImeComposition = _currentImeComposition.Replace("'", "");
            }

            if (!string.IsNullOrEmpty(previous))
            {
                if (string.IsNullOrEmpty(_currentImeComposition))
                {
                    if (_pendingImeCommitChars.Count > 0)
                    {
                        var committed = new string(_pendingImeCommitChars.ToArray());
                        _pendingImeCommitChars.Clear();
                        ImeCompositionFinished?.Invoke(this, new EventArgs<string>(committed));
                    }
                    else
                    {
                        ImeCompositionCancelled?.Invoke(this, EventArgs.Empty);
                    }
                }
            }

            if (!string.IsNullOrEmpty(_currentImeComposition) && _currentImeComposition != previous)
            {
                ImeCompositionChanged?.Invoke(this, new EventArgs<string>(_currentImeComposition));
            }
        }
#endif

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (_keyboard == null)
            {
                return;
            }
            _enableImeIfNeeded();

            _modifiersDown = _getModifiers();
            _processNonTextKeys();
            _processModifierKeysOnly();
            _processKeysReleased();
#endif
        }

#if ENABLE_INPUT_SYSTEM
        void _processNonTextKeys()
        {
            foreach (var (key, js) in _nonTextKeyMap)
            {
                var control = _getControl(key);
                if (control == null)
                {
                    continue;
                }
                if (control.wasPressedThisFrame)
                {
                    KeyDownReceived?.Invoke(this, new KeyboardEventArgs(js, _modifiersDown));
                    _keysDown.Add(js);
                    if (_keyRepeatState != null)
                    {
                        CancelInvoke(REPEAT_KEY_METHOD_NAME);
                    }
                    _keyRepeatState = new KeyRepeatState { Key = js };
                    InvokeRepeating(REPEAT_KEY_METHOD_NAME, 0.5f, 0.1f);
                }
            }
        }

        void _processModifierKeysOnly()
        {
            if (_keyboard.shiftKey.wasPressedThisFrame)
            {
                _emitModifierKeyDown("Shift");
            }
            if (_keyboard.ctrlKey.wasPressedThisFrame)
            {
                _emitModifierKeyDown("Control");
            }
            if (_keyboard.altKey.wasPressedThisFrame)
            {
                _emitModifierKeyDown("Alt");
            }
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (_keyboard.leftWindowsKey.wasPressedThisFrame || _keyboard.rightWindowsKey.wasPressedThisFrame)
            {
                _emitModifierKeyDown("Meta");
            }
#else
            if (_keyboard.leftCommandKey.wasPressedThisFrame || _keyboard.rightCommandKey.wasPressedThisFrame) {
                _emitModifierKeyDown("Meta");
            }
#endif
        }

        void _emitModifierKeyDown(string keyName)
        {
            KeyDownReceived?.Invoke(this, new KeyboardEventArgs(keyName, KeyModifier.None));
            _keysDown.Add(keyName);
        }

        void _processKeysReleased()
        {
            if (_keysDown.Count == 0)
            {
                return;
            }
            var keysDownCopy = new List<string>(_keysDown);
            foreach (var jsKey in keysDownCopy)
            {
                bool keyUp = false;
                if (_tryGetKeysForJs(jsKey, out var keys))
                {
                    foreach (var k in keys)
                    {
                        var control = _getControl(k);
                        if (control != null && control.wasReleasedThisFrame)
                        {
                            keyUp = true;
                            break;
                        }
                    }
                }
                else
                {
                    keyUp = true;
                }

                if (keyUp)
                {
                    _keysDown.Remove(jsKey);
                    var emitKeyUp = true;
                    if (_keyRepeatState?.Key == jsKey)
                    {
                        CancelInvoke(REPEAT_KEY_METHOD_NAME);
                        if (_keyRepeatState.HasRepeated)
                        {
                            // KeyUp was already emitted as part of repeating logic.
                            emitKeyUp = false;
                        }
                        _keyRepeatState = null;
                    }
                    if (emitKeyUp)
                    {
                        KeyUpReceived?.Invoke(this, new KeyboardEventArgs(jsKey, _modifiersDown));
                    }
                }
            }
        }

        const string REPEAT_KEY_METHOD_NAME = "_repeatKey";
        void _repeatKey()
        {
            var key = _keyRepeatState?.Key;
            if (key == null)
            {
                CancelInvoke(REPEAT_KEY_METHOD_NAME);
                return;
            }
            var eventArgs = new KeyboardEventArgs(key, _modifiersDown);
            if (!_keyRepeatState.HasRepeated)
            {
                KeyUpReceived?.Invoke(this, eventArgs);
                _keyRepeatState.HasRepeated = true;
            }
            KeyDownReceived?.Invoke(this, eventArgs);
            KeyUpReceived?.Invoke(this, eventArgs);
        }

        // === Mapping helpers ===
        static string _charToJsKey(char c)
        {
            switch (c)
            {
                case '\b': return "Backspace";
                case '\n':
                case '\r': return "Enter";
                default: return c.ToString();
            }
        }

        static bool _tryMapDigit(char c, out InputKey key)
        {
            key = default;
            if (c >= '0' && c <= '9')
            {
                key = (InputKey)((int)InputKey.Digit0 + (c - '0'));
                return true;
            }
            return false;
        }

        bool _tryMapCharToKey(char c, out InputKey key)
        {
            key = default;
            var lower = char.ToLowerInvariant(c);
            if (lower >= 'a' && lower <= 'z')
            {
                key = (InputKey)((int)InputKey.A + (lower - 'a'));
                return true;
            }
            if (_tryMapDigit(c, out key))
            {
                return true;
            }
            switch (c)
            {
                case '`': key = InputKey.Backquote; return true;
                case '-': key = InputKey.Minus; return true;
                case '=': key = InputKey.Equals; return true;
                case '[': key = InputKey.LeftBracket; return true;
                case ']': key = InputKey.RightBracket; return true;
                case '\\': key = InputKey.Backslash; return true;
                case ';': key = InputKey.Semicolon; return true;
                case '\'': key = InputKey.Quote; return true;
                case ',': key = InputKey.Comma; return true;
                case '.': key = InputKey.Period; return true;
                case '/': key = InputKey.Slash; return true;
                case ' ': key = InputKey.Space; return true;
            }
            return false;
        }

        bool _tryGetKeysForJs(string js, out InputKey[] keys)
        {
            if (js != null && js.Length == 1 && _tryMapCharToKey(js[0], out var k))
            {
                keys = new[] { k };
                return true;
            }

            switch (js)
            {
                case " ": keys = new[] { InputKey.Space }; return true;
                case "Alt": keys = new[] { InputKey.LeftAlt, InputKey.RightAlt }; return true;
                case "Control": keys = new[] { InputKey.LeftCtrl, InputKey.RightCtrl }; return true;
                case "Shift": keys = new[] { InputKey.LeftShift, InputKey.RightShift }; return true;
                case "Meta": keys = new[] { InputKey.LeftWindows, InputKey.RightWindows, InputKey.LeftCommand, InputKey.RightCommand }; return true;
                case "ArrowUp": keys = new[] { InputKey.UpArrow }; return true;
                case "ArrowDown": keys = new[] { InputKey.DownArrow }; return true;
                case "ArrowRight": keys = new[] { InputKey.RightArrow }; return true;
                case "ArrowLeft": keys = new[] { InputKey.LeftArrow }; return true;
                case "Enter": keys = new[] { InputKey.Enter, InputKey.NumpadEnter }; return true;
                case "Escape": keys = new[] { InputKey.Escape }; return true;
                case "Backspace": keys = new[] { InputKey.Backspace }; return true;
                case "Delete": keys = new[] { InputKey.Delete }; return true;
                case "Home": keys = new[] { InputKey.Home }; return true;
                case "End": keys = new[] { InputKey.End }; return true;
                case "Insert": keys = new[] { InputKey.Insert }; return true;
                case "PageUp": keys = new[] { InputKey.PageUp }; return true;
                case "PageDown": keys = new[] { InputKey.PageDown }; return true;
                case "Tab": keys = new[] { InputKey.Tab }; return true;
            }
            keys = null;
            return false;
        }

        InputKeyControl _getControl(InputKey key)
        {
            if (_keyboard == null)
            {
                return null;
            }
            return _keyboard.FindKeyOnCurrentKeyboardLayout(_keyboard[key].displayName);
        }
#endif
    }
}
