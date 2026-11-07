using System;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

public partial class PrototypeMenu
{
    private bool settingsFromPause;
    private int settingsTab;
    private GameAction? bindingAction;
    private Button bindingButton;
    private int captureFrame;
    private GameSettings displayBefore;
    private float displayDeadline;
    private Label displayCountdown;
    private VisualElement displayDialog;

    public void BackOrResume()
    {
        if (displayBefore != null) { FinishDisplay(false); return; }
        if (bindingAction.HasValue) { bindingAction = null; ShowSettings(settingsFromPause); return; }
        if (screen == Screen.Settings)
        {
            if (settingsFromPause) ShowPause(); else ShowHome();
        }
        else app.Resume();
    }

    private void ShowSettings(bool fromPause)
    {
        if (displayBefore != null) FinishDisplay(false);
        settingsFromPause = fromPause; screen = Screen.Settings;
        Draw("Настройки", "Изменения сохраняются автоматически.", true);
        var tabs = new VisualElement(); tabs.AddToClassList("inline-buttons"); content.Add(tabs);
        string[] names = { "Графика", "Управление", "Звук" };
        for (int i = 0; i < names.Length; i++)
        {
            int tab = i;
            ActionButton(tabs, names[i], () => { settingsTab = tab; bindingAction = null; ShowSettings(fromPause); }, settingsTab == i);
        }
        var scroll = new ScrollView(); scroll.style.height = 460; scroll.style.flexShrink = 1; content.Add(scroll);
        var s = GameSettings.Current;
        void Flag(string label, bool initial, Action<bool> assign)
        {
            var field = new Toggle(label) { value = initial }; scroll.Add(field);
            field.RegisterValueChangedCallback(e => { assign(e.newValue); s.Apply(); });
        }
        void SliderValue(string label, float min, float max, float initial, Action<float> assign)
        {
            var field = new Slider(label, min, max) { value = initial, showInputField = true }; scroll.Add(field);
            field.RegisterValueChangedCallback(e => { assign(e.newValue); s.Apply(); });
        }
        void Choice(string label, string[] choices, int initial, Action<int> assign)
        {
            var field = new DropdownField(label, choices.ToList(), Mathf.Clamp(initial, 0, choices.Length - 1)); scroll.Add(field);
            field.RegisterValueChangedCallback(e => { assign(field.index); s.Apply(); });
        }
        if (settingsTab == 0)
        {
            var resolutions = UnityEngine.Screen.resolutions.Select(r => new Vector2Int(r.width, r.height))
                .Concat(new[] { new Vector2Int(s.Width, s.Height), new Vector2Int(1280, 720), new Vector2Int(1920, 1080) })
                .Distinct().OrderBy(r => r.x).ThenBy(r => r.y).ToList();
            var resolution = new DropdownField("Разрешение", resolutions.Select(r => r.x + " × " + r.y).ToList(),
                resolutions.FindIndex(r => r.x == s.Width && r.y == s.Height)); scroll.Add(resolution);
            var modes = new[] { FullScreenMode.FullScreenWindow, FullScreenMode.Windowed, FullScreenMode.ExclusiveFullScreen };
            var mode = new DropdownField("Режим экрана", new[] { "Без рамки", "Оконный", "Полный экран" }.ToList(),
                Mathf.Max(0, Array.IndexOf(modes, s.Mode))); scroll.Add(mode);
            ActionButton(scroll, "Проверить разрешение и режим", () =>
            {
                displayBefore = s.Clone(); displayDeadline = Time.realtimeSinceStartup + 15f;
                s.Width = resolutions[resolution.index].x; s.Height = resolutions[resolution.index].y;
                s.Mode = modes[mode.index]; s.ApplyDisplay(); ShowDisplayDialog();
            });
            Choice("Качество", GameSettings.QualityNames, s.Quality, n => s.Quality = n);
            Choice("Тени", new[] { "Выкл.", "Низкие", "Средние", "Высокие" }, s.Shadows, n => s.Shadows = n);
            Choice("Эффекты", new[] { "Низкие", "Обычные", "Высокие" }, s.Effects, n => s.Effects = n);
            Flag("Ослабить эффекты оглушения", s.ReducedConcussion, b => s.ReducedConcussion = b);
            Flag("Вертикальная синхронизация", s.VSync, b => s.VSync = b);
            SliderValue("Лимит FPS (0 = ∞)", 0, 360, s.FpsLimit, n => s.FpsLimit = Mathf.RoundToInt(n));
            SliderValue("Дальность, м", 80, 1000, s.DrawDistance, n => s.DrawDistance = n);
            SliderValue("Поле зрения человека", 60, 120, s.Fov, n => s.Fov = n);
            SliderValue("Поле зрения дрона", 60, 120, s.DroneFov, n => s.DroneFov = n);
        }
        else if (settingsTab == 1)
        {
            SliderValue("Мышь: человек", 0.02f, 0.6f, s.HumanSensitivity, n => s.HumanSensitivity = n);
            SliderValue("Мышь: дрон", 0.02f, 0.6f, s.DroneSensitivity, n => s.DroneSensitivity = n);
            SliderValue("Мышь при наклоне", 0.2f, 1.5f, s.LeanSensitivity, n => s.LeanSensitivity = n);
            Flag("Инверсия: человек", s.InvertHuman, b => s.InvertHuman = b);
            Flag("Инверсия: дрон", s.InvertDrone, b => s.InvertDrone = b);
            SliderValue("Экспонента стиков дрона", 0, 0.9f, s.DroneExpo, n => s.DroneExpo = n);
            Flag("Дрон: Acro по умолчанию", s.DroneAcro, b => s.DroneAcro = b);
            scroll.Add(Text("Нажми действие, затем клавишу. Esc отменяет. Общая E для действия и наклона допустима.", "field-note"));
            foreach (var action in GameSettings.ActionNames)
            {
                GameAction target = action.Key;
                Button button = null;
                button = ActionButton(scroll, action.Value + "  ·  " + GameSettings.KeyName(target), () =>
                {
                    if (bindingButton != null && bindingAction.HasValue)
                        bindingButton.text = GameSettings.ActionNames[bindingAction.Value] + "  ·  " + GameSettings.KeyName(bindingAction.Value);
                    bindingAction = target; bindingButton = button; captureFrame = Time.frameCount;
                    button.text = action.Value + "  ·  Нажми клавишу…";
                });
            }
            ActionButton(scroll, "Сбросить клавиши", () => { s.ResetKeys(); s.Apply(); bindingAction = null; ShowSettings(fromPause); });
        }
        else
        {
            SliderValue("Общая громкость", 0, 1, s.Master, n => s.Master = n);
            SliderValue("Эффекты", 0, 1, s.EffectsVolume, n => s.EffectsVolume = n);
            SliderValue("Окружение", 0, 1, s.AmbientVolume, n => s.AmbientVolume = n);
            SliderValue("Интерфейс", 0, 1, s.InterfaceVolume, n => s.InterfaceVolume = n);
            Flag("Без звука", s.Muted, b => s.Muted = b);
            ActionButton(scroll, "Проверить звук", () => GameAudio.PlayFlat(SoundBank.Switch, 0.65f));
        }
        ActionButton(content, "По умолчанию", () =>
        {
            var defaults = new GameSettings { Width = s.Width, Height = s.Height, Mode = s.Mode };
            defaults.Apply(); bindingAction = null; ShowSettings(fromPause);
        });
        ActionButton(content, "Назад", BackOrResume); Footer();
    }

