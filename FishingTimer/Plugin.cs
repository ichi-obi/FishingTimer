using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using System;
using System.Diagnostics;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace FishingTimer
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

        public bool LockWindow { get; set; } = false;
        public bool CenterText { get; set; } = true;
        public float TimerFontSize { get; set; } = 48f;
        public Vector4 TimerColor { get; set; } = new Vector4(0.2f, 1.0f, 0.4f, 1.0f);
        public Vector4 BackgroundColor { get; set; } = new Vector4(0.06f, 0.06f, 0.06f, 0.85f);
        public Vector4 BorderColor { get; set; } = new Vector4(0.2f, 1.0f, 0.4f, 0.85f);
    }

    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "Fishing Timer";

        private const string CommandName = "/fishingtimer";
        private const string ShortCommandName = "/ftimer";
        private const float BaseFontPt = 36f;

        private readonly IDalamudPluginInterface _pluginInterface;
        private readonly Configuration _config;

        [PluginService] private static ICondition Condition { get; set; } = null!;
        [PluginService] private static IFramework Framework { get; set; } = null!;
        [PluginService] private static ICommandManager CommandManager { get; set; } = null!;

        private readonly Stopwatch _fishingStopwatch = new();
        private bool _isFishingSessionActive = false; // Czy gracz stoi z wyjeta wedka nad woda
        private bool _isLineOut = false;              // Czy splawik jest w wodzie
        private bool _showConfigWindow = false;
        private bool _showPreview = false;

        private string _debugInfo = "Not fishing";

        private readonly IFontHandle _hiResFont;

        public Plugin(IDalamudPluginInterface pluginInterface)
        {
            _pluginInterface = pluginInterface;
            _config = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

            if (_config.TimerFontSize < 16f)
            {
                _config.TimerFontSize = 48f;
            }

            if (_config.TimerColor.W == 0f && _config.BackgroundColor.W == 0f && _config.BorderColor.W == 0f)
            {
                _config.TimerColor = new Vector4(_config.TimerColor.X, _config.TimerColor.Y, _config.TimerColor.Z, 1.0f);
                _config.BackgroundColor = new Vector4(_config.BackgroundColor.X, _config.BackgroundColor.Y, _config.BackgroundColor.Z, 0.85f);
                _config.BorderColor = new Vector4(_config.BorderColor.X, _config.BorderColor.Y, _config.BorderColor.Z, 0.85f);
            }

            _hiResFont = _pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
                new GameFontStyle(GameFontFamily.Axis, BaseFontPt)
            );

            var commandInfo = new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens or closes the Fishing Timer settings window."
            };
            CommandManager.AddHandler(CommandName, commandInfo);
            CommandManager.AddHandler(ShortCommandName, commandInfo);

            Framework.Update += OnFrameworkUpdate;
            _pluginInterface.UiBuilder.Draw += DrawUI;
            _pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
            _pluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;
        }

        private void OnCommand(string command, string args)
        {
            ToggleConfigUi();
        }

        private void ToggleConfigUi()
        {
            _showConfigWindow = !_showConfigWindow;
        }

        private unsafe void OnFrameworkUpdate(IFramework framework)
        {
            bool lineInWater = Condition[ConditionFlag.Fishing];
            bool gatheringActive = Condition[ConditionFlag.Gathering];

            FishingState currentState = FishingState.None;
            var eventFramework = EventFramework.Instance();
            if (eventFramework != null)
            {
                var fishingHandler = eventFramework->EventHandlerModule.FishingEventHandler;
                if (fishingHandler != null)
                {
                    currentState = fishingHandler->State;
                }
            }

            // Okno jest widoczne dopoki mamy wyjeta wedke nad woda (ConditionFlag.Gathering lub aktywny FishingState)
            bool hasRodOutAtWater = lineInWater || (gatheringActive && currentState != FishingState.None);

            if (hasRodOutAtWater)
            {
                _isFishingSessionActive = true;

                if (lineInWater && !_isLineOut)
                {
                    // Nowy rzut wedka -> zerujemy i startujemy stoper
                    _isLineOut = true;
                    _fishingStopwatch.Restart();
                }
                else if (lineInWater && _isLineOut)
                {
                    // Zatrzymanie dokladnie w momencie wyskoczenia wykrzyknikow (! / !! / !!!)
                    if (_fishingStopwatch.IsRunning && currentState == FishingState.Bite)
                    {
                        _fishingStopwatch.Stop();
                    }
                }
                else if (!lineInWater && _isLineOut)
                {
                    // Wyciagnelismy rybe/splawik, ale nadal stoimy z wedka nad woda ->
                    // zatrzymujemy stoper (zostawiajac ostatni wynik na ekranie) i czekamy na kolejny rzut
                    _isLineOut = false;
                    _fishingStopwatch.Stop();
                }

                _debugInfo = $"FishingState: {currentState} ({(int)currentState})";
            }
            else if (_isFishingSessionActive)
            {
                // Odeszlismy od wody / schowalismy wedke -> ukrywamy okno i zerujemy stoper
                _isFishingSessionActive = false;
                _isLineOut = false;
                _fishingStopwatch.Reset();
                _debugInfo = "Not fishing";
            }
        }

        private void DrawUI()
        {
            DrawTimerOverlay();
            DrawConfigWindow();
        }

        private void DrawTimerOverlay()
        {
            // Wyswietlamy dopoki gracz stoi z wyjeta wedka nad woda (lub ma wlaczony tryb Preview)
            if (!_isFishingSessionActive && !_showPreview) return;

            ImGui.SetNextWindowSize(new Vector2(200, 100), ImGuiCond.FirstUseEver);

            ImGuiWindowFlags windowFlags = ImGuiWindowFlags.NoTitleBar;

            if (_config.LockWindow)
            {
                windowFlags |= ImGuiWindowFlags.NoMove
                             | ImGuiWindowFlags.NoResize
                             | ImGuiWindowFlags.NoInputs;
            }

            ImGui.PushStyleColor(ImGuiCol.WindowBg, _config.BackgroundColor);
            ImGui.PushStyleColor(ImGuiCol.Border, _config.BorderColor);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f);

            if (ImGui.Begin("FishingTimerOverlay", windowFlags))
            {
                // Ucinanie w dol (Math.Floor) zapewnia dokladnosc co do 0.1s bez przyspieszania
                double elapsedSeconds = Math.Floor(_fishingStopwatch.Elapsed.TotalSeconds * 10.0) / 10.0;

                string timerText = $"{elapsedSeconds:F1} s";
                string previewText = "[Preview Mode]";
                bool showPreviewText = _showPreview && !_isFishingSessionActive;

                Vector2 windowSize = ImGui.GetWindowSize();
                float spacing = ImGui.GetStyle().ItemSpacing.Y;

                Vector2 previewSize = showPreviewText ? ImGui.CalcTextSize(previewText) : Vector2.Zero;

                Vector2 timerSize;
                if (_hiResFont.Available)
                {
                    using (_hiResFont.Lock())
                    {
                        ImGui.SetWindowFontScale(_config.TimerFontSize / BaseFontPt);
                        timerSize = ImGui.CalcTextSize(timerText);
                        ImGui.SetWindowFontScale(1.0f);
                    }
                }
                else
                {
                    ImGui.SetWindowFontScale(_config.TimerFontSize / 16f);
                    timerSize = ImGui.CalcTextSize(timerText);
                    ImGui.SetWindowFontScale(1.0f);
                }

                float totalHeight = timerSize.Y;
                if (showPreviewText) totalHeight += previewSize.Y + spacing;

                float currentY = ImGui.GetCursorPosY();
                if (_config.CenterText)
                {
                    currentY = Math.Max(8f, (windowSize.Y - totalHeight) * 0.5f);
                    ImGui.SetCursorPos(new Vector2(Math.Max(8f, (windowSize.X - timerSize.X) * 0.5f), currentY));
                }

                if (_hiResFont.Available)
                {
                    using (_hiResFont.Lock())
                    {
                        ImGui.SetWindowFontScale(_config.TimerFontSize / BaseFontPt);
                        ImGui.TextColored(_config.TimerColor, timerText);
                        ImGui.SetWindowFontScale(1.0f);
                    }
                }
                else
                {
                    ImGui.SetWindowFontScale(_config.TimerFontSize / 16f);
                    ImGui.TextColored(_config.TimerColor, timerText);
                    ImGui.SetWindowFontScale(1.0f);
                }
                currentY += timerSize.Y + spacing;

                if (showPreviewText)
                {
                    if (_config.CenterText)
                    {
                        ImGui.SetCursorPos(new Vector2(Math.Max(8f, (windowSize.X - previewSize.X) * 0.5f), currentY));
                    }
                    ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), previewText);
                }
            }
            ImGui.End();

            ImGui.PopStyleVar(1);
            ImGui.PopStyleColor(2);
        }

        private void DrawConfigWindow()
        {
            if (!_showConfigWindow) return;

            ImGui.SetNextWindowSize(new Vector2(390, 340), ImGuiCond.FirstUseEver);

            if (ImGui.Begin("Fishing Timer Settings", ref _showConfigWindow, ImGuiWindowFlags.NoCollapse))
            {
                bool changed = false;

                ImGui.Checkbox("Show timer preview (for positioning)", ref _showPreview);
                ImGui.Separator();

                bool lockWin = _config.LockWindow;
                if (ImGui.Checkbox("Lock window position and size", ref lockWin))
                {
                    _config.LockWindow = lockWin;
                    changed = true;
                }

                bool centerText = _config.CenterText;
                if (ImGui.Checkbox("Center timer in window", ref centerText))
                {
                    _config.CenterText = centerText;
                    changed = true;
                }

                Vector4 bgColor = _config.BackgroundColor;
                if (ImGui.ColorEdit4("Background Color & Opacity", ref bgColor, ImGuiColorEditFlags.AlphaBar))
                {
                    _config.BackgroundColor = bgColor;
                    changed = true;
                }

                Vector4 borderColor = _config.BorderColor;
                if (ImGui.ColorEdit4("Border Color & Opacity", ref borderColor, ImGuiColorEditFlags.AlphaBar))
                {
                    _config.BorderColor = borderColor;
                    changed = true;
                }

                ImGui.Separator();

                float fontSize = _config.TimerFontSize;
                if (ImGui.SliderFloat("Font Size", ref fontSize, 16f, 200f, "%.0f"))
                {
                    _config.TimerFontSize = fontSize;
                    changed = true;
                }

                Vector4 timerColor = _config.TimerColor;
                if (ImGui.ColorEdit4("Timer Font Color & Opacity", ref timerColor, ImGuiColorEditFlags.AlphaBar))
                {
                    _config.TimerColor = timerColor;
                    changed = true;
                }

                ImGui.Spacing();
                if (ImGui.Button("Reset to Defaults"))
                {
                    _config.LockWindow = false;
                    _config.CenterText = true;
                    _config.TimerFontSize = 48f;
                    _config.TimerColor = new Vector4(0.2f, 1.0f, 0.4f, 1.0f);
                    _config.BackgroundColor = new Vector4(0.06f, 0.06f, 0.06f, 0.85f);
                    _config.BorderColor = new Vector4(0.2f, 1.0f, 0.4f, 0.85f);
                    changed = true;
                }

                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"Status: {_debugInfo}");

                if (changed)
                {
                    _pluginInterface.SavePluginConfig(_config);
                }
            }
            ImGui.End();
        }

        public void Dispose()
        {
            CommandManager.RemoveHandler(CommandName);
            CommandManager.RemoveHandler(ShortCommandName);
            Framework.Update -= OnFrameworkUpdate;
            _pluginInterface.UiBuilder.Draw -= DrawUI;
            _pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
            _pluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;
            _hiResFont.Dispose();
        }
    }
}