    private void ShowDisplayDialog()
    {
        displayDialog = new VisualElement(); displayDialog.AddToClassList("display-confirm"); root.Add(displayDialog);
        displayCountdown = Text("Сохранить режим экрана?", "menu-title"); displayDialog.Add(displayCountdown);
        ActionButton(displayDialog, "Сохранить", () => FinishDisplay(true), true);
        ActionButton(displayDialog, "Вернуть прежний", () => FinishDisplay(false));
    }

    private void FinishDisplay(bool keep)
    {
        if (displayBefore == null) return;
        var s = GameSettings.Current;
        if (!keep)
        {
            s.Width = displayBefore.Width; s.Height = displayBefore.Height; s.Mode = displayBefore.Mode;
            s.ApplyDisplay();
        }
        displayBefore = null; s.Apply();
        displayDialog?.RemoveFromHierarchy(); displayDialog = null;
        if (Visible && screen == Screen.Settings) ShowSettings(settingsFromPause);
    }

    private void Update()
    {
        if (displayBefore != null)
        {
            float remaining = displayDeadline - Time.realtimeSinceStartup;
            if (displayCountdown != null) displayCountdown.text = "Сохранить режим? " + Mathf.CeilToInt(remaining) + " сек.";
            if (remaining <= 0f) FinishDisplay(false);
            return;
        }
        if (bindingAction.HasValue && Time.frameCount > captureFrame && Keyboard.current != null)
            foreach (var key in Keyboard.current.allKeys)
            {
                if (!key.wasPressedThisFrame) continue;
                if (key.keyCode == Key.Escape) { bindingAction = null; ShowSettings(settingsFromPause); return; }
                var conflict = GameSettings.Conflict(bindingAction.Value, key.keyCode);
                if (conflict.HasValue) { ShowError("Клавиша занята: " + GameSettings.ActionNames[conflict.Value]); return; }
                GameSettings.Current.SetKey(bindingAction.Value, key.keyCode); GameSettings.Current.Apply();
                bindingAction = null; ShowSettings(settingsFromPause); return;
            }
        if (Visible && screen == Screen.Settings && !settingsFromPause && Keyboard.current?.escapeKey.wasPressedThisFrame == true)
            BackOrResume();
    }
}